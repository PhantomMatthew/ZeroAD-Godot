using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>剩余地图脚本（Phase E 批量翻译）。
    /// 所有地图共享 StandardMap 基类——只覆盖地形/实体参数。
    /// 地图名按原版 maps/random/ 目录名对应。</summary>

    // ── 简单陆地地图（共享 medit biome）──

    public sealed class SurvivalOfTheFittestMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class FortressMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class FrontierMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class LandGrabMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class MigrationMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    // ── 沙漠/热带 ──

    public sealed class BahrainMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
        protected override string CliffTerrain => "desert_cliff_1";
    }

    public sealed class CappadocianBadlandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "steppe_rocks_mossy_dry_1";
    }

    public sealed class FieldsOfMeroeMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "savanna_grass_b_dryseason";
        /// <summary>上游 fields_of_meroe.json SupportedBiomes = "fields_of_meroe/"(专属,
        /// 未移植——回退最接近的 generic/nubia,同处尼罗河流域)。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => new[] { "nubia" };
    }

    public sealed class NgorongoroMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "savanna_grass_b_wetseason";
    }

    public sealed class OasisMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    public sealed class PersianHighlandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_grass_a";
    }

    public sealed class RedSeaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    public sealed class SahelWateringHolesMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "savanna_grass_b_wetseason";
    }

    public sealed class ScythianRivuletMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "steppe_grass_dirt_33";
    }

    public sealed class SyriaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_grass_a";
    }

    public sealed class TheNileMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    // ── 温带/森林 ──

    public sealed class BelgianUplandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
    }

    public sealed class BotswananHavenMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "savanna_grass_b_wetseason";
    }

    public sealed class CaledonianMeadowsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "celtic_grass_field";
    }

    public sealed class LorrainePlainMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
    }

    public sealed class SchwarzwaldMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
        protected override double ForestRatio => 0.8;
    }

    public sealed class RhineMarshlandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_mud_01";
    }

    // ── 河谷/峡谷 ──

    public sealed class CanyonMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_grass_a";
    }

    public sealed class GuadalquivirRiverMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class LatiumMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class RatumacosMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
    }

    public sealed class RiversMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class RiverArchipelagoMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class LionsDenMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class HellsPassMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "alpine_grass";
    }

    // ── 海洋/岛屿 ──

    public sealed class CycladicArchipelagoMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class CorsicaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class DodecaneseMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class IslandStrongholdMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class IslandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class MediterraneanMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class MarmaraMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class CorinthianIsthmusMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class PhoenicianLevantMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class HyrcanianShoresMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
    }

    public sealed class KeralMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "tropic_grass_c";
    }

    public sealed class LowerNubiaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    public sealed class HarborMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class GulfOfBothniaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "alpine_snow_a";
    }

    public sealed class NorthernLightsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "polar_snow_b";
    }

    public sealed class SnowflakeSearocksMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    // ── 特殊地图（保留骨架——完整版需各自独特的生成逻辑）──

    public sealed class ExtinctVolcanoMap : StandardMap
    {
        protected override double HeightLand => 1;
        protected override string BaseTerrain => "cliff volcanic light";
    }

    public sealed class FloodMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class GearMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class PompeiiMap : StandardMap
    {
        protected override double HeightLand => 1;
        protected override string BaseTerrain => "cliff volcanic light";
    }

    public sealed class ElephantineMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    public sealed class PyreneanSierraMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
    }

    public sealed class CoastRangeMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "alpine_grass";
    }

    public sealed class DanubiusMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class JebelBarkalMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    public sealed class UnknownMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class WallDemoMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    public sealed class NewRmsTestMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }
}
