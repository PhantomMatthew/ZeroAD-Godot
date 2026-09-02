using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>运输计划（原版 petra/transportPlan.js，753 行）功能端口。
/// 跨海运兵:登船(岸点会合 + Garrison)→ 航行(船驶目标岸线)→ 卸载
/// (Ungarrison,单位落岸)→ 完成。元数据契约(原版同款):
///   单位: transport=计划ID / endPos=目的点 / onBoard=船id 或 "onBoard"(已入舱)
///   船:   transporter=计划ID
/// 简化:单船多趟(原版的 flotilla 多船并行); boardingPos 用岸线扫描
/// (原版 landingZones 海陆交叉表——我们用 Accessibility.TryFindShoreline 近似);
/// 卡死重试/被困销毁(nTry)保留轻量版(3 次失败 → 单位放弃运输)。</summary>
public sealed class TransportPlan
{
    public enum TransportState { Boarding, Sailing, Completed, Failed, Canceled }

    public readonly int ID;
    public readonly ushort StartIndex;   // 出发陆区
    public readonly ushort EndIndex;     // 目标陆区
    public readonly ushort Sea;          // 途经海域(0 = 无——不应发生,构造时已验)
    public readonly FixedVector2D EndPos;
    public TransportState State { get; private set; } = TransportState.Boarding;

    /// <summary>待运单位 / 分配到的运输船(entity id)。</summary>
    public readonly List<uint> Units = new();
    public readonly List<uint> Ships = new();
    /// <summary>船 → 登船点(岸线上船位)。</summary>
    private readonly Dictionary<uint, FixedVector2D> _boardingPos = new();
    /// <summary>单位 → 登船尝试次数(卡死检测)。</summary>
    private readonly Dictionary<uint, int> _tryCount = new();
    /// <summary>单位 → 上次位置(卡死检测)。</summary>
    private readonly Dictionary<uint, FixedVector2D> _lastPos = new();
    private double _stateSince;

    public TransportPlan(int id, ushort startIndex, ushort endIndex, ushort sea, FixedVector2D endPos)
    {
        ID = id; StartIndex = startIndex; EndIndex = endIndex; Sea = sea; EndPos = endPos;
    }

    /// <summary>单位加入(原版 addUnit):设元数据 + 若已在航行先就近落岸再登船——
    /// 简化:只在 Boarding 态接受。</summary>
    public bool AddUnit(GameState gameState, uint unitId)
    {
        if (State != TransportState.Boarding || Units.Contains(unitId)) return false;
        var ent = gameState.GetEntityById(unitId);
        if (ent == null || ent.Position2D == default) return false;
        Units.Add(unitId);
        gameState.Metadata.Set(unitId, "transport", ID);
        gameState.Metadata.Set(unitId, "endPosX", EndPos.X.ToFloat());
        gameState.Metadata.Set(unitId, "endPosZ", EndPos.Y.ToFloat());
        return true;
    }

    /// <summary>船分配(原版 assignShip):自由运输船(有空舱位)入列。</summary>
    public bool AssignShip(GameState gameState, uint shipId)
    {
        if (Ships.Contains(shipId)) return false;
        var ship = gameState.GetEntityById(shipId);
        if (ship == null) return false;
        Ships.Add(shipId);
        gameState.Metadata.Set(shipId, "transporter", ID);
        return true;
    }

    /// <summary>空舱位数(原版 countFreeSlots)。</summary>
    public int CountFreeSlots(GameState gameState)
    {
        int slots = 0;
        foreach (var shipId in Ships)
        {
            var ship = gameState.GetEntityById(shipId);
            if (ship == null) continue;
            var holder = gameState.Cm.QueryInterface<Components.GarrisonHolderComponent>(ship.Entity);
            if (holder != null)
                slots += holder.Max - holder.Entities.Count;
        }
        return slots;
    }

    /// <summary>全部单位是否已在某船舱内(无位置 = 已入舱,原版 isOnBoard 语义)。</summary>
    private bool AllOnBoard(GameState gameState)
    {
        foreach (var id in Units)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null) continue;   // 死了当已处理
            if (ent.Position2D != default) return false;   // 有位置 = 还在岸上
        }
        return true;
    }

    /// <summary>主推进(原版 update:Boarding→Sailing;返回剩余单位数,0 = 收尾)。</summary>
    public void Update(GameState gameState)
    {
        double now = gameState.ElapsedTime;
        switch (State)
        {
            case TransportState.Boarding:
                OnBoarding(gameState, now);
                break;
            case TransportState.Sailing:
                OnSailing(gameState, now);
                break;
        }
        // 全灭 → 失败收尾(原版 units.length==0 → 清理)。
        Units.RemoveAll(id =>
        {
            var e = gameState.GetEntityById(id);
            return e == null || e.IsDead;
        });
        Ships.RemoveAll(id =>
        {
            var e = gameState.GetEntityById(id);
            return e == null || e.IsDead;
        });
        if (Units.Count == 0 && State != TransportState.Completed)
            State = TransportState.Failed;
        if (State is TransportState.Completed or TransportState.Failed or TransportState.Canceled)
            ReleaseAll(gameState);
    }

    private void OnBoarding(GameState gameState, double now)
    {
        if (Ships.Count == 0) return;   // 等 navalManager 分船(原版 needTransportShips)

        var acc = gameState.Accessibility;
        foreach (var shipId in Ships)
        {
            var ship = gameState.GetEntityById(shipId);
            if (ship == null) continue;
            // 船先到登船岸点(原版:ship.move(boardingPos))。
            if (!_boardingPos.ContainsKey(shipId) && acc != null)
            {
                var pivot = Units
                    .Select(id => gameState.GetEntityById(id))
                    .FirstOrDefault(e => e != null && e.Position2D != default);
                float px = pivot?.Position2D.X.ToFloat() ?? ship.Position2D.X.ToFloat();
                float pz = pivot?.Position2D.Y.ToFloat() ?? ship.Position2D.Y.ToFloat();
                if (acc.TryFindShoreline(px, pz, out float sx, out float sz))
                    _boardingPos[shipId] = new FixedVector2D(
                        Fixed.FromFloat(sx), Fixed.FromFloat(sz));
                else
                    _boardingPos[shipId] = ship.Position2D;
                var bp = _boardingPos[shipId];
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                    (uint)gameState.PlayerId, shipId, bp.X, bp.Y));
            }
        }

        // 单位登船:有空位的船 → Garrison 命令(原版 ent.garrison(ship))。
        foreach (var unitId in Units.ToList())
        {
            var ent = gameState.GetEntityById(unitId);
            if (ent == null) continue;
            if (ent.Position2D == default) continue;   // 已入舱
            var shipId = ShipWithFreeSlot(gameState);
            if (shipId == null) continue;   // 全满——等船往返或分新船
            gameState.Metadata.Set(unitId, "onBoard", (int)shipId.Value);
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Garrison(
                (uint)gameState.PlayerId, unitId, shipId.Value));

            // 卡死检测(原版 nTry 轻量版:2s 未挪窝 → 重试;>3 次 → 放弃该单位)。
            if (_lastPos.TryGetValue(unitId, out var last) && last == ent.Position2D
                && _stateSince + 2 < now)
            {
                int tries = _tryCount.GetValueOrDefault(unitId) + 1;
                _tryCount[unitId] = tries;
                if (tries > 3)
                {
                    ResetUnit(gameState, unitId);
                    Units.Remove(unitId);
                    continue;
                }
                // 重试:先挪向登船点再登。
                if (_boardingPos.TryGetValue(shipId.Value, out var bp))
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                        (uint)gameState.PlayerId, unitId, bp.X, bp.Y));
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Garrison(
                    (uint)gameState.PlayerId, unitId, shipId.Value));
            }
            _lastPos[unitId] = ent.Position2D;
        }

        if (!AllOnBoard(gameState)) return;

        // 全员入舱 → 航行:船驶目标岸点(原版:boardingPos 换成 endIndex 岸线,
        // avoidEnemy 领土惩罚——我们取 EndPos 最近岸线)。
        _stateSince = now;
        State = TransportState.Sailing;
        foreach (var shipId in Ships)
        {
            var ship = gameState.GetEntityById(shipId);
            if (ship == null) continue;
            FixedVector2D dest = EndPos;
            if (acc != null && acc.TryFindShoreline(EndPos.X.ToFloat(), EndPos.Y.ToFloat(),
                    out float sx, out float sz))
                dest = new FixedVector2D(Fixed.FromFloat(sx), Fixed.FromFloat(sz));
            _boardingPos[shipId] = dest;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                (uint)gameState.PlayerId, shipId, dest.X, dest.Y));
        }
    }

    private void OnSailing(GameState gameState, double now)
    {
        // 船抵目标岸点(距岸点 < 20m)且停稳即卸载(原版:到位后 unboard)。
        bool anyShipUnderway = false;
        foreach (var shipId in Ships)
        {
            var ship = gameState.GetEntityById(shipId);
            if (ship == null || ship.Position2D == default) continue;
            var dest = _boardingPos.GetValueOrDefault(shipId);
            float dx = ship.Position2D.X.ToFloat() - dest.X.ToFloat();
            float dz = ship.Position2D.Y.ToFloat() - dest.Y.ToFloat();
            if (dx * dx + dz * dz > 20f * 20f)
            {
                anyShipUnderway = true;
                continue;
            }
            // 到位 → 卸载全部舱内单位(原版 unboardAll;Ungarrison unitId=-1 = 全部)。
            var holder = gameState.Cm.QueryInterface<Components.GarrisonHolderComponent>(ship.Entity);
            if (holder != null && holder.Entities.Count > 0)
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Ungarrison(
                    (uint)gameState.PlayerId, shipId));
        }
        if (anyShipUnderway) return;

        // 卸载完成判定:所有存活单位都有位置(落岸)。
        bool allOut = true;
        foreach (var unitId in Units)
        {
            var ent = gameState.GetEntityById(unitId);
            if (ent == null) continue;
            if (ent.Position2D == default) { allOut = false; break; }
        }
        if (!allOut) return;

        // 完成:单位向目的点推进(原版:落岸后各自 move 到 endPos)。
        foreach (var unitId in Units)
        {
            var ent = gameState.GetEntityById(unitId);
            if (ent == null) continue;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                (uint)gameState.PlayerId, unitId, EndPos.X, EndPos.Y));
        }
        State = TransportState.Completed;
    }

    /// <summary>有空舱位的船(原版 assignUnitToShip 的选船)。</summary>
    private uint? ShipWithFreeSlot(GameState gameState)
    {
        foreach (var shipId in Ships)
        {
            var ship = gameState.GetEntityById(shipId);
            if (ship == null) continue;
            var holder = gameState.Cm.QueryInterface<Components.GarrisonHolderComponent>(ship.Entity);
            if (holder != null && holder.Entities.Count < holder.Max)
                return shipId;
        }
        return null;
    }

    /// <summary>单位元数据清理(原版 resetUnit)。</summary>
    private void ResetUnit(GameState gameState, uint unitId)
    {
        gameState.Metadata.Remove(unitId, "transport");
        gameState.Metadata.Remove(unitId, "onBoard");
        gameState.Metadata.Remove(unitId, "endPosX");
        gameState.Metadata.Remove(unitId, "endPosZ");
    }

    /// <summary>收尾释放(原版 releaseAll):单位/船元数据全清。</summary>
    private void ReleaseAll(GameState gameState)
    {
        foreach (var id in Units) ResetUnit(gameState, id);
        foreach (var id in Ships)
            gameState.Metadata.Remove(id, "transporter");
        Units.Clear();
        Ships.Clear();
    }
}
