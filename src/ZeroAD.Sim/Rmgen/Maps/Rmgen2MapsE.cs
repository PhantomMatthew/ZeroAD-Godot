using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>ngorongoro.js（逐字移植）——以 ngorongoro.png 真实海拔为底，
    /// 中央 eden 与高地分三套 biome 渲染；placePlayersNomad 与环境参数按约定省略。</summary>
    public sealed class NgorongoroMap2 : Rmgen2Map
    {
        protected override string? ForcedBiome => "generic/savanna";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "eden", "highlands" };

        /// <summary>land 类在高度图载入后由脚本显式标记。</summary>
        protected override bool PaintLandClass => false;

        protected override void OverrideBiome(BiomeSet biome)
        {
            biome.RoadWild = "savanna_riparian_dry";
            biome.Road = "road2";

            biome.MetalLarge = "gaia/ore/savanna_large";
            biome.MetalSmall = "gaia/ore/tropical_small";
            biome.Fish = "gaia/fish/tilapia";
            biome.Tree1 = "gaia/tree/baobab";
            biome.Tree2 = "gaia/tree/baobab";
            biome.Tree3 = "gaia/tree/baobab";
            biome.Tree4 = "gaia/tree/baobab";
            biome.Tree5 = "gaia/tree/baobab";

            biome.Grass = "actor|props/flora/grass_savanna.xml";
            biome.GrassShort = "actor|props/flora/grass_soft_dry_tuft_a.xml";
            biome.RockLarge = "actor|geology/stone_savanna_med.xml";
            biome.RockMedium = "actor|geology/stone_savanna_med.xml";
            biome.BushMedium = "actor|props/flora/bush_desert_dry_a.xml";
            biome.BushSmall = "actor|props/flora/bush_dry_a.xml";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clEden = c.Cl("eden");
            var clHighlands = c.Cl("highlands");

            double heightScale = MapSize / 320.0;
            double heightHighlands = 45 * heightScale;
            double heightEden = 60 * heightScale;
            const double heightMax = 150;

            LoadNgorongoroHeightmap(heightMax);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.1, 0.5, MapSize), 1),
                null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClLand),
                null);

            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.14, MapSize), c.MapCenter),
                new TileClassPainter(clEden),
                new HeightConstraint(Map, double.NegativeInfinity, heightEden));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(clHighlands),
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(Map, heightHighlands, double.PositiveInfinity),
                    RmgenLibrary.AvoidClasses(clEden, 0),
                }));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Cliff, Rng),
                    new TileClassPainter(c.ClMountain),
                },
                new SlopeConstraint(Map, 2, double.PositiveInfinity));

            if (!Settings.Nomad)
                PlacePlayersNgorongoro(clEden, clHighlands);

            SetBiomeLowlands(Biome);
            AddLowlands(clEden, clHighlands);

            SetBiomeHighlands(Biome);
            AddHighlands(clHighlands);

            SetBiomeEden(Biome);
            AddEden(clEden);

            // placePlayersNomad 与光照/雾/后期处理是表现层功能，本移植层不处理。
        }

        private void PlacePlayersNgorongoro(TileClass clEden, TileClass clHighlands)
        {
            var c = Ctx;
            double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
            var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClMountain, 5, clHighlands, 5, clEden, 5),
                    RmgenLibrary.StayClasses(c.ClLand, baseRadius),
                }));

            if (!placement.HasValue)
                return;

            var (playerIDs, playerPosition) = placement.Value;
            c.CreateBases(playerIDs, playerPosition, true);

            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(baseRadius * 0.8),
                        0.95, 0.6, double.PositiveInfinity, position),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        Map.GetHeight(position), 6),
                    null);
        }

        private void AddLowlands(TileClass clEden, TileClass clHighlands)
        {
            var c = Ctx;

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                        clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddSmallMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 6,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 6,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "scarce" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 8, c.ClMetal, 3,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 3, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "normal" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 2, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "big" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 2, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "unique" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 4, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 12, c.ClRock, 2, clEden, 2, clHighlands, 2 },
                    Sizes = new[] { "big" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));
        }

        private void AddHighlands(TileClass clHighlands)
        {
            var c = Ctx;

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddSmallMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 3,
                        c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 3,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 8, c.ClMetal, 3,
                        c.ClMountain, 3, c.ClPlayer, 20, c.ClRock, 3 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 3, c.ClPlayer, 20, c.ClRock, 2 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 3, c.ClPlayer, 12, c.ClRock, 2 },
                    Stay = new object[] { clHighlands, 2 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));
        }

        private void AddEden(TileClass clEden)
        {
            var c = Ctx;

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClMetal, 3 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClMetal, 3 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddSmallMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClMetal, 3 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 3, c.ClMetal, 3 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 8, c.ClMetal, 3,
                        c.ClMountain, 8, c.ClPlayer, 20, c.ClRock, 3 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "similar" }, Amounts = new[] { "scarce" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 2, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 3, c.ClPlayer, 20, c.ClRock, 2 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 8, c.ClPlayer, 12, c.ClRock, 2 },
                    Stay = new object[] { clEden, 2 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same" }, Amounts = new[] { "scarce" },
                },
            }));
        }

        private static void SetBiomeLowlands(BiomeSet biome)
        {
            biome.MainHuntableAnimal = "gaia/fauna_giraffe";
            biome.SecondaryHuntableAnimal = "gaia/fauna_zebra";

            biome.MainTerrain = new List<string> { "savanna_riparian_bank" };
            biome.ForestFloor1 = "savanna_dirt_rocks_b";
            biome.ForestFloor2 = "savanna_dirt_rocks_c";
            biome.Tier1Terrain = "savanna_dirt_rocks_a";
            biome.Tier2Terrain = "savanna_grass_a";
            biome.Tier3Terrain = "savanna_grass_b";
            biome.Tier4Terrain = "savanna_forest_floor_a";
        }

        private static void SetBiomeHighlands(BiomeSet biome)
        {
            biome.MainHuntableAnimal = "gaia/fauna_lioness";
            biome.SecondaryHuntableAnimal = "gaia/fauna_lion";

            biome.MainTerrain = new List<string> { "savanna_grass_a_wetseason" };
            biome.ForestFloor1 = "savanna_grass_a";
            biome.ForestFloor2 = "savanna_grass_b";
            biome.Tier1Terrain = "savanna_grass_a_wetseason";
            biome.Tier2Terrain = "savanna_grass_b_wetseason";
            biome.Tier3Terrain = "savanna_shrubs_a_wetseason";
            biome.Tier4Terrain = "savanna_shrubs_b";
        }

        private static void SetBiomeEden(BiomeSet biome)
        {
            biome.MainHuntableAnimal = "gaia/fauna_rhinoceros_white";
            biome.SecondaryHuntableAnimal = "gaia/fauna_elephant_african_bush";
        }

        /// <summary>g_Map.LoadHeightmapImage("ngorongoro.png", 0, 150)。</summary>
        private void LoadNgorongoroHeightmap(double maxHeight)
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "ngorongoro.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, maxHeight), null);
        }
    }

    /// <summary>pompeii.js（逐字移植）——以 pompeii.png 真实海拔为底的维苏威火山海岸图，
    /// 包含熔岩、烟雾、码头/废码头和遗迹装饰；placePlayersNomad 与环境参数按约定省略。</summary>
    public sealed class PompeiiMap2 : Rmgen2Map
    {
        private const string LavaOuter = "LavaTest06";
        private const string LavaInner = "LavaTest05";
        private const string LavaCenter = "LavaTest04";
        private const string MainTerrain = "ocean_rock_a";
        private const string PompeiiCliffTerrain = "ocean_rock_b";

        private const string ColumnsDoric = "gaia/ruins/column_doric";
        private const string RomanStatue = "gaia/ruins/stone_statues_roman";
        private const string UnfinishedTemple = "gaia/ruins/unfinished_greek_temple";
        private const string Dock = "structures/rome/dock";
        private const string DockRubble = "rubble/rubble_rome_dock";

        private const string Smoke1 = "actor|particle/smoke_volcano.xml";
        private const string Smoke2 = "actor|particle/smoke_curved.xml";
        private const string Skeleton = "actor|props/special/eyecandy/skeleton.xml";

        private static readonly string[] Shipwrecks =
        {
            "actor|props/special/eyecandy/shipwreck_hull.xml",
            "actor|props/special/eyecandy/shipwreck_ram_side.xml",
            "actor|props/special/eyecandy/shipwreck_sail_boat.xml",
            "actor|props/special/eyecandy/shipwreck_sail_boat_cut.xml",
            "actor|props/special/eyecandy/barrels_floating.xml",
        };

        private static readonly string[] Statues =
        {
            "actor|props/special/eyecandy/statue_aphrodite_huge.xml",
            "actor|props/special/eyecandy/sele_colonnade.xml",
            "actor|props/special/eyecandy/well_1_b.xml",
            "actor|props/special/eyecandy/anvil.xml",
            "actor|props/special/eyecandy/wheel_laying.xml",
            "actor|props/special/eyecandy/vase_rome_a.xml",
        };

        protected override string? ForcedBiome => "generic/aegean";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses
            => new[] { "decorative", "lava", "dock" };

        /// <summary>水/陆类取决于高度图，不能由基类预刷。</summary>
        protected override bool PaintLandClass => false;

        protected override void OverrideBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string> { MainTerrain };
            biome.ForestFloor1 = "dirt_burned";
            biome.ForestFloor2 = "shoreline_stoney_a";
            biome.Tier1Terrain = "rock_metamorphic";
            biome.Tier2Terrain = "fissures";
            biome.Tier3Terrain = LavaOuter;
            biome.Tier4Terrain = PompeiiCliffTerrain;
            biome.RoadWild = "road1";
            biome.Road = "road1";
            biome.Water = MainTerrain;
            biome.Cliff = new List<string> { PompeiiCliffTerrain };

            biome.MainHuntableAnimal = "gaia/fauna_goat";
            biome.SecondaryHuntableAnimal = "birds/buzzard";
            biome.FruitBush = "gaia/fauna_chicken";
            biome.Fish = "gaia/fish/generic";
            biome.Tree1 = "gaia/tree/dead";
            biome.Tree2 = "gaia/tree/oak_dead";
            biome.Tree3 = "gaia/tree/dead";
            biome.Tree4 = "gaia/tree/oak_dead";
            biome.Tree5 = "gaia/tree/dead";
            biome.StoneSmall = "gaia/rock/alpine_small";

            biome.Grass = "actor|props/flora/grass_field_parched_short.xml";
            biome.GrassShort = "actor|props/flora/grass_soft_dry_tuft_a.xml";
            biome.BushMedium = "actor|props/special/eyecandy/barrels_buried.xml";
            biome.BushSmall = "actor|props/special/eyecandy/handcart_1_broken.xml";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clDecorative = c.Cl("decorative");
            var clLava = c.Cl("lava");
            var clDock = c.Cl("dock");

            double heightScale = MapSize / 320.0;
            double heightSeaGround = -30 * heightScale;
            double heightDockMin = -6 * heightScale;
            double heightWaterLevel = 0 * heightScale;
            double heightDockMax = 1 * heightScale;
            double heightLavaVesuv = 38 * heightScale;
            const double heightMountains = 140;

            LoadPompeiiHeightmap(heightMountains);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 2),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, 0.8, 1),
                null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClWater),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClLand),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(PompeiiCliffTerrain, Rng),
                    new TileClassPainter(c.ClMountain),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClWater, 2),
                    new SlopeConstraint(Map, 2, double.PositiveInfinity),
                }));

            Area? areaVesuv = CreateVesuvArea(clLava, heightLavaVesuv);

            if (areaVesuv != null)
                RmgenLibrary.CreateObjectGroupsByAreas(Rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(Rng, Smoke1, 1, 1, 0, 4),
                        new ScatterObject(Rng, Smoke2, 2, 2, 0, 4),
                    }),
                    0,
                    RmgenLibrary.StayClasses(clLava, 0),
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize),
                    20,
                    new[] { areaVesuv });

            if (!Settings.Nomad)
                PlacePlayersPompeii();

            PlaceDocks(clDock, heightDockMin, heightDockMax);
            AddTerrainAndResources(clLava);
            AddRuins(clDecorative, clLava);

            // placePlayersNomad 与水色/光照/雾/后期处理是表现层功能，本移植层不处理。
        }

        private Area? CreateVesuvArea(TileClass clLava, double heightLavaVesuv)
        {
            double x1 = Ctx.MapCenter.X;
            double y1 = RmgenLibrary.FractionToTiles(0.3, MapSize);
            double x2 = RmgenLibrary.FractionToTiles(0.7, MapSize);
            double y2 = RmgenLibrary.FractionToTiles(0.15, MapSize);

            // 上游 RectPlacer 用 bounding box；这里手动规整反向 y 坐标。
            return RmgenLibrary.CreateArea(
                new RectPlacer((int)Math.Min(x1, x2), (int)Math.Min(y1, y2),
                    (int)Math.Max(x1, x2), (int)Math.Max(y1, y2)),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { LavaOuter, LavaInner, LavaCenter },
                        new[] { RmgenLibrary.ScaleByMapSize(1, 3, MapSize), 2 }, Rng),
                    new TileClassPainter(clLava),
                },
                new HeightConstraint(Map, heightLavaVesuv, double.PositiveInfinity));
        }

        private void PlacePlayersPompeii()
        {
            var c = Ctx;
            var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClMountain, 5),
                    RmgenLibrary.StayClasses(c.ClLand, RmgenLibrary.ScaleByMapSize(5, 15, MapSize)),
                }));

            if (!placement.HasValue)
                return;

            var (playerIDs, playerPosition) = placement.Value;
            c.CreateBases(playerIDs, playerPosition, false);

            double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(baseRadius * 0.8),
                        0.95, 0.6, double.PositiveInfinity, position),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        Map.GetHeight(position), 6),
                    null);
        }

        private void PlaceDocks(TileClass clDock, double heightDockMin, double heightDockMax)
        {
            var c = Ctx;
            var dockTypes = new (string Template, double Count)[]
            {
                (Dock, RmgenLibrary.ScaleByMapSize(1, 2, MapSize)),
                (DockRubble, RmgenLibrary.ScaleByMapSize(2, 3, MapSize)),
            };

            foreach (var dockType in dockTypes)
                GaiaEntities.PlaceDocks(Rng, Map,
                    dockType.Template,
                    0,
                    dockType.Count,
                    c.ClWater,
                    clDock,
                    heightDockMin,
                    heightDockMax,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(clDock, RmgenLibrary.ScaleByMapSize(10, 25, MapSize)),
                        new StaticConstraint(Map, RmgenLibrary.AvoidClasses(
                            c.ClMountain, RmgenLibrary.ScaleByMapSize(6, 8, MapSize),
                            c.ClBaseResource, 10)),
                    }),
                    0,
                    50);
        }

        private void AddTerrainAndResources(TileClass clLava)
        {
            var c = Ctx;

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, clLava, 2, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                        clLava, 2, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20, clLava, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10, clLava, 5, c.ClWater, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 18, c.ClMetal, 3,
                        c.ClMountain, 5, c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 1, c.ClPlayer, 20, c.ClRock, 2, clLava, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 12, c.ClPlayer, 8 },
                    Stay = new object[] { c.ClWater, 4 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 7, c.ClMetal, 2,
                        c.ClMountain, 1, c.ClPlayer, 12, c.ClRock, 2, clLava, 5, c.ClWater, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));
        }

        private void AddRuins(TileClass clDecorative, TileClass clLava)
        {
            var c = Ctx;

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(Rng, RomanStatue, 1, 1, 1, 4) },
                    true, c.ClMetal),
                0,
                RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 20, c.ClMountain, 3,
                    c.ClForest, 2, clLava, 5, c.ClMetal, 20),
                5 * RmgenLibrary.ScaleByMapSize(1, 4, MapSize),
                50);

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(Rng, UnfinishedTemple, 0, 1, 1, 4),
                    new ScatterObject(Rng, ColumnsDoric, 1, 1, 1, 4),
                }, true, clDecorative),
                0,
                RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 20, c.ClMountain, 5,
                    c.ClForest, 2, clLava, 5, clDecorative, 20),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize),
                20);

            RmgenLibrary.CreateObjectGroups(Rng,
                CreatePropGroup(Shipwrecks, 1, 20, clDecorative),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clDecorative, 20),
                    RmgenLibrary.StayClasses(c.ClWater, 0),
                }),
                RmgenLibrary.ScaleByMapSize(1, 5, MapSize),
                20);

            RmgenLibrary.CreateObjectGroups(Rng,
                CreatePropGroup(Statues, 1, 20, clDecorative),
                0,
                RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 20, c.ClMountain, 2,
                    c.ClForest, 2, clLava, 5, clDecorative, 20),
                RmgenLibrary.ScaleByMapSize(3, 15, MapSize),
                30);

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(Rng, Skeleton, 3, 10, 1, 7) },
                    true, c.ClDirt),
                0,
                RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 10, c.ClMountain, 2,
                    c.ClForest, 2, clDecorative, 2),
                RmgenLibrary.ScaleByMapSize(1, 5, MapSize),
                50);
        }

        private ObjectGroup CreatePropGroup(IEnumerable<string> templates, double minDistance,
            double maxDistance, TileClass tileClass)
        {
            var objects = new List<IGroupElement>();
            foreach (string template in templates)
                objects.Add(new ScatterObject(Rng, template, 0, 1, minDistance, maxDistance));
            return new ObjectGroup(objects, true, tileClass);
        }

        /// <summary>g_Map.LoadHeightmapImage("pompeii.png", 0, 140)。</summary>
        private void LoadPompeiiHeightmap(double maxHeight)
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "pompeii.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, maxHeight), null);
        }
    }
}
