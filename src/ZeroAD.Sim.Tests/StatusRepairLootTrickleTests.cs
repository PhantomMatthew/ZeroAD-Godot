using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

// StatusEffects / Repairable / Loot / ResourceTrickle 四系统(原版同名 JS 组件移植)。
public sealed class StatusRepairLootTrickleTests
{
    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        var range = new RangeManager(cm, ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static EntityId MakeEntity(ComponentManager cm, int owner, float x = 0, float z = 0)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent { Name = "E", IsUnit = true });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    // --- Repairable ---

    [Fact]
    public void BuildMultiplier_DiminishingReturns_MatchesUpstream()
    {
        Assert.Equal(1f, RepairableComponent.CalculateBuildMultiplier(0));
        Assert.Equal(1f, RepairableComponent.CalculateBuildMultiplier(1));
        // 原版:10^0.7 / 10 ≈ 0.5012
        Assert.Equal(0.5012f, RepairableComponent.CalculateBuildMultiplier(10), 3);
    }

    [Fact]
    public void Repair_HealsUpToMax_AndReportsDone()
    {
        var cm = SetupWorld();
        var bld = MakeEntity(cm, 1);
        cm.AddComponent(bld, new HealthComponent { Current = 40, Max = 100 });
        cm.AddComponent(bld, new CostComponent { BuildTime = 10f });
        var rep = new RepairableComponent { RepairTimeRatio = 2f };
        cm.AddComponent(bld, rep);
        var builder = MakeEntity(cm, 1);

        // GetRepairRate = maxHp / (ratio × buildTime) = 100 / (2×10) = 5 HP/s。
        // 单工人 rate=1,mult=1:每 0.1s tick 回 0.5 → 小数结转,2 tick 回 1。
        rep.AddBuilder(builder, 1f);
        Assert.Equal(1, rep.NumBuilders);

        bool done = false;
        for (int i = 0; i < 200 && !done; i++)
            done = rep.Repair(cm, builder, 1f, 0.1f);

        Assert.True(done);
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(bld)!.Current);
    }

    [Fact]
    public void UnitAI_RepairOrder_EntersRepairingState_WhenAtWorksite()
    {
        // 到岗转移(原版 MoveCompleted → REPAIRING):工人在工位半径内时,
        // FSM 必须从 REPAIR.APPROACHING 进 REPAIR.REPAIRING——否则动画停在
        // walk(工人原地踏步盖房子)。
        var cm = SetupWorld();
        var site = MakeEntity(cm, 1, 5, 0);   // 距工人 5m(< 8m 工位半径)
        var fdn = new FoundationComponent();
        cm.AddComponent(site, fdn);
        fdn.Configure("structures/test", 100f);

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        cm.AddComponent(worker, new BuilderComponent { BuildSpeed = 1f });
        var ai = new UnitAIComponent();
        cm.AddComponent(worker, ai);

        ai.Repair(site);
        for (int i = 0; i < 6; i++) ai.Tick(0.1f, cm);

        Assert.Equal("INDIVIDUAL.REPAIR.REPAIRING", ai.FsmStateName);
        Assert.True(cm.QueryInterface<BuilderComponent>(worker)!.AtWorksite);
    }

    [Fact]
    public void BuilderTick_RepairsAdjacentDamagedBuilding_ThenClearsTarget()
    {
        var cm = SetupWorld();
        var bld = MakeEntity(cm, 1, 5, 0);   // 距工人 5m(< 8m 工位半径)
        cm.AddComponent(bld, new HealthComponent { Current = 90, Max = 100 });
        cm.AddComponent(bld, new CostComponent { BuildTime = 10f });
        var rep = new RepairableComponent { RepairTimeRatio = 2f };
        cm.AddComponent(bld, rep);

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        var builderCmp = new BuilderComponent { BuildSpeed = 50f };  // 高速率快速修满
        cm.AddComponent(worker, builderCmp);
        builderCmp.Build(bld);

        for (int i = 0; i < 50 && builderCmp.Target != null; i++)
            builderCmp.Tick(cm);

        Assert.Null(builderCmp.Target);
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(bld)!.Current);
        Assert.Equal(0, rep.NumBuilders);   // 收工出工人表
    }

    [Fact]
    public void BuilderTick_RepairUnregisteredWhenWalkingAway()
    {
        var cm = SetupWorld();
        var bld = MakeEntity(cm, 1, 5, 0);
        cm.AddComponent(bld, new HealthComponent { Current = 50, Max = 100 });
        cm.AddComponent(bld, new CostComponent { BuildTime = 10f });
        var rep = new RepairableComponent { RepairTimeRatio = 2f };
        cm.AddComponent(bld, rep);

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        var builderCmp = new BuilderComponent { BuildSpeed = 1f };
        cm.AddComponent(worker, builderCmp);
        builderCmp.Build(bld);

        builderCmp.Tick(cm);   // 进工位 → 入表
        Assert.Equal(1, rep.NumBuilders);

        // 目标被搬到远处(模拟工人被打断后重新接近的场景反向):直接把工人搬走。
        var pos = cm.QueryInterface<PositionComponent>(worker)!;
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(100), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero);
        builderCmp.Tick(cm);   // 出工位 → 出表
        Assert.Equal(0, rep.NumBuilders);
    }

    [Fact]
    public void UnitAI_RepairOrder_RejectsFullHealthBuilding()
    {
        var cm = SetupWorld();
        var bld = MakeEntity(cm, 1, 5, 0);
        cm.AddComponent(bld, new HealthComponent { Current = 100, Max = 100 });  // 满血
        cm.AddComponent(bld, new RepairableComponent());

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        cm.AddComponent(worker, new BuilderComponent());
        var ai = new UnitAIComponent();
        cm.AddComponent(worker, ai);

        ai.Repair(bld);
        ai.Tick(0.1f, cm);   // 处理订单 → 满血拒收 → FinishOrder 回 IDLE

        var builder = cm.QueryInterface<BuilderComponent>(worker)!;
        Assert.Null(builder.Target);
        Assert.Equal("IDLE", ai.FsmStateName.Substring(ai.FsmStateName.Length - 4));
    }

    // --- Loot / Looter ---

    [Fact]
    public void LooterCollect_GrantsLootPlusCarriedResources()
    {
        var cm = SetupWorld();
        var killer = MakeEntity(cm, 1);
        cm.AddComponent(killer, new LooterComponent());

        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new LootComponent { Metal = 10, Food = 3 });
        var gatherer = new ResourceGatherer();
        cm.AddComponent(victim, gatherer);
        gatherer.CarryAmount = 5;
        gatherer.CarryType = ResourceType.Wood;

        var player = cm.Players.GetPlayerEntity(1)!;
        int m0 = player.Metal, f0 = player.Food, w0 = player.Wood, s0 = player.Stone;
        cm.QueryInterface<LooterComponent>(killer)!.Collect(cm, victim);

        Assert.Equal(m0 + 10, player.Metal);
        Assert.Equal(f0 + 3, player.Food);
        Assert.Equal(w0 + 5, player.Wood);
        Assert.Equal(s0, player.Stone);
    }

    [Fact]
    public void KillThroughDamagePipeline_TriggersLootCollection()
    {
        var cm = SetupWorld();
        var killer = MakeEntity(cm, 1);
        cm.AddComponent(killer, new LooterComponent());

        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new HealthComponent { Current = 5, Max = 50 });
        cm.AddComponent(victim, new LootComponent { Metal = 7 });

        // 经 DelayedDamage 结算击杀(组件管理器自带队列 → 排入后本回合结算)。
        int m0 = cm.Players.GetPlayerEntity(1)!.Metal;
        DelayedDamage.ScheduleHit(cm, killer, victim,
            new DamageBlock(DamageType.Hack, 10), delaySeconds: 0f);
        cm.DelayedDamage.TickPending(cm);

        Assert.True(cm.QueryInterface<HealthComponent>(victim)!.IsDead);
        Assert.Equal(m0 + 7, cm.Players.GetPlayerEntity(1)!.Metal);
    }

    // --- ResourceTrickle ---

    [Fact]
    public void ResourceTrickle_PaysEachInterval()
    {
        var cm = SetupWorld();
        var wonder = MakeEntity(cm, 1);
        // 奇观率:1.0×4 / 2000ms。
        var trickle = new ResourceTrickleComponent
        {
            IntervalMs = 2000f,
            FoodRate = 1f, WoodRate = 1f, StoneRate = 1f, MetalRate = 1f
        };
        cm.AddComponent(wonder, trickle);

        var player = cm.Players.GetPlayerEntity(1)!;
        int f0 = player.Food, m0 = player.Metal;
        for (int i = 0; i < 19; i++) trickle.Tick(cm, 0.1f);  // 1.9s → 未到
        Assert.Equal(f0, player.Food);
        trickle.Tick(cm, 0.1f);                               // 2.0s → 第一次发放
        Assert.Equal(f0 + 1, player.Food);
        Assert.Equal(m0 + 1, player.Metal);
        for (int i = 0; i < 20; i++) trickle.Tick(cm, 0.1f);  // 4.0s → 第二次
        Assert.Equal(f0 + 2, player.Food);
    }

    [Fact]
    public void ResourceTrickle_FractionalRate_CarriesRemainder()
    {
        var cm = SetupWorld();
        var e = MakeEntity(cm, 1);
        var trickle = new ResourceTrickleComponent { IntervalMs = 1000f, FoodRate = 0.5f };
        cm.AddComponent(e, trickle);

        var player = cm.Players.GetPlayerEntity(1)!;
        int f0 = player.Food;
        for (int i = 0; i < 10; i++) trickle.Tick(cm, 0.1f);  // 1s → 0.5 结转,不发
        Assert.Equal(f0, player.Food);
        for (int i = 0; i < 10; i++) trickle.Tick(cm, 0.1f);  // 2s → 0.5+0.5=1 发放
        Assert.Equal(f0 + 1, player.Food);
    }

    [Fact]
    public void ResourceTrickle_ZeroIntervalOrRates_NoOp()
    {
        var cm = SetupWorld();
        var e = MakeEntity(cm, 1);
        var trickle = new ResourceTrickleComponent { IntervalMs = 0f, FoodRate = 5f };
        cm.AddComponent(e, trickle);
        var player = cm.Players.GetPlayerEntity(1)!;
        int f0 = player.Food;
        for (int i = 0; i < 50; i++) trickle.Tick(cm, 0.1f);
        Assert.Equal(f0, player.Food);
    }

    // --- StatusEffectsReceiver ---

    private static ActiveStatusEffect BurnEffect(float durationMs, float intervalMs, int dmgPerTick,
        string stackability = "Ignore")
    {
        var fx = new ActiveStatusEffect
        {
            DurationMs = durationMs,
            IntervalMs = intervalMs,
            Stackability = stackability,
        };
        fx.Damage.Amounts[DamageType.Hack] = dmgPerTick;
        return fx;
    }

    [Fact]
    public void AddStatus_StackabilityRules()
    {
        var cm = SetupWorld();
        var e = MakeEntity(cm, 1);
        var receiver = new StatusEffectsReceiverComponent();
        cm.AddComponent(e, receiver);
        var src = MakeEntity(cm, 2);

        // Ignore:同名再施加 → 拒。
        Assert.NotNull(receiver.AddStatus(cm, "Burn", BurnEffect(3000, 1000, 5), src, 2));
        Assert.Null(receiver.AddStatus(cm, "Burn", BurnEffect(3000, 1000, 5), src, 2));
        Assert.Single(receiver.ActiveStatuses);

        // Extend:时长累加。
        var first = receiver.ActiveStatuses["Burn"];
        receiver.RemoveStatus(cm, "Burn");
        receiver.AddStatus(cm, "Burn", BurnEffect(3000, 1000, 5, "Extend"), src, 2);
        Assert.NotNull(receiver.AddStatus(cm, "Burn", BurnEffect(2000, 1000, 5, "Extend"), src, 2));
        Assert.Equal(5000f, receiver.ActiveStatuses["Burn"].DurationMs);

        // Replace:旧的撤掉换新的。
        receiver.AddStatus(cm, "Burn", BurnEffect(900, 1000, 5, "Replace"), src, 2);
        Assert.Equal(900f, receiver.ActiveStatuses["Burn"].DurationMs);

        // Stack:另起后缀键。
        Assert.Equal("Burn_0", receiver.AddStatus(cm, "Burn", BurnEffect(1000, 1000, 5, "Stack"), src, 2));
        Assert.Equal("Burn_1", receiver.AddStatus(cm, "Burn", BurnEffect(1000, 1000, 5, "Stack"), src, 2));
        Assert.Equal(3, receiver.ActiveStatuses.Count);
    }

    [Fact]
    public void StatusEffect_DamageOverTime_ThroughResistancePipeline()
    {
        var cm = SetupWorld();
        var victim = MakeEntity(cm, 1);
        cm.AddComponent(victim, new HealthComponent { Current = 100, Max = 100 });
        var receiver = new StatusEffectsReceiverComponent();
        cm.AddComponent(victim, receiver);
        var src = MakeEntity(cm, 2);

        receiver.AddStatus(cm, "Burn", BurnEffect(10000, 1000, 10), src, 2);

        for (int i = 0; i < 10; i++) receiver.Tick(cm, 0.1f);  // 1s → 第一次灼烧(10 伤)
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(90, cm.QueryInterface<HealthComponent>(victim)!.Current);
        for (int i = 0; i < 10; i++) receiver.Tick(cm, 0.1f);  // 2s → 第二次
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(80, cm.QueryInterface<HealthComponent>(victim)!.Current);
    }

    [Fact]
    public void StatusEffect_ExpiresAfterDuration_RemovesModifiers()
    {
        var cm = SetupWorld();
        var victim = MakeEntity(cm, 1);
        cm.AddComponent(victim, new HealthComponent { Current = 100, Max = 100 });
        var receiver = new StatusEffectsReceiverComponent();
        cm.AddComponent(victim, receiver);
        var src = MakeEntity(cm, 2);

        var fx = BurnEffect(2000, 1000, 0);
        fx.Damage.Amounts.Clear();   // 无伤害,仅修饰
        fx.Mods.Add(new Modification("Health/Max", null, 2f, null, System.Array.Empty<string>()));
        receiver.AddStatus(cm, "Enraged", fx, src, 2);

        // 修饰生效:Health/Max ×2。
        float modded = cm.Modifiers.Apply("Health/Max", 100f, victim);
        Assert.Equal(200f, modded);

        // 推进 3s(> 2s 时长)→ 状态过期,修饰撤除。
        for (int i = 0; i < 30; i++) receiver.Tick(cm, 0.1f);
        Assert.Empty(receiver.ActiveStatuses);
        Assert.Equal(100f, cm.Modifiers.Apply("Health/Max", 100f, victim));
    }

    // --- 攻击施加源(Attack/ApplyStatus → DelayedDamage → AddStatus) ---

    [Fact]
    public void PerformAttack_AppliesStatusEffect_OnHit()
    {
        var cm = SetupWorld();
        var attacker = MakeEntity(cm, 1);
        var atk = new AttackComponent();
        cm.AddComponent(attacker, atk);
        atk.Damage.Amounts[DamageType.Hack] = 5;
        atk.StatusEffectName = "Burning";
        atk.StatusEffectDurationMs = 2000;
        atk.StatusEffectIntervalMs = 1000;
        atk.StatusEffectStackability = "Replace";
        atk.StatusEffectDmgFire = 2;

        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new HealthComponent { Current = 100, Max = 100 });
        var receiver = new StatusEffectsReceiverComponent();
        cm.AddComponent(victim, receiver);

        atk.Target = victim;
        atk.PerformAttack(cm);
        cm.DelayedDamage.AdvanceTurn();
        cm.DelayedDamage.TickPending(cm);

        Assert.True(receiver.ActiveStatuses.ContainsKey("Burning"));
        var fx = receiver.ActiveStatuses["Burning"];
        Assert.Equal(attacker, fx.SourceEntity);
        Assert.Equal(1, fx.SourceOwner);
        Assert.Equal(2, fx.Damage.Get(DamageType.Fire));
        Assert.Equal(2000f, fx.DurationMs);
    }

    [Fact]
    public void PerformAttack_NoStatusConfigured_NothingApplied()
    {
        var cm = SetupWorld();
        var attacker = MakeEntity(cm, 1);
        var atk = new AttackComponent();
        cm.AddComponent(attacker, atk);
        atk.Damage.Amounts[DamageType.Hack] = 5;

        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new HealthComponent { Current = 100, Max = 100 });
        var receiver = new StatusEffectsReceiverComponent();
        cm.AddComponent(victim, receiver);

        atk.Target = victim;
        atk.PerformAttack(cm);
        cm.DelayedDamage.AdvanceTurn();
        cm.DelayedDamage.TickPending(cm);

        Assert.Empty(receiver.ActiveStatuses);
        Assert.Equal(95, cm.QueryInterface<HealthComponent>(victim)!.Current);
    }

    // --- Loot/xp 按比例(原版 Health.js:xp = Loot/xp × 实际扣血 / maxHp) ---

    [Fact]
    public void KillXp_ProportionalToDealtOverMaxHp()
    {
        var cm = SetupWorld();
        var attacker = MakeEntity(cm, 1);
        var promo = new PromotionComponent();
        cm.AddComponent(attacker, promo);

        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new HealthComponent { Current = 50, Max = 100 });
        cm.AddComponent(victim, new LootComponent { Xp = 40 });

        // 打 25(实际扣 25/100):xp = floor(40 × 25/100) = 10。
        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Hack] = 25;
        DelayedDamage.ScheduleHit(cm, attacker, victim, dmg, 0);
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(10, promo.XP);
        Assert.Equal(1, promo.Level);

        // 补 50(只剩 25 可扣,目标死):xp = floor(40 × 25/100) = 10 → 累计 20 → 升级。
        var dmg2 = new DamageBlock();
        dmg2.Amounts[DamageType.Hack] = 50;
        DelayedDamage.ScheduleHit(cm, attacker, victim, dmg2, 0);
        cm.DelayedDamage.TickPending(cm);
        Assert.True(cm.QueryInterface<HealthComponent>(victim)!.IsDead);
        Assert.Equal(2, promo.Level);
        Assert.Equal(0, promo.XP);
    }

    [Fact]
    public void KillXp_NoLootComponent_NoXp()
    {
        var cm = SetupWorld();
        var attacker = MakeEntity(cm, 1);
        var promo = new PromotionComponent();
        cm.AddComponent(attacker, promo);
        var victim = MakeEntity(cm, 2);
        cm.AddComponent(victim, new HealthComponent { Current = 100, Max = 100 });

        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Hack] = 30;
        DelayedDamage.ScheduleHit(cm, attacker, victim, dmg, 0);
        cm.DelayedDamage.TickPending(cm);

        Assert.Equal(0, promo.XP);
        Assert.Equal(1, promo.Level);
    }

    // --- Foundation 多工人递减(n^0.7/n,与 Repairable 同源) ---

    [Fact]
    public void Foundation_BuildMultiplier_DiminishingReturns_MatchesUpstream()
    {
        Assert.Equal(1f, FoundationComponent.CalculateBuildMultiplier(0));
        Assert.Equal(1f, FoundationComponent.CalculateBuildMultiplier(1));
        // 原版:2^0.7 / 2 ≈ 0.8123;10^0.7 / 10 ≈ 0.5012
        Assert.Equal(0.8123f, FoundationComponent.CalculateBuildMultiplier(2), 3);
        Assert.Equal(0.5012f, FoundationComponent.CalculateBuildMultiplier(10), 3);
    }

    [Fact]
    public void Foundation_TwoBuilders_ProgressScalesByMultiplier()
    {
        var cm = SetupWorld();
        var site = MakeEntity(cm, 1);
        var fdn = new FoundationComponent();
        cm.AddComponent(site, fdn);
        fdn.Configure("structures/test", 10f);
        var w1 = MakeEntity(cm, 1);
        var w2 = MakeEntity(cm, 1);

        fdn.AddBuilder(w1, 1f);
        Assert.True(fdn.Build(w1, 1f, 1f) == false);
        Assert.Equal(1f, fdn.Progress, 3);   // 单人:mult=1 → +1

        fdn.AddBuilder(w2, 1f);
        Assert.Equal(2, fdn.NumBuilders);
        fdn.Build(w1, 1f, 1f);
        fdn.Build(w2, 1f, 1f);               // 双人:各 +0.8123
        Assert.Equal(1f + 2f * 0.8123f, fdn.Progress, 3);

        fdn.RemoveBuilder(w1);
        Assert.Equal(1f, fdn.BuildMultiplier);
        Assert.Equal(1, fdn.NumBuilders);
    }

    [Fact]
    public void BuilderTick_FoundationRegistersAndUnregistersOnCompletion()
    {
        var cm = SetupWorld();
        var site = MakeEntity(cm, 1, 5, 0);   // 距工人 5m(< 8m 工位半径)
        var fdn = new FoundationComponent();
        cm.AddComponent(site, fdn);
        fdn.Configure("structures/test", 100f);

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        var b = new BuilderComponent { BuildSpeed = 1f };
        cm.AddComponent(worker, b);
        b.Build(site);

        b.Tick(cm);   // 进工位 → 入表
        Assert.Equal(1, fdn.NumBuilders);
        Assert.True(fdn.Progress > 0);

        // 直接 Build 到满 → 下一 tick 清登记 + 清目标。
        while (!fdn.IsBuilt) fdn.Build(worker, 50f, 1f);
        b.Tick(cm);
        Assert.Null(b.Target);
        Assert.Equal(0, fdn.NumBuilders);
    }

    [Fact]
    public void BuilderTick_FoundationUnregisteredWhenWalkingAway()
    {
        var cm = SetupWorld();
        var site = MakeEntity(cm, 1, 5, 0);
        var fdn = new FoundationComponent();
        cm.AddComponent(site, fdn);
        fdn.Configure("structures/test", 100f);

        var worker = MakeEntity(cm, 1, 0, 0);
        cm.AddComponent(worker, new UnitMotion());
        var b = new BuilderComponent { BuildSpeed = 1f };
        cm.AddComponent(worker, b);
        b.Build(site);

        b.Tick(cm);   // 进工位 → 入表
        Assert.Equal(1, fdn.NumBuilders);

        // 把工人搬走 → 下一 tick 出工人表。
        var pos = cm.QueryInterface<PositionComponent>(worker)!;
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(100), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero);
        b.Tick(cm);
        Assert.Equal(0, fdn.NumBuilders);
        Assert.Equal(1f, fdn.BuildMultiplier);
    }
}
