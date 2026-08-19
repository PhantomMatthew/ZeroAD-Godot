using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>ardennes_forest.js（492 行）——阿登森林：高地图心下凹成谷，
    /// 谷中 ravine/凯尔特人小屋，可探索区（15..45 高度带）内密林 + 散兵资源。
    /// biome 固定 generic/temperate（上游 setBiome 写死，不消耗 biome 选择抽数，
    /// 但 temperate 的 .js 覆盖层抽数照常）。
    /// 环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class ArdennesForestMap : StandardMap
    {
        private static readonly string[] tPrimary =
            { "steppe_grass_03", "steppe_grass_03", "alpine_cliff_c", "temperate_grass_mud_01" };
        private static readonly string[] tGrass =
            { "steppe_grass_04", "steppe_grass_04", "aegean_grass_dirt_03" };
        private const string tPineForestFloor = "steppe_grass_03";
        private static readonly string[] tForestFloor =
            { tPineForestFloor, tPineForestFloor, "temperate_grass_mud_01" };
        private static readonly string[] tCliff =
            { "alpine_cliff_c", "alpine_cliff_c", "temperate_cliff_01" };
        private const string tCity = "new_alpine_citytile";   // 上游为 2 元素名单，简化取首项
        private static readonly string[] tGrassPatch = { "alpine_grass_d" };

        private const string oBoar = "gaia/fauna_boar";
        private const string oDeer = "gaia/fauna_deer";
        private const string oBear = "gaia/fauna_bear_brown";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oMetalSmall = "gaia/ore/alpine_small";
        private const string oMetalLarge = "gaia/ore/temperate_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oStoneLarge = "gaia/rock/temperate_large";

        private const string oOak = "gaia/tree/oak";
        private const string oOakLarge = "gaia/tree/oak_large";
        private const string oPine = "gaia/tree/pine";
        private const string oAleppoPine = "gaia/tree/fir";

        private static readonly string[] aTrees =
        {
            "actor|flora/trees/oak.xml", "actor|flora/trees/oak_large.xml",
            "actor|flora/trees/pine.xml", "actor|flora/trees/fir_tree.xml",
        };

        private const string aGrassLarge = "actor|props/flora/grass_soft_large.xml";
        private const string aWoodLarge = "actor|props/special/eyecandy/wood_pile_1_b.xml";
        private const string aWoodA = "actor|props/special/eyecandy/wood_sm_pile_a.xml";
        private const string aWoodB = "actor|props/special/eyecandy/wood_sm_pile_b.xml";
        private const string aBarrel = "actor|props/special/eyecandy/barrel_a.xml";
        private const string aWheel = "actor|props/special/eyecandy/wheel_laying.xml";
        private const string aCeltHomestead = "actor|structures/celts/homestead.xml";
        private const string aCeltHouse = "actor|structures/celts/house.xml";
        private const string aCeltLongHouse = "actor|structures/celts/longhouse.xml";

        private const double heightRavineValley = 2;
        private const double heightLand = 30;
        private const double heightRavineHill = 40;
        private const double heightHill = 50;
        private const double heightOffsetRavine = 10;

        protected override double HeightLand => heightHill;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 固定 biome（上游 setBiome("generic/temperate")——无选择抽数）
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);
            Biome = settings.BiomeData ?? BiomeLoader.Load(settings.DataRoot, "generic/temperate", rng);
            BiomeName = "generic/temperate";
            Map = new RandomMap(rng, MapSize, heightHill, tPrimary, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;
            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);

            var map = Map;
            var clForestJoin = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clHillDeco = new TileClass(MapSize);
            var clExplorable = new TileClass(MapSize);

            var mapCenter = map.GetCenter();

            // ── 中央下凹 ──
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng,
                    RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.44, MapSize)),
                    0.94, 0.05, 0.1, mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tGrass }, new[] { 3 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, heightLand, 3),
                },
                null);

            // ── 找山丘（噪声扰动山坡；Noise2D 构造抽数在循环前）──
            var noise0 = new Noise2D(rng, 20);
            for (int ix = 0; ix < MapSize; ix++)
                for (int iz = 0; iz < MapSize; iz++)
                {
                    var position = new RmgenVector2D(ix, iz);
                    double h = map.GetHeight(position);
                    if (h > heightRavineHill)
                    {
                        ClHill.Add(position);
                        double x = ix / (MapSize + 1.0);
                        double z = iz / (MapSize + 1.0);
                        double n = (noise0.Get(x, z) - 0.5) * heightRavineHill;
                        map.SetHeight(position, h + n);
                    }
                }

            var (_, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            double DistanceToPlayers(double x, double z)
            {
                double r = 10000;
                for (int i = 0; i < NumPlayers; i++)
                {
                    double dx = x - RmgenLibrary.TilesToFraction(playerPosition[i].X, MapSize);
                    double dz = z - RmgenLibrary.TilesToFraction(playerPosition[i].Y, MapSize);
                    r = Math.Min(r, dx * dx + dz * dz);
                }
                return Math.Sqrt(r);
            }

            double PlayerNearness(double x, double z)
            {
                double d = RmgenLibrary.FractionToTiles(DistanceToPlayers(x, z), MapSize);
                if (d < 13) return 0;
                if (d < 19) return (d - 13) / (19 - 13);
                return 1;
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, tCity, tCity);

            // ── 标玩家领地（ClumpPlacer(250) ≈ 半径 9 圆盘）──
            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, 250, 0.95, 0.3, 0.1, playerPosition[i]),
                    new TileClassPainter(ClPlayer),
                    null);

            // ── 丘陵/峡谷（4 档尺寸）──
            foreach (double hillSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(50, 800, MapSize),
                RmgenLibrary.ScaleByMapSize(50, 400, MapSize),
                RmgenLibrary.ScaleByMapSize(10, 30, MapSize),
                RmgenLibrary.ScaleByMapSize(10, 30, MapSize),
            })
            {
                var mountains = RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, hillSize, 0.1, 0.2, 0.1),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                            { tCliff, new object[] { tForestFloor, tForestFloor, tCliff } },
                            new[] { 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightHill, hillSize < 50 ? 2 : 4),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 8, clBaseResource, 2, ClHill, 5),
                    RmgenLibrary.ScaleByMapSize(1, 4, MapSize));

                if (hillSize > 100 && mountains.Count > 0)
                    RmgenLibrary.CreateAreasInAreas(rng,
                        new ClumpPlacer(rng, hillSize * 0.3, 0.94, 0.05, 0.1),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tCliff, tForestFloor }, new[] { 2 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightOffsetRavine, 3, relative: true),
                        },
                        RmgenLibrary.StayClasses(ClHill, 4),
                        mountains.Count * 2, 20, mountains);

                var ravine = RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, hillSize, 0.1, 0.2, 0.1),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tCliff, tForestFloor }, new[] { 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightRavineValley, 2),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 6, clBaseResource, 2, ClHill, 5),
                    RmgenLibrary.ScaleByMapSize(1, 3, MapSize));

                if (hillSize > 150 && ravine.Count > 0)
                {
                    // 峡谷中的凯尔特人小屋
                    RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                        new RandomGroup(rng, new IGroupElement[]
                        {
                            new ScatterObject(rng, aCeltHouse, 0, 1, 4, 5),
                            new ScatterObject(rng, aCeltLongHouse, 1, 1, 4, 5),
                        }, true, clHillDeco),
                        0,
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(clHillDeco, 3),
                            RmgenLibrary.StayClasses(ClHill, 3),
                        }),
                        ravine.Count * 5, 20, ravine);

                    RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                        new RandomGroup(rng, new IGroupElement[]
                            { new ScatterObject(rng, aCeltHomestead, 1, 1, 1, 1) }, true, clHillDeco),
                        0,
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(clHillDeco, 5),
                            RmgenLibrary.StayClasses(ClHill, 4),
                        }),
                        ravine.Count * 2, 100, ravine);

                    RmgenLibrary.CreateAreasInAreas(rng,
                        new ClumpPlacer(rng, hillSize * 0.3, 0.94, 0.05, 0.1),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tCliff, tForestFloor }, new[] { 2 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightRavineValley, 2),
                        },
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(clHillDeco, 2),
                            RmgenLibrary.StayClasses(ClHill, 0),
                        }),
                        ravine.Count * 2, 20, ravine);

                    RmgenLibrary.CreateAreasInAreas(rng,
                        new ClumpPlacer(rng, hillSize * 0.1, 0.3, 0.05, 0.1),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tCliff, tForestFloor }, new[] { 2 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightRavineHill, 2),
                            new TileClassPainter(ClHill),
                        },
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(clHillDeco, 2),
                            RmgenLibrary.BorderClasses(ClHill, 15, 1),
                        }),
                        ravine.Count * 2, 50, ravine);
                }
            }

            // ── 按高度撒装饰树（&& 短路抽数顺序与上游一致）──
            for (int ix = 0; ix < MapSize; ix++)
                for (int iz = 0; iz < MapSize; iz++)
                {
                    var position = new RmgenVector2D(ix, iz);
                    double h = map.GetHeight(position);

                    if (h > 35 && rng.RandBool(0.1) ||
                        h < 15 && rng.RandBool(0.05) &&
                        clHillDeco.CountMembersInRadius(position, 1) == 0)
                        map.PlaceEntityAnywhere(
                            rng.PickRandom(aTrees),
                            0,
                            RmgenLibrary.RandomPositionOnTile(rng, position),
                            rng.RandomAngle());
                }

            // ── 可探索区（15..45 高度带且不在玩家领地）──
            var explorableArea = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                (IPainter?)null,
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(map, 15, 45),
                    RmgenLibrary.AvoidClasses(ClPlayer, 1),
                }));
            if (explorableArea != null)
                new TileClassPainter(clExplorable).Paint(explorableArea);
            var explorableAreas = explorableArea != null
                ? new List<Area> { explorableArea }
                : new List<Area>();

            // ── 通用噪声（远玩家处起伏大；无抽数）──
            for (int ix = 0; ix < MapSize; ix++)
            {
                double x = ix / (MapSize + 1.0);
                for (int iz = 0; iz < MapSize; iz++)
                {
                    var position = new RmgenVector2D(ix, iz);
                    double z = iz / (MapSize + 1.0);
                    double h = map.GetHeight(position);
                    double pn = PlayerNearness(x, z);
                    double n = (noise0.Get(x, z) - 0.5) * 10;
                    map.SetHeight(position, h + n * pn);
                }
            }

            // ── 森林（嵌套 getTreeCounts）──
            double protoForestTrees = 0.8 * RmgenLibrary.ScaleByMapSize(1300, 8000, MapSize);
            double stragglerTrees = (1 - 0.8) * RmgenLibrary.ScaleByMapSize(1300, 8000, MapSize);
            double forestTreesJoin = 0.25 * protoForestTrees;
            double forestTrees = (1 - 0.25) * protoForestTrees;

            var pForest = new object[]
            {
                tPineForestFloor + "|" + oOak, tForestFloor,
                tPineForestFloor + "|" + oPine, tForestFloor,
                tPineForestFloor + "|" + oAleppoPine, tForestFloor,
                tForestFloor,
            };

            double treeNum = forestTrees / RmgenLibrary.ScaleByMapSize(20, 70, MapSize);
            RmgenLibrary.CreateAreasInAreas(rng,
                new ClumpPlacer(rng, forestTrees / treeNum, 0.1, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new TerrainPainter(pForest, rng),
                    new TileClassPainter(ClForest),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 5, clBaseResource, 4, ClForest, 5, ClHill, 4),
                treeNum, 100, explorableAreas);

            double joinNum = forestTreesJoin /
                (RmgenLibrary.ScaleByMapSize(4, 6, MapSize) * NumPlayers);
            RmgenLibrary.CreateAreasInAreas(rng,
                new ClumpPlacer(rng, forestTreesJoin / joinNum, 0.1, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new TerrainPainter(pForest, rng),
                    new TileClassPainter(ClForest),
                    new TileClassPainter(clForestJoin),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 5, clBaseResource, 4, clForestJoin, 5, ClHill, 4),
                    RmgenLibrary.BorderClasses(ClForest, 1, 4),
                }),
                joinNum, 100, explorableAreas);

            // ── 草地斑块 ──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(new object[] { tGrass, tGrassPatch }, rng) },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 2, ClPlayer, 5),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 砍伐迹地斑块 ──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 120, MapSize), 0.3, 0.06, 0.5),
                new IPainter[] { new TerrainPainter(tForestFloor, rng) },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 2, ClPlayer, 5),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize));

            // ── 平衡矿（限可探索区）──
            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                oMetalSmall, oMetalLarge, ClMetal,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clExplorable, 1),
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer,
                        RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1),
                }));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                oStoneSmall, oStoneLarge, ClRock,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clExplorable, 1),
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer,
                        RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1, ClMetal, 10),
                }));

            // ── 野生动物（限可探索区）──
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(ClHill, 4, ClForest, 0, ClPlayer, 0, clBaseResource, 20),
                3 * NumPlayers, 100, explorableAreas);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBoar, 2, 3, 0, 5) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(ClHill, 4, ClForest, 0, ClPlayer, 0, clBaseResource, 15),
                NumPlayers, 50, explorableAreas);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBear, 1, 1, 0, 4) },
                    false, clFood),
                0,
                RmgenLibrary.AvoidClasses(ClHill, 4, ClForest, 0, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(3, 12, MapSize), 200, explorableAreas);

            // ── 浆果 ──
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 4, clFood, 20),
                rng.RandIntInclusive(3, 12) * NumPlayers + 2, 50);

            // ── 装饰道具 ──
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aWoodA, 1, 2, 0, 1),
                    new ScatterObject(rng, aWoodB, 1, 3, 0, 1),
                    new ScatterObject(rng, aWheel, 0, 2, 0, 1),
                    new ScatterObject(rng, aWoodLarge, 0, 1, 0, 1),
                    new ScatterObject(rng, aBarrel, 0, 2, 0, 1),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0),
                RmgenLibrary.ScaleByMapSize(5, 50, MapSize), 50, explorableAreas);

            // ── 散落树 ──
            var types = new[] { oOak, oOakLarge, oPine, oAleppoPine };
            double stragglerNum = Math.Floor(stragglerTrees / types.Length);
            foreach (string type in types)
                RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                    new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, type, 1, 1, 0, 3) },
                        true, ClForest),
                    0,
                    RmgenLibrary.AvoidClasses(ClForest, 4, ClHill, 5, ClPlayer, 10,
                        clBaseResource, 2, ClMetal, 5, ClRock, 5),
                    stragglerNum, 20, explorableAreas);

            // ── 草丛 ──
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, aGrassLarge, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) }),
                0,
                RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2),
                RmgenLibrary.ScaleByMapSize(50, 300, MapSize), 20, explorableAreas);

            return map.MakeExportable();
        }
    }
}
