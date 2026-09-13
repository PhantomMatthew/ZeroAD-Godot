using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Simulation;

/// <summary>Per-world tick accumulators that are not sim-hash state (gate poll cadence).</summary>
public sealed class SimLoopState
{
    public float GateTickAccum;
}

/// <summary>Presentation/tutorial hooks. Null-safe; RL leaves these unset.</summary>
public sealed class SimLoopHooks
{
    public Action<EntityId>? OnCorpseConverted;
    public Action? TutorialTick;
    /// <summary>完工地基 → 建筑实体的装配器:(模板全名, x, z, 玩家, 朝向 yaw) → 实体。
    /// Godot 侧提供 SimBridge.SpawnScenarioBuilding(Footprint/静态阻挡/生产队列/驻守等建筑组件);
    /// 为空时回落 <see cref="ComponentManager.SpawnEntity"/>——注意那条路径按单位装配
    /// (UnitMotion/UnitAI/单位圆阻挡),只适合尚无建筑装配器的无头 RL 夹具。</summary>
    public Func<string, float, float, int, float, EntityId>? SpawnBuilding;
    /// <summary>地基 ResultTemplate 为旧式短名(如 "House")时映射到模板全名;为空则原样使用。</summary>
    public Func<string, string>? MapBuildTemplate;
}

/// <summary>Shared simulation tick order for Godot <c>SimBridge</c> and the headless RL host.
/// Phase names match the former SimBridge.TickSimulation labels so profiling stays comparable.</summary>
public static class SimLoop
{
    public delegate void PhaseHook(string name, Action fn);

    public static void Tick(ComponentManager cm, float dt, SimLoopState state,
        PhaseHook? profile = null, SimLoopHooks? hooks = null)
    {
        void P(string name, Action fn)
        {
            if (profile != null)
            {
                profile(name, fn);
                return;
            }
            try { fn(); }
            catch (Exception ex) { Diag.Err("Sim", $"tick phase {name} failed: {ex.Message}"); }
        }

        var pathfinder = SimSystem.Pathfinder;
        var range = cm.Range ?? SimSystem.Range;
        var territory = SimSystem.Territory;

        P("pathharvest", () => pathfinder?.HarvestPathResults());
        P("dead", () => RemoveDeadEntities(cm, hooks));
        P("motions", () => ForEach(cm, e => cm.QueryInterface<UnitMotion>(e)?.Tick(dt)));
        P("separation", () => UnitSeparation.Separate(cm, Fixed.FromFloat(dt)));
        P("visionsharing", () =>
        {
            if (range != null) VisionSharingComponent.TickAll(cm, range);
        });
        P("los1", () => range?.UpdateVisibilityData());
        P("unitai", () => ForEach(cm, e => cm.QueryInterface<UnitAIComponent>(e)?.Tick(dt, cm)));
        P("attack", () => ForEach(cm, e => cm.QueryInterface<AttackComponent>(e)?.Tick(dt, cm)));
        P("buildingai", () => ForEach(cm, e => cm.QueryInterface<BuildingAIComponent>(e)?.Tick(dt, cm)));
        P("build", () => ForEach(cm, e => cm.QueryInterface<BuilderComponent>(e)?.Tick(cm)));
        P("prod", () => ForEach(cm, e => cm.QueryInterface<ProductionQueue>(e)?.Tick(dt, cm)));
        P("found", () => CompleteBuiltFoundations(cm, hooks));
        P("research", () => TickResearch(cm, dt));
        P("auras", () => TickAuras(cm, range));
        P("territory", () => TickTerritoryDecay(cm, territory, dt));
        P("garrison", () =>
        {
            ForEach(cm, e =>
            {
                cm.QueryInterface<GarrisonHolderComponent>(e)?.Tick(dt, cm);
                cm.QueryInterface<MotionBallComponent>(e)?.Tick(dt, cm);
            });
        });
        P("turrets", () => ForEach(cm, e => cm.QueryInterface<TurretableComponent>(e)?.UpdatePosition(cm)));
        P("gates", () => TickGates(cm, state, dt));
        P("trickle", () => ForEach(cm, e => cm.QueryInterface<ResourceTrickleComponent>(e)?.Tick(cm, dt)));
        P("closure", () => TickGameplayClosure(cm, dt));
        P("status", () => ForEach(cm, e => cm.QueryInterface<StatusEffectsReceiverComponent>(e)?.Tick(cm, dt)));
        P("visionrange", () =>
        {
            if (range != null) ValueModificationApplier.ReapplyVisionRangeAll(cm, range);
        });
        P("damage", () =>
        {
            cm.DelayedDamage.TickPending(cm);
            cm.DelayedDamage.AdvanceTurn();
        });
        P("victory", () => cm.TickVictory());
        P("tutorial", () => hooks?.TutorialTick?.Invoke());
        P("los2", () => range?.UpdateVisibilityData());
        P("pathgrid", () =>
        {
            pathfinder?.UpdateGrid();
            pathfinder?.StartAsyncPathComputation();
        });
    }

    public static void TickAiBrains(ComponentManager cm)
    {
        foreach (var entity in Snapshot(cm))
            cm.QueryInterface<AIComponent>(entity)?.Tick();
    }

    private static void ForEach(ComponentManager cm, Action<EntityId> fn)
    {
        foreach (var e in Snapshot(cm)) fn(e);
    }

    private static List<EntityId> Snapshot(ComponentManager cm)
    {
        var list = new List<EntityId>(cm.AllEntities.Count);
        foreach (var e in cm.AllEntities) list.Add(e);
        return list;
    }

    private static void RemoveDeadEntities(ComponentManager cm, SimLoopHooks? hooks)
    {
        foreach (var entity in Snapshot(cm))
        {
            var health = cm.QueryInterface<HealthComponent>(entity);
            if (health == null || !health.IsDead) continue;
            cm.QueryInterface<UpgradeComponent>(entity)?.CancelUpgrade(cm);
            if (cm.QueryInterface<CorpseComponent>(entity) != null) continue;
            var deadSupply = cm.QueryInterface<ResourceSupply>(entity);
            var deadOwner = cm.QueryInterface<OwnershipComponent>(entity);
            if (deadSupply != null && deadSupply.KillBeforeGather && deadOwner == null)
            {
                ConvertToCorpse(cm, entity, hooks);
                continue;
            }

            int fromPlayer = deadOwner?.PlayerId ?? -1;
            cm.Events.RaiseOwnershipChanged(new OwnershipChangedEvent
            {
                Entity = entity,
                From = fromPlayer,
                To = -1
            });
            cm.ApplyOwnershipPopChange(entity, fromPlayer, -1);
            cm.QueryInterface<DeathDamageComponent>(entity)?.CauseDeathDamage(cm);
            cm.QueryInterface<GarrisonHolderComponent>(entity)?.EjectOrKillAll(cm);
            cm.QueryInterface<TurretHolderComponent>(entity)?.EjectOrKillAll(cm);
            var turretable = cm.QueryInterface<TurretableComponent>(entity);
            if (turretable is { Holder: not null })
                turretable.LeaveTurret(cm, forced: true);
            var memberAi = cm.QueryInterface<UnitAIComponent>(entity);
            if (memberAi?.FormationController is { } formationCtrl)
                cm.QueryInterface<FormationComponent>(formationCtrl)
                    ?.RemoveMembers(cm, new List<EntityId> { entity });
            cm.DestroyEntity(entity);
        }
    }

    private static void ConvertToCorpse(ComponentManager cm, EntityId entity, SimLoopHooks? hooks)
    {
        cm.AddComponent(entity, new CorpseComponent());
        cm.QueryInterface<UnitAIComponent>(entity)?.OnCorpseConverted(cm);
        var identity = cm.QueryInterface<IdentityComponent>(entity);
        if (identity != null) identity.IsUnit = false;
        hooks?.OnCorpseConverted?.Invoke(entity);
    }

    private static void CompleteBuiltFoundations(ComponentManager cm, SimLoopHooks? hooks)
    {
        if (cm.Templates == null) return;
        var completed = new List<EntityId>();
        foreach (var entity in Snapshot(cm))
        {
            var foundation = cm.QueryInterface<FoundationComponent>(entity);
            if (foundation is { IsBuilt: true }) completed.Add(entity);
        }

        foreach (var entity in completed)
        {
            var foundation = cm.QueryInterface<FoundationComponent>(entity);
            if (foundation == null) continue;
            var pos = cm.QueryInterface<PositionComponent>(entity);
            var identity = cm.QueryInterface<IdentityComponent>(entity);
            // 优先 IdentityComponent 里的模板全名(玩家放置地基由 SimCommandExecutor 写入);
            // 旧式/剧情地基只有短显示名,经 MapBuildTemplate 映射。
            string fullTemplate = !string.IsNullOrEmpty(identity?.TemplateName)
                ? identity!.TemplateName
                : hooks?.MapBuildTemplate?.Invoke(foundation.ResultTemplate) ?? foundation.ResultTemplate;
            if (string.IsNullOrEmpty(fullTemplate)) continue;

            float x = pos?.Position.X.ToFloat() ?? 0f;
            float z = pos?.Position.Z.ToFloat() ?? 0f;
            // 完工继承地基朝向(原版 Transform.js 把 rot.y 拷给新实体)。
            var rot = pos?.Rotation ?? default;
            var owner = cm.QueryInterface<OwnershipComponent>(entity);
            int ownerId = owner?.PlayerId ?? 1;
            cm.DestroyEntity(entity);

            // 建筑必须走建筑装配器:SpawnEntity 按单位装配会给房子挂 UnitMotion/UnitAI
            // (选中可移动)和单位圆阻挡。
            var built = hooks?.SpawnBuilding != null
                ? hooks.SpawnBuilding(fullTemplate, x, z, ownerId, rot.Y.ToFloat())
                : cm.SpawnEntity(fullTemplate, x, z, ownerId);
            var builtPos = cm.QueryInterface<PositionComponent>(built);
            if (builtPos != null) builtPos.Rotation = rot;
            cm.Events.RaiseStructureBuilt(new StructureBuiltEvent
            {
                Building = built,
                TemplateName = fullTemplate
            });
            cm.RecomputePlayerPopBonus(ownerId);
            AutoAssignIdleBuilders(cm, x, z);
        }
    }

    private static void AutoAssignIdleBuilders(ComponentManager cm, float bx, float bz)
    {
        EntityId? nearest = null;
        float nearestDist = 30f * 30f;
        foreach (var e in Snapshot(cm))
        {
            var supply = cm.QueryInterface<ResourceSupply>(e);
            if (supply == null || supply.Amount <= 0) continue;
            var pos = cm.QueryInterface<PositionComponent>(e);
            if (pos == null) continue;
            float dx = pos.Position.X.ToFloat() - bx;
            float dz = pos.Position.Z.ToFloat() - bz;
            float d2 = dx * dx + dz * dz;
            if (d2 < nearestDist)
            {
                nearestDist = d2;
                nearest = e;
            }
        }
        if (nearest == null) return;

        foreach (var e in Snapshot(cm))
        {
            var builder = cm.QueryInterface<BuilderComponent>(e);
            if (builder == null || builder.Target != null) continue;
            if (cm.QueryInterface<ResourceGatherer>(e) == null) continue;
            var motion = cm.QueryInterface<UnitMotion>(e);
            if (motion == null || motion.HasMoveTarget) continue;
            var ai = cm.QueryInterface<UnitAIComponent>(e);
            if (ai?.CurrentOrder != null) continue;
            ai?.Gather(nearest.Value);
        }
    }

    private static void TickResearch(ComponentManager cm, float dt)
    {
        var playerIds = new List<int>(cm.Players.GetNonGaiaPlayerIds());
        playerIds.Sort();
        foreach (int playerId in playerIds)
        {
            var playerEnt = cm.GetPlayerEntityId(playerId);
            if (playerEnt == null) continue;
            var techMgr = cm.QueryInterface<TechnologyManager>(playerEnt.Value);
            if (techMgr == null) continue;
            foreach (var entity in Snapshot(cm))
            {
                var researcher = cm.QueryInterface<ResearcherComponent>(entity);
                if (researcher == null) continue;
                var owner = cm.QueryInterface<OwnershipComponent>(entity);
                if (owner == null || owner.PlayerId != playerId) continue;
                var completed = researcher.Tick(dt, techMgr, cm);
                if (completed == null) continue;
                techMgr.UpdateAutoResearch(cm);
                ValueModificationApplier.RescaleHealth(cm, playerEnt.Value);
                cm.QueryInterface<PlayerComponent>(playerEnt.Value)?.RecomputeBarterMultipliers(cm);
                ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt.Value);
                cm.Events.RaiseResearchFinished(new ResearchFinishedEvent
                {
                    ResearcherEntity = entity,
                    Tech = completed
                });
            }
        }
    }

    private static void TickAuras(ComponentManager cm, RangeManager? range)
    {
        var catalog = cm.Auras;
        if (catalog == null || catalog.Auras.Count == 0 || range == null) return;
        foreach (var entity in Snapshot(cm))
            cm.QueryInterface<AuraComponent>(entity)?.Tick(cm, range, catalog);
    }

    private static void TickTerritoryDecay(ComponentManager cm, TerritoryManager? territory, float dt)
    {
        var fixedDt = Fixed.FromFloat(dt);
        foreach (var entity in Snapshot(cm))
        {
            var decay = cm.QueryInterface<TerritoryDecayComponent>(entity);
            if (decay != null && territory != null) decay.Refresh(cm, territory);
            cm.QueryInterface<CapturableComponent>(entity)?.TimerTick(cm, fixedDt);
        }
    }

    private static void TickGates(ComponentManager cm, SimLoopState state, float dt)
    {
        state.GateTickAccum += dt;
        if (state.GateTickAccum < 0.5f) return;
        state.GateTickAccum = 0f;
        foreach (var entity in Snapshot(cm))
            cm.QueryInterface<GateComponent>(entity)?.OperateGate(cm);
    }

    private static void TickGameplayClosure(ComponentManager cm, float dt)
    {
        foreach (var entity in Snapshot(cm))
        {
            cm.QueryInterface<UpkeepComponent>(entity)?.Tick(cm, dt);
            cm.QueryInterface<AutoBuildableComponent>(entity)?.Tick(cm, dt);
            cm.QueryInterface<AlertRaiserComponent>(entity)?.Tick(dt);
            cm.QueryInterface<AttackDetectionComponent>(entity)?.Tick(dt);
            cm.QueryInterface<BattleDetectionComponent>(entity)?.Tick(dt);
            cm.QueryInterface<HealthComponent>(entity)?.TickRegen(cm, dt);
        }
        BarterSystem.TickRestore(dt);
    }
}
