using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>Fogging/Mirage lifecycle, ported from Fogging.js + Mirage.js: mirage spawn on
/// VISIBLE→FOGGED, swap-back on re-scout, mirage reuse, orphaning on parent destruction,
/// and template parsing (<Fogging/> + Visibility/RetainInFog).</summary>
public sealed class FogMirageLifecycleTests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld()
    {
        var cm = new ComponentManager(42);
        var rm = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        return (cm, rm);
    }

    private static EntityId SpawnSeer(ComponentManager cm, RangeManager rm, int x, int z, int owner, int range)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new VisionComponent());
        cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(range);
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    /// <summary>An enemy structure with fogging + retain-in-fog (mirrors template_structure.xml).</summary>
    private static EntityId SpawnFort(ComponentManager cm, RangeManager rm, int x, int z, int owner = 2)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new HealthComponent());
        var hp = cm.QueryInterface<HealthComponent>(e)!; // set AFTER AddComponent: OnInit resets
        hp.Current = 800;
        hp.Max = 1000;
        var fog = new FoggingComponent();
        cm.AddComponent(e, fog);
        cm.QueryInterface<FoggingComponent>(e)!.TemplateName = "structures/athen_fortress";
        cm.AddComponent(e, new VisibilityComponent());
        cm.QueryInterface<VisibilityComponent>(e)!.RetainInFog = true;
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        cm.NotifyOwnerChanged(e, -1, owner); // activates fogging (mirrors MT_OwnershipChanged)
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static void Move(ComponentManager cm, EntityId e, int fromX, int fromZ, int toX, int toZ)
    {
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(toX), Fixed.Zero, Fixed.FromInt(toZ));
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(fromX), Fixed.FromInt(fromZ)),
            new FixedVector2D(Fixed.FromInt(toX), Fixed.FromInt(toZ)));
    }

    [Fact]
    public void Fogged_SpawnsMirage_FrozenCopy()
    {
        var (cm, rm) = NewWorld();
        var seer = SpawnSeer(cm, rm, 190, 195, owner: 1, range: 40);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(fort, 1));

        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData(); // fort FOGGED → LoadMirage
        var fog = cm.QueryInterface<FoggingComponent>(fort)!;
        Assert.True(fog.IsMiraged(1));
        var mirageId = fog.MirageOf[1];
        Assert.NotNull(mirageId);

        var mirage = cm.QueryInterface<MirageComponent>(mirageId!.Value);
        Assert.NotNull(mirage);
        Assert.Equal(fort, mirage!.Parent);
        Assert.Equal(1, mirage.Player);
        Assert.Equal(800, mirage.FrozenHealthCurrent);
        Assert.Equal(1000, mirage.FrozenHealthMax);
        Assert.Equal(2, cm.QueryInterface<OwnershipComponent>(mirageId.Value)!.PlayerId);
        var mpos = cm.QueryInterface<PositionComponent>(mirageId.Value)!.Position;
        Assert.Equal(Fixed.FromInt(200), mpos.X);
        Assert.Equal(Fixed.FromInt(200), mpos.Z);

        rm.UpdateVisibilityData(); // requested re-eval: parent now hidden behind its mirage
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(fort, 1));
        Assert.Equal(LosVisibility.Fogged, rm.GetLosVisibility(mirageId.Value, 1));
        Assert.True(LosVisibility.Hidden == rm.GetLosVisibility(mirageId.Value, 2),
            "a mirage is only ever visible to its own player");
    }

    [Fact]
    public void VisibleAgain_SwapsBack_KeepsMirage()
    {
        var (cm, rm) = NewWorld();
        var seer = SpawnSeer(cm, rm, 190, 195, owner: 1, range: 40);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData();
        var mirageId = cm.QueryInterface<FoggingComponent>(fort)!.MirageOf[1]!.Value;

        var swaps = new List<MirageSwapBackEvent>();
        cm.Events.MirageSwapBack += e => swaps.Add(e);

        Move(cm, seer, 40, 40, 190, 195);
        rm.UpdateVisibilityData();

        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(fort, 1));
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(mirageId, 1));
        Assert.False(cm.QueryInterface<FoggingComponent>(fort)!.IsMiraged(1));
        Assert.NotNull(cm.QueryInterface<MirageComponent>(mirageId)); // kept for reuse
        var swap = Assert.Single(swaps);
        Assert.Equal(mirageId, swap.Mirage);
        Assert.Equal(fort, swap.Parent);
        Assert.Equal(1, swap.Player);
    }

    [Fact]
    public void SecondFogCycle_ReusesMirage_RefreshesFrozenData()
    {
        var (cm, rm) = NewWorld();
        var seer = SpawnSeer(cm, rm, 190, 195, owner: 1, range: 40);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData();
        var firstMirage = cm.QueryInterface<FoggingComponent>(fort)!.MirageOf[1]!.Value;

        // Re-scout, fort takes damage while visible, then fog closes again.
        Move(cm, seer, 40, 40, 190, 195);
        rm.UpdateVisibilityData();
        cm.QueryInterface<HealthComponent>(fort)!.Current = 300;
        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData();

        var fog = cm.QueryInterface<FoggingComponent>(fort)!;
        Assert.Equal(firstMirage, fog.MirageOf[1]!.Value); // reused, not respawned
        Assert.Equal(300, cm.QueryInterface<MirageComponent>(firstMirage)!.FrozenHealthCurrent);
    }

    [Fact]
    public void UnscoutedEnemy_NeverFogged_NoMirage()
    {
        var (cm, rm) = NewWorld();
        SpawnSeer(cm, rm, 40, 40, owner: 1, range: 16);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        rm.UpdateVisibilityData();

        var fog = cm.QueryInterface<FoggingComponent>(fort)!;
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(fort, 1));
        Assert.False(fog.WasSeen(1));
        Assert.False(fog.IsMiraged(1));
        Assert.Null(fog.MirageOf[1]);
    }

    [Fact]
    public void ParentDestroyed_WhileMirageHidden_MirageDestroyedToo()
    {
        var (cm, rm) = NewWorld();
        var seer = SpawnSeer(cm, rm, 190, 195, owner: 1, range: 40);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData();
        var mirageId = cm.QueryInterface<FoggingComponent>(fort)!.MirageOf[1]!.Value;
        Move(cm, seer, 40, 40, 190, 195);
        rm.UpdateVisibilityData(); // mirage now HIDDEN behind the visible parent

        cm.DestroyEntity(fort);
        Assert.Null(cm.QueryInterface<MirageComponent>(mirageId));
    }

    [Fact]
    public void ParentDestroyed_WhileFogged_OrphanSelfDestructsOnRescout()
    {
        var (cm, rm) = NewWorld();
        var seer = SpawnSeer(cm, rm, 190, 195, owner: 1, range: 40);
        var fort = SpawnFort(cm, rm, 200, 200);
        rm.UpdateVisibilityData();
        Move(cm, seer, 190, 195, 40, 40);
        rm.UpdateVisibilityData();
        var mirageId = cm.QueryInterface<FoggingComponent>(fort)!.MirageOf[1]!.Value;

        cm.DestroyEntity(fort); // mirage is FOGGED → orphaned, still standing in the fog
        var mirage = cm.QueryInterface<MirageComponent>(mirageId);
        Assert.NotNull(mirage);
        Assert.Equal(default, mirage!.Parent);

        Move(cm, seer, 40, 40, 190, 195);
        rm.UpdateVisibilityData(); // tile visible → orphan mirage goes HIDDEN → self-destructs
        Assert.Null(cm.QueryInterface<MirageComponent>(mirageId));
    }

    [Fact]
    public void TemplateParsing_Fogging_RetainInFog()
    {
        var structure = Content.TemplateLoader.ExtractStatsFromNode(Templates.ParamNode.LoadXml(
            "<Entity>" +
            "<Fogging/>" +
            "<Visibility><RetainInFog>true</RetainInFog></Visibility>" +
            "<Vision><Range>4</Range></Vision>" +
            "</Entity>"));
        Assert.True(structure.HasFogging);
        Assert.True(structure.RetainInFog);
        Assert.Equal(4, structure.VisionRange);

        var unit = Content.TemplateLoader.ExtractStatsFromNode(Templates.ParamNode.LoadXml(
            "<Entity>" +
            "<Visibility><RetainInFog>false</RetainInFog></Visibility>" +
            "<Vision><Range>12</Range></Vision>" +
            "</Entity>"));
        Assert.False(unit.HasFogging);
        Assert.False(unit.RetainInFog);
        Assert.Equal(12, unit.VisionRange);
    }

    [Fact]
    public void Assemble_AttachesFogging_ActivatesOnOwnership()
    {
        var (cm, rm) = NewWorld();
        var stats = new Content.TemplateStats
        {
            HasFogging = true,
            RetainInFog = true,
            VisionRange = 4
        };
        var e = cm.CreateEntity();
        EntityAssembler.AssembleUnit(cm, e, "structures/athen_fortress", stats, 200, 200);

        var fog = cm.QueryInterface<FoggingComponent>(e);
        Assert.NotNull(fog);
        Assert.False(fog!.Activated); // no owner yet
        Assert.Equal("structures/athen_fortress", fog.TemplateName);
        Assert.True(cm.QueryInterface<VisibilityComponent>(e)!.RetainInFog);

        cm.AddComponent(e, new OwnershipComponent { PlayerId = 2 });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, 2);
        Assert.True(fog.Activated);
        Assert.Equal(4, (int)cm.QueryInterface<VisionComponent>(e)!.Range.ToIntRoundToNearest());
    }
}
