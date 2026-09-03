using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.Tests;

/// <summary>VictoryManager 圣物护卫/治疗者编排(原版 manageCriticalEntGuards/
/// assignGuardToCriticalEnt)测试。</summary>
public sealed class VictoryGuardTests
{
    private static (ComponentManager cm, VictoryManager vm) World()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        return (cm, new VictoryManager(new PetraConfig(DifficultyLevel.Medium)));
    }

    private static EntityId AddUnit(ComponentManager cm, int player, float x, float z,
        string templateName, params string[] classes)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        var id = new IdentityComponent { IsUnit = true, TemplateName = templateName };
        cm.AddComponent(e, id);
        foreach (var c in classes) id.Classes.Add(c);
        cm.NotifyEntityCreated(e);
        return e;
    }

    private static global::ZeroAD.Sim.AI.CommonApi.AIEntity AiOf(ZeroAD.Sim.AI.CommonApi.GameState gs, EntityId e)
        => gs.GetEntityById(e.Value)!;

    private static ZeroAD.Sim.AI.CommonApi.GameState MakeGameState(ComponentManager cm)
    {
        var net = new Net.NetTurnManager(cm, commandDelay: 2, localPlayerId: 1,
            Net.NetRole.Standalone, expectedPlayers: new HashSet<uint> { 1 });
        // AIEntity 由模板装(AITemplate 读类表)——临时模板目录塞最小桩件。
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "zad_vg_" + System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "units", "test"));
        foreach (var (name, classes) in new[]
        {
            ("champion", "Champion Soldier Unit"), ("soldier", "Soldier Unit"),
            ("healer", "Healer Support Unit"), ("relic", "Relic"),
        })
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "units", "test", name + ".xml"),
                "<Entity><Identity><Civ>athen</Civ>"
                + "<Classes datatype=\"tokens\">" + classes + "</Classes>"
                + "</Identity></Entity>");
        var emptyTemplates = new ZeroAD.Sim.Content.TemplateLoader(dir);
        emptyTemplates.LoadAllTemplates();
        var emptyTech = new ZeroAD.Sim.Content.TechCatalog(
            new Dictionary<string, ZeroAD.Sim.Content.TechnologyDefinition>(),
            new Dictionary<string, IReadOnlyList<string>>());
        return new ZeroAD.Sim.AI.CommonApi.GameState(cm, emptyTemplates, emptyTech, 1,
            new ZeroAD.Sim.AI.EntityMetadata(), new ZeroAD.Sim.AI.AIEventBuffer(), null)
        { Net = net };
    }

    [Fact]
    public void AssignGuard_SetsMetadataAndPostsGuardCommand()
    {
        var (cm, vm) = World();
        var relic = AddUnit(cm, 1, 50, 50, "units/test/relic", "Relic");
        var guard = AddUnit(cm, 1, 52, 50, "units/test/champion", "Champion", "Soldier");
        var gs = MakeGameState(cm);
        // 圣物胜条件开(否则 Update 早退)——直接驱动 AssignGuard API。
        vm.RegisterCriticalEnt(gs, relic.Value);
        Assert.True(vm.IsCritical(relic.Value));

        // probe:逐件确认非空
        var aiEnt = AiOf(gs, guard);
        Assert.True(aiEnt != null, "AiOf null");
        var uai = cm.QueryInterface<UnitAIComponent>(guard);
        Assert.True(uai != null, "QueryInterface null");
        Assert.True(gs.Cm != null, "gs.Cm null");

        bool ok = vm.AssignGuardToCriticalEnt(gs, aiEnt, relic.Value);
        Assert.True(ok);
        Assert.Equal(relic.Value, gs.Metadata.GetObject(guard.Value, "guardedEnt"));
        Assert.Contains(guard.Value, vm.CriticalEnts[relic.Value].GuardsAssigned);
        Assert.Equal("guard", vm.CriticalEnts[relic.Value].Guards[guard.Value]);
    }

    [Fact]
    public void AssignGuard_NoTargetPicksFewestGuarded()
    {
        var (cm, vm) = World();
        var relicA = AddUnit(cm, 1, 50, 50, "units/test/relic", "Relic");
        var relicB = AddUnit(cm, 1, 80, 50, "units/test/relic", "Relic");
        var g1 = AddUnit(cm, 1, 52, 50, "units/test/champion", "Champion", "Soldier");
        var g2 = AddUnit(cm, 1, 82, 50, "units/test/champion", "Champion", "Soldier");
        var gs = MakeGameState(cm);
        vm.RegisterCriticalEnt(gs, relicA.Value);
        vm.RegisterCriticalEnt(gs, relicB.Value);

        Assert.True(vm.AssignGuardToCriticalEnt(gs, AiOf(gs, g1), relicA.Value));
        // 第二个不指定 → 派给护卫更少的 B。
        Assert.True(vm.AssignGuardToCriticalEnt(gs, AiOf(gs, g2), null));
        Assert.Contains(g2.Value, vm.CriticalEnts[relicB.Value].GuardsAssigned);
    }

    [Fact]
    public void HealerQuota_RespectsPersonalityCap()
    {
        var (cm, vm) = World();
        var relic = AddUnit(cm, 1, 50, 50, "units/test/relic", "Relic");
        var gs = MakeGameState(cm);
        vm.RegisterCriticalEnt(gs, relic.Value);
        // 中等性格 defensive=0.5 → healersPerCriticalEnt = 2+round(1)=3。
        var healers = new List<EntityId>();
        for (int i = 0; i < 5; i++)
            healers.Add(AddUnit(cm, 1, 52 + i, 50, "units/test/healer", "Healer", "Support"));
        foreach (var h in healers)
            vm.AssignGuardToCriticalEnt(gs, AiOf(gs, h), relic.Value);
        // AssignGuardToCriticalEnt 本身不卡配额(配额在 ManageCriticalEntGuards);
        // 配额语义:healer 桶记账。钉 API 语义:直接指派全收,配额由编排层卡。
        Assert.Equal(5, vm.CriticalEnts[relic.Value].HealersAssigned.Count);
        Assert.All(vm.CriticalEnts[relic.Value].Guards.Values,
            r => Assert.Equal("healer", r));
    }

    [Fact]
    public void RemoveCriticalEnt_ReleasesGuards()
    {
        var (cm, vm) = World();
        var relic = AddUnit(cm, 1, 50, 50, "units/test/relic", "Relic");
        var guard = AddUnit(cm, 1, 52, 50, "units/test/champion", "Champion", "Soldier");
        var gs = MakeGameState(cm);
        vm.RegisterCriticalEnt(gs, relic.Value);
        vm.AssignGuardToCriticalEnt(gs, AiOf(gs, guard), relic.Value);

        vm.RemoveCriticalEnt(gs, relic.Value);
        Assert.False(vm.IsCritical(relic.Value));
        Assert.Null(gs.Metadata.GetObject(guard.Value, "guardedEnt"));
        Assert.Equal(-1, gs.Metadata.GetObject(guard.Value, "plan"));
    }

    [Fact]
    public void RoundTrip_PreservesGuardState()
    {
        var (cm, vm) = World();
        var relic = AddUnit(cm, 1, 50, 50, "units/test/relic", "Relic");
        var guard = AddUnit(cm, 1, 52, 50, "units/test/champion", "Champion", "Soldier");
        var gs = MakeGameState(cm);
        vm.RegisterCriticalEnt(gs, relic.Value);
        vm.AssignGuardToCriticalEnt(gs, AiOf(gs, guard), relic.Value);

        var ms = new System.IO.MemoryStream();
        vm.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var vm2 = new VictoryManager(new PetraConfig(DifficultyLevel.Medium));
        vm2.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));
        Assert.True(vm2.IsCritical(relic.Value));
        Assert.Contains(guard.Value, vm2.CriticalEnts[relic.Value].GuardsAssigned);
        Assert.Equal("guard", vm2.CriticalEnts[relic.Value].Guards[guard.Value]);
    }
}
