using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// PackComponent — port of Pack.js. Pack/Unpack accumulates ElapsedTime; at PackTime the entity
// flips Packed and transforms into the Pack/Entity template (new entity, same position/owner,
// health fraction preserved — mirrors the engine's ChangeEntityTemplate). UnitAI's
// PACKING/UNPACKING states drive Tick; leaving the state cancels progress (leave hook).
public sealed class PackComponentTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        int hp = 100, int maxHp = 100)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var health = new HealthComponent();
        cm.AddComponent(e, health);
        health.Max = maxHp;
        health.Current = hp;
        if (player >= 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    [Fact]
    public void Pack_AccumulatesProgress_ThenFlipsPacked()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var pack = new PackComponent { PackTime = 1.0f };
        cm.AddComponent(unit, pack);

        Assert.True(pack.CanPack());
        pack.Pack();
        Assert.True(pack.Packing);

        Assert.False(pack.Tick(0.4f, cm));          // 0.4 < 1.0
        Assert.True(pack.Packing);
        Assert.False(pack.Packed);

        Assert.True(pack.Tick(0.7f, cm));           // 1.1 ≥ 1.0 → 完成
        Assert.False(pack.Packing);
        Assert.True(pack.Packed);
        Assert.Equal(0f, pack.ElapsedTime);
    }

    [Fact]
    public void Unpack_FromPacked_FlipsBack()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var pack = new PackComponent { PackTime = 1.0f, Packed = true };
        cm.AddComponent(unit, pack);

        Assert.False(pack.CanPack());
        Assert.True(pack.CanUnpack());
        pack.Unpack();
        Assert.True(pack.Tick(1.1f, cm));
        Assert.False(pack.Packed);
        Assert.False(pack.Packing);
    }

    [Fact]
    public void Guards_PackWhilePackingOrPacked_NoOp()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var pack = new PackComponent { PackTime = 1.0f };
        cm.AddComponent(unit, pack);

        pack.Unpack();                               // 未打包 → 拒
        Assert.False(pack.Packing);

        pack.Pack();
        float before = pack.ElapsedTime;
        pack.Pack();                                 // 进行中 → 拒(不重置进度)
        Assert.Equal(before, pack.ElapsedTime);

        pack.CancelPack();
        Assert.False(pack.Packing);
        Assert.Equal(0f, pack.ElapsedTime);
        Assert.False(pack.Packed);                   // 取消不翻转状态
    }

    [Fact]
    public void Transform_SpawnsPackEntity_PreservingPosOwnerHealth()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm, player: 2, x: 10f, z: 20f, hp: 50, maxHp: 100);
        var pack = new PackComponent { PackTime = 0.5f, PackEntity = "units/test_siege_packed" };
        cm.AddComponent(unit, pack);

        pack.Pack();
        Assert.True(pack.Tick(0.6f, cm));

        // 换模板:新实体同位置/同主/血量比例保持,旧实体销毁。
        Assert.NotNull(pack.LastTransformedTo);
        var newEnt = pack.LastTransformedTo!.Value;
        Assert.NotEqual(unit, newEnt);
        Assert.Null(cm.QueryInterface<PositionComponent>(unit));       // 旧实体已销毁
        var newPos = cm.QueryInterface<PositionComponent>(newEnt);
        Assert.NotNull(newPos);
        Assert.Equal(10f, newPos!.Position.X.ToFloat(), 2);
        Assert.Equal(20f, newPos.Position.Z.ToFloat(), 2);
        Assert.Equal(2, cm.QueryInterface<OwnershipComponent>(newEnt)!.PlayerId);
        var newHealth = cm.QueryInterface<HealthComponent>(newEnt);
        Assert.NotNull(newHealth);
        Assert.Equal(newHealth!.Max / 2, newHealth.Current);           // 50/100 → 新 Max 的一半
    }

    [Fact]
    public void RoundTrip_PreservesProgress()
    {
        var pack = new PackComponent { PackTime = 3.5f, Packed = true, Packing = true, ElapsedTime = 1.25f, PackEntity = "units/x" };
        var ms = new System.IO.MemoryStream();
        pack.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new PackComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.True(back.Packed);
        Assert.True(back.Packing);
        Assert.Equal(3.5f, back.PackTime, 3);
        Assert.Equal(1.25f, back.ElapsedTime, 3);
        Assert.Equal("units/x", back.PackEntity);
    }

    // --- UnitAI 集成 ---

    [Fact]
    public void UnitAI_PackOrder_PacksToCompletion_ThenIdle()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var pack = new PackComponent { PackTime = 0.5f };
        cm.AddComponent(unit, pack);
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.Pack();
        ai.Tick(0.1f, cm);                          // 派发 Order.Pack
        Assert.Equal("INDIVIDUAL.PACKING", ai.FsmStateName);
        Assert.True(pack.Packing);

        for (int i = 0; i < 20 && pack.Packing; i++)
            ai.Tick(0.1f, cm);

        Assert.True(pack.Packed);
        Assert.EndsWith("IDLE", ai.FsmStateName);   // PackFinished → FinishOrder → IDLE
    }

    [Fact]
    public void UnitAI_PackRejectedWithoutPackComponent_FinishesOrder_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);                    // 无 PackComponent → 拒收
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.Pack();
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }

    [Fact]
    public void UnitAI_StopDuringPacking_CancelsProgress()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var pack = new PackComponent { PackTime = 5f };
        cm.AddComponent(unit, pack);
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.Pack();
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);                          // 累计 0.2s 进度
        Assert.True(pack.Packing);

        ai.Stop();
        Assert.False(pack.Packing);                 // PACKING.leave → CancelPack
        Assert.Equal(0f, pack.ElapsedTime);
        Assert.False(pack.Packed);
        Assert.EndsWith("IDLE", ai.FsmStateName);
    }
}
