using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

// 偏好索敌(原版 Attack.js GetPreference + UnitAI.js AttackEntitiesByPreference):
// 攻击件 PreferredClasses 命中最小下标者优先;同偏好内最近;无偏好垫底。
public sealed class AttackPreferenceTests
{
    [Fact]
    public void GetPreference_MinIndexAcrossTypes()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var target = cm.CreateEntity();
        cm.AddComponent(target, new IdentityComponent
        {
            TemplateName = "x",
            Classes = new List<string> { "Unit", "Cavalry" },
        });

        var attacker = cm.CreateEntity();
        var atk = new AttackComponent();
        atk.Types.Add(new AttackComponent.AttackTypeSpec { Name = "Melee", PreferredClasses = "Human Cavalry" });
        cm.AddComponent(attacker, atk);

        // Cavalry 命中下标 1(Human 不中)。
        Assert.Equal(1, atk.GetPreference(cm, target));

        // 无命中 → null。
        var other = cm.CreateEntity();
        cm.AddComponent(other, new IdentityComponent
        { TemplateName = "y", Classes = new List<string> { "Structure" } });
        Assert.Null(atk.GetPreference(cm, other));

        // 下标 0 命中 → 短路 0。
        var pref0 = cm.CreateEntity();
        cm.AddComponent(pref0, new IdentityComponent
        { TemplateName = "z", Classes = new List<string> { "Unit", "Human" } });
        Assert.Equal(0, atk.GetPreference(cm, pref0));
    }

    // --- ScanAndEngage(WAF/巡逻/GUARDING 共用)走偏好排序(不再是 enemies[0]) ---

    private static (ComponentManager cm, RangeManager rm) NewLosWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(rm);
        // 无 DiplomacyComponent:IsEnemy 默认异主为敌(同 StanceTests)。
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        return (cm, rm);
    }

    private static EntityId SpawnUnit(ComponentManager cm, RangeManager rm,
        int x, int z, int owner, string classes, string? preferredClasses = null)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' '));
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        var atk = new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 5), Range = 4f };
        if (preferredClasses != null)
            atk.Types.Add(new AttackComponent.AttackTypeSpec
            { Name = "Melee", PreferredClasses = preferredClasses });
        cm.AddComponent(e, atk);
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new VisionComponent());
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    [Fact]
    public void ScanAndEngage_PrefersPreferredClass_OverCloserEnemy()
    {
        var (cm, rm) = NewLosWorld();
        // 攻击者偏好 Cavalry;骑兵更远(8m),步兵更近(3m)——偏好应压过距离。
        var u = SpawnUnit(cm, rm, 10, 10, owner: 1, "Unit Infantry", preferredClasses: "Cavalry");
        var nearInfantry = SpawnUnit(cm, rm, 13, 10, owner: 2, "Unit Infantry");
        var farCavalry = SpawnUnit(cm, rm, 18, 10, owner: 2, "Unit Cavalry");
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        ai.WalkAndFight(new FixedVector2D(Fixed.FromInt(40), Fixed.FromInt(10)));
        ai.Tick(0.1f, cm);   // 派发 → WALKINGANDFIGHTING
        ai.Tick(1.0f, cm);   // 1s 节流索敌 → 前插 Attack

        var order = ai.CurrentOrder;
        Assert.Equal("Attack", order?.Type);
        Assert.Equal(farCavalry, order!.Target);
        Assert.NotEqual(nearInfantry, order.Target);
    }

    [Fact]
    public void ScanAndEngage_NoPreference_PicksNearest()
    {
        var (cm, rm) = NewLosWorld();
        // 无 PreferredClasses(全 null 偏好垫底)→ 退化最近优先(定点距离比较)。
        var u = SpawnUnit(cm, rm, 10, 10, owner: 1, "Unit Infantry");
        var near = SpawnUnit(cm, rm, 13, 10, owner: 2, "Unit Cavalry");
        SpawnUnit(cm, rm, 18, 10, owner: 2, "Unit Infantry");
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        ai.WalkAndFight(new FixedVector2D(Fixed.FromInt(40), Fixed.FromInt(10)));
        ai.Tick(0.1f, cm);
        ai.Tick(1.0f, cm);

        var order = ai.CurrentOrder;
        Assert.Equal("Attack", order?.Type);
        Assert.Equal(near, order!.Target);
    }
}
