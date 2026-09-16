using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.RL;

public static class ActionTranslator
{
    private static readonly string[] StanceNames =
        { "violent", "aggressive", "defensive", "passive", "standground" };
    private static readonly string[] FormationShapes =
        { "box", "column", "line", "scatter" };

    public static bool FansOut(RlFunction fn) => fn switch
    {
        RlFunction.Stop or RlFunction.Move or RlFunction.Attack or RlFunction.Gather
            or RlFunction.Repair or RlFunction.Garrison or RlFunction.Patrol
            or RlFunction.AttackWalk or RlFunction.ReturnResource or RlFunction.Guard
            or RlFunction.Delete or RlFunction.Stance or RlFunction.Pack
            or RlFunction.WalkToRange or RlFunction.CollectTreasure => true,
        _ => false
    };

    public static bool TryTranslate(RlObservation obs, RlAction action, uint player,
        int worldMeters, RlCatalog catalog, out NetCommand command)
    {
        command = default;
        if (action.Function == RlFunction.NoOp) return false;
        if (action.FunctionMaskBlocked(obs)) return false;

        uint selected = EntityAt(obs, action.SelectedIndex);
        if (selected == 0) return false;

        uint target = EntityAt(obs, action.TargetEntityIndex);
        var (wx, wz) = CellToWorld(action.TargetCellX, action.TargetCellZ, worldMeters);
        int other = player == 1 ? 2 : 1;
        int catalogPlayer = action.CatalogId >= 1 && action.CatalogId <= 8
            ? action.CatalogId : other;

        switch (action.Function)
        {
            case RlFunction.Stop:
                command = NetCommand.Stop(player, selected);
                return true;
            case RlFunction.Move:
                command = NetCommand.Move(player, selected, wx, wz);
                return true;
            case RlFunction.Attack:
                if (target == 0) return false;
                command = NetCommand.Attack(player, selected, target);
                return true;
            case RlFunction.Gather:
                if (target == 0) return false;
                command = NetCommand.Gather(player, selected, target);
                return true;
            case RlFunction.Repair:
                if (target == 0) return false;
                command = NetCommand.Repair(player, selected, target);
                return true;
            case RlFunction.Build:
                string buildTmpl = catalog.TemplateName(action.CatalogId);
                if (buildTmpl.Length == 0) return false;
                command = NetCommand.Build(player, selected, buildTmpl, wx, wz, Fixed.Zero);
                return true;
            case RlFunction.Train:
                string trainTmpl = catalog.TemplateName(action.CatalogId);
                if (trainTmpl.Length == 0) return false;
                command = NetCommand.Train(player, selected, trainTmpl);
                return true;
            case RlFunction.Research:
                string tech = catalog.TechName(action.CatalogId);
                if (tech.Length == 0) return false;
                command = NetCommand.Research(player, selected, tech);
                return true;
            case RlFunction.Garrison:
                if (target == 0) return false;
                command = NetCommand.Garrison(player, selected, target);
                return true;
            case RlFunction.Patrol:
                command = NetCommand.Patrol(player, selected, wx, wz);
                return true;
            case RlFunction.AttackWalk:
                command = NetCommand.AttackWalk(player, selected, wx, wz);
                return true;
            case RlFunction.ReturnResource:
                if (target == 0) return false;
                command = NetCommand.ReturnResource(player, selected, target);
                return true;
            case RlFunction.Ungarrison:
                command = NetCommand.Ungarrison(player, selected, target == 0 ? -1 : (int)target);
                return true;
            case RlFunction.Rally:
                if (target != 0)
                    command = NetCommand.SetRallyPoint(player, selected, target);
                else
                    command = NetCommand.SetRallyPointPosition(player, selected, wx, wz);
                return true;
            case RlFunction.Guard:
                if (target == 0) return false;
                command = NetCommand.Guard(player, selected, target);
                return true;
            case RlFunction.Delete:
                command = NetCommand.Delete(player, selected);
                return true;
            case RlFunction.Stance:
                command = NetCommand.SetUnitStance(player, selected, StanceName(action.CatalogId));
                return true;
            case RlFunction.CancelProduction:
                command = NetCommand.CancelProduction(player, selected, Math.Max(0, action.CatalogId));
                return true;
            case RlFunction.Pack:
                command = NetCommand.Pack(player, selected, action.CatalogId != 0);
                return true;
            case RlFunction.Upgrade:
                command = NetCommand.Upgrade(player, selected, target);
                return true;
            case RlFunction.Gate:
                command = NetCommand.Gate(player, selected, action.CatalogId != 0);
                return true;
            case RlFunction.CollectTreasure:
                if (target == 0) return false;
                command = NetCommand.CollectTreasureCmd(player, selected, target);
                return true;
            case RlFunction.WalkToRange:
                if (target == 0) return false;
                int maxRange = action.CatalogId > 0 ? action.CatalogId : 12;
                command = NetCommand.WalkToRange(player, selected, target, Fixed.Zero,
                    Fixed.FromInt(maxRange));
                return true;
            case RlFunction.FocusFire:
                if (target == 0) return false;
                command = NetCommand.FocusFire(player, selected, target);
                return true;
            case RlFunction.SpyRequest:
                command = NetCommand.SpyRequest(player, catalogPlayer);
                return true;
            case RlFunction.Formation:
                if (RlPacking.IsControlAssign(action.CatalogId, out _)
                    || RlPacking.IsControlRecall(action.CatalogId, out _))
                    return false;
                return TryFormation(obs, action, player, out command);
            case RlFunction.Tribute:
                command = NetCommand.Tribute(player, other, RlPacking.ResourceOf(action.CatalogId),
                    RlPacking.TributeAmount(action.CatalogId));
                return true;
            case RlFunction.Barter:
                RlPacking.BarterPair(action.CatalogId, out var sell, out var buy, out int barterAmt);
                command = NetCommand.Barter(player, sell, buy, barterAmt);
                return true;
            case RlFunction.SetupTradeRoute:
                if (target == 0) return false;
                command = NetCommand.SetupTradeRoute(player, selected, target);
                return true;
            case RlFunction.AttackRequest:
                command = NetCommand.AttackRequest(player, catalogPlayer);
                return true;
            case RlFunction.SetTradingGoods:
                RlPacking.TradingGoods(action.CatalogId, out int tw, out int tf, out int ts, out int tm);
                command = NetCommand.SetTradingGoods(player, tw, tf, ts, tm);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Wallset Build (piece chain) and extra Rally points. Returns true when
    /// <paramref name="dest"/> was filled and the caller should skip <see cref="TryTranslate"/>.</summary>
    public static bool TryExpand(RlObservation obs, RlAction action, uint player,
        int worldMeters, RlCatalog catalog, List<NetCommand> dest)
    {
        dest.Clear();
        if (action.Function == RlFunction.Build)
            return TryWallChain(obs, action, player, worldMeters, catalog, dest);
        if (action.Function == RlFunction.Rally)
            return TryMultiRally(obs, action, player, worldMeters, dest);
        return false;
    }

    private static bool TryWallChain(RlObservation obs, RlAction action, uint player,
        int worldMeters, RlCatalog catalog, List<NetCommand> dest)
    {
        uint selected = EntityAt(obs, action.SelectedIndex);
        if (selected == 0) return false;
        string setName = catalog.TemplateName(action.CatalogId);
        if (setName.Length == 0) return false;
        var stats = SimSystem.Sim?.Templates?.ExtractStats(setName);
        if (stats == null || !stats.IsWallSet) return false;

        float PieceLen(string tmpl, float fallback)
        {
            if (string.IsNullOrEmpty(tmpl)) return fallback;
            var ps = SimSystem.Sim?.Templates?.ExtractStats(tmpl);
            return ps != null && ps.WallPieceLength > 0f ? ps.WallPieceLength : fallback;
        }

        float sx = Fixed.Zero.WithInternalValue(
            obs.Entity(action.SelectedIndex, RlSpec.Ent.PosXInternal)).ToFloat();
        float sz = Fixed.Zero.WithInternalValue(
            obs.Entity(action.SelectedIndex, RlSpec.Ent.PosZInternal)).ToFloat();
        var (ex, ez) = CellToWorld(action.TargetCellX, action.TargetCellZ, worldMeters);
        var pieces = WallChain.Compute(
            stats.WallSetTower, stats.WallSetLong, stats.WallSetMedium, stats.WallSetShort,
            PieceLen(stats.WallSetTower, 8f),
            PieceLen(stats.WallSetLong, 12f),
            PieceLen(stats.WallSetMedium, 8f),
            PieceLen(stats.WallSetShort, 4f),
            stats.WallSetMinTowerOverlap, stats.WallSetMaxTowerOverlap,
            sx, sz, ex.ToFloat(), ez.ToFloat());
        if (pieces.Count == 0) return false;
        foreach (var p in pieces)
        {
            if (string.IsNullOrEmpty(p.Template)) continue;
            dest.Add(NetCommand.Build(player, selected, p.Template,
                Fixed.FromFloat(p.X), Fixed.FromFloat(p.Z), Fixed.FromFloat(p.Angle)));
        }
        return dest.Count > 0;
    }

    private static bool TryMultiRally(RlObservation obs, RlAction action, uint player,
        int worldMeters, List<NetCommand> dest)
    {
        uint selected = EntityAt(obs, action.SelectedIndex);
        if (selected == 0) return false;
        uint target = EntityAt(obs, action.TargetEntityIndex);
        var (wx, wz) = CellToWorld(action.TargetCellX, action.TargetCellZ, worldMeters);
        if (target != 0)
            dest.Add(NetCommand.SetRallyPoint(player, selected, target));
        else
            dest.Add(NetCommand.SetRallyPointPosition(player, selected, wx, wz));
        for (int i = 1; i < RlSpec.MaxSelected; i++)
        {
            uint extra = EntityAt(obs, action.SelectedAt(i));
            if (extra == 0) continue;
            dest.Add(NetCommand.SetRallyPointFull(player, selected, extra,
                Fixed.Zero, Fixed.Zero, "walk", append: true));
        }
        return dest.Count > 0;
    }

    /// <summary>When cells are negative (legacy packed opponent actions omit them), copy
    /// the target entity's observation cell, else the selected unit's, else the map centre.</summary>
    public static RlAction WithMapCells(RlObservation obs, RlAction action)
    {
        if (action.TargetCellX >= 0 && action.TargetCellZ >= 0) return action;
        int row = action.TargetEntityIndex;
        if (row < 0 || row >= RlSpec.MaxEntities || obs.Entity(row, RlSpec.Ent.Valid) == 0)
            row = action.SelectedIndex;
        int cx = RlSpec.SpatialSize / 2;
        int cz = cx;
        if (row >= 0 && row < RlSpec.MaxEntities && obs.Entity(row, RlSpec.Ent.Valid) != 0)
        {
            cx = obs.Entity(row, RlSpec.Ent.CellX);
            cz = obs.Entity(row, RlSpec.Ent.CellZ);
        }
        return new RlAction(action.Function, action.SelectedIndex, action.TargetEntityIndex,
            cx, cz, action.CatalogId,
            action.Selected1, action.Selected2, action.Selected3, action.Selected4,
            action.Selected5, action.Selected6, action.Selected7);
    }

    public static uint EntityAt(RlObservation obs, int row)
    {
        if (row < 0 || row >= RlSpec.MaxEntities) return 0;
        if (obs.Entity(row, RlSpec.Ent.Valid) == 0) return 0;
        int id = obs.Entity(row, RlSpec.Ent.EntityId);
        return id <= 0 ? 0 : (uint)id;
    }

    public static (Fixed x, Fixed z) CellToWorld(int cellX, int cellZ, int worldMeters)
    {
        int span = Math.Max(1, worldMeters);
        int cx = cellX; int cz = cellZ;
        if (cx < 0) cx = 0; if (cx >= RlSpec.SpatialSize) cx = RlSpec.SpatialSize - 1;
        if (cz < 0) cz = 0; if (cz >= RlSpec.SpatialSize) cz = RlSpec.SpatialSize - 1;
        int wx = ((cx * 2 + 1) * span) / (RlSpec.SpatialSize * 2);
        int wz = ((cz * 2 + 1) * span) / (RlSpec.SpatialSize * 2);
        return (Fixed.FromInt(wx), Fixed.FromInt(wz));
    }

    private static bool TryFormation(RlObservation obs, RlAction action, uint player,
        out NetCommand command)
    {
        var members = new List<uint>(RlSpec.MaxSelected);
        for (int i = 0; i < RlSpec.MaxSelected; i++)
        {
            uint id = EntityAt(obs, action.SelectedAt(i));
            if (id != 0) members.Add(id);
        }
        if (members.Count == 0)
        {
            command = default;
            return false;
        }
        string shape = FormationShapes[Math.Clamp(action.CatalogId, 0, FormationShapes.Length - 1)];
        command = NetCommand.FormationCmd(player, shape, members);
        return true;
    }

    private static string StanceName(int catalogId) =>
        StanceNames[Math.Clamp(catalogId, 0, StanceNames.Length - 1)];
}

internal static class RlActionMask
{
    public static bool FunctionMaskBlocked(this RlAction action, RlObservation obs)
    {
        int i = (int)action.Function;
        if (i < 0 || i >= obs.FunctionMask.Length) return true;
        if (obs.FunctionMask[i] == 0 && action.Function != RlFunction.NoOp) return true;
        int row = action.SelectedIndex;
        if (row >= 0 && row < obs.EntityMask.Length && obs.EntityMask[row] != 0)
            return (obs.EntityMask[row] & (1u << i)) == 0;
        return false;
    }
}
