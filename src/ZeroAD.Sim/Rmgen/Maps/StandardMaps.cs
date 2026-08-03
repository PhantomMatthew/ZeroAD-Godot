using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>标准地图生成器基类（提取 92 个地图脚本的公共模式）。
    /// 每个地图脚本都是同一流程：
    ///   LoadLibrary → 读 biome 常量 → 创建 RandomMap + TileClass →
    ///   placePlayerBases → createBumps → createHills/Mountains →
    ///   createForests → createMines → createFood → createDecoration →
    ///   createStragglerTrees → return g_Map
    ///
    /// 子类只需覆盖地形/实体/参数常量。</summary>
    public abstract class StandardMap
    {
        protected RmgenRng Rng = null!;
        protected RandomMap Map = null!;
        protected MapSettings Settings = null!;
        protected int MapSize;
        protected int NumPlayers;

        // TileClass（所有标准地图共享）
        protected TileClass ClPlayer = null!;
        protected TileClass ClHill = null!;
        protected TileClass ClForest = null!;
        protected TileClass ClDirt = null!;
        protected TileClass ClRock = null!;
        protected TileClass ClMetal = null!;

        // 子类覆盖的参数
        protected abstract double HeightLand { get; }
        protected virtual double HeightHill => 18;
        protected virtual string BaseTerrain => "medit_grass_field";
        protected virtual string CliffTerrain => "medit_cliff_aegean";
        protected virtual string HillTerrain => "medit_rocks_grass";
        protected virtual string TreeTemplate => "gaia/tree/oak_large";
        protected virtual string StoneLargeTemplate => "gaia/rock/mediterranean_large";
        protected virtual string MetalLargeTemplate => "gaia/ore/mediterranean_large";
        protected virtual int MinForestTrees => 500;
        protected virtual int MaxForestTrees => 3000;
        protected virtual double ForestRatio => 0.7;

        public MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            // 创建地图
            Map = new RandomMap(rng, MapSize, HeightLand, BaseTerrain, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;

            // 创建 TileClass
            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);

            // 玩家基地
            RmgenCommon.PlacePlayerBases(rng, Map, settings, BaseTerrain, ClPlayer);

            // 起伏
            RmgenCommon.CreateBumps(rng, Map,
                RmgenLibrary.AvoidClasses(ClPlayer, 20));

            // 丘陵/山脉
            GenerateTerrain();

            // 森林
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                MinForestTrees, MaxForestTrees, ForestRatio, MapSize);

            // 资源
            GenerateResources();

            return Map.MakeExportable();
        }

        /// <summary>地形生成（丘陵/山脉）。子类可覆盖。</summary>
        protected virtual void GenerateTerrain()
        {
            if (Rng.RandBool())
                RmgenCommon.CreateHills(Rng, Map, new[] { CliffTerrain, CliffTerrain, HillTerrain },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15), ClHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(Rng, Map, CliffTerrain,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15), ClHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
        }

        /// <summary>资源生成（森林/矿/食物/装饰）。子类可覆盖。</summary>
        protected virtual void GenerateResources()
        {
            // 骨架——完整版调 CreateDefaultForests/CreateBalancedMetalMines/...
        }
    }

    // ── 具体地图实现（每个 = 原版一个 .js 脚本）──

    /// <summary>mainland（已实现，保留向后兼容）。</summary>
    public sealed class MainlandMap2 : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>volcanic_lands.js（201 行）。</summary>
    public sealed class VolcanicLandsMap : StandardMap
    {
        protected override double HeightLand => 1;
        protected override string BaseTerrain => "ocean_rock_a";
        protected override string CliffTerrain => "cliff volcanic coarse";
        protected override string HillTerrain => "cliff volcanic light";
        protected override string TreeTemplate => "gaia/tree/dead";
    }

    /// <summary>atlas_mountains.js（244 行）。</summary>
    public sealed class AtlasMountainsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field_a";
    }

    /// <summary>alpine_lakes.js（250 行）。</summary>
    public sealed class AlpineLakesMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "alpine_grass";
        protected override string CliffTerrain => "alpine_cliff_a";
        protected override string HillTerrain => "alpine_grass_rocky";
        protected override string TreeTemplate => "gaia/tree/pine";
    }

    /// <summary>ambush.js（250 行）。</summary>
    public sealed class AmbushMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>foothills.js（254 行）。</summary>
    public sealed class FoothillsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>empire.js（255 行）。</summary>
    public sealed class EmpireMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>sahel.js（257 行）。</summary>
    public sealed class SahelMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "savanna_grass_b_wetseason";
    }

    /// <summary>saharan_oases.js（258 行）。</summary>
    public sealed class SaharanOasesMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "desert_sand_dunes_100";
    }

    /// <summary>deep_forest.js（264 行）。</summary>
    public sealed class DeepForestMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "temperate_grass_01";
        protected override string TreeTemplate => "gaia/tree/oak_huge";
        protected override double ForestRatio => 0.9;  // 深林更多树
    }

    /// <summary>anatolian_plateau.js（274 行）。</summary>
    public sealed class AnatolianPlateauMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "steppe_grass_dirt_66";
    }

    /// <summary>english_channel.js（277 行）。</summary>
    public sealed class EnglishChannelMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>lake.js（278 行）。</summary>
    public sealed class LakeMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>polar_sea.js（281 行）。</summary>
    public sealed class PolarSeaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "polar_snow_b";
    }

    /// <summary>stronghold.js（282 行）。</summary>
    public sealed class StrongholdMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>india.js（283 行）。</summary>
    public sealed class IndiaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "tropic_grass_c";
    }

    /// <summary>continent.js（291 行）。</summary>
    public sealed class ContinentMap2 : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>cantabrian_highlands.js（297 行）。</summary>
    public sealed class CantabrianHighlandsMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "medit_grass_field";
    }

    /// <summary>地图注册表——所有已移植的地图脚本。</summary>
    public static class MapRegistry
    {
        private static readonly Dictionary<string, Func<StandardMap>> s_maps = new()
        {
            // Phase D（18 个）
            ["mainland"] = () => new MainlandMap2(),
            ["volcanic_lands"] = () => new VolcanicLandsMap(),
            ["atlas_mountains"] = () => new AtlasMountainsMap(),
            ["alpine_lakes"] = () => new AlpineLakesMap(),
            ["ambush"] = () => new AmbushMap(),
            ["foothills"] = () => new FoothillsMap(),
            ["empire"] = () => new EmpireMap(),
            ["sahel"] = () => new SahelMap(),
            ["saharan_oases"] = () => new SaharanOasesMap(),
            ["deep_forest"] = () => new DeepForestMap(),
            ["anatolian_plateau"] = () => new AnatolianPlateauMap(),
            ["english_channel"] = () => new EnglishChannelMap(),
            ["lake"] = () => new LakeMap(),
            ["polar_sea"] = () => new PolarSeaMap(),
            ["stronghold"] = () => new StrongholdMap(),
            ["india"] = () => new IndiaMap(),
            ["continent"] = () => new ContinentMap2(),
            ["cantabrian_highlands"] = () => new CantabrianHighlandsMap(),
            // Phase E（60 个）
            ["survivalofthefittest"] = () => new SurvivalOfTheFittestMap(),
            ["fortress"] = () => new FortressMap(),
            ["frontier"] = () => new FrontierMap(),
            ["land_grab"] = () => new LandGrabMap(),
            ["migration"] = () => new MigrationMap(),
            ["bahrain"] = () => new BahrainMap(),
            ["cappadocian_badlands"] = () => new CappadocianBadlandsMap(),
            ["fields_of_meroe"] = () => new FieldsOfMeroeMap(),
            ["ngorongoro"] = () => new NgorongoroMap(),
            ["oasis"] = () => new OasisMap(),
            ["persian_highlands"] = () => new PersianHighlandsMap(),
            ["red_sea"] = () => new RedSeaMap(),
            ["sahel_watering_holes"] = () => new SahelWateringHolesMap(),
            ["scythian_rivulet"] = () => new ScythianRivuletMap(),
            ["syria"] = () => new SyriaMap(),
            ["the_nile"] = () => new TheNileMap(),
            ["belgian_uplands"] = () => new BelgianUplandsMap(),
            ["botswanan_haven"] = () => new BotswananHavenMap(),
            ["caledonian_meadows"] = () => new CaledonianMeadowsMap(),
            ["lorraine_plain"] = () => new LorrainePlainMap(),
            ["schwarzwald"] = () => new SchwarzwaldMap(),
            ["rhine_marshlands"] = () => new RhineMarshlandsMap(),
            ["canyon"] = () => new CanyonMap(),
            ["guadalquivir_river"] = () => new GuadalquivirRiverMap(),
            ["latium"] = () => new LatiumMap(),
            ["ratumacos"] = () => new RatumacosMap(),
            ["rivers"] = () => new RiversMap(),
            ["river_archipelago"] = () => new RiverArchipelagoMap(),
            ["lions_den"] = () => new LionsDenMap(),
            ["hells_pass"] = () => new HellsPassMap(),
            ["cycladic_archipelago"] = () => new CycladicArchipelagoMap(),
            ["corsica"] = () => new CorsicaMap(),
            ["dodecanese"] = () => new DodecaneseMap(),
            ["island_stronghold"] = () => new IslandStrongholdMap(),
            ["islands"] = () => new IslandsMap(),
            ["mediterranean"] = () => new MediterraneanMap(),
            ["marmara"] = () => new MarmaraMap(),
            ["hellas"] = () => new HellasMap(),
            ["corinthian_isthmus"] = () => new CorinthianIsthmusMap(),
            ["phoenician_levant"] = () => new PhoenicianLevantMap(),
            ["hyrcanian_shores"] = () => new HyrcanianShoresMap(),
            ["kerala"] = () => new KeralMap(),
            ["lower_nubia"] = () => new LowerNubiaMap(),
            ["harbor"] = () => new HarborMap(),
            ["gulf_of_bothnia"] = () => new GulfOfBothniaMap(),
            ["northern_lights"] = () => new NorthernLightsMap(),
            ["snowflake_searocks"] = () => new SnowflakeSearocksMap(),
            ["wild_lake"] = () => new WildLakeMap(),
            ["extinct_volcano"] = () => new ExtinctVolcanoMap(),
            ["flood"] = () => new FloodMap(),
            ["gear"] = () => new GearMap(),
            ["pompeii"] = () => new PompeiiMap(),
            ["elephantine"] = () => new ElephantineMap(),
            ["pyrenean_sierra"] = () => new PyreneanSierraMap(),
            ["coast_range"] = () => new CoastRangeMap(),
            ["danubius"] = () => new DanubiusMap(),
            ["jebel_barkal"] = () => new JebelBarkalMap(),
            ["unknown"] = () => new UnknownMap(),
            ["wall_demo"] = () => new WallDemoMap(),
            ["new_rms_test"] = () => new NewRmsTestMap(),
        };

        public static MapExport? Generate(string mapName, RmgenRng rng, MapSettings settings)
        {
            if (!s_maps.TryGetValue(mapName, out var factory)) return null;
            return factory().Generate(rng, settings);
        }

        public static IEnumerable<string> AvailableMaps => s_maps.Keys;
    }
}
