using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>UpgradeComponent(原版 Upgrade.js 原地升级)测试:
/// 扣费启动/进度/完成换模板/取消退还/易主取消。</summary>
public sealed class UpgradeComponentTests
{
    private static (ComponentManager cm, EntityId building, PlayerComponent player) World()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 1000; pc.Food = 1000; pc.Stone = 1000; pc.Metal = 1000;   // OnInit 重置后赋值
        cm.Players.AddPlayer(1, pe);

        var b = cm.CreateEntity();
        cm.AddComponent(b, new PositionComponent());
        cm.QueryInterface<PositionComponent>(b)!.Position =
            new FixedVector3D(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10));
        cm.AddComponent(b, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(b, new IdentityComponent
        { TemplateName = "structures/athen/sentry_tower", IsBuilding = true });
        cm.NotifyEntityCreated(b);
        return (cm, b, pc);
    }

    [Fact]
    public void StartUpgrade_SpendsCost_TracksProgress()
    {
        var (cm, b, player) = World();
        var up = new UpgradeComponent();
        cm.AddComponent(b, up);
        Assert.True(up.StartUpgrade(cm, "structures/athen/defense_tower", 10f,
            100, 50, 0, 0, "", player));
        Assert.Equal(900, player.Wood);
        Assert.Equal(950, player.Food);
        Assert.True(up.IsUpgrading);
        up.Tick(cm, 5f);
        Assert.Equal(0.5f, up.GetProgress(), 2);
    }

    [Fact]
    public void CancelUpgrade_RefundsAll()
    {
        var (cm, b, player) = World();
        var up = new UpgradeComponent();
        cm.AddComponent(b, up);
        up.StartUpgrade(cm, "structures/athen/defense_tower", 10f, 100, 50, 0, 0, "", player);
        up.Tick(cm, 3f);
        up.CancelUpgrade(cm);
        Assert.Equal(1000, player.Wood);
        Assert.Equal(1000, player.Food);
        Assert.False(up.IsUpgrading);
    }

    [Fact]
    public void StartUpgrade_CannotAfford_Fails()
    {
        var (cm, b, player) = World();
        player.Wood = 10;
        var up = new UpgradeComponent();
        cm.AddComponent(b, up);
        Assert.False(up.StartUpgrade(cm, "x", 10f, 100, 0, 0, 0, "", player));
        Assert.Equal(10, player.Wood);
        Assert.False(up.IsUpgrading);
    }

    [Fact]
    public void OwnershipChange_Cancels()
    {
        var (cm, b, player) = World();
        var up = new UpgradeComponent();
        cm.AddComponent(b, up);
        up.StartUpgrade(cm, "x", 10f, 100, 0, 0, 0, "", player);
        cm.QueryInterface<OwnershipComponent>(b)!.PlayerId = 2;
        cm.NotifyOwnerChanged(b, 1, 2);
        Assert.False(up.IsUpgrading);
        Assert.Equal(1000, player.Wood);
    }

    [Fact]
    public void RoundTrip_PreservesProgress()
    {
        var (cm, b, _) = World();
        var up = new UpgradeComponent();
        cm.AddComponent(b, up);
        up.StartUpgrade(cm, "x", 10f, 100, 50, 0, 0, "var", cm.GetPlayerEntity(1)!);
        up.Tick(cm, 4f);

        var s1 = new CapturingSerializer();
        up.Serialize(s1);
        var up2 = new UpgradeComponent();
        up2.Deserialize(new ReplayingDeserializer(s1));
        var s2 = new CapturingSerializer();
        up2.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);
    }
}
