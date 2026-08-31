using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>rmgen2 系地图的基类——对应上游 `import ... from "maps/random/rmgen2/{gaia,setup}.js"`
    /// 的那一批脚本。流程固定为：
    ///   setBiome → new RandomMap(heightLand, mainTerrain) → initTileClasses →
    ///   全图刷 land → createBases(playerPlacementByPattern(...)) → 各图自己的 addElements 序列。
    ///
    /// 与 <see cref="StandardMap"/> 的区别：不走 mainland.js 那套 createBumps/createForests，
    /// 而是全用 rmgen2 的 add* 图元（<see cref="Rmgen2Context"/>）。
    /// placePlayersNomad 分支按本仓既有移植约定省略。</summary>
    public abstract class Rmgen2Map : StandardMap
    {
        protected Rmgen2Context Ctx = null!;

        private double _heightLand = 3;
        protected override double HeightLand => _heightLand;

        /// <summary>本局基准地面高度。上游多数图是常量，frontier 一类按 randIntInclusive 抽。</summary>
        protected virtual double PickHeightLand(RmgenRng rng) => 3;

        /// <summary>initTileClasses 的自定义类名（上游 initTileClasses(["bluffsPassage", ...])）。</summary>
        protected virtual IReadOnlyList<string>? ExtraTileClasses => null;

        /// <summary>基础地形——默认 biome.mainTerrain（名单，逐图块 pickRandom）。</summary>
        protected virtual IReadOnlyList<string> BaseTerrainList => Biome.MainTerrain;

        /// <summary>是否在开局把全图刷成 land class（上游绝大多数 rmgen2 图都刷）。</summary>
        protected virtual bool PaintLandClass => true;

        /// <summary>强制 biome 名（上游 setBiome("generic/sahara") 之类）——
        /// 非 null 时忽略 settings.BiomeData / SupportedBiomes 随机。</summary>
        protected virtual string? ForcedBiome => null;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            if (ForcedBiome != null)
            {
                BiomeName = ForcedBiome.Contains('/') ? ForcedBiome : "generic/" + ForcedBiome;
                Biome = BiomeLoader.Load(settings.DataRoot, ForcedBiome, rng);
            }
            else if (settings.BiomeData != null)
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

            OverrideBiome(Biome);

            _heightLand = PickHeightLand(rng);

            Map = new RandomMap(rng, MapSize, _heightLand, BaseTerrainList, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;

            Ctx = new Rmgen2Context(rng, Map, settings, Biome, BiomeName, ExtraTileClasses);

            if (PaintLandClass)
                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new TileClassPainter(Ctx.ClLand), null);

            GenerateRmgen2();

            return Map.MakeExportable();
        }

        /// <summary>就地改写 biome 地形/实体表（上游 bahrain 一类图在 setBiome 后直接
        /// 覆写 g_Terrains/g_Gaia/g_Decoratives 字段）。</summary>
        protected virtual void OverrideBiome(BiomeSet biome) { }

        /// <summary>本图的 rmgen2 生成流程（上游 generateMap 主体）。</summary>
        protected abstract void GenerateRmgen2();

        // ── 供子类使用的短名 ──

        /// <summary>createBases(playerPlacementByPattern(PlayerPlacement, distance, groupedDistance,
        /// randomAngle()), playerbaseTypes[PlayerPlacement].walls) 的一体化写法。
        /// 抽数顺序：distance → groupedDistance → angle → 布置 → 建基地（与上游实参求值序一致）。</summary>
        protected List<RmgenVector2D> CreateBasesByPattern(
            double distanceMinFraction, double distanceMaxFraction,
            double groupedMinFraction = 0.08, double groupedMaxFraction = 0.1)
        {
            double distance = RmgenLibrary.FractionToTiles(
                Rng.RandFloat(distanceMinFraction, distanceMaxFraction), MapSize);
            double groupedDistance = RmgenLibrary.FractionToTiles(
                Rng.RandFloat(groupedMinFraction, groupedMaxFraction), MapSize);
            double angle = Rng.RandomAngle();

            return CreateBasesAt(null, distance, groupedDistance, angle,
                Ctx.PlayerbaseWalls(Settings.PlayerPlacement));
        }

        /// <summary>固定模式版（empire/stronghold 强制 "stronghold"，距离/角度由图给定）。</summary>
        protected List<RmgenVector2D> CreateBasesAt(string? pattern, double distance,
            double groupedDistance, double angle, bool walls)
        {
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                Rng, Map, Settings, pattern, distance, groupedDistance, angle);

            Ctx.CreateBases(playerIDs, playerPosition, walls);
            return playerPosition;
        }

        /// <summary>shuffleArray——上游各图对 features 表洗牌后再 addElements。</summary>
        protected List<Rmgen2Context.Element> Shuffle(IReadOnlyList<Rmgen2Context.Element> elements)
            => RmgenCommon.ShuffleArray(Rng, elements);
    }
}
