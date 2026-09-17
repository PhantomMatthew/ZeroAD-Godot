using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.RL;

namespace ZeroAD.Sim.Tests.RL;

public sealed class RlMapApplyTests
{
    [Fact]
    public void ApplyHeightGrid_MarksWaterAndSteepSlope()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        static float Height(int x, int z)
        {
            if (x <= 1 && z <= 1) return 0f;
            if (x >= 8) return x * 5f;
            return 10f;
        }
        RlMapApply.ApplyHeightGrid(cm, 16, 4f, Height, waterMeters: 2f, circular: false, modsPublic: null);
        var t = SimSystem.Terrain;
        Assert.NotNull(t);
        Assert.Equal(TerrainClass.Water, t.GetClass(Fixed.FromFloat(2f), Fixed.FromFloat(2f)));
        Assert.Equal(TerrainClass.Land, t.GetClass(Fixed.FromFloat(18f), Fixed.FromFloat(18f)));
        Assert.Equal(TerrainClass.Impassable, t.GetClass(Fixed.FromFloat(34f), Fixed.FromFloat(2f)));
    }
}
