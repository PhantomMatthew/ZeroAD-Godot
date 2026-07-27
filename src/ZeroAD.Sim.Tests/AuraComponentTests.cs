using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>AuraComponent 行为测试(对齐 Auras.js:MVP range + global + player)。
/// fixture 组合 RangeManagerTests.SpawnAt(空间索引)+ UseSiteModifierTests.World(player+tech)。</summary>
public sealed class AuraComponentTests
{
    private static (ComponentManager cm, RangeManager rm, EntityId player, TechnologyManager tm) NewWorld()
    {
        var cm = new ComponentManager(42);
        var rm = new RangeManager(cm, Fixed.FromInt(64), Fixed.FromInt(64));
        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent());
        cm.AddComponent(player, new OwnershipComponent { PlayerId = 1 });
        var tm = new TechnologyManager();
        cm.AddComponent(player, tm);
        // 空科技目录 + 一个 phase_town 占位(reqTech 门控测试用)。
        var techs = new Dictionary<string, TechnologyDefinition>
        {
            ["phase_town"] = new("phase_town", "phase", 0, 0, 0, 0, 0f,
                Array.Empty<TechRequirement>(), Array.Empty<Modification>(),
                false, null, Array.Empty<string>())
        };
        tm.Configure(new TechCatalog(techs, new Dictionary<string, IReadOnlyList<string>>()), "athen");
        cm.Players.AddPlayer(1, player);
        return (cm, rm, player, tm);
    }

    private static EntityId Spawn(ComponentManager cm, RangeManager rm, int x, int z,
        int owner, IList<string>? classes = null)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new IdentityComponent { Classes = new List<string>(classes ?? new List<string>()) });
        if (owner > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p2 = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p2, p2);
        return e;
    }

    private static void AttachAura(ComponentManager cm, EntityId src, AuraCatalog catalog, params string[] names)
    {
        var aura = new AuraComponent();
        aura.Configure(names, cm);
        cm.AddComponent(src, aura);
        cm.Auras = catalog;
    }

    private static AuraCatalog RangeCatalog(string name, float radius, float multiply,
        IReadOnlyList<string> affects, bool stackable = false) =>
        new(new Dictionary<string, AuraDefinition>
        {
            [name] = new(name, "range", radius, affects, new[] { "Player" },
                new[] { new Modification("ResourceGatherer/Rates/food.grain", null, multiply, null, affects) },
                null, stackable, name, "")
        });

    private const float GatherPath = 10f;
    private const string Path = "ResourceGatherer/Rates/food.grain";

    // ---------- range ----------

    [Fact]
    public void RangeAura_Applies_When_Target_Enters_Radius()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f, affects: new[] { "Worker" });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/farm");
        var worker = Spawn(cm, rm, 12, 10, owner: 1, classes: new[] { "Worker" });

        cm.QueryInterface<AuraComponent>(src)!.Tick(cm, rm, catalog);

        float mod = cm.Modifiers.Apply(Path, GatherPath, worker);
        Assert.Equal(17.5f, mod, 0.01f);
    }

    [Fact]
    public void RangeAura_Removes_When_Target_Leaves_Radius()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f, affects: new[] { "Worker" });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/farm");
        var worker = Spawn(cm, rm, 12, 10, owner: 1, classes: new[] { "Worker" });

        var aura = cm.QueryInterface<AuraComponent>(src)!;
        aura.Tick(cm, rm, catalog);
        Assert.Equal(17.5f, cm.Modifiers.Apply(Path, GatherPath, worker), 0.01f);

        // 移出半径:更新位置 + 通知 RangeManager 重索引 + re-Tick → diff 检测离开 → remove。
        var pos = cm.QueryInterface<PositionComponent>(worker)!;
        var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
        pos.Position = new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));
        cm.NotifyPositionChanged(worker, old, new FixedVector2D(Fixed.FromInt(50), Fixed.FromInt(50)));
        aura.Tick(cm, rm, catalog);

        Assert.Equal(GatherPath, cm.Modifiers.Apply(Path, GatherPath, worker), 0.01f);
    }

    [Fact]
    public void RangeAura_Affects_Filter_Excludes_NonClass()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f, affects: new[] { "Worker" });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/farm");
        // Cavalry 不含 Worker 类 → predicate 拒。
        var cav = Spawn(cm, rm, 12, 10, owner: 1, classes: new[] { "Cavalry" });

        cm.QueryInterface<AuraComponent>(src)!.Tick(cm, rm, catalog);

        Assert.Equal(GatherPath, cm.Modifiers.Apply(Path, GatherPath, cav), 0.01f);
    }

    [Fact]
    public void RangeAura_Player_Only_Buffs_Same_Owner()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f, affects: new[] { "Worker" });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/farm");
        // 敌方 worker(owner 2)→ affectedPlayers "Player" 只 buff 同 owner。
        var enemy = Spawn(cm, rm, 12, 10, owner: 2, classes: new[] { "Worker" });

        cm.QueryInterface<AuraComponent>(src)!.Tick(cm, rm, catalog);

        Assert.Equal(GatherPath, cm.Modifiers.Apply(Path, GatherPath, enemy), 0.01f);
    }

    [Fact]
    public void RangeAura_Stackable_Dual_Source_Stacks()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f,
            affects: new[] { "Worker" }, stackable: true);
        var src1 = Spawn(cm, rm, 10, 10, owner: 1);
        var src2 = Spawn(cm, rm, 11, 11, owner: 1);
        AttachAura(cm, src1, catalog, "test/farm");
        AttachAura(cm, src2, catalog, "test/farm");
        var worker = Spawn(cm, rm, 11, 10, owner: 1, classes: new[] { "Worker" });

        cm.QueryInterface<AuraComponent>(src1)!.Tick(cm, rm, catalog);
        cm.QueryInterface<AuraComponent>(src2)!.Tick(cm, rm, catalog);

        // 两源各自 modId(含 source entity)→ 两个 ×1.75 叠乘 = 3.0625。
        Assert.Equal(10f * 1.75f * 1.75f, cm.Modifiers.Apply(Path, GatherPath, worker), 0.02f);
    }

    [Fact]
    public void RangeAura_NonStack_Dual_Source_Applies_Once()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f,
            affects: new[] { "Worker" }, stackable: false);
        var src1 = Spawn(cm, rm, 10, 10, owner: 1);
        var src2 = Spawn(cm, rm, 11, 11, owner: 1);
        AttachAura(cm, src1, catalog, "test/farm");
        AttachAura(cm, src2, catalog, "test/farm");
        var worker = Spawn(cm, rm, 11, 10, owner: 1, classes: new[] { "Worker" });

        cm.QueryInterface<AuraComponent>(src1)!.Tick(cm, rm, catalog);
        cm.QueryInterface<AuraComponent>(src2)!.Tick(cm, rm, catalog);

        // 非 stack:modId 同 "aura/test/farm",第二源 Add 拒重 → 只 ×1.75 一次。
        Assert.Equal(17.5f, cm.Modifiers.Apply(Path, GatherPath, worker), 0.01f);
    }

    // ---------- global / player ----------

    [Fact]
    public void GlobalAura_Targets_PlayerEntity()
    {
        var (cm, rm, player, _) = NewWorld();
        var catalog = new AuraCatalog(new Dictionary<string, AuraDefinition>
        {
            ["test/global"] = new("test/global", "global", 0f, new[] { "Structure" },
                new[] { "Player" },
                new[] { new Modification("Cost/BuildTime", null, 0.9f, null, new[] { "Structure" }) },
                null, false, "g", "")
        });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/global");
        var structure = Spawn(cm, rm, 20, 20, owner: 1, classes: new[] { "Structure" });

        cm.QueryInterface<AuraComponent>(src)!.Tick(cm, rm, catalog);

        // global mod 加在 player entity;structure 查询经 owner 链命中 player 级。
        Assert.Equal(100f * 0.9f, cm.Modifiers.Apply("Cost/BuildTime", 100f, structure), 0.01f);
    }

    [Fact]
    public void PlayerAura_RequiredTech_Gates_Application()
    {
        var (cm, rm, player, tm) = NewWorld();
        var catalog = new AuraCatalog(new Dictionary<string, AuraDefinition>
        {
            ["test/player"] = new("test/player", "player", 0f, Array.Empty<string>(),
                new[] { "Player" },
                new[] { new Modification("Cost/BuildTime", null, 0.8f, null, Array.Empty<string>()) },
                "phase_town", false, "p", "")
        });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/player");
        var structure = Spawn(cm, rm, 20, 20, owner: 1, classes: new[] { "Structure" });

        var aura = cm.QueryInterface<AuraComponent>(src)!;
        aura.Tick(cm, rm, catalog);
        // reqTech 未研究 → 不 apply。
        Assert.Equal(100f, cm.Modifiers.Apply("Cost/BuildTime", 100f, structure), 0.01f);

        tm.ApplyResearch("phase_town", cm);
        aura.Tick(cm, rm, catalog);
        // 研究后 re-Tick → 翻转 apply。
        Assert.Equal(100f * 0.8f, cm.Modifiers.Apply("Cost/BuildTime", 100f, structure), 0.01f);
    }

    // ---------- lifecycle ----------

    [Fact]
    public void OnDestroy_Clears_Residual_Range_Modifiers()
    {
        var (cm, rm, _, _) = NewWorld();
        var catalog = RangeCatalog("test/farm", radius: 10, multiply: 1.75f, affects: new[] { "Worker" });
        var src = Spawn(cm, rm, 10, 10, owner: 1);
        AttachAura(cm, src, catalog, "test/farm");
        var worker = Spawn(cm, rm, 12, 10, owner: 1, classes: new[] { "Worker" });

        cm.QueryInterface<AuraComponent>(src)!.Tick(cm, rm, catalog);
        Assert.Equal(17.5f, cm.Modifiers.Apply(Path, GatherPath, worker), 0.01f);

        cm.DestroyEntity(src); // 触发 OnDeinit → 清残留 modifier。

        Assert.Equal(GatherPath, cm.Modifiers.Apply(Path, GatherPath, worker), 0.01f);
    }
}
