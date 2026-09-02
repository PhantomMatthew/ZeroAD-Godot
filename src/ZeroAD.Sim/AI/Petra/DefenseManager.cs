using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>防御管理器（原版 petra/defenseManager.js，988 行）全量移植。
/// 军队模型(替代旧的"就近拦截"):
///   isDangerous 威胁判定(领土/修理地基侦察/射程距/CC 协同门/盟友领土援助) →
///   makeIntoArmy 编军(default 并入紧凑军,capturing 独立) →
///   checkEnemyArmies(军更新/脱离者裂军/空散军转攻/邻近军合并/5 回合领土安危重评) →
///   assignDefenders(两趟分派:先同陆后任意;强度按需递减;后备 12 人留守;
///   兵力不足 trainEmergencyUnits 应急训练)。
/// 威胁清单(targetList:敌方地基/攻城目标)保留——attackPlan 的 Raid 目标源。</summary>
public sealed class DefenseManager
{
    private readonly PetraConfig _config;

    /// <summary>防御军列表(原版 armies)。</summary>
    public readonly List<DefenseArmy> Armies = new();
    internal int _nextArmyId = 1;

    /// <summary>敌方进攻目标(地基/在建 CC)id(原版 targetList;Raid 目标源)。</summary>
    public readonly List<uint> TargetList = new();
    /// <summary>敌→(盟友→进攻单位数)(原版 attackingUnits,逐玩家轮次清)。</summary>
    private readonly Dictionary<int, Dictionary<int, int>> _attackingUnits = new();
    /// <summary>被攻盟友 → 同时进攻其的敌数(原版 attackedAllies;援助积极度依据)。</summary>
    public readonly Dictionary<int, int> AttackedAllies = new();
    /// <summary>HQ 反链(HQ 构造注入;switchToAttack/trainEmergencyUnits 用)。</summary>
    public Headquarters? Hq;

    public DefenseManager(PetraConfig config) => _config = config;

    public DefenseArmy? GetArmy(int id) => Armies.FirstOrDefault(a => a.ID == id);

    // ── 主更新(原版 defenseManager.update:26-77)──

    public void Update(GameState gameState, AIEventBuffer events)
    {
        CheckEvents(gameState, events);

        // 清理失效目标(原版:无位置/不再敌对即除)。
        TargetList.RemoveAll(id =>
        {
            var t = gameState.GetEntityById(id);
            return t == null || t.Position2D == default
                || !gameState.IsPlayerEnemy(t.Owner);
        });

        // attackedAllies 重算(原版:每轮从 attackingUnits 聚合,≥8 单位算一支攻军)。
        AttackedAllies.Clear();
        var attackingArmies = new Dictionary<int, Dictionary<int, int>>();
        foreach (var (enemy, allies) in _attackingUnits)
            foreach (var (ally, count) in allies)
            {
                if (count < 8) continue;
                if (!attackingArmies.TryGetValue(enemy, out var m))
                    attackingArmies[enemy] = m = new Dictionary<int, int>();
                m[ally] = m.GetValueOrDefault(ally) + 1;
            }
        foreach (var (_, allies) in attackingArmies)
            foreach (var (ally, _) in allies)
                AttackedAllies[ally] = AttackedAllies.GetValueOrDefault(ally) + 1;

        CheckEnemyArmies(gameState);
        CheckEnemyUnits(gameState);
        AssignDefenders(gameState);
    }

    // ── 编军(原版 makeIntoArmy)──

    public void MakeIntoArmy(GameState gameState, uint entityId, string type = DefenseArmy.TypeDefault)
    {
        if (type == DefenseArmy.TypeDefault)
            foreach (var army in Armies)
                if (army.Type == type && army.AddFoe(gameState, entityId))
                    return;
        Armies.Add(new DefenseArmy(gameState, new[] { entityId }, type, _nextArmyId++, _config));
    }

    // ── 威胁判定(原版 isDangerous:94-205)──

    public bool IsDangerous(GameState gameState, AIEntity entity)
    {
        if (entity.Position2D == default) return false;

        var territory = SimSystem.Territory;
        int territoryOwner = territory?.GetOwner(entity.Position2D.X, entity.Position2D.Y) ?? 0;
        // 敌领土内的敌单位不追(原版:领土主非 0 且非盟友 → 安全)。
        if (territoryOwner != 0 && !gameState.IsPlayerAlly(territoryOwner))
            return false;

        // 敌工程兵在修建筑 → 若在修敌地基(我领土内)或敌 CC(离我建筑 ~173m²=30000)→
        // 地基入威胁清单(原版 REPAIRING 分支;Raid 目标源)。
        if (entity.UnitAIState == "INDIVIDUAL.REPAIR.REPAIRING"
            && entity.UnitAIOrderTarget is { } repairTargetId)
        {
            if (TargetList.Contains(repairTargetId.Value)) return true;
            var repairTarget = gameState.GetEntityById(repairTargetId.Value);
            if (repairTarget != null && gameState.IsPlayerEnemy(repairTarget.Owner))
            {
                if (territoryOwner == gameState.PlayerId)
                {
                    if (repairTarget.IsStructure) TargetList.Add(repairTargetId.Value);
                    return true;
                }
                if (repairTarget.HasClass("CivCentre"))
                {
                    foreach (var building in gameState.GetOwnStructures().Values())
                    {
                        if (building.IsFoundation) continue;
                        if (SquareDist(building.Position2D, entity.Position2D) > 30000) continue;
                        TargetList.Add(repairTargetId.Value);
                        return true;
                    }
                }
            }
        }

        if (!entity.CanAttack || entity.HasClass("Support")) return false;
        // 远程按射程(+30 建筑尺寸近似),近战定值 6000(原版同款)。
        float dist2Min = 6000;
        if (entity.IsRanged)
        {
            float range = entity.Template.GetFloat("Attack/Ranged/MaxRange");
            if (range > 0) dist2Min = (range + 30) * (range + 30);
        }

        // 威胁清单邻近(原版:敌在任一被威胁地基附近即危)。
        foreach (var targetId in TargetList)
        {
            var threatTarget = gameState.GetEntityById(targetId);
            if (threatTarget == null || threatTarget.Position2D == default) continue;
            if (SquareDist(threatTarget.Position2D, entity.Position2D) < dist2Min) return true;
        }

        // 同盟 CC 邻近(原版:协同度门——cooperation<0.3 不援,在建 <0.6 不援)。
        foreach (var cc in gameState.GetStructures().Values()
            .Where(e => e.HasClass("CivCentre") && e.Position2D != default))
        {
            if (!gameState.IsPlayerAlly(cc.Owner)) continue;
            if (cc.IsFoundation && cc.FoundationProgress <= 0) continue;
            double cooperation = GetCooperationLevel(cc.Owner);
            if (cooperation < 0.3 || cooperation < 0.6 && cc.IsFoundation) continue;
            if (SquareDist(cc.Position2D, entity.Position2D) < dist2Min) return true;
        }

        // 我方建筑邻近(原版:非 blinking(连通领土)或可防守建筑)。
        foreach (var building in gameState.GetOwnStructures().Values())
        {
            if (building.IsFoundation && building.FoundationProgress <= 0) continue;
            if (SquareDist(building.Position2D, entity.Position2D) > dist2Min) continue;
            if (territory == null
                || !territory.IsTerritoryBlinking(building.Position2D.X, building.Position2D.Y)
                || Hq == null || Hq.IsDefendable(building))
                return true;
        }

        // 盟友领土内:该盟友正被 ≥2 敌攻且协同度高 → 连普通建筑也援(原版 attackedAllies 分支)。
        if (territoryOwner != 0 && territoryOwner != gameState.PlayerId
            && gameState.IsPlayerMutualAlly(territoryOwner))
        {
            if (AttackedAllies.GetValueOrDefault(territoryOwner) > 1
                && GetCooperationLevel(territoryOwner) > 0.7)
            {
                foreach (var building in gameState.GetStructures().Values()
                    .Where(e => e.Owner == territoryOwner && e.Position2D != default))
                {
                    if (building.IsFoundation && building.FoundationProgress <= 0) continue;
                    if (SquareDist(building.Position2D, entity.Position2D) > dist2Min) continue;
                    if (territory == null
                        || !territory.IsTerritoryBlinking(building.Position2D.X, building.Position2D.Y))
                        return true;
                }
            }
            // 记账:敌 → 盟友进攻计数(原版 attackingUnits 更新)。
            int enemy = entity.Owner;
            if (enemy > 0)
            {
                if (!_attackingUnits.TryGetValue(enemy, out var m))
                    _attackingUnits[enemy] = m = new Dictionary<int, int>();
                m[territoryOwner] = m.GetValueOrDefault(territoryOwner) + 1;
            }
        }
        return false;
    }

    /// <summary>原版 GetCooperationLevel:协同性格 + 该盟友每多一个同时进攻者 +0.2。</summary>
    public double GetCooperationLevel(int ally)
    {
        double cooperation = _config.Personality.Cooperative;
        int attacked = AttackedAllies.GetValueOrDefault(ally);
        if (attacked > 1) cooperation += 0.2 * (attacked - 1);
        return cooperation;
    }

    // ── 敌军扫描(原版 checkEnemyUnits:逐玩家轮次)──

    private void CheckEnemyUnits(GameState gameState)
    {
        // 原版轮转域 = playersData(地图槽全表);我们没有槽表,用注册玩家 ∪
        // 敌兵实际属主(覆盖无主/未注册敌人——否则他们的单位永不进扫描)。
        var players = gameState.Cm.Players.GetNonGaiaPlayerIds().ToList();
        foreach (var e in gameState.GetEnemyUnits().Values())
            if (e.Owner > 0 && !players.Contains(e.Owner)) players.Add(e.Owner);
        players.Sort();
        if (players.Count == 0) return;
        int i = players[(int)((gameState.Net?.CurrentTurn ?? 0) % (uint)players.Count)];
        _attackingUnits.Remove(i);

        if (i == gameState.PlayerId)
        {
            // 无军时:我方被占建筑占领点流失 ≥25% → 夺回军(原版 capturing 分支)。
            if (Armies.Count == 0)
                foreach (var ent in gameState.GetOwnStructures().Values())
                {
                    var cap = gameState.Cm.QueryInterface<Components.CapturableComponent>(ent.Entity);
                    if (cap == null) continue;
                    int mine = (int)cap.CapturePoints[Math.Min(gameState.PlayerId,
                        Components.CapturableComponent.MaxPlayers)].ToFloat();
                    int lost = 0;
                    for (int j = 0; j < cap.CapturePoints.Length; j++)
                        if (gameState.IsPlayerEnemy(j))
                            lost += (int)cap.CapturePoints[j].ToFloat();
                    if (lost < Math.Ceiling(0.25 * mine) || mine == 0) continue;
                    MakeIntoArmy(gameState, ent.Id, DefenseArmy.TypeCapturing);
                    break;
                }
            return;
        }
        if (!gameState.IsPlayerEnemy(i)) return;

        foreach (var ent in gameState.GetEnemyUnits().Values().Where(e => e.Owner == i))
        {
            if (gameState.Metadata.GetObject(ent.Id, "PartOfArmy") != null) continue;

            // 动物:仅进攻我方/盟友的计入(原版 COMBAT 状态 + 目标盟友过滤)。
            if (ent.HasClass("Animal"))
            {
                if (!(ent.UnitAIState ?? "").Contains(".COMBAT")) continue;
                var target = ent.UnitAIOrderTarget is { } t ? gameState.GetEntityById(t.Value) : null;
                if (target == null || !gameState.IsPlayerAlly(target.Owner)) continue;
            }
            if (ent.HasClass("Ship") || ent.HasClass("Trader")) continue;
            if (IsDangerous(gameState, ent))
                MakeIntoArmy(gameState, ent.Id);
        }

        // 我领土内的 gaia 可占建筑(敌退/衰变残留)→ 夺还军(原版尾段)。
        if (i != 0 || Armies.Count > 1 || Hq == null || !Hq.HasActiveBase(gameState)) return;
        foreach (var ent in gameState.GetStructures().Values().Where(e => e.Owner == 0))
        {
            if (ent.Position2D == default) continue;
            if (gameState.Metadata.GetObject(ent.Id, "PartOfArmy") != null) continue;
            bool capturable = gameState.Cm.QueryInterface<Components.CapturableComponent>(ent.Entity) != null;
            if (!capturable && !ent.HasDefensiveFire) continue;
            var territory = SimSystem.Territory;
            if (territory != null
                && territory.GetOwner(ent.Position2D.X, ent.Position2D.Y) == gameState.PlayerId)
                MakeIntoArmy(gameState, ent.Id, DefenseArmy.TypeCapturing);
        }
    }

    // ── 军管理(原版 checkEnemyArmies:282-396)──

    private void CheckEnemyArmies(GameState gameState)
    {
        for (int i = 0; i < Armies.Count; i++)
        {
            var army = Armies[i];
            var breakaways = army.Update(gameState);
            // 脱离者各自成新军(原版:假定危险)。
            foreach (var breaker in breakaways)
                MakeIntoArmy(gameState, breaker);
            if (army.GetState() == 0)
            {
                if (army.Type == DefenseArmy.TypeDefault)
                    SwitchToAttack(gameState, army);
                army.Clear(gameState);
                Armies.RemoveAt(i--);
            }
        }

        // 邻近 default 军合并(armyMergeSize 内;原版同款双重循环)。
        for (int i = 0; i < Armies.Count - 1; i++)
        {
            var army = Armies[i];
            if (army.Type != DefenseArmy.TypeDefault) continue;
            for (int j = i + 1; j < Armies.Count; j++)
            {
                var other = Armies[j];
                if (other.Type != DefenseArmy.TypeDefault) continue;
                if (SquareDist(army.FoePosition, other.FoePosition) > _config.Defense.ArmyMergeSize)
                    continue;
                army.Merge(gameState, other);
                Armies.RemoveAt(j--);
            }
        }

        if ((gameState.Net?.CurrentTurn ?? 0) % 5 != 0) return;
        // 5 回合一评:军质心回敌领土 → 散;中立/无主区且不近我 CC(200m²=40000)
        // 也不近码头(100m²=10000)→ 散(default 军先尝试转攻)。
        var territory = SimSystem.Territory;
        for (int i = 0; i < Armies.Count; i++)
        {
            var army = Armies[i];
            army.RecalculatePosition(gameState);
            int owner = territory?.GetOwner(army.FoePosition.X, army.FoePosition.Y) ?? 0;
            if (gameState.IsPlayerEnemy(owner))
            {
                if (gameState.IsPlayerMutualAlly(owner))
                {
                    // 盟友领土:更新攻盟计数(原版 attackingArmies 维护)。
                    foreach (var id in army.FoeEntities)
                    {
                        var ent = gameState.GetEntityById(id);
                        if (ent == null || ent.Owner <= 0) continue;
                        AttackedAllies[owner] = AttackedAllies.GetValueOrDefault(owner) + 1;
                        break;
                    }
                }
                continue;
            }
            if (owner != 0)
            {
                army.Clear(gameState);
                Armies.RemoveAt(i--);
                continue;
            }
            bool stillDangerous = false;
            foreach (var cc in gameState.GetStructures().Values()
                .Where(e => e.HasClass("CivCentre") && e.Position2D != default))
            {
                if (!gameState.IsPlayerAlly(cc.Owner)) continue;
                if (GetCooperationLevel(cc.Owner) < 0.3 && cc.Owner != gameState.PlayerId) continue;
                if (SquareDist(cc.Position2D, army.FoePosition) > 40000) continue;
                stillDangerous = true;
                break;
            }
            if (!stillDangerous)
                foreach (var dock in gameState.GetOwnStructures().Values()
                    .Where(e => e.HasClass("Dock") && e.Position2D != default))
                    if (SquareDist(dock.Position2D, army.FoePosition) <= 10000)
                    { stillDangerous = true; break; }
            if (stillDangerous) continue;
            if (army.Type == DefenseArmy.TypeDefault)
                SwitchToAttack(gameState, army);
            army.Clear(gameState);
            Armies.RemoveAt(i--);
        }
    }

    // ── 分派防守者(原版 assignDefenders:397-523)──

    private void AssignDefenders(GameState gameState)
    {
        if (Armies.Count == 0) return;

        var armiesNeeding = new List<(DefenseArmy Army, ushort Access, double Need)>();
        foreach (var army in Armies)
        {
            double need = army.NeedsDefenders(gameState);
            if (need <= 0) continue;
            ushort access = 0;
            foreach (var foeId in army.FoeEntities)
            {
                var ent = gameState.GetEntityById(foeId);
                if (ent == null || ent.Position2D == default) continue;
                access = gameState.Accessibility?.GetAccessValue(
                    ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat()) ?? (ushort)0;
                break;
            }
            army.RecalculatePosition(gameState);
            armiesNeeding.Add((army, access, need));
        }
        if (armiesNeeding.Count == 0) return;

        // 候选防守者(原版过滤全量:无位置/-2/-3 计划/Support/无攻击/攻城投石/
        // 渔船/运输中/胜利关键/进攻计划中的 completing/walking/attacking 不收)。
        var potential = new List<uint?>();
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            if (ent.Position2D == default) continue;
            var plan = gameState.Metadata.GetObject(ent.Id, "plan");
            if (plan is int p && (p == -2 || p == -3)) continue;
            if (ent.HasClass("Support") || !ent.CanAttack) continue;
            if (ent.HasClass("StoneThrower") || ent.HasClass("FishingBoat")) continue;
            if (gameState.Metadata.GetObject(ent.Id, "transport") != null
                || gameState.Metadata.GetObject(ent.Id, "transporter") != null) continue;
            if (Hq != null && Hq.VictoryManager.CriticalEnts.Contains(ent.Id)) continue;
            if (plan is int pv && pv != -1)
            {
                var subrole = gameState.Metadata.GetObject(ent.Id, "subrole")?.ToString();
                if (subrole is WorkerRoles.SubroleCompleting or WorkerRoles.SubroleWalking
                    or WorkerRoles.SubroleAttacking) continue;
            }
            potential.Add(ent.Id);
        }

        // 两趟分派:先同陆,后任意(原版 ipass 语义)。
        for (int ipass = 0; ipass < 2; ipass++)
        {
            int backup = 0;
            for (int i = 0; i < potential.Count; i++)
            {
                if (potential[i] == null) continue;
                var ent = gameState.GetEntityById(potential[i]!.Value);
                if (ent == null || ent.Position2D == default) continue;
                ushort? access = ipass == 0
                    ? gameState.Accessibility?.GetAccessValue(
                        ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat())
                    : null;

                int aMin = -1;
                float distMin = float.MaxValue;
                for (int a = 0; a < armiesNeeding.Count; a++)
                {
                    if (access != null && armiesNeeding[a].Access != access.Value) continue;
                    // 至少能攻击军内一个目标。
                    bool canHit = armiesNeeding[a].Army.FoeEntities
                        .Select(id => gameState.GetEntityById(id))
                        .Any(e => e != null && ent.CanAttackTarget(e));
                    if (!canHit) continue;
                    float dist = SquareDist(ent.Position2D, armiesNeeding[a].Army.FoePosition);
                    if (aMin >= 0 && dist > distMin) continue;
                    aMin = a; distMin = dist;
                }
                // 出境/远距作战留后备(原版:backup<12 且(无匹配 或 远且非我领土))。
                var territory = SimSystem.Territory;
                if (backup < 12 && (aMin < 0 || distMin > 40000
                        && territory != null && territory.GetOwner(
                            armiesNeeding[aMin].Army.FoePosition.X,
                            armiesNeeding[aMin].Army.FoePosition.Y) != gameState.PlayerId))
                {
                    backup++;
                    potential[i] = null;
                    continue;
                }
                if (aMin < 0) continue;

                var need = armiesNeeding[aMin];
                need.Need -= Headquarters.GetMaxStrength(ent.Template, null);
                armiesNeeding[aMin] = need;
                need.Army.AddOwn(gameState, potential[i]!.Value);
                need.Army.AssignUnit(gameState, potential[i]!.Value);
                potential[i] = null;

                if (need.Need <= 0)
                {
                    armiesNeeding.RemoveAt(aMin);
                    if (armiesNeeding.Count == 0) return;
                }
            }
        }

        // 兵力仍缺 → 应急训练(原版 trainEmergencyUnits:最近 CC 补步兵)。
        if (armiesNeeding.Count > 0 && Hq != null)
            Hq.TrainEmergencyUnits(gameState,
                armiesNeeding.Select(a => a.Army.FoePosition).ToList());
    }

    // ── 转攻(原版 switchToAttack:935-958)──

    /// <summary>威胁解除的 default 军转进攻:目标清单中 120m(14400 平方)内同陆目标 →
    /// 经 attackManager.switchDefenseToAttack 起 uniqueTarget 进攻。</summary>
    private void SwitchToAttack(GameState gameState, DefenseArmy army)
    {
        if (Hq == null) return;
        foreach (var targetId in TargetList)
        {
            var target = gameState.GetEntityById(targetId);
            if (target == null || target.Position2D == default
                || !gameState.IsPlayerEnemy(target.Owner)) continue;
            ushort targetAccess = gameState.Accessibility?.GetAccessValue(
                target.Position2D.X.ToFloat(), target.Position2D.Y.ToFloat()) ?? (ushort)0;
            foreach (var entId in army.OwnEntities)
            {
                var ent = gameState.GetEntityById(entId);
                if (ent == null || ent.Position2D == default) continue;
                if (gameState.Accessibility != null
                    && gameState.Accessibility.GetAccessValue(
                        ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat()) != targetAccess)
                    continue;
                if (SquareDist(target.Position2D, ent.Position2D) > 14400) continue;
                Hq.AttackManager.SwitchDefenseToAttack(gameState, target, army.ID);
                return;
            }
        }
    }

    /// <summary>abortArmy:解散并清簿记(原版:对外取消路径)。</summary>
    public void AbortArmy(GameState gameState, DefenseArmy army)
    {
        army.Clear(gameState);
        Armies.Remove(army);
    }

    // ── 序列化(原版 defenseManager.js Serialize)──
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("nextArmy", _nextArmyId);
        s.NumberI32("targets", TargetList.Count);
        foreach (var id in TargetList) s.NumberU32("t", id);
        s.NumberI32("attackedAllies", AttackedAllies.Count);
        foreach (var kv in AttackedAllies.OrderBy(kv => kv.Key))
        {
            s.NumberI32("ally", kv.Key);
            s.NumberI32("count", kv.Value);
        }
        s.NumberI32("armies", Armies.Count);
        foreach (var army in Armies.OrderBy(a => a.ID))
            army.Serialize(s);
    }

    public void Deserialize(Serialization.IDeserializer d, GameState gameState)
    {
        _nextArmyId = d.NumberI32("nextArmy");
        int targets = d.NumberI32("targets");
        for (int i = 0; i < targets; i++) TargetList.Add(d.NumberU32("t"));
        int allies = d.NumberI32("attackedAllies");
        for (int i = 0; i < allies; i++)
            AttackedAllies[d.NumberI32("ally")] = d.NumberI32("count");
        int armies = d.NumberI32("armies");
        for (int i = 0; i < armies; i++)
            Armies.Add(DefenseArmy.Deserialize(d, gameState, _config));
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var army in Armies)
            army.CheckEvents(gameState, events);
    }

    private static float SquareDist(FixedVector2D a, FixedVector2D b)
    {
        float dx = a.X.ToFloat() - b.X.ToFloat();
        float dz = a.Y.ToFloat() - b.Y.ToFloat();
        return dx * dx + dz * dz;
    }
}
