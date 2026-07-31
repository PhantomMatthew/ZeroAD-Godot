using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 单位捕获(对齐原版 Capturable.js Capture/CanCapture/GetRegenRate + Attack.js
/// GetBestAttackAgainst 的 allowCapture 语义 + helpers/Attack.js 的 Capture 效果路由):
/// 每玩家 CP 积累、主人 CP 归零翻面、多敌均摊、驻军捕获强度加成 regen、
/// DelayedDamage 捕获通道(伤害先结算 → hp 比例放大)、攻击类型选择偏好矩阵、
/// UnitAI allowCapture 指令语义(捕获单只掉 CP 不掉血)、序列化往返。
/// </summary>
public sealed class CapturableTests
{
    private static Fixed F(float v) => Fixed.FromFloat(v);

    private static EntityId AddPlayerWithDiplomacy(ComponentManager cm, int playerId)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
        cm.AddComponent(e, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, e);
        return e;
    }

    /// <summary>P1/P2 互为敌人(异队),P3 按需。</summary>
    private static void SeedEnemies(ComponentManager cm, params int[] playerIds)
    {
        foreach (var p in playerIds) AddPlayerWithDiplomacy(cm, p);
        var teams = new Dictionary<int, int>();
        foreach (var p in playerIds) teams[p] = p;   // 每人一队 → 全互敌
        cm.Players.SeedDiplomacyFromTeams(teams);
    }

    /// <summary>带 Capturable 的建筑;owner CP 拉满(InitForOwner)。</summary>
    private static EntityId AddCapturableBuilding(ComponentManager cm, int owner,
        float maxCp = 500, float regen = 0, float garrisonRegen = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new HealthComponent());
        cm.AddComponent(e, new CapturableComponent());
        var cap = cm.QueryInterface<CapturableComponent>(e)!;
        cap.MaxCapturePoints = F(maxCp);
        cap.RegenRate = F(regen);
        cap.GarrisonRegenRate = F(garrisonRegen);
        cap.InitForOwner(owner);
        return e;
    }

    private static EntityId AddSoldier(ComponentManager cm, int owner,
        float captureStrength = 0, float hackDamage = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new HealthComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new AttackComponent());
        var atk = cm.QueryInterface<AttackComponent>(e)!;
        atk.Damage.Amounts[DamageType.Hack] = (int)hackDamage;
        atk.CaptureStrength = F(captureStrength);
        return e;
    }

    // ---------- Capture / CanCapture ----------

    [Fact]
    public void Capture_TakesFromOwner_AwardsCaptor()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var soldier = AddSoldier(cm, owner: 2);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        Fixed taken = cap.Capture(cm, F(100), soldier, captorOwner: 2);

        Assert.Equal(F(100), taken);
        Assert.Equal(F(400), cap.CapturePoints[1]);
        Assert.Equal(F(100), cap.CapturePoints[2]);
        Assert.Equal(F(500), cap.CapturePoints[0] + cap.CapturePoints[1] + cap.CapturePoints[2]);
        Assert.Equal(1, cm.QueryInterface<OwnershipComponent>(building)!.PlayerId);   // 未翻面
    }

    [Fact]
    public void Capture_FlipsOwnership_WhenOwnerCpReachesZero()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var soldier = AddSoldier(cm, owner: 2);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        cap.Capture(cm, F(500), soldier, captorOwner: 2);

        Assert.Equal(F(0), cap.CapturePoints[1]);
        Assert.Equal(F(500), cap.CapturePoints[2]);
        Assert.Equal(2, cm.QueryInterface<OwnershipComponent>(building)!.PlayerId);   // 翻面给 CP 最多者
    }

    [Fact]
    public void Capture_FlipGoesToPlayerWithMostCp_NotLastCaptor()
    {
        var cm = new ComponentManager(1);
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        AddPlayerWithDiplomacy(cm, 3);
        // P2/P3 同队(互盟)共伐 P1:盟友 CP 互不被对方均摊抽走。
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 1 });
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        var s2 = AddSoldier(cm, owner: 2);
        var s3 = AddSoldier(cm, owner: 3);

        cap.Capture(cm, F(300), s3, captorOwner: 3);   // P3 先啃 300
        cap.Capture(cm, F(200), s2, captorOwner: 2);   // P2 补最后 200 → 主人归零

        Assert.Equal(3, cm.QueryInterface<OwnershipComponent>(building)!.PlayerId);   // P3 CP 多 → 归 P3
        Assert.Equal(F(300), cap.CapturePoints[3]);
        Assert.Equal(F(200), cap.CapturePoints[2]);
    }

    [Fact]
    public void Capture_RejectsAlly_AndInvalidOwner()
    {
        var cm = new ComponentManager(1);
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 });   // 同队=盟
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var ally = AddSoldier(cm, owner: 2);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        Assert.False(cap.CanCapture(cm, 2));                                    // 无敌方 CP 可抽
        Assert.Equal(F(0), cap.Capture(cm, F(100), ally, captorOwner: 2));      // 盟友拒收
        Assert.Equal(F(0), cap.Capture(cm, F(100), ally, captorOwner: -1));     // INVALID_PLAYER 拒收
        Assert.Equal(F(500), cap.CapturePoints[1]);
        Assert.Equal(F(0), cap.CapturePoints[2]);
    }

    [Fact]
    public void Capture_DistributesAcrossAllEnemies()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2, 3);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        // 构造混合 CP:P1 300 / P3 100 / P2 100(总和=500)。
        cap.CapturePoints[1] = F(300);
        cap.CapturePoints[3] = F(100);
        cap.CapturePoints[2] = F(100);
        var s2 = AddSoldier(cm, owner: 2);

        Fixed taken = cap.Capture(cm, F(60), s2, captorOwner: 2);

        Assert.Equal(F(60), taken);
        Assert.Equal(F(270), cap.CapturePoints[1]);   // 均摊 30
        Assert.Equal(F(70), cap.CapturePoints[3]);    // 均摊 30
        Assert.Equal(F(160), cap.CapturePoints[2]);   // 抽中量全奖给捕获者
    }

    [Fact]
    public void CanCapture_False_WhenOnlyOwnCpRemains()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 2, maxCp: 500);   // P2 自己的建筑
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        Assert.False(cap.CanCapture(cm, 2));   // 自己的 CP 不是敌方 CP
        Assert.True(cap.CanCapture(cm, 1));
    }

    [Fact]
    public void SetCapturePoints_ReplacesArray()
    {
        var cm = new ComponentManager(1);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        var points = new Fixed[cap.CapturePoints.Length];
        points[1] = F(200);
        points[2] = F(300);
        cap.SetCapturePoints(points);

        Assert.Equal(F(200), cap.CapturePoints[1]);
        Assert.Equal(F(300), cap.CapturePoints[2]);
        points[1] = F(999);                                     // 拷贝语义:改外部数组不影响组件
        Assert.Equal(F(200), cap.CapturePoints[1]);
    }

    [Fact]
    public void Reduce_AwardsMaxMinusSum_AndNeverHangs()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 1000);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        // 9 个敌方 CP 持有者(gaia + P1..P8),captor=P9;总和 900 < max 1000
        // (decay 分配整除截断造成的漂移态)。
        cap.CapturePoints[0] = F(100);
        for (int p = 1; p <= 8; p++) cap.CapturePoints[p] = F(100);
        cap.CapturePoints[9] = Fixed.Zero;
        var s9 = AddSoldier(cm, owner: 9);

        // 剩余 8 raw < 敌数 9 → 均摊份额定点截断为 0:无保底死循环,有保底弃尘埃。
        Fixed tiny = Fixed.Zero.WithInternalValue(8);
        Fixed taken = cap.Capture(cm, tiny, s9, captorOwner: 9);

        // 原版语义:奖给量 = max − 总和(自愈漂移)= 100,而非实际抽走的 ~0。
        Assert.Equal(F(100), taken);
        Assert.Equal(F(100), cap.CapturePoints[9]);
    }

    // ---------- regen(驻军捕获强度加成) ----------

    [Fact]
    public void GetRegenRate_AddsGarrisonedCaptureStrength()
    {
        var cm = new ComponentManager(1);
        AddPlayerWithDiplomacy(cm, 1);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 5, garrisonRegen: 2);
        cm.AddComponent(building, new GarrisonHolderComponent());
        var holder = cm.QueryInterface<GarrisonHolderComponent>(building)!;
        holder.AllowedClasses.Add("Infantry");
        var soldier = AddSoldier(cm, owner: 1, captureStrength: 2.5f);
        cm.QueryInterface<IdentityComponent>(soldier)!.Classes.Add("Infantry");
        cm.AddComponent(soldier, new GarrisonableComponent());

        Assert.Equal(F(5), cm.QueryInterface<CapturableComponent>(building)!.GetRegenRate(cm));
        Assert.True(holder.Garrison(cm, soldier));
        // 原版 GetRegenRate:base + Σ(驻军 Capture 强度 × GarrisonRegenRate)= 5 + 2.5×2 = 10。
        Assert.Equal(F(10), cm.QueryInterface<CapturableComponent>(building)!.GetRegenRate(cm));
    }

    [Fact]
    public void TimerTick_OwnerRegen_DrainsEnemies()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 5);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        cap.CapturePoints[1] = F(400);
        cap.CapturePoints[2] = F(100);

        cap.TimerTick(cm, Fixed.FromInt(1));

        Assert.Equal(F(405), cap.CapturePoints[1]);   // 主人 +5
        Assert.Equal(F(95), cap.CapturePoints[2]);    // 从敌方(P2)抽
    }

    // ---------- DelayedDamage 捕获通道 ----------

    [Fact]
    public void DelayedDamage_RoutesCapture_AfterDamage_HpScaled()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var health = cm.QueryInterface<HealthComponent>(building)!;
        health.Current = 50; health.Max = 100;   // 半血 → scale = 0.1+0.9×0.5 = 0.55
        var soldier = AddSoldier(cm, owner: 2);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        // 捕获通道:raw 10 → hp 放大 10/0.55 ≈ 18.18。
        DelayedDamage.ScheduleHit(cm, soldier, building,
            new DamageBlock { Capture = F(10) }, delayTurns: 0);
        cm.DelayedDamage.TickPending(cm);

        Assert.Equal(50, health.Current);                       // 无物理伤害
        Assert.InRange(cap.CapturePoints[2].ToFloat(), 18.0, 18.5);
        Assert.InRange(cap.CapturePoints[1].ToFloat(), 481.5, 482.0);
        Assert.Equal(1, cm.QueryInterface<OwnershipComponent>(building)!.PlayerId);
    }

    [Fact]
    public void DelayedDamage_CaptureFlip_ThroughFullPipeline()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        var soldier = AddSoldier(cm, owner: 2);

        DelayedDamage.ScheduleHit(cm, soldier, building,
            new DamageBlock { Capture = F(600) }, delayTurns: 0);   // 超量 → 抽干即翻面
        cm.DelayedDamage.TickPending(cm);

        Assert.Equal(2, cm.QueryInterface<OwnershipComponent>(building)!.PlayerId);
        Assert.Equal(F(500), cm.QueryInterface<CapturableComponent>(building)!.CapturePoints[2]);
    }

    // ---------- AttackComponent 攻击类型选择 ----------

    [Fact]
    public void BestAttack_PrefersCaptureVsBuilding_WhenAllowed()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1);
        cm.QueryInterface<IdentityComponent>(building)!.Classes.Add("Structure");
        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        var atk = cm.QueryInterface<AttackComponent>(soldier)!;
        atk.PreferredClasses = "Unit+!Ship";

        Assert.Equal(AttackComponent.AttackChoice.Capture,
            atk.GetBestAttackAgainst(cm, building, allowCapture: true));
        Assert.Equal(AttackComponent.AttackChoice.Physical,
            atk.GetBestAttackAgainst(cm, building, allowCapture: false));
    }

    [Fact]
    public void BestAttack_UnitTie_GoesToCapture_WhenAllowed()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        // 可捕获单位(原版大象等):allowCapture=true 时 Melee(类匹 +2)与 Capture
        // (指令偏好 +1)得分相同(4:4,类型数 2);升序 .pop 取尾 → Capture 赢平手
        // (原版 g_AttackTypes = [Melee,Ranged,Capture],Capture 恒排最后)。
        var elephant = AddCapturableBuilding(cm, owner: 1);
        cm.QueryInterface<IdentityComponent>(elephant)!.Classes.Add("Unit");
        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        var atk = cm.QueryInterface<AttackComponent>(soldier)!;
        atk.PreferredClasses = "Unit+!Ship";

        Assert.Equal(AttackComponent.AttackChoice.Capture,
            atk.GetBestAttackAgainst(cm, elephant, allowCapture: true));
        // allowCapture=false:Melee 类匹 2 + 指令 1 = 3 分(总分 5)压 Capture(0 分,总分 1)。
        Assert.Equal(AttackComponent.AttackChoice.Physical,
            atk.GetBestAttackAgainst(cm, elephant, allowCapture: false));
    }

    [Fact]
    public void BestAttack_Capture_RespectsRestrictedClasses()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        var wall = AddCapturableBuilding(cm, owner: 1);
        cm.QueryInterface<IdentityComponent>(wall)!.Classes.Add("Palisade");
        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        var atk = cm.QueryInterface<AttackComponent>(soldier)!;
        atk.CaptureRestrictedClasses = "Field Palisade Wall";

        Assert.Equal(AttackComponent.AttackChoice.Physical,
            atk.GetBestAttackAgainst(cm, wall, allowCapture: true));
    }

    [Fact]
    public void BestAttack_Physical_RespectsRestrictedClasses()
    {
        var cm = new ComponentManager(1);
        SeedEnemies(cm, 1, 2);
        // 冲车(原版 template_unit_siege_ram:Melee RestrictedClasses "Field Organic"):
        // 有机目标物理型被门 + 无捕获型 → null(原版 → undefined,Order.Attack 拒单)。
        var organic = AddCapturableBuilding(cm, owner: 1);
        cm.QueryInterface<IdentityComponent>(organic)!.Classes.Add("Organic");
        var ram = AddSoldier(cm, owner: 2, captureStrength: 0, hackDamage: 100);
        var atk = cm.QueryInterface<AttackComponent>(ram)!;
        atk.PhysicalRestrictedClasses = "Field Organic";

        Assert.Null(atk.GetBestAttackAgainst(cm, organic, allowCapture: true));

        var structure = AddCapturableBuilding(cm, owner: 1);
        cm.QueryInterface<IdentityComponent>(structure)!.Classes.Add("Structure");
        Assert.Equal(AttackComponent.AttackChoice.Physical,
            atk.GetBestAttackAgainst(cm, structure, allowCapture: false));
    }

    [Fact]
    public void BestAttack_Null_WhenNothingCanAttack()
    {
        var cm = new ComponentManager(1);
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 });   // 盟
        var building = AddCapturableBuilding(cm, owner: 2, maxCp: 500);   // P2 的,P2 来打
        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        var atk = cm.QueryInterface<AttackComponent>(soldier)!;

        // 物理:非敌拒;捕获:CanCapture=false(自己的 CP)拒 → null。
        Assert.Null(atk.GetBestAttackAgainst(cm, building, allowCapture: true));
    }

    // ---------- UnitAI allowCapture 指令语义 ----------

    [Fact]
    public void AttackOrder_AllowCapture_DealsCaptureOnly()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 0);
        cm.QueryInterface<PositionComponent>(building)!.Position =
            new FixedVector3D(F(1), Fixed.Zero, F(0));
        var health = cm.QueryInterface<HealthComponent>(building)!;

        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        cm.AddComponent(soldier, new UnitMotion());
        cm.AddComponent(soldier, new UnitAIComponent());
        var ai = cm.QueryInterface<UnitAIComponent>(soldier)!;

        ai.Attack(building, allowCapture: true);
        for (int i = 0; i < 30; i++)
        {
            ai.Tick(0.1f, cm);
            cm.DelayedDamage.TickPending(cm);
        }

        Assert.Equal(100, health.Current);                       // 捕获单:不掉血
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        Assert.True(cap.CapturePoints[2] > Fixed.Zero);          // CP 在掉
        Assert.True(cap.CapturePoints[1] < F(500));
        Assert.True(cm.QueryInterface<AttackComponent>(soldier)!.CurrentAttackIsCapture);
    }

    [Fact]
    public void AttackOrder_Default_DealsPhysicalOnly()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 0);
        cm.QueryInterface<PositionComponent>(building)!.Position =
            new FixedVector3D(F(1), Fixed.Zero, F(0));
        var health = cm.QueryInterface<HealthComponent>(building)!;

        var soldier = AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10);
        cm.AddComponent(soldier, new UnitMotion());
        cm.AddComponent(soldier, new UnitAIComponent());
        var ai = cm.QueryInterface<UnitAIComponent>(soldier)!;

        ai.Attack(building);   // DEFAULT_CAPTURE=false(原版)
        for (int i = 0; i < 30; i++)
        {
            ai.Tick(0.1f, cm);
            cm.DelayedDamage.TickPending(cm);
        }

        Assert.True(health.Current < 100);                       // 物理单:掉血
        var cap = cm.QueryInterface<CapturableComponent>(building)!;
        Assert.Equal(F(500), cap.CapturePoints[1]);              // CP 不动
        Assert.Equal(F(0), cap.CapturePoints[2]);
        Assert.False(cm.QueryInterface<AttackComponent>(soldier)!.CurrentAttackIsCapture);
    }

    [Fact]
    public void AttackOrder_Physical_StopsAfterOwnershipFlip()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 0);
        cm.QueryInterface<PositionComponent>(building)!.Position =
            new FixedVector3D(F(1), Fixed.Zero, F(0));

        var soldier = AddSoldier(cm, owner: 2, captureStrength: 0, hackDamage: 10);
        cm.AddComponent(soldier, new UnitMotion());
        cm.AddComponent(soldier, new UnitAIComponent());
        var ai = cm.QueryInterface<UnitAIComponent>(soldier)!;
        var attack = cm.QueryInterface<AttackComponent>(soldier)!;

        ai.Attack(building);   // 物理单(无捕获型)
        ai.Tick(0.1f, cm);
        Assert.NotNull(attack.Target);

        // 建筑被我方翻面(友军捕获完成)→ 物理攻击必须收工(原版 OnOwnershipChanged
        // 触发 UnitAI 重评;我们 Tick 轮询),不能再打自己的建筑。
        cm.QueryInterface<OwnershipComponent>(building)!.PlayerId = 2;
        cm.NotifyOwnerChanged(building, 1, 2);
        for (int i = 0; i < 3; i++) ai.Tick(0.1f, cm);

        Assert.Null(attack.Target);
        Assert.Equal("INDIVIDUAL.IDLE", ai.FsmStateName);
    }

    // ---------- 序列化往返 ----------

    [Fact]
    public void AttackComponent_CaptureFields_RoundTrip()
    {
        var original = new AttackComponent();
        original.CaptureStrength = F(2.5f);
        original.CaptureRange = 6f;
        original.CaptureRate = 0.5f;
        original.CaptureRestrictedClasses = "Field Palisade Wall";
        original.PhysicalRestrictedClasses = "Field Organic";
        original.PreferredClasses = "Unit+!Ship";
        original.CurrentAttackIsCapture = true;

        var s1 = new CapturingSerializer();
        original.Serialize(s1);
        var restored = new AttackComponent();
        restored.Deserialize(new ReplayingDeserializer(s1));

        Assert.Equal(F(2.5f), restored.CaptureStrength);
        Assert.Equal(6f, restored.CaptureRange);
        Assert.Equal(0.5f, restored.CaptureRate);
        Assert.Equal("Field Palisade Wall", restored.CaptureRestrictedClasses);
        Assert.Equal("Field Organic", restored.PhysicalRestrictedClasses);
        Assert.Equal("Unit+!Ship", restored.PreferredClasses);
        Assert.True(restored.CurrentAttackIsCapture);
    }

    [Fact]
    public void DamageBlock_Capture_RoundTrip()
    {
        var original = new DamageBlock { Capture = F(2.5f) };
        original.Amounts[DamageType.Hack] = 7;

        var s1 = new CapturingSerializer();
        original.Serialize(s1, "dmg");
        var restored = DamageBlock.Deserialize(new ReplayingDeserializer(s1), "dmg");

        Assert.Equal(F(2.5f), restored.Capture);
        Assert.Equal(7, restored.Get(DamageType.Hack));
    }

    // ---------- 真实模板 ----------

    [Fact]
    public void RealTemplate_Infantry_ParsesCaptureAttackType()
    {
        // 真实模板(template_unit_infantry.xml):Attack/Capture 顶层类型 =
        // 强度 2.5、MaxRange 4、RepeatTime 1000、RestrictedClasses "Field Palisade Wall";
        // Melee PreferredClasses "Unit+!Ship"。
        const string templatesRoot = "../../../binaries/data/mods/public/simulation/templates";
        if (!System.IO.Directory.Exists(templatesRoot)) return;   // 数据树未拉取则跳过
        var loader = new Content.TemplateLoader(templatesRoot);

        var stats = loader.ExtractStats("units/athen/infantry_spearman_b");

        Assert.Equal(F(2.5f), stats.AttackCaptureStrength);
        Assert.Equal(4f, stats.AttackCaptureRange);
        Assert.Equal(1f, stats.AttackCaptureRate);
        Assert.Equal("Field Palisade Wall", stats.AttackCaptureRestrictedClasses);
        Assert.Equal("Unit+!Ship", stats.AttackPreferredClasses);
    }

    // ---------- Capturable/* 科技修正(use-site 惰性读) ----------

    private static TechnologyDefinition Def(string name, IReadOnlyList<Modification> mods) =>
        new(name, name, 0, 0, 0, 0, 10f,
            Array.Empty<TechRequirement>(), mods, false, null, Array.Empty<string>());

    /// <summary>单科技目录(名为 cap_tech),便于 regen 类科技修正测试。</summary>
    private static TechnologyManager TechMgrWith(params Modification[] mods)
    {
        var catalog = new TechCatalog(
            new Dictionary<string, TechnologyDefinition> { ["cap_tech"] = Def("cap_tech", mods.ToList()) },
            new Dictionary<string, IReadOnlyList<string>>());
        var tm = new TechnologyManager();
        tm.Configure(catalog, "athen");
        return tm;
    }

    private static EntityId AddPlayerWithTech(ComponentManager cm, int playerId, TechnologyManager tm)
    {
        var e = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(e, pc);
        cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
        cm.AddComponent(e, tm);
        cm.Players.AddPlayer(playerId, e);
        return e;
    }

    [Fact]
    public void GetRegenRate_AppliesRegenRateModifier()
    {
        var cm = new ComponentManager(1);
        var tm = TechMgrWith(new Modification("Capturable/RegenRate", 3f, null, null, new List<string>()));
        AddPlayerWithTech(cm, 1, tm);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 5);
        var cap = cm.QueryInterface<CapturableComponent>(building)!;

        Assert.Equal(F(5), cap.GetRegenRate(cm));     // 研究前
        tm.ApplyResearch("cap_tech", cm);
        Assert.Equal(F(8), cap.GetRegenRate(cm));     // 5 + 3(Capturable/RegenRate add)
    }

    [Fact]
    public void GetRegenRate_AppliesGarrisonRegenRateModifier()
    {
        var cm = new ComponentManager(1);
        var tm = TechMgrWith(new Modification("Capturable/GarrisonRegenRate", null, 2f, null, new List<string>()));
        AddPlayerWithTech(cm, 1, tm);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500, regen: 5, garrisonRegen: 2);
        cm.AddComponent(building, new GarrisonHolderComponent());
        var holder = cm.QueryInterface<GarrisonHolderComponent>(building)!;
        holder.AllowedClasses.Add("Infantry");
        var soldier = AddSoldier(cm, owner: 1, captureStrength: 2.5f);
        cm.QueryInterface<IdentityComponent>(soldier)!.Classes.Add("Infantry");
        cm.AddComponent(soldier, new GarrisonableComponent());
        Assert.True(holder.Garrison(cm, soldier));

        // 研究前:base 5 + 2.5 × 2 = 10
        Assert.Equal(F(10), cm.QueryInterface<CapturableComponent>(building)!.GetRegenRate(cm));
        tm.ApplyResearch("cap_tech", cm);
        // 研究后 GarrisonRegenRate ×2 → 2×2=4:base 5 + 2.5 × 4 = 15
        Assert.Equal(F(15), cm.QueryInterface<CapturableComponent>(building)!.GetRegenRate(cm));
    }

    [Fact]
    public void Capturable_Serialize_RoundTrips_BaseMaxAndCp()
    {
        var cap = new CapturableComponent();
        cap.MaxCapturePoints = F(500);
        cap.BaseMaxCapturePoints = F(500);
        cap.RegenRate = F(5);
        cap.GarrisonRegenRate = F(2);
        cap.CapturePoints[1] = F(300);
        cap.CapturePoints[2] = F(200);

        var s1 = new CapturingSerializer();
        cap.Serialize(s1);
        var restored = new CapturableComponent();
        restored.Deserialize(new ReplayingDeserializer(s1));

        Assert.Equal(F(500), restored.BaseMaxCapturePoints);
        Assert.Equal(F(500), restored.MaxCapturePoints);
        Assert.Equal(F(5), restored.RegenRate);
        Assert.Equal(F(2), restored.GarrisonRegenRate);
        Assert.Equal(F(300), restored.CapturePoints[1]);
        Assert.Equal(F(200), restored.CapturePoints[2]);
    }

    // ---------- AI allowCapture(PetraManagers → NetCommand → SimCommandExecutor) ----------

    [Fact]
    public void AttackManager_LaunchAttack_PassesAllowCapture_OnCapturableBuilding()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        SeedEnemies(cm, 1, 2);
        var building = AddCapturableBuilding(cm, owner: 1, maxCp: 500);
        cm.QueryInterface<IdentityComponent>(building)!.Classes.Add("Structure");

        // 5 个士兵(AttackComponent 含捕获强度;无 UnitAI → ApplyAttack 直走 AttackTarget,
        // 设 CurrentAttackIsCapture,无需 Tick)。AttackWaveSize=5。
        var soldiers = new List<EntityId>();
        for (int i = 0; i < 5; i++)
            soldiers.Add(AddSoldier(cm, owner: 2, captureStrength: 2.5f, hackDamage: 10));

        var net = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 2,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });
        var playerEnt = cm.Players.GetPlayerEntityId(2)!.Value;
        var snap = new AISnapshot
        {
            Player = cm.QueryInterface<PlayerComponent>(playerEnt)!,
            Soldiers = soldiers,
            EnemyBuildings = new List<EntityId> { building },
        };
        var attack = new AttackManager(cm, net);
        // AttackIntervalThinks=40:前 39 次 Update 早退,第 40 次触发 LaunchAttack。
        for (int i = 0; i < 40; i++) attack.Update(snap, 2);

        // NetTurnManager 把 commandDelay 钳到 ≥1(MAth.Max(1,delay)),命令入 _aiBundles[1]。
        // 第 1 次 AdvanceTurn 排空 [0](空),第 2 次排空 [1] → 执行 Attack(allowCapture:true)。
        net.AdvanceTurn();
        net.AdvanceTurn();

        // LaunchAttack 对每个士兵提交 Attack(allowCapture:true)→ AttackTarget 选 Capture 型。
        Assert.True(cm.QueryInterface<AttackComponent>(soldiers[0])!.CurrentAttackIsCapture);
    }
}
