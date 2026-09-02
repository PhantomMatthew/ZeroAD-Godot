using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

// Guard 双向(原版 Guard.js + UnitAI.js GuardedAttacked):
// 护卫订单登记到被护方 GuardComponent;被护方受击 → 护卫前插反击订单;
// 外交翻面(互盟破裂)→ 护卫关系摘除。
public sealed class GuardTests
{
    private static (ComponentManager cm, EntityId guard, EntityId guarded, EntityId attacker) World()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.AddComponent(p1, new DiplomacyComponent());
        cm.Players.AddPlayer(1, p1);

        EntityId Mk(int x)
        {
            var e = cm.CreateEntity();
            var pos = new PositionComponent();
            cm.AddComponent(e, pos);
            pos.Position = new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero);
            cm.AddComponent(e, new IdentityComponent
            {
                TemplateName = "units/athen/infantry_spearman_b",
                IsUnit = true,
                Classes = new List<string> { "Unit" },
            });
            cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
            cm.AddComponent(e, new UnitMotion());
            cm.AddComponent(e, new UnitAIComponent());
            cm.AddComponent(e, new OwnershipComponent { PlayerId = 1 });
            return e;
        }
        var guard = Mk(10);
        var guarded = Mk(20);
        cm.AddComponent(guarded, new GuardComponent());
        var attacker = Mk(30);
        return (cm, guard, guarded, attacker);
    }

    [Fact]
    public void GuardOrder_RegistersOnGuarded_AndAttacked_GuardRetaliates()
    {
        var (cm, guard, guarded, attacker) = World();

        var ai = cm.QueryInterface<UnitAIComponent>(guard)!;
        ai.Guard(guarded);
        ai.Tick(0.1f, cm);   // dispatch Order.Guard

        var gc = cm.QueryInterface<GuardComponent>(guarded)!;
        Assert.Contains(guard, gc.Entities);

        // 被护方受击 → 护卫前插反击订单(原版:可见 → Attack;不可见 →
        // WalkAndFight 到攻击者位置。本夹具无 LOS 体系 → 走 WalkAndFight 分支)。
        gc.NotifyAttacked(cm, attacker);
        var order = ai.CurrentOrder;
        Assert.NotNull(order);
        Assert.True(order!.Type is "Attack" or "WalkAndFight",
            $"expected retaliation order, got {order.Type}");
        Assert.Equal(attacker, order.Target);

        // 停卫 → 双向摘除。
        ai.RemoveGuard(cm);
        Assert.DoesNotContain(guard, gc.Entities);
    }

    [Fact]
    public void DiplomacyFlip_RemovesGuardFromList()
    {
        var (cm, guard, guarded, _) = World();
        var ai = cm.QueryInterface<UnitAIComponent>(guard)!;
        ai.Guard(guarded);
        ai.Tick(0.1f, cm);
        var gc = cm.QueryInterface<GuardComponent>(guarded)!;
        Assert.Contains(guard, gc.Entities);

        // 互盟破裂(双方互设敌)→ CheckGuards 摘除。
        var pEnt = cm.Players.GetPlayerEntityId(1)!.Value;
        var dip = cm.QueryInterface<DiplomacyComponent>(pEnt)!;
        dip.SetStanceToward(1, dip, 1, DiplomacyComponent.Enemy);
        // 事件广播驱动 CheckGuards(DiplomacyChanged 订阅)。
        gc.CheckGuards(cm);
        Assert.DoesNotContain(guard, gc.Entities);
        Assert.False(ai.IsGuardOf(guarded));
    }
}
