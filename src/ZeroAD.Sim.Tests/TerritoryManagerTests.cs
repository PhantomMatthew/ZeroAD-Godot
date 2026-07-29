using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 领土系统测试(TerritoryManager 网格 + CanBuildHere 放置限制 + ApplyBuild 集成)。
/// 对齐 CCmpTerritoryManager 重建实现:影响力放射衰减 argmax 定主、root 区域连通性、
/// BuildRestrictions.js 的 own/ally/neutral/enemy + 未连通需 neutral。
/// </summary>
public sealed class TerritoryManagerTests
{
    private static (ComponentManager cm, TerritoryManager tm) NewWorld(int meters = 64)
    {
        var cm = new ComponentManager(42);
        var tm = new TerritoryManager(cm, meters);
        return (cm, tm);
    }

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

    private static Fixed M(float v) => Fixed.FromFloat(v);

    // ---------- 网格 ----------

    [Fact]
    public void NoInfluencers_AllGaia()
    {
        var (_, tm) = NewWorld();
        Assert.Equal(0, tm.GetOwner(M(32), M(32)));
        Assert.Equal(0, tm.GetOwner(M(0), M(0)));
    }

    [Fact]
    public void SingleInfluence_OwnsNearbyCells_NotFar()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, owner: 1, x: 32, z: 32, radius: 16);

        Assert.Equal(1, tm.GetOwner(M(32), M(32)));   // 中心
        Assert.Equal(1, tm.GetOwner(M(40), M(32)));   // 半径内(d=8<16)
        Assert.Equal(0, tm.GetOwner(M(60), M(60)));   // 远
    }

    [Fact]
    public void ArgMax_CloserPlayerWins_Tie_LowerPlayerId()
    {
        var (cm, tm) = NewWorld();
        // 网格按 4m cell 量化,cell 中心 = 4k+2。对称摆位 (30,32)/(38,32) → cell(8,8)
        // 中心 (34,34) 与两者精确等距(d²=20 定点),构成真平手。
        AddInfluencer(cm, 1, x: 30, z: 32, radius: 20);
        AddInfluencer(cm, 2, x: 38, z: 32, radius: 20);

        Assert.Equal(1, tm.GetOwner(M(26), M(34)));   // cell(6,8):d1²=20 < d2²=148
        Assert.Equal(2, tm.GetOwner(M(42), M(34)));   // cell(10,8):d2²=20 < d1²=148
        Assert.Equal(1, tm.GetOwner(M(34), M(34)));   // cell(8,8):等距平手 → 小编号
    }

    [Fact]
    public void Weight_Beats_SmallDistanceEdge()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, 1, x: 26, z: 32, radius: 20, weight: 1);        // 近但轻
        AddInfluencer(cm, 2, x: 36, z: 32, radius: 20, weight: 40000);    // 稍远但重

        Assert.Equal(2, tm.GetOwner(M(28), M(32)));   // 重量级决胜(对齐 house 40000 vs CC 10000)
    }

    [Fact]
    public void RootRegion_Connected_LoneInfluence_Unconnected()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, 1, x: 16, z: 16, radius: 24, root: true);       // CC:root 锚点
        AddInfluencer(cm, 1, x: 48, z: 48, radius: 8, weight: 40000);     // 孤立 house:无 root

        Assert.True(tm.IsConnected(M(16), M(16)));     // root 区域连通
        Assert.False(tm.IsTerritoryBlinking(M(16), M(16)));
        Assert.Equal(1, tm.GetOwner(M(48), M(48)));    // house 区域有主…
        Assert.False(tm.IsConnected(M(48), M(48)));    // …但无 root → 未连通(blinking)
        Assert.True(tm.IsTerritoryBlinking(M(48), M(48)));
    }

    [Fact]
    public void Deterministic_TwoIdenticalWorlds_SameGrid()
    {
        var (cm1, tm1) = NewWorld();
        var (cm2, tm2) = NewWorld();
        foreach (var (cm, _) in new[] { (cm1, tm1), (cm2, tm2) })
        {
            AddInfluencer(cm, 1, x: 16, z: 16, radius: 24, weight: 10000, root: true);
            AddInfluencer(cm, 2, x: 48, z: 48, radius: 30, weight: 1, root: true);
            AddInfluencer(cm, 1, x: 40, z: 16, radius: 8, weight: 40000);
        }
        for (float x = 2; x < 64; x += 4)
            for (float z = 2; z < 64; z += 4)
            {
                Assert.Equal(tm1.GetOwner(M(x), M(z)), tm2.GetOwner(M(x), M(z)));
                Assert.Equal(tm1.IsConnected(M(x), M(z)), tm2.IsConnected(M(x), M(z)));
            }
    }

    // ---------- CanBuildHere ----------

    [Fact]
    public void CanBuildHere_Own_Connected_Ok_Gaia_NeedsNeutral()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, 1, x: 32, z: 32, radius: 24, root: true);

        Assert.True(tm.CanBuildHere("own", 1, M(32), M(32)));        // 自家连通
        Assert.False(tm.CanBuildHere("own", 1, M(60), M(60)));       // gaia 不给 own
        Assert.True(tm.CanBuildHere("own neutral", 1, M(60), M(60)));// CC 形状:own neutral
        Assert.False(tm.CanBuildHere("own", 2, M(32), M(32)));       // 别家领土 ≠ own
    }

    [Fact]
    public void CanBuildHere_UnconnectedOwn_NeedsNeutral()
    {
        var (cm, tm) = NewWorld();
        AddInfluencer(cm, 1, x: 32, z: 32, radius: 8, weight: 40000); // 孤立 house 区域(无 root)

        Assert.False(tm.CanBuildHere("own", 1, M(32), M(32)));        // 未连通 own → 拒
        Assert.True(tm.CanBuildHere("own neutral", 1, M(32), M(32))); // 带 neutral → 放
    }

    [Fact]
    public void CanBuildHere_Ally_NeedsAlly_Enemy_NeedsEnemy()
    {
        var (cm, tm) = NewWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        AddPlayerWithDiplomacy(cm, 3);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1 });
        AddInfluencer(cm, 2, x: 32, z: 32, radius: 24, root: true);   // P2 领土

        Assert.True(tm.CanBuildHere("ally", 1, M(32), M(32)));        // 互盟 → ally 可建
        Assert.False(tm.CanBuildHere("own", 1, M(32), M(32)));        // 盟友领土 ≠ own
        Assert.False(tm.CanBuildHere("ally", 3, M(32), M(32)));       // 敌对不给 ally
        Assert.True(tm.CanBuildHere("enemy", 3, M(32), M(32)));       // 敌对需 enemy
        Assert.False(tm.CanBuildHere("own neutral", 3, M(32), M(32)));// 敌领土 ≠ own/neutral
    }

    // ---------- 组件序列化 ----------

    [Fact]
    public void TerritoryInfluence_RoundTrip()
    {
        var c = new TerritoryInfluenceComponent
        {
            Radius = Fixed.FromFloat(140f),
            Weight = 10000,
            Root = true,
        };
        var s1 = new CapturingSerializer();
        c.Serialize(s1);
        var restored = new TerritoryInfluenceComponent();
        restored.Deserialize(new ReplayingDeserializer(s1));
        var s2 = new CapturingSerializer();
        restored.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);
        Assert.Equal(140f, restored.Radius.ToFloat(), 0.01f);
        Assert.Equal(10000, restored.Weight);
        Assert.True(restored.Root);
    }

    // ---------- ApplyBuild 集成 ----------

    private static string? FindRepoPath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : System.IO.Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void ApplyBuild_TerritoryCheck_GaiaRejectsHouse_CcFoundationExpandsTerritory()
    {
        var templatesPath = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (templatesPath == null) return;   // LFS 数据缺失 → 跳过(同 Train 测试惯例)
        var templates = new Content.TemplateLoader(templatesPath);

        var cm = new ComponentManager(42, templates: templates);
        SimSystem.Init(cm);
        var tm = new TerritoryManager(cm, 64);
        var executor = new SimCommandExecutor(cm, territory: tm);   // 无 pathfinder → 只验领土

        var playerEntity = cm.CreateEntity();
        var playerComp = new PlayerComponent();
        cm.AddComponent(playerEntity, playerComp);
        // OnInit 会重置资源为默认开局值(300/300/200/100)——必须在 AddComponent 之后覆写。
        playerComp.Wood = 5000; playerComp.Food = 5000; playerComp.Stone = 5000; playerComp.Metal = 5000;
        playerComp.PopBonuses = 100;
        cm.RegisterPlayer(1, playerEntity);

        var builder = cm.CreateEntity();
        cm.AddComponent(builder, new PositionComponent());
        cm.AddComponent(builder, new UnitMotion());
        cm.AddComponent(builder, new UnitAIComponent());
        cm.AddComponent(builder, new IdentityComponent());
        cm.AddComponent(builder, new OwnershipComponent { PlayerId = 1 });

        int EntityCount() { int n = 0; foreach (var _ in cm.AllEntities) n++; return n; }

        // 1) house("own")落 gaia → 拒(实体数不变)。
        int before = EntityCount();
        executor.Apply(NetCommand.Build(1, builder.Value, "structures/athen/house", M(56), M(56)));
        Assert.Equal(before, EntityCount());

        // 2) civil_centre("own neutral")落 gaia → 放;foundation 自带 TerritoryInfluence
        //    (SpawnFoundation → RegisterForLos 装配)→ 周围变 P1 领土。
        executor.Apply(NetCommand.Build(1, builder.Value, "structures/athen/civil_centre", M(16), M(16)));
        Assert.Equal(before + 1, EntityCount());
        Assert.Equal(1, tm.GetOwner(M(20), M(20)));

        // 3) house 落 CC 领土内 → 放。
        executor.Apply(NetCommand.Build(1, builder.Value, "structures/athen/house", M(20), M(20)));
        Assert.Equal(before + 2, EntityCount());
    }
}
