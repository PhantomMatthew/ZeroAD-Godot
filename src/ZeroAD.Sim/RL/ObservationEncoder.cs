using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.RL;

public static class ObservationEncoder
{
    public static void Encode(ComponentManager cm, RangeManager range, RlCatalog catalog,
        int agentPlayerId, bool privileged, uint turn, RlObservation dest,
        PathfinderComponent? pathfinder = null, TerritoryManager? territory = null)
    {
        Array.Clear(dest.Entities, 0, dest.Entities.Length);
        Array.Clear(dest.Spatial, 0, dest.Spatial.Length);
        Array.Clear(dest.Scalars, 0, dest.Scalars.Length);
        Array.Clear(dest.FunctionMask, 0, dest.FunctionMask.Length);
        Array.Clear(dest.EntityMask, 0, dest.EntityMask.Length);

        var ids = new List<EntityId>(cm.AllEntities.Count);
        foreach (var e in cm.AllEntities) ids.Add(e);
        ids.Sort((a, b) => a.Value.CompareTo(b.Value));

        var rowOf = new Dictionary<uint, int>();
        int row = 0;
        foreach (var e in ids)
        {
            if (row >= RlSpec.MaxEntities) break;
            var vis = range.GetLosVisibility(e, agentPlayerId);
            if (!privileged && vis == LosVisibility.Hidden) continue;
            if (!FillEntityRow(cm, catalog, agentPlayerId, e, vis, dest, row)) continue;
            rowOf[e.Value] = row;
            row++;
        }

        FillSpatial(cm, range, agentPlayerId, dest, pathfinder, territory);
        FillScalars(cm, agentPlayerId, privileged, turn, dest);
        FillFunctionMask(cm, dest, rowOf, agentPlayerId);
        dest.Turn = turn;
    }

    private static bool FillEntityRow(ComponentManager cm, RlCatalog catalog, int agentPlayerId,
        EntityId e, LosVisibility vis, RlObservation dest, int row)
    {
        var pos = cm.QueryInterface<PositionComponent>(e);
        if (pos == null || !pos.InWorld) return false;
        var ident = cm.QueryInterface<IdentityComponent>(e);
        var own = cm.QueryInterface<OwnershipComponent>(e);
        var hp = cm.QueryInterface<HealthComponent>(e);
        var ai = cm.QueryInterface<UnitAIComponent>(e);
        int owner = own?.PlayerId ?? 0;
        dest.SetEntity(row, RlSpec.Ent.Valid, 1);
        dest.SetEntity(row, RlSpec.Ent.OwnerRel, OwnerRel(cm, agentPlayerId, owner));
        dest.SetEntity(row, RlSpec.Ent.TemplateId, catalog.InternTemplate(ident?.TemplateName ?? ""));
        dest.SetEntity(row, RlSpec.Ent.CellX, WorldToCell(pos.Position.X, rangeWorld(cm)));
        dest.SetEntity(row, RlSpec.Ent.CellZ, WorldToCell(pos.Position.Z, rangeWorld(cm)));
        int hpCur = hp?.Current ?? 0;
        int hpMax = hp?.Max ?? 0;
        int unitAi = catalog.InternUnitAi(ai?.FsmStateName ?? "");
        int posX = pos.Position.X.InternalValue;
        int posZ = pos.Position.Z.InternalValue;
        if (vis == LosVisibility.Fogged)
        {
            var mirage = cm.QueryInterface<MirageComponent>(e);
            if (mirage != null)
            {
                hpCur = mirage.FrozenHealthCurrent;
                hpMax = mirage.FrozenHealthMax;
                unitAi = 0;
            }
            else
            {
                hpCur = 0;
                hpMax = 0;
                unitAi = 0;
                posX = 0;
                posZ = 0;
            }
        }
        dest.SetEntity(row, RlSpec.Ent.Hp, hpCur);
        dest.SetEntity(row, RlSpec.Ent.HpMax, hpMax);
        dest.SetEntity(row, RlSpec.Ent.UnitAi, unitAi);
        int flags = 0;
        if (ident?.IsBuilding == true) flags |= RlSpec.Ent.FlagBuilding;
        if (cm.QueryInterface<AttackComponent>(e) != null) flags |= RlSpec.Ent.FlagCanAttack;
        if (cm.QueryInterface<ResourceGatherer>(e) != null) flags |= RlSpec.Ent.FlagCanGather;
        if (cm.QueryInterface<FoundationComponent>(e) != null) flags |= RlSpec.Ent.FlagFoundation;
        dest.SetEntity(row, RlSpec.Ent.Flags, flags);
        dest.SetEntity(row, RlSpec.Ent.Visibility, (int)vis);
        dest.SetEntity(row, RlSpec.Ent.EntityId, (int)e.Value);
        dest.SetEntity(row, RlSpec.Ent.PosXInternal, posX);
        dest.SetEntity(row, RlSpec.Ent.PosZInternal, posZ);
        return true;
    }

    private static int rangeWorld(ComponentManager cm)
    {
        var rm = cm.Range;
        if (rm == null) return RlSpec.WorldMetersDefault;
        return Math.Max(1, rm.Los.VerticesPerSide - 1) * LosGrid.TileSize;
    }

    private static int WorldToCell(Fixed world, int worldMeters)
    {
        int span = Math.Max(1, worldMeters);
        int cell = (world.ToIntRoundToNearest() * RlSpec.SpatialSize) / span;
        if (cell < 0) return 0;
        if (cell >= RlSpec.SpatialSize) return RlSpec.SpatialSize - 1;
        return cell;
    }

    private static int OwnerRel(ComponentManager cm, int agent, int owner)
    {
        if (owner <= 0) return RlSpec.OwnerRel.Neutral;
        if (owner == agent) return RlSpec.OwnerRel.Self;
        if (cm.Players.IsEnemy(agent, owner)) return RlSpec.OwnerRel.Enemy;
        var selfDip = cm.QueryInterface<DiplomacyComponent>(cm.GetPlayerEntityId(agent) ?? default);
        if (selfDip != null && selfDip.IsAlly(owner)) return RlSpec.OwnerRel.Ally;
        return RlSpec.OwnerRel.Neutral;
    }

    private static void FillSpatial(ComponentManager cm, RangeManager range, int agent,
        RlObservation dest, PathfinderComponent? pathfinder, TerritoryManager? territory)
    {
        int n = RlSpec.SpatialSize;
        int world = rangeWorld(cm);
        var los = range.Los;
        ushort landMask = pathfinder != null ? pathfinder.DefaultClass.Mask.Mask : (ushort)1;
        var grid = pathfinder?.PassabilityGrid;
        for (int cz = 0; cz < n; cz++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                int wx = (cx * world) / n;
                int wz = (cz * world) / n;
                var wv = Fixed.FromInt(wx);
                var zv = Fixed.FromInt(wz);
                var vis = range.GetLosVisibilityPosition(wv, zv, agent);
                int visCh = vis == LosVisibility.Visible ? 2 : vis == LosVisibility.Fogged ? 1 : 0;
                int explored = los.IsExplored(agent, los.WorldToVertex(wv, zv).i,
                    los.WorldToVertex(wv, zv).j) ? 1 : 0;
                int passable = 1;
                if (grid != null)
                {
                    int gi = Math.Clamp((wx * grid.W) / Math.Max(1, world), 0, grid.W - 1);
                    int gj = Math.Clamp((wz * grid.H) / Math.Max(1, world), 0, grid.H - 1);
                    passable = (grid.Get(gi, gj).Value & landMask) == 0 ? 1 : 0;
                }
                int terr = territory != null ? territory.GetOwner(wv, zv) : 0;
                int baseIdx = (cz * n + cx);
                dest.Spatial[RlSpec.Spat.Visibility * n * n + baseIdx] = visCh;
                dest.Spatial[RlSpec.Spat.Explored * n * n + baseIdx] = explored;
                dest.Spatial[RlSpec.Spat.Passable * n * n + baseIdx] = passable;
                dest.Spatial[RlSpec.Spat.Territory * n * n + baseIdx] = terr;
            }
        }

        foreach (var e in cm.AllEntities)
        {
            var supply = cm.QueryInterface<ResourceSupply>(e);
            if (supply == null || supply.Amount <= 0) continue;
            var pos = cm.QueryInterface<PositionComponent>(e);
            if (pos == null || !pos.InWorld) continue;
            int cx = WorldToCell(pos.Position.X, world);
            int cz = WorldToCell(pos.Position.Z, world);
            dest.Spatial[RlSpec.Spat.Resource * n * n + cz * n + cx] += supply.Amount;
        }
    }

    private static void FillScalars(ComponentManager cm, int agent, bool privileged, uint turn,
        RlObservation dest)
    {
        var p = cm.GetPlayerEntity(agent);
        dest.Scalars[RlSpec.Scal.Wood] = p?.Wood ?? 0;
        dest.Scalars[RlSpec.Scal.Food] = p?.Food ?? 0;
        dest.Scalars[RlSpec.Scal.Stone] = p?.Stone ?? 0;
        dest.Scalars[RlSpec.Scal.Metal] = p?.Metal ?? 0;
        dest.Scalars[RlSpec.Scal.Pop] = p?.PopUsed ?? 0;
        dest.Scalars[RlSpec.Scal.PopCap] = p?.PopulationLimit ?? 0;
        dest.Scalars[RlSpec.Scal.Turn] = (int)turn;
        dest.Scalars[RlSpec.Scal.Phase] = ReadPhase(cm, agent);
        dest.Scalars[RlSpec.Scal.Won] = p != null && p.HasWon() ? 1 : 0;
        dest.Scalars[RlSpec.Scal.Defeated] = p != null && p.IsDefeated() ? 1 : 0;
        dest.Scalars[RlSpec.Scal.Privileged] = privileged ? 1 : 0;
    }

    private static void FillFunctionMask(ComponentManager cm, RlObservation dest,
        Dictionary<uint, int> rowOf, int agentPlayerId)
    {
        dest.FunctionMask[(int)RlFunction.NoOp] = 1;
        foreach (var e in cm.AllEntities)
        {
            var own = cm.QueryInterface<OwnershipComponent>(e);
            if (own == null) continue;
            if (!rowOf.TryGetValue(e.Value, out int row)) continue;
            bool isOwn = own.PlayerId == agentPlayerId;
            bool playerOwned = own.PlayerId > 0;

            Allow(dest, row, RlFunction.NoOp, isOwn);
            if (playerOwned)
            {
                Allow(dest, row, RlFunction.Delete, isOwn);
                Allow(dest, row, RlFunction.SpyRequest, isOwn);
                Allow(dest, row, RlFunction.Tribute, isOwn);
                Allow(dest, row, RlFunction.Barter, isOwn);
                Allow(dest, row, RlFunction.AttackRequest, isOwn);
                Allow(dest, row, RlFunction.SetTradingGoods, isOwn);
            }

            if (cm.QueryInterface<UnitAIComponent>(e) != null)
            {
                Allow(dest, row, RlFunction.Stop, isOwn);
                Allow(dest, row, RlFunction.Move, isOwn);
                Allow(dest, row, RlFunction.Patrol, isOwn);
                Allow(dest, row, RlFunction.AttackWalk, isOwn);
                Allow(dest, row, RlFunction.Guard, isOwn);
                Allow(dest, row, RlFunction.Garrison, isOwn);
                Allow(dest, row, RlFunction.Stance, isOwn);
                Allow(dest, row, RlFunction.Formation, isOwn);
                Allow(dest, row, RlFunction.CollectTreasure, isOwn);
            }
            if (cm.QueryInterface<AttackComponent>(e) != null)
            {
                Allow(dest, row, RlFunction.Attack, isOwn);
                Allow(dest, row, RlFunction.WalkToRange, isOwn);
            }
            if (cm.QueryInterface<ResourceGatherer>(e) != null)
            {
                Allow(dest, row, RlFunction.Gather, isOwn);
                Allow(dest, row, RlFunction.ReturnResource, isOwn);
            }
            if (cm.QueryInterface<BuilderComponent>(e) != null)
            {
                Allow(dest, row, RlFunction.Repair, isOwn);
                Allow(dest, row, RlFunction.Build, isOwn);
            }
            if (cm.QueryInterface<ProductionQueue>(e) != null)
            {
                Allow(dest, row, RlFunction.Train, isOwn);
                Allow(dest, row, RlFunction.Research, isOwn);
                Allow(dest, row, RlFunction.CancelProduction, isOwn);
            }
            var holder = cm.QueryInterface<GarrisonHolderComponent>(e);
            if (holder != null && holder.Entities.Count > 0)
                Allow(dest, row, RlFunction.Ungarrison, isOwn);
            if (cm.QueryInterface<RallyPointComponent>(e) != null)
                Allow(dest, row, RlFunction.Rally, isOwn);
            if (cm.QueryInterface<PackComponent>(e) != null)
                Allow(dest, row, RlFunction.Pack, isOwn);
            if (cm.QueryInterface<GateComponent>(e) != null)
                Allow(dest, row, RlFunction.Gate, isOwn);
            if (cm.QueryInterface<UpgradeComponent>(e) != null)
                Allow(dest, row, RlFunction.Upgrade, isOwn);
            if (cm.QueryInterface<BuildingAIComponent>(e) != null)
                Allow(dest, row, RlFunction.FocusFire, isOwn);
            if (cm.QueryInterface<TraderComponent>(e) != null)
                Allow(dest, row, RlFunction.SetupTradeRoute, isOwn);
        }
    }

    private static void Allow(RlObservation dest, int row, RlFunction fn, bool contributeGlobal)
    {
        int i = (int)fn;
        dest.EntityMask[row] |= 1u << i;
        if (contributeGlobal)
            dest.FunctionMask[i] = 1;
    }

    private static int ReadPhase(ComponentManager cm, int agent)
    {
        var pe = cm.GetPlayerEntityId(agent);
        if (pe == null) return 0;
        var tm = cm.QueryInterface<TechnologyManager>(pe.Value);
        if (tm == null) return 0;
        if (tm.IsResearched("phase_city") || tm.IsResearched("phase_city_generic")) return 2;
        if (tm.IsResearched("phase_town") || tm.IsResearched("phase_town_generic")) return 1;
        return 0;
    }
}
