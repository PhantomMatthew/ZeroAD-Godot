using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// UnitAI 批 2:FINDINGNEWTARGET(采空自动续采)+ GatherNearPosition(就近采集)测试。
/// 对照原版 UnitAI.js:GATHER.FINDINGNEWTARGET 的同类续采过滤(排除采空目标、
/// 同 specific、可见)与 Order.GatherNearPosition 的就近查找。
/// </summary>
public sealed class UnitAIGatherRetargetTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1)
    {
        Components.SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new ResourceGatherer());
        if (player > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static EntityId MakeSupply(ComponentManager cm, float x, float z, string specific = "tree", int amount = 100)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new IdentityComponent { TemplateName = $"gaia/tree/{specific}" });
        cm.AddComponent(e, new ResourceSupply());
        // Amount/类型在 AddComponent 后设置——OnInit 会把 Amount 重置为默认 100,
        // 且会清 SpecificType/GenericType(与 SimBridge 生成路径同款次序约束)。
        var supply = cm.QueryInterface<ResourceSupply>(e)!;
        supply.Amount = amount;
        supply.MaxAmount = amount;
        supply.SetTypeString($"wood.{specific}");
        SetPos(cm, e, x, z);
        return e;
    }

    private static void SetPos(ComponentManager cm, EntityId e, float x, float z)
    {
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
    }

    [Fact]
    public void DepletedSupply_AutoRetargetsToNearestSameType()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var worker = MakeUnit(cm);
        SetPos(cm, worker, 0, 0);
        var near = MakeSupply(cm, 8, 0);      // 最近同类
        var far = MakeSupply(cm, 40, 0);      // 更远同类
        var depleted = MakeSupply(cm, 2, 0, amount: 0);   // 已采空(排除项)

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.Gather(depleted);
        ai.Tick(0.1f, cm);   // 派发 Gather → APPROACHING
        // 目标空 → 下拍转 GATHERING → 空 → FINDINGNEWTARGET
        for (int i = 0; i < 10; i++)
        {
            cm.QueryInterface<UnitMotion>(worker)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
            if (ai.FsmStateName == "INDIVIDUAL.GATHER.APPROACHING" && i > 2) break;
        }

        var gatherer = cm.QueryInterface<ResourceGatherer>(worker)!;
        Assert.Equal(near, gatherer.TargetSupply);
    }

    [Fact]
    public void GatherNearPosition_PicksClosestSupply()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var worker = MakeUnit(cm);
        SetPos(cm, worker, 0, 0);
        MakeSupply(cm, 30, 30);               // 远
        var closest = MakeSupply(cm, 12, 6);  // 近

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.GatherNearPosition(new FixedVector2D(Fixed.FromFloat(10f), Fixed.FromFloat(5f)));
        ai.Tick(0.1f, cm);

        var gatherer = cm.QueryInterface<ResourceGatherer>(worker)!;
        Assert.Equal(closest, gatherer.TargetSupply);
        Assert.Equal("INDIVIDUAL.GATHER.APPROACHING", ai.FsmStateName);
    }

    [Fact]
    public void GatherNearPosition_NoSupply_FinishesOrder()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var worker = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.GatherNearPosition(new FixedVector2D(Fixed.FromFloat(500f), Fixed.FromFloat(500f)));
        ai.Tick(0.1f, cm);
        Assert.True(ai.IsIdle);
    }
}
