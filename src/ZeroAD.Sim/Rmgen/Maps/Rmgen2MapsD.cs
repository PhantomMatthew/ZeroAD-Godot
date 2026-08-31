using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>harbor.js（逐字移植）——图心一片内海，玩家各有一处凹入的港湾，
    /// 放射状山脊（spine）把陆路切成扇形。</summary>
    public sealed class HarborMap2 : Rmgen2Map
    {
        private const double HeightSeaGround = -18;
        private const double HeightLandConst = 2;
        private const double HeightOffsetHarbor = -11;

        private double _startAngle;

        protected override double PickHeightLand(RmgenRng rng) => HeightLandConst;

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            _startAngle = Rng.RandomAngle();

            var playerPosition = CreateBasesAt("circle",
                RmgenLibrary.FractionToTiles(0.38, MapSize),
                RmgenLibrary.FractionToTiles(0.05, MapSize),
                _startAngle, true);

            AddCenterLake();

            if (MapSize >= 192)
                AddHarbors(playerPosition);

            AddSpines();

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 12, c.ClHill, 8, c.ClMountain, 8,
                        c.ClPlayer, 8, c.ClSpine, 4 },
                    Stay = new object[] { c.ClWater, 7 },
                    Amounts = new[] { "tons" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClMountain, 2,
                        c.ClPlateau, 5, c.ClPlayer, 20, c.ClSpine, 5, c.ClValley, 2, c.ClWater, 2 },
                    Sizes = new[] { "tiny", "small" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMountains(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 20, c.ClSpine, 20, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "small" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddPlateaus(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 40, c.ClSpine, 20, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "small" },
                },
                new E
                {
                    Func = (cs, s, d, f, bh) => c.AddBluffs(cs, s, d, f, bh),
                    BaseHeight = HeightLandConst,
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 40, c.ClSpine, 20, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "normal" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 10,
                        c.ClSpine, 5, c.ClMetal, 20, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 20,
                        c.ClSpine, 5, c.ClMetal, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 8,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlateau, 5, c.ClPlayer, 20,
                        c.ClRock, 3, c.ClSpine, 5, c.ClWater, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" },
                    Amounts = new[] { "many" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClBluff, 5, c.ClForest, 5,
                        c.ClMetal, 10, c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 10, c.ClSpine, 2, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBluff, 5, c.ClForest, 2,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 2, c.ClSpine, 2, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 7,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 12,
                        c.ClRock, 2, c.ClSpine, 2, c.ClWater, 5 },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClSpine, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClSpine, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });
        }

        private void AddCenterLake()
            => RmgenLibrary.CreateArea(
                new ChainPlacer(Rng, 2,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 12, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 160, MapSize)),
                    double.PositiveInfinity, Ctx.MapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.2, MapSize)) }),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { Biome.Shore, Biome.Water }, new[] { 1 }, Rng),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightSeaGround, 10),
                    new TileClassPainter(Ctx.ClWater),
                },
                RmgenLibrary.AvoidClasses(Ctx.ClPlayer, 20));

        private void AddHarbors(IReadOnlyList<RmgenVector2D> playerPosition)
        {
            foreach (var position in playerPosition)
            {
                var toCenter = RmgenVector2D.Sub(Ctx.MapCenter, position);
                toCenter = RmgenVector2D.Div(toCenter, 2.5);
                toCenter.Round();
                var harborPosition = RmgenVector2D.Add(position, toCenter);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, 1200, 0.5, 0.5, double.PositiveInfinity, harborPosition),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { Biome.Shore, Biome.Water },
                            new[] { 2 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Blurry,
                            HeightOffsetHarbor, 3, relative: true),
                        new TileClassPainter(Ctx.ClWater),
                    },
                    RmgenLibrary.AvoidClasses(Ctx.ClPlayer, 15, Ctx.ClHill, 1));
            }
        }

        private void AddSpines()
        {
            var c = Ctx;
            bool smallSpines = MapSize <= 192;
            double spineSize = smallSpines ? 0.02 : 0.5;
            double spineTapering = smallSpines ? -0.1 : -1.4;
            double heightOffsetSpine = smallSpines ? 20 : 35;

            object spineTile = Biome.Dirt;
            if (BiomeName == "generic/arctic")
                spineTile = Biome.Tier1Terrain;
            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna")
                spineTile = Biome.Tier2Terrain;
            if (BiomeName == "generic/autumn")
                spineTile = Biome.Tier4Terrain;

            int split = 1;
            if (NumPlayers <= 3 || MapSize >= 320 && NumPlayers <= 4)
                split = 2;

            for (int i = 0; i < NumPlayers * split; ++i)
            {
                double tang = _startAngle + (i + 0.5) * 2 * SafeMath.PI / (NumPlayers * split);

                var startOffset = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.12, MapSize), 0);
                startOffset.Rotate(-tang);
                var endOffset = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.4, MapSize), 0);
                endOffset.Rotate(-tang);

                RmgenLibrary.CreateArea(
                    new PathPlacer(Rng, 0.6, 0.1, 0.4, spineTapering)
                    {
                        Start = RmgenVector2D.Add(c.MapCenter, startOffset),
                        End = RmgenVector2D.Add(c.MapCenter, endOffset),
                        Width = RmgenLibrary.ScaleByMapSize(14, spineSize, MapSize),
                    },
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, spineTile },
                            new[] { 3 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetSpine, 3, relative: true),
                        new TileClassPainter(c.ClSpine),
                    },
                    RmgenLibrary.AvoidClasses(c.ClPlayer, 5));
            }

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, c.ClWater, 3 },
                    Stay = new object[] { c.ClSpine, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddProps(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClPlayer, 2, c.ClProp, 20, c.ClWater, 3 },
                    Stay = new object[] { c.ClSpine, 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
        }
    }

    /// <summary>bahrain.js（逐字移植）——以 bahrain.png 真实海拔为底的浅海群岛；
    /// 玩家随机落在陆地上，图中北部有一座独立小岛（专属 biome 贴图）。</summary>
    public sealed class BahrainMap2 : Rmgen2Map
    {
        protected override string? ForcedBiome => "generic/sahara";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string> BaseTerrainList => Biome.MainTerrain;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "island", "shoreline" };

        /// <summary>land 类由高度决定，不是全图刷。</summary>
        protected override bool PaintLandClass => false;

        /// <summary>setLandBiome + 图专属 gaia/decoratives 覆写。</summary>
        protected override void OverrideBiome(BiomeSet biome)
        {
            SetLandBiome(biome);

            biome.RoadWild = "desert_city_tile_pers_dirt";
            biome.Road = "desert_city_tile_pers";

            biome.MainHuntableAnimal = "gaia/fauna_camel";
            biome.SecondaryHuntableAnimal = "gaia/fauna_gazelle";
            biome.Fish = "gaia/fish/generic";
            biome.Tree1 = "gaia/tree/cretan_date_palm_tall";
            biome.Tree2 = "gaia/tree/cretan_date_palm_short";
            biome.Tree3 = "gaia/tree/cretan_date_palm_patch";
            biome.Tree4 = "gaia/tree/cretan_date_palm_tall";
            biome.Tree5 = "gaia/tree/cretan_date_palm_short";
            biome.FruitBush = "gaia/fruit/grapes";

            biome.Grass = "actor|props/flora/grass_field_parched_short.xml";
            biome.GrassShort = "actor|props/flora/grass_field_parched_short.xml";
            biome.RockLarge = "actor|geology/stone_savanna_med.xml";
            biome.RockMedium = "actor|geology/stone_granite_greek_small.xml";
            biome.BushMedium = "actor|props/flora/bush_desert_dry_a.xml";
            biome.BushSmall = "actor|props/flora/bush_medit_la_dry";
        }

        private const string WoodTreasure = "gaia/treasure/wood";
        private const string FoodTreasure = "gaia/treasure/food_bin";
        private const string ShipWreck = "gaia/treasure/shipwreck_sail_boat_cut";

        private static void SetLandBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string>
            {
                "desert_dirt_rough_2", "desert_dirt_rough_2", "desert_dirt_rough_2",
                "desert_dirt_rocks_3", "desert_sand_stones",
            };
            biome.ForestFloor1 = "grass_dead";
            biome.ForestFloor2 = "desert_dirt_persia_1";
            biome.Tier1Terrain = "desert_sand_dunes_stones";
            biome.Tier2Terrain = "desert_sand_scrub";
            biome.Tier3Terrain = "desert_plants_b";
            biome.Tier4Terrain = "medit_dirt_dry";
        }

        private static void SetIslandBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string> { "sand" };
            biome.ForestFloor1 = "desert_wave";
            biome.ForestFloor2 = "desert_sahara";
            biome.Tier1Terrain = "sand_scrub_25";
            biome.Tier2Terrain = "sand_scrub_75";
            biome.Tier3Terrain = "sand_scrub_50";
            biome.Tier4Terrain = "sand";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clIsland = c.Cl("island");
            var clShoreline = c.Cl("shoreline");

            double heightScale = MapSize / 320.0;
            double heightSeaGround = -6 * heightScale;
            double heightWaterLevel = 0 * heightScale;
            double heightShoreline = 0.5 * heightScale;

            LoadBahrainHeightmap();

            // 海床下压
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 2),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            // 高度图平滑
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.1, 0.5, MapSize), 1),
                null);

            // 水/陆标记
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClWater),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClLand),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            // 北部小岛（图上 x∈[0.4,0.6]、y∈[上边界, 中线] 的陆地）
            var areaIsland = RmgenLibrary.CreateArea(
                new RectPlacer(
                    (int)RmgenLibrary.FractionToTiles(0.4, MapSize), 0,
                    (int)RmgenLibrary.FractionToTiles(0.6, MapSize), (int)c.MapCenter.Y),
                new TileClassPainter(clIsland),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            // 岸线
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Water, Rng),
                    new TileClassPainter(clShoreline),
                },
                new HeightConstraint(Map, double.NegativeInfinity, heightShoreline));

            // 崖壁
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Cliff, Rng),
                    new TileClassPainter(c.ClMountain),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClWater, 0),
                    new SlopeConstraint(Map, 2, double.PositiveInfinity),
                }));

            if (!Settings.Nomad)
            {
                double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
                var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(clIsland, 5),
                        RmgenLibrary.StayClasses(c.ClLand, baseRadius / 2),
                    }));

                if (placement.HasValue)
                {
                    var (playerIDs, playerPosition) = placement.Value;
                    c.CreateBases(playerIDs, playerPosition, true);

                    // 基地区压平
                    foreach (var position in playerPosition)
                        RmgenLibrary.CreateArea(
                            new ClumpPlacer(Rng, RmgenGeometry.DiskArea(baseRadius * 0.8),
                                0.95, 0.6, double.PositiveInfinity, position),
                            new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                                Map.GetHeight(position), 6),
                            null);
                }
            }

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                        c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "small" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 30, c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClPlayer, 30,
                        c.ClRock, 30, c.ClMetal, 20, c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 35, c.ClMetal, 3,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2, clIsland, 2 },
                    Sizes = new[] { "big" }, Mixes = new[] { "similar" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 18, c.ClMetal, 3,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClForest, 5, c.ClMetal, 10,
                        c.ClPlayer, 20, c.ClRock, 10, c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 1, c.ClPlayer, 20, c.ClRock, 2, c.ClWater, 3, clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 18, c.ClPlayer, 8 },
                    Stay = new object[] { c.ClWater, 4 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 7, c.ClMetal, 2,
                        c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 5, clIsland, 2 },
                    Sizes = new[] { "small" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            // 岛上换一套贴图
            SetIslandBiome(Biome);

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Stay = new object[] { clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Stay = new object[] { clIsland, 2 },
                    Sizes = new[] { "tiny" }, Mixes = new[] { "same" }, Amounts = new[] { "scarce" },
                },
            });

            // 岛上矿点
            if (areaIsland != null)
                for (int i = 0; i < RmgenLibrary.ScaleByMapSize(4, 10, MapSize); ++i)
                    RmgenLibrary.CreateObjectGroupsByAreas(Rng,
                        Rng.RandBool()
                            ? new ObjectGroup(new IGroupElement[]
                                { new ScatterObject(Rng, Biome.MetalLarge, 1, 1, 0, 4) },
                                true, c.ClMetal)
                            : new ObjectGroup(new IGroupElement[]
                                { new ScatterObject(Rng, Biome.StoneLarge, 1, 1, 0, 4) },
                                true, c.ClRock),
                        0,
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(c.ClRock, 8, c.ClMetal, 8),
                            RmgenLibrary.StayClasses(clIsland, 4),
                        }),
                        1, 40, new[] { areaIsland });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 10, c.ClMetal, 3,
                        c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2 },
                    Stay = new object[] { clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 7, c.ClMetal, 2,
                        c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 5 },
                    Stay = new object[] { clIsland, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));

            // 额外装饰
            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(Rng, "actor|props/special/eyecandy/awning_wood_small.xml",
                        1, 1, 1, 7),
                    new ScatterObject(Rng, "actor|props/special/eyecandy/barrels_buried.xml",
                        1, 2, 1, 7),
                }, true, c.ClDirt),
                0,
                RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 10, c.ClMountain, 2, c.ClForest, 2),
                2 * RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 20);

            // 宝藏
            foreach (string treasure in new[] { WoodTreasure, FoodTreasure })
                RmgenLibrary.CreateObjectGroups(Rng,
                    new ObjectGroup(new IGroupElement[]
                        { new ScatterObject(Rng, treasure, 1, 1, 0, 2) }, true),
                    0,
                    RmgenLibrary.AvoidClasses(c.ClWater, 2, c.ClPlayer, 25, c.ClForest, 2),
                    Rng.RandIntInclusive(1, NumPlayers), 20);

            // 沉船
            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(Rng, ShipWreck, 1, 1, 0, 1) }, true),
                0,
                RmgenLibrary.StayClasses(c.ClWater, 2),
                Rng.RandIntInclusive(0, 1), 20);
        }

        /// <summary>g_Map.LoadHeightmapImage("bahrain.png", 0, 15)——
        /// 数据缺失（测试环境）时静默跳过，地面保持初始平坦。</summary>
        private void LoadBahrainHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "bahrain.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, 15), null);
        }
    }
}
