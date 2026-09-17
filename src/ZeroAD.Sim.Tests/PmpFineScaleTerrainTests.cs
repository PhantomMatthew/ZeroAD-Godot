using System.Collections.Generic;
using Xunit;
using ZeroAD.Godot;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 市政厅脚下 citytile/paving 是高频平铺;草地等低频贴图不走高密度烘焙。
/// </summary>
public sealed class PmpFineScaleTerrainTests
{
    [Theory]
    [InlineData("medit_city_tile", true)]
    [InlineData("medit_city_tile_02", true)]
    [InlineData("tropic_citytile_a", true)]
    [InlineData("new_alpine_citytile", true)]
    [InlineData("temperate_paving_03", true)]
    [InlineData("medit_city_pavement", true)]
    [InlineData("desert_city_tile", true)]
    [InlineData("aegean_grass_dirt_01", false)]
    [InlineData("medit_rocks_grass", false)]
    [InlineData("cliff_01", false)]
    [InlineData("", false)]
    public void IsFineScaleTerrain_ClassifiesCityPaving(string name, bool expected)
    {
        Assert.Equal(expected, PmpMap.IsFineScaleTerrain(name));
    }

    [Fact]
    public void PatchUsesFineScaleTerrain_OnlyPatchesThatContainCityTile()
    {
        const int patches = 2;
        int tiles = patches * PmpMap.PatchSize;
        var tex1 = new ushort[tiles * tiles];
        // patch (1,0): one city tile; the rest stay grass (index 0).
        int x = 1 * PmpMap.PatchSize + 2;
        int z = 0 * PmpMap.PatchSize + 3;
        tex1[z * tiles + x] = 1;
        var map = new PmpMap
        {
            Version = 7,
            PatchesPerSide = patches,
            VerticesPerSide = tiles + 1,
            TextureNames = new List<string> { "aegean_grass_dirt_01", "medit_city_tile" },
            TileTex1 = tex1,
        };

        Assert.True(map.PatchUsesFineScaleTerrain(1, 0));
        Assert.False(map.PatchUsesFineScaleTerrain(0, 0));
        Assert.False(map.PatchUsesFineScaleTerrain(0, 1));
        Assert.False(map.PatchUsesFineScaleTerrain(1, 1));
    }

    [Fact]
    public void PatchUsesFineScaleTerrain_EmptyTextures_IsFalse()
    {
        var map = new PmpMap
        {
            Version = 7,
            PatchesPerSide = 1,
            VerticesPerSide = PmpMap.PatchSize + 1,
        };
        Assert.False(map.PatchUsesFineScaleTerrain(0, 0));
    }
}
