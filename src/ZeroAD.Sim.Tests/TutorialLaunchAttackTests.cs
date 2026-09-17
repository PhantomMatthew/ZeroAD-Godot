using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Tutorial;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 原版 introductory_tutorial.js LaunchAttack 对敌方 CitizenSoldier 发
/// ProcessCommand(attack-walk)，UnitAI 进入 INDIVIDUAL.WALKINGANDFIGHTING。
/// C# 若走 AttackComponent.AttackTarget(建筑)，UnitAI 停在 IDLE，表现层
/// ResolveAnimationState 也播不出走步。
/// </summary>
public sealed class TutorialLaunchAttackTests
{
    [Fact]
    public void LaunchAttack_EnemyCitizenSoldiers_EnterWalkAndFight()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);

        var tower = cm.CreateEntity();
        cm.AddComponent(tower, new PositionComponent
        {
            Position = new FixedVector3D(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10))
        });
        cm.AddComponent(tower, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(tower, new IdentityComponent
        {
            IsBuilding = true,
            IsUnit = false,
            Classes = new List<string> { "Tower", "Structure" }
        });

        var soldier = cm.CreateEntity();
        cm.AddComponent(soldier, new PositionComponent
        {
            Position = new FixedVector3D(Fixed.FromInt(80), Fixed.Zero, Fixed.FromInt(80))
        });
        cm.AddComponent(soldier, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(soldier, new IdentityComponent
        {
            Classes = new List<string> { "CitizenSoldier", "Unit", "Infantry" }
        });
        cm.AddComponent(soldier, new UnitMotion { Speed = Fixed.FromInt(7) });
        cm.AddComponent(soldier, new UnitAIComponent());
        cm.AddComponent(soldier, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 5), Range = 4f });

        var level = new TutorialLevelData
        {
            Goals =
            {
                new GoalData
                {
                    Instructions = { "launch" },
                    Actions = { "launchAttack" },
                    On = { new BindingData { Event = "researchFinished" } }
                }
            }
        };
        var engine = new TutorialEngine(TutorialLevelLoader.Assemble(level));
        engine.Init(cm, cm.Events);
        cm.Events.RaiseResearchFinished(new ResearchFinishedEvent { Tech = "any" });

        var ai = cm.QueryInterface<UnitAIComponent>(soldier)!;
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.WALKINGANDFIGHTING", ai.FsmStateName);
        Assert.True(cm.QueryInterface<UnitMotion>(soldier)!.HasMoveTarget);
    }
}
