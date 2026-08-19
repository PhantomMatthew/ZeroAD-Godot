using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>wall_builder.js 最小移植——仅 wild_lake 的 "other" 风格
    /// （农场围栏：fence/fence_short/bench/foodBin/animal/farmstead + turn_X）。
    /// 元素参数取自上游模板 XML 的 WallPiece 块
    /// （fence_long Length=12/fence_short Length=6，除 TERRAIN_TILE_SIZE=4 → 3/1.5 格）。
    /// 全流程无 RNG（确定性布局）；抽数都在调用方（pickRandom(fences)/randomAngle）。</summary>
    public static class WallBuilder
    {
        /// <summary>墙元素（上游 readyWallElement/内联字面量的公共形状）。</summary>
        public readonly struct WallElement
        {
            public readonly string? TemplateName;   // null = 空白（turn/gap/entry）
            public readonly double Angle, Length, Indent, Bend;
            public WallElement(string? templateName, double angle, double length, double indent, double bend)
            { TemplateName = templateName; Angle = angle; Length = length; Indent = indent; Bend = bend; }
        }

        /// <summary>Fortress（墙元素名序列 + 可选中心偏移）。</summary>
        public sealed class Fortress
        {
            public readonly string Type;
            public readonly List<string> Wall;
            public RmgenVector2D? CenterToFirstElement;
            public Fortress(string type, IEnumerable<string> wall, RmgenVector2D? centerToFirstElement = null)
            { Type = type; Wall = new List<string>(wall); CenterToFirstElement = centerToFirstElement; }
        }

        private const double TerrainTileSize = RmgenConstants.TERRAIN_TILE_SIZE;

        /// <summary>wild_lake 的 g_WallStyles.other（farmEntities 由 biome JSON 给）。</summary>
        public static Dictionary<string, WallElement> WildLakeOtherStyle(string farmAnimal, string farmBuilding)
            => new()
            {
                // readyWallElement("structures/fence_long"/"fence_short", "gaia")：WallPiece
                // Length 12/6、Orientation 0.5（angle=0.5π）、无 Indent/Bend
                ["fence"] = new WallElement("structures/fence_long", Math.PI / 2, 12 / TerrainTileSize, 0, 0),
                ["fence_short"] = new WallElement("structures/fence_short", Math.PI / 2, 6 / TerrainTileSize, 0, 0),
                ["bench"] = new WallElement("structures/bench", Math.PI / 2, 1.5, 0, 0),
                ["foodBin"] = new WallElement("gaia/treasure/food_bin", Math.PI / 2, 1.5, 0, 0),
                ["animal"] = new WallElement(farmAnimal, 0, 0, 0.75, 0),
                ["farmstead"] = new WallElement(farmBuilding, Math.PI, 0, -3, 0),
            };

        /// <summary>getWallElement（"other" 风格版）——直查表 + turn_X 派生；
        /// overlap 恒 0。</summary>
        private static WallElement GetWallElement(Dictionary<string, WallElement> style, string element)
        {
            if (style.TryGetValue(element, out var e))
                return e;

            // 无 tower 的 "other" 风格：默认全零元素
            var ret = new WallElement(null, 0, 0, 0, 0);
            if (element.StartsWith("turn_", StringComparison.Ordinal))
                ret = new WallElement(null, 0, 0, 0,
                    double.Parse(element.Substring("turn_".Length),
                        System.Globalization.CultureInfo.InvariantCulture) * Math.PI);
            else if (element.StartsWith("gap_", StringComparison.Ordinal))
                ret = new WallElement(null, 0,
                    double.Parse(element.Substring("gap_".Length),
                        System.Globalization.CultureInfo.InvariantCulture), 0, 0);
            return ret;
        }

        /// <summary>getWallAlignment——沿墙链逐个定元素位置/朝向（indent/bend 修正同上游）。</summary>
        public static List<(RmgenVector2D position, string? templateName, double angle)> GetWallAlignment(
            Dictionary<string, WallElement> style, RmgenVector2D position,
            IReadOnlyList<string> wall, double orientation)
        {
            var alignment = new List<(RmgenVector2D, string?, double)>();
            var wallPosition = position;

            for (int i = 0; i < wall.Count; ++i)
            {
                var element = GetWallElement(style, wall[i]);

                alignment.Add((
                    RmgenVector2D.Sub(wallPosition,
                        Rotate(new RmgenVector2D(element.Indent, 0), -orientation)),
                    element.TemplateName,
                    orientation + element.Angle));

                if (i + 1 < wall.Count)
                {
                    orientation += element.Bend;
                    var nextElement = GetWallElement(style, wall[i + 1]);

                    double distance = (element.Length + nextElement.Length) / 2;   // overlap=0

                    // 同时有 indent 和 bend 的修正
                    double indent = element.Indent;
                    double bend = element.Bend;
                    if (bend != 0 && indent != 0)
                    {
                        distance += indent * SafeMath.Sin(bend);
                        wallPosition.Add(Rotate(new RmgenVector2D(indent, 0), -orientation));
                    }

                    var step = Rotate(new RmgenVector2D(distance, 0), -orientation).Perpendicular();
                    wallPosition.Add(step);
                }
            }

            return alignment;
        }

        /// <summary>getCenterToFirstElement——对齐结果的质心到首元素的向量。</summary>
        public static RmgenVector2D GetCenterToFirstElement(
            List<(RmgenVector2D position, string? templateName, double angle)> alignment)
        {
            var result = new RmgenVector2D(0, 0);
            foreach (var align in alignment)
                result.Sub(RmgenVector2D.Div(align.position, alignment.Count));
            return result;
        }

        /// <summary>placeWall——按对齐结果放实体（validTilePassable + 约束检查）。</summary>
        public static void PlaceWall(RandomMap map, Dictionary<string, WallElement> style,
            RmgenVector2D position, IReadOnlyList<string> wall, int playerId, double orientation,
            IConstraint? constraints)
        {
            var constraint = constraints ?? new NullConstraint();
            foreach (var align in GetWallAlignment(style, position, wall, orientation))
            {
                if (align.templateName == null || !map.InMapBounds(align.position))
                    continue;
                var floored = align.position;
                floored.Floor();
                if (!constraint.Allows(floored))
                    continue;
                map.PlaceEntityPassable(align.templateName, playerId, align.position, align.angle);
            }
        }

        /// <summary>placeCustomFortress——质心对齐后 placeWall。</summary>
        public static void PlaceCustomFortress(RandomMap map, Dictionary<string, WallElement> style,
            RmgenVector2D centerPosition, Fortress fortress, int playerId, double orientation,
            IConstraint? constraints)
        {
            var centerToFirstElement = fortress.CenterToFirstElement
                ?? GetCenterToFirstElement(GetWallAlignment(style, new RmgenVector2D(0, 0), fortress.Wall, 0));

            var a = Rotate(new RmgenVector2D(centerToFirstElement.X, 0), -orientation);
            var b = Rotate(new RmgenVector2D(centerToFirstElement.Y, 0).Perpendicular(), -orientation);
            var position = RmgenVector2D.Add(RmgenVector2D.Add(centerPosition, a), b);

            PlaceWall(map, style, position, fortress.Wall, playerId, orientation, constraints);
        }

        private static RmgenVector2D Rotate(RmgenVector2D v, double angle)
        { v.Rotate(angle); return v; }
    }
}
