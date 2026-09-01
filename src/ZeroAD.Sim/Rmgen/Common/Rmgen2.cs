using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>命名 TileClass 集合(逐字移植 rmgen2/setup.js 的 g_TileClasses/initTileClasses)。
    /// 原版 23 个默认类 + 地图自定义追加类(如 ambush 的 bluffsPassage/nomadArea)。</summary>
    public sealed class TileClassSet
    {
        /// <summary>g_DefaultTileClasses(setup.js)。</summary>
        public static readonly string[] DefaultNames =
        {
            "animals", "baseResource", "berries", "bluff", "bluffIgnore", "dirt", "fish", "food",
            "forest", "hill", "land", "map", "metal", "mountain", "plateau", "player", "prop",
            "ramp", "rock", "settlement", "spine", "valley", "water",
        };

        private readonly Dictionary<string, TileClass> _classes = new(StringComparer.Ordinal);

        public TileClassSet(int mapSize, IEnumerable<string>? extra = null)
        {
            foreach (var name in DefaultNames)
                _classes[name] = new TileClass(mapSize);
            if (extra != null)
                foreach (var name in extra)
                    if (!_classes.ContainsKey(name))
                        _classes[name] = new TileClass(mapSize);
        }

        public TileClass this[string name] => _classes.TryGetValue(name, out var tc)
            ? tc
            : throw new KeyNotFoundException(
                $"TileClass '{name}' 未初始化——在 TileClassSet 构造时的 extra 列表里加上它");
    }

    /// <summary>rmgen2/setup.js 的 addElements 声明式管线("size/mix/amount" 语义词
    /// → 数值系数,再统一调 add* 函数)。</summary>
    public static class Rmgen2Setup
    {
        private static readonly Dictionary<string, double> s_amounts = new()
        {
            ["scarce"] = 0.2, ["few"] = 0.5, ["normal"] = 1, ["many"] = 1.75, ["tons"] = 3,
        };
        private static readonly Dictionary<string, double> s_mixes = new()
        {
            ["same"] = 0, ["similar"] = 0.1, ["normal"] = 0.25, ["varied"] = 0.5, ["unique"] = 0.75,
        };
        private static readonly Dictionary<string, double> s_sizes = new()
        {
            ["tiny"] = 0.5, ["small"] = 0.75, ["normal"] = 1, ["big"] = 1.25, ["huge"] = 1.5,
        };

        public static readonly string[] AllAmounts = { "scarce", "few", "normal", "many", "tons" };
        public static readonly string[] AllMixes = { "same", "similar", "normal", "varied", "unique" };
        public static readonly string[] AllSizes = { "tiny", "small", "normal", "big", "huge" };

        public static double PickAmount(RmgenRng rng, IReadOnlyList<string> amounts)
            => s_amounts.TryGetValue(rng.PickRandom(amounts), out var v) ? v : s_amounts["normal"];
        public static double PickMix(RmgenRng rng, IReadOnlyList<string> mixes)
            => s_mixes.TryGetValue(rng.PickRandom(mixes), out var v) ? v : s_mixes["normal"];
        public static double PickSize(RmgenRng rng, IReadOnlyList<string> sizes)
            => s_sizes.TryGetValue(rng.PickRandom(sizes), out var v) ? v : s_sizes["normal"];

        /// <summary>(constraint, size, deviation/mix, fill/amount, baseHeight) → void。
        /// baseHeight 只有 addBluffs/addValleys 用,其余函数照单收下忽略(同 JS 多余实参无害)。</summary>
        public delegate void ElementFunc(IConstraint constraint, double size, double deviation,
            double fill, double baseHeight);

        /// <summary>addElements 的元素描述(原版字面量对象)。</summary>
        public sealed class GaiaElement
        {
            public required ElementFunc Func;
            public object[] Avoid = Array.Empty<object>();
            public object[]? Stay;
            public string[] Sizes = AllSizes;
            public string[] Mixes = AllMixes;
            public string[] Amounts = AllAmounts;
            public double BaseHeight;
        }

        public static void AddElements(RmgenRng rng, IEnumerable<GaiaElement> elements)
        {
            foreach (var e in elements)
            {
                var constraint = new AndConstraint(
                    RmgenLibrary.AvoidClasses(e.Avoid),
                    RmgenLibrary.StayClasses(e.Stay ?? Array.Empty<object>()));
                double size = PickSize(rng, e.Sizes);
                double mix = PickMix(rng, e.Mixes);
                double amount = PickAmount(rng, e.Amounts);
                e.Func(constraint, size, mix, amount, e.BaseHeight);
            }
        }

        /// <summary>shuffleArray 等价(原版多处 addElements(shuffleArray([...]))——
        /// 决定同优先级资源的摆放顺序,影响谁先抢到好位置)。</summary>
        public static List<T> Shuffle<T>(RmgenRng rng, IReadOnlyList<T> source)
            => RmgenCommon.ShuffleArray(rng, source);

        /// <summary>createBase(rmgen2/setup.js)——单玩家基地。注意:上游 placePlayerBases 内部
        /// 用 g_MapSettings 算总玩家数循环下发,不能对"只含 1 个玩家"的临时数组单独调用
        /// (numPlayers 与数组长度会对不上导致越界)——因此本函数只是 <see cref="CreateBases"/>
        /// 的单玩家特例,内部走同一条整批 PlacePlayerBases 路径。</summary>
        public static void CreateBase(RmgenRng rng, RandomMap map, MapSettings settings,
            TileClassSet tc, BiomeSet biome, string biomeName, int playerId, RmgenVector2D position)
            => CreateBasesInternal(rng, map, settings, tc, biome, biomeName,
                new[] { playerId }, new[] { position }, 1);

        /// <summary>createBases(rmgen2/setup.js)——按位置数组批量建所有基地
        /// (一次性调用 PlacePlayerBases,而非逐玩家单独调用——避免其内部
        /// GetNumPlayers(settings) 与传入数组长度不一致导致的越界)。</summary>
        public static void CreateBases(RmgenRng rng, RandomMap map, MapSettings settings,
            TileClassSet tc, BiomeSet biome, string biomeName, IReadOnlyList<int> playerIDs,
            IReadOnlyList<RmgenVector2D> playerPosition)
            => CreateBasesInternal(rng, map, settings, tc, biome, biomeName, playerIDs, playerPosition,
                RmgenCommon.GetNumPlayers(settings));

        private static void CreateBasesInternal(RmgenRng rng, RandomMap map, MapSettings settings,
            TileClassSet tc, BiomeSet biome, string biomeName, IReadOnlyList<int> playerIDs,
            IReadOnlyList<RmgenVector2D> playerPosition, int numPlayersOverride)
        {
            int treesCount = biomeName == "generic/savanna" ? 5 : 15;
            // PlacePlayerBases 内部按 GetNumPlayers(settings) 循环;当调用方只想放一部分玩家
            // (如 CreateBase 单玩家特例)时,借一个只含目标玩家数的临时 MapSettings 让循环次数
            // 与传入数组长度一致,同时保留原 settings 的其余字段(civ/team 查询用)。
            var effectiveSettings = numPlayersOverride == RmgenCommon.GetNumPlayers(settings)
                ? settings
                : SliceSettings(settings, playerIDs);

            RmgenCommon.PlacePlayerBases(rng, map, effectiveSettings, biome.MainTerrain0, tc["player"], biome,
                playerPosition,
                cityPatchOuterTerrain: biome.RoadWild, cityPatchInnerTerrain: biome.Road,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = tc["baseResource"],
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(tc["water"], 0, tc["mountain"], 0),
                    StartingAnimal = true,
                    StartingAnimalTemplate = biome.StartingAnimal,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = treesCount,
                    DecorativesTemplate = biome.GrassShort,
                });
        }

        /// <summary>构造一个"玩家数等于 playerIDs.Count"的 MapSettings 副本(PlayerData[0]=gaia 保留,
        /// 按 playerIDs 顺序取原 settings 对应 civ/team)——只用于批量接口之外的单玩家调用场景。</summary>
        private static MapSettings SliceSettings(MapSettings settings, IReadOnlyList<int> playerIDs)
        {
            var sliced = new MapSettings
            {
                Size = settings.Size, Seed = settings.Seed, CircularMap = settings.CircularMap,
                DataRoot = settings.DataRoot, BiomeData = settings.BiomeData, Nomad = settings.Nomad,
                PlayerPlacement = settings.PlayerPlacement,
            };
            sliced.PlayerData.Add(settings.PlayerData.Count > 0 ? settings.PlayerData[0] : new PlayerData { Civ = "gaia" });
            foreach (int id in playerIDs)
                sliced.PlayerData.Add(id < settings.PlayerData.Count ? settings.PlayerData[id] : new PlayerData());
            return sliced;
        }
    }

    /// <summary>rmgen2/gaia.js 逐字移植——高层地形/资源生成函数集,配合 Rmgen2Setup.AddElements
    /// 声明式管线使用。每个地图脚本(ambush/bahrain/empire/...)持有一个实例,
    /// 用自己的 rng/map/biome/tileClasses 驱动。</summary>
    public sealed class Rmgen2Gaia
    {
        private readonly RmgenRng _rng;
        private readonly RandomMap _map;
        private readonly BiomeSet _biome;
        private readonly string _biomeName;
        private readonly TileClassSet _tc;
        private readonly MapSettings _settings;

        private static readonly Dictionary<string, string> s_props = new()
        {
            ["barrels"] = "actor|props/special/eyecandy/barrels_buried.xml",
            ["crate"] = "actor|props/special/eyecandy/crate_a.xml",
            ["cart"] = "actor|props/special/eyecandy/handcart_1_broken.xml",
            ["well"] = "actor|props/special/eyecandy/well_1_c.xml",
            ["skeleton"] = "actor|props/special/eyecandy/skeleton.xml",
        };

        public Rmgen2Gaia(RmgenRng rng, RandomMap map, BiomeSet biome, string biomeName, TileClassSet tc,
            MapSettings settings)
        { _rng = rng; _map = map; _biome = biome; _biomeName = biomeName; _tc = tc; _settings = settings; }

        private static double GetRandomDeviation(double baseValue, double deviation, RmgenRng rng)
            => baseValue + rng.RandFloat(-1, 1) * Math.Min(baseValue, deviation);
        private double GetRandomDeviation(double baseValue, double deviation)
            => GetRandomDeviation(baseValue, deviation, _rng);

        private static RmgenVector2D Average(RmgenVector2D a, RmgenVector2D b)
            => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        // ══════════ markPlayerAvoidanceArea / createBluffsPassages ══════════

        /// <summary>防止 CC 周围出现循环 bluff 图案(markPlayerAvoidanceArea)。</summary>
        public void MarkPlayerAvoidanceArea(IReadOnlyList<RmgenVector2D> playerPosition, double radius)
        {
            int mapSize = _map.GetSize();
            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ChainPlacer(_rng, 3, 6, RmgenLibrary.ScaleByMapSize(25, 60, mapSize),
                        double.PositiveInfinity, position, radius),
                    new TileClassPainter(_tc["bluffIgnore"]), null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(_tc["bluffIgnore"]),
                new NearTileClassConstraint(_tc["baseResource"], 5));
        }

        /// <summary>玩家基地→地图中心方向铲出可通行斜坡(createBluffsPassages)。</summary>
        public void CreateBluffsPassages(IReadOnlyList<RmgenVector2D> playerPosition)
        {
            foreach (var position in playerPosition)
            {
                for (int tryCount = 0; tryCount < 80; ++tryCount)
                {
                    double angle = position.AngleTo(_map.GetCenter()) + _rng.RandFloat(-1, 1) * SafeMath.PI / 2;

                    var v1 = new RmgenVector2D(RmgenCommon.DefaultPlayerBaseRadius(_map.GetSize()) * 0.7, 0);
                    v1.Rotate(angle);
                    var start = RmgenVector2D.Add(position, v1.Perpendicular());
                    start.Round();

                    var v2 = new RmgenVector2D(
                        RmgenCommon.DefaultPlayerBaseRadius(_map.GetSize()) * _rng.RandFloat(1.7, 2), 0);
                    v2.Rotate(angle);
                    var end = RmgenVector2D.Add(position, v2.Perpendicular());
                    end.Round();

                    if (_tc["forest"].Has(end) || !RmgenLibrary.StayClasses(_tc["bluff"], 12).Allows(end))
                        continue;

                    var startF = start; startF.Floor();
                    var endF = end; endF.Floor();
                    if ((_map.GetHeight(endF) - _map.GetHeight(startF)) / start.DistanceTo(end) > 1.5)
                        continue;

                    var area = Rmgen2Gaia.CreatePassage(_map, _rng, start, end,
                        RmgenLibrary.ScaleByMapSize(10, 20, _map.GetSize()),
                        RmgenLibrary.ScaleByMapSize(10, 14, _map.GetSize()),
                        3, terrain: _biome.MainTerrain, tileClass: _tc["bluffsPassage"]);

                    foreach (var point in area.GetPoints())
                        _map.DeleteTerrainEntity(point);

                    RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                        new TerrainPainter(TerrainFactory.CreateTerrain(_biome.Cliff), _rng),
                        new AndConstraint(new StayAreasConstraint(new[] { area }),
                            new SlopeConstraint(_map, 2, double.PositiveInfinity)));

                    break;
                }
            }
        }

        /// <summary>createPassage(gaia_terrain.js 逐字移植)——两点间平滑可通行斜坡。</summary>
        public static Area CreatePassage(RandomMap map, RmgenRng rng, RmgenVector2D start, RmgenVector2D end,
            double startWidth, double endWidth, double smoothWidth,
            IConstraint? constraints = null, double? startHeight = null, double? endHeight = null,
            TileClass? tileClass = null, object? terrain = null, object? edgeTerrain = null)
        {
            double Bound(double x) => Math.Max(0, Math.Min(Math.Round(x), map.GetSize()));
            var startB = new RmgenVector2D(Bound(start.X), Bound(start.Y));
            var endB = new RmgenVector2D(Bound(end.X), Bound(end.Y));
            double sh = startHeight ?? map.GetHeight(startB);
            double eh = endHeight ?? map.GetHeight(endB);

            var passageVec = RmgenVector2D.Sub(end, start);
            var widthDirection = passageVec.Perpendicular();
            widthDirection.Normalize();
            double lengthStep = 1.0 / (2 * passageVec.Length());
            var points = new List<RmgenVector2D>();
            var staticConstraint = constraints != null ? new StaticConstraint(map, constraints) : null;

            for (double lengthFraction = 0; lengthFraction <= 1; lengthFraction += lengthStep)
            {
                var locationLength = RmgenVector2D.Add(start, RmgenVector2D.Mult(passageVec, lengthFraction));
                double halfPassageWidth = (startWidth + (endWidth - startWidth) * lengthFraction) / 2;
                double passageHeight = sh + (eh - sh) * lengthFraction;

                for (double stepWidth = -halfPassageWidth; stepWidth <= halfPassageWidth; stepWidth += 0.5)
                {
                    var location = RmgenVector2D.Add(locationLength, RmgenVector2D.Mult(widthDirection, stepWidth));
                    location.Round();

                    if (!map.InMapBounds(location) || (staticConstraint != null && !staticConstraint.Allows(location)))
                        continue;

                    points.Add(location);

                    double smoothDistance = smoothWidth + Math.Abs(stepWidth) - halfPassageWidth;
                    double newHeight = smoothDistance > 0
                        ? (map.GetHeight(location) * smoothDistance + passageHeight / smoothDistance)
                            / (smoothDistance + 1 / smoothDistance)
                        : passageHeight;
                    map.SetHeight(location, newHeight);

                    tileClass?.Add(location);

                    if (edgeTerrain != null && smoothDistance > 0)
                        TerrainFactory.CreateTerrain(edgeTerrain).Place(map, rng, location);
                    else if (terrain != null)
                        TerrainFactory.CreateTerrain(terrain).Place(map, rng, location);
                }
            }
            return new Area(map, points);
        }

        // ══════════ addBluffs ══════════

        public void AddBluffs(IConstraint constraint, double size, double deviation, double fill, double baseHeight)
        {
            const double elevation = 30;
            const double margin = 0.08;

            object contrastTerrain = _biome.Tier2Terrain;
            if (_biomeName == "generic/india") contrastTerrain = _biome.Dirt;
            if (_biomeName == "generic/autumn") contrastTerrain = _biome.Tier3Terrain;

            for (int i = 0; i < fill * 15; ++i)
            {
                double bluffDeviation = GetRandomDeviation(size, deviation);
                var areasBluff = RmgenLibrary.CreateAreas(_rng,
                    new ChainPlacer(_rng, 5 * bluffDeviation, 7 * bluffDeviation, 100 * bluffDeviation, 0.5),
                    Array.Empty<IPainter>(), constraint, 1);
                if (areasBluff.Count == 0 || areasBluff[0].PointCount == 0) continue;

                int angle = _rng.RandIntInclusive(0, 3);
                int opposingAngle = (angle + 2) % 4;
                (RmgenVector2D start, RmgenVector2D end)? baseLine = null, endLine = null;
                int retries = 0;
                bool bluffPassable = false;
                while (!bluffPassable && retries++ < 4)
                {
                    baseLine = FindClearLine(areasBluff[0], angle);
                    endLine = FindClearLine(areasBluff[0], opposingAngle);
                    bluffPassable = IsBluffPassable(baseLine, endLine);
                    angle = (angle + 1) % 4;
                    opposingAngle = (angle + 2) % 4;
                }
                if (!bluffPassable || baseLine == null || endLine == null) continue;

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { _biome.MainTerrain, contrastTerrain },
                            new[] { 5.0 }, _rng),
                        new SmoothElevationPainter(_rng, SmoothElevationPainter.SmoothType.Blurry,
                            elevation * bluffDeviation, 2, relative: true),
                        new TileClassPainter(_tc["bluff"]),
                    },
                    new StayAreasConstraint(areasBluff));

                double slopeLength = (1 - margin) *
                    Average(baseLine.Value.start, baseLine.Value.end)
                        .DistanceTo(Average(endLine.Value.start, endLine.Value.end));

                foreach (var point in areasBluff[0].GetPoints())
                {
                    double dist = Math.Abs(RmgenGeometry.DistanceOfPointFromLine(
                        baseLine.Value.start, baseLine.Value.end, point));
                    _map.SetHeight(point,
                        Math.Max(_map.GetHeight(point) * (1 - dist / slopeLength) - 2, baseHeight));
                }

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new SmoothingPainter(1, 1, 1),
                        new TerrainPainter((object)_biome.MainTerrain, _rng),
                    },
                    new AdjacentToAreaConstraint(areasBluff));

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new TerrainPainter(TerrainFactory.CreateTerrain(_biome.Cliff), _rng),
                    new AndConstraint(new StayAreasConstraint(areasBluff),
                        new SlopeConstraint(_map, 2, double.PositiveInfinity)));

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new TileClassPainter(_tc["bluffIgnore"]),
                    new NearTileClassConstraint(_tc["bluff"], 8));
            }

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddHills,
                    Avoid = new object[] { _tc["hill"], 3, _tc["player"], 20, _tc["valley"], 2, _tc["water"], 2 },
                    Stay = new object[] { _tc["bluff"], 3 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            });

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddLayeredPatches,
                    Avoid = new object[] { _tc["dirt"], 5, _tc["forest"], 2, _tc["mountain"], 2,
                                           _tc["player"], 12, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddDecoration,
                    Avoid = new object[] { _tc["forest"], 2, _tc["player"], 12, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddProps,
                    Avoid = new object[] { _tc["forest"], 2, _tc["player"], 12, _tc["prop"], 40, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 7, _tc["mountain"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            Rmgen2Setup.AddElements(_rng, Rmgen2Setup.Shuffle(_rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = AddForests,
                    Avoid = new object[] { _tc["berries"], 5, _tc["forest"], 18, _tc["metal"], 5,
                                           _tc["mountain"], 5, _tc["player"], 20, _tc["rock"], 5, _tc["water"], 2 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes,
                    Amounts = new[] { "normal", "many", "tons" },
                },
                new()
                {
                    Func = AddMetal,
                    Avoid = new object[] { _tc["berries"], 5, _tc["forest"], 5, _tc["mountain"], 2,
                                           _tc["player"], 50, _tc["rock"], 15, _tc["metal"], 40, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new()
                {
                    Func = AddStone,
                    Avoid = new object[] { _tc["berries"], 5, _tc["forest"], 5, _tc["mountain"], 2,
                                           _tc["player"], 50, _tc["rock"], 40, _tc["metal"], 15, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            bool savanna = _biomeName == "generic/savanna";
            Rmgen2Setup.AddElements(_rng, Rmgen2Setup.Shuffle(_rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = AddStragglerTrees,
                    Avoid = new object[] { _tc["berries"], 5, _tc["forest"], 10, _tc["metal"], 5,
                                           _tc["mountain"], 1, _tc["player"], 12, _tc["rock"], 5, _tc["water"], 5 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = savanna ? new[] { "big" } : Rmgen2Setup.AllSizes,
                    Mixes = savanna ? new[] { "varied" } : Rmgen2Setup.AllMixes,
                    Amounts = savanna ? new[] { "tons" } : new[] { "normal", "many", "tons" },
                },
                new()
                {
                    Func = AddAnimals,
                    Avoid = new object[] { _tc["animals"], 20, _tc["forest"], 5, _tc["mountain"], 1,
                                           _tc["player"], 20, _tc["rock"], 5, _tc["metal"], 5, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes,
                    Amounts = new[] { "normal", "many", "tons" },
                },
                new()
                {
                    Func = AddBerries,
                    Avoid = new object[] { _tc["berries"], 50, _tc["forest"], 5, _tc["metal"], 10,
                                           _tc["mountain"], 2, _tc["player"], 20, _tc["rock"], 10, _tc["water"], 3 },
                    Stay = new object[] { _tc["bluff"], 6 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes,
                    Amounts = new[] { "normal", "many", "tons" },
                },
            }));
        }

        private bool IsBluffPassable((RmgenVector2D start, RmgenVector2D end)? baseLine,
            (RmgenVector2D start, RmgenVector2D end)? endLine)
        {
            if (baseLine == null || endLine == null) return false;
            if (!_map.ValidTilePassable(endLine.Value.start) && !_map.ValidTilePassable(endLine.Value.end))
                return false;
            return true;   // 详细逐块检查见 IsBluffPassableDetailed(area 版) —— 见下方重载
        }

        /// <summary>isBluffPassable(逐字移植)——bluffArea 逐行/逐列检查连通块尺寸。</summary>
        private bool IsBluffPassable(Area bluffArea,
            (RmgenVector2D start, RmgenVector2D end)? baseLine, (RmgenVector2D start, RmgenVector2D end)? endLine)
        {
            if (baseLine == null || endLine == null) return false;
            if (!_map.ValidTilePassable(endLine.Value.start) && !_map.ValidTilePassable(endLine.Value.end))
                return false;

            const int minTilesInGroup = 2;
            bool insideBluff = false, outsideBluff = false;
            var (min, max) = RmgenGeometry.GetBoundingBox(bluffArea.GetPoints());

            for (double x = min.X; x <= max.X; ++x)
            {
                int count = 0;
                for (double y = min.Y; y <= max.Y; ++y)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (!bluffArea.Contains(pos)) continue;
                    bool valid = _map.ValidTilePassable(pos);
                    if (valid) { ++count; insideBluff = true; }
                    if (outsideBluff && valid) return false;
                }
                if (insideBluff && count < minTilesInGroup) outsideBluff = true;
            }

            insideBluff = false; outsideBluff = false;
            for (double y = min.Y; y <= max.Y; ++y)
            {
                int count = 0;
                for (double x = min.X; x <= max.X; ++x)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (!bluffArea.Contains(pos)) continue;
                    var offsetPos = pos;
                    offsetPos.Add(min);
                    bool valid = _map.ValidTilePassable(offsetPos);
                    if (valid) { ++count; insideBluff = true; }
                    if (outsideBluff && valid) return false;
                }
                if (insideBluff && count < minTilesInGroup) outsideBluff = true;
            }
            return true;
        }

        private (RmgenVector2D start, RmgenVector2D end)? FindClearLine(Area bluffArea, int angle)
        {
            var (min, max) = RmgenGeometry.GetBoundingBox(bluffArea.GetPoints());
            RmgenVector2D offset;
            double y;
            switch (angle)
            {
                case 0: offset = new RmgenVector2D(-1, -1); y = max.Y; break;
                case 1: offset = new RmgenVector2D(1, -1); y = max.Y; break;
                case 2: offset = new RmgenVector2D(1, 1); y = min.Y; break;
                case 3: offset = new RmgenVector2D(-1, 1); y = min.Y; break;
                default: throw new ArgumentException("Unknown angle " + angle);
            }

            (RmgenVector2D start, RmgenVector2D end)? clearLine = null;
            for (double x = min.X; x <= max.X; ++x)
            {
                var start = new RmgenVector2D(x, y);
                bool intersectsBluff = false;
                var end = start;
                while (end.X >= min.X && end.X <= max.X && end.Y >= min.Y && end.Y <= max.Y)
                {
                    if (bluffArea.Contains(end) && _map.ValidTilePassable(end))
                    { intersectsBluff = true; break; }
                    end.Add(offset);
                }
                if (!intersectsBluff)
                {
                    var e2 = end;
                    e2.Sub(offset);
                    clearLine = (start, e2);
                }
                bool stop = intersectsBluff ? (angle == 0 || angle == 3) : (angle == 1 || angle == 2);
                if (stop) break;
            }
            return clearLine;
        }

        // ══════════ addDecoration ══════════

        public void AddDecoration(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double offset = GetRandomDeviation(size, deviation);
            var decorations = new IGroupElement[][]
            {
                new IGroupElement[] { new ScatterObject(_rng, _biome.RockMedium, offset, 3 * offset, 0, offset) },
                new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.RockLarge, offset, 2 * offset, 0, offset),
                    new ScatterObject(_rng, _biome.RockMedium, offset, 3 * offset, 0, 2 * offset),
                },
                new IGroupElement[] { new ScatterObject(_rng, _biome.GrassShort, offset, 2 * offset, 0, offset) },
                new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.Grass, 2 * offset, 4 * offset, 0, 1.8 * offset),
                    new ScatterObject(_rng, _biome.GrassShort, 3 * offset, 6 * offset, 1.2 * offset, 2.5 * offset),
                },
                new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.BushMedium, offset, 2 * offset, 0, 2 * offset),
                    new ScatterObject(_rng, _biome.BushSmall, 2 * offset, 4 * offset, 0, 2 * offset),
                },
            };

            double baseCount = _biomeName == "generic/india" ? 8 : 1;
            int mapSize = _map.GetSize();
            double[] counts =
            {
                RmgenLibrary.ScaleByMapSize(16, 262, mapSize),
                RmgenLibrary.ScaleByMapSize(8, 131, mapSize),
                baseCount * RmgenLibrary.ScaleByMapSize(13, 200, mapSize),
                baseCount * RmgenLibrary.ScaleByMapSize(13, 200, mapSize),
                baseCount * RmgenLibrary.ScaleByMapSize(13, 200, mapSize),
            };

            for (int i = 0; i < decorations.Length; ++i)
            {
                double decorCount = Math.Floor(counts[i] * fill);
                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(decorations[i], avoidSelf: true), 0, constraint, decorCount, 5);
            }
        }

        // ══════════ addElevation(内部)+ addHills/addLakes/addMountains/addPlateaus/addValleys ══════════

        private sealed class ElevationSpec
        {
            public required TileClass Class;
            public required object[] Painter;   // 长度 2:[边界地形, 中心地形]
            public double Size, Deviation, Fill;
            public double Count, MinSize, MaxSize, Spread, MinElevation, MaxElevation, Steepness;
        }

        private void AddElevation(IConstraint constraint, ElevationSpec el)
        {
            double count = el.Fill * el.Count;
            bool isWater = ReferenceEquals(el.Class, _tc["water"]);

            for (int i = 0; i < count; ++i)
            {
                double elevation = _rng.RandIntExclusive(el.MinElevation, el.MaxElevation);
                double smooth = Math.Floor(elevation / el.Steepness);

                double offset = GetRandomDeviation(el.Size, el.Deviation);
                double pMaxSize = Math.Floor(el.MaxSize * offset);
                double pSpread = Math.Floor(el.Spread * offset);
                double pSmooth = Math.Abs(Math.Floor(smooth * offset));
                double pElevation = Math.Floor(elevation * offset);

                pElevation = Math.Max(el.MinElevation, Math.Min(pElevation, el.MaxElevation));
                pMaxSize = Math.Min(pMaxSize, el.MaxSize);
                double pMinSize = Math.Max(pMaxSize, el.MinSize);
                pSmooth = Math.Max(pSmooth, 1);

                RmgenLibrary.CreateAreas(_rng,
                    new ChainPlacer(_rng, pMinSize, pMaxSize, pSpread, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(el.Painter, new[] { pSmooth }, _rng),
                        new SmoothElevationPainter(_rng,
                            isWater ? SmoothElevationPainter.SmoothType.Solid : SmoothElevationPainter.SmoothType.Blurry,
                            pElevation, pSmooth, relative: !isWater),
                        new TileClassPainter(el.Class),
                    },
                    constraint, 1);
            }
        }

        public void AddHills(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            AddElevation(constraint, new ElevationSpec
            {
                Class = _tc["hill"], Painter = new object[] { _biome.MainTerrain, _biome.MainTerrain },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 5, MaxSize = 8, Spread = 20, MinElevation = 6, MaxElevation = 12,
                Steepness = 1.5,
            });

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(_tc["bluffIgnore"]), new NearTileClassConstraint(_tc["hill"], 6));
        }

        public void AddLakes(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            object lakeTile = _biome.Water;
            if (_biomeName == "generic/temperate" || _biomeName == "generic/india") lakeTile = _biome.Dirt;
            if (_biomeName == "generic/aegean") lakeTile = _biome.Tier2Terrain;
            if (_biomeName == "generic/autumn") lakeTile = _biome.Shore;

            AddElevation(constraint, new ElevationSpec
            {
                Class = _tc["water"], Painter = new object[] { lakeTile, lakeTile },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 6, MinSize = 7, MaxSize = 9, Spread = 70, MinElevation = -15, MaxElevation = -2,
                Steepness = 1.5,
            });

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddFish,
                    Avoid = new object[] { _tc["fish"], 12, _tc["hill"], 8, _tc["mountain"], 8, _tc["player"], 8 },
                    Stay = new object[] { _tc["water"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes,
                    Amounts = new[] { "normal", "many", "tons" },
                },
            });

            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(_rng, _biome.RockMedium, 1, 3, 1, 3) },
                    avoidSelf: true, tileClass: _tc["dirt"]),
                0, new AndConstraint(RmgenLibrary.StayClasses(_tc["water"], 1),
                    RmgenLibrary.BorderClasses(_tc["water"], 4, 3)), 1000, 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.Reeds, 10, 15, 1, 3),
                    new ScatterObject(_rng, _biome.RockMedium, 1, 3, 1, 3),
                }, avoidSelf: true, tileClass: _tc["dirt"]),
                0, new AndConstraint(RmgenLibrary.StayClasses(_tc["water"], 2),
                    RmgenLibrary.BorderClasses(_tc["water"], 4, 3)), 1000, 100);
        }

        public void AddMountains(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            AddElevation(constraint, new ElevationSpec
            {
                Class = _tc["mountain"], Painter = new object[] { _biome.Cliff, _biome.Hill },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 2, MaxSize = 4, Spread = 100, MinElevation = 100, MaxElevation = 120,
                Steepness = 4,
            });
        }

        public void AddPlateaus(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            object plateauTile = _biome.Dirt;
            if (_biomeName == "generic/arctic") plateauTile = _biome.Tier1Terrain;
            if (_biomeName == "generic/alpine" || _biomeName == "generic/savanna") plateauTile = _biome.Tier2Terrain;
            if (_biomeName == "generic/autumn") plateauTile = _biome.Tier4Terrain;

            AddElevation(constraint, new ElevationSpec
            {
                Class = _tc["plateau"], Painter = new object[] { _biome.Cliff, plateauTile },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 15, MinSize = 2, MaxSize = 4, Spread = 200, MinElevation = 20, MaxElevation = 30,
                Steepness = 8,
            });

            for (int i = 0; i < 40; ++i)
            {
                double hillElevation = _rng.RandIntInclusive(4, 18);
                RmgenLibrary.CreateAreas(_rng,
                    new ChainPlacer(_rng, 3, 15, 1, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { plateauTile, plateauTile }, new[] { 3.0 }, _rng),
                        new SmoothElevationPainter(_rng, SmoothElevationPainter.SmoothType.Blurry,
                            hillElevation, hillElevation - 2, relative: true),
                        new TileClassPainter(_tc["hill"]),
                    },
                    new AndConstraint(RmgenLibrary.AvoidClasses(_tc["hill"], 7),
                        RmgenLibrary.StayClasses(_tc["plateau"], 7)),
                    1);
            }

            Rmgen2Setup.AddElements(_rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddDecoration,
                    Avoid = new object[] { _tc["dirt"], 15, _tc["forest"], 2, _tc["player"], 12, _tc["water"], 3 },
                    Stay = new object[] { _tc["plateau"], 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = AddProps,
                    Avoid = new object[] { _tc["forest"], 2, _tc["player"], 12, _tc["prop"], 40, _tc["water"], 3 },
                    Stay = new object[] { _tc["plateau"], 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
        }

        public void AddValleys(IConstraint constraint, double size, double deviation, double fill, double baseHeight)
        {
            if (baseHeight < 6) return;

            double minElevation = Math.Max(-baseHeight, 1 - baseHeight / (size * (deviation + 1)));

            object valleySlope = _biome.Tier1Terrain;
            object valleyFloor = _biome.Tier4Terrain;
            if (_biomeName == "generic/sahara") { valleySlope = _biome.Tier3Terrain; valleyFloor = _biome.Dirt; }
            if (_biomeName == "generic/aegean") { valleySlope = _biome.Tier2Terrain; valleyFloor = _biome.Dirt; }
            if (_biomeName == "generic/alpine" || _biomeName == "generic/savanna") valleyFloor = _biome.Tier2Terrain;
            if (_biomeName == "generic/india") valleySlope = _biome.Dirt;
            if (_biomeName == "generic/autumn") valleyFloor = _biome.Tier3Terrain;

            AddElevation(constraint, new ElevationSpec
            {
                Class = _tc["valley"], Painter = new object[] { valleySlope, valleyFloor },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 5, MaxSize = 8, Spread = 30, MinElevation = minElevation, MaxElevation = -2,
                Steepness = 4,
            });
        }

        // ══════════ 资源/装饰 ══════════

        public void AddAnimals(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double groupOffset = GetRandomDeviation(size, deviation);
            var animals = new IGroupElement[][]
            {
                new IGroupElement[] { new ScatterObject(_rng, _biome.MainHuntableAnimal,
                    5 * groupOffset, 7 * groupOffset, 0, 4 * groupOffset) },
                new IGroupElement[] { new ScatterObject(_rng, _biome.SecondaryHuntableAnimal,
                    2 * groupOffset, 3 * groupOffset, 0, 2 * groupOffset) },
            };
            foreach (var animal in animals)
                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(animal, avoidSelf: true, tileClass: _tc["animals"]),
                    0, constraint, Math.Floor(30 * fill), 50);
        }

        public void AddBerries(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double groupOffset = GetRandomDeviation(size, deviation);
            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.FruitBush, 5 * groupOffset, 5 * groupOffset, 0, 3 * groupOffset),
                }, avoidSelf: true, tileClass: _tc["berries"]),
                0, constraint, Math.Floor(50 * fill), 40);
        }

        public void AddFish(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double groupOffset = GetRandomDeviation(size, deviation);
            var fishes = new IGroupElement[][]
            {
                new IGroupElement[] { new ScatterObject(_rng, _biome.Fish, groupOffset, 2 * groupOffset, 0, 2 * groupOffset) },
                new IGroupElement[] { new ScatterObject(_rng, _biome.Fish, 2 * groupOffset, 4 * groupOffset,
                    10 * groupOffset, 20 * groupOffset) },
            };
            foreach (var fish in fishes)
                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(fish, avoidSelf: true, tileClass: _tc["fish"]),
                    0, constraint, Math.Floor(40 * fill), 50);
        }

        public void AddForests(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            if (_biomeName == "generic/savanna") return;

            var treeTypes = new object[][]
            {
                new object[]
                {
                    _biome.ForestFloor2 + TerrainFactory.TerrainSeparator + _biome.Tree1,
                    _biome.ForestFloor2 + TerrainFactory.TerrainSeparator + _biome.Tree2,
                    _biome.ForestFloor2,
                },
                new object[]
                {
                    _biome.ForestFloor1 + TerrainFactory.TerrainSeparator + _biome.Tree4,
                    _biome.ForestFloor1 + TerrainFactory.TerrainSeparator + _biome.Tree5,
                    _biome.ForestFloor1,
                },
            };

            var forestTypes = new object[][][]
            {
                new object[][]
                {
                    new object[] { _biome.ForestFloor2, _biome.MainTerrain, treeTypes[0] },
                    new object[] { _biome.ForestFloor2, treeTypes[0] },
                },
                new object[][]
                {
                    new object[] { _biome.ForestFloor2, _biome.MainTerrain, treeTypes[1] },
                    new object[] { _biome.ForestFloor1, treeTypes[1] },
                },
                new object[][]
                {
                    new object[] { _biome.ForestFloor1, _biome.MainTerrain, treeTypes[0] },
                    new object[] { _biome.ForestFloor2, treeTypes[0] },
                },
                new object[][]
                {
                    new object[] { _biome.ForestFloor1, _biome.MainTerrain, treeTypes[1] },
                    new object[] { _biome.ForestFloor1, treeTypes[1] },
                },
            };

            int mapSize = _map.GetSize();
            foreach (var forestType in forestTypes)
            {
                double offset = GetRandomDeviation(size, deviation);
                RmgenLibrary.CreateAreas(_rng,
                    new ChainPlacer(_rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize) * offset),
                        Math.Floor(50 * offset), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(forestType, new[] { 2.0 }, _rng),
                        new TileClassPainter(_tc["forest"]),
                    },
                    constraint, 10 * fill);
            }
        }

        public void AddMetal(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double offset = GetRandomDeviation(size, deviation);
            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.MetalLarge, offset, offset, 0, 4 * offset),
                }, avoidSelf: true, tileClass: _tc["metal"]),
                0, constraint, 1 + 20 * fill, 100);
        }

        public void AddSmallMetal(IConstraint constraint, double size, double mixes, double amounts, double baseHeight = 0)
        {
            double deviation = GetRandomDeviation(size, mixes);
            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.MetalSmall, 2 * deviation, 5 * deviation, deviation, 3 * deviation),
                }, avoidSelf: true, tileClass: _tc["metal"]),
                0, constraint, 1 + 20 * amounts, 100);
        }

        public void AddStone(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double offset = GetRandomDeviation(size, deviation);
            var mines = new IGroupElement[][]
            {
                new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.StoneSmall, 0, 2 * offset, 0, 4 * offset),
                    new ScatterObject(_rng, _biome.StoneLarge, offset, offset, 0, 4 * offset),
                },
                new IGroupElement[]
                {
                    new ScatterObject(_rng, _biome.StoneSmall, 2 * offset, 5 * offset, offset, 3 * offset),
                },
            };
            foreach (var mine in mines)
                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(mine, avoidSelf: true, tileClass: _tc["rock"]),
                    0, constraint, 1 + 20 * fill, 100);
        }

        public void AddStragglerTrees(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            if (_biomeName == "generic/savanna")
            {
                fill = Math.Max(fill, 2);
                size = Math.Max(size, 1);
            }

            var trees = new[] { _biome.Tree1, _biome.Tree2, _biome.Tree3, _biome.Tree4 };
            const double treesPerPlayer = 40;
            double playerBonus = Math.Max(1, (RmgenCommon.GetNumPlayers(_settings) - 3) / 2.0);

            double offset = GetRandomDeviation(size, deviation);
            double treeCount = treesPerPlayer * playerBonus * fill;
            double totalTrees = RmgenLibrary.ScaleByMapSize(treeCount, treeCount, _map.GetSize());

            double count = Math.Floor(totalTrees / trees.Length) * fill;
            double min = offset, max = 4 * offset, minDist = offset, maxDist = 5 * offset;

            if (_biomeName == "generic/savanna")
            {
                min = 3 * offset; max = 5 * offset; minDist = 2 * offset + 1; maxDist = 3 * offset + 2;
            }

            for (int i = 0; i < trees.Length; ++i)
            {
                double treesMax = max;
                if (i == 2 && (_biomeName == "generic/sahara" || _biomeName == "generic/aegean"))
                    treesMax = 1;
                double treesMin = Math.Min(min, treesMax);

                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(_rng, trees[i], treesMin, treesMax, minDist, maxDist),
                    }, avoidSelf: true, tileClass: _tc["forest"]),
                    0, constraint, count, 10);
            }
        }

        public void AddProps(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            double offset = GetRandomDeviation(size, deviation);
            var props = new IGroupElement[][]
            {
                new IGroupElement[]
                {
                    new ScatterObject(_rng, s_props["skeleton"], offset, 5 * offset, 0, 3 * offset + 2),
                },
                new IGroupElement[]
                {
                    new ScatterObject(_rng, s_props["barrels"], offset, 2 * offset, 2, 3 * offset + 2),
                    new ScatterObject(_rng, s_props["cart"], 0, offset, 5, 2.5 * offset + 5),
                    new ScatterObject(_rng, s_props["crate"], offset, 2 * offset, 2, 2 * offset + 2),
                    new ScatterObject(_rng, s_props["well"], 0, 1, 2, 2 * offset + 2),
                },
            };

            int mapSize = _map.GetSize();
            double[] counts =
            {
                RmgenLibrary.ScaleByMapSize(16, 262, mapSize),
                RmgenLibrary.ScaleByMapSize(8, 131, mapSize),
            };

            for (int i = 0; i < props.Length; ++i)
            {
                double propCount = Math.Floor(counts[i] * fill);
                RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                    new ObjectGroup(props[i], avoidSelf: true), 0, constraint, propCount, 5);
            }

            var trees = new ScatterObject(_rng, _biome.Tree, 5 * offset, 30 * offset, 2, 3 * offset + 10);
            RmgenLibrary.CreateObjectGroupsDeprecated(_rng,
                new ObjectGroup(new IGroupElement[] { trees }, avoidSelf: true),
                0, constraint, counts[0] * 5 * fill, 5);
        }

        public void AddLayeredPatches(IConstraint constraint, double size, double deviation, double fill, double baseHeight = 0)
        {
            int mapSize = _map.GetSize();
            double minRadius = 1;
            double maxRadius = Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize));
            double count = fill * RmgenLibrary.ScaleByMapSize(15, 45, mapSize);

            double[] patchSizes =
            {
                RmgenLibrary.ScaleByMapSize(3, 6, mapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, mapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, mapSize),
            };

            foreach (double patchSize in patchSizes)
            {
                double offset = GetRandomDeviation(size, deviation);
                double patchMinRadius = Math.Floor(minRadius * offset);
                double patchMaxRadius = Math.Floor(maxRadius * offset);

                RmgenLibrary.CreateAreas(_rng,
                    new ChainPlacer(_rng, Math.Min(patchMinRadius, patchMaxRadius), patchMaxRadius,
                        Math.Floor(patchSize * offset), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { _biome.MainTerrain, _biome.Tier1Terrain },
                            new object[] { _biome.Tier1Terrain, _biome.Tier2Terrain },
                            new object[] { _biome.Tier2Terrain, _biome.Tier3Terrain },
                            new object[] { _biome.Tier4Terrain },
                        }, new[] { 1.0, 1.0 }, _rng),
                        new TileClassPainter(_tc["dirt"]),
                    },
                    constraint, count * offset);
            }
        }
    }
}
