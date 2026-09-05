using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>光环定义目录(对齐原版全局 AuraTemplates)。世界初始化时由
        /// <c>AuraLoader.LoadAll</c> 注入;AuraComponent.Tick 查此解析 template &lt;Auras&gt; 名。
        /// 纯查询只读快照,无 mutable 状态。</summary>
        public Content.AuraCatalog? Auras { get; set; }

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
            EntitySetVersion++;   // AI GameState 集合缓存的失效信号
            return id;
        }

        /// <summary>实体集变更计数(CreateEntity/DestroyEntity 递增)。AI GameState
        /// 用它给按 tick 的实体集合缓存判活:版本不变 = 世界未变 = 缓存可用。</summary>
        public int EntitySetVersion { get; private set; }

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
            // Ownership assignment is an ownership change (mirrors MT_OwnershipChanged on init):
            // activates Fogging for player-owned entities.
            if (ownerPlayerId > 0)
                NotifyOwnerChanged(entity, -1, ownerPlayerId);
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

        /// <summary>终局管理器(胜利条件体系,EndGameManager.js 移植;TickVictory 驱动)。
        /// 地图加载时经 SetVictoryConditions 注入;沙盒/默认局保持默认征服。</summary>
        public Components.EndGameManager EndGame { get; } = new();

        /// <summary>数据驱动触发器系统(Trigger.js 移植框架)。每回合随 TickVictory 推进;
        /// 效果出口(消息/生成)由 SimBridge 经 Sink 注入。</summary>
        public Triggers.TriggerSystem Triggers { get; } = new();

        /// <summary>
        /// Per-turn victory check. Defeat rules follow the map's VictoryConditions
        /// (conquest/conquest_units/conquest_civic_centers); wonder/capture_the_relic/
        /// ceasefire run through <see cref="EndGame"/>. A player is defeated when their
        /// condition-relevant entity count hits zero; last active player wins.
        ///
        /// Deterministic: uses the RangeManager's sorted entity index (no RNG, no float). Idempotent
        /// via PlayerComponent's Active-only transition guard. Ported from ConquestCommon.js +
        /// EndGameManager.AlliedVictoryCheck.
        /// </summary>
        /// <summary>回合长度(定点秒;0.1s/回合)。TriggerSystem/EndGame 的 dt 真源。</summary>
        internal static readonly Maths.Fixed TurnLengthSeconds = Maths.Fixed.FromFraction(1, 10);

        public void TickVictory()
        {
            // 触发器始终推进(原版 Trigger 组件不受终局状态影响;动作自身可判胜/判负)。
            Triggers.Tick(this, TurnLengthSeconds);

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

            // endless:无任何判负/判胜(原版 endlessGame 跳过 AlliedVictoryCheck;
            // 无征服条件模块注册 → 无清零判负)。奇观/圣物计时仍走 EndGame.Tick。
            if (!EndGame.HasCondition("endless"))
            {
                // 1a. 弑君判负(Regicide.js CheckRegicideDefeat 的轮询等价):
                // 已分配英雄的玩家,英雄被毁或易主 → 判负。
                if (EndGame.HasCondition("regicide") && EndGame.RegicideHeroes.Count > 0)
                {
                    foreach (int pid in Players.GetNonGaiaPlayerIds())
                    {
                        var player = Players.GetPlayerEntity(pid);
                        if (player == null || !player.IsActive()) continue;
                        if (!EndGame.RegicideHeroes.TryGetValue(pid, out var hero)) continue;
                        var heroOwner = QueryInterface<OwnershipComponent>(hero);
                        if (heroOwner == null || heroOwner.PlayerId != pid)
                        {
                            if (player.SetDefeated())
                                Events.RaisePlayerDefeated(new PlayerDefeatedEvent
                                {
                                    PlayerId = pid,
                                    Reason = "Lost hero."
                                });
                        }
                    }
                }

                // 1b. 征服系清零判负(仅征服系条件生效时;原版由各条件模块注册)。
                if (EndGame.HasAnyConquest)
                {
                    foreach (int pid in Players.GetNonGaiaPlayerIds())
                    {
                        var player = Players.GetPlayerEntity(pid);
                        if (player == null || !player.IsActive()) continue;

                        if (CountDefeatEntities(pid, range) == 0)
                        {
                            if (player.SetDefeated())
                                Events.RaisePlayerDefeated(new PlayerDefeatedEvent
                                {
                                    PlayerId = pid,
                                    Reason = DefeatReason()
                                });
                        }
                    }
                }

                // 2. 判胜(原版 AlliedVictoryCheck):
                //    alliedVictory(默认)→ 剩余活跃玩家互为同盟即全体共胜;
                //    LMS 模式 → 只剩 1 人才判胜。
                var actives = new List<int>();
                foreach (int pid in Players.GetNonGaiaPlayerIds())
                {
                    var player = Players.GetPlayerEntity(pid);
                    if (player != null && player.IsActive()) actives.Add(pid);
                }
                if (actives.Count > 0)
                {
                    bool crownAll = actives.Count == 1;
                    if (!crownAll && EndGame.AlliedVictory)
                    {
                        // 全体互为同盟?(原版:IsMutualAlly(allies[0]) 逐查——
                        // 同盟关系在锁队下是等价类,查首人互为同盟即可)
                        crownAll = true;
                        for (int i = 1; i < actives.Count && crownAll; i++)
                            if (!Players.GetMutualAllies(actives[0]).Contains(actives[i]))
                                crownAll = false;
                    }
                    if (crownAll)
                    {
                        int crowned = 0;
                        foreach (int pid in actives)
                        {
                            var player = Players.GetPlayerEntity(pid);
                            if (player != null && player.SetWon())
                            {
                                crowned++;
                                Events.RaisePlayerWon(new PlayerWonEvent { PlayerId = pid });
                            }
                        }
                        if (crowned > 0)
                        {
                            IsGameOver = true;
                            Events.RaiseGameEnded(new GameEndedEvent { WinnerPlayerId = actives[0] });
                            return;
                        }
                    }
                }
                else
                {
                    // 无活跃玩家(TriggerHelper.SetPlayerWon →
                    // EndGameManager.MarkPlayerAndAlliesAsWon 路径:判胜/判负全在
                    // TickVictory 外完成):有人已判胜 → 补 GameEnded 收尾;
                    // 全员同归于尽(无胜者)→ 平局,不发。
                    foreach (int pid in Players.GetNonGaiaPlayerIds())
                    {
                        var player = Players.GetPlayerEntity(pid);
                        if (player == null || !player.HasWon()) continue;
                        IsGameOver = true;
                        Events.RaiseGameEnded(new GameEndedEvent { WinnerPlayerId = pid });
                        return;
                    }
                }
            }

            // 3. 奇观/圣物计时 + 停战推进(EndGameManager)。
            if (EndGame.Tick(this, 0.1f))
                IsGameOver = true;
        }

        private string DefeatReason() =>
            EndGame.HasCondition("conquest_units") ? "Lost all units."
            : EndGame.HasCondition("conquest_civic_centers") ? "Lost all civic centres."
            : "Lost all units and structures.";

        /// <summary>Count a player's defeat-relevant entities per the active victory condition:
        /// conquest(default) → units+buildings;conquest_units → units only;
        /// conquest_civic_centers → civic-centre-class structures only.</summary>
        private int CountDefeatEntities(int playerId, Components.RangeManager range)
        {
            bool unitsOnly = EndGame.HasCondition("conquest_units") && !EndGame.HasCondition("conquest");
            bool ccOnly = EndGame.HasCondition("conquest_civic_centers");
            int count = 0;
            foreach (var entity in range.GetEntitiesByPlayer(playerId))
            {
                var id = QueryInterface<IdentityComponent>(entity);
                if (id == null) continue;
                if (ccOnly)
                {
                    if (id.IsBuilding && id.HasClass("CivCentre")) count++;
                }
                else if (unitsOnly)
                {
                    if (id.IsUnit) count++;
                }
                else if (id.IsUnit || id.IsBuilding) count++;
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
            // OwnershipComponent 后挂(SpawnUnit 走 AssembleUnit 时不带 owner,调用方在
            // AddComponent 后才设):通知 RangeManager 更新 d.Owner + SyncLos 加视野圆。
            // 此前不通知 → d.Owner 保持 -1 → SyncLos 的 want=false → LOS grid 永远不加
            // 该单位的视野圆 → 该玩家单位永远看不到敌人 → 不攻击(原版 MT_OwnershipChanged)。
            if (component is Components.OwnershipComponent oc)
                NotifyOwnerChanged(entity, -1, oc.PlayerId);
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
            // TriggerPoint 移除(原版 TriggerPoint.OnDestroy → RemoveRegisteredTriggerPoint)。
            if (QueryInterface<Components.TriggerPointComponent>(entity) is { } triggerPoint)
                Triggers.UnregisterTriggerPoint(triggerPoint.Reference, entity);
            foreach (var comp in components.Values)
                comp.Deinit();
            _componentsByEntity.Remove(entity);
            _allEntities.Remove(entity);
            EntitySetVersion++;   // AI GameState 集合缓存的失效信号
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

        /// <summary>把单个实体的所有组件 dump 成可读文本(诊断用:F12 选中实体 dump)。
        /// 复用 TextDumpSerializer,逐组件走 Serialize —— 和 SerializeFullState 同一数据
        /// 通路,但只取一个实体。用于"为什么不显示/不攻击"类问题的末端反推:选中实体
        /// dump 一眼看出缺哪个组件、字段值对不对,免去临时加 [DIAG] 打印。</summary>
        public string DumpEntity(EntityId entity)
        {
            var serializer = new Serialization.TextDumpSerializer();
            serializer.BeginSection($"entity {entity.Value}");
            serializer.NumberU32("entity", entity.Value);
            if (_componentsByEntity.TryGetValue(entity, out var components))
            {
                foreach (var comp in components.Values.OrderBy(c => c.GetType().Name))
                {
                    serializer.BeginSection($"component {comp.GetType().Name}");
                    comp.Serialize(serializer);
                }
            }
            return serializer.ToString();
        }

        /// <summary>
        /// Save-game serialization: like <see cref="SerializeFullState"/> but with
        /// structural metadata (entity count, component count, component type names)
        /// so the stream can be deserialized back. The OOS hash serializer omits
        /// these because it only needs a deterministic byte sequence, not round-trip.
        /// </summary>
        public void SerializeSaveGame(Serialization.ISerializer s)
        {
            s.StringASCII("rng", _rng.Serialize());
            s.NumberU32("nextEntityId", _entityManager.NextEntityId);
            // v6: player registry (pid→entity) round-trips so a cold load re-points every
            // player at its live entity instead of the pre-load (destroyed) one.
            Players.Serialize(s);

            var nonLocal = _componentsByEntity
                .Where(k => !k.Key.IsLocal)
                .OrderBy(k => k.Key.Value)
                .ToList();
            s.NumberU32("entityCount", (uint)nonLocal.Count);

            foreach (var kvp in nonLocal)
            {
                s.NumberU32("entityId", kvp.Key.Value);
                var comps = kvp.Value.Values.OrderBy(c => c.GetType().Name).ToList();
                s.NumberU32("compCount", (uint)comps.Count);
                foreach (var comp in comps)
                {
                    // Write the [Component(name)] attribute value (e.g. "Position"),
                    // NOT the C# class name ("PositionComponent") — the registry's
                    // GetComponentType lookup uses the attribute name.
                    var attr = (ComponentAttribute?)Attribute.GetCustomAttribute(
                        comp.GetType(), typeof(ComponentAttribute));
                    s.StringASCII("typeName", attr?.Name ?? comp.GetType().Name);
                    comp.Serialize(s);
                }
            }

            // 触发器系统(非组件系统对象,存档骑缝;原版 Trigger 组件的
            // triggerData.enabled + 定时器状态):定义表动态部分(Enabled/Elapsed)。
            Triggers.Serialize(s);
        }

        /// <summary>
        /// Restores the full simulation state from a save-game stream. Clears all
        /// existing state first (ResetState), then recreates entities with their
        /// original IDs and deserializes each component. After this call the caller
        /// must rebuild derived state (RangeManager index, LOS grid, visual nodes).
        /// </summary>
        public void DeserializeSaveGame(Serialization.IDeserializer d,
            System.Action<ComponentBase>? prepareComponent = null)
        {
            ResetState();

            _rng.Deserialize(d.StringASCII("rng"));
            _entityManager.RestoreNextEntityId(d.NumberU32("nextEntityId"));
            // v6: restore the player registry (its Deserialize clears the stale pre-load
            // mappings first, then re-points each player at its live entity).
            Players.Deserialize(d);

            uint entityCount = d.NumberU32("entityCount");
            for (uint i = 0; i < entityCount; i++)
            {
                var id = new EntityId(d.NumberU32("entityId"));
                _componentsByEntity[id] = new Dictionary<InterfaceId, IComponent>();
                _allEntities.Add(id);

                uint compCount = d.NumberU32("compCount");
                for (uint j = 0; j < compCount; j++)
                {
                    string typeName = d.StringASCII("typeName");
                    var cid = _registry.GetComponentType(typeName);
                    if (!cid.IsValid)
                        throw new InvalidDataException(
                            $"Unknown component type '{typeName}' on entity {id} " +
                            $"(entity {i+1}/{entityCount}, comp {j+1}/{compCount})");
                    var comp = _registry.CreateComponent(cid);
                    comp.SetEntity(id);
                    var iid = _registry.GetInterfaceForComponent(cid);
                    _componentsByEntity[id][iid] = comp;
                    ((IComponent)comp).Init();
                    // Let the caller inject external dependencies (RangeManager for
                    // LosManagerComponent, etc.) before deserializing state.
                    prepareComponent?.Invoke(comp);
                    try
                    {
                        comp.Deserialize(d);
                    }
                    catch (System.Exception ex) when (ex is not InvalidDataException)
                    {
                        throw new InvalidDataException(
                            $"Deserialize failed for '{typeName}' on entity {id} " +
                            $"(entity {i+1}/{entityCount}, comp {j+1}/{compCount}): {ex.Message}");
                    }
                }
            }

            // 触发器系统(与 SerializeSaveGame 写序逐位一致;Enabled/Elapsed 骑缝,
            // 条件/动作静态定义由地图脚本重注)。
            Triggers.Deserialize(d);
            // 原版 OnDeserialized:读档完成后广播(触发器脚本重建瞬态)。
            Triggers.NotifyDeserialized(this);
        }
    }

    public interface IComponentMessageHandler
    {
        void HandleMessage(IMessage message);
    }
}
