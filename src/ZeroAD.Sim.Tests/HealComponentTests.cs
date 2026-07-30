using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// HealComponent — port of Heal.js. A healer restores HP to an injured allied unit in range,
// one tick every Interval after a 1 s prepare; the interval since the last heal is respected
// across target switches (repeatLeft), matching the original's timer semantics.
public sealed class HealComponentTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        string classes = "Support Infantry", int hp = 100, int maxHp = 100)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        var health = new HealthComponent();
        cm.AddComponent(e, health);
        health.Max = maxHp;          // 赋值须在 AddComponent 之后(OnInit 语义陷阱)
        health.Current = hp;
        if (player >= 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static HealComponent AddHealer(ComponentManager cm, EntityId e,
        int amount = 5, float range = 12f, float interval = 2f)
    {
        var heal = new HealComponent { HealAmount = amount, Range = range, Rate = interval };
        cm.AddComponent(e, heal);
        heal.HealableClasses.Add("Support");
        heal.HealableClasses.Add("Infantry");
        return heal;
    }

    // --- CanHeal 校验矩阵(对齐 Heal.js CanHeal) ---

    [Fact]
    public void CanHeal_InjuredAllyInClass_True()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40);
        var heal = AddHealer(cm, healer);

        Assert.True(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_FullHealth_False()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 100);      // 满血 → IsInjured false
        var heal = AddHealer(cm, healer);

        Assert.False(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_EnemyTarget_False()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm, player: 1);
        var target = MakeUnit(cm, player: 2, hp: 40);   // 无外交注册 → 非互盟
        var heal = AddHealer(cm, healer);

        Assert.False(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_MutualAlly_True()
    {
        var cm = new ComponentManager(rngSeed: 1);
        // 同队(≥0)→ 互盟(SeedDiplomacyFromTeams,Auras/领土同款 harness)。
        foreach (var pid in new[] { 1, 2 })
        {
            var pe = cm.CreateEntity();
            cm.AddComponent(pe, new PlayerComponent());
            cm.AddComponent(pe, new DiplomacyComponent());
            cm.Players.AddPlayer(pid, pe);
        }
        cm.Players.SeedDiplomacyFromTeams(new System.Collections.Generic.Dictionary<int, int> { [1] = 0, [2] = 0 });

        var healer = MakeUnit(cm, player: 1);
        var target = MakeUnit(cm, player: 2, hp: 40);
        var heal = AddHealer(cm, healer);

        Assert.True(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_UnhealableClassWins_False()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40, classes: "Support Cavalry");
        var heal = AddHealer(cm, healer);
        heal.UnhealableClasses.Add("Cavalry");   // 原版:unhealable 优先于 healable

        Assert.False(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_MissingHealableClass_False()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40, classes: "Cavalry");
        var heal = AddHealer(cm, healer);

        Assert.False(heal.CanHeal(cm, target));
    }

    [Fact]
    public void CanHeal_UnhealableFlag_False()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40);
        cm.QueryInterface<HealthComponent>(target)!.Unhealable = true;
        var heal = AddHealer(cm, healer);

        Assert.False(heal.CanHeal(cm, target));
    }

    // --- 计时器语义(prepare 1s + repeat Interval + repeatLeft 冷却) ---

    [Fact]
    public void Tick_HealsAfterPrepare_ThenEveryInterval()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40, x: 3f);    // 距 3m,range 12 内
        var heal = AddHealer(cm, healer);

        Assert.True(heal.StartHealing(cm, target));

        // prepare = 1s:1s 前不治疗。
        Assert.Equal(HealTickResult.Healing, heal.Tick(0.5f, cm));
        Assert.Equal(40, cm.QueryInterface<HealthComponent>(target)!.Current);

        Assert.Equal(HealTickResult.Healing, heal.Tick(0.6f, cm));   // 累计 1.1s → 首次治疗
        Assert.Equal(45, cm.QueryInterface<HealthComponent>(target)!.Current);

        // repeat = 2s:下次治疗在 Elapsed 3.0(prepare 1.0 + interval 2.0)。
        Assert.Equal(HealTickResult.Healing, heal.Tick(1.0f, cm));   // Elapsed 2.1 → 不治疗
        Assert.Equal(45, cm.QueryInterface<HealthComponent>(target)!.Current);
        heal.Tick(1.0f, cm);                                          // Elapsed 3.1 → 第二次
        Assert.Equal(50, cm.QueryInterface<HealthComponent>(target)!.Current);
    }

    [Fact]
    public void Tick_FullyHealed_TargetInvalidAndStops()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 97, x: 3f);   // 差 3 HP,一次 5 点补满
        var heal = AddHealer(cm, healer);

        heal.StartHealing(cm, target);
        Assert.Equal(HealTickResult.TargetInvalid, heal.Tick(1.1f, cm));
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(target)!.Current);
        Assert.Null(heal.Target);                  // StopHealing 已清目标
    }

    [Fact]
    public void Tick_TargetMovesOutOfRange_OutOfRange()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40, x: 3f);
        var heal = AddHealer(cm, healer, range: 4f);

        heal.StartHealing(cm, target);
        Assert.Equal(HealTickResult.Healing, heal.Tick(0.5f, cm));

        // 目标走出射程。
        cm.QueryInterface<PositionComponent>(target)!.Position =
            new FixedVector3D(Fixed.FromFloat(30f), Fixed.Zero, Fixed.Zero);
        Assert.Equal(HealTickResult.OutOfRange, heal.Tick(0.6f, cm));
        Assert.Null(heal.Target);
    }

    [Fact]
    public void StartHealing_RespectsRepeatCooldown()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var t1 = MakeUnit(cm, hp: 40, x: 3f);
        var t2 = MakeUnit(cm, hp: 40, x: -3f);
        var heal = AddHealer(cm, healer, interval: 2f);

        // 对 t1 完成一次治疗后立刻换 t2:prepare 应延长为 repeatLeft(≈2s)而非默认 1s。
        heal.StartHealing(cm, t1);
        heal.Tick(1.1f, cm);                        // t1 首次治疗(时刻 1.1s)
        Assert.Equal(45, cm.QueryInterface<HealthComponent>(t1)!.Current);

        Assert.True(heal.StartHealing(cm, t2));     // 换目标
        heal.Tick(1.5f, cm);                        // 累计 1.5s > 默认 1s,但 < repeatLeft 2s
        Assert.Equal(40, cm.QueryInterface<HealthComponent>(t2)!.Current);
        heal.Tick(0.6f, cm);                        // 累计 2.1s ≥ repeatLeft
        Assert.Equal(45, cm.QueryInterface<HealthComponent>(t2)!.Current);
    }

    [Fact]
    public void StartHealing_InvalidTarget_ReturnsFalse()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 100);         // 满血
        var heal = AddHealer(cm, healer);

        Assert.False(heal.StartHealing(cm, target));
        Assert.Null(heal.Target);
    }

    // --- 序列化(二进制位置流往返,防读序错位) ---

    [Fact]
    public void RoundTrip_PreservesTimerState()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm);
        var target = MakeUnit(cm, hp: 40, x: 3f);
        var heal = AddHealer(cm, healer, amount: 7, range: 9.5f, interval: 1.5f);
        heal.StartHealing(cm, target);
        heal.Tick(0.7f, cm);

        var ms = new System.IO.MemoryStream();
        heal.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new HealComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.Equal(7, back.HealAmount);
        Assert.Equal(9.5f, back.Range, 3);
        Assert.Equal(1.5f, back.Rate, 3);
        Assert.Equal(new[] { "Support", "Infantry" }, back.HealableClasses);
        Assert.Equal(target, back.Target);
        Assert.Equal(heal.Prepare, back.Prepare, 3);
        Assert.Equal(heal.Elapsed, back.Elapsed, 3);
        Assert.Equal(heal.SinceLastHeal, back.SinceLastHeal, 3);
    }

    // --- UnitAI 集成(Order.Heal → APPROACHING → HEALING → HP 上升) ---

    [Fact]
    public void UnitAI_HealOrder_ApproachesAndHeals()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm, x: 0f, z: 0f);
        var target = MakeUnit(cm, hp: 40, x: 6f, z: 0f);   // 6m,range 12 → 边缘内
        var heal = AddHealer(cm, healer);
        var ai = new UnitAIComponent();
        cm.AddComponent(healer, ai);

        ai.Heal(target);
        ai.Tick(0.1f, cm);                          // 派发 Order.Heal
        Assert.StartsWith("INDIVIDUAL.HEAL", ai.FsmStateName);

        for (int i = 0; i < 100 && cm.QueryInterface<HealthComponent>(target)!.Current == 40; i++)
        {
            cm.QueryInterface<UnitMotion>(healer)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }
        Assert.True(cm.QueryInterface<HealthComponent>(target)!.Current > 40,
            $"expected healing; state={ai.FsmStateName} hp={cm.QueryInterface<HealthComponent>(target)!.Current}");
        Assert.Equal("INDIVIDUAL.HEAL.HEALING", ai.FsmStateName);
    }

    [Fact]
    public void UnitAI_HealRejectedWithoutHealer_FinishesOrder_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);                    // 无 HealComponent → 拒收
        var target = MakeUnit(cm, hp: 40, x: 5f);
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.Heal(target);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }

    [Fact]
    public void UnitAI_HealEnemy_Rejected()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var healer = MakeUnit(cm, player: 1);
        var target = MakeUnit(cm, player: 2, hp: 40, x: 5f);
        AddHealer(cm, healer);
        var ai = new UnitAIComponent();
        cm.AddComponent(healer, ai);

        ai.Heal(target);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);   // CanHeal false → StartHealing false → FinishOrder
    }
}
