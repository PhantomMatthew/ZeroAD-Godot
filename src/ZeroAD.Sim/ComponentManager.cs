using System;
using System.Collections.Generic;
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
        // player ID → player entity. Populated by RegisterPlayer. Used by EnqueueTraining and
        // pop/entity-limit accounting to find the owner's PlayerComponent without scanning.
        private readonly Dictionary<int, EntityId> _playerEntities = new();

        public EntityManager Entities => _entityManager;
        public Rand48 RNG => _rng;
        public ComponentRegistry Registry => _registry;
        public IReadOnlyList<EntityId> AllEntities => _allEntities;

        /// <summary>
        /// Template loader used by <see cref="SpawnEntity"/> and training/spawn paths.
        /// Null in pure determinism tests that don't load XML.
        /// </summary>
        public TemplateLoader? Templates { get; set; }

        /// <summary>
        /// Event bus owned by the sim. Spawn/death/ownership paths raise events here so the
        /// Godot presentation layer can subscribe and build visuals without the sim depending on Godot.
        /// </summary>
        public SimEventBus Events { get; }

        public ComponentManager(uint rngSeed, ComponentRegistry? registry = null,
            TemplateLoader? templates = null, SimEventBus? events = null)
        {
            _rng = new Rand48(rngSeed);
            _registry = registry ?? new ComponentRegistry();
            Templates = templates;
            Events = events ?? new SimEventBus();
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
        /// world setup (the presentation layer already creates these entities today).
        /// </summary>
        public void RegisterPlayer(int playerId, EntityId entity) => _playerEntities[playerId] = entity;

        public EntityId? GetPlayerEntityId(int playerId) =>
            _playerEntities.TryGetValue(playerId, out var eid) ? eid : null;

        /// <summary>Resolve a player's PlayerComponent by player ID, or null if unregistered.</summary>
        public PlayerComponent? GetPlayerEntity(int playerId)
        {
            if (!_playerEntities.TryGetValue(playerId, out var eid)) return null;
            return QueryInterface<PlayerComponent>(eid);
        }

        /// <summary>
        /// Adjust pop usage for a player when an entity's ownership changes. Called by the
        /// presentation layer's ownership-change handler (mirrors how Player.js reacts to
        /// MT_OwnershipChanged). Kept on ComponentManager so the rule is owned by the sim
        /// and stays deterministic across single/multiplayer. Pop is charged by CostComponent.
        /// </summary>
        public void ApplyOwnershipPopChange(EntityId entity, int oldOwner, int newOwner)
        {
            var cost = QueryInterface<CostComponent>(entity);
            if (cost == null || cost.PopulationCost == 0) return;

            if (oldOwner > 0)
            {
                var p = GetPlayerEntity(oldOwner);
                if (p != null) p.PopUsed = Math.Max(0, p.PopUsed - cost.PopulationCost);
            }
            if (newOwner > 0)
            {
                var p = GetPlayerEntity(newOwner);
                if (p != null) p.PopUsed += cost.PopulationCost;
            }
        }

        /// <summary>
        /// Aggregate a player's PopulationComponent bonuses (House +10, etc.) into
        /// PlayerComponent.PopBonuses. Called after buildings spawn/change ownership.
        /// Scans the player's owned entities — cheap enough for the handful of buildings
        /// a player has; mirrors how Player.js re-derives popBonuses on MT_ValueModification.
        /// </summary>
        public void RecomputePlayerPopBonus(int playerId)
        {
            var player = GetPlayerEntity(playerId);
            if (player == null) return;
            int total = 0;
            foreach (var entity in _allEntities)
            {
                var own = QueryInterface<OwnershipComponent>(entity);
                if (own == null || own.PlayerId != playerId) continue;
                var pop = QueryInterface<PopulationComponent>(entity);
                if (pop != null) total += pop.Bonus;
            }
            player.PopBonuses = total;
        }

        public void AddComponent(EntityId entity, ComponentTypeId cid)
        {
            var component = _registry.CreateComponent(cid);
            component.SetEntity(entity);
            var iid = _registry.GetInterfaceForComponent(cid);
            _componentsByEntity[entity][iid] = component;
            ((IComponent)component).Init();
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

        public byte[] ComputeStateHash()
        {
            var serializer = new Serialization.HashSerializer();
            serializer.StringASCII("rng", _rng.Serialize());
            serializer.NumberU32("next entity id", _entityManager.NextEntityId);

            foreach (var kvp in _componentsByEntity)
            {
                if (kvp.Key.IsLocal)
                    continue;
                serializer.NumberU32("entity", kvp.Key.Value);
                foreach (var comp in kvp.Value.Values)
                    comp.Serialize(serializer);
            }

            return serializer.ComputeHash();
        }
    }

    public interface IComponentMessageHandler
    {
        void HandleMessage(IMessage message);
    }
}
