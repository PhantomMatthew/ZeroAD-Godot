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
                if (gatherer != null && supply != null && supplyPos != null && motion != null)
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
                    x, z, Fixed.FromFloat(halfSize), Fixed.FromFloat(halfSize));
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
            var foundation = SpawnFoundation(template, x, z, buildTime, (int)cmd.Player);

            var ai = _cm.QueryInterface<UnitAIComponent>(builder);
            if (ai != null)
                ai.Repair(foundation);
            else
                _cm.QueryInterface<BuilderComponent>(builder)?.Build(foundation);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = foundation });
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
        private EntityId SpawnFoundation(string template, Fixed x, Fixed z, float buildTime, int ownerPlayerId)
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
                pos.Position = new FixedVector3D(x, Fixed.Zero, z);
            _cm.Events.RaiseEntityCreated(new EntityCreatedEvent
            {
                Entity = entity,
                TemplateName = template,
                OwnerPlayerId = ownerPlayerId
            });
            // Fog-of-war registration (Fogging/RetainInFog from the structure template —
            // foundations stand in explored fog and mirage like completed buildings).
            EntityAssembler.RegisterForLos(_cm, entity, template, stats);
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
            EntityId? target = null;
            if (cmd.IntParam1 != 0)
            {
                // Entity rally (resource gather anchor): rally to the target entity position.
                target = new EntityId((uint)cmd.IntParam1);
                var pos = _cm.QueryInterface<PositionComponent>(target.Value);
                if (pos != null)
                    rally.Set(new FixedVector2D(pos.Position.X, pos.Position.Z));
            }
            else if (cmd.FixedParam1 != 0 || cmd.FixedParam2 != 0)
            {
                // Ground rally (right-click empty ground): rally to world x/z.
                var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
                var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
                rally.Set(new FixedVector2D(x, z));
            }
            else
            {
                // Clear: reset to zero so Position.IsZero reads as "no rally point".
                rally.Set(new FixedVector2D(Fixed.Zero, Fixed.Zero));
            }
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "set-rallypoint", Target = target });
        }

        /// <summary>
        /// 删除己方实体(原版 delete-entities 的简化:仅允许删自己拥有的实体;原版另有
        /// IsUndeletable/占领点数门槛,本移植暂不引入)。DestroyEntity 自带索引清理
        /// (RangeManager/ObstructionManager 经 NotifyEntityDestroyed 摘除)。
        /// </summary>
        private void ApplyDelete(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            var owner = _cm.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId != (int)cmd.Player) return;
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
            source.TributeResource(dest, type, amount);
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
    }
}
