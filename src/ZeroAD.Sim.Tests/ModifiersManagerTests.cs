using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Tests;

public sealed class ModifiersManagerTests
{
    private static (ComponentManager cm, EntityId playerEnt, EntityId unit) World()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEnt = cm.CreateEntity();
        cm.AddComponent(playerEnt, new PlayerComponent());
        cm.Players.AddPlayer(1, playerEnt);
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new IdentityComponent { Classes = new List<string> { "Unit", "Soldier", "Melee" } });
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        return (cm, playerEnt, unit);
    }

    private static Modification Mul(string path, float m, params string[] affects) =>
        new(path, null, m, null, new List<string>(affects));

    private static Modification Add(string path, float a, params string[] affects) =>
        new(path, a, null, null, new List<string>(affects));

    [Fact]
    public void Apply_ReturnsBase_WhenNoModifiers()
    {
        var (cm, _, unit) = World();
        Assert.Equal(10f, cm.Modifiers.Apply("Attack/Melee/Damage/Hack", 10f, unit));
    }

    [Fact]
    public void Apply_AddBeforeMultiply()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("tech_add", new[] { Add("Health/Max", 20f) }, playerEnt);
        cm.Modifiers.AddModifiers("tech_mul", new[] { Mul("Health/Max", 1.5f) }, playerEnt);
        // add 先于 multiply:(100 + 20) * 1.5 = 180
        Assert.Equal(180f, cm.Modifiers.Apply("Health/Max", 100f, unit));
    }

    [Fact]
    public void Apply_AffectsFilter_MatchesSpaceSeparatedAnd()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[] { Mul("Health/Max", 2f, "Soldier Melee") }, playerEnt);
        Assert.Equal(200f, cm.Modifiers.Apply("Health/Max", 100f, unit));

        // 不匹配:unit 无 Ranged 类 → 仍只有 ×2
        cm.Modifiers.AddModifiers("t2", new[] { Mul("Health/Max", 3f, "Soldier Ranged") }, playerEnt);
        Assert.Equal(200f, cm.Modifiers.Apply("Health/Max", 100f, unit));
    }

    [Fact]
    public void Apply_NoIdentity_ReturnsBase()
    {
        var (cm, playerEnt, _) = World();
        var bare = cm.CreateEntity(); // 无 Identity
        cm.Modifiers.AddModifiers("t", new[] { Mul("Health/Max", 2f) }, playerEnt);
        Assert.Equal(100f, cm.Modifiers.Apply("Health/Max", 100f, bare));
    }

    [Fact]
    public void Apply_EntityLocal_AppliesAfterPlayerWide()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("pw", new[] { Add("Health/Max", 10f) }, playerEnt);
        cm.Modifiers.AddModifiers("aura", new[] { Add("Health/Max", 100f) }, unit);
        Assert.Equal(160f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void AddModifiers_SameModId_Rejected()
    {
        var (cm, playerEnt, unit) = World();
        var mods = new[] { Add("Health/Max", 5f) };
        cm.Modifiers.AddModifiers("t", mods, playerEnt);
        cm.Modifiers.AddModifiers("t", mods, playerEnt); // 重复拒绝
        Assert.Equal(55f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void RemoveAllModifiers_RemovesByModId()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[] { Add("Health/Max", 5f) }, playerEnt);
        cm.Modifiers.RemoveAllModifiers("t", playerEnt);
        Assert.Equal(50f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void ApplyPrefix_MatchesSubPaths()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[]
        {
            Mul("ResourceGatherer/Rates/wood.tree", 1.15f),
            Mul("ResourceGatherer/Rates/wood.ruins", 1.15f),
            Mul("ResourceGatherer/Rates/food.grain", 9f)
        }, playerEnt);
        Assert.Equal(10f * 1.15f * 1.15f,
            cm.Modifiers.ApplyPrefix("ResourceGatherer/Rates/wood", 10f, unit), 3);
    }

    [Fact]
    public void Deterministic_CrossTechOrder_SortedByModId()
    {
        var (cm, playerEnt, unit) = World();
        // 反序插入,结果必须一致(modId 排序固定)
        cm.Modifiers.AddModifiers("b_mul", new[] { Mul("Health/Max", 2f) }, playerEnt);
        cm.Modifiers.AddModifiers("a_add", new[] { Add("Health/Max", 10f) }, playerEnt);
        Assert.Equal((50f + 10f) * 2f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void ApplyTemplate_OnlyPlayerWide()
    {
        var (cm, playerEnt, _) = World();
        cm.Modifiers.AddModifiers("t", new[] { Mul("Cost/BuildTime", 0.9f, "Siege") }, playerEnt);
        var siege = new List<string> { "Unit", "Siege" };
        var infantry = new List<string> { "Unit", "Infantry" };
        Assert.Equal(9f, cm.Modifiers.ApplyTemplate("Cost/BuildTime", 10f, siege, playerEnt), 3);
        Assert.Equal(10f, cm.Modifiers.ApplyTemplate("Cost/BuildTime", 10f, infantry, playerEnt));
    }
}
