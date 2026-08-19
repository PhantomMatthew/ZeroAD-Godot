using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>Terrain 抽象(原版 rmgen/Terrain.js):对一个 tile 落一次"地表动作"
    /// (刷贴图,可附带放实体)。</summary>
    public interface ITerrain
    {
        void Place(RandomMap map, RmgenRng rng, RmgenVector2D pos);
    }

    /// <summary>SimpleTerrain:刷 texture;"tex|entity" 形式附带在每格放 entity
    /// (替代同格旧 terrainEntity,原版语义)。</summary>
    public sealed class SimpleTerrain : ITerrain
    {
        public readonly string Texture;
        public readonly string? TemplateName;

        public SimpleTerrain(string texture, string? templateName = null)
        {
            Texture = texture ?? throw new ArgumentNullException(nameof(texture));
            TemplateName = templateName;
        }

        public void Place(RandomMap map, RmgenRng rng, RmgenVector2D pos)
        {
            if (TemplateName != null && map.ValidTilePassable(pos))
                map.SetTerrainEntity(TemplateName, 0,
                    new RmgenVector2D(pos.X + 0.5, pos.Y + 0.5), rng.RandomAngle());
            map.SetTexture(pos, Texture);
        }
    }

    /// <summary>RandomTerrain:每格从候选中随机挑一个 Terrain 落(森林异质化核心:
    /// 同一片森林里 floor 与多树种按格混合)。</summary>
    public sealed class RandomTerrain : ITerrain
    {
        private readonly List<ITerrain> _terrains;
        public RandomTerrain(List<ITerrain> terrains)
        {
            if (terrains.Count == 0) throw new ArgumentException("RandomTerrain: empty", nameof(terrains));
            _terrains = terrains;
        }

        public void Place(RandomMap map, RmgenRng rng, RmgenVector2D pos)
            => rng.PickRandom(_terrains).Place(map, rng, pos);
    }

    public static class TerrainFactory
    {
        public const char TerrainSeparator = '|';

        /// <summary>原版 createTerrain:string → "tex" 或 "tex|entity" → SimpleTerrain;
        /// string[](或 List) → RandomTerrain(逐元素解析)。</summary>
        public static ITerrain CreateTerrain(string terrain)
        {
            int sep = terrain.IndexOf(TerrainSeparator);
            return sep < 0
                ? new SimpleTerrain(terrain)
                : new SimpleTerrain(terrain.Substring(0, sep), terrain.Substring(sep + 1));
        }

        public static ITerrain CreateTerrain(IEnumerable<string> terrains)
        {
            var list = new List<ITerrain>();
            foreach (var t in terrains) list.Add(CreateTerrain(t));
            return new RandomTerrain(list);
        }

        /// <summary>混合嵌套版 createTerrain:string → Simple（"tex|entity" 拆分）；
        /// ITerrain 直接用；IEnumerable（string 与数组任意嵌套）→ RandomTerrain 逐元素递归——
        /// 与 JS createTerrain 的数组递归一致。</summary>
        public static ITerrain CreateTerrain(object terrain)
        {
            switch (terrain)
            {
                case string s: return CreateTerrain(s);
                case ITerrain it: return it;
                case System.Collections.IEnumerable mix:
                    var list = new List<ITerrain>();
                    foreach (var item in mix)
                        list.Add(CreateTerrain(item!));
                    return new RandomTerrain(list);
                default:
                    throw new ArgumentException($"createTerrain: bad terrain {terrain}", nameof(terrain));
            }
        }
    }
}
