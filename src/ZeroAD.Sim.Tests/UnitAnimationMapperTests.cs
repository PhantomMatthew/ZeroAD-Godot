using Xunit;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 表现层走步判定。原版 CCmpUnitMotion 按实际移动播 walk;
/// C# 用 FSM 名映射时 EndsWith(".WALKING") 漏掉 WALKINGANDFIGHTING
/// (教程 attack-walk 的状态),FLEEING / PATROLLING 同理。
/// </summary>
public sealed class UnitAnimationMapperTests
{
    [Theory]
    [InlineData("INDIVIDUAL.WALKING", "Walk")]
    [InlineData("INDIVIDUAL.WALKINGANDFIGHTING", "Walk")]
    [InlineData("INDIVIDUAL.FLEEING", "Walk")]
    [InlineData("INDIVIDUAL.PATROL.PATROLLING", "Walk")]
    [InlineData("INDIVIDUAL.COMBAT.APPROACHING", "Walk")]
    [InlineData("INDIVIDUAL.GATHER.RETURNINGRESOURCE", "Walk")]
    [InlineData("INDIVIDUAL.GUARD.ESCORTING", "Walk")]
    [InlineData("INDIVIDUAL.COMBAT.ATTACKING", "attack_melee")]
    [InlineData("INDIVIDUAL.IDLE", "Idle")]
    [InlineData("INDIVIDUAL.REPAIR.REPAIRING", "Build")]
    public void Resolve_Fsm_MapsToClip(string fsm, string clip)
    {
        Assert.Equal(clip, UnitAnimationMapper.Resolve(fsm, hasMoveTarget: false));
    }

    [Fact]
    public void Resolve_IdleButHasMoveTarget_IsWalk()
    {
        Assert.Equal("Walk", UnitAnimationMapper.Resolve("INDIVIDUAL.IDLE", hasMoveTarget: true));
    }

    [Fact]
    public void Resolve_IdleButApproaching_IsWalk()
    {
        Assert.Equal("Walk", UnitAnimationMapper.Resolve(
            "INDIVIDUAL.IDLE",
            hasMoveTarget: false,
            AttackComponent.AttackState.Approaching));
    }

    [Fact]
    public void Resolve_Gathering_UsesSpecificType()
    {
        Assert.Equal("gather_fruit", UnitAnimationMapper.Resolve(
            "INDIVIDUAL.GATHER.GATHERING", hasMoveTarget: false, gatherSpecific: "fruit"));
        Assert.Equal("gather_tree", UnitAnimationMapper.Resolve(
            "INDIVIDUAL.GATHER.GATHERING", hasMoveTarget: false, gatherSpecific: null));
    }
}
