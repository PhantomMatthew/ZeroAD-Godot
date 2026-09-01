using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>belgian_uplands.js（逐字移植）——随机高度场经强平滑/重缩放后，
    /// 按水位到高地森林分层刷地表与地形实体；玩家只落在中低海拔可通行带。
    /// setWaterHeight 保留，其余环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class BelgianUplandsMap2 : StandardMap
    {
        private const double MinHeight = -RmgenConstants.SEA_LEVEL;
        private const double MaxHeight = 0xFFFF / RmgenConstants.HEIGHT_UNITS_PER_METRE - RmgenConstants.SEA_LEVEL;
        private const double BuildingOrientation = -SafeMath.PI / 4;

        private static readonly string[] tPrimary =
        {
            "temp_grass", "temp_grass_b", "temp_grass_c", "temp_grass_d",
            "temp_grass_long_b", "temp_grass_clovers_2", "temp_grass_mossy", "temp_grass_plants",
        };

        private double _heightSeaGround;

        protected override double HeightLand => 0;

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
            => env.SetWaterHeight(_heightSeaGround);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            double rangeMin = MinHeight * MapSize / 8192.0;
            double rangeMax = MaxHeight * MapSize / 8192.0;
            double averageWaterCoverage = RmgenLibrary.ScaleByMapSize(1.0 / 5, 1.0 / 3, MapSize);
            double heightSeaGround = _heightSeaGround = -MinHeight + rangeMin +
                averageWaterCoverage * (rangeMax - rangeMin);
            double heightSeaGroundAdjusted = heightSeaGround + MinHeight;
            map.Env.SetWaterHeight(heightSeaGround);

            var terrainTypes = CreateBelgianTerrainTypes(rangeMin, rangeMax, heightSeaGroundAdjusted);
            double lowerHeightLimit = terrainTypes[3].UpperHeightLimit;
            double upperHeightLimit = terrainTypes[6].UpperHeightLimit;

            (List<int> playerIDs, List<RmgenVector2D> playerPosition) placement;
            while (true)
            {
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new RandomElevationPainter(rng, rangeMin, rangeMax),
                    null);

                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new SmoothingPainter(2, 1, 20),
                    null);

                HeightmapLib.RescaleHeightmap(rangeMin, rangeMax, map.Height);

                var tHeightRange = new TileClass(MapSize);
                var area = RmgenLibrary.CreateArea(
                    new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, MapSize) - RmgenConstants.MAP_BORDER_WIDTH,
                        mapCenter),
                    new TileClassPainter(tHeightRange),
                    new HeightConstraint(map, lowerHeightLimit, upperHeightLimit));

                if (area == null)
                    continue;

                var players = PlayerPlacementRandom(rng, map, settings,
                    RmgenCommon.SortAllPlayers(rng, settings),
                    RmgenLibrary.StayClasses(tHeightRange, 15));
                if (players.HasValue)
                {
                    placement = players.Value;
                    break;
                }
            }

            double propDensity = MapSize > 500 ? 1.0 / 4 : MapSize > 400 ? 3.0 / 4 : 1;
            for (int x = 0; x < MapSize; ++x)
                for (int y = 0; y < MapSize; ++y)
                {
                    var position = new RmgenVector2D(x, y);
                    if (!map.ValidHeight(position))
                        continue;

                    double positionHeight = map.GetHeight(position);
                    if (positionHeight < rangeMin ||
                        positionHeight > terrainTypes[^1].UpperHeightLimit)
                        continue;

                    BelgianTerrainType? elem = null;
                    foreach (var terrainType in terrainTypes)
                        if (positionHeight <= terrainType.UpperHeightLimit)
                        {
                            elem = terrainType;
                            break;
                        }
                    if (elem == null)
                        continue;

                    elem.Terrain.Place(map, rng, position);

                    string? template = null;
                    foreach (var actor in elem.Actors)
                        if (rng.RandBool(propDensity / actor.ProbabilityDivisor))
                        {
                            template = actor.TemplateName;
                            break;
                        }
                    if (template != null)
                        map.PlaceEntityAnywhere(template, 0, position, rng.RandomAngle());
                }

            if (!settings.Nomad)
                PlaceBelgianStartingResources(rng, map, settings,
                    placement.playerIDs, placement.playerPosition);

            return map.MakeExportable();
        }

        private static void PlaceBelgianStartingResources(RmgenRng rng, RandomMap map,
            MapSettings settings, IReadOnlyList<int> playerIDs, IReadOnlyList<RmgenVector2D> playerPosition)
        {
            const double resourceDistance = 8;
            const double resourceSpacing = 1;
            const int resourceCount = 4;

            for (int i = 0; i < playerPosition.Count; ++i)
            {
                int playerID = playerIDs[i];
                RmgenCommon.PlaceStartingEntities(map, playerPosition[i], playerID,
                    RmgenCommon.GetStartingEntities(settings.DataRoot,
                        RmgenCommon.GetCivCode(settings, playerID)),
                    6, BuildingOrientation);

                for (int j = 1; j <= 4; ++j)
                {
                    double uAngle = BuildingOrientation - SafeMath.PI * (2 - j) / 2;
                    for (int k = 0; k < resourceCount; ++k)
                    {
                        var offset1 = new RmgenVector2D(resourceDistance, 0);
                        offset1.Rotate(-uAngle);
                        var offset2 = new RmgenVector2D(k * resourceSpacing, 0);
                        offset2.Rotate(-uAngle - SafeMath.PI / 2);
                        var offset3 = new RmgenVector2D(
                            -0.75 * resourceSpacing * Math.Floor(resourceCount / 2.0), 0);
                        offset3.Rotate(-uAngle - SafeMath.PI / 2);

                        string template = j % 2 != 0 ? "gaia/tree/cypress" : "gaia/fruit/berry_01";
                        map.PlaceEntityPassable(template, 0,
                            RmgenVector2D.Add(RmgenVector2D.Add(
                                RmgenVector2D.Add(playerPosition[i], offset1), offset2), offset3),
                            rng.RandomAngle());
                    }
                }
            }
        }

        private static BelgianTerrainType[] CreateBelgianTerrainTypes(double rangeMin, double rangeMax,
            double heightSeaGroundAdjusted)
        {
            return new[]
            {
                new BelgianTerrainType(
                    rangeMin + 1.0 / 3 * (heightSeaGroundAdjusted - rangeMin),
                    "temp_sea_rocks",
                    new[]
                    {
                        new ActorSpawn(100, "actor|props/flora/pond_lillies_large.xml"),
                        new ActorSpawn(40, "actor|props/flora/water_lillies.xml"),
                    }),
                new BelgianTerrainType(
                    rangeMin + 2.0 / 3 * (heightSeaGroundAdjusted - rangeMin),
                    Combine(RepeatValue(25, "temp_sea_weed"),
                        new[] { "temp_sea_weed|gaia/fish/generic" }),
                    new[]
                    {
                        new ActorSpawn(200, "actor|props/flora/pond_lillies_large.xml"),
                        new ActorSpawn(100, "actor|props/flora/water_lillies.xml"),
                    }),
                new BelgianTerrainType(
                    rangeMin + 3.0 / 3 * (heightSeaGroundAdjusted - rangeMin),
                    "temp_mud_a",
                    new[]
                    {
                        new ActorSpawn(200, "actor|props/flora/water_log.xml"),
                        new ActorSpawn(100, "actor|props/flora/water_lillies.xml"),
                        new ActorSpawn(40, "actor|geology/highland_c.xml"),
                        new ActorSpawn(20, "actor|props/flora/reeds_pond_lush_b.xml"),
                        new ActorSpawn(10, "actor|props/flora/reeds_pond_lush_a.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 1.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    Combine(
                        RepeatFlatten(48,
                            "temp_plants_bog",
                            "temp_plants_bog_aut",
                            "temp_dirt_gravel_plants",
                            "temp_grass_d"),
                        RepeatValue(4, "temp_plants_bog|gaia/tree/bush_temperate"),
                        RepeatFlatten(2,
                            "temp_dirt_gravel_plants|gaia/ore/temperate_small",
                            "temp_dirt_gravel_plants|gaia/rock/temperate_small",
                            "temp_plants_bog|gaia/fauna_rabbit"),
                        new[] { "temp_plants_bog_aut|gaia/tree/dead" }),
                    new[]
                    {
                        new ActorSpawn(200, "actor|props/flora/water_log.xml"),
                        new ActorSpawn(100, "actor|geology/highland_c.xml"),
                        new ActorSpawn(40, "actor|props/flora/reeds_pond_lush_a.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 2.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    new[] { "temp_grass", "temp_grass_d", "temp_grass_long_b", "temp_grass_plants" },
                    new[]
                    {
                        new ActorSpawn(800, "actor|props/flora/grass_field_flowering_tall.xml"),
                        new ActorSpawn(400, "actor|geology/gray_rock1.xml"),
                        new ActorSpawn(200, "actor|props/flora/bush_tempe_sm_lush.xml"),
                        new ActorSpawn(100, "actor|props/flora/bush_tempe_b.xml"),
                        new ActorSpawn(40, "actor|props/flora/grass_soft_small_tall.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 3.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    new[] { "temp_grass", "temp_grass_b", "temp_grass_c", "temp_grass_mossy" },
                    new[]
                    {
                        new ActorSpawn(800, "actor|geology/decal_stone_medit_a.xml"),
                        new ActorSpawn(400, "actor|props/flora/decals_flowers_daisies.xml"),
                        new ActorSpawn(200, "actor|props/flora/bush_tempe_underbrush.xml"),
                        new ActorSpawn(100, "actor|props/flora/grass_soft_small_tall.xml"),
                        new ActorSpawn(40, "actor|props/flora/grass_temp_field.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 4.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    new[]
                    {
                        "temp_grass", "temp_grass_b", "temp_grass_c", "temp_grass_d",
                        "temp_grass_long_b", "temp_grass_clovers_2", "temp_grass_mossy", "temp_grass_plants",
                    },
                    new[]
                    {
                        new ActorSpawn(400, "actor|geology/stone_granite_boulder.xml"),
                        new ActorSpawn(200, "actor|props/flora/foliagebush.xml"),
                        new ActorSpawn(100, "actor|props/flora/bush_tempe_underbrush.xml"),
                        new ActorSpawn(40, "actor|props/flora/grass_soft_small_tall.xml"),
                        new ActorSpawn(20, "actor|props/flora/ferns.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 5.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    InterleaveWithMain("temp_grass_plants",
                        "temp_grass_plants|gaia/tree/euro_beech",
                        "temp_grass_mossy|gaia/tree/poplar",
                        "temp_grass_mossy|gaia/tree/poplar_lombardy",
                        "temp_grass_long|gaia/tree/bush_temperate",
                        "temp_mud_plants|gaia/tree/bush_temperate",
                        "temp_mud_plants|gaia/tree/bush_badlands",
                        "temp_grass_long|gaia/fruit/apple",
                        "temp_grass_clovers|gaia/fruit/berry_01",
                        "temp_grass_clovers_2|gaia/fruit/grapes",
                        "temp_grass_plants|gaia/fauna_deer",
                        "temp_grass_long_b|gaia/fauna_rabbit"),
                    new[]
                    {
                        new ActorSpawn(400, "actor|geology/highland_c.xml"),
                        new ActorSpawn(200, "actor|props/flora/bush_tempe_a.xml"),
                        new ActorSpawn(100, "actor|props/flora/ferns.xml"),
                        new ActorSpawn(40, "actor|props/flora/grass_soft_tuft_a.xml"),
                    }),
                new BelgianTerrainType(
                    heightSeaGroundAdjusted + 6.0 / 6 * (rangeMax - heightSeaGroundAdjusted),
                    new[]
                    {
                        "temp_grass_mossy|gaia/tree/oak",
                        "temp_forestfloor_pine|gaia/tree/pine",
                        "temp_grass_mossy|gaia/tree/oak",
                        "temp_forestfloor_pine|gaia/tree/pine",
                        "temp_mud_plants|gaia/tree/dead",
                        "temp_plants_bog|gaia/tree/oak_large",
                        "temp_dirt_gravel_plants|gaia/tree/aleppo_pine",
                        "temp_forestfloor_autumn|gaia/tree/carob",
                    },
                    new[]
                    {
                        new ActorSpawn(200, "actor|geology/highland2_moss.xml"),
                        new ActorSpawn(100, "actor|props/flora/grass_soft_tuft_a.xml"),
                        new ActorSpawn(40, "actor|props/flora/ferns.xml"),
                    }),
            };
        }

        private static (List<int> playerIDs, List<RmgenVector2D> playerPosition)? PlayerPlacementRandom(
            RmgenRng rng, RandomMap map, MapSettings settings, List<int> playerIDs,
            IConstraint? constraints)
        {
            int numPlayers = RmgenCommon.GetNumPlayers(settings);
            var locations = new List<RmgenVector2D>();
            int attempts = 0;
            int resets = 0;

            var mapCenter = map.GetCenter();
            double playerMinDistSquared =
                SafeMath.Square(RmgenLibrary.FractionToTiles(0.25, map.GetSize()));
            double borderDistance = RmgenLibrary.FractionToTiles(0.08, map.GetSize());
            var area = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, constraints);
            if (area == null || area.PointCount == 0)
                return null;

            for (int i = 0; i < numPlayers; ++i)
            {
                var position = rng.PickRandom(area.GetPoints());
                bool tooClose = false;
                foreach (var loc in locations)
                    if (loc.DistanceToSquared(position) < playerMinDistSquared)
                    {
                        tooClose = true;
                        break;
                    }

                if (tooClose ||
                    position.DistanceToSquared(mapCenter) >
                    SafeMath.Square(mapCenter.X - borderDistance))
                {
                    --i;
                    ++attempts;

                    if (attempts > 500)
                    {
                        locations = new List<RmgenVector2D>();
                        i = -1;
                        attempts = 0;
                        ++resets;

                        if (resets % 25 == 0)
                            playerMinDistSquared *= 0.95;

                        if (resets == 500)
                            return null;
                    }
                    continue;
                }

                if (locations.Count == i)
                    locations.Add(position);
                else
                    locations[i] = position;
            }

            return RmgenCommon.GroupPlayersByArea(rng, settings, playerIDs, locations);
        }

        private static string[] RepeatValue(int count, string value)
        {
            var result = new string[count];
            for (int i = 0; i < count; ++i)
                result[i] = value;
            return result;
        }

        private static string[] RepeatFlatten(int count, params string[] values)
        {
            var result = new List<string>(count * values.Length);
            for (int i = 0; i < count; ++i)
                result.AddRange(values);
            return result.ToArray();
        }

        private static string[] Combine(params IReadOnlyList<string>[] parts)
        {
            var result = new List<string>();
            foreach (var part in parts)
                result.AddRange(part);
            return result.ToArray();
        }

        private static string[] InterleaveWithMain(string mainTerrain, params string[] values)
        {
            var result = new List<string>(values.Length * 2);
            foreach (string value in values)
            {
                result.Add(value);
                result.Add(mainTerrain);
            }
            return result.ToArray();
        }

        private readonly struct ActorSpawn
        {
            public readonly double ProbabilityDivisor;
            public readonly string TemplateName;

            public ActorSpawn(double probabilityDivisor, string templateName)
            {
                ProbabilityDivisor = probabilityDivisor;
                TemplateName = templateName;
            }
        }

        private sealed class BelgianTerrainType
        {
            public readonly double UpperHeightLimit;
            public readonly ITerrain Terrain;
            public readonly IReadOnlyList<ActorSpawn> Actors;

            public BelgianTerrainType(double upperHeightLimit, object terrain,
                IReadOnlyList<ActorSpawn> actors)
            {
                UpperHeightLimit = upperHeightLimit;
                Terrain = TerrainFactory.CreateTerrain(terrain);
                Actors = actors;
            }
        }
    }

    /// <summary>schwarzwald.js（逐字移植）——中央低洼 diamond-square 盆地，玩家基地压平，
    /// 水岸/道路外全图按径向密度逐格长成高密度黑森林迷宫。setWaterHeight 保留，
    /// 其余环境设置由表驱动或按约定省略；placePlayersNomad 省略。</summary>
    public sealed class SchwarzwaldMap2 : StandardMap
    {
        private const double MinHeight = -RmgenConstants.SEA_LEVEL;
        private const double MaxHeight = 0xFFFF / RmgenConstants.HEIGHT_UNITS_PER_METRE - RmgenConstants.SEA_LEVEL;
        private const double BuildingOrientation = -SafeMath.PI / 4;

        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oMetalLarge = "gaia/ore/alpine_large";
        private const string oFish = "gaia/fish/generic";
        private const string oOakLarge = "gaia/tree/oak_large";
        private const string oBerryBush = "gaia/fruit/berry_01";

        private const string aGrass = "actor|props/flora/grass_soft_small_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";
        private const string aReeds = "actor|props/flora/reeds_pond_lush_b.xml";

        private static readonly string[] terrainPrimary = { "temp_grass_plants", "temp_plants_bog" };
        private static readonly string[] terrainWood =
        {
            "alpine_forrestfloor|gaia/tree/oak",
            "alpine_forrestfloor|gaia/tree/pine",
        };
        private static readonly string[] terrainWoodBorder =
        {
            "new_alpine_grass_mossy|gaia/tree/oak",
            "alpine_forrestfloor|gaia/tree/pine",
            "temp_grass_long|gaia/tree/bush_temperate",
            "temp_grass_clovers|gaia/fruit/berry_01",
            "temp_grass_clovers_2|gaia/fruit/grapes",
            "temp_grass_plants|gaia/fauna_deer",
            "temp_grass_plants|gaia/fauna_rabbit",
            "new_alpine_grass_dirt_a",
        };
        private static readonly string[] terrainBase =
        {
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_grass_plants|gaia/fauna_sheep",
        };
        private static readonly string[] terrainBaseBorder =
        {
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_d",
            "temp_grass_plants",
            "temp_plants_bog",
            "temp_grass_plants",
            "temp_grass_plants",
        };
        private static readonly string[] baseTex = { "temp_road", "temp_road_overgrown" };
        private static readonly string[] terrainPath = { "temp_road", "temp_road_overgrown" };
        private static readonly string[] tWater = { "dirt_brown_d" };
        private static readonly string[] tWaterBorder = { "dirt_brown_d" };

        private double _heightSeaGround;

        protected override double HeightLand => 1;

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
            => env.SetWaterHeight(_heightSeaGround);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, terrainPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();
            double mapRadius = MapSize / 2.0;
            var clPath = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clOpen = new TileClass(MapSize);

            const double baseRadius = 15;
            const double heightOffsetPath = -0.1;
            double minPlayerRadius = Math.Min(mapRadius - 1.5 * baseRadius, 5.0 / 8 * mapRadius);
            double maxPlayerRadius = Math.Min(mapRadius - baseRadius, 3.0 / 4 * mapRadius);

            var playerPosition = new List<RmgenVector2D>();
            double playerAngleStart = rng.RandomAngle();
            double playerAngleAddAvrg = 2 * SafeMath.PI / NumPlayers;
            double playerAngleMaxOff = playerAngleAddAvrg / 4;

            double resourceRadius = RmgenLibrary.FractionToTiles(1.0 / 3, MapSize);
            double maxTreeDensity = Math.Min(256.0 * (192 + 8 * NumPlayers) /
                SafeMath.Square(MapSize), 1);
            const double bushChance = 1.0 / 3;

            double rangeMin = MinHeight * (MapSize + 512) / 8192.0;
            double rangeMax = MaxHeight * (MapSize + 512) / 8192.0;
            const double averageWaterCoverage = 1.0 / 5;
            double heightSeaGround = _heightSeaGround = -MinHeight + rangeMin +
                averageWaterCoverage * (rangeMax - rangeMin);
            double heightSeaGroundAdjusted = heightSeaGround + MinHeight;
            map.Env.SetWaterHeight(heightSeaGround);

            double[][] initialReliefmap =
            {
                new[] { rangeMax, rangeMax, rangeMax },
                new[] { rangeMax, rangeMin, rangeMax },
                new[] { rangeMax, rangeMax, rangeMax },
            };
            HeightmapLib.SetBaseTerrainDiamondSquare(rng, map.Height,
                rangeMin, rangeMax, initialReliefmap, 0.5);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new SmoothingPainter(1, 0.8, 5),
                null);

            HeightmapLib.RescaleHeightmap(rangeMin, rangeMax, map.Height);

            var heightLimits = new[]
            {
                rangeMin + 1.0 / 3 * (heightSeaGroundAdjusted - rangeMin),
                rangeMin + 2.0 / 3 * (heightSeaGroundAdjusted - rangeMin),
                rangeMin + (heightSeaGroundAdjusted - rangeMin),
                heightSeaGroundAdjusted + 1.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 2.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 3.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 4.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 5.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 6.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + 7.0 / 8 * (rangeMax - heightSeaGroundAdjusted),
                heightSeaGroundAdjusted + (rangeMax - heightSeaGroundAdjusted),
            };

            for (int i = 0; i < NumPlayers; ++i)
            {
                double radius = rng.RandFloat(minPlayerRadius, maxPlayerRadius);
                double angle = -((playerAngleStart + i * playerAngleAddAvrg +
                    rng.RandFloat(0, playerAngleMaxOff)) % (2 * SafeMath.PI));
                var offset = new RmgenVector2D(radius, 0);
                offset.Rotate(angle);
                var position = RmgenVector2D.Add(mapCenter, offset);
                position.Round();
                playerPosition.Add(position);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(20), 0.8, 0.8,
                        double.PositiveInfinity, position),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        map.GetHeight(position), 20),
                    null);
            }

            PlaceSchwarzwaldPlayerBases(rng, map, settings, playerPosition);

            for (int h = 0; h < 2; ++h)
            {
                double minHeight = h == 0 ? heightLimits[3] : (heightLimits[5] + heightLimits[6]) / 2;
                double maxHeight = h == 0 ? (heightLimits[4] + heightLimits[3]) / 2 : heightLimits[7];

                foreach (var pair in new[] { (oStoneLarge, ClRock), (oMetalLarge, ClMetal) })
                    RmgenLibrary.CreateObjectGroups(rng,
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, pair.Item1, 1, 1, 0, 4),
                        }, true, pair.Item2),
                        0,
                        new AndConstraint(new IConstraint[]
                        {
                            new HeightConstraint(map, minHeight, maxHeight),
                            RmgenLibrary.AvoidClasses(ClForest, 4, ClPlayer, 20,
                                ClMetal, 40, ClRock, 40),
                        }),
                        RmgenLibrary.ScaleByMapSize(2, 8, MapSize),
                        100);
            }

            double betweenShallowAndShore = (heightLimits[3] + heightLimits[2]) / 2;
            RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinIncludeMax,
                    heightLimits[2], betweenShallowAndShore),
                new LayeredPainter(new object[] { terrainBase, terrainBaseBorder }, new[] { 5 }, rng),
                null);

            RmgenLibrary.PaintTileClassBasedOnHeight(heightLimits[2], betweenShallowAndShore,
                HeightPlacer.Mode.IncludeMinIncludeMax, clOpen);

            RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinIncludeMax, rangeMin, heightLimits[2]),
                new LayeredPainter(new object[] { tWaterBorder, tWater }, new[] { 2 }, rng),
                null);

            RmgenLibrary.PaintTileClassBasedOnHeight(rangeMin, heightLimits[2],
                HeightPlacer.Mode.IncludeMinIncludeMax, clWater);

            bool pathBlending = NumPlayers <= 4;
            for (int i = 0; i < NumPlayers + (pathBlending ? 1 : 0); ++i)
                for (int j = pathBlending ? 0 : i + 1; j < NumPlayers + 1; ++j)
                {
                    var pathStart = i < NumPlayers ? playerPosition[i] : mapCenter;
                    var pathEnd = j < NumPlayers ? playerPosition[j] : mapCenter;

                    RmgenLibrary.CreateArea(
                        new RandomPathPlacer(rng, pathStart, pathEnd, 1.75, baseRadius / 2, pathBlending),
                        new IPainter[]
                        {
                            new TerrainPainter(terrainPath, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightOffsetPath, 1, relative: true),
                            new TileClassPainter(clPath),
                        },
                        RmgenLibrary.AvoidClasses(clPath, 0, clOpen, 0, clWater, 4, ClBaseResource, 4));
                }

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 0, clPath, 3, clWater, 3));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) },
                },
                new double[] { 100 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 5),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                clFood);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aReeds, 1, 1, 0, 0),
                }, true),
                0,
                RmgenLibrary.BorderClasses(clWater, 0, 6),
                RmgenLibrary.ScaleByMapSize(1, 2, MapSize) * 1000,
                1000);

            var woodTerrain = TerrainFactory.CreateTerrain(terrainWood);
            var woodBorderTerrain = TerrainFactory.CreateTerrain(terrainWoodBorder);
            for (int x = 0; x < MapSize; x++)
                for (int z = 0; z < MapSize; z++)
                {
                    var position = new RmgenVector2D(x, z);
                    if (!map.ValidTile(position))
                        continue;

                    double radius = RmgenVector2D.Add(position, new RmgenVector2D(0.5, 0.5))
                        .DistanceTo(mapCenter);
                    double minDistToSL = MapSize;
                    for (int i = 0; i < NumPlayers; ++i)
                        minDistToSL = Math.Min(minDistToSL, position.DistanceTo(playerPosition[i]));

                    double tDensFactSL = Math.Max(Math.Min((minDistToSL - baseRadius) / baseRadius, 1), 0);
                    double tDensFactRad = Math.Abs((resourceRadius - radius) / resourceRadius);
                    double tDensActual = maxTreeDensity * tDensFactSL * tDensFactRad * 0.75;

                    if (!rng.RandBool(tDensActual))
                        continue;

                    bool border = tDensActual < rng.RandFloat(0, bushChance * maxTreeDensity);
                    IConstraint constraint = border ?
                        RmgenLibrary.AvoidClasses(clPath, 1, clOpen, 2, clWater, 3, ClMetal, 4, ClRock, 4) :
                        RmgenLibrary.AvoidClasses(clPath, 2, clOpen, 3, clWater, 4, ClMetal, 4, ClRock, 4);

                    if (constraint.Allows(position))
                    {
                        ClForest.Add(position);
                        (border ? woodBorderTerrain : woodTerrain).Place(map, rng, position);
                    }
                }

            return map.MakeExportable();
        }

        private void PlaceSchwarzwaldPlayerBases(RmgenRng rng, RandomMap map,
            MapSettings settings, IReadOnlyList<RmgenVector2D> playerPosition)
        {
            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            if (settings.Nomad)
                return;

            IConstraint baseResourceConstraint = RmgenLibrary.AvoidClasses(ClBaseResource, 4);

            for (int i = 0; i < playerPosition.Count; ++i)
            {
                int playerID = playerIDs[i];
                RmgenCommon.PlaceStartingEntities(map, playerPosition[i], playerID,
                    RmgenCommon.GetStartingEntities(settings.DataRoot,
                        RmgenCommon.GetCivCode(settings, playerID)),
                    6, BuildingOrientation);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, Math.Floor(RmgenGeometry.DiskArea(0.8 * 15)),
                        0.6, 1.0 / 8, double.PositiveInfinity, playerPosition[i]),
                    new IPainter[]
                    {
                        new TerrainPainter(new object[] { baseTex }, rng),
                        new TileClassPainter(ClPlayer),
                    },
                    null);

                PlaceSchwarzwaldBaseTrees(rng, playerPosition[i], baseResourceConstraint);
                PlaceSchwarzwaldBaseMines(rng, map, playerPosition[i], baseResourceConstraint);
                PlaceSchwarzwaldBaseBerries(rng, playerPosition[i], baseResourceConstraint);
            }
        }

        private void PlaceSchwarzwaldBaseTrees(RmgenRng rng,
            RmgenVector2D playerPos, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var off = new RmgenVector2D(0, rng.RandFloat(11, 13));
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);
                position.Round();

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oOakLarge, 2, 2, 0, 5),
                    }, false, ClBaseResource, position),
                    0, constraint))
                    return;
            }
        }

        private void PlaceSchwarzwaldBaseMines(RmgenRng rng, RandomMap map,
            RmgenVector2D playerPos, IConstraint constraint)
        {
            double angleBetweenMines = rng.RandFloat(SafeMath.PI / 2, SafeMath.PI);
            string[] mineTemplates = { oMetalLarge, oStoneLarge };

            for (int tries = 0; tries < 75; ++tries)
            {
                var pos = new RmgenVector2D[mineTemplates.Length];
                double startAngle = rng.RandomAngle();
                bool success = true;
                for (int i = 0; i < mineTemplates.Length; ++i)
                {
                    double angle = startAngle + angleBetweenMines * (i + (mineTemplates.Length - 1) / 2.0);
                    var off = new RmgenVector2D(0, 15);
                    off.Rotate(angle);
                    var minePos = RmgenVector2D.Add(off, playerPos);
                    minePos.Round();
                    pos[i] = minePos;
                    if (!map.ValidTilePassable(minePos) || !constraint.Allows(minePos))
                    {
                        success = false;
                        break;
                    }
                }

                if (!success)
                    continue;

                for (int i = 0; i < mineTemplates.Length; ++i)
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, mineTemplates[i], 1, 1, 0, 0),
                        }, true, ClBaseResource, pos[i]),
                        0, null);
                return;
            }
        }

        private void PlaceSchwarzwaldBaseBerries(RmgenRng rng,
            RmgenVector2D playerPos, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var off = new RmgenVector2D(0, 12);
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oBerryBush, 2, 2, 1, 3),
                    }, true, ClBaseResource, position),
                    0, constraint))
                    return;
            }
        }
    }

    /// <summary>caledonian_meadows.js（逐字移植）——diamond-square 丘陵经五轮溅射侵蚀，
    /// 玩家/道路先平滑，再按高度带与坡度分刷高地草甸、林带、湿岸；矿点、围栏羊圈、
    /// 林苑与营地沿高度带轮替。setWaterHeight 保留；环境、伊比利亚起始墙与 placePlayersNomad 省略。</summary>
    public sealed class CaledonianMeadowsMap2 : StandardMap
    {
        private const double MinHeight = -RmgenConstants.SEA_LEVEL;
        private const double MaxHeight = 0xFFFF / RmgenConstants.HEIGHT_UNITS_PER_METRE - RmgenConstants.SEA_LEVEL;
        private const double BuildingOrientation = -SafeMath.PI / 4;

        private const string tGrove = "temp_grass_plants";
        private const string tPath = "road_rome_a";

        private static readonly string[] oGroveEntities =
            { "structures/gaul/outpost", "gaia/tree/oak_new" };
        private static readonly string[] decorations =
        {
            "actor|geology/gray1.xml",
            "actor|geology/gray_rock1.xml",
            "actor|geology/highland1.xml",
            "actor|geology/highland2.xml",
            "actor|geology/highland3.xml",
            "actor|geology/highland_c.xml",
            "actor|geology/highland_d.xml",
            "actor|geology/highland_e.xml",
            "actor|props/flora/bush.xml",
            "actor|props/flora/bush_dry_a.xml",
            "actor|props/flora/bush_highlands.xml",
            "actor|props/flora/bush_tempe_a.xml",
            "actor|props/flora/bush_tempe_b.xml",
            "actor|props/flora/ferns.xml",
        };
        private static readonly string[] groveEntities =
            { "gaia/tree/bush_temperate", "gaia/tree/euro_beech" };
        private static readonly string[] groveActors =
        {
            "actor|geology/highland1_moss.xml",
            "actor|geology/highland2_moss.xml",
            "actor|props/flora/bush.xml",
            "actor|props/flora/bush_dry_a.xml",
            "actor|props/flora/bush_highlands.xml",
            "actor|props/flora/bush_tempe_a.xml",
            "actor|props/flora/bush_tempe_b.xml",
            "actor|props/flora/ferns.xml",
        };
        private static readonly string[] campEntities =
        {
            "gaia/treasure/metal",
            "gaia/treasure/standing_stone",
            "units/brit/infantry_slinger_b",
            "units/brit/infantry_javelineer_b",
            "units/gaul/infantry_slinger_b",
            "units/gaul/infantry_javelineer_b",
            "units/gaul/champion_fanatic",
            "actor|props/special/common/waypoint_flag_factions.xml",
            "actor|props/special/eyecandy/barrel_a.xml",
            "actor|props/special/eyecandy/basket_celt_a.xml",
            "actor|props/special/eyecandy/crate_a.xml",
            "actor|props/special/eyecandy/dummy_a.xml",
            "actor|props/special/eyecandy/handcart_1.xml",
            "actor|props/special/eyecandy/handcart_1_broken.xml",
            "actor|props/special/eyecandy/sack_1.xml",
            "actor|props/special/eyecandy/sack_1_rough.xml",
        };
        private static readonly string[] foodEntities =
            { "gaia/fruit/berry_01", "gaia/fauna_chicken", "gaia/fauna_chicken" };

        private double _heightSeaGround;

        protected override double HeightLand => 0;

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
            => env.SetWaterHeight(_heightSeaGround);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, "whiteness");
            var map = Map;

            double heightScale = (MapSize + 256) / 768.0 / 4;
            double rangeMin = MinHeight * heightScale;
            double rangeMax = MaxHeight * heightScale;
            const double averageWaterCoverage = 1.0 / 5;
            double heightSeaGround = _heightSeaGround = -MinHeight + rangeMin +
                averageWaterCoverage * (rangeMax - rangeMin);
            double heightSeaGroundAdjusted = heightSeaGround + MinHeight;
            map.Env.SetWaterHeight(heightSeaGround);

            double medH = (rangeMin + rangeMax) / 2;
            double[][] initialHeightmap =
            {
                new[] { medH, medH },
                new[] { medH, medH },
            };
            HeightmapLib.SetBaseTerrainDiamondSquare(rng, map.Height,
                rangeMin, rangeMax, initialHeightmap, 0.8);

            for (int i = 0; i < 5; ++i)
                HeightmapLib.SplashErodeMap(0.1, map.Height);

            HeightmapLib.RescaleHeightmap(rangeMin, rangeMax, map.Height);

            var heightLimits = CreateCaledonianHeightLimits(rangeMin, rangeMax, heightSeaGroundAdjusted);
            double playerHeight = (heightLimits[4] + heightLimits[5]) / 2;
            var heightBiome = CreateCaledonianBiome();

            var startLocations = HeightmapLib.GetStartLocationsByHeightmap(rng, map,
                heightLimits[4], heightLimits[5], 1000, 30, NumPlayers, settings.CircularMap)
                ?? throw new InvalidOperationException("caledonian_meadows: no valid start locations");
            var (playerIDs, playerPosition) = RmgenCommon.GroupPlayersCycle(rng, settings, startLocations);

            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new DiskPlacer(35, position),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        map.GetHeight(position), 35),
                    null);

            var clPath = new TileClass(MapSize);
            for (int i = 0; i < playerPosition.Count; ++i)
                RmgenLibrary.CreateArea(
                    new RandomPathPlacer(rng, playerPosition[i],
                        playerPosition[(i + 1) % playerPosition.Count], 4, 2, false),
                    new IPainter[]
                    {
                        new TerrainPainter(tPath, rng),
                        new ElevationBlendingPainter(playerHeight, 0.4),
                        new TileClassPainter(clPath),
                    },
                    null);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new SmoothingPainter(5, 1, 1),
                new NearTileClassConstraint(clPath, 5));

            var avoidPoints = playerPosition
                .Select(pos => new HeightmapLib.HeightPoint((int)pos.X, (int)pos.Y, 30))
                .ToList();
            var resourceSpots = GetPointsByHeightAvoidClass(rng, map,
                (heightLimits[3] + heightLimits[4]) / 2,
                (heightLimits[5] + heightLimits[6]) / 2,
                avoidPoints, clPath, 20, 2 * MapSize, settings.CircularMap);

            var tchm = HeightmapLib.GetTileCenteredHeightmap(map.Height);
            var areas = new List<RmgenVector2D>[heightLimits.Length];
            for (int h = 0; h < areas.Length; ++h)
                areas[h] = new List<RmgenVector2D>();

            IConstraint avoidPath = RmgenLibrary.AvoidClasses(clPath, 0);
            for (int x = 0; x < tchm.Length; ++x)
                for (int y = 0; y < tchm[0].Length; ++y)
                {
                    var position = new RmgenVector2D(x, y);
                    if (!avoidPath.Allows(position) || tchm[x][y] < rangeMin)
                        continue;

                    for (int h = 0; h < heightLimits.Length; ++h)
                        if (tchm[x][y] <= heightLimits[h])
                        {
                            areas[h].Add(position);
                            break;
                        }
                }

            var slopeMap = HeightmapLib.GetSlopeMap(map.Height);
            var slopeMidpoints = new double[areas.Length];
            for (int h = 0; h < areas.Length; ++h)
            {
                double minSlope = double.PositiveInfinity;
                double maxSlope = double.NegativeInfinity;
                foreach (var point in areas[h])
                {
                    double slope = slopeMap[(int)point.X][(int)point.Y];
                    minSlope = Math.Min(minSlope, slope);
                    maxSlope = Math.Max(maxSlope, slope);
                }
                slopeMidpoints[h] = minSlope + maxSlope;
            }

            for (int h = 0; h < heightLimits.Length; ++h)
                foreach (var point in areas[h])
                {
                    bool isFlat = slopeMap[(int)point.X][(int)point.Y] < 0.4 * slopeMidpoints[h];
                    var selectedBiome = isFlat ? heightBiome[h].Flat : heightBiome[h].Steep;

                    map.SetTexture(point, rng.PickRandom(selectedBiome.Texture));

                    if (rng.RandBool(selectedBiome.EntityProbability))
                    {
                        string entity = rng.PickRandom(selectedBiome.Entity);
                        var entityPosition = RmgenLibrary.RandomPositionOnTile(rng, point);
                        map.PlaceEntityPassable(entity, 0, entityPosition, rng.RandomAngle());
                    }
                }

            if (!settings.Nomad)
                for (int p = 0; p < playerIDs.Count; ++p)
                {
                    RmgenCommon.PlaceStartingEntities(map, playerPosition[p], playerIDs[p],
                        RmgenCommon.GetStartingEntities(settings.DataRoot,
                            RmgenCommon.GetCivCode(settings, playerIDs[p])),
                        6, BuildingOrientation);
                    PlaceStartLocationResources(rng, map, playerPosition[p]);
                }

            var fences = CreateCaledonianFences();
            var otherStyle = CaledonianOtherStyle();
            for (int i = 0; i < resourceSpots.Count; ++i)
            {
                var pos = new RmgenVector2D(resourceSpots[i].X, resourceSpots[i].Y);
                int choice = i % (settings.Nomad ? 4 : 5);
                if (choice == 0)
                    PlaceMine(rng, map, pos, "gaia/rock/temperate_large_02");
                if (choice == 1)
                    PlaceMine(rng, map, pos, "gaia/ore/temperate_large");
                if (choice == 2)
                    WallBuilder.PlaceCustomFortress(map, otherStyle, pos,
                        rng.PickRandom(fences), 0, rng.RandomAngle(), null);
                if (choice == 3)
                    PlaceGrove(rng, map, pos);
                if (choice == 4)
                    PlaceCamp(rng, map, pos);
            }

            return map.MakeExportable();
        }

        private static double[] CreateCaledonianHeightLimits(double rangeMin, double rangeMax,
            double heightSeaGroundAdjusted)
        {
            var input = new[]
            {
                (true, 1.0 / 3),
                (true, 2.0 / 3),
                (true, 3.0 / 3),
                (false, 1.0 / 8),
                (false, 2.0 / 8),
                (false, 3.0 / 8),
                (false, 4.0 / 8),
                (false, 5.0 / 8),
                (false, 6.0 / 8),
                (false, 7.0 / 8),
                (false, 8.0 / 8),
            };
            var result = new double[input.Length];
            for (int i = 0; i < input.Length; ++i)
            {
                double baseHeight = input[i].Item1 ? rangeMin : heightSeaGroundAdjusted;
                double factor = input[i].Item1 ? heightSeaGroundAdjusted - rangeMin :
                    rangeMax - heightSeaGroundAdjusted;
                result[i] = baseHeight + input[i].Item2 * factor;
            }
            return result;
        }

        private static HeightBiomeBand[] CreateCaledonianBiome()
        {
            return new[]
            {
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "shoreline_stoney_a" },
                        new[] { "gaia/fish/generic", "actor|geology/stone_granite_boulder.xml" },
                        0.02),
                    new BiomeVariant(
                        new[] { "alpine_mountainside" },
                        new[] { "gaia/fish/generic" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "shoreline_stoney_a", "alpine_shore_rocks" },
                        new[] { "actor|geology/stone_granite_boulder.xml", "actor|geology/stone_granite_med.xml" },
                        0.03),
                    new BiomeVariant(
                        new[] { "alpine_mountainside" },
                        new[] { "actor|geology/stone_granite_boulder.xml", "actor|geology/stone_granite_med.xml" },
                        0.0)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_shore_rocks" },
                        new[]
                        {
                            "actor|props/flora/reeds_pond_dry.xml",
                            "actor|geology/stone_granite_large.xml",
                            "actor|geology/stone_granite_med.xml",
                            "actor|props/flora/reeds_pond_lush_b.xml",
                        },
                        0.2),
                    new BiomeVariant(
                        new[] { "alpine_mountainside" },
                        new[] { "actor|props/flora/reeds_pond_dry.xml", "actor|geology/stone_granite_med.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_shore_rocks_grass_50", "alpine_grass_rocky" },
                        new[]
                        {
                            "gaia/tree/pine",
                            "gaia/tree/bush_badlands",
                            "actor|geology/highland1_moss.xml",
                            "actor|props/flora/grass_soft_tuft_a.xml",
                            "actor|props/flora/bush.xml",
                        },
                        0.3),
                    new BiomeVariant(
                        new[] { "alpine_mountainside" },
                        new[] { "actor|props/flora/grass_soft_tuft_a.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_dirt_grass_50", "alpine_grass_rocky" },
                        new[]
                        {
                            "actor|geology/stone_granite_med.xml",
                            "actor|props/flora/grass_soft_tuft_a.xml",
                            "actor|props/flora/bush.xml",
                            "actor|props/flora/grass_medit_flowering_tall.xml",
                        },
                        0.2),
                    new BiomeVariant(
                        new[] { "alpine_grass_rocky" },
                        new[] { "actor|geology/stone_granite_med.xml", "actor|props/flora/grass_soft_tuft_a.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "new_alpine_grass_c", "new_alpine_grass_b", "new_alpine_grass_d" },
                        new[]
                        {
                            "actor|geology/stone_granite_small.xml",
                            "actor|props/flora/grass_soft_small.xml",
                            "actor|props/flora/grass_medit_flowering_tall.xml",
                        },
                        0.2),
                    new BiomeVariant(
                        new[] { "alpine_grass_rocky" },
                        new[] { "actor|geology/stone_granite_small.xml", "actor|props/flora/grass_soft_small.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "new_alpine_grass_a", "alpine_grass_rocky" },
                        new[]
                        {
                            "actor|geology/stone_granite_med.xml",
                            "actor|props/flora/grass_tufts_a.xml",
                            "actor|props/flora/bush_highlands.xml",
                            "actor|props/flora/grass_medit_flowering_tall.xml",
                        },
                        0.2),
                    new BiomeVariant(
                        new[] { "alpine_grass_rocky" },
                        new[] { "actor|geology/stone_granite_med.xml", "actor|props/flora/grass_tufts_a.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "new_alpine_grass_mossy", "alpine_grass_rocky" },
                        new[]
                        {
                            "gaia/tree/pine",
                            "gaia/tree/oak",
                            "actor|props/flora/grass_tufts_a.xml",
                            "gaia/fruit/berry_01",
                            "actor|geology/highland2_moss.xml",
                            "gaia/fauna_goat",
                            "actor|props/flora/bush_tempe_underbrush.xml",
                        },
                        0.3),
                    new BiomeVariant(
                        new[] { "alpine_cliff_c" },
                        new[] { "actor|props/flora/grass_tufts_a.xml", "actor|geology/highland2_moss.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_forrestfloor" },
                        new[]
                        {
                            "gaia/tree/pine",
                            "gaia/tree/pine",
                            "gaia/tree/pine",
                            "gaia/tree/pine",
                            "actor|geology/highland2_moss.xml",
                            "actor|props/flora/bush_highlands.xml",
                        },
                        0.5),
                    new BiomeVariant(
                        new[] { "alpine_cliff_c" },
                        new[] { "actor|geology/highland2_moss.xml", "actor|geology/stone_granite_med.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_forrestfloor_snow", "new_alpine_grass_dirt_a" },
                        new[] { "gaia/tree/pine", "actor|geology/snow1.xml" },
                        0.3),
                    new BiomeVariant(
                        new[] { "alpine_cliff_b" },
                        new[] { "actor|geology/stone_granite_med.xml", "actor|geology/snow1.xml" },
                        0.1)),
                new HeightBiomeBand(
                    new BiomeVariant(
                        new[] { "alpine_cliff_a", "alpine_cliff_snow" },
                        new[] { "actor|geology/highland1.xml" },
                        0.05),
                    new BiomeVariant(
                        new[] { "alpine_cliff_c" },
                        new[] { "actor|geology/highland1.xml" },
                        0.0)),
            };
        }

        private static void PlaceMine(RmgenRng rng, RandomMap map,
            RmgenVector2D point, string centerEntity)
        {
            map.PlaceEntityPassable(centerEntity, 0, point, rng.RandomAngle());
            int quantity = rng.RandIntInclusive(11, 23);
            double dAngle = 2 * SafeMath.PI / quantity;

            for (int i = 0; i < quantity; ++i)
            {
                string template = rng.PickRandom(decorations);
                double dist = rng.RandFloat(2, 5);
                double angle = dAngle * rng.RandFloat(i, i + 1);
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                map.PlaceEntityPassable(template, 0, RmgenVector2D.Add(point, offset), rng.RandomAngle());
            }
        }

        private static void PlaceGrove(RmgenRng rng, RandomMap map, RmgenVector2D point)
        {
            map.PlaceEntityPassable(rng.PickRandom(oGroveEntities), 0, point, rng.RandomAngle());
            int quantity = rng.RandIntInclusive(20, 30);
            double dAngle = 2 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                double angle = dAngle * rng.RandFloat(i, i + 1);
                double dist = rng.RandFloat(2, 5);
                var objectList = i % 3 == 0 ? groveActors : groveEntities;
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                var position = RmgenVector2D.Add(point, offset);
                map.PlaceEntityPassable(rng.PickRandom(objectList), 0, position, rng.RandomAngle());
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, 5, 1, 1, double.PositiveInfinity, position),
                    new TerrainPainter(tGrove, rng),
                    null);
            }
        }

        private static void PlaceCamp(RmgenRng rng, RandomMap map, RmgenVector2D point)
        {
            const string centerEntity = "actor|props/special/eyecandy/campfire.xml";
            map.PlaceEntityPassable(centerEntity, 0, point, rng.RandomAngle());
            int quantity = rng.RandIntInclusive(5, 11);
            double dAngle = 2 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                double angle = dAngle * rng.RandFloat(i, i + 1);
                double dist = rng.RandFloat(1, 3);
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                map.PlaceEntityPassable(rng.PickRandom(campEntities), 0,
                    RmgenVector2D.Add(point, offset), rng.RandomAngle());
            }
        }

        private static void PlaceStartLocationResources(RmgenRng rng, RandomMap map,
            RmgenVector2D point)
        {
            double currentAngle = rng.RandomAngle();
            double dAngle = 4.0 / 9 * SafeMath.PI;
            double angle = currentAngle + rng.RandFloat(1, 3) * dAngle / 4;
            var stoneOffset = new RmgenVector2D(12, 0);
            stoneOffset.Rotate(-angle);
            PlaceMine(rng, map, RmgenVector2D.Add(point, stoneOffset), "gaia/rock/temperate_large");
            currentAngle += dAngle;

            int quantity = 80;
            dAngle = 2 * SafeMath.PI / quantity / 3;
            for (int i = 0; i < quantity; ++i)
            {
                angle = currentAngle + rng.RandFloat(0, dAngle);
                var objectList = i % 2 == 0 ? groveActors : groveEntities;
                var woodOffset = new RmgenVector2D(rng.RandFloat(10, 15), 0);
                woodOffset.Rotate(-angle);
                var woodPosition = RmgenVector2D.Add(point, woodOffset);
                map.PlaceEntityPassable(rng.PickRandom(objectList), 0, woodPosition, rng.RandomAngle());
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, 5, 1, 1, double.PositiveInfinity, woodPosition),
                    new TerrainPainter(tGrove, rng),
                    null);
                currentAngle += dAngle;
            }

            dAngle = 2 * SafeMath.PI * 2 / 9;
            angle = currentAngle + dAngle * rng.RandFloat(1, 3) / 4;
            var metalOffset = new RmgenVector2D(13, 0);
            metalOffset.Rotate(-angle);
            PlaceMine(rng, map, RmgenVector2D.Add(point, metalOffset), "gaia/ore/temperate_large");
            currentAngle += dAngle;

            quantity = 15;
            dAngle = 2 * SafeMath.PI / quantity * 2 / 9;
            for (int i = 0; i < quantity; ++i)
            {
                angle = currentAngle + rng.RandFloat(0, dAngle);
                var berriesOffset = new RmgenVector2D(rng.RandFloat(10, 15), 0);
                berriesOffset.Rotate(-angle);
                var berriesPosition = RmgenVector2D.Add(point, berriesOffset);
                map.PlaceEntityPassable(rng.PickRandom(foodEntities), 0,
                    berriesPosition, rng.RandomAngle());
                currentAngle += dAngle;
            }
        }

        private static List<HeightmapLib.HeightPoint> GetPointsByHeightAvoidClass(RmgenRng rng,
            RandomMap map, double minHeight, double maxHeight,
            List<HeightmapLib.HeightPoint> avoidPoints, TileClass avoidClass,
            double minDistance, int maxTries, bool isCircular)
        {
            var points = new List<HeightmapLib.HeightPoint>();
            var placements = new List<HeightmapLib.HeightPoint>(avoidPoints);
            var validVertices = new List<HeightmapLib.HeightPoint>();
            double r = 0.5 * (map.Height.Length - 1);

            for (int x = (int)minDistance; x < map.Height.Length - minDistance; ++x)
                for (int y = (int)minDistance; y < map.Height[x].Length - minDistance; ++y)
                {
                    if (avoidClass.Has(new RmgenVector2D(Math.Max(x - 1, 0), y)) ||
                        avoidClass.Has(new RmgenVector2D(x, Math.Max(y - 1, 0))) ||
                        avoidClass.Has(new RmgenVector2D(Math.Min(x + 1, avoidClass.Size - 1), y)) ||
                        avoidClass.Has(new RmgenVector2D(x, Math.Min(y + 1, avoidClass.Size - 1))))
                        continue;

                    if (map.Height[x][y] > minHeight && map.Height[x][y] < maxHeight &&
                        (!isCircular || r - SafeMath.EuclidDistance2D(x, y, r, r) >= minDistance))
                        validVertices.Add(new HeightmapLib.HeightPoint(x, y, minDistance));
                }

            for (int tries = 0; tries < maxTries; ++tries)
            {
                if (validVertices.Count == 0)
                    break;
                var point = rng.PickRandom(validVertices);
                bool ok = true;
                foreach (var p in placements)
                    if (SafeMath.EuclidDistance2D(p.X, p.Y, point.X, point.Y) <=
                        Math.Max(minDistance, p.Dist))
                    {
                        ok = false;
                        break;
                    }

                if (ok)
                {
                    points.Add(point);
                    placements.Add(point);
                }
            }

            return points;
        }

        private static Dictionary<string, WallBuilder.WallElement> CaledonianOtherStyle()
            => new()
            {
                ["fence"] = new WallBuilder.WallElement("structures/fence_long",
                    SafeMath.PI / 2, 12.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0, 0),
                ["fence_short"] = new WallBuilder.WallElement("structures/fence_short",
                    SafeMath.PI / 2, 6.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0, 0),
                ["bench"] = new WallBuilder.WallElement("structures/bench", SafeMath.PI / 2, 1.5, 0, 0),
                ["sheep"] = new WallBuilder.WallElement("gaia/fauna_sheep", 0, 0, 0.75, 0),
                ["foodBin"] = new WallBuilder.WallElement("gaia/treasure/food_bin", SafeMath.PI / 2, 1.5, 0, 0),
                ["farmstead"] = new WallBuilder.WallElement("structures/brit/farmstead", SafeMath.PI, 0, -3, 0),
            };

        private static List<WallBuilder.Fortress> CreateCaledonianFences()
        {
            var fences = new List<WallBuilder.Fortress>
            {
                new("fence", new[]
                {
                    "foodBin", "farmstead", "bench",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "bench", "sheep", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "turn_0.5", "bench", "turn_-0.5", "fence_short",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence_short", "sheep", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "turn_0.5", "fence_short", "turn_-0.5", "bench",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence_short", "sheep", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "bench", "sheep", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence_short", "sheep", "fence",
                    "turn_0.25", "sheep", "turn_0.25", "fence_short", "sheep", "fence",
                }),
            };

            int count = fences.Count;
            for (int i = 0; i < count; ++i)
            {
                var reversed = new List<string>(fences[i].Wall);
                reversed.Reverse();
                fences.Add(new WallBuilder.Fortress("fence", reversed));
            }
            return fences;
        }

        private readonly struct BiomeVariant
        {
            public readonly IReadOnlyList<string> Texture;
            public readonly IReadOnlyList<string> Entity;
            public readonly double EntityProbability;

            public BiomeVariant(IReadOnlyList<string> texture, IReadOnlyList<string> entity,
                double entityProbability)
            {
                Texture = texture;
                Entity = entity;
                EntityProbability = entityProbability;
            }
        }

        private readonly struct HeightBiomeBand
        {
            public readonly BiomeVariant Flat;
            public readonly BiomeVariant Steep;

            public HeightBiomeBand(BiomeVariant flat, BiomeVariant steep)
            {
                Flat = flat;
                Steep = steep;
            }
        }
    }
}
