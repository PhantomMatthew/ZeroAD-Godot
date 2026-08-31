using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>rmgen2/setup.js 的 C# 等价物——rmgen2 图共享的上下文。
    /// 上游 rmgen2 是一组吃全局状态（g_Map/g_TileClasses/g_Terrains/g_Gaia/g_Decoratives）的
    /// 自由函数；本版把这些全局收进实例字段，函数变实例方法（<see cref="Rmgen2Gaia"/> 部分类），
    /// 语义与抽数顺序逐行对齐上游。</summary>
    public sealed partial class Rmgen2Context
    {
        public readonly RmgenRng Rng;
        public readonly RandomMap Map;
        public readonly MapSettings Settings;
        public readonly BiomeSet Biome;

        /// <summary>本局 biome 全名（"generic/temperate" 等）——对应上游 currentBiome()。</summary>
        public readonly string BiomeName;

        public readonly int MapSize;
        public readonly RmgenVector2D MapCenter;

        private readonly Dictionary<string, TileClass> _tileClasses;

        public Rmgen2Context(RmgenRng rng, RandomMap map, MapSettings settings, BiomeSet biome,
            string biomeName, IEnumerable<string>? extraTileClasses = null)
        {
            Rng = rng;
            Map = map;
            Settings = settings;
            Biome = biome;
            BiomeName = biomeName;
            MapSize = map.GetSize();
            MapCenter = map.GetCenter();
            _tileClasses = new Dictionary<string, TileClass>(StringComparer.Ordinal);
            InitTileClasses(extraTileClasses);
        }

        private Rmgen2Context(Rmgen2Context source, BiomeSet biome, string biomeName)
        {
            Rng = source.Rng;
            Map = source.Map;
            Settings = source.Settings;
            Biome = biome;
            BiomeName = biomeName;
            MapSize = source.MapSize;
            MapCenter = source.MapCenter;
            _tileClasses = source._tileClasses;   // 共享同一批 tileclass
        }

        /// <summary>换 biome 但共享 tileclass 的上下文——对应上游在同一张图里
        /// 反复 setBiome(zone.biome)（mediterranean 的多气候区）。</summary>
        public Rmgen2Context WithBiome(BiomeSet biome, string biomeName)
            => new(this, biome, biomeName);

        // ── setup.js 的量词表 ──

        private static readonly Dictionary<string, double> s_amounts = new(StringComparer.Ordinal)
        {
            ["scarce"] = 0.2, ["few"] = 0.5, ["normal"] = 1, ["many"] = 1.75, ["tons"] = 3,
        };

        private static readonly Dictionary<string, double> s_mixes = new(StringComparer.Ordinal)
        {
            ["same"] = 0, ["similar"] = 0.1, ["normal"] = 0.25, ["varied"] = 0.5, ["unique"] = 0.75,
        };

        private static readonly Dictionary<string, double> s_sizes = new(StringComparer.Ordinal)
        {
            ["tiny"] = 0.5, ["small"] = 0.75, ["normal"] = 1, ["big"] = 1.25, ["huge"] = 1.5,
        };

        /// <summary>allAmounts——Object.keys(g_Amounts) 的插入序。</summary>
        public static readonly string[] AllAmounts = { "scarce", "few", "normal", "many", "tons" };

        /// <summary>allMixes。</summary>
        public static readonly string[] AllMixes = { "same", "similar", "normal", "varied", "unique" };

        /// <summary>allSizes。</summary>
        public static readonly string[] AllSizes = { "tiny", "small", "normal", "big", "huge" };

        /// <summary>setup.js g_DefaultTileClasses。</summary>
        public static readonly string[] DefaultTileClasses =
        {
            "animals", "baseResource", "berries", "bluff", "bluffIgnore", "dirt", "fish", "food",
            "forest", "hill", "land", "map", "metal", "mountain", "plateau", "player", "prop",
            "ramp", "rock", "settlement", "spine", "valley", "water",
        };

        /// <summary>initTileClasses——默认类 + 图自定义类。</summary>
        public void InitTileClasses(IEnumerable<string>? newClasses)
        {
            _tileClasses.Clear();
            foreach (string name in DefaultTileClasses)
                _tileClasses[name] = new TileClass(MapSize);
            if (newClasses != null)
                foreach (string name in newClasses)
                    _tileClasses[name] = new TileClass(MapSize);
        }

        /// <summary>g_TileClasses.&lt;name&gt;——未注册的名字即上游访问 undefined 属性，视为错误。</summary>
        public TileClass Cl(string name) => _tileClasses.TryGetValue(name, out var tc)
            ? tc
            : throw new ArgumentException($"g_TileClasses.{name} not initialized", nameof(name));

        public TileClass ClAnimals => Cl("animals");
        public TileClass ClBaseResource => Cl("baseResource");
        public TileClass ClBerries => Cl("berries");
        public TileClass ClBluff => Cl("bluff");
        public TileClass ClBluffIgnore => Cl("bluffIgnore");
        public TileClass ClDirt => Cl("dirt");
        public TileClass ClFish => Cl("fish");
        public TileClass ClFood => Cl("food");
        public TileClass ClForest => Cl("forest");
        public TileClass ClHill => Cl("hill");
        public TileClass ClLand => Cl("land");
        public TileClass ClMetal => Cl("metal");
        public TileClass ClMountain => Cl("mountain");
        public TileClass ClPlateau => Cl("plateau");
        public TileClass ClPlayer => Cl("player");
        public TileClass ClProp => Cl("prop");
        public TileClass ClRock => Cl("rock");
        public TileClass ClSettlement => Cl("settlement");
        public TileClass ClSpine => Cl("spine");
        public TileClass ClValley => Cl("valley");
        public TileClass ClWater => Cl("water");

        // ── playerbaseTypes（setup.js）──

        /// <summary>playerbaseTypes[patternName].walls——是否给伊比利亚发起始城墙。
        /// 上游同名对象还带 distance/groupedDistance 两个字段，但**没有任何地图读取它们**
        /// （各图自行 randFloat 出距离后传给 playerPlacementByPattern），故此处只移植 walls，
        /// 不复现那两个字段（它们在上游模块求值期调用 fractionToTiles，彼时 g_Map 尚不存在）。</summary>
        private static readonly Dictionary<string, bool> s_playerbaseWalls = new(StringComparer.Ordinal)
        {
            ["groupedLines"] = false,
            ["river"] = true,
            ["circle"] = true,
            ["randomGroup"] = true,
            ["stronghold"] = false,
        };

        /// <summary>playerbaseTypes[pattern].walls（未知模式按 circle 处理）。
        /// 上游还要求 g_Map.getSize() &gt; 192 才真发墙——已并入此处。</summary>
        public bool PlayerbaseWalls(string? pattern)
            => MapSize > 192 &&
                s_playerbaseWalls.TryGetValue(pattern ?? Settings.PlayerPlacement, out bool w) && w;

        // ── addElements ──

        /// <summary>addElements 的元素（对应上游对象字面量的一条）。</summary>
        public sealed class Element
        {
            /// <summary>func(constraint, size, mix, amount, baseHeight)。</summary>
            public Action<IConstraint, double, double, double, double> Func = null!;

            /// <summary>avoidClasses 的扁平实参表 [class, dist, ...]。</summary>
            public object[] Avoid = Array.Empty<object>();

            /// <summary>stayClasses 的扁平实参表 [class, dist, ...]（空 = 无 stay 约束）。</summary>
            public object[] Stay = Array.Empty<object>();

            public string[] Sizes = AllSizes;
            public string[] Mixes = AllMixes;
            public string[] Amounts = AllAmounts;
            public double BaseHeight;
        }

        /// <summary>addElements——逐条构约束 + 抽 size/mix/amount 后调用 func。
        /// 抽数顺序：pickSize → pickMix → pickAmount（与上游实参求值序一致）。</summary>
        public void AddElements(IEnumerable<Element> elements)
        {
            foreach (var element in elements)
            {
                // 上游 stayClasses.apply(null, null) → stayClasses() → AndConstraint([])，恒真
                var constraint = new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(element.Avoid),
                    RmgenLibrary.StayClasses(element.Stay),
                });

                element.Func(constraint,
                    PickSize(element.Sizes),
                    PickMix(element.Mixes),
                    PickAmount(element.Amounts),
                    element.BaseHeight);
            }
        }

        private double PickAmount(IReadOnlyList<string> amounts)
            => s_amounts.TryGetValue(Rng.PickRandom(amounts), out double v) ? v : s_amounts["normal"];

        private double PickMix(IReadOnlyList<string> mixes)
            => s_mixes.TryGetValue(Rng.PickRandom(mixes), out double v) ? v : s_mixes["normal"];

        private double PickSize(IReadOnlyList<string> sizes)
            => s_sizes.TryGetValue(Rng.PickRandom(sizes), out double v) ? v : s_sizes["normal"];

        /// <summary>getRandomDeviation(base, deviation)——base ± randFloat(-1,1)*min(base,deviation)。</summary>
        public double GetRandomDeviation(double baseValue, double deviation)
            => baseValue + Rng.RandFloat(-1, 1) * Math.Min(baseValue, deviation);

        // ── createBases ──

        /// <summary>createBases——按 playerPlacement 结果逐玩家 placePlayerBase。
        /// 注意：上游 walls 只对伊比利亚起始城墙生效，本仓 placePlayerBases 尚未移植起始城墙，
        /// 因此 walls 目前不产生可见差异（保留形参以对齐调用点与后续接线）。</summary>
        public void CreateBases(IReadOnlyList<int> playerIDs,
            IReadOnlyList<RmgenVector2D> playerPosition, bool walls)
        {
            _ = walls;
            RmgenCommon.PlacePlayerBases(Rng, Map, Settings, Biome.MainTerrain0, ClPlayer, Biome,
                playerPosition,
                cityPatchOuterTerrain: Biome.RoadWild, cityPatchInnerTerrain: Biome.Road,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    ExtraBaseResourceConstraint =
                        RmgenLibrary.AvoidClasses(ClWater, 0, ClMountain, 0),
                    StartingAnimal = true,
                    StartingAnimalTemplate = Biome.StartingAnimal,
                    BerriesTemplate = Biome.FruitBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (Biome.MetalLarge, null, null),
                        (Biome.StoneLarge, null, null),
                    },
                    TreesTemplate = Biome.Tree1,
                    TreesCount = BiomeName == "generic/savanna" ? 5 : 15,
                    DecorativesTemplate = Biome.GrassShort,
                });
        }

        // ── 常用短名（gaia.js 大量直用）──
        internal double ScaleByMapSize(double min, double max)
            => RmgenLibrary.ScaleByMapSize(min, max, MapSize);

        internal IConstraint Avoid(params object[] args) => RmgenLibrary.AvoidClasses(args);
        internal IConstraint Stay(params object[] args) => RmgenLibrary.StayClasses(args);
    }
}
