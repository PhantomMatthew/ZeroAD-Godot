using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>防御军（原版 petra/defenseArmy.js，657 行）全量移植。
/// 一军 = 一队敌方威胁(foe)+ 一队我方防守者(own);类型:
/// "default" 反入侵;"capturing" 夺还(gaia 建筑/被占建筑的占领点,单 foe 不合并)。
/// 核心:foe 紧凑性(compactSize 内才并入)+ breakaway 脱离(超 breakawaySize 裂出新军)
/// + 强度簿记(evaluateStrength 增量/全量)+ needsDefenders(领土主权决定防守比)
/// + assignUnit(被攻击者优先 → 少堆叠 → 最近;8/5 堆叠上限)+ merge。</summary>
public sealed class DefenseArmy
{
    public const string TypeDefault = "default";
    public const string TypeCapturing = "capturing";

    public readonly int ID;
    public readonly string Type;
    private readonly PetraConfig _config;

    /// <summary>敌军质心(原版 foePosition;recalculatePosition 维护)。</summary>
    public FixedVector2D FoePosition;
    private double _positionLastUpdate = -1;

    /// <summary>防守者 → 目标(assignedTo)/ 目标 → 防守者列表(assignedAgainst)。</summary>
    internal readonly Dictionary<uint, uint> _assignedTo = new();
    internal readonly Dictionary<uint, List<uint>> _assignedAgainst = new();

    public readonly List<uint> FoeEntities = new();
    public readonly List<uint> OwnEntities = new();
    public double FoeStrength;
    public double OwnStrength;

    private readonly int _compactSize;
    private readonly int _breakawaySize;

    public DefenseArmy(GameState gameState, IEnumerable<uint> foeEntities, string type,
        int id, PetraConfig config)
    {
        ID = id;
        Type = type ?? TypeDefault;
        _config = config;
        _compactSize = config.Defense.ArmyCompactSize;
        _breakawaySize = config.Defense.ArmyBreakawaySize;
        foreach (var foe in foeEntities)
            AddFoe(gameState, foe, force: true);
        RecalculatePosition(gameState, force: true);
    }

    /// <summary>敌军入列(原版 addFoe):去重 + 紧凑性门(force 豁免)+ 强度记账 +
    /// PartOfArmy 元数据(attackPlan/defenseManager 依此排除已编军单位)。</summary>
    public bool AddFoe(GameState gameState, uint enemyId, bool force = false)
    {
        if (FoeEntities.Contains(enemyId)) return false;
        var ent = gameState.GetEntityById(enemyId);
        if (ent == null || ent.Position2D == default) return false;
        if (!force && FoeEntities.Count > 0
            && SquareDist(ent.Position2D, FoePosition) > _compactSize)
            return false;
        FoeEntities.Add(enemyId);
        _assignedAgainst[enemyId] = new List<uint>();
        _positionLastUpdate = -1;
        EvaluateStrength(gameState, ent);
        gameState.Metadata.Set(enemyId, "PartOfArmy", ID);
        return true;
    }

    /// <summary>原版 removeFoe:清簿记 + 减强度 + 摘元数据。</summary>
    public bool RemoveFoe(GameState gameState, uint enemyId)
    {
        if (!FoeEntities.Remove(enemyId)) return false;
        _assignedAgainst.Remove(enemyId);
        foreach (var kv in _assignedTo.Where(kv => kv.Value == enemyId).ToList())
            _assignedTo.Remove(kv.Key);
        var ent = gameState.GetEntityById(enemyId);
        if (ent != null)
        {
            EvaluateStrength(gameState, ent, isOwn: false, remove: true);
            gameState.Metadata.Remove(ent.Id, "PartOfArmy");
        }
        return true;
    }

    /// <summary>防守者入列(原版 addOwn;尚未分派)。</summary>
    public bool AddOwn(GameState gameState, uint id, bool force = false)
    {
        if (OwnEntities.Contains(id)) return false;
        var ent = gameState.GetEntityById(id);
        if (ent == null || (!force && ent.Position2D == default)) return false;
        OwnEntities.Add(id);
        EvaluateStrength(gameState, ent, isOwn: true);
        return true;
    }

    /// <summary>原版 removeOwn:减强度 + 摘簿记(订单保留——单位自行收工)。</summary>
    public bool RemoveOwn(GameState gameState, uint id)
    {
        if (!OwnEntities.Remove(id)) return false;
        _assignedTo.Remove(id);
        foreach (var list in _assignedAgainst.Values)
            list.Remove(id);
        var ent = gameState.GetEntityById(id);
        if (ent != null)
            EvaluateStrength(gameState, ent, isOwn: true, remove: true);
        return true;
    }

    /// <summary>解散(原版 clear):全军摘簿记/元数据。</summary>
    public void Clear(GameState gameState)
    {
        foreach (var id in FoeEntities.ToList()) RemoveFoe(gameState, id);
        foreach (var id in OwnEntities.ToList()) RemoveOwn(gameState, id);
        FoeStrength = 0; OwnStrength = 0;
    }

    /// <summary>原版 getState:无 foe → 0(待解散)。</summary>
    public int GetState() => FoeEntities.Count == 0 ? 0 : 1;

    /// <summary>合并(原版 merge):簿记并表 + 双方成员 force 入列 + 重算位置/强度。</summary>
    public void Merge(GameState gameState, DefenseArmy other)
    {
        foreach (var kv in other._assignedAgainst)
            if (_assignedAgainst.TryGetValue(kv.Key, out var mine)) mine.AddRange(kv.Value);
            else _assignedAgainst[kv.Key] = new List<uint>(kv.Value);
        foreach (var kv in other._assignedTo)
            _assignedTo[kv.Key] = kv.Value;
        foreach (var id in other.FoeEntities.ToList()) AddFoe(gameState, id, force: true);
        foreach (var id in other.OwnEntities.ToList()) AddOwn(gameState, id, force: true);
        other.FoeEntities.Clear(); other.OwnEntities.Clear();
        RecalculatePosition(gameState, force: true);
        RecalculateStrengths(gameState);
    }

    /// <summary>原版 needsDefenders:敌强度 × 防守比(我方领土 own=2/盟友 ally=1.4
    /// 递减 per 额外盟友/中立 neutral=1.8)− 我方强度;≤0 = 不缺。</summary>
    public double NeedsDefenders(GameState gameState)
    {
        double defenseRatio;
        var territory = SimSystem.Territory;
        int territoryOwner = territory?.GetOwner(FoePosition.X, FoePosition.Y) ?? 0;
        if (territoryOwner == gameState.PlayerId)
            defenseRatio = _config.Defense.DefenseRatio.own;
        else if (territoryOwner != 0 && gameState.IsPlayerAlly(territoryOwner))
            defenseRatio = _config.Defense.DefenseRatio.ally;
        else
            defenseRatio = _config.Defense.DefenseRatio.neutral;

        if (FoeStrength <= 0 || OwnStrength <= 0)
            RecalculateStrengths(gameState);
        if (FoeStrength * defenseRatio <= OwnStrength)
            return -1;
        return FoeStrength * defenseRatio - OwnStrength;
    }

    /// <summary>原版 recalculatePosition:敌军质心(5s 节流由调用点判)。</summary>
    public void RecalculatePosition(GameState gameState, bool force = false)
    {
        if (!force && _positionLastUpdate == gameState.ElapsedTime) return;
        float sx = 0, sz = 0;
        int n = 0;
        foreach (var id in FoeEntities)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null || ent.Position2D == default) continue;
            sx += ent.Position2D.X.ToFloat();
            sz += ent.Position2D.Y.ToFloat();
            n++;
        }
        // n==0:军已全灭,保旧位(原版同款),下轮被清。
        if (n > 0)
            FoePosition = new FixedVector2D(
                Fixed.FromFloat(sx / n), Fixed.FromFloat(sz / n));
        _positionLastUpdate = gameState.ElapsedTime;
    }

    public void RecalculateStrengths(GameState gameState)
    {
        OwnStrength = 0; FoeStrength = 0;
        foreach (var id in FoeEntities)
            EvaluateStrength(gameState, gameState.GetEntityById(id));
        foreach (var id in OwnEntities)
            EvaluateStrength(gameState, gameState.GetEntityById(id), isOwn: true);
    }

    /// <summary>原版 evaluateStrength:建筑(敌:defaultArrow×6 或 4;我军夺回场景 2)、
    /// 单位 getMaxStrength;大象 ×3(原版:动物强度低估补偿)。remove 取负。</summary>
    private void EvaluateStrength(GameState gameState, AIEntity? ent, bool isOwn = false, bool remove = false)
    {
        if (ent == null) return;
        double entStrength;
        if (ent.IsStructure)
        {
            if (ent.Owner != gameState.PlayerId)
            {
                // 原版 getDefaultArrow(默认箭数)——我们的 BuildingAI 无该面,用 4 基线,
                // 有防御火力的按 6 估。
                entStrength = ent.HasDefensiveFire ? 6 : 4;
            }
            else
                entStrength = 2;   // 夺回占领点的小强度
        }
        else
            entStrength = Headquarters.GetMaxStrength(ent.Template, null);
        if (ent.HasClass("Animal") && ent.HasClass("Elephant"))
            entStrength *= 3;
        if (remove) entStrength = -entStrength;
        if (isOwn) OwnStrength += entStrength;
        else FoeStrength += entStrength;
    }

    /// <summary>原版 assignUnit:目标选择序——正攻击我者优先 → 堆叠 >2 降权/≥8(非英雄
    /// 非攻城 ≥5)跳过 → 最近;同陆才下令(异陆走运输——未移植,跳过)。载货先回送
    /// (原版 returnResources;简化:直接攻击,UnitAI 自行处理)。</summary>
    public bool AssignUnit(GameState gameState, uint entId)
    {
        var ent = gameState.GetEntityById(entId);
        if (ent == null || ent.Position2D == default) return false;

        uint idMin = 0, idMinAll = 0;
        float distMin = float.MaxValue, distMinAll = float.MaxValue;
        bool foundMin = false, foundAll = false;
        foreach (var id in FoeEntities)
        {
            var eEnt = gameState.GetEntityById(id);
            if (eEnt == null || eEnt.Position2D == default) continue;
            if (!ent.CanAttackTarget(eEnt)) continue;

            // 被攻击中 → 优先反打(原版:敌当前订单目标是我)。
            if (eEnt.IsUnit && eEnt.UnitAIOrderTarget.HasValue
                && eEnt.UnitAIOrderTarget.Value.Value == entId)
            { idMin = id; foundMin = true; break; }

            int assigned = _assignedAgainst.GetValueOrDefault(id)?.Count ?? 0;
            if (assigned > 8 || assigned > 5
                && !eEnt.HasClass("Hero") && !eEnt.HasClass("Siege"))
                continue;
            float dist = SquareDist(ent.Position2D, eEnt.Position2D);
            if (!foundAll || dist < distMinAll) { idMinAll = id; distMinAll = dist; foundAll = true; }
            if (assigned > 2) continue;
            if (!foundMin || dist < distMin) { idMin = id; distMin = dist; foundMin = true; }
        }

        uint idFoe;
        if (foundMin) idFoe = idMin;
        else if (foundAll) idFoe = idMinAll;
        else return false;

        var foeEnt = gameState.GetEntityById(idFoe)!;
        // 同陆校验(原版 access 一致才下攻击令;异陆 requireTransport——未移植跳过)。
        if (gameState.Accessibility != null && !ent.HasClass("Ship"))
        {
            ushort ownAccess = gameState.Accessibility.GetAccessValue(
                ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat());
            ushort foeAccess = gameState.Accessibility.GetAccessValue(
                foeEnt.Position2D.X.ToFloat(), foeEnt.Position2D.Y.ToFloat());
            if (ownAccess != foeAccess) return false;
        }
        _assignedTo[entId] = idFoe;
        (_assignedAgainst[idFoe] ??= new List<uint>()).Add(entId);
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Attack(
            (uint)gameState.PlayerId, entId, idFoe));
        return true;
    }

    /// <summary>事件维护(原版 checkEvents):Destroy → 双方除名;
    /// OwnershipChanged → 被占走的 foe 除名/被抢走的自己人除名。
    /// (原版 EntityRenamed 晋升换名/Garrison 驻军除名——我们的事件缓冲无这两类,跳过。)</summary>
    public void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            switch (ev.Type)
            {
                case AIEventType.Destroy:
                    RemoveOwn(gameState, ev.Entity);
                    RemoveFoe(gameState, ev.Entity);
                    break;
                case AIEventType.OwnershipChanged:
                    if (!gameState.IsPlayerEnemy(ev.IntParam2))
                        RemoveFoe(gameState, ev.Entity);
                    else if (ev.IntParam == gameState.PlayerId)
                        RemoveOwn(gameState, ev.Entity);
                    break;
            }
        }
    }

    /// <summary>原版 update:无订单防守者重分派;capturing 军查占领点(无敌占领点即撤 foe);
    /// default 军 5s 节流重算质心 + breakaway 脱离(超 breakawaySize 裂出)。返回脱离者 id。</summary>
    public List<uint> Update(GameState gameState)
    {
        foreach (var entId in OwnEntities.ToList())
        {
            var ent = gameState.GetEntityById(entId);
            if (ent == null) { RemoveOwn(gameState, entId); continue; }
            // 无当前订单且不在运输中 → 重分派。
            if (ent.UnitAIOrderType == null
                && gameState.Metadata.GetObject(entId, "transport") == null)
                AssignUnit(gameState, entId);
        }

        if (Type == TypeCapturing)
        {
            if (FoeEntities.Count > 0 && gameState.GetEntityById(FoeEntities[0]) != null)
            {
                // 原版:该建筑无敌方占领点残留 → 撤 foe(夺回完成)。
                var target = gameState.GetEntityById(FoeEntities[0])!;
                var cap = gameState.Cm.QueryInterface<Components.CapturableComponent>(target.Entity);
                bool enemyPointsRemain = false;
                if (cap != null)
                    for (int j = 0; j < cap.CapturePoints.Length; j++)
                        if (gameState.IsPlayerEnemy(j) && cap.CapturePoints[j] > Fixed.Zero)
                        { enemyPointsRemain = true; break; }
                if (!enemyPointsRemain)
                    RemoveFoe(gameState, FoeEntities[0]);
            }
            return new List<uint>();
        }

        var breakaways = new List<uint>();
        if (gameState.ElapsedTime - _positionLastUpdate > 5)
        {
            RecalculatePosition(gameState);
            for (int i = 0; i < FoeEntities.Count; i++)
            {
                var id = FoeEntities[i];
                var ent = gameState.GetEntityById(id);
                if (ent == null || ent.Position2D == default) continue;
                if (SquareDist(ent.Position2D, FoePosition) > _breakawaySize)
                {
                    breakaways.Add(id);
                    if (RemoveFoe(gameState, id)) i--;
                }
            }
            RecalculatePosition(gameState);
        }
        return breakaways;
    }

    private static float SquareDist(FixedVector2D a, FixedVector2D b)
    {
        float dx = a.X.ToFloat() - b.X.ToFloat();
        float dz = a.Y.ToFloat() - b.Y.ToFloat();
        return dx * dx + dz * dz;
    }

    // ── 序列化(原版 defenseArmy.js Serialize)──
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("id", ID);
        s.StringASCII("type", Type);
        s.NumberFixed("fx", FoePosition.X);
        s.NumberFixed("fz", FoePosition.Y);
        s.NumberFixed("foeStr", Fixed.FromFloat((float)FoeStrength));
        s.NumberFixed("ownStr", Fixed.FromFloat((float)OwnStrength));
        s.NumberI32("foes", FoeEntities.Count);
        foreach (var id in FoeEntities.OrderBy(i => i)) s.NumberU32("f", id);
        s.NumberI32("own", OwnEntities.Count);
        foreach (var id in OwnEntities.OrderBy(i => i)) s.NumberU32("o", id);
        s.NumberI32("assignedTo", _assignedTo.Count);
        foreach (var kv in _assignedTo.OrderBy(kv => kv.Key))
        {
            s.NumberU32("unit", kv.Key);
            s.NumberU32("foe", kv.Value);
        }
    }

    public static DefenseArmy Deserialize(Serialization.IDeserializer d, GameState gameState,
        PetraConfig config)
    {
        int id = d.NumberI32("id");
        string type = d.StringASCII("type");
        var army = new DefenseArmy(gameState, System.Array.Empty<uint>(), type, id, config);
        army.FoePosition = new FixedVector2D(d.NumberFixed("fx"), d.NumberFixed("fz"));
        army.FoeStrength = d.NumberFixed("foeStr").ToFloat();
        army.OwnStrength = d.NumberFixed("ownStr").ToFloat();
        int foes = d.NumberI32("foes");
        for (int i = 0; i < foes; i++)
        {
            uint f = d.NumberU32("f");
            army.FoeEntities.Add(f);
            gameState.Metadata.Set(f, "PartOfArmy", id);
        }
        int own = d.NumberI32("own");
        for (int i = 0; i < own; i++) army.OwnEntities.Add(d.NumberU32("o"));
        int assigned = d.NumberI32("assignedTo");
        for (int i = 0; i < assigned; i++)
        {
            uint unit = d.NumberU32("unit");
            uint foe = d.NumberU32("foe");
            army._assignedTo[unit] = foe;
            if (!army._assignedAgainst.TryGetValue(foe, out var list))
                army._assignedAgainst[foe] = list = new List<uint>();
            list.Add(unit);
        }
        return army;
    }
}
