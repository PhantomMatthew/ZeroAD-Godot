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
        /// <summary>基地资源标记(mainland.js clBaseResource——基地浆果/矿/树线落点互不重叠)。</summary>
        protected TileClass ClBaseResource = null!;

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

        public virtual MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;

            // 玩家基地(含 CityPatch 基地区刷漆 + 逐基地资源:浆果/矿/树线/起始动物/装饰,
            // mainland.js 的 placePlayerBases 全参数)
            RmgenCommon.PlacePlayerBases(rng, Map, settings, biome.MainTerrain0, ClPlayer, biome,
                BaseOptions(biome));

            // 起伏
            RmgenCommon.CreateBumps(rng, Map,
                RmgenLibrary.AvoidClasses(ClPlayer, 20));

            // 丘陵/山脉
            GenerateTerrain(biome);

            // 森林(mainland.js:biome 树数 + pForest1/2 混合地表格)
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);

            string ff1 = biome.ForestFloor1, ff2 = biome.ForestFloor2;
            var pForest1 = new[] { ff2 + "|" + biome.Tree1, ff2 + "|" + biome.Tree2, ff2 };
            var pForest2 = new[] { ff1 + "|" + biome.Tree4, ff1 + "|" + biome.Tree5, ff1 };
            RmgenCommon.CreateDefaultForests(rng, Map,
                new object[] { biome.MainTerrain0, ff1, ff2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 18, ClHill, 0),
                ClForest, forestTrees);

            // 资源 + 斑块(mainland.js 原参数)
            GenerateResources(biome, stragglerTrees);

            return Map.MakeExportable();
        }

        /// <summary>生成前导：字段 + biome 选择 + RandomMap + 共享 TileClass。
        /// 自定义流程的地图（覆盖 Generate）以此复用公共初始化。
        /// biome:调用方指定 > 按 SupportedBiomes 自选(上游 gamesetup "random" 行为——
        /// 多数图每局随机 biome;选择消耗抽数,在生成最前,同 setBiome(mapSettings.Biome))。</summary>
        protected void InitContext(RmgenRng rng, MapSettings settings)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            if (settings.BiomeData != null)
            {
                Biome = settings.BiomeData;
                BiomeName = "";
            }
            else
            {
                string picked = rng.PickRandom(SupportedBiomes);
                BiomeName = picked.Contains('/') ? picked : "generic/" + picked;
                Biome = BiomeLoader.Load(settings.DataRoot, picked, rng);
            }

            // 创建地图
            Map = CreateMap(Biome);
            RmgenLibrary.CurrentMap = Map;

            // 创建 TileClass
            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            ClBaseResource = new TileClass(MapSize);
        }

        /// <summary>RandomMap 创建（默认 MainTerrain0 单贴图）。基底为名单的图
        /// （上游 RandomMap 对数组逐图块 pickRandom）覆盖此钩子。</summary>
        protected virtual RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain0, Settings.CircularMap);

        /// <summary>逐基地资源参数（mainland.js 的 placePlayerBases 全表:
        /// 起始动物默认鸡 + 浆果 + 金属/石矿 + 树线 + 草饰）。强主题图覆盖。</summary>
        protected virtual RmgenCommon.PlayerBaseOptions BaseOptions(BiomeSet biome) => new()
        {
            BaseResourceClass = ClBaseResource,
            StartingAnimal = true,
            BerriesTemplate = biome.FruitBush,
            Mines = new() { (biome.MetalLarge, (string?)null, (string?)null),
                            (biome.StoneLarge, (string?)null, (string?)null) },
            TreesTemplate = biome.Tree1,
            DecorativesTemplate = biome.GrassShort,
        };

        /// <summary>无 biome 图的前导（arctic_summer 等内联常量图——上游不 LoadLibrary("rmbiome")，
        /// 也就不消耗 biome 选择抽数）。</summary>
        protected void InitContextNoBiome(RmgenRng rng, MapSettings settings, string baseTerrain)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            Map = new RandomMap(rng, MapSize, HeightLand, baseTerrain, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;

            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            ClBaseResource = new TileClass(MapSize);
        }

        /// <summary>无 biome + 基底贴图名单版（aegean_sea 等——上游 RandomMap 对名单
        /// 逐图块 pickRandom）。</summary>
        protected void InitContextNoBiome(RmgenRng rng, MapSettings settings, IReadOnlyList<string> baseTerrain)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            Map = new RandomMap(rng, MapSize, HeightLand, baseTerrain, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;

            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            ClBaseResource = new TileClass(MapSize);
        }

        /// <summary>补充环境设置——上游那几条依赖图内局部变量的调用
        /// （多为 setWaterHeight(heightSeaGround)）。表驱动的 <see cref="MapEnvironments"/>
        /// 覆盖不到，由地图类自己写。表驱动部分先施加、本钩子后施加，二者都在生成尾部，
        /// 所以只要同一张图不在两边同时抽数，抽数顺序就与上游一致。</summary>
        protected internal virtual void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng) { }

        /// <summary>本图可随机的 biome 白名单(上游 SupportedBiomes;默认全 generic——
        /// 多数上游图即如此,biome 每局随机)。强主题图覆盖。</summary>
        protected virtual IReadOnlyList<string> SupportedBiomes => BiomeLoader.KnownBiomes;

        /// <summary>本局 biome(Generate 中解析)。</summary>
        protected BiomeSet Biome = null!;

        /// <summary>本局 biome 全名（"generic/temperate" 或图专属 "alpine/winter"；
        /// 对应上游 currentBiome()。调用方以 BiomeData 直接指定时为 ""。</summary>
        protected string BiomeName { get; set; } = "";

        /// <summary>地形生成（丘陵/山脉）。子类可覆盖。</summary>
        protected virtual void GenerateTerrain(BiomeSet biome)
        {
            if (Rng.RandBool())
                RmgenCommon.CreateHills(Rng, Map,
                    new object[] { biome.Cliff, biome.Cliff, biome.Hill },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15), ClHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(Rng, Map, biome.Cliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15), ClHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
        }

        /// <summary>资源生成（斑块/森林/矿/食物/装饰/散落树,mainland.js 原参数）。</summary>
        protected virtual void GenerateResources(BiomeSet biome, int stragglerTrees)
        {
            // 泥地分层斑块(mainland.js:三种尺寸 × [main→tier1→tier2→tier3] 渐变,widths [1,1])
            RmgenCommon.CreateLayeredPatches(Rng, Map,
                new[] { RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                        RmgenLibrary.ScaleByMapSize(8, 21, MapSize) },
                new object[] {
                    new[] { biome.MainTerrain0, biome.Tier1Terrain },
                    new[] { biome.Tier1Terrain, biome.Tier2Terrain },
                    new[] { biome.Tier2Terrain, biome.Tier3Terrain } },
                new[] { 1, 1 },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                (int)RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            // 草地斑块(tier4)
            RmgenCommon.CreatePatches(Rng, Map,
                new[] { RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                        RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 15, MapSize) },
                biome.Tier4Terrain,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                (int)RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            // 金属矿/石矿
            RmgenCommon.CreateBalancedMetalMines(Rng, Map, biome.MetalLarge,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1), ClMetal);
            RmgenCommon.CreateBalancedStoneMines(Rng, Map, biome.StoneLarge,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1, ClMetal, 10), ClRock);

            // 装饰物(rock/grass/bush)
            RmgenCommon.CreateDecoration(Rng, Map,
                new[] { biome.RockMedium, biome.RockLarge, biome.GrassShort,
                        biome.Grass, biome.BushMedium, biome.BushSmall },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 0));

            // 食物:主猎物群 + 浆果丛(mainland.js 两次 createFood)
            var clFood = new TileClass(MapSize);
            RmgenCommon.CreateFood(Rng, Map,
                new[] { biome.MainHuntableAnimal, biome.SecondaryHuntableAnimal },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1,
                    ClMetal, 4, ClRock, 4, clFood, 20), clFood);
            RmgenCommon.CreateFood(Rng, Map,
                new[] { biome.FruitBush },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1,
                    ClMetal, 4, ClRock, 4, clFood, 10), clFood);

            // 散落树木(mainland.js:[oTree1, oTree2, oTree4, oTree3])
            RmgenCommon.CreateStragglerTrees(Rng, Map,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(ClForest, 8, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6, clFood, 1), ClForest,
                stragglerTrees);
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
        /// <summary>上游 alpine_lakes.json SupportedBiomes = "alpine/"（图专属 biome 目录）。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.AlpineBiomes;

        /// <summary>上游按 biome 分支的雾/饱和度三条，加 setWaterHeight(heightSeaGround=-5)。</summary>
        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
        {
            bool lateSpring = BiomeName == "alpine/late_spring";
            env.SetFogThickness(lateSpring ? 0.26 : 0.19);
            env.SetFogFactor(lateSpring ? 0.4 : 0.35);
            env.SetPPSaturation(lateSpring ? 0.48 : 0.37);
            env.SetWaterHeight(-5);
        }
    }

    /// <summary>foothills.js（254 行）。</summary>
    public sealed class FoothillsMap : StandardMap
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

    /// <summary>india.js（283 行）。</summary>
    public sealed class IndiaMap : StandardMap
    {
        protected override double HeightLand => 3;
        protected override string BaseTerrain => "tropic_grass_c";
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
            ["ambush"] = () => new AmbushMap2(),
            ["foothills"] = () => new FoothillsMap(),
            ["empire"] = () => new EmpireMap2(),
            ["sahel"] = () => new SahelMap(),
            ["saharan_oases"] = () => new SaharanOasesMap(),
            ["deep_forest"] = () => new DeepForestMap(),
            ["anatolian_plateau"] = () => new AnatolianPlateauMap(),
            ["english_channel"] = () => new EnglishChannelMap2(),
            ["lake"] = () => new LakeMap(),
            ["polar_sea"] = () => new PolarSeaMap(),
            ["stronghold"] = () => new StrongholdMap2(),
            ["india"] = () => new IndiaMap(),
            ["continent"] = () => new ContinentMap2(),
            ["cantabrian_highlands"] = () => new CantabrianHighlandsMap(),
            // Phase E（60 个）
            ["survivalofthefittest"] = () => new SurvivalOfTheFittestMap(),
            ["fortress"] = () => new FortressMap(),
            ["frontier"] = () => new FrontierMap2(),
            ["land_grab"] = () => new LandGrabMap(),
            ["migration"] = () => new MigrationMap(),
            ["bahrain"] = () => new BahrainMap2(),
            ["cappadocian_badlands"] = () => new CappadocianBadlandsMap(),
            ["fields_of_meroe"] = () => new FieldsOfMeroeMap(),
            ["ngorongoro"] = () => new NgorongoroMap2(),
            ["oasis"] = () => new OasisMap(),
            ["persian_highlands"] = () => new PersianHighlandsMap(),
            ["red_sea"] = () => new RedSeaMap2(),
            ["sahel_watering_holes"] = () => new SahelWateringHolesMap(),
            ["scythian_rivulet"] = () => new ScythianRivuletMap(),
            ["syria"] = () => new SyriaMap(),
            ["the_nile"] = () => new TheNileMap(),
            ["belgian_uplands"] = () => new BelgianUplandsMap2(),
            ["botswanan_haven"] = () => new BotswananHavenMap(),
            ["caledonian_meadows"] = () => new CaledonianMeadowsMap2(),
            ["lorraine_plain"] = () => new LorrainePlainMap(),
            ["schwarzwald"] = () => new SchwarzwaldMap2(),
            ["rhine_marshlands"] = () => new RhineMarshlandsMap(),
            ["canyon"] = () => new CanyonMap2(),
            ["guadalquivir_river"] = () => new GuadalquivirRiverMap(),
            ["latium"] = () => new LatiumMap(),
            ["ratumacos"] = () => new RatumacosMap2(),
            ["rivers"] = () => new RiversMap2(),
            ["river_archipelago"] = () => new RiverArchipelagoMap2(),
            ["lions_den"] = () => new LionsDenMap2(),
            ["hells_pass"] = () => new HellsPassMap2(),
            ["cycladic_archipelago"] = () => new CycladicArchipelagoMap2(),
            ["corsica"] = () => new CorsicaMap2(),
            ["dodecanese"] = () => new DodecaneseMap2(),
            ["island_stronghold"] = () => new IslandStrongholdMap(),
            ["islands"] = () => new IslandsMap2(),
            ["mediterranean"] = () => new MediterraneanMap(),
            ["marmara"] = () => new MarmaraMap2(),
            ["hellas"] = () => new HellasMap(),
            ["corinthian_isthmus"] = () => new CorinthianIsthmusMap2(),
            ["phoenician_levant"] = () => new PhoenicianLevantMap(),
            ["hyrcanian_shores"] = () => new HyrcanianShoresMap(),
            ["kerala"] = () => new KeralMap(),
            ["lower_nubia"] = () => new LowerNubiaMap(),
            ["harbor"] = () => new HarborMap2(),
            ["gulf_of_bothnia"] = () => new GulfOfBothniaMap(),
            ["northern_lights"] = () => new NorthernLightsMap(),
            ["snowflake_searocks"] = () => new SnowflakeSearocksMap(),
            ["wild_lake"] = () => new WildLakeMap(),
            ["extinct_volcano"] = () => new ExtinctVolcanoMap(),
            ["flood"] = () => new FloodMap(),
            ["gear"] = () => new GearMap2(),
            ["pompeii"] = () => new PompeiiMap2(),
            ["elephantine"] = () => new ElephantineMap(),
            ["pyrenean_sierra"] = () => new PyreneanSierraMap2(),
            ["coast_range"] = () => new CoastRangeMap2(),
            ["danubius"] = () => new DanubiusMap(),
            ["jebel_barkal"] = () => new JebelBarkalMap(),
            ["unknown"] = () => new UnknownMap(),
            ["wall_demo"] = () => new WallDemoMap(),
            ["new_rms_test"] = () => new NewRmsTestMap(),
            // Phase F（逐字翻译——依赖完整 rmgen 库的图）
            ["alpine_valley"] = () => new AlpineValleyMap(),
            ["arctic_summer"] = () => new ArcticSummerMap(),
            ["aegean_sea"] = () => new AegeanSeaMap(),
            ["archipelago"] = () => new ArchipelagoMap(),
            ["african_plains"] = () => new AfricanPlainsMap(),
            ["ardennes_forest"] = () => new ArdennesForestMap(),
        };

        public static MapExport? Generate(string mapName, RmgenRng rng, MapSettings settings)
        {
            if (!s_maps.TryGetValue(mapName, out var factory)) return null;
            var map = factory();
            var export = map.Generate(rng, settings);

            // 环境设置（上游各图末尾的 setSkySet/setSun*/setWater*/setFog*/setPP*）。
            // 表驱动部分先施加，再让地图类补依赖局部变量的那几条。
            MapEnvironments.Apply(mapName, export.Environment, rng, settings.Size);
            map.ApplyExtraEnvironment(export.Environment, rng);
            return export;
        }

        public static IEnumerable<string> AvailableMaps => s_maps.Keys;
    }
}
