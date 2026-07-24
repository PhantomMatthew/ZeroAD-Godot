using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim
{
    /// <summary>
    /// Message broadcast to all components on an entity (or all entities).
    /// Mirrors <c>MT_*</c> messages from <c>TypeList.h</c>.
    /// </summary>
    public interface IMessage
    {
        int TypeId { get; }
    }

    public sealed class ComponentManager
    {
        private readonly EntityManager _entityManager = new();
        private readonly Rand48 _rng;
        private readonly ComponentRegistry _registry;
        private readonly Dictionary<EntityId, Dictionary<InterfaceId, IComponent>> _componentsByEntity = new();
        private readonly List<EntityId> _allEntities = new();

        public EntityManager Entities => _entityManager;
        public Rand48 RNG => _rng;
        public ComponentRegistry Registry => _registry;
        public IReadOnlyList<EntityId> AllEntities => _allEntities;

        // Formalized managers. PlayerManager owns the player registry + pop accounting rules
        // (ported from PlayerManager.js). TemplateManager wraps the TemplateLoader (ported from
        // CCmpTemplateManager). WaterManager holds the sim-side water height (CCmpWaterManager).
        // Lazy-created so pure determinism tests that never touch them pay no cost.
        public PlayerManager Players { get; }
        public TemplateManager? TemplateManager { get; private set; }
        public WaterManager Water { get; } = new();
        public DelayedDamage DelayedDamage { get; } = new();

        /// <summary>
        /// Template loader used by <see cref="SpawnEntity"/> and training/spawn paths.
        /// Null in pure determinism tests that don't load XML. Setting this also (re)creates
        /// the <see cref="TemplateManager"/> wrapper.
        /// </summary>
        public TemplateLoader? Templates
        {
            get => TemplateManager?.Loader;
            set => TemplateManager = value != null ? new TemplateManager(value) : null;
        }

        /// <summary>
        /// Event bus owned by the sim. Spawn/death/ownership paths raise events here so the
        /// Godot presentation layer can subscribe and build visuals without the sim depending on Godot.
        /// </summary>
        public SimEventBus Events { get; }

        /// <summary>
        /// 修正值管线(对齐原版 ModifiersManager.js)。派生态:不随状态序列化,
        /// 由 TechnologyManager 在研究/重放时写入。
        /// </summary>
        public Components.ModifiersManager Modifiers { get; }

        public ComponentManager(uint rngSeed, ComponentRegistry? registry = null,
            TemplateLoader? templates = null, SimEventBus? events = null)
        {
            _rng = new Rand48(rngSeed);
            _registry = registry ?? new ComponentRegistry();
            Players = new PlayerManager(this);
            Events = events ?? new SimEventBus();
            Modifiers = new Components.ModifiersManager(this);
            if (templates != null) TemplateManager = new TemplateManager(templates);
        }

        public EntityId CreateEntity()
        {
            var id = _entityManager.AllocateEntity();
            _componentsByEntity[id] = new Dictionary<InterfaceId, IComponent>();
            _allEntities.Add(id);
            return id;
        }

        /// <summary>
        /// Spawn a unit entity from a template name at a world position. The sim owns the full
        /// pipeline: create entity, assemble components from the template stats, apply ownership,
        /// and raise <see cref="SimEventBus.EntityCreated"/> so the presentation layer builds visuals.
        /// This is the deterministic, Godot-free counterpart to the legacy SimBridge.Spawn* paths
        /// and is what training/production uses. Building/gaia spawn stays on the SimBridge side
        /// for now (their component assembly is not yet ported to <see cref="EntityAssembler"/>).
        /// </summary>
        public EntityId SpawnEntity(string templateName, float x, float z, int ownerPlayerId = -1)
        {
            var entity = CreateEntity();
            TemplateStats? stats = null;
            try { stats = Templates?.ExtractStats(templateName); }
            catch { /* missing/bad template: assemble with defaults */ }
            EntityAssembler.AssembleUnit(this, entity, templateName, stats, x, z);

            if (ownerPlayerId > 0)
                AddComponent(entity, new OwnershipComponent { PlayerId = ownerPlayerId });

            Events.RaiseEntityCreated(new EntityCreatedEvent
            {
                Entity = entity,
                TemplateName = templateName,
                OwnerPlayerId = ownerPlayerId
            });
            // Notify sim-internal listeners (RangeManager) so they index this entity. Separate from
            // the SimEventBus raise above which targets the presentation layer.
            NotifyEntityCreated(entity);
            return entity;
        }

        /// <summary>
        /// Register a player entity under its player ID so <see cref="GetPlayerEntity"/> and
        /// pop/entity-limit accounting can resolve owners in O(1). Call once per player at
        /// world setup. Forwards to <see cref="Players"/>.
        /// </summary>
        public void RegisterPlayer(int playerId, EntityId entity) => Players.AddPlayer(playerId, entity);

        public EntityId? GetPlayerEntityId(int playerId) => Players.GetPlayerEntityId(playerId);

        /// <summary>Resolve a player's PlayerComponent by player ID, or null if unregistered.
        /// Forwards to <see cref="Players"/>.</summary>
        public PlayerComponent? GetPlayerEntity(int playerId) => Players.GetPlayerEntity(playerId);

        /// <summary>
        /// Adjust pop usage for a player when an entity's ownership changes. Mirrors how
        /// Player.js reacts to MT_OwnershipChanged. Forwards to <see cref="Players"/>.
        /// </summary>
        public void ApplyOwnershipPopChange(EntityId entity, int oldOwner, int newOwner)
            => Players.ApplyOwnershipPopChange(entity, oldOwner, newOwner);

        /// <summary>
        /// Aggregate a player's PopulationComponent bonuses (House +10, etc.) into
        /// PlayerComponent.PopBonuses. Forwards to <see cref="Players"/>.
        /// </summary>
        public void RecomputePlayerPopBonus(int playerId) => Players.RecomputePlayerPopBonus(playerId);

        public void AddComponent(EntityId entity, ComponentTypeId cid)
        {
            var component = _registry.CreateComponent(cid);
            component.SetEntity(entity);
            var iid = _registry.GetInterfaceForComponent(cid);
            _componentsByEntity[entity][iid] = component;
            ((IComponent)component).Init();
        }

        /// <summary>True once any player has won (the match is over). TickVictory short-circuits
        /// on this so it doesn't re-fire GameEnded every turn.</summary>
        public bool IsGameOver { get; private set; }

        /// <summary>
        /// Per-turn conquest victory check. Called by the presentation layer once per sim tick
        /// (after RemoveDeadEntities, so the dead are gone from the RangeManager index). A player
        /// is defeated when they own zero units or buildings (resources/animals don't count);
        /// when only one active player remains, that player wins and the match ends.
        ///
        /// Deterministic: uses the RangeManager's sorted entity index (no RNG, no float). Idempotent
        /// via PlayerComponent's Active-only transition guard. Ported from ConquestCommon.js +
        /// EndGameManager.AlliedVictoryCheck.
        /// </summary>
        public void TickVictory()
        {
            if (IsGameOver) return;

            var range = Components.SimSystem.Range;
            // Without a RangeManager (pure determinism tests), victory detection can't run — skip.
            if (range == null) return;

            // Conquest requires at least 2 non-gaia players. With only one (tutorial mode, or a
            // test), "last one standing" is meaningless and the zero-entity check would spuriously
            // defeat the sole player if their entities aren't indexed yet.
            int nonGaia = 0;
            foreach (var _ in Players.GetNonGaiaPlayerIds()) nonGaia++;
            if (nonGaia < 2) return;

            // 1. Mark any active player with zero units/buildings as defeated.
            foreach (int pid in Players.GetNonGaiaPlayerIds())
            {
                var player = Players.GetPlayerEntity(pid);
                if (player == null || !player.IsActive()) continue;

                if (CountConquestEntities(pid, range) == 0)
                {
                    if (player.SetDefeated())
                        Events.RaisePlayerDefeated(new PlayerDefeatedEvent
                        {
                            PlayerId = pid,
                            Reason = "Lost all units and structures."
                        });
                }
            }

            // 2. If only one active player remains, they win and the match ends.
            int winnerId = -1;
            int activeCount = 0;
            foreach (int pid in Players.GetNonGaiaPlayerIds())
            {
                var player = Players.GetPlayerEntity(pid);
                if (player != null && player.IsActive())
                {
                    activeCount++;
                    if (activeCount == 1) winnerId = pid;
                    else { winnerId = -1; break; }  // more than one active → no winner yet
                }
            }

            if (activeCount <= 1 && winnerId > 0)
            {
                var winner = Players.GetPlayerEntity(winnerId);
                if (winner != null && winner.SetWon())
                {
                    IsGameOver = true;
                    Events.RaisePlayerWon(new PlayerWonEvent { PlayerId = winnerId });
                    Events.RaiseGameEnded(new GameEndedEvent { WinnerPlayerId = winnerId });
                }
            }
        }

        /// <summary>Count a player's entities that count for survival: units + buildings only
        /// (not resources/animals/decals). Mirrors the conquest "ConquestCritical" filter.</summary>
        private int CountConquestEntities(int playerId, Components.RangeManager range)
        {
            int count = 0;
            foreach (var entity in range.GetEntitiesByPlayer(playerId))
            {
                var id = QueryInterface<IdentityComponent>(entity);
                if (id != null && (id.IsUnit || id.IsBuilding)) count++;
            }
            return count;
        }

        public void AddComponent<T>(EntityId entity, T component) where T : ComponentBase
        {
            component.SetEntity(entity);
            var iid = _registry.GetInterfaceIdForType<T>();
            if (!iid.IsValid)
            {
                var attr = (ComponentAttribute?)Attribute.GetCustomAttribute(
                    typeof(T), typeof(ComponentAttribute));
                iid = attr != null
                    ? _registry.RegisterInterface(attr.InterfaceName)
                    : _registry.RegisterInterface(typeof(T).Name);
                _registry.CacheTypeMapping<T>(iid);
            }
            _componentsByEntity[entity][iid] = component;
            ((IComponent)component).Init();
        }

        public T? QueryInterface<T>(EntityId entity) where T : class, IComponent
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return null;

            var iid = _registry.GetInterfaceIdForType<T>();
            if (iid.IsValid && components.TryGetValue(iid, out var compDirect))
                return compDirect as T;

            foreach (var comp in components.Values)
                if (comp is T typed)
                    return typed;
            return null;
        }

        public IComponent? QueryInterface(EntityId entity, InterfaceId iid)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return null;
            return components.TryGetValue(iid, out var comp) ? comp : null;
        }

        public void PostMessage(EntityId entity, IMessage message)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return;
            foreach (var kvp in components)
                if (kvp.Value is IComponentMessageHandler handler)
                    handler.HandleMessage(message);
        }

        public void BroadcastMessage(IMessage message)
        {
            foreach (var kvp in _componentsByEntity)
                foreach (var comp in kvp.Value.Values)
                    if (comp is IComponentMessageHandler handler)
                        handler.HandleMessage(message);
        }

        // --- System-level change notifications (strongly typed, for RangeManager / ObstructionManager
        //     listeners). These mirror the original's SubscribeGloballyToMessageType(MT_PositionChanged)
        //     etc., but as concrete events so subscribers don't have to switch on TypeId. Code that
        //     moves an entity calls NotifyPositionChanged; RangeManager/ObstructionComponent react. ---

        /// <summary>Fired after an entity's world position changes. Carries old + new XZ so listeners
        /// can update spatial indices without re-querying the PositionComponent.</summary>
        public event Action<EntityId, Maths.FixedVector2D, Maths.FixedVector2D>? PositionChanged;

        /// <summary>Fired after an entity is fully created (components added). RangeManager uses it to
        /// register the entity in its spatial index.</summary>
        public event Action<EntityId>? EntityCreated;

        /// <summary>Fired before an entity is destroyed. Listeners clean up their per-entity state.</summary>
        public event Action<EntityId>? EntityDestroyed;

        /// <summary>Fired after an entity's owner changes. RangeManager/EntityLimits react.</summary>
        public event Action<EntityId, int, int>? OwnerChanged;

        // Re-exported through SimEventBus too for presentation-layer subscribers; these sim-internal
        // hooks are the canonical source.

        /// <summary>
        /// Notify system listeners that <paramref name="entity"/> moved from
        /// <paramref name="from"/> to <paramref name="to"/> (XZ plane). Call after mutating a
        /// PositionComponent. Both args are XZ world coordinates.
        /// </summary>
        public void NotifyPositionChanged(EntityId entity, Maths.FixedVector2D from, Maths.FixedVector2D to)
            => PositionChanged?.Invoke(entity, from, to);

        public void NotifyEntityCreated(EntityId entity) => EntityCreated?.Invoke(entity);
        public void NotifyEntityDestroyed(EntityId entity) => EntityDestroyed?.Invoke(entity);
        public void NotifyOwnerChanged(EntityId entity, int fromPlayer, int toPlayer)
            => OwnerChanged?.Invoke(entity, fromPlayer, toPlayer);

        public void DestroyEntity(EntityId entity)
        {
            if (!_componentsByEntity.TryGetValue(entity, out var components))
                return;
            // Let system listeners (RangeManager, ObstructionManager via ObstructionComponent)
            // drop this entity from their indices before we tear down the components.
            NotifyEntityDestroyed(entity);
            foreach (var comp in components.Values)
                comp.Deinit();
            _componentsByEntity.Remove(entity);
            _allEntities.Remove(entity);
        }

        public void ResetState()
        {
            foreach (var components in _componentsByEntity.Values)
                foreach (var comp in components.Values)
                    comp.Deinit();
            _componentsByEntity.Clear();
            _allEntities.Clear();
            _entityManager.Reset();
        }

        /// <summary>
        /// Serialize the entire deterministic state (RNG, entity ids, every non-local
        /// entity's components). Traversal order is fully sorted so two peers produce
        /// byte-identical streams regardless of insertion order; used by both the state
        /// hash (OOS detection) and StateDump (OOS forensics).
        /// </summary>
        public void SerializeFullState(ISerializer serializer)
        {
            serializer.StringASCII("rng", _rng.Serialize());
            serializer.NumberU32("next entity id", _entityManager.NextEntityId);

            var entitySection = serializer as ISectionSerializer;
            foreach (var kvp in _componentsByEntity.OrderBy(k => k.Key.Value))
            {
                if (kvp.Key.IsLocal)
                    continue;
                entitySection?.BeginSection($"entity {kvp.Key.Value}");
                serializer.NumberU32("entity", kvp.Key.Value);
                foreach (var comp in kvp.Value.Values.OrderBy(c => c.GetType().Name))
                {
                    entitySection?.BeginSection($"component {comp.GetType().Name}");
                    comp.Serialize(serializer);
                }
            }
        }

        public byte[] ComputeStateHash()
        {
            var serializer = new Serialization.HashSerializer();
            SerializeFullState(serializer);
            return serializer.ComputeHash();
        }
    }

    public interface IComponentMessageHandler
    {
        void HandleMessage(IMessage message);
    }
}
