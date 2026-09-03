using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>Pickup 接送(原版 UnitAI.js pickup 体系)测试:乘客发起 → 持有者插
/// PickupUnit 单 → 接近/等待 → 入驻完成握手(取消即完成)。</summary>
public sealed class PickupTests
{
    private static (ComponentManager cm, EntityId transport, EntityId passenger) World(
        float transportX, float passengerX)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);

        var transport = cm.CreateEntity();
        cm.AddComponent(transport, new PositionComponent());
        cm.QueryInterface<PositionComponent>(transport)!.Position =
            new FixedVector3D(Fixed.FromFloat(transportX), Fixed.Zero, Fixed.Zero);
        cm.AddComponent(transport, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(transport, new UnitMotion());
        cm.AddComponent(transport, new UnitAIComponent());
        cm.AddComponent(transport, new IdentityComponent { IsUnit = true });
        var holder = new GarrisonHolderComponent
        { Pickup = true, LoadingRange = 4f, Max = 5 };
        cm.AddComponent(transport, holder);
        holder.AllowedClasses.Add("Infantry");

        var passenger = cm.CreateEntity();
        cm.AddComponent(passenger, new PositionComponent());
        cm.QueryInterface<PositionComponent>(passenger)!.Position =
            new FixedVector3D(Fixed.FromFloat(passengerX), Fixed.Zero, Fixed.Zero);
        cm.AddComponent(passenger, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(passenger, new UnitMotion());
        cm.AddComponent(passenger, new UnitAIComponent());
        var pid = new IdentityComponent { IsUnit = true };
        cm.AddComponent(passenger, pid);
        pid.Classes.Add("Infantry");
        cm.AddComponent(passenger, new GarrisonableComponent());
        return (cm, transport, passenger);
    }

    private static void TickAll(ComponentManager cm, int turns)
    {
        for (int i = 0; i < turns; i++)
            foreach (var e in cm.AllEntities)
                cm.QueryInterface<UnitAIComponent>(e)?.Tick(0.1f, cm);
    }

    [Fact]
    public void PassengerRequest_InsertsPickupOrder_OnHolder()
    {
        var (cm, transport, passenger) = World(0f, 100f);
        cm.QueryInterface<UnitAIComponent>(passenger)!.Garrison(transport);
        TickAll(cm, 2);   // 指令分发 → APPROACHING + pickup 登记

        var tai = cm.QueryInterface<UnitAIComponent>(transport)!;
        Assert.True(tai.HasPickupOrder(passenger), "holder should have a PickupUnit order");
        var order = tai.CurrentOrder;
        Assert.NotNull(order);
        Assert.Equal("PickupUnit", order!.Type);
        Assert.Equal(passenger, order.Target);
    }

    [Fact]
    public void NoPickupTemplate_NoPickupOrder()
    {
        var (cm, transport, passenger) = World(0f, 100f);
        cm.QueryInterface<GarrisonHolderComponent>(transport)!.Pickup = false;
        cm.QueryInterface<UnitAIComponent>(passenger)!.Garrison(transport);
        TickAll(cm, 2);
        Assert.False(cm.QueryInterface<UnitAIComponent>(transport)!.HasPickupOrder(passenger));
    }

    [Fact]
    public void CloseBy_GoesStraightToLoading_ThenCompletes()
    {
        // 乘客 100m 内(<200)且能走 → 运输船原地 LOADING;乘客走近自动入驻 →
        // 双方收单(取消握手)。
        var (cm, transport, passenger) = World(0f, 30f);
        cm.QueryInterface<UnitAIComponent>(passenger)!.Garrison(transport);
        TickAll(cm, 3);
        var tai = cm.QueryInterface<UnitAIComponent>(transport)!;
        Assert.True(tai.HasPickupOrder(passenger));
        // 运输船不动(LOADING 等待)。
        var tPos0 = cm.QueryInterface<PositionComponent>(transport)!.Position;
        TickAll(cm, 5);
        var tPos1 = cm.QueryInterface<PositionComponent>(transport)!.Position;
        Assert.Equal(tPos0.X, tPos1.X);

        // 乘客走 UnitMotion 逼近(无寻路 → 直线);走完进 GARRISONING → 入驻。
        for (int i = 0; i < 400; i++)
        {
            TickAll(cm, 1);
            foreach (var e in cm.AllEntities)
                cm.QueryInterface<UnitMotion>(e)?.Tick(0.1f);
            if (cm.QueryInterface<UnitAIComponent>(passenger)!.IsGarrisoned) break;
        }
        Assert.True(cm.QueryInterface<UnitAIComponent>(passenger)!.IsGarrisoned,
            "passenger should be garrisoned");
        TickAll(cm, 2);
        Assert.False(tai.HasPickupOrder(passenger),
            "pickup order completed via cancel-handshake");
        Assert.Contains(passenger, cm.QueryInterface<GarrisonHolderComponent>(transport)!.Entities);
    }

    [Fact]
    public void FarAway_TransportApproaches()
    {
        // 乘客 300m 外(>200) → 运输船 APPROACHING 主动接近。
        var (cm, transport, passenger) = World(0f, 300f);
        cm.QueryInterface<UnitAIComponent>(passenger)!.Garrison(transport);
        TickAll(cm, 3);
        var tai = cm.QueryInterface<UnitAIComponent>(transport)!;
        Assert.True(tai.HasPickupOrder(passenger));
        float x0 = cm.QueryInterface<PositionComponent>(transport)!.Position.X.ToFloat();
        for (int i = 0; i < 30; i++)
        {
            TickAll(cm, 1);
            cm.QueryInterface<UnitMotion>(transport)?.Tick(0.1f);
        }
        float x1 = cm.QueryInterface<PositionComponent>(transport)!.Position.X.ToFloat();
        Assert.True(x1 > x0 + 1f, $"transport should approach passenger (x: {x0} → {x1})");
    }

    [Fact]
    public void PassengerAborted_RemovesPickupOrder()
    {
        var (cm, transport, passenger) = World(0f, 100f);
        cm.QueryInterface<UnitAIComponent>(passenger)!.Garrison(transport);
        TickAll(cm, 2);
        var tai = cm.QueryInterface<UnitAIComponent>(transport)!;
        Assert.True(tai.HasPickupOrder(passenger));

        // 乘客改道(新指令顶掉 Garrison → APPROACHING leave → 取消握手)。
        cm.QueryInterface<UnitAIComponent>(passenger)!
            .Walk(new FixedVector2D(Fixed.FromInt(500), Fixed.FromInt(500)));
        TickAll(cm, 3);
        Assert.False(tai.HasPickupOrder(passenger), "cancel handshake should drop the pickup order");
    }
}
