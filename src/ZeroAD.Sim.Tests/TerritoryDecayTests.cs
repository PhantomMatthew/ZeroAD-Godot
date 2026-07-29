using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// TerritoryDecay + Capturable 内核测试(逐行对齐原版 TerritoryDecay.js / Capturable.js
/// 的 decay 闭环):IsConnected 判定矩阵、blink 覆盖、GetNeighbours、CP 抽干分配/翻面、
/// regen 恢复、序列化往返、双世界确定性。
/// </summary>
public sealed class TerritoryDecayTests
{
    private static (ComponentManager cm, TerritoryManager tm) NewWorld(int meters = 64)
    {
        var cm = new ComponentManager(42);
        var tm = new TerritoryManager(cm, meters);
        return (cm, tm);
    }

    private static Fixed M(float v) => Fixed.FromFloat(v);
    private static readonly Fixed OneSec = Fixed.FromInt(1);

    private static EntityId AddInfluencer(ComponentManager cm, int owner, float x, float z,
        float radius, int weight = 1, bool root = false)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new TerritoryInfluenceComponent
        {
            Radius = Fixed.FromFloat(radius),
            Weight = weight,
            Root = root,
        });
        cm.NotifyEntityCreated(e);
        return e;
    }

    private static void AddPlayerWithDiplomacy(ComponentManager cm, int playerId)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
        cm.AddComponent(e, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, e);
    }

    /// <summary>挂 TerritoryDecay(+Capturable 可选)的建筑;owner/位置/衰减参数可调。</summary>
    private static EntityId AddBuilding(ComponentManager cm, int owner, float x, float z,
        float decayRate, string territory, float maxCp = 0, float regen = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new TerritoryDecayComponent
        {
            DecayRate = Fixed.FromFloat(decayRate),
            Territory = territory,
        });
        if (maxCp > 0)
        {
            var cap = new CapturableComponent
            {
                MaxCapturePoints = Fixed.FromFloat(maxCp),
                RegenRate = Fixed.FromFloat(regen),
            };
            cm.AddComponent(e, cap);
            cap.InitForOwner(owner);
        }
        cm.NotifyEntityCreated(e);
        return e;
    }

    // ---------- TerritoryManager:GetNeighbours / blink 覆盖 ----------

    [Fact]
    public void GetNeighbours_CountsBorderCellsPerPlayer()
    {
        var (cm, tm) = NewWorld();
        // P1 飞地(无 root)贴 P2 连通领土:cell 边界各算一条。
        AddInfluencer(cm, 1, x: 30, z: 32, radius: 8, weight: 40000);
        AddInfluencer(cm, 2, x: 46, z: 32, radius: 12, weight: 1, root: true);

        var counts = tm.GetNeighbours(M(30), M(32), onlyConnected: true);
        Assert.True(counts[2] > 0);          // 邻着 P2 连通领土
        Assert.Equal(0, counts[1]);          // 不数自己
        // gaia 无"连通"概念,onlyConnected 下不数(原版同——孤立飞地 total=0 时
        // Capturable 走 "decay to gaia as default" 分支,正是为此存在)。
        Assert.Equal(0, counts[0]);

        var all = tm.GetNeighbours(M(30), M(32), onlyConnected: false);
        Assert.True(all[0] > 0);             // 不过滤时 gaia 边界计入
        Assert.True(all[2] > 0);
    }

    [Fact]
    public void BlinkOverride_SetClear_FallsBackToUnconnected()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, 1, x: 32, z: 32, radius: 8, weight: 40000);  // 孤立飞地

        Assert.True(tm.IsTerritoryBlinking(M(32), M(32)));   // 无覆盖 → 未连通即闪
        tm.SetTerritoryBlinking(M(32), M(32), false);
        Assert.False(tm.IsTerritoryBlinking(M(32), M(32)));  // 覆盖关
        tm.SetTerritoryBlinking(M(32), M(32), true);
        Assert.True(tm.IsTerritoryBlinking(M(32), M(32)));
        Assert.False(tm.IsTerritoryBlinking(M(4), M(4)));    // gaia cell 不闪
        tm.SetTerritoryBlinking(M(4), M(4), true);           // gaia 覆盖被忽略
        Assert.False(tm.IsTerritoryBlinking(M(4), M(4)));
    }

    // ---------- TerritoryDecay.IsConnected 矩阵 ----------

    [Fact]
    public void IsConnected_GaiaTile_NeutralTokenDecides()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);   // 原版语义:无外交组件的玩家实体不 decay
        var e1 = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy");
        var d1 = cm.QueryInterface<TerritoryDecayComponent>(e1)!;
        Assert.False(d1.IsConnected(cm, tm));               // gaia + 列表含 neutral → decay
        Assert.Equal(1, d1.ConnectedNeighbours[0]);         // 衰向 gaia

        var e2 = AddBuilding(cm, 1, 40, 32, 20, "enemy");
        var d2 = cm.QueryInterface<TerritoryDecayComponent>(e2)!;
        Assert.True(d2.IsConnected(cm, tm));                // gaia + 列表无 neutral → 不 decay
    }

    [Fact]
    public void IsConnected_EnemyConnectedTile_EnemyTokenDecides()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        AddInfluencer(cm, 2, x: 32, z: 32, radius: 24, root: true);   // P2 连通领土

        var e1 = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy");
        Assert.False(cm.QueryInterface<TerritoryDecayComponent>(e1)!.IsConnected(cm, tm));

        var e2 = AddBuilding(cm, 1, 36, 32, 20, "neutral");
        Assert.True(cm.QueryInterface<TerritoryDecayComponent>(e2)!.IsConnected(cm, tm));
    }

    [Fact]
    public void IsConnected_OwnOrAllyConnectedTile_NoDecay()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 }); // 互盟
        AddInfluencer(cm, 1, x: 24, z: 32, radius: 16, root: true);   // P1 连通
        AddInfluencer(cm, 2, x: 44, z: 32, radius: 16, root: true);   // P2 连通

        var own = AddBuilding(cm, 1, 24, 32, 20, "neutral enemy");
        Assert.True(cm.QueryInterface<TerritoryDecayComponent>(own)!.IsConnected(cm, tm));
        var ally = AddBuilding(cm, 1, 44, 32, 20, "neutral enemy");
        Assert.True(cm.QueryInterface<TerritoryDecayComponent>(ally)!.IsConnected(cm, tm));
    }

    [Fact]
    public void IsConnected_UnconnectedEnemyTile_DecaysTowardGaia()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        AddInfluencer(cm, 2, x: 32, z: 32, radius: 8, weight: 40000);  // P2 飞地(无 root)

        var e = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy");
        var d = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        Assert.False(d.IsConnected(cm, tm));                // playerID != tileOwner → 衰向 gaia
        Assert.Equal(1, d.ConnectedNeighbours[0]);
    }

    [Fact]
    public void IsConnected_OwnEnclave_AllyNeighbourSaves_Unblink()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 }); // 互盟
        AddInfluencer(cm, 1, x: 28, z: 32, radius: 8, weight: 40000);  // P1 飞地
        AddInfluencer(cm, 2, x: 44, z: 32, radius: 12, root: true);    // 毗邻 P2 连通领土

        var e = AddBuilding(cm, 1, 28, 32, 20, "neutral enemy");
        var d = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        Assert.True(d.IsConnected(cm, tm));                 // 盟军连通邻主 → 不 decay
        Assert.False(tm.IsTerritoryBlinking(M(28), M(32))); // 且灭 blink
    }

    [Fact]
    public void IsConnected_OwnEnclave_Isolated_DecaysAndBlinks()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddInfluencer(cm, 1, x: 32, z: 32, radius: 8, weight: 40000);  // P1 孤立飞地

        var e = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy");
        var d = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        Assert.False(d.IsConnected(cm, tm));
        Assert.True(tm.IsTerritoryBlinking(M(32), M(32)));  // blink 覆盖开
        d.UpdateDecayState(cm, tm);
        Assert.True(d.Decaying);
    }

    [Fact]
    public void UpdateDecayState_ZeroRate_NeverDecaying()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        var e = AddBuilding(cm, 1, 32, 32, 0, "neutral enemy");   // gaia 上,rate=0
        var d = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        d.UpdateDecayState(cm, tm);
        Assert.False(d.Decaying);
    }

    // ---------- Capturable 闭环 ----------

    [Fact]
    public void Capturable_Init_OwnerGetsMaxCp()
    {
        var (cm, _) = NewWorld();
        var e = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy", maxCp: 500);
        var cap = cm.QueryInterface<CapturableComponent>(e)!;
        Assert.Equal(Fixed.FromInt(500), cap.CapturePoints[1]);
        Assert.Equal(Fixed.Zero, cap.CapturePoints[0]);
    }

    [Fact]
    public void Capturable_DecayDrainsToGaia_FlipsOwnership()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        var e = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy", maxCp: 10);
        var decay = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        var cap = cm.QueryInterface<CapturableComponent>(e)!;

        (int from, int to)? flip = null;
        cm.OwnerChanged += (ent, f, t) => { if (ent == e) flip = (f, t); };

        decay.UpdateDecayState(cm, tm);          // gaia 上 + neutral → decaying
        Assert.True(decay.Decaying);
        cap.TimerTick(cm, OneSec);               // drain=min(20,10)=10 → 抽空

        Assert.Equal(Fixed.Zero, cap.CapturePoints[1]);
        Assert.Equal(Fixed.FromInt(10), cap.CapturePoints[0]);
        Assert.Equal((1, 0), flip);              // 翻面:1 → gaia
        Assert.Equal(0, cm.QueryInterface<OwnershipComponent>(e)!.PlayerId);
    }

    [Fact]
    public void Capturable_DecayDistributesByNeighbourShare()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        AddInfluencer(cm, 2, x: 44, z: 32, radius: 12, root: true);    // P2 连通领土
        AddInfluencer(cm, 1, x: 28, z: 32, radius: 8, weight: 40000);  // P1 飞地贴着它

        var e = AddBuilding(cm, 1, 28, 32, 4, "neutral enemy", maxCp: 12);
        var decay = cm.QueryInterface<TerritoryDecayComponent>(e)!;
        var cap = cm.QueryInterface<CapturableComponent>(e)!;
        decay.UpdateDecayState(cm, tm);          // 自家飞地:邻主表来自 GetNeighbours
        Assert.True(decay.Decaying);
        Assert.True(decay.ConnectedNeighbours[2] > 0);
        // gaia 在 onlyConnected 下不数(原版同);分配式用下面手动 3:1 验证。

        // 手动控成 3:1 验证分配式(地形格数依赖实现,不断言具体格数)。
        decay.ConnectedNeighbours[2] = 3; decay.ConnectedNeighbours[0] = 1;
        cap.TimerTick(cm, OneSec);               // drain=4:P2 得 3,gaia 得 1
        Assert.Equal(Fixed.FromInt(8), cap.CapturePoints[1]);
        Assert.Equal(Fixed.FromInt(3), cap.CapturePoints[2]);
        Assert.Equal(Fixed.FromInt(1), cap.CapturePoints[0]);
    }

    [Fact]
    public void Capturable_RegenDrainsEnemiesBackToOwner()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        var e = AddBuilding(cm, 1, 32, 32, 20, "neutral enemy", maxCp: 12, regen: 2);
        var cap = cm.QueryInterface<CapturableComponent>(e)!;
        cap.CapturePoints[1] = Fixed.FromInt(8);
        cap.CapturePoints[0] = Fixed.FromInt(4);  // gaia 持有 4(regen 须抽回)

        cap.TimerTick(cm, OneSec);               // 自家连通无关——此楼在 gaia 但 rate 路径关
        Assert.Equal(Fixed.FromInt(10), cap.CapturePoints[1]);
        Assert.Equal(Fixed.FromInt(2), cap.CapturePoints[0]);
    }

    // ---------- 序列化 / 确定性 ----------

    [Fact]
    public void TerritoryDecay_RoundTrip()
    {
        var c = new TerritoryDecayComponent
        {
            DecayRate = Fixed.FromFloat(20f),
            Territory = "neutral enemy",
            Decaying = true,
        };
        c.ConnectedNeighbours[0] = 1;
        c.ConnectedNeighbours[2] = 3;
        var s1 = new CapturingSerializer();
        c.Serialize(s1);
        var restored = new TerritoryDecayComponent();
        restored.Deserialize(new ReplayingDeserializer(s1));
        var s2 = new CapturingSerializer();
        restored.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);
        Assert.True(restored.Decaying);
        Assert.Equal(3, restored.ConnectedNeighbours[2]);
    }

    [Fact]
    public void Capturable_RoundTrip()
    {
        var c = new CapturableComponent
        {
            MaxCapturePoints = Fixed.FromInt(500),
            RegenRate = Fixed.FromInt(5),
            GarrisonRegenRate = Fixed.FromInt(1),
        };
        c.CapturePoints[0] = Fixed.FromInt(120);
        c.CapturePoints[1] = Fixed.FromInt(380);
        var s1 = new CapturingSerializer();
        c.Serialize(s1);
        var restored = new CapturableComponent();
        restored.Deserialize(new ReplayingDeserializer(s1));
        var s2 = new CapturingSerializer();
        restored.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);
        Assert.Equal(Fixed.FromInt(380), restored.CapturePoints[1]);
    }

    [Fact]
    public void DecayLoop_TwoIdenticalWorlds_SameOutcome()
    {
        Fixed[] Run()
        {
            var (cm, tm) = NewWorld();
            AddPlayerWithDiplomacy(cm, 1);
            AddPlayerWithDiplomacy(cm, 2);
            cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
            AddInfluencer(cm, 2, x: 44, z: 32, radius: 12, root: true);
            AddInfluencer(cm, 1, x: 28, z: 32, radius: 8, weight: 40000);
            var e = AddBuilding(cm, 1, 28, 32, 20, "neutral enemy", maxCp: 100);
            var decay = cm.QueryInterface<TerritoryDecayComponent>(e)!;
            var cap = cm.QueryInterface<CapturableComponent>(e)!;
            var dt = Fixed.FromFloat(0.1f);
            for (int turn = 0; turn < 50; turn++)
            {
                decay.Refresh(cm, tm);
                cap.TimerTick(cm, dt);
            }
            return cap.CapturePoints;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }
}
