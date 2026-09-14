using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.RL;

public static class ActionTranslator
{
    public static bool TryTranslate(RlObservation obs, RlAction action, uint player,
        int worldMeters, RlCatalog catalog, out NetCommand command)
    {
        command = default;
        if (action.Function == RlFunction.NoOp) return false;
        if (action.FunctionMaskBlocked(obs)) return false;

        uint selected = EntityAt(obs, action.SelectedIndex);
        if (selected == 0 && action.Function != RlFunction.NoOp) return false;

        uint target = EntityAt(obs, action.TargetEntityIndex);
        var (wx, wz) = CellToWorld(action.TargetCellX, action.TargetCellZ, worldMeters);

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
            default:
                return false;
        }
    }

    /// <summary>When cells are negative (packed opponent actions omit them), copy
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
            cx, cz, action.CatalogId);
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
}

internal static class RlActionMask
{
    public static bool FunctionMaskBlocked(this RlAction action, RlObservation obs)
    {
        int i = (int)action.Function;
        if (i < 0 || i >= obs.FunctionMask.Length) return true;
        return obs.FunctionMask[i] == 0 && action.Function != RlFunction.NoOp;
    }
}
