using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>rmgen2/gaia.js 的 C# 等价物（逐行移植）——地貌与资源的"通用图元层"。
    /// 全部函数签名统一为 (constraint, size, deviation, fill[, baseHeight])，
    /// 与 <see cref="Rmgen2Context.AddElements"/> 的调用协议一致。</summary>
    public sealed partial class Rmgen2Context
    {
        /// <summary>gaia.js g_Props。</summary>
        private static class Props
        {
            public const string Barrels = "actor|props/special/eyecandy/barrels_buried.xml";
            public const string Crate = "actor|props/special/eyecandy/crate_a.xml";
            public const string Cart = "actor|props/special/eyecandy/handcart_1_broken.xml";
            public const string Well = "actor|props/special/eyecandy/well_1_c.xml";
            public const string Skeleton = "actor|props/special/eyecandy/skeleton.xml";
        }

        private ScatterObject Obj(string template, double minCount, double maxCount,
            double minDistance, double maxDistance)
            => new(Rng, template, minCount, maxCount, minDistance, maxDistance);

        private ObjectGroup Group(bool avoidSelf, TileClass? tileClass, params IGroupElement[] elements)
            => new(elements, avoidSelf, tileClass);

        private int PlaceGroups(ICenteredObjectGroup group, IConstraint constraint,
            double amount, int retryFactor = 10)
            => RmgenLibrary.CreateObjectGroupsDeprecated(Rng, group, 0, constraint, amount, retryFactor);

        // ══════════ 玩家基地保护 ══════════

        /// <summary>markPlayerAvoidanceArea——在基地周围随机链状打 bluffIgnore 标记，
        /// 防止 bluff 在基地四周形成环。</summary>
        public void MarkPlayerAvoidanceArea(IReadOnlyList<RmgenVector2D> playerPosition, double radius)
        {
            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ChainPlacer(Rng, 3, 6, ScaleByMapSize(25, 60),
                        double.PositiveInfinity, position, radius),
                    new TileClassPainter(ClBluffIgnore),
                    null);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(ClBluffIgnore),
                new NearTileClassConstraint(ClBaseResource, 5));
        }

        /// <summary>createBluffsPassages——为每个基地朝图心方向凿一条可通行的斜坡通道，
        /// 避免 bluff 把基地整个圈死。</summary>
        public void CreateBluffsPassages(IReadOnlyList<RmgenVector2D> playerPosition)
        {
            var bluffsPassage = ClOrNull("bluffsPassage");
            double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);

            foreach (var position in playerPosition)
                for (int tryCount = 0; tryCount < 80; ++tryCount)
                {
                    double angle = position.AngleTo(MapCenter) +
                        Rng.RandFloat(-1, 1) * SafeMath.PI / 2;

                    var startOffset = new RmgenVector2D(baseRadius * 0.7, 0);
                    startOffset.Rotate(angle);
                    startOffset = startOffset.Perpendicular();
                    var start = RmgenVector2D.Add(position, startOffset);
                    start.Round();

                    var endOffset = new RmgenVector2D(baseRadius * Rng.RandFloat(1.7, 2), 0);
                    endOffset.Rotate(angle);
                    endOffset = endOffset.Perpendicular();
                    var end = RmgenVector2D.Add(position, endOffset);
                    end.Round();

                    if (ClForest.Has(end) || !Stay(ClBluff, 12).Allows(end))
                        continue;

                    var endFloored = end; endFloored.Floor();
                    var startFloored = start; startFloored.Floor();
                    if (!Map.ValidHeight(endFloored) || !Map.ValidHeight(startFloored))
                        continue;

                    if ((Map.GetHeight(endFloored) - Map.GetHeight(startFloored)) /
                        start.DistanceTo(end) > 1.5)
                        continue;

                    var area = RmgenCommon.CreatePassage(Rng, Map, start, end,
                        startWidth: ScaleByMapSize(10, 20),
                        endWidth: ScaleByMapSize(10, 14),
                        smoothWidth: 3,
                        terrain: Biome.MainTerrain,
                        tileClass: bluffsPassage);

                    if (area == null)
                        break;

                    foreach (var point in area.GetPoints())
                        Map.DeleteTerrainEntity(point);

                    RmgenLibrary.CreateArea(
                        new MapBoundsPlacer(),
                        new TerrainPainter(Biome.Cliff, Rng),
                        new AndConstraint(new IConstraint[]
                        {
                            new StayAreasConstraint(new[] { area }),
                            new SlopeConstraint(Map, 2, double.PositiveInfinity),
                        }));

                    break;
                }
        }

        // ══════════ addBluffs ══════════

        /// <summary>addBluffs——造"可从地面走上去的台地"，并在其上补林/矿/兽/饰。</summary>
        public void AddBluffs(IConstraint constraint, double size, double deviation, double fill,
            double baseHeight)
        {
            const double elevation = 30;
            const double margin = 0.08;   // 台地入口区占长度比

            object contrastTerrain = Biome.Tier2Terrain;
            if (BiomeName == "generic/india")
                contrastTerrain = Biome.Dirt;
            if (BiomeName == "generic/autumn")
                contrastTerrain = Biome.Tier3Terrain;

            for (int i = 0; i < fill * 15; ++i)
            {
                double bluffDeviation = GetRandomDeviation(size, deviation);

                var areasBluff = RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, 5 * bluffDeviation, 7 * bluffDeviation,
                        100 * bluffDeviation, 0.5),
                    Array.Empty<IPainter>(),
                    constraint,
                    1);

                if (areasBluff.Count == 0 || areasBluff[0].PointCount == 0)
                    continue;

                int angle = Rng.RandIntInclusive(0, 3);
                int opposingAngle = (angle + 2) % 4;

                (RmgenVector2D start, RmgenVector2D end)? baseLine = null;
                (RmgenVector2D start, RmgenVector2D end)? endLine = null;

                int retries = 0;
                bool bluffPassable = false;
                while (!bluffPassable && retries++ < 4)
                {
                    baseLine = FindClearLine(areasBluff[0], angle);
                    endLine = FindClearLine(areasBluff[0], opposingAngle);
                    bluffPassable = IsBluffPassable(areasBluff[0], baseLine, endLine);

                    angle = (angle + 1) % 4;
                    opposingAngle = (angle + 2) % 4;
                }

                if (!bluffPassable)
                    continue;

                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.MainTerrain, contrastTerrain },
                            new[] { 5 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Blurry,
                            elevation * bluffDeviation, 2, relative: true),
                        new TileClassPainter(ClBluff),
                    },
                    new StayAreasConstraint(areasBluff));

                double slopeLength = (1 - margin) *
                    Average(baseLine!.Value.start, baseLine.Value.end)
                        .DistanceTo(Average(endLine!.Value.start, endLine.Value.end));

                foreach (var point in areasBluff[0].GetPoints())
                {
                    double dist = Math.Abs(RmgenGeometry.DistanceOfPointFromLine(
                        baseLine.Value.start, baseLine.Value.end, point));
                    Map.SetHeight(point,
                        Math.Max(Map.GetHeight(point) * (1 - dist / slopeLength) - 2, baseHeight));
                }

                // 台地边缘外一圈抹平
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new SmoothingPainter(1, 1, 1),
                        new TerrainPainter(Biome.MainTerrain, Rng),
                    },
                    new AdjacentToAreaConstraint(Map, areasBluff));

                // 陡坡刷崖壁
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new TerrainPainter(Biome.Cliff, Rng),
                    new AndConstraint(new IConstraint[]
                    {
                        new StayAreasConstraint(areasBluff),
                        new SlopeConstraint(Map, 2, double.PositiveInfinity),
                    }));

                // 性能优化标记
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new TileClassPainter(ClBluffIgnore),
                    new NearTileClassConstraint(ClBluff, 8));
            }

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddHills(c, s, d, f),
                    Avoid = new object[] { ClHill, 3, ClPlayer, 20, ClValley, 2, ClWater, 2 },
                    Stay = new object[] { ClBluff, 3 },
                },
            });

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddLayeredPatches(c, s, d, f),
                    Avoid = new object[] { ClDirt, 5, ClForest, 2, ClMountain, 2, ClPlayer, 12, ClWater, 3 },
                    Stay = new object[] { ClBluff, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddDecoration(c, s, d, f),
                    Avoid = new object[] { ClForest, 2, ClPlayer, 12, ClWater, 3 },
                    Stay = new object[] { ClBluff, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddProps(c, s, d, f),
                    Avoid = new object[] { ClForest, 2, ClPlayer, 12, ClProp, 40, ClWater, 3 },
                    Stay = new object[] { ClBluff, 7, ClMountain, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            AddElements(RmgenCommon.ShuffleArray(Rng, new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddForests(c, s, d, f),
                    Avoid = new object[] { ClBerries, 5, ClForest, 18, ClMetal, 5, ClMountain, 5,
                        ClPlayer, 20, ClRock, 5, ClWater, 2 },
                    Stay = new object[] { ClBluff, 6 },
                    Amounts = new[] { "normal", "many", "tons" },
                },
                new Element
                {
                    Func = (c, s, d, f, _) => AddMetal(c, s, d, f),
                    Avoid = new object[] { ClBerries, 5, ClForest, 5, ClMountain, 2, ClPlayer, 50,
                        ClRock, 15, ClMetal, 40, ClWater, 3 },
                    Stay = new object[] { ClBluff, 6 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
                new Element
                {
                    Func = (c, s, d, f, _) => AddStone(c, s, d, f),
                    Avoid = new object[] { ClBerries, 5, ClForest, 5, ClMountain, 2, ClPlayer, 50,
                        ClRock, 40, ClMetal, 15, ClWater, 3 },
                    Stay = new object[] { ClBluff, 6 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            bool savanna = BiomeName == "generic/savanna";
            AddElements(RmgenCommon.ShuffleArray(Rng, new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddStragglerTrees(c, s, d, f),
                    Avoid = new object[] { ClBerries, 5, ClForest, 10, ClMetal, 5, ClMountain, 1,
                        ClPlayer, 12, ClRock, 5, ClWater, 5 },
                    Stay = new object[] { ClBluff, 6 },
                    Sizes = savanna ? new[] { "big" } : AllSizes,
                    Mixes = savanna ? new[] { "varied" } : AllMixes,
                    Amounts = savanna ? new[] { "tons" } : new[] { "normal", "many", "tons" },
                },
                new Element
                {
                    Func = (c, s, d, f, _) => AddAnimals(c, s, d, f),
                    Avoid = new object[] { ClAnimals, 20, ClForest, 5, ClMountain, 1, ClPlayer, 20,
                        ClRock, 5, ClMetal, 5, ClWater, 3 },
                    Stay = new object[] { ClBluff, 6 },
                    Amounts = new[] { "normal", "many", "tons" },
                },
                new Element
                {
                    Func = (c, s, d, f, _) => AddBerries(c, s, d, f),
                    Avoid = new object[] { ClBerries, 50, ClForest, 5, ClMetal, 10, ClMountain, 2,
                        ClPlayer, 20, ClRock, 10, ClWater, 3 },
                    Stay = new object[] { ClBluff, 6 },
                    Amounts = new[] { "normal", "many", "tons" },
                },
            }));
        }

        private static RmgenVector2D Average(RmgenVector2D a, RmgenVector2D b)
            => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        // ══════════ addDecoration ══════════

        /// <summary>addDecoration——石/草/灌木五组装饰。</summary>
        public void AddDecoration(IConstraint constraint, double size, double deviation, double fill)
        {
            double offset = GetRandomDeviation(size, deviation);

            var decorations = new[]
            {
                new IGroupElement[] { Obj(Biome.RockMedium, offset, 3 * offset, 0, offset) },
                new IGroupElement[]
                {
                    Obj(Biome.RockLarge, offset, 2 * offset, 0, offset),
                    Obj(Biome.RockMedium, offset, 3 * offset, 0, 2 * offset),
                },
                new IGroupElement[] { Obj(Biome.GrassShort, offset, 2 * offset, 0, offset) },
                new IGroupElement[]
                {
                    Obj(Biome.Grass, 2 * offset, 4 * offset, 0, 1.8 * offset),
                    Obj(Biome.GrassShort, 3 * offset, 6 * offset, 1.2 * offset, 2.5 * offset),
                },
                new IGroupElement[]
                {
                    Obj(Biome.BushMedium, offset, 2 * offset, 0, 2 * offset),
                    Obj(Biome.BushSmall, 2 * offset, 4 * offset, 0, 2 * offset),
                },
            };

            double baseCount = BiomeName == "generic/india" ? 8 : 1;

            var counts = new[]
            {
                ScaleByMapSize(16, 262),
                ScaleByMapSize(8, 131),
                baseCount * ScaleByMapSize(13, 200),
                baseCount * ScaleByMapSize(13, 200),
                baseCount * ScaleByMapSize(13, 200),
            };

            for (int i = 0; i < decorations.Length; ++i)
                PlaceGroups(new ObjectGroup(decorations[i], true), constraint,
                    Math.Floor(counts[i] * fill), 5);
        }

        // ══════════ addElevation 家族 ══════════

        private sealed class ElevationSpec
        {
            public TileClass Class = null!;
            public object[] Painter = Array.Empty<object>();
            public double Size, Deviation, Fill, Count, MinSize, MaxSize, Spread;
            public double MinElevation, MaxElevation, Steepness;
        }

        /// <summary>addElevation——rmgen2 所有起伏（丘陵/湖泊/山脉/台地/谷地）的公共实现。</summary>
        private void AddElevation(IConstraint constraint, ElevationSpec el)
        {
            double count = el.Fill * el.Count;

            // 上游 ELEVATION_SET（水面）/ ELEVATION_MODIFY（其余）
            bool relative = el.Class != ClWater;

            // 多层 painter 时，除最后一层外每层宽 1（留出岸线/崖壁）
            var widths = new List<double>();
            for (int s = el.Painter.Length; s > 2; --s)
                widths.Add(1);

            for (int i = 0; i < count; ++i)
            {
                int elevation = Rng.RandIntExclusive(el.MinElevation, el.MaxElevation);
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

                var layerWidths = widths.Concat(new[] { pSmooth }).ToArray();

                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, pMinSize, pMaxSize, pSpread, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(el.Painter, layerWidths, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Blurry,
                            pElevation, pSmooth, relative: relative),
                        new TileClassPainter(el.Class),
                    },
                    constraint,
                    1);
            }
        }

        /// <summary>addHills——缓丘。</summary>
        public void AddHills(IConstraint constraint, double size, double deviation, double fill)
        {
            AddElevation(constraint, new ElevationSpec
            {
                Class = ClHill,
                Painter = new object[] { Biome.MainTerrain, Biome.MainTerrain },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 5, MaxSize = 8, Spread = 20,
                MinElevation = 6, MaxElevation = 12, Steepness = 1.5,
            });

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(ClBluffIgnore),
                new NearTileClassConstraint(ClHill, 6));
        }

        /// <summary>addLakes——带鱼的湖泊 + 岸边芦苇碎石。</summary>
        public void AddLakes(IConstraint constraint, double size, double deviation, double fill)
        {
            object lakeTile = Biome.Water;

            if (BiomeName == "generic/temperate" || BiomeName == "generic/india")
                lakeTile = Biome.Dirt;
            if (BiomeName == "generic/aegean")
                lakeTile = Biome.Tier2Terrain;
            if (BiomeName == "generic/autumn")
                lakeTile = Biome.Shore;

            AddElevation(constraint, new ElevationSpec
            {
                Class = ClWater,
                Painter = new[] { lakeTile, lakeTile },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 6, MinSize = 7, MaxSize = 9, Spread = 70,
                MinElevation = -15, MaxElevation = -2, Steepness = 1.5,
            });

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddFish(c, s, d, f),
                    Avoid = new object[] { ClFish, 12, ClHill, 8, ClMountain, 8, ClPlayer, 8 },
                    Stay = new object[] { ClWater, 7 },
                    Amounts = new[] { "normal", "many", "tons" },
                },
            });

            PlaceGroups(
                Group(true, ClDirt, Obj(Biome.RockMedium, 1, 3, 1, 3)),
                new AndConstraint(new IConstraint[]
                {
                    Stay(ClWater, 1),
                    RmgenLibrary.BorderClasses(ClWater, 4, 3),
                }),
                1000, 100);

            PlaceGroups(
                Group(true, ClDirt,
                    Obj(Biome.Reeds, 10, 15, 1, 3),
                    Obj(Biome.RockMedium, 1, 3, 1, 3)),
                new AndConstraint(new IConstraint[]
                {
                    Stay(ClWater, 2),
                    RmgenLibrary.BorderClasses(ClWater, 4, 3),
                }),
                1000, 100);
        }

        /// <summary>addLayeredPatches——三档尺寸的分层泥地斑块。</summary>
        public void AddLayeredPatches(IConstraint constraint, double size, double deviation, double fill)
        {
            const double minRadius = 1;
            double maxRadius = Math.Floor(ScaleByMapSize(3, 5));
            double count = fill * ScaleByMapSize(15, 45);

            var patchSizes = new[]
            {
                ScaleByMapSize(3, 6),
                ScaleByMapSize(5, 10),
                ScaleByMapSize(8, 21),
            };

            foreach (double patchSize in patchSizes)
            {
                double offset = GetRandomDeviation(size, deviation);
                double patchMinRadius = Math.Floor(minRadius * offset);
                double patchMaxRadius = Math.Floor(maxRadius * offset);

                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng,
                        Math.Min(patchMinRadius, patchMaxRadius), patchMaxRadius,
                        Math.Floor(patchSize * offset), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { Biome.MainTerrain, Biome.Tier1Terrain },
                            new[] { Biome.Tier1Terrain, Biome.Tier2Terrain },
                            new[] { Biome.Tier2Terrain, Biome.Tier3Terrain },
                            new[] { Biome.Tier4Terrain },
                        }, new[] { 1, 1 }, Rng),
                        new TileClassPainter(ClDirt),
                    },
                    constraint,
                    count * offset);
            }
        }

        /// <summary>addMountains——陡峭山体。</summary>
        public void AddMountains(IConstraint constraint, double size, double deviation, double fill)
            => AddElevation(constraint, new ElevationSpec
            {
                Class = ClMountain,
                Painter = new object[] { Biome.Cliff, Biome.Hill },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 2, MaxSize = 4, Spread = 100,
                MinElevation = 100, MaxElevation = 120, Steepness = 4,
            });

        /// <summary>addPlateaus——高原 + 高原上的小丘 + 饰物。</summary>
        public void AddPlateaus(IConstraint constraint, double size, double deviation, double fill)
        {
            object plateauTile = Biome.Dirt;

            if (BiomeName == "generic/arctic")
                plateauTile = Biome.Tier1Terrain;
            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna")
                plateauTile = Biome.Tier2Terrain;
            if (BiomeName == "generic/autumn")
                plateauTile = Biome.Tier4Terrain;

            AddElevation(constraint, new ElevationSpec
            {
                Class = ClPlateau,
                Painter = new object[] { Biome.Cliff, plateauTile },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 15, MinSize = 2, MaxSize = 4, Spread = 200,
                MinElevation = 20, MaxElevation = 30, Steepness = 8,
            });

            for (int i = 0; i < 40; ++i)
            {
                int hillElevation = Rng.RandIntInclusive(4, 18);
                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, 3, 15, 1, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { plateauTile, plateauTile }, new[] { 3 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Blurry,
                            hillElevation, hillElevation - 2, relative: true),
                        new TileClassPainter(ClHill),
                    },
                    new AndConstraint(new IConstraint[] { Avoid(ClHill, 7), Stay(ClPlateau, 7) }),
                    1);
            }

            AddElements(new[]
            {
                new Element
                {
                    Func = (c, s, d, f, _) => AddDecoration(c, s, d, f),
                    Avoid = new object[] { ClDirt, 15, ClForest, 2, ClPlayer, 12, ClWater, 3 },
                    Stay = new object[] { ClPlateau, 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
                new Element
                {
                    Func = (c, s, d, f, _) => AddProps(c, s, d, f),
                    Avoid = new object[] { ClForest, 2, ClPlayer, 12, ClProp, 40, ClWater, 3 },
                    Stay = new object[] { ClPlateau, 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
        }

        /// <summary>addProps——桶/箱/推车/水井/骸骨等稀有道具 + 装饰树。</summary>
        public void AddProps(IConstraint constraint, double size, double deviation, double fill)
        {
            double offset = GetRandomDeviation(size, deviation);

            var props = new[]
            {
                new IGroupElement[] { Obj(Props.Skeleton, offset, 5 * offset, 0, 3 * offset + 2) },
                new IGroupElement[]
                {
                    Obj(Props.Barrels, offset, 2 * offset, 2, 3 * offset + 2),
                    Obj(Props.Cart, 0, offset, 5, 2.5 * offset + 5),
                    Obj(Props.Crate, offset, 2 * offset, 2, 2 * offset + 2),
                    Obj(Props.Well, 0, 1, 2, 2 * offset + 2),
                },
            };

            var counts = new[]
            {
                ScaleByMapSize(16, 262),
                ScaleByMapSize(8, 131),
            };

            for (int i = 0; i < props.Length; ++i)
                PlaceGroups(new ObjectGroup(props[i], true), constraint,
                    Math.Floor(counts[i] * fill), 5);

            var trees = Obj(Biome.Tree, 5 * offset, 30 * offset, 2, 3 * offset + 10);
            PlaceGroups(new ObjectGroup(new IGroupElement[] { trees }, true), constraint,
                counts[0] * 5 * fill, 5);
        }

        /// <summary>addValleys——洼地（baseHeight &lt; 6 时上游直接跳过）。</summary>
        public void AddValleys(IConstraint constraint, double size, double deviation, double fill,
            double baseHeight)
        {
            if (baseHeight < 6)
                return;

            double minElevation = Math.Max(-baseHeight, 1 - baseHeight / (size * (deviation + 1)));

            object valleySlope = Biome.Tier1Terrain;
            object valleyFloor = Biome.Tier4Terrain;

            if (BiomeName == "generic/sahara")
            {
                valleySlope = Biome.Tier3Terrain;
                valleyFloor = Biome.Dirt;
            }

            if (BiomeName == "generic/aegean")
            {
                valleySlope = Biome.Tier2Terrain;
                valleyFloor = Biome.Dirt;
            }

            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna")
                valleyFloor = Biome.Tier2Terrain;

            if (BiomeName == "generic/india")
                valleySlope = Biome.Dirt;

            if (BiomeName == "generic/autumn")
                valleyFloor = Biome.Tier3Terrain;

            AddElevation(constraint, new ElevationSpec
            {
                Class = ClValley,
                Painter = new[] { valleySlope, valleyFloor },
                Size = size, Deviation = deviation, Fill = fill,
                Count = 8, MinSize = 5, MaxSize = 8, Spread = 30,
                MinElevation = minElevation, MaxElevation = -2, Steepness = 4,
            });
        }

        // ══════════ 资源 ══════════

        /// <summary>addAnimals——主/次猎物群。</summary>
        public void AddAnimals(IConstraint constraint, double size, double deviation, double fill)
        {
            double groupOffset = GetRandomDeviation(size, deviation);

            var animals = new[]
            {
                new IGroupElement[]
                {
                    Obj(Biome.MainHuntableAnimal, 5 * groupOffset, 7 * groupOffset, 0, 4 * groupOffset),
                },
                new IGroupElement[]
                {
                    Obj(Biome.SecondaryHuntableAnimal, 2 * groupOffset, 3 * groupOffset, 0, 2 * groupOffset),
                },
            };

            foreach (var animal in animals)
                PlaceGroups(new ObjectGroup(animal, true, ClAnimals), constraint,
                    Math.Floor(30 * fill), 50);
        }

        /// <summary>addBerries——浆果丛。</summary>
        public void AddBerries(IConstraint constraint, double size, double deviation, double fill)
        {
            double groupOffset = GetRandomDeviation(size, deviation);

            PlaceGroups(
                Group(true, ClBerries,
                    Obj(Biome.FruitBush, 5 * groupOffset, 5 * groupOffset, 0, 3 * groupOffset)),
                constraint, Math.Floor(50 * fill), 40);
        }

        /// <summary>addFish——近岸小群 + 深水大群。</summary>
        public void AddFish(IConstraint constraint, double size, double deviation, double fill)
        {
            double groupOffset = GetRandomDeviation(size, deviation);

            var fishes = new[]
            {
                new IGroupElement[] { Obj(Biome.Fish, groupOffset, 2 * groupOffset, 0, 2 * groupOffset) },
                new IGroupElement[]
                {
                    Obj(Biome.Fish, 2 * groupOffset, 4 * groupOffset, 10 * groupOffset, 20 * groupOffset),
                },
            };

            foreach (var fish in fishes)
                PlaceGroups(new ObjectGroup(fish, true, ClFish), constraint,
                    Math.Floor(40 * fill), 50);
        }

        /// <summary>addForests——四类林型（林地 + 两种树种混合）。savanna 无林。</summary>
        public void AddForests(IConstraint constraint, double size, double deviation, double fill)
        {
            if (BiomeName == "generic/savanna")
                return;

            const char sep = TerrainFactory.TerrainSeparator;

            var treeTypes = new[]
            {
                new object[]
                {
                    Biome.ForestFloor2 + sep + Biome.Tree1,
                    Biome.ForestFloor2 + sep + Biome.Tree2,
                    Biome.ForestFloor2,
                },
                new object[]
                {
                    Biome.ForestFloor1 + sep + Biome.Tree4,
                    Biome.ForestFloor1 + sep + Biome.Tree5,
                    Biome.ForestFloor1,
                },
            };

            var forestTypes = new[]
            {
                new object[] { Biome.ForestFloor2, Biome.MainTerrain, treeTypes[0] },
                new object[] { Biome.ForestFloor2, treeTypes[0] },
                new object[] { Biome.ForestFloor2, Biome.MainTerrain, treeTypes[1] },
                new object[] { Biome.ForestFloor1, treeTypes[1] },
                new object[] { Biome.ForestFloor1, Biome.MainTerrain, treeTypes[0] },
                new object[] { Biome.ForestFloor2, treeTypes[0] },
                new object[] { Biome.ForestFloor1, Biome.MainTerrain, treeTypes[1] },
                new object[] { Biome.ForestFloor1, treeTypes[1] },
            };

            // 上游 forestTypes 是 4 个「二元组」，逐组一次 createAreas（painter 层为该二元组）
            for (int t = 0; t < forestTypes.Length; t += 2)
            {
                double offset = GetRandomDeviation(size, deviation);
                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, 1,
                        Math.Floor(ScaleByMapSize(3, 5) * offset),
                        Math.Floor(50 * offset), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { forestTypes[t], forestTypes[t + 1] },
                            new[] { 2 }, Rng),
                        new TileClassPainter(ClForest),
                    },
                    constraint,
                    10 * fill);
            }
        }

        /// <summary>addMetal——大金属矿。</summary>
        public void AddMetal(IConstraint constraint, double size, double deviation, double fill)
        {
            double offset = GetRandomDeviation(size, deviation);
            PlaceGroups(
                Group(true, ClMetal, Obj(Biome.MetalLarge, offset, offset, 0, 4 * offset)),
                constraint, 1 + 20 * fill, 100);
        }

        /// <summary>addSmallMetal——小金属矿。</summary>
        public void AddSmallMetal(IConstraint constraint, double size, double mixes, double amounts)
        {
            double deviation = GetRandomDeviation(size, mixes);
            PlaceGroups(
                Group(true, ClMetal,
                    Obj(Biome.MetalSmall, 2 * deviation, 5 * deviation, deviation, 3 * deviation)),
                constraint, 1 + 20 * amounts, 100);
        }

        /// <summary>addStone——大石矿 + 小石矿两组。</summary>
        public void AddStone(IConstraint constraint, double size, double deviation, double fill)
        {
            double offset = GetRandomDeviation(size, deviation);

            var mines = new[]
            {
                new IGroupElement[]
                {
                    Obj(Biome.StoneSmall, 0, 2 * offset, 0, 4 * offset),
                    Obj(Biome.StoneLarge, offset, offset, 0, 4 * offset),
                },
                new IGroupElement[]
                {
                    Obj(Biome.StoneSmall, 2 * offset, 5 * offset, offset, 3 * offset),
                },
            };

            foreach (var mine in mines)
                PlaceGroups(new ObjectGroup(mine, true, ClRock), constraint, 1 + 20 * fill, 100);
        }

        /// <summary>addStragglerTrees——散落树（savanna 强制加量）。</summary>
        public void AddStragglerTrees(IConstraint constraint, double size, double deviation, double fill)
        {
            bool savanna = BiomeName == "generic/savanna";
            if (savanna)
            {
                fill = Math.Max(fill, 2);
                size = Math.Max(size, 1);
            }

            var trees = new[] { Biome.Tree1, Biome.Tree2, Biome.Tree3, Biome.Tree4 };

            const double treesPerPlayer = 40;
            double playerBonus = Math.Max(1, (RmgenCommon.GetNumPlayers(Settings) - 3) / 2.0);

            double offset = GetRandomDeviation(size, deviation);
            double treeCount = treesPerPlayer * playerBonus * fill;
            // scaleByMapSize(x, x) 恒为 x
            double totalTrees = treeCount;

            double count = Math.Floor(totalTrees / trees.Length) * fill;
            double min = offset;
            double max = 4 * offset;
            double minDist = offset;
            double maxDist = 5 * offset;

            if (savanna)
            {
                min = 3 * offset;
                max = 5 * offset;
                minDist = 2 * offset + 1;
                maxDist = 3 * offset + 2;
            }

            for (int i = 0; i < trees.Length; ++i)
            {
                double treesMax = max;

                // 果树不成簇
                if (i == 2 && (BiomeName == "generic/sahara" || BiomeName == "generic/aegean"))
                    treesMax = 1;

                min = Math.Min(min, treesMax);

                PlaceGroups(
                    Group(true, ClForest, Obj(trees[i], min, treesMax, minDist, maxDist)),
                    constraint, count);
            }
        }

        // ══════════ bluff 几何辅助 ══════════

        /// <summary>isBluffPassable——台地是否有可从地面走上去的连续入口（逐字移植）。</summary>
        private bool IsBluffPassable(Area bluffArea,
            (RmgenVector2D start, RmgenVector2D end)? baseLine,
            (RmgenVector2D start, RmgenVector2D end)? endLine)
        {
            if (baseLine == null || endLine == null ||
                !Map.ValidTilePassable(endLine.Value.start) && !Map.ValidTilePassable(endLine.Value.end))
                return false;

            const int minTilesInGroup = 2;
            bool insideBluff = false;
            bool outsideBluff = false;

            var (min, max) = RmgenGeometry.GetBoundingBox(bluffArea.GetPoints());

            for (double x = min.X; x <= max.X; ++x)
            {
                int count = 0;
                for (double y = min.Y; y <= max.Y; ++y)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (!bluffArea.Contains(pos))
                        continue;

                    bool valid = Map.ValidTilePassable(pos);
                    if (valid)
                    {
                        ++count;
                        insideBluff = true;
                        if (outsideBluff)
                            return false;
                    }
                }

                if (insideBluff && count < minTilesInGroup)
                    outsideBluff = true;
            }

            insideBluff = false;
            outsideBluff = false;

            for (double y = min.Y; y <= max.Y; ++y)
            {
                int count = 0;
                for (double x = min.X; x <= max.X; ++x)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (!bluffArea.Contains(pos))
                        continue;

                    // 上游此处确实用 pos.add(corners.min)（与上一循环不对称，疑似上游笔误，照搬）
                    bool valid = Map.ValidTilePassable(RmgenVector2D.Add(pos, min));
                    if (valid)
                    {
                        ++count;
                        insideBluff = true;
                        if (outsideBluff)
                            return false;
                    }
                }

                if (insideBluff && count < minTilesInGroup)
                    outsideBluff = true;
            }

            return true;
        }

        /// <summary>findClearLine——找一条 45° 方向上不与台地相交的扫描线（逐字移植）。</summary>
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
                default: throw new ArgumentOutOfRangeException(nameof(angle), angle, "Unknown angle");
            }

            (RmgenVector2D start, RmgenVector2D end)? clearLine = null;

            for (double x = min.X; x <= max.X; ++x)
            {
                var start = new RmgenVector2D(x, y);

                bool intersectsBluff = false;
                var end = start;

                while (end.X >= min.X && end.X <= max.X && end.Y >= min.Y && end.Y <= max.Y)
                {
                    if (bluffArea.Contains(end) && Map.ValidTilePassable(end))
                    {
                        intersectsBluff = true;
                        break;
                    }
                    end.Add(offset);
                }

                if (!intersectsBluff)
                    clearLine = (start, RmgenVector2D.Sub(end, offset));

                if (intersectsBluff ? angle == 0 || angle == 3 : angle == 1 || angle == 2)
                    break;
            }

            return clearLine;
        }
    }
}
