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
}
