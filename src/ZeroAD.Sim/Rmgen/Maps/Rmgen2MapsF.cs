using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>ratumacos.js（逐字移植）——诺曼底实景高度图上的曲折浅滩河网；
    /// 玩家沿河两岸布置。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class RatumacosMap2 : Rmgen2Map
    {
        protected override string? ForcedBiome => "generic/alpine";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "shoreline", "shallows" };

        /// <summary>land 类由高度/水面判断后再标记。</summary>
        protected override bool PaintLandClass => false;

        protected override void OverrideBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string> { "new_alpine_grass_d" };
            biome.ForestFloor1 = "alpine_grass_d";
            biome.ForestFloor2 = "alpine_grass_c";
            biome.Tier1Terrain = "new_alpine_grass_c";
            biome.Tier2Terrain = "new_alpine_grass_b";
            biome.Tier3Terrain = "alpine_grass_a";
            biome.Tier4Terrain = "new_alpine_grass_e";

            biome.MainHuntableAnimal = "gaia/fauna_deer";
            biome.SecondaryHuntableAnimal = "gaia/fauna_pig";
            biome.Fish = "gaia/fish/tilapia";
            biome.Tree1 = "gaia/tree/poplar";
            biome.Tree2 = "gaia/tree/toona";
            biome.Tree3 = "gaia/fruit/apple";
            biome.Tree4 = "gaia/tree/acacia";
            biome.Tree5 = "gaia/tree/carob";

            biome.Grass = "actor|props/flora/grass_soft_large.xml";
            biome.GrassShort = "actor|props/flora/grass_tufts_a.xml";
            biome.RockLarge = "actor|geology/stone_granite_med.xml";
            biome.RockMedium = "actor|geology/stone_granite_small.xml";
            biome.BushMedium = "actor|props/flora/bush_tempe_a.xml";
            biome.BushSmall = "actor|props/flora/bush_tempe_b.xml";
            biome.Reeds = "actor|props/flora/reeds_pond_lush_a.xml";
            biome.Lillies = "actor|props/flora/water_lillies.xml";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clShoreline = c.Cl("shoreline");

            double heightReedsMin = HeightScale(-2);
            double heightShallow = HeightScale(-1);
            double heightWaterLevel = HeightScale(0);
            double heightShoreline = HeightScale(3);
            double heightPlayer = HeightScale(10);

            const double riverAngle = 0.65 * SafeMath.PI;

            LoadRatumacosHeightmap();

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, 0.1, 1), null);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(5, 12, MapSize); ++i)
            {
                double x = RmgenLibrary.FractionToTiles(Rng.RandFloat(0, 1), MapSize);

                var start = new RmgenVector2D(x, 0);
                start.RotateAround(riverAngle + SafeMath.PI / 2 * Rng.RandFloat(0.8, 1.2), c.MapCenter);

                var end = new RmgenVector2D(x, MapSize);
                end.RotateAround(riverAngle + SafeMath.PI / 2 * Rng.RandFloat(0.8, 1.2), c.MapCenter);

                RmgenCommon.CreatePassage(Rng, Map, start, end,
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    2,
                    constraints: new HeightConstraint(Map, double.NegativeInfinity, heightShallow),
                    startHeight: heightShallow,
                    endHeight: heightShallow);
            }

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Water, Rng),
                    new TileClassPainter(c.ClWater),
                },
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClLand),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Shore, Rng),
                    new TileClassPainter(clShoreline),
                },
                new HeightConstraint(Map, heightWaterLevel, heightShoreline));

            PlaceRatumacosBases(riverAngle, heightPlayer);

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                        c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddSmallMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 6,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 30, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 6,
                        c.ClPlayer, 30, c.ClRock, 30, c.ClMetal, 20, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 6,
                        c.ClPlayer, 30, c.ClRock, 30, c.ClMetal, 20, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 8, c.ClMetal, 3,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2, clShoreline, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 2, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "similar" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 20, c.ClRock, 2, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "similar" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClForest, 5, c.ClMetal, 10,
                        c.ClMountain, 2, c.ClPlayer, 20, c.ClRock, 10, c.ClWater, 3, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 4, c.ClMetal, 2,
                        c.ClMountain, 6, c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 5, clShoreline, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            GaiaEntities.CreateDecoration(Rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(Rng, Biome.Reeds, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(Rng, Biome.Lillies, 1, 2, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(1800, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(900, MapSize, Settings.CircularMap),
                },
                new HeightConstraint(Map, heightReedsMin, heightShoreline));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 10 },
                    Stay = new object[] { c.ClWater, 4 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "few" },
                },
            });
        }

        private double HeightScale(double value) => value * MapSize / 320.0;

        /// <summary>g_Map.LoadHeightmapImage("ratumacos.png", -3, 20)；数据缺失时保持平坦图。</summary>
        private void LoadRatumacosHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "ratumacos.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, -3, 20), null);
        }

        /// <summary>本图直接调用 placePlayerBases，参数不同于 rmgen2/setup.js 的 createBase。</summary>
        private void PlaceRatumacosBases(double riverAngle, double heightPlayer)
        {
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                Rng, Map, Settings, riverAngle, RmgenLibrary.FractionToTiles(0.6, MapSize));

            if (Settings.Nomad)
                return;

            for (int i = 0; i < NumPlayers; ++i)
                PlaceRatumacosBase(playerIDs[i], playerPosition[i], heightPlayer);
        }

        private void PlaceRatumacosBase(int playerID, RmgenVector2D playerPosition, double heightPlayer)
        {
            var c = Ctx;

            RmgenCommon.PlaceStartingEntities(Map, playerPosition, playerID,
                RmgenCommon.GetStartingEntities(Settings.DataRoot, RmgenCommon.GetCivCode(Settings, playerID)),
                6, -SafeMath.PI / 4);
            RmgenCommon.AddCivicCenterAreaToClass(Map, playerPosition, c.ClPlayer);

            IConstraint baseResourceConstraint = RmgenLibrary.AvoidClasses(c.ClBaseResource, 4);

            RmgenLibrary.CreateArea(
                new ClumpPlacer(Rng,
                    Math.Floor(RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize) / 3)),
                    0.6, 0.3, double.PositiveInfinity, playerPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { Biome.RoadWild, Biome.Road }, new double[] { 1 }, Rng),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid, heightPlayer, 2),
                },
                null);

            PlaceRatumacosBaseTrees(playerPosition, baseResourceConstraint);
            PlaceRatumacosBaseMines(playerPosition, baseResourceConstraint);
            PlaceRatumacosBaseBerries(playerPosition, baseResourceConstraint);
            PlaceRatumacosBaseStartingAnimals(playerPosition, baseResourceConstraint);
            PlaceRatumacosBaseDecoratives(playerPosition, baseResourceConstraint);
        }

        private void PlaceRatumacosBaseTrees(RmgenVector2D playerPosition, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var offset = new RmgenVector2D(0, Rng.RandFloat(11, 13));
                offset.Rotate(Rng.RandomAngle());
                var position = RmgenVector2D.Add(offset, playerPosition);
                position.Round();

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(Rng, Biome.Tree1, 2, 2, 0, 5),
                    }, false, Ctx.ClBaseResource, position),
                    0, constraint))
                    return;
            }
        }

        private void PlaceRatumacosBaseMines(RmgenVector2D playerPosition, IConstraint constraint)
        {
            double angleBetweenMines = Rng.RandFloat(SafeMath.PI / 6, SafeMath.PI / 3);
            var templates = new[] { Biome.MetalLarge, Biome.StoneLarge };

            for (int tries = 0; tries < 75; ++tries)
            {
                var positions = new RmgenVector2D[templates.Length];
                bool valid = true;
                double startAngle = Rng.RandomAngle();

                for (int i = 0; i < templates.Length; ++i)
                {
                    double angle = startAngle + angleBetweenMines * (i + (templates.Length - 1) / 2.0);
                    var offset = new RmgenVector2D(0, 12);
                    offset.Rotate(angle);
                    var position = RmgenVector2D.Add(offset, playerPosition);
                    position.Round();
                    positions[i] = position;

                    if (!Map.ValidTilePassable(position) || !constraint.Allows(position))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    continue;

                for (int i = 0; i < templates.Length; ++i)
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(Rng, templates[i], 1, 1, 0, 0),
                        }, true, Ctx.ClBaseResource, positions[i]),
                        0, null);

                return;
            }
        }

        private void PlaceRatumacosBaseBerries(RmgenVector2D playerPosition, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var offset = new RmgenVector2D(0, 12);
                offset.Rotate(Rng.RandomAngle());
                var position = RmgenVector2D.Add(offset, playerPosition);

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(Rng, Biome.FruitBush, 5, 5, 1, 3),
                    }, true, Ctx.ClBaseResource, position),
                    0, constraint))
                    return;
            }
        }

        private void PlaceRatumacosBaseStartingAnimals(RmgenVector2D playerPosition, IConstraint constraint)
        {
            const string template = "gaia/fauna_chicken";

            for (int i = 0; i < 2; ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var offset = new RmgenVector2D(0, 9);
                    offset.Rotate(Rng.RandomAngle());
                    var position = RmgenVector2D.Add(offset, playerPosition);

                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(Rng, template, 5, 5, 0, 2),
                        }, true, Ctx.ClBaseResource, position),
                        0, constraint))
                    {
                        success = true;
                        break;
                    }
                }

                if (!success)
                    return;
            }
        }

        private void PlaceRatumacosBaseDecoratives(RmgenVector2D playerPosition, IConstraint constraint)
        {
            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(2, 5, MapSize); ++i)
                for (int tries = 0; tries < 30; ++tries)
                {
                    var offset = new RmgenVector2D(0, Rng.RandIntInclusive(8, 11));
                    offset.Rotate(Rng.RandomAngle());
                    var position = RmgenVector2D.Add(offset, playerPosition);
                    position.Round();

                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(Rng, Biome.RockMedium, 2, 5, 0, 1),
                        }, false, Ctx.ClBaseResource, position),
                        0, constraint))
                        break;
                }
        }
    }

    /// <summary>red_sea.js（逐字移植）——红海实景高度图，低地刷海床、
    /// 山地刷荒漠峭壁，岸边补芦苇与风沙。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class RedSeaMap2 : Rmgen2Map
    {
        private const string AdditionalDirt1 = "desert_plants_b";
        private const string AdditionalDirt2 = "desert_sand_scrub";
        private const string Dust = "actor|particle/dust_storm_reddish.xml";

        protected override string? ForcedBiome => "generic/sahara";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "shoreline" };

        /// <summary>land 类由高度/水面判断后再标记。</summary>
        protected override bool PaintLandClass => false;

        protected override void OverrideBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string>
            {
                "desert_sand_dunes_50", "desert_sand_dunes_50", "desert_sand_dunes_50",
                "desert_sand_dunes_50", "desert_sand_dunes_rocks", "desert_dirt_rough_2",
            };
            biome.ForestFloor1 = "desert_grass_a_sand";
            biome.Cliff = new List<string> { "desert_cliff_3_dirty" };
            biome.ForestFloor2 = "desert_grass_a_sand";
            biome.Tier1Terrain = "desert_dirt_rocks_2";
            biome.Tier2Terrain = "desert_dirt_rough";
            biome.Tier3Terrain = "desert_dirt_rough";
            biome.Tier4Terrain = "desert_sand_stones";
            biome.RoadWild = "road2";
            biome.Road = "road2";
            biome.Tree1 = "gaia/tree/date_palm";
            biome.Tree2 = "gaia/tree/senegal_date_palm";
            biome.Tree3 = "gaia/fruit/date";
            biome.Tree4 = "gaia/tree/cretan_date_palm_tall";
            biome.Tree5 = "gaia/tree/cretan_date_palm_short";
            biome.FruitBush = "gaia/fruit/berry_05";
            biome.Grass = "actor|props/flora/grass_field_dry_tall_b.xml";
            biome.GrassShort = "actor|props/flora/grass_field_parched_short.xml";
            biome.RockLarge = "actor|geology/stone_desert_med.xml";
            biome.RockMedium = "actor|geology/stone_savanna_med.xml";
            biome.BushMedium = "actor|props/flora/bush_desert_dry_a.xml";
            biome.BushSmall = "actor|props/flora/bush_medit_sm_dry.xml";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clShoreline = c.Cl("shoreline");

            double heightSeaGround = HeightScale(-4);
            double heightReedsMin = HeightScale(-2);
            double heightReedsMax = HeightScale(-0.5);
            double heightWaterLevel = HeightScale(0);
            double heightShoreline = HeightScale(0.5);
            var mapCenter = Map.GetCenter();

            LoadRedSeaHeightmap();

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 2),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.1, 0.5, MapSize), 1),
                null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClWater),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, MapSize), mapCenter),
                new TileClassPainter(c.ClLand),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Water, Rng),
                    new TileClassPainter(clShoreline),
                },
                new HeightConstraint(Map, double.NegativeInfinity, heightShoreline));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Cliff, Rng),
                    new TileClassPainter(c.ClMountain),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClWater, 2),
                    new SlopeConstraint(Map, 2, double.PositiveInfinity),
                }));

            if (!Settings.Nomad)
            {
                double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
                var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(c.ClMountain, RmgenLibrary.ScaleByMapSize(5, 10, MapSize)),
                        RmgenLibrary.StayClasses(c.ClLand, baseRadius),
                    }));

                if (placement.HasValue)
                {
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
            }

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 3, c.ClForest, 20, c.ClMetal, 4,
                        c.ClMountain, 3, c.ClPlayer, 20, c.ClRock, 4, c.ClWater, 2 },
                    Sizes = new[] { "big" }, Mixes = new[] { "similar" }, Amounts = new[] { "few" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 2, c.ClForest, 25, c.ClMetal, 4,
                        c.ClMountain, 5, c.ClPlayer, 15, c.ClRock, 4, c.ClWater, 2 },
                    Sizes = new[] { "small" }, Mixes = new[] { "similar" }, Amounts = new[] { "tons" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClForest, 5, c.ClMetal, 10,
                        c.ClMountain, 2, c.ClPlayer, 20, c.ClRock, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClForest, 2, c.ClMetal, 2,
                        c.ClMountain, 1, c.ClPlayer, 20, c.ClRock, 4, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
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
                    Avoid = new object[] { c.ClBerries, 5, c.ClForest, 15, c.ClMetal, 2,
                        c.ClMountain, 1, c.ClPlayer, 20, c.ClRock, 4, c.ClWater, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, c.ClWater, 3, clShoreline, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                        c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
            });

            CreatePatches(new[] { 2.0, 4.0 }, AdditionalDirt1,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(c.ClLand, 6),
                    RmgenLibrary.AvoidClasses(c.ClMountain, 4, c.ClForest, 2, clShoreline, 2, c.ClPlayer, 12),
                }),
                RmgenLibrary.ScaleByMapSize(2, 5, MapSize), c.ClDirt);

            CreatePatches(new[] { 4.0, 6.0, 8.0 }, AdditionalDirt2,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(c.ClLand, 6),
                    RmgenLibrary.AvoidClasses(c.ClMountain, 4, c.ClForest, 2, clShoreline, 2, c.ClPlayer, 12),
                }),
                RmgenLibrary.ScaleByMapSize(4, 8, MapSize), c.ClDirt);

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(Rng, Biome.Reeds, 5, 12, 1, 4),
                    new ScatterObject(Rng, Biome.RockMedium, 1, 2, 1, 5),
                }, false, c.ClDirt),
                0,
                new HeightConstraint(Map, heightReedsMin, heightReedsMax),
                RmgenLibrary.ScaleByMapSize(10, 25, MapSize),
                5);

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(Rng, Dust, 1, 1, 1, 4),
                }, false),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(c.ClLand, 5),
                    RmgenLibrary.AvoidClasses(c.ClPlayer, 10),
                }),
                RmgenLibrary.ScaleByMapSize(10, 50, MapSize),
                20);
        }

        private double HeightScale(double value) => value * MapSize / 320.0;

        /// <summary>g_Map.LoadHeightmapImage("red_sea.png", 0, 25)；数据缺失时保持平坦图。</summary>
        private void LoadRedSeaHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "red_sea.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, 25), null);
        }

        private void CreatePatches(double[] sizes, string terrain, IConstraint constraint,
            double count, TileClass tileClass)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(terrain, Rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }
    }

    /// <summary>marmara.js（逐字移植）——马尔马拉海实景高度图，
    /// 两岸丘陵与浅海边缘集中资源。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class MarmaraMap2 : Rmgen2Map
    {
        protected override string? ForcedBiome => "generic/aegean";

        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "shoreline" };

        /// <summary>land 类由高度/水面判断后再标记。</summary>
        protected override bool PaintLandClass => false;

        protected override void OverrideBiome(BiomeSet biome)
        {
            biome.MainTerrain = new List<string>
            {
                "grass_mediterranean_dry_1024test", "grass_field_dry", "new_savanna_grass_b",
            };
            biome.ForestFloor1 = "steppe_grass_dirt_66";
            biome.ForestFloor2 = "steppe_dirt_a";
            biome.Tier1Terrain = "medit_grass_field_b";
            biome.Tier2Terrain = "medit_grass_field_dry";
            biome.Tier3Terrain = "medit_shrubs_golden";
            biome.Tier4Terrain = "steppe_dirt_b";
            biome.Cliff = new List<string> { "medit_cliff_a" };
            biome.RoadWild = "road_med_a";
            biome.Road = "road2";
            biome.Water = "medit_sand_messy";

            biome.MainHuntableAnimal = "gaia/fauna_horse";
            biome.SecondaryHuntableAnimal = "gaia/fauna_boar";
            biome.Fish = "gaia/fish/generic";
            biome.Tree1 = "gaia/tree/carob";
            biome.Tree2 = "gaia/tree/poplar_lombardy";
            biome.Tree3 = "gaia/tree/dead";
            biome.Tree4 = "gaia/tree/dead";
            biome.Tree5 = "gaia/tree/carob";
            biome.FruitBush = "gaia/fruit/grapes";
            biome.MetalSmall = "gaia/ore/desert_small";

            biome.Grass = "actor|props/special/eyecandy/block_limestone.xml";
            biome.GrassShort = "actor|props/special/eyecandy/blocks_sandstone_pile_a.xml";
            biome.RockLarge = "actor|geology/stone_savanna_med.xml";
            biome.RockMedium = "actor|geology/stone_granite_small.xml";
            biome.BushMedium = "actor|props/flora/bush_medit_me_dry.xml";
            biome.BushSmall = "actor|props/flora/bush_medit_sm_dry.xml";
            biome.Reeds = "actor|props/flora/reeds_pond_lush_a.xml";
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clShoreline = c.Cl("shoreline");

            double heightSeaGround = HeightScale(RmgenLibrary.ScaleByMapSize(-6, -4, MapSize));
            double heightWaterLevel = HeightScale(0);
            double heightShoreline = HeightScale(0);

            LoadMarmaraHeightmap();

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, RmgenLibrary.ScaleByMapSize(1, 3, MapSize)),
                new HeightConstraint(Map, double.NegativeInfinity, heightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.1, 0.2, MapSize), 1),
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
                    new TerrainPainter(Biome.Water, Rng),
                    new TileClassPainter(clShoreline),
                },
                new HeightConstraint(Map, double.NegativeInfinity, heightShoreline));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(Biome.Cliff, Rng),
                    new TileClassPainter(c.ClMountain),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(c.ClWater, 2),
                    new SlopeConstraint(Map, 2, double.PositiveInfinity),
                }));

            if (!Settings.Nomad)
            {
                var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(c.ClMountain, 5),
                        RmgenLibrary.StayClasses(c.ClLand, RmgenLibrary.ScaleByMapSize(6, 25, MapSize)),
                    }));

                if (placement.HasValue)
                {
                    var (playerIDs, playerPosition) = placement.Value;
                    c.CreateBases(playerIDs, playerPosition, true);

                    double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
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
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "many" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 20,
                        c.ClMetal, 30, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddSmallMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 20,
                        c.ClMetal, 30, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 30,
                        c.ClMetal, 20, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 10,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlateau, 5, c.ClPlayer, 20,
                        c.ClRock, 3, c.ClWater, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClBluff, 5, c.ClForest, 5,
                        c.ClMetal, 10, c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                    Amounts = new[] { "normal", "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBluff, 5, c.ClForest, 2,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 2, c.ClWater, 3 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "unique" }, Amounts = new[] { "tons" },
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
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 5,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 12,
                        c.ClRock, 2, c.ClWater, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            RmgenLibrary.CreateObjectGroups(Rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(Rng, Biome.Reeds, 5, 12, 1, 2),
                    new ScatterObject(Rng, Biome.RockMedium, 1, 2, 1, 3),
                }, true, c.ClDirt),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(c.ClWater, 0),
                    RmgenLibrary.BorderClasses(c.ClWater,
                        RmgenLibrary.ScaleByMapSize(2, 8, MapSize),
                        RmgenLibrary.ScaleByMapSize(2, 8, MapSize)),
                }),
                RmgenLibrary.ScaleByMapSize(50, 400, MapSize),
                2);
        }

        private double HeightScale(double value) => value * MapSize / 320.0;

        /// <summary>g_Map.LoadHeightmapImage("marmara.png", 0, 10)；数据缺失时保持平坦图。</summary>
        private void LoadMarmaraHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "marmara.png")
                : null;
            if (path == null || !System.IO.File.Exists(path))
                return;

            var heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                HeightmapLoader.LoadHeightmapImage(path));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, 10), null);
        }
    }
}
