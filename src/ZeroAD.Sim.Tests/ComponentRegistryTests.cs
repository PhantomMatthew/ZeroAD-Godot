using Xunit;

namespace ZeroAD.Sim.Tests;

public class ComponentRegistryTests
{
    [Fact]
    public void RegisterInterface_AssignsUniqueId()
    {
        var reg = new ComponentRegistry();
        var iid1 = reg.RegisterInterface("Position");
        var iid2 = reg.RegisterInterface("Health");
        Assert.NotEqual(iid1, iid2);
        Assert.True(iid1.Value < iid2.Value);
    }

    [Fact]
    public void RegisterInterface_DuplicateReturnsSame()
    {
        var reg = new ComponentRegistry();
        var iid1 = reg.RegisterInterface("Position");
        var iid2 = reg.RegisterInterface("Position");
        Assert.Equal(iid1, iid2);
    }

    [Fact]
    public void RegisterComponent_AssignsCidAndMapsToInterface()
    {
        var reg = new ComponentRegistry();
        var cid = reg.RegisterComponent<Components.PositionComponent>("Position", "Position");
        Assert.True(cid.IsValid);

        var iid = reg.GetInterfaceForComponent(cid);
        Assert.Equal("Position", iid.Name);
    }

    [Fact]
    public void RegisterComponent_GetByName()
    {
        var reg = new ComponentRegistry();
        reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var cid = reg.GetComponentType("Health");
        Assert.True(cid.IsValid);
    }

    [Fact]
    public void RegisterComponent_DuplicateReturnsSame()
    {
        var reg = new ComponentRegistry();
        var cid1 = reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var cid2 = reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        Assert.Equal(cid1, cid2);
    }

    [Fact]
    public void CreateComponent_ProducesInstance()
    {
        var reg = new ComponentRegistry();
        var cid = reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var comp = reg.CreateComponent(cid);
        Assert.IsType<Components.HealthComponent>(comp);
    }

    [Fact]
    public void GetDefaultImplementation_ReturnsFirstRegistered()
    {
        var reg = new ComponentRegistry();
        reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var iid = reg.GetInterface("Health");
        var cid = reg.GetDefaultImplementation(iid);
        Assert.True(cid.HasValue);
    }

    [Fact]
    public void AutoRegister_FindsAttributedTypes()
    {
        var reg = new ComponentRegistry();
        reg.AutoRegister(typeof(Components.PositionComponent).Assembly);

        var posCid = reg.GetComponentType("Position");
        var healthCid = reg.GetComponentType("Health");
        var ownCid = reg.GetComponentType("Ownership");

        Assert.True(posCid.IsValid);
        Assert.True(healthCid.IsValid);
        Assert.True(ownCid.IsValid);
    }

    [Fact]
    public void GenerateSchema_ContainsAllRegisteredComponents()
    {
        var reg = new ComponentRegistry();
        reg.RegisterComponent<Components.PositionComponent>("Position", "Position");
        reg.RegisterComponent<Components.HealthComponent>("Health", "Health");

        string schema = reg.GenerateSchema();
        Assert.Contains("Position", schema);
        Assert.Contains("Health", schema);
        Assert.Contains("<Components>", schema);
    }

    [Fact]
    public void ComponentManager_UsesRegistryForAddByCid()
    {
        var reg = new ComponentRegistry();
        reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var cm = new ComponentManager(42, reg);

        var entity = cm.CreateEntity();
        var cid = reg.GetComponentType("Health");
        cm.AddComponent(entity, cid);

        var health = cm.QueryInterface<Components.HealthComponent>(entity);
        Assert.NotNull(health);
        Assert.Equal(100, health!.Max);
    }

    [Fact]
    public void ComponentManager_QueryByInterfaceId()
    {
        var reg = new ComponentRegistry();
        reg.RegisterComponent<Components.HealthComponent>("Health", "Health");
        var cm = new ComponentManager(42, reg);

        var entity = cm.CreateEntity();
        var cid = reg.GetComponentType("Health");
        cm.AddComponent(entity, cid);

        var iid = reg.GetInterface("Health");
        var comp = cm.QueryInterface(entity, iid);
        Assert.NotNull(comp);
    }
}
