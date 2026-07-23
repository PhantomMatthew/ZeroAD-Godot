using System.IO;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class StateDumpTests
{
    private static ComponentManager MakeWorld(uint seed)
    {
        var cm = new ComponentManager(seed);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new HealthComponent { Current = 80, Max = 100 });
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(3f), Fixed.Zero, Fixed.FromFloat(4f));
        return cm;
    }

    private static string TextDump(ComponentManager cm)
    {
        var s = new TextDumpSerializer();
        cm.SerializeFullState(s);
        return s.ToString();
    }

    [Fact]
    public void IdenticalStates_ProduceIdenticalTextDumps()
    {
        Assert.Equal(TextDump(MakeWorld(7)), TextDump(MakeWorld(7)));
    }

    [Fact]
    public void DivergedStates_DiffLocalizesEntityAndField()
    {
        var a = MakeWorld(7);
        var b = MakeWorld(7);
        // Diverge: hurt the entity on b.
        var entity = b.AllEntities[^1];
        b.QueryInterface<HealthComponent>(entity)!.Current = 1;

        string ta = TextDump(a);
        string tb = TextDump(b);
        Assert.NotEqual(ta, tb);
        // The dump carries entity sections and field lines a plain diff can localize.
        Assert.Contains("[entity ", ta);
        Assert.Contains("health", tb.ToLowerInvariant());
    }

    [Fact]
    public void WriteAll_CreatesBinaryAndTextDumps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zeroad_oos_test_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var (bin, txt) = StateDump.WriteAll(MakeWorld(7), dir, turn: 40, playerId: 2);
            Assert.True(File.Exists(bin));
            Assert.True(File.Exists(txt));
            Assert.Contains("oos_turn40_player2", bin);
            Assert.True(new FileInfo(bin).Length > 0);
            Assert.Contains("[entity ", File.ReadAllText(txt));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
