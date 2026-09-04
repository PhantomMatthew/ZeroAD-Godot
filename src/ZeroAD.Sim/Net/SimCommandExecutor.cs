using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    /// <summary>
    /// The ONE place player commands are applied to the sim. Both the single-player
    /// presentation path (SimBridge.CommandX wrappers) and the lockstep path
    /// (NetTurnManager) delegate here, so SP and MP can never diverge in command
    /// semantics — the historical "hardcoded villager in NetTurnManager" OOS was
    /// exactly this class of divergence.
    /// </summary>
    public sealed class SimCommandExecutor
    {
        private readonly ComponentManager _cm;
        private readonly PathfinderComponent? _pathfinder;
        private readonly Components.TerritoryManager? _territory;

        // ── 多建造者去重(对齐原版 construct 命令带 entities 数组)──
        // 原版一条 construct 命令带多个 builder,spawn 一个地基后给所有 builder 派 repair。
        // C# NetCommand 是固定字段 struct,一条命令只带一个 builder,所以 UI 给每个选中
        // builder 都发 CommandBuild;此处检测"同 turn + 同玩家 + 同位置 + 同模板"的后续
        // Build 命令,跳过 spawn/扣费,改为把当前 builder 派去帮建已有地基。
        // 双端同判(同 turn 同命令序列 → 同去重结果),确定性不受影响。
        private uint _dedupTurn;
        private uint _currentTurn;
        private readonly Dictionary<(uint player, int x, int z, string template), EntityId> _buildSites = new();
        /// <summary>本回合已领到 Build 命令的建造者(同回合再领则排队施工——墙链/连放)。</summary>
        private readonly HashSet<EntityId> _buildOrderBuilders = new();

        /// <summary>每 turn 执行命令前调(NetTurnManager.AdvanceTurn 调一次)。
        /// 清多建造者去重缓存,避免跨 turn 误合并地基。</summary>
        public void BeginTurn(uint turn)
        {
            _currentTurn = turn;
            _dedupTurn = turn;
            _buildSites.Clear();
            _buildOrderBuilders.Clear();
        }

        /// <param name="pathfinder">Optional explicit pathfinder for build-placement
        /// validation. When null, falls back to <see cref="SimSystem.Pathfinder"/>
        /// (the production wiring); tests can inject one to avoid the static.</param>
        /// <param name="territory">Optional explicit territory manager for the build
        /// territory restriction. When null, falls back to <see cref="SimSystem.Territory"/>.</param>
        public SimCommandExecutor(ComponentManager cm, PathfinderComponent? pathfinder = null,
            Components.TerritoryManager? territory = null)
        {
            _cm = cm;
            _pathfinder = pathfinder;
            _territory = territory;
        }

        public void Apply(NetCommand cmd)
        {
            // 去重缓存懒清空:turn 推进时(NetTurnManager 调 BeginTurn)已清;此处的
            // _dedupTurn 对比是保险——即便漏调 BeginTurn 也不会跨 turn 误合并。
            if (_buildSites.Count > 0 && _dedupTurn != _currentTurn)
            {
                _buildSites.Clear();
                _buildOrderBuilders.Clear();
            }
            switch (cmd.Type)
            {
                // Entity-bearing commands: EntityId is the acted-on entity (validated ≠ 0).
                case NetCommandType.Move: ApplyMove(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Gather: ApplyGather(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Attack: ApplyAttack(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Train: ApplyTrain(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Build: ApplyBuild(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Research: ApplyResearch(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.SetRallyPoint: ApplySetRallyPoint(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Stop: _cm.QueryInterface<UnitAIComponent>(new EntityId(cmd.EntityId))?.Stop(); break;
                case NetCommandType.Delete: ApplyDelete(cmd); break;
                case NetCommandType.CancelProduction:
                    _cm.QueryInterface<ProductionQueue>(new EntityId(cmd.EntityId))?.CancelAt(cmd.IntParam1, _cm);
                    break;
                case NetCommandType.SetUnitStance: ApplySetUnitStance(cmd); break;
                case NetCommandType.Garrison: ApplyGarrison(cmd); break;
                case NetCommandType.Ungarrison: ApplyUngarrison(cmd); break;
                // Player-level commands (外交/贸易):无 entity,EntityId=0,不构造 EntityId(0 非法)。
                case NetCommandType.SetStance: ApplySetStance(cmd); break;
                case NetCommandType.Tribute: ApplyTribute(cmd); break;
                case NetCommandType.SetTradingGoods: ApplySetTradingGoods(cmd); break;
                case NetCommandType.Barter: ApplyBarter(cmd); break;
                // Phase 4 缺口
                case NetCommandType.Repair: ApplyRepair(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.ReturnResource: ApplyReturnResource(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.AttackWalk: ApplyAttackWalk(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.WalkToRange: ApplyWalkToRange(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.SetupTradeRoute: ApplySetupTradeRoute(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.CollectTreasure: ApplyCollectTreasure(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Guard: ApplyGuard(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Patrol: ApplyPatrol(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Formation: ApplyFormation(cmd); break;
                case NetCommandType.Pack: ApplyPack(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.AttackRequest:
                    // 盟友请求进攻(原版 events.AttackRequest 广播点)。
                    _cm.Events.RaiseAttackRequested(new Events.AttackRequestedEvent
                    { SourcePlayer = (int)cmd.Player, TargetPlayer = cmd.IntParam1 });
                    break;
                case NetCommandType.Upgrade: ApplyUpgrade(new EntityId(cmd.EntityId), cmd); break;
                case NetCommandType.Gate: ApplyGate(new EntityId(cmd.EntityId), cmd); break;
            }
        }

        private void ApplyMove(EntityId entity, NetCommand cmd)
        {
            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
            // Route through UnitAI when present (the canonical command sink); otherwise
            // fall back to direct UnitMotion for legacy entities.
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
                ai.Walk(new FixedVector2D(x, z));
            else
                _cm.QueryInterface<UnitMotion>(entity)?.MoveToPoint(new FixedVector2D(x, z));
        }

        private void ApplyGather(EntityId entity, NetCommand cmd)
        {
            var target = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
            {
                ai.Gather(target);
            }
            else
            {
                var motion = _cm.QueryInterface<UnitMotion>(entity);
                var gatherer = _cm.QueryInterface<ResourceGatherer>(entity);
                var supply = _cm.QueryInterface<ResourceSupply>(target);
                var supplyPos = _cm.QueryInterface<PositionComponent>(target);
                var unitAi = _cm.QueryInterface<UnitAIComponent>(entity);
                // 有 UnitAI 走订单链(Order.Gather handler;含狩猎重定向:活体动物先攻击,
                // 死后采尸体),与攻击命令同构;无 UnitAI 才落旧直接驱动。
                if (unitAi != null)
                {
                    unitAi.Gather(target);
                }
                else if (gatherer != null && supply != null && supplyPos != null && motion != null)
                {
                    gatherer.TargetSupply = target;
                    gatherer.CarryType = supply.Type;
                    gatherer.State = ResourceGatherer.GatherState.MovingToResource;
                    motion.MoveToPoint(new FixedVector2D(supplyPos.Position.X, supplyPos.Position.Z));
                }
            }
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "gather", Target = target });
        }

        private void ApplyAttack(EntityId entity, NetCommand cmd)
        {
            var target = new EntityId((uint)cmd.IntParam1);
            bool allowCapture = cmd.IntParam2 != 0;
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
                ai.Attack(target, allowCapture);
            else if (_cm.QueryInterface<BuildingAIComponent>(entity) is { } bai)
                // 原版 Commands.js:建筑选中+攻击敌 → 手动集火(BuildingAI.unitAITarget/
                // focusTargets;Shift 追加 = queued)。
                bai.SetUnitAITarget(target);
            else
                _cm.QueryInterface<AttackComponent>(entity)?.AttackTarget(_cm, target, allowCapture);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "attack", Target = target });
        }

        private void ApplyTrain(EntityId entity, NetCommand cmd)
        {
            var queue = _cm.QueryInterface<ProductionQueue>(entity);
            if (queue == null) return;
            string template = string.IsNullOrEmpty(cmd.TemplateName)
                ? "units/spart/support_civilian"
                : cmd.TemplateName;
            if (!queue.EnqueueTraining(template, Math.Max(1, cmd.IntParam1), _cm))
            {
                // 训练拒绝(资源/人口/上限):与 build-rejected 同通道回传,GUI 弹红字。
                // 双端同判,各端只看到自己指令的拒绝。
                var e = new PlayerCommandEvent { Type = "train-rejected" };
                e.Data["player"] = (int)cmd.Player;
                e.Data["reason"] = queue.LastRejectionReason ?? "unknown";
                _cm.Events.RaisePlayerCommand(e);
            }
        }

        private void ApplyBuild(EntityId builder, NetCommand cmd)
        {
            string template = cmd.TemplateName;
            if (template.Length == 0) return;
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (player == null) return;

            // Deterministic cost from template data — identical on every peer.
            TemplateStats? stats = null;
            try { stats = _cm.Templates?.ExtractStats(template); } catch { }
            int wood = stats?.WoodCost ?? 0;
            int stone = stats?.StoneCost ?? 0;
            int metal = stats?.MetalCost ?? 0;
            int food = stats?.FoodCost ?? 0;
            float buildTime = stats != null && stats.BuildTime > 0f ? stats.BuildTime : 8.0f;
            if (!player.CanAfford(wood, food, stone, metal))
            {
                RaiseBuildRejected(cmd, "cannot-afford");
                return;
            }

            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);

            // 多建造者去重:同 turn + 同玩家 + 同位置 + 同模板的后续 Build 命令不再 spawn
            // 新地基/扣费,直接把当前 builder 派去帮建已有地基(对齐原版 construct 带 entities
            // 数组 → spawn 一个地基 + 给所有 builder 派 repair)。位置 key 用 Fixed 内部值
            // (定点,双端一致),不用浮点。
            var siteKey = (cmd.Player, cmd.FixedParam1, cmd.FixedParam2, template);
            if (_buildSites.TryGetValue(siteKey, out var existingFoundation))
            {
                // 该位置本 turn 已有地基:当前 builder 去帮建,不重复扣费/spawn。
                var aiExist = _cm.QueryInterface<UnitAIComponent>(builder);
                if (aiExist != null)
                    aiExist.Repair(existingFoundation);
                else
                    _cm.QueryInterface<BuilderComponent>(builder)?.Build(existingFoundation);
                _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = existingFoundation });
                return;
            }

            // Re-validate placement at execution time (the UI check is only a courtesy
            // pre-filter; both peers must reach the same verdict here).
            var pathfinder = _pathfinder ?? SimSystem.Pathfinder;
            if (pathfinder != null)
            {
                float halfSize = 3f;
                if (stats != null)
                {
                    float ob = Math.Max(stats.ObstructionSize0.ToFloat(), stats.ObstructionSize1.ToFloat());
                    if (ob > 0) halfSize = ob * 0.5f;
                }
                var result = pathfinder.CheckBuildingPlacement(
                    x, z, Fixed.FromFloat(halfSize), Fixed.FromFloat(halfSize),
                    // 墙件(Identity 含 Wall 类)允许与同玩家其他墙件互叠(拼链段搭进塔楼,
                    // 原版靠 control group;此处换算为该玩家的墙组)。
                    allowedGroup: stats != null && stats.GetClassList().Contains("Wall")
                        ? Components.ObstructionComponent.PlayerWallGroup((int)cmd.Player)
                        : 0u);
                if (result != PlacementResult.Success)
                {
                    RaiseBuildRejected(cmd, "invalid-placement");
                    return;
                }
            }

            // 领土限制(对齐 BuildRestrictions.js:186-240,双端同判定):own/ally/neutral/enemy
            // + 未连通需 neutral。tokens 空 = 无限制(非建筑)。
            var territory = _territory ?? Components.SimSystem.Territory;
            if (territory != null)
            {
                string tokens = stats?.BuildRestrictionsTerritory ?? "";
                if (!territory.CanBuildHere(tokens, (int)cmd.Player, x, z))
                {
                    RaiseBuildRejected(cmd, "territory");
                    return;
                }
            }

            player.Spend(wood, food, stone, metal);
            // Yaw(原版 cmd.angle):IntParam1 载 Fixed.InternalValue。建造默认 GUI 给 3π/4。
            var angle = Fixed.Zero.WithInternalValue(cmd.IntParam1);
            var foundation = SpawnFoundation(template, x, z, angle, buildTime, (int)cmd.Player);
            // 记录本 turn 此位置的地基,供同批后续 Build 命令去重(多建造者合一地基)。
            _buildSites[siteKey] = foundation;

            // 同回合同建造者的后续 Build(墙链各件/Shift 连放)排队施工而非顶替——
            // 对齐原版 construct-wall 的 autorepair/autocontinue 语义。
            bool queueBuild = !_buildOrderBuilders.Add(builder);
            var ai = _cm.QueryInterface<UnitAIComponent>(builder);
            if (ai != null)
                ai.Repair(foundation, queued: queueBuild);
            else
                _cm.QueryInterface<BuilderComponent>(builder)?.Build(foundation);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = foundation });
            // 原版 cmd type "construct"(地基落锤即报;教程/触发器的 OnPlayerCommand 用)。
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent
            {
                Type = "construct",
                Target = foundation,
                Data = { ["template"] = template },
            });
        }

        /// <summary>建造拒绝事件(原版 GUI 红字提示的移植:此前拒绝全静默,玩家点地面
        /// 无地基且无任何反馈)。表现层过滤 player==本地玩家后弹 toast;事件不进存档,
        /// 双端同判故各端只看到自己指令的拒绝。</summary>
        private void RaiseBuildRejected(NetCommand cmd, string reason)
        {
            var e = new PlayerCommandEvent { Type = "build-rejected" };
            e.Data["player"] = (int)cmd.Player;
            e.Data["reason"] = reason;
            _cm.Events.RaisePlayerCommand(e);
        }

        /// <summary>
        /// Kernel-side foundation spawn (moved out of SimBridge so the lockstep path can
        /// run it headless). Visuals are built by the presentation layer via the
        /// EntityCreated event raised here. The foundation's ResultTemplate is the FULL
        /// template name; the completion path (SimBridge.TickFoundations, migrated in
        /// Task 7) reads IdentityComponent.TemplateName directly instead of re-mapping a
        /// display name — so the full template must travel here, not a UI short name.
        /// </summary>
        private EntityId SpawnFoundation(string template, Fixed x, Fixed z, Fixed angle, float buildTime, int ownerPlayerId)
        {
            var entity = _cm.CreateEntity();
            _cm.AddComponent(entity, new PositionComponent());
            _cm.AddComponent(entity, new FoundationComponent());
            string displayName = template.Substring(template.LastIndexOf('/') + 1);
            TemplateStats? stats = null;
            try { stats = _cm.Templates?.ExtractStats(template); } catch { }
            _cm.AddComponent(entity, new IdentityComponent
            {
                Name = displayName + " (building)",
                TemplateName = template,
                IsBuilding = true,
                IsUnit = false,
                Classes = stats?.GetClassList() ?? new List<string> { displayName }
            });
            _cm.AddComponent(entity, new HealthComponent { Current = 200, Max = 200 });
            _cm.AddComponent(entity, new OwnershipComponent { PlayerId = ownerPlayerId });
            _cm.QueryInterface<FoundationComponent>(entity)?.Configure(template, buildTime);
            var pos = _cm.QueryInterface<PositionComponent>(entity);
            if (pos != null)
            {
                pos.Position = new FixedVector3D(x, Fixed.Zero, z);
                // Yaw 来自玩家放置角度(原版 Commands.js:1187 cmpPosition.SetYRotation(angle))。
                // 完工换模板时 SimBridge.TickFoundations 读 Rotation.Y 继承给最终建筑
                // (原版 Transform.js:57-58)。Rotation.X/Z 留 0(建筑不俯仰/侧倾)。
                pos.Rotation = new FixedVector3D(Fixed.Zero, angle, Fixed.Zero);
            }
            _cm.Events.RaiseEntityCreated(new EntityCreatedEvent
            {
                Entity = entity,
                TemplateName = template,
                OwnerPlayerId = ownerPlayerId
            });
            // Fog-of-war registration (Fogging/RetainInFog from the structure template —
            // foundations stand in explored fog and mirage like completed buildings).
            EntityAssembler.RegisterForLos(_cm, entity, template, stats);
            // 原版 MT_ConstructionStarted(地基放下即广播;触发器 OnConstructionStarted)。
            _cm.Events.RaiseConstructionStarted(new Events.ConstructionStartedEvent
            {
                Foundation = entity,
                Template = template,
                OwnerPlayerId = ownerPlayerId,
            });
            return entity;
        }

        private void ApplyResearch(EntityId building, NetCommand cmd)
        {
            var researcher = _cm.QueryInterface<ResearcherComponent>(building);
            var playerEntityId = _cm.GetPlayerEntityId((int)cmd.Player);
            var techMgr = playerEntityId.HasValue
                ? _cm.QueryInterface<TechnologyManager>(playerEntityId.Value)
                : null;
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (researcher == null || techMgr == null || player == null) return;
            if (!researcher.StartResearch(cmd.TemplateName, techMgr, player)) return;
            _cm.Events.RaiseResearchQueued(new ResearchQueuedEvent
            {
                ResearcherEntity = building,
                TechnologyTemplate = cmd.TemplateName
            });
        }

        private void ApplySetRallyPoint(EntityId building, NetCommand cmd)
        {
            var rally = _cm.QueryInterface<RallyPointComponent>(building);
            if (rally == null) return;
            EntityId? target = cmd.IntParam1 != 0 ? new EntityId((uint)cmd.IntParam1) : null;
            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
            bool append = (cmd.IntParam2 & 1) != 0;

            // 扩展字段(TemplateName = "cmd;res"):原版 getActionInfo 的指令类型 +
            // 资源子类。空/未知 → 旧行为(单点 walk/采集锚)。
            string commandType = "walk";
            string resourceType = "";
            if (!string.IsNullOrEmpty(cmd.TemplateName))
            {
                var parts = cmd.TemplateName.Split(';');
                commandType = parts[0];
                if (parts.Length > 1) resourceType = parts[1];
            }

            int player = _cm.QueryInterface<OwnershipComponent>(building)?.PlayerId ?? -1;
            if (!append)
            {
                rally.Unset(player);   // 原版:无 Shift 重设单点(清空队列)。
                // 原版 GUI 直发 unset-rallypoint(右键建筑自身清集结);教程目标靠它判。
                if (target.HasValue && target.Value == building)
                {
                    _cm.Events.RaisePlayerCommand(new PlayerCommandEvent
                    { Type = "unset-rallypoint", Target = null });
                    _cm.Events.RaisePlayerCommand(new PlayerCommandEvent
                    { Type = "set-rallypoint", Target = null });
                    return;
                }
            }

            if (target.HasValue)
            {
                var pos = _cm.QueryInterface<PositionComponent>(target.Value);
                if (pos == null) return;
                x = pos.Position.X; z = pos.Position.Z;
            }
            else if (x == Fixed.Zero && z == Fixed.Zero && cmd.TemplateName == "")
            {
                // 旧清零路径(无坐标无目标无指令):等价旧"清空"。
                rally.Unset(player);
                _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "set-rallypoint", Target = null });
                return;
            }

            rally.AddPosition(new FixedVector2D(x, z), player);
            rally.AddData(new RallyPointComponent.RallyPointData
            {
                Command = commandType,
                Target = target?.Value ?? 0,
                ResourceType = resourceType,
            }, player);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent
            {
                Type = "set-rallypoint",
                Target = target,
                // 原版 cmd.data.command/resourceType(教程 set-rallypoint 目标校验用)。
                Data = { ["command"] = commandType, ["resourceType"] = resourceType },
            });
        }

        /// <summary>
        /// 删除己方实体(原版 Commands.js delete-entities:仅允许删自己拥有的实体;
        /// 三道豁免门槛已对齐原版 L390-403——Undeletable 模板 / 占领点未过半 / 须先猎杀
        /// 的资源;controlAllUnits 作弊未移植)。DestroyEntity 自带索引清理
        /// (RangeManager/ObstructionManager 经 NotifyEntityDestroyed 摘除)。
        /// </summary>
        private void ApplyDelete(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            var owner = _cm.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;

            if (_cm.QueryInterface<IdentityComponent>(entity) is { Undeletable: true }) return;
            var capturable = _cm.QueryInterface<CapturableComponent>(entity);
            if (capturable != null && capturable.MaxCapturePoints > Maths.Fixed.Zero
                && capturable.CapturePoints[(int)cmd.Player] < capturable.MaxCapturePoints / 2) return;
            if (_cm.QueryInterface<Components.ResourceSupply>(entity)?.KillBeforeGather == true) return;

            _cm.DestroyEntity(entity);
        }

        /// <summary>改单位站姿(原版 stance 命令;EntityId=单位,TemplateName=站姿名)。
        /// 与 Delete 同样的归属校验——只能改自己单位的站姿。</summary>
        private void ApplySetUnitStance(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            var owner = _cm.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;
            _cm.QueryInterface<UnitAIComponent>(entity)?.SetStance(cmd.TemplateName, _cm);
        }

        /// <summary>载入驻军(原版 garrison 命令;EntityId=单位,IntParam1=宿主)。
        /// 单位归属校验同 Delete;宿主是否接受由 UnitAI 的 GARRISON 子树
        /// (Garrisonable.CanGarrison → GarrisonHolder)在执行订单时判定。</summary>
        private void ApplyGarrison(NetCommand cmd)
        {
            var unit = new EntityId(cmd.EntityId);
            var owner = _cm.QueryInterface<OwnershipComponent>(unit);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;
            _cm.QueryInterface<UnitAIComponent>(unit)?.Garrison(new EntityId((uint)cmd.IntParam1));
        }

        /// <summary>卸载驻军(原版 unload/unload-all-by-owner;EntityId=宿主,
        /// IntParam1=要卸载的实体,-1=全部)。宿主归属校验:只能卸载自己建筑里的驻军。</summary>
        private void ApplyUngarrison(NetCommand cmd)
        {
            var holder = new EntityId(cmd.EntityId);
            var owner = _cm.QueryInterface<OwnershipComponent>(holder);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;
            var garrison = _cm.QueryInterface<GarrisonHolderComponent>(holder);
            if (garrison == null) return;
            if (cmd.IntParam1 < 0)
                garrison.UnloadAll(_cm);
            else
                garrison.Unload(_cm, new EntityId((uint)cmd.IntParam1));
        }

        // ── 第二梯队菜单面板:外交/贸易命令(均玩家级,不用 entity) ────────────────

        private void ApplySetStance(NetCommand cmd)
        {
            int localId = (int)cmd.Player;
            int targetId = cmd.IntParam1;
            int stance = cmd.IntParam2;
            var localEid = _cm.GetPlayerEntityId(localId);
            var targetEid = _cm.GetPlayerEntityId(targetId);
            if (!localEid.HasValue || !targetEid.HasValue) return;
            var localDip = _cm.QueryInterface<DiplomacyComponent>(localEid.Value);
            var targetDip = _cm.QueryInterface<DiplomacyComponent>(targetEid.Value);
            if (localDip == null || targetDip == null) return;
            // ceasefire/teamLock 门(本轮 IsTeamLocked 恒 false,停火延后;保留门以对齐原版 Commands.js)。
            if (localDip.IsTeamLocked()) return;
            localDip.SetStanceToward(localId, targetDip, targetId, stance);
        }

        private void ApplyTribute(NetCommand cmd)
        {
            int destId = cmd.IntParam1;
            int amount = cmd.IntParam2;
            var type = (ResourceType)cmd.FixedParam1;
            var source = _cm.GetPlayerEntity((int)cmd.Player);
            var dest = _cm.GetPlayerEntity(destId);
            if (source == null || dest == null) return;
            if (source.TributeResource(dest, type, amount))
            {
                // 贡品事件（驱动 StatisticsTracker.tributesSent/Received）。镜像 Player.js:686,689。
                _cm.Events.RaiseTribute(new Events.TributeEvent
                {
                    FromPlayerId = (int)cmd.Player,
                    ToPlayerId = destId,
                    Type = type,
                    Amount = amount,
                });
            }
        }

        private void ApplySetTradingGoods(NetCommand cmd)
        {
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (player == null) return;
            // 4 资源百分比(和=100 由 SetTradingGoods 校验)。编码见 NetCommand.SetTradingGoods。
            var goods = new Dictionary<ResourceType, int>
            {
                [ResourceType.Wood] = cmd.IntParam1,
                [ResourceType.Food] = cmd.IntParam2,
                [ResourceType.Stone] = cmd.FixedParam1,
                [ResourceType.Metal] = cmd.FixedParam2,
            };
            player.SetTradingGoods(goods);
        }

        private void ApplyBarter(NetCommand cmd)
        {
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (player == null) return;
            var sell = (ResourceType)cmd.IntParam1;
            var buy = (ResourceType)cmd.IntParam2;
            int amount = cmd.FixedParam1;
            BarterSystem.ExchangeResources(_cm, player, (int)cmd.Player, sell, buy, amount);
        }

        // ── Phase 4 缺口 Apply 方法 ──

        private void ApplyRepair(EntityId builder, NetCommand cmd)
        {
            var target = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(builder);
            if (ai != null) ai.Repair(target);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = target });
        }

        private void ApplyReturnResource(EntityId gatherer, NetCommand cmd)
        {
            // 走 UnitAI 的 ReturnResource 订单(RETURNRESOURCE 子树:接近→交付);
            // 此前直写 gatherer 状态+裸移动,绕开 FSM(蹲点/被打断语义不对)。
            var dropsite = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(gatherer);
            if (ai != null) ai.ReturnResource(dropsite);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "returnresource", Target = dropsite });
        }

        private void ApplyAttackWalk(EntityId entity, NetCommand cmd)
        {
            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
            // AttackWalk = UnitAI WalkAndFight 订单:移动到坐标 + 沿途遇敌自动攻击、打完续行。
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null) ai.WalkAndFight(new Maths.FixedVector2D(x, z));
            else _cm.QueryInterface<UnitMotion>(entity)?.MoveToPoint(new Maths.FixedVector2D(x, z));
        }

        private void ApplyPatrol(EntityId entity, NetCommand cmd)
        {
            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
            // Patrol = UnitAI 巡逻订单:起点⇄目标点往返 + 沿途索敌。
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null) ai.Patrol(new Maths.FixedVector2D(x, z));
        }

        /// <summary>Pack 命令(原版 cmd type:"pack"/"unpack"):攻城器打包/解包订单,
        /// UnitAI PACKING/UNPACKING 状态机驱动(PackComponent 校验自含)。</summary>
        private void ApplyPack(EntityId entity, NetCommand cmd)
        {
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai == null) return;
            if (cmd.IntParam1 == 1) ai.Unpack();
            else ai.Pack();
        }

        /// <summary>Gate 命令(原版 cmd lock-gate/unlock-gate):城门锁切换,
        /// GateComponent 联动阻挡活性+重建寻路网格。仅属主可操作。</summary>
        private void ApplyGate(EntityId gate, NetCommand cmd)
        {
            var owner = _cm.QueryInterface<OwnershipComponent>(gate);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;
            _cm.QueryInterface<GateComponent>(gate)?.SetLocked(_cm, cmd.IntParam1 == 1);
        }

        private void ApplyUpgrade(EntityId building, NetCommand cmd)
        {
            var identity = _cm.QueryInterface<IdentityComponent>(building);
            var owner = _cm.QueryInterface<OwnershipComponent>(building);
            var pos = _cm.QueryInterface<PositionComponent>(building);
            if (identity == null || owner == null || pos == null) return;
            if (owner.PlayerId != (int)cmd.Player) return;

            var stats = _cm.Templates?.ExtractStats(identity.TemplateName);
            if (stats == null || stats.UpgradeToTemplate.Length == 0) return;
            string target = stats.UpgradeToTemplate.Replace("{civ}",
                _cm.GetPlayerEntity(owner.PlayerId)?.Civ ?? "");
            if (target.Contains('{') || _cm.Templates?.TemplateExists(target) != true) return;

            var player = _cm.GetPlayerEntity(owner.PlayerId);
            if (player == null) return;

            // 原版 Upgrade.js 模型:原地升级(建筑保留,进度走时间,完成换模板)。
            // 前置:生产队列非空 → 拒(原版 "Entity is producing" 通知);
            // 科技门(Upgrade/Requirements/Techs,含 ! 否定 token);扣费改由组件管账
            // (取消/被毁退还——此前立即拆毁+地基模式无退还路径)。
            var queue = _cm.QueryInterface<ProductionQueue>(building);
            if (queue != null && queue.QueueCount > 0) return;   // 生产中不可升级
            if (stats.UpgradeRequiredTechs.Length > 0)
            {
                var tm = _cm.QueryInterface<TechnologyManager>(_cm.GetPlayerEntityId(owner.PlayerId) ?? default);
                if (tm != null)
                    foreach (var tok in stats.UpgradeRequiredTechs.Split(
                        (char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        bool neg = tok.StartsWith('!');
                        string tech = neg ? tok[1..] : tok;
                        if (tm.IsResearched(tech) == neg) return;   // 门未过
                    }
            }

            var up = _cm.QueryInterface<UpgradeComponent>(building)
                ?? AddUpgradeComponent(building);
            up.StartUpgrade(_cm, target,
                stats.UpgradeTime > 0 ? stats.UpgradeTime : 10f,
                stats.UpgradeCostWood, stats.UpgradeCostFood,
                stats.UpgradeCostStone, stats.UpgradeCostMetal,
                stats.UpgradeVariant, player);

            // 指派的建造者(与 Repair 同路——原版升级不强制工人,建造者在场时
            // 作为 Repair 接近,无工人升级照常计时;组件自己走时间)。
            if (cmd.IntParam1 > 0)
            {
                var builder = new EntityId((uint)cmd.IntParam1);
                var ai = _cm.QueryInterface<UnitAIComponent>(builder);
                if (ai != null) ai.Repair(building);
            }
        }

        private UpgradeComponent AddUpgradeComponent(EntityId building)
        {
            var up = new UpgradeComponent();
            _cm.AddComponent(building, up);
            return up;
        }

        private void ApplyWalkToRange(EntityId entity, NetCommand cmd)
        {
            // 简化：移动到目标位置（忽略 min/max range，精确版需 UnitAI 的 walk-to-range order）
            var target = new EntityId((uint)cmd.IntParam1);
            var targetPos = _cm.QueryInterface<PositionComponent>(target);
            if (targetPos == null) return;
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null) ai.Walk(new Maths.FixedVector2D(targetPos.Position.X, targetPos.Position.Z));
        }

        private void ApplySetupTradeRoute(EntityId trader, NetCommand cmd)
        {
            // SetupTradeRoute = TraderComponent.SetTargetMarket(原版 setup-trade-route 命令):
            // 目标市场(IntParam1),源市场=cmd.EntityId 路线上另一端(由 Trader 组件自洽)。
            var target = new EntityId((uint)cmd.IntParam1);
            var tc = _cm.QueryInterface<TraderComponent>(trader);
            tc?.SetTargetMarket(_cm, target);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "setup-trade-route", Target = target });
        }

        private void ApplyCollectTreasure(EntityId collector, NetCommand cmd)
        {
            var treasure = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(collector);
            if (ai != null) ai.Gather(treasure);  // 简化：TreasureCollector 走 Gather-like 路径
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "collect-treasure", Target = treasure });
        }

        private void ApplyGuard(EntityId guard, NetCommand cmd)
        {
            // Guard = UnitAI 护卫订单:跟随目标 + 响应周边战斗 + 可治疗时自动治疗。
            var target = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(guard);
            if (ai != null) ai.Guard(target);
        }

        /// <summary>Formation 命令(原版 cmd type:"formation"):TemplateName = "shape|id1,id2,..."。
        /// shape=null → 解散成员控制器;否则过滤合格成员(同主、可编队、非驻防/炮塔),
        /// 达 RequiredMemberCount 时在成员质心生 special/formations/{shape} 控制器并 SetMembers
        /// (内核确定性路径,锁步安全;控制器 spawn 在模拟内不在表现层)。</summary>
        private void ApplyFormation(NetCommand cmd)
        {
            string payload = cmd.TemplateName;
            int sep = payload.IndexOf('|');
            if (sep <= 0) return;
            string shape = payload[..sep];
            var ids = payload[(sep + 1)..].Split(',',
                System.StringSplitOptions.RemoveEmptyEntries);

            // shape=remove:成员脱队(原版 RemoveFromFormation;部分选中个体命令前用)。
            if (shape == "remove")
            {
                foreach (var s in ids)
                {
                    if (!uint.TryParse(s, out uint raw)) continue;
                    var e = new EntityId(raw);
                    var ai = _cm.QueryInterface<UnitAIComponent>(e);
                    if (ai?.FormationController is { } fc)
                        _cm.QueryInterface<FormationComponent>(fc)
                            ?.RemoveMembers(_cm, new System.Collections.Generic.List<EntityId> { e });
                }
                return;
            }

            // 原版 GetFormationUnitAIs 的 LoadFormation 分支:选择集恰全在一个现存编队
            // 且命令了不同阵型 → 控制器换模板(LoadFormation),成员原样转挂。
            if (shape != "null" && shape != "remove")
            {
                var controllers = new System.Collections.Generic.HashSet<EntityId>();
                bool allInOne = ids.Length > 0;
                foreach (var s2 in ids)
                {
                    if (!uint.TryParse(s2, out uint raw)) { allInOne = false; break; }
                    var ai = _cm.QueryInterface<UnitAIComponent>(new EntityId(raw));
                    if (ai?.FormationController is not { } fc) { allInOne = false; break; }
                    controllers.Add(fc);
                }
                if (allInOne && controllers.Count == 1)
                {
                    var fc = default(EntityId);
                    foreach (var c in controllers) fc = c;   // 单元素取出
                    var formation = _cm.QueryInterface<FormationComponent>(fc);
                    var identity = _cm.QueryInterface<IdentityComponent>(fc);
                    string newTemplate = "special/formations/" + shape;
                    if (formation != null && identity != null
                        && identity.TemplateName != newTemplate)
                    {
                        // 成员须全可编新阵型(原版 CanMoveEntsIntoFormation)。
                        bool canAll = true;
                        foreach (var m in formation.Members)
                        {
                            var mai = _cm.QueryInterface<UnitAIComponent>(m);
                            if (mai == null || !mai.CanUseFormation(_cm, shape)) { canAll = false; break; }
                        }
                        if (canAll)
                        {
                            formation.LoadFormation(_cm, newTemplate);
                            return;
                        }
                    }
                }
            }

            // 成员过滤:同主(命令玩家)、有 UnitAI、非驻防/炮塔/已在编队,
            // 且模板可编该阵型(原版 GetFormationUnitAIs → UnitAI.CanUseFormation:
            // <Formations disable=""/> 的村民/无列表的攻城器与船 不计数也不入队,
            // 它们在原版改收个体令——本命令仅编队,个体令由表现层另发,天然兼容)。
            // shape=null(解散)不走此过滤:下方按全量 id 找控制器 Disband。
            var members = new System.Collections.Generic.List<EntityId>();
            int owner = (int)cmd.Player;
            foreach (var s in ids)
            {
                if (!uint.TryParse(s, out uint raw)) continue;
                var e = new EntityId(raw);
                var ai = _cm.QueryInterface<UnitAIComponent>(e);
                if (ai == null || ai.IsGarrisoned || ai.IsTurret
                    || ai.FormationController != null || ai.IsFormationController)
                    continue;
                if ((_cm.QueryInterface<OwnershipComponent>(e)?.PlayerId ?? -1) != owner) continue;
                if (shape != "null" && !ai.CanUseFormation(_cm, shape)) continue;
                members.Add(e);
            }

            if (shape == "null")
            {
                // 解散(原版 formation null):找出成员所在控制器并 Disband。
                var controllers = new System.Collections.Generic.HashSet<EntityId>();
                foreach (var m in members)
                {
                    var ai = _cm.QueryInterface<UnitAIComponent>(m);
                    if (ai?.FormationController is { } fc) controllers.Add(fc);
                }
                // 上面过滤掉了已在编队的成员 —— null 语义须查全部传入 id。
                foreach (var s in ids)
                {
                    if (!uint.TryParse(s, out uint raw)) continue;
                    var ai = _cm.QueryInterface<UnitAIComponent>(new EntityId(raw));
                    if (ai?.FormationController is { } fc) controllers.Add(fc);
                }
                foreach (var fc in controllers)
                    _cm.QueryInterface<FormationComponent>(fc)?.Disband(_cm);
                return;
            }

            if (members.Count == 0) return;
            string template = "special/formations/" + shape;
            Content.TemplateStats? stats = null;
            try { stats = _cm.Templates?.ExtractStats(template); } catch { }
            int required = stats?.FormationRequiredMemberCount ?? int.MaxValue;
            if (stats == null || members.Count < required) return;

            // 原版 GetFormationUnitAIs 分簇:按可达陆区分簇,每簇一队;
            // 同批簇队互登孪生(twin merge 的判定域)。无分层寻路(测试环境)→ 单簇。
            var clusters = new System.Collections.Generic.List<System.Collections.Generic.List<EntityId>>();
            var hier = SimSystem.Pathfinder;
            if (hier?.PassabilityGrid != null)
            {
                var byRegion = new System.Collections.Generic.Dictionary<uint,
                    System.Collections.Generic.List<EntityId>>();
                foreach (var m in members)
                {
                    var pos = _cm.QueryInterface<PositionComponent>(m);
                    // 分层寻路的全局陆区(0 = 不可达/界外——各归一簇)。
                    uint region = pos != null
                        ? hier.GetLandRegion(pos.Position.X, pos.Position.Z) : 0;
                    if (!byRegion.TryGetValue(region, out var list))
                        byRegion[region] = list = new System.Collections.Generic.List<EntityId>();
                    list.Add(m);
                }
                clusters.AddRange(byRegion.Values);
            }
            else
                clusters.Add(members);

            // 每簇建队(够员才建),互登孪生。
            var formationEnts = new System.Collections.Generic.List<EntityId>();
            foreach (var cluster in clusters)
            {
                if (cluster.Count < required) continue;
                float ax = 0, az = 0;
                foreach (var m in cluster)
                {
                    var p = _cm.QueryInterface<PositionComponent>(m);
                    if (p == null) continue;
                    ax += p.Position.X.ToFloat();
                    az += p.Position.Z.ToFloat();
                }
                ax /= cluster.Count;
                az /= cluster.Count;
                var controller = _cm.SpawnEntity(template, ax, az, owner);
                var formation = _cm.QueryInterface<FormationComponent>(controller);
                if (formation == null) continue;
                formation.SetMembers(_cm, cluster);
                formationEnts.Add(controller);
            }
            // 孪生互登(原版 RegisterTwinFormation 于建队后)。
            foreach (var fc in formationEnts)
                foreach (var other in formationEnts)
                    if (other != fc)
                        _cm.QueryInterface<FormationComponent>(fc)
                            ?.RegisterTwinFormation(_cm, other);
        }
    }
}
