using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>oasis.js（401 行）——绿洲：中央大绿洲（湖心水 + 棕榈林环），
    /// 每玩家身旁一小片棕榈 + 一洼水（花/芦苇点缀），沙漠起伏 + 沙丘，
    /// 偶发穿绿洲的沙径（clPassage 上散生棕榈）。无 biome（全内联常量）；
    /// 基底 tSand 名单逐图块 pickRandom（RandomMap 名单构造器）。
    /// 环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class OasisMap : StandardMap
    {
        private static readonly string[] tSand =
        {
            "desert_sand_dunes_100", "desert_dirt_cracks", "desert_sand_smooth",
            "desert_dirt_rough", "desert_dirt_rough_2", "desert_sand_smooth",
        };
        private static readonly string[] tDune = { "desert_sand_dunes_50" };
        private const string tForestFloor = "desert_forestfloor_palms";
        private static readonly string[] tDirt =
        {
            "desert_dirt_rough", "desert_dirt_rough", "desert_dirt_rough",
            "desert_dirt_rough_2", "desert_dirt_rocks_2",
        };
        private const string tRoad = "desert_city_tile";
        private const string tRoadWild = "desert_city_tile";
        private const string tShore = "dirta";
        private const string tWater = "desert_sand_wet";

        private const string ePalmShort = "gaia/tree/cretan_date_palm_short";
        private const string ePalmTall = "gaia/tree/cretan_date_palm_tall";
        private const string eCamel = "gaia/fauna_camel";
        private const string eGazelle = "gaia/fauna_gazelle";
        private const string eLion = "gaia/fauna_lion";
        private const string eLioness = "gaia/fauna_lioness";
        private const string eStoneMine = "gaia/rock/desert_large";
        private const string eMetalMine = "gaia/ore/desert_large";

        private const string aFlower1 = "actor|props/flora/decals_flowers_daisies.xml";
        private const string aWaterFlower = "actor|props/flora/water_lillies.xml";
        private const string aReedsA = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string aReedsB = "actor|props/flora/reeds_pond_lush_b.xml";
        private const string aRock = "actor|geology/stone_desert_med.xml";
        private const string aBushA = "actor|props/flora/bush_desert_dry_a.xml";
        private const string aBushB = "actor|props/flora/bush_desert_dry_a.xml";
        private const string aSand = "actor|particle/blowing_sand.xml";

        private const double heightSeaGround = -3;
        private const double heightFloraMin = -2.5;
        private const double heightFloraReedsMax = -1.9;
        private const double heightFloraMax = -1;
        private const double heightSand = 3.4;
        private const double heightOasisPath = 4;
        private const double heightOffsetBump = 4;
        private const double heightOffsetDune = 18;

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tSand);
            var map = Map;

            var clOasis = new TileClass(MapSize);
            var clPassage = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

            var mapCenter = map.GetCenter();

            double waterRadius = RmgenLibrary.ScaleByMapSize(7, 50, MapSize);
            double shoreDistance = RmgenLibrary.ScaleByMapSize(4, 10, MapSize);
            double forestDistance = RmgenLibrary.ScaleByMapSize(6, 20, MapSize);

            var pForestMain = new object[]
            {
                tForestFloor + "|" + ePalmShort,
                tForestFloor + "|" + ePalmTall,
                tForestFloor,
            };
            var pOasisForestLight = new object[]
            {
                tForestFloor + "|" + ePalmShort,
                tForestFloor + "|" + ePalmTall,
                tForestFloor, tForestFloor, tForestFloor, tForestFloor,
                tForestFloor, tForestFloor, tForestFloor, tForestFloor,
            };

            var (_, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            // ── 玩家旁的小绿洲（棕榈丛 + 小水洼 + 花/芦苇；do-while 重试同上游）──
            double forestDist = 1.2 * RmgenCommon.DefaultPlayerBaseRadius(MapSize);
            for (int i = 0; i < NumPlayers; ++i)
            {
                double forestAngle;
                RmgenVector2D forestPosition = default;
                bool placed = false;
                do
                {
                    forestAngle = SafeMath.PI / 3 * rng.RandFloat(1, 2);
                    var offset = new RmgenVector2D(forestDist, 0);
                    offset.Rotate(-forestAngle);
                    forestPosition = RmgenVector2D.Add(playerPosition[i], offset);
                    placed = RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, 70, 1, 0.5, double.PositiveInfinity, forestPosition),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tForestFloor, pForestMain },
                                new double[] { 0 }, rng),
                            new TileClassPainter(clBaseResource),
                        },
                        RmgenLibrary.AvoidClasses(clBaseResource, 0)) != null;
                } while (!placed);

                RmgenVector2D flowerPosition = default, reedsPosition = default;
                do
                {
                    double waterAngle = forestAngle + rng.RandFloat(1, 5) / 3 * SafeMath.PI;
                    var waterOffset = new RmgenVector2D(6, 0);
                    waterOffset.Rotate(-waterAngle);
                    var waterPosition = RmgenVector2D.Add(forestPosition, waterOffset);
                    waterPosition.Round();
                    var flowerOffset = new RmgenVector2D(3, 0);
                    flowerOffset.Rotate(-waterAngle);
                    flowerPosition = RmgenVector2D.Add(forestPosition, flowerOffset);
                    flowerPosition.Round();
                    var reedsOffset = new RmgenVector2D(5, 0);
                    reedsOffset.Rotate(-waterAngle);
                    reedsPosition = RmgenVector2D.Add(forestPosition, reedsOffset);
                    reedsPosition.Round();

                    placed = RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(4.5), 0.9, 0.4,
                            double.PositiveInfinity, waterPosition),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightSeaGround, 3),
                        },
                        RmgenLibrary.AvoidClasses(clBaseResource, 0)) != null;
                } while (!placed);

                RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                        { new ScatterObject(rng, aFlower1, 1, 5, 0, 3) }, true, null, flowerPosition),
                    0, null);
                RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                        { new ScatterObject(rng, aReedsA, 1, 3, 0, 0) }, true, null, reedsPosition),
                    0, null);
            }

            // ── 玩家基地（CityPatch: desert_city_tile）──
            RmgenCommon.PlacePlayerBases(rng, map, settings, tSand[0], ClPlayer, null,
                playerPosition, tRoadWild, tRoad);

            // ── 中央大绿洲（湖心水 + 棕榈林环）──
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng,
                    RmgenGeometry.DiskArea(forestDistance + shoreDistance + waterRadius),
                    0.8, 0.2, double.PositiveInfinity, mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { pOasisForestLight, tWater },
                        new double[] { forestDistance }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightSeaGround, forestDistance + shoreDistance),
                    new TileClassPainter(clOasis),
                },
                null);

            // ── 起伏 ──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 3, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 10, clBaseResource, 6, clOasis, 4),
                RmgenLibrary.ScaleByMapSize(30, 70, MapSize));

            // ── 泥地斑块 ──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, 80, 0.3, 0.06, double.PositiveInfinity),
                new IPainter[] { new TerrainPainter(tDirt, rng) },
                RmgenLibrary.AvoidClasses(ClPlayer, 10, clBaseResource, 6, clOasis, 4, ClForest, 4),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            // ── 沙丘 ──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, 120, 0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new TerrainPainter(tDune, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetDune, 30, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 10, clBaseResource, 6, clOasis, 4, ClForest, 4),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            // ── 穿绿洲的沙径（>150 图且 randBool）──
            if (MapSize > 150 && rng.RandBool())
            {
                double pathWidth = RmgenLibrary.ScaleByMapSize(7, 18, MapSize);
                var points = RmgenGeometry.DistributePointsOnCircle(2, rng.RandomAngle(),
                    waterRadius + shoreDistance + forestDistance + pathWidth, mapCenter).points;
                var pathPlacer = new PathPlacer(rng, 0.4, 1, 0.2, 0)
                {
                    Start = points[0],
                    End = points[1],
                    Width = pathWidth,
                };
                RmgenLibrary.CreateArea(pathPlacer,
                    new IPainter[]
                    {
                        new TerrainPainter(tSand, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOasisPath, 5),
                        new TileClassPainter(clPassage),
                    },
                    null);
            }

            // ── 沙径旁散生棕榈（无论沙径是否生成都跑——空 clPassage 时
            // 每次尝试失败但仍消耗抽数，deprecated 语义恰好 amount 次）──
            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, ePalmTall, 1, 1, 0, 0),
                new ScatterObject(rng, ePalmShort, 1, 2, 1, 2),
                new ScatterObject(rng, aBushA, 0, 2, 1, 3),
            }, true, ClForest);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.StayClasses(clPassage, 3),
                RmgenLibrary.ScaleByMapSize(60, 250, MapSize), 100);

            // ── 石矿/金属矿（矿 + 棕榈/灌木混编组）──
            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, eStoneMine, 1, 1, 0, 0),
                new ScatterObject(rng, ePalmShort, 1, 2, 3, 3),
                new ScatterObject(rng, ePalmTall, 0, 1, 3, 3),
                new ScatterObject(rng, aBushB, 1, 1, 2, 2),
                new ScatterObject(rng, aBushA, 0, 2, 1, 3),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 10, ClForest, 1, ClPlayer, 30, ClRock, 10,
                    clBaseResource, 2, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(6, 25, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, eMetalMine, 1, 1, 0, 0),
                new ScatterObject(rng, ePalmShort, 1, 2, 2, 3),
                new ScatterObject(rng, ePalmTall, 0, 1, 2, 2),
                new ScatterObject(rng, aBushB, 1, 1, 2, 2),
                new ScatterObject(rng, aBushA, 0, 2, 1, 3),
            }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 10, ClForest, 1, ClPlayer, 30, ClMetal, 10,
                    clBaseResource, 2, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(6, 25, MapSize), 100);

            // ── 小装饰石 ──
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aRock, 2, 4, 0, 2) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 3, ClForest, 0, ClPlayer, 10, ClHill, 1,
                    clFood, 20),
                30, (int)RmgenLibrary.ScaleByMapSize(10, 50, MapSize));

            // ── 骆驼/瞪羚 ──
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, eCamel, 1, 2, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 3, ClForest, 0, ClPlayer, 10, ClHill, 1,
                    clFood, 20),
                1 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, eGazelle, 2, 4, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 3, ClForest, 0, ClPlayer, 10, ClHill, 1,
                    clFood, 20),
                1 * NumPlayers, 50);

            // ── 绿洲动物（中央环带随机组）──
            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(5, 30, MapSize); ++i)
            {
                var offset = new RmgenVector2D(forestDistance + shoreDistance + waterRadius, 0);
                offset.Rotate(-rng.RandomAngle());
                var animalPos = RmgenVector2D.Add(mapCenter, offset);

                RmgenLibrary.CreateObjectGroup(
                    new RandomGroup(rng, new IGroupElement[]
                    {
                        new ScatterObject(rng, eLion, 1, 2, 0, 4),
                        new ScatterObject(rng, eLioness, 1, 2, 2, 4),
                        new ScatterObject(rng, eGazelle, 4, 6, 1, 5),
                        new ScatterObject(rng, eCamel, 1, 2, 1, 5),
                    }, true, clFood, animalPos),
                    0, null);
            }

            // ── 灌木 ──
            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushB, 1, 2, 0, 2),
                new ScatterObject(rng, aBushA, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clOasis, 2, ClHill, 1, ClPlayer, 1, clPassage, 1),
                RmgenLibrary.ScaleByMapSize(10, 40, MapSize), 20);

            // ── 风沙/水边花草网格点缀（4 格步进；&& 短路抽数同上游）──
            var objectsWaterFlora = new IGroupElement[]
            {
                new ScatterObject(rng, aReedsA, 5, 12, 0, 2),
                new ScatterObject(rng, aReedsB, 5, 12, 0, 2),
            };
            for (int sandx = 0; sandx < MapSize; sandx += 4)
                for (int sandz = 0; sandz < MapSize; sandz += 4)
                {
                    var position = new RmgenVector2D(sandx, sandz);
                    double height = map.GetHeight(position);

                    if (height > heightSand)
                    {
                        if (rng.RandBool((height - heightSand) / 1.4))
                            RmgenLibrary.CreateObjectGroup(
                                new ObjectGroup(new IGroupElement[]
                                    { new ScatterObject(rng, aSand, 0, 1, 0, 2) },
                                    true, null, position),
                                0, null);
                    }
                    else if (height > heightFloraMin && height < heightFloraMax)
                    {
                        if (rng.RandBool(0.4))
                            RmgenLibrary.CreateObjectGroup(
                                new ObjectGroup(new IGroupElement[]
                                    { new ScatterObject(rng, aWaterFlower, 1, 4, 1, 2) },
                                    true, null, position),
                                0, null);
                        else if (rng.RandBool(0.7) && height < heightFloraReedsMax)
                            RmgenLibrary.CreateObjectGroup(
                                new ObjectGroup(objectsWaterFlora, true, null, position),
                                0, null);

                        if (clPassage.CountMembersInRadius(position, 2) != 0)
                        {
                            if (rng.RandBool(0.4))
                                RmgenLibrary.CreateObjectGroup(
                                    new ObjectGroup(new IGroupElement[]
                                        { new ScatterObject(rng, aWaterFlower, 1, 4, 1, 2) },
                                        true, null, position),
                                    0, null);
                            else if (rng.RandBool(0.7) && height < heightFloraReedsMax)
                                RmgenLibrary.CreateObjectGroup(
                                    new ObjectGroup(objectsWaterFlora, true, null, position),
                                    0, null);
                        }
                    }
                }

            return map.MakeExportable();
        }
    }
}
