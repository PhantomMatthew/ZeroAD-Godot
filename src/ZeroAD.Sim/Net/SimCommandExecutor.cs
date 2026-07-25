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

        /// <param name="pathfinder">Optional explicit pathfinder for build-placement
        /// validation. When null, falls back to <see cref="SimSystem.Pathfinder"/>
        /// (the production wiring); tests can inject one to avoid the static.</param>
        public SimCommandExecutor(ComponentManager cm, PathfinderComponent? pathfinder = null)
        {
            _cm = cm;
            _pathfinder = pathfinder;
        }

        public void Apply(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            switch (cmd.Type)
            {
                case NetCommandType.Move: ApplyMove(entity, cmd); break;
                case NetCommandType.Gather: ApplyGather(entity, cmd); break;
                case NetCommandType.Attack: ApplyAttack(entity, cmd); break;
                case NetCommandType.Train: ApplyTrain(entity, cmd); break;
                case NetCommandType.Build: ApplyBuild(entity, cmd); break;
                case NetCommandType.Research: ApplyResearch(entity, cmd); break;
                case NetCommandType.SetRallyPoint: ApplySetRallyPoint(entity, cmd); break;
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
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
                ai.Attack(target);
            else
                _cm.QueryInterface<AttackComponent>(entity)?.AttackTarget(target);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "attack", Target = target });
        }

        private void ApplyTrain(EntityId entity, NetCommand cmd)
        {
            var queue = _cm.QueryInterface<ProductionQueue>(entity);
            if (queue == null) return;
            string template = string.IsNullOrEmpty(cmd.TemplateName)
                ? "units/spart/support_civilian"
                : cmd.TemplateName;
            queue.EnqueueTraining(template, Math.Max(1, cmd.IntParam1), _cm);
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
            if (!player.CanAfford(wood, food, stone, metal)) return;

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
                if (result != PlacementResult.Success) return;
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
                target = new EntityId((uint)cmd.IntParam1);
                var pos = _cm.QueryInterface<PositionComponent>(target.Value);
                if (pos != null)
                    rally.Set(new FixedVector2D(pos.Position.X, pos.Position.Z));
            }
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "set-rallypoint", Target = target });
        }
    }
}
