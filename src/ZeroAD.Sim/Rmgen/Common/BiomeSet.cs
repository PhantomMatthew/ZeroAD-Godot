using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>
    /// biome 地形/实体套装——原版 rmbiome 的 g_Terrains + g_Gaia + g_Decoratives +
    /// ResourceCounts.trees 的合成视图。数据来自上游
    /// maps/random/rmbiome/defaultbiome.json(基线)+ generic/&lt;name&gt;.json(覆盖),
    /// 带 .js 随机分支的 5 个 biome(temperate/savanna/sahara/nubia/aegean)在 JSON 之上
    /// 由 <see cref="BiomeLoader.ApplyJsOverlay"/> 用 RmgenRng 复现(逐行移植,消费顺序一致)。
    /// </summary>
    public sealed class BiomeSet
    {
        // ── Terrains(名单字段在上游即 string|string[] 混合,统一成 List)──
        public List<string> MainTerrain = new();
        public string ForestFloor1 = "";
        public string ForestFloor2 = "";
        public string Tier1Terrain = "";
        public string Tier2Terrain = "";
        public string Tier3Terrain = "";
        public string Tier4Terrain = "";
        public List<string> Cliff = new();
        public List<string> Hill = new();
        public List<string> Dirt = new();
        public string Road = "";
        public string RoadWild = "";
        public string ShoreBlend = "";
        public string Shore = "";
        public string Water = "";

        // ── 图专属 biome 字段（rmbiome/<map>/<name>.json 才有，如 alpine_valley）──
        public string ForestFloor = "";
        public List<string> HalfSnow = new();
        public List<string> SnowLimited = new();

        // ── Gaia ──
        public string Tree1 = "";
        public string Tree2 = "";
        public string Tree3 = "";
        public string Tree4 = "";
        public string Tree5 = "";
        public string FruitBush = "";
        public string StartingAnimal = "";
        public string Fish = "";
        public string MainHuntableAnimal = "";
        public string SecondaryHuntableAnimal = "";
        public string StoneLarge = "";
        public string StoneSmall = "";
        public string MetalLarge = "";
        public string MetalSmall = "";

        // ── Decoratives ──
        public string Grass = "";
        public string GrassShort = "";
        public string BushMedium = "";
        public string BushSmall = "";
        public string RockLarge = "";
        public string RockMedium = "";

        // ── ResourceCounts.trees ──
        public int TreesMin = 500;
        public int TreesMax = 3000;
        public double ForestProbability = 0.7;

        /// <summary>mainTerrain 在上游是数组(g_Terrains.mainTerrain[0] 被 map 脚本引用)。</summary>
        public string MainTerrain0 => MainTerrain.Count > 0 ? MainTerrain[0] : "";

        /// <summary>深拷贝(List 字段独立)——JSON 合并结果被缓存,覆盖层必须改在副本上。</summary>
        public BiomeSet Clone()
        {
            var c = (BiomeSet)MemberwiseClone();
            c.MainTerrain = new List<string>(MainTerrain);
            c.HalfSnow = new List<string>(HalfSnow);
            c.SnowLimited = new List<string>(SnowLimited);
            c.Cliff = new List<string>(Cliff);
            c.Hill = new List<string>(Hill);
            c.Dirt = new List<string>(Dirt);
            return c;
        }
    }

    /// <summary>biome 加载器。dataRoot = binaries/data/mods/public(junction);null 时
    /// 回退内置 temperate 默认(测试/数据缺失环境)。结果按 (biome 名) 缓存 JSON 合并部分;
    /// .js 覆盖层每次用调用方 rng 现算(随机分支,不该缓存)。</summary>
    public static class BiomeLoader
    {
        private static readonly Dictionary<string, BiomeSet> s_cache = new(StringComparer.Ordinal);

        /// <summary>已移植的 biome 名(generic/ 前缀省略)。.js 5 个 + 纯 JSON 5 个。</summary>
        public static readonly string[] KnownBiomes =
        {
            "temperate", "alpine", "arctic", "autumn", "india",
            "nubia", "sahara", "savanna", "steppe", "aegean",
        };

        /// <summary>加载 biome(defaultbiome.json 基线 + generic/&lt;name&gt;.json 覆盖 + .js 覆盖层)。
        /// biomeName 可带 "generic/" 前缀。rng 仅用于 .js 覆盖层的随机分支。</summary>
        public static BiomeSet Load(string? dataRoot, string biomeName, RmgenRng rng)
        {
            string name = biomeName.StartsWith("generic/", StringComparison.Ordinal)
                ? biomeName.Substring("generic/".Length) : biomeName;

            BiomeSet set;
            if (dataRoot != null)
            {
                if (!s_cache.TryGetValue(dataRoot + "|" + name, out var cached))
                {
                    cached = LoadMerged(dataRoot, name);
                    s_cache[dataRoot + "|" + name] = cached;
                }
                set = cached.Clone();
            }
            else
            {
                set = TemperateDefault();
            }

            ApplyJsOverlay(set, name, rng);
            return set;
        }

        /// <summary>数据缺失环境(单元测试)下的 temperate 默认——抄自 defaultbiome.json。</summary>
        public static BiomeSet TemperateDefault() => new()
        {
            MainTerrain = { "temperate_grass_02" },
            ForestFloor1 = "temperate_forestfloor_01",
            ForestFloor2 = "temperate_forestfloor_02",
            Tier1Terrain = "temperate_grass_dirt_02",
            Tier2Terrain = "temperate_grass_03",
            Tier3Terrain = "temperate_grass_04",
            Tier4Terrain = "temperate_grass_01",
            Cliff = { "temperate_cliff_01", "temperate_cliff_02" },
            Hill = { "temperate_rocks_dirt_01", "temperate_grass_dirt_03" },
            Dirt = { "temperate_mud_01", "temperate_grass_mud_01" },
            Road = "temperate_paving_03",
            RoadWild = "temperate_paving_01",
            ShoreBlend = "temperate_grass_dirt_01",
            Shore = "temperate_rocks_dirt_01",
            Water = "temperate_rocks_dirt_01",
            ForestFloor = "temperate_forestfloor_01",
            HalfSnow = { "temperate_grass_dirt_02" },
            SnowLimited = { "temperate_rocks_dirt_01" },
            Tree1 = "gaia/tree/oak",
            Tree2 = "gaia/tree/oak_holly",
            Tree3 = "gaia/tree/oak_hungarian",
            Tree4 = "gaia/tree/pine_black",
            Tree5 = "gaia/tree/pine_maritime",
            FruitBush = "gaia/fruit/berry_01",
            StartingAnimal = "gaia/fauna_chicken",
            Fish = "gaia/fish/generic",
            MainHuntableAnimal = "gaia/fauna_deer",
            SecondaryHuntableAnimal = "gaia/fauna_sheep",
            StoneLarge = "gaia/rock/temperate_large",
            StoneSmall = "gaia/rock/temperate_cut",
            MetalLarge = "gaia/ore/temperate_01",
            MetalSmall = "gaia/ore/temperate_02",
            Grass = "actor|props/flora/grass_soft_large_tall.xml",
            GrassShort = "actor|props/flora/grass_soft_large.xml",
            RockLarge = "actor|geology/stone_granite_med.xml",
            RockMedium = "actor|geology/stone_granite_small.xml",
            BushMedium = "actor|props/flora/bush_medit_me.xml",
            BushSmall = "actor|props/flora/bush_medit_sm.xml",
            TreesMin = 500,
            TreesMax = 3000,
            ForestProbability = 0.7,
        };

        // ── JSON 加载(defaultbiome 基线 + biome 覆盖)──

        private static BiomeSet LoadMerged(string dataRoot, string name)
        {
            var set = TemperateDefault();   // defaultbiome.json 即 temperate 基线
            string biomeDir = Path.Combine(dataRoot, "maps", "random", "rmbiome");
            ApplyJsonFile(set, Path.Combine(biomeDir, "defaultbiome.json"));
            // name 带 "/" 时是图专属目录（如 alpine/winter → rmbiome/alpine/winter.json，
            // 上游 setBiome 同样直接 loadBiomeFile(biomeID)）；否则走 generic/。
            ApplyJsonFile(set, name.Contains('/')
                ? Path.Combine(biomeDir, name + ".json")
                : Path.Combine(biomeDir, "generic", name + ".json"));
            return set;
        }

        private static void ApplyJsonFile(BiomeSet set, string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("Terrains", out var t)) ApplyTerrains(set, t);
                if (root.TryGetProperty("Gaia", out var g)) ApplyGaia(set, g);
                if (root.TryGetProperty("Decoratives", out var d)) ApplyDecoratives(set, d);
                if (root.TryGetProperty("ResourceCounts", out var rc) &&
                    rc.TryGetProperty("trees", out var trees))
                {
                    if (trees.TryGetProperty("min", out var mn)) set.TreesMin = mn.GetInt32();
                    if (trees.TryGetProperty("max", out var mx)) set.TreesMax = mx.GetInt32();
                    if (trees.TryGetProperty("forestProbability", out var fp))
                        set.ForestProbability = fp.GetDouble();
                }
            }
            catch (Exception)
            {
                // 单个 biome JSON 解析失败 → 保留当前累积值
            }
        }

        private static void Assign(ref string field, JsonElement obj, string key)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                field = v.GetString() ?? field;
        }

        private static void AssignList(List<string> field, JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var v)) return;
            field.Clear();
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) field.Add(s);
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        field.Add(item.GetString() ?? "");
            }
        }

        private static void ApplyTerrains(BiomeSet s, JsonElement t)
        {
            AssignList(s.MainTerrain, t, "mainTerrain");
            Assign(ref s.ForestFloor1, t, "forestFloor1");
            Assign(ref s.ForestFloor2, t, "forestFloor2");
            Assign(ref s.Tier1Terrain, t, "tier1Terrain");
            Assign(ref s.Tier2Terrain, t, "tier2Terrain");
            Assign(ref s.Tier3Terrain, t, "tier3Terrain");
            Assign(ref s.Tier4Terrain, t, "tier4Terrain");
            AssignList(s.Cliff, t, "cliff");
            AssignList(s.Hill, t, "hill");
            AssignList(s.Dirt, t, "dirt");
            Assign(ref s.Road, t, "road");
            Assign(ref s.RoadWild, t, "roadWild");
            Assign(ref s.ShoreBlend, t, "shoreBlend");
            Assign(ref s.Shore, t, "shore");
            Assign(ref s.Water, t, "water");
            Assign(ref s.ForestFloor, t, "forestFloor");
            AssignList(s.HalfSnow, t, "halfSnow");
            AssignList(s.SnowLimited, t, "snowLimited");
        }

        private static void ApplyGaia(BiomeSet s, JsonElement g)
        {
            Assign(ref s.Tree1, g, "tree1");
            Assign(ref s.Tree2, g, "tree2");
            Assign(ref s.Tree3, g, "tree3");
            Assign(ref s.Tree4, g, "tree4");
            Assign(ref s.Tree5, g, "tree5");
            Assign(ref s.FruitBush, g, "fruitBush");
            Assign(ref s.StartingAnimal, g, "startingAnimal");
            Assign(ref s.Fish, g, "fish");
            Assign(ref s.MainHuntableAnimal, g, "mainHuntableAnimal");
            Assign(ref s.SecondaryHuntableAnimal, g, "secondaryHuntableAnimal");
            Assign(ref s.StoneLarge, g, "stoneLarge");
            Assign(ref s.StoneSmall, g, "stoneSmall");
            Assign(ref s.MetalLarge, g, "metalLarge");
            Assign(ref s.MetalSmall, g, "metalSmall");
        }

        private static void ApplyDecoratives(BiomeSet s, JsonElement d)
        {
            Assign(ref s.Grass, d, "grass");
            Assign(ref s.GrassShort, d, "grassShort");
            Assign(ref s.BushMedium, d, "bushMedium");
            Assign(ref s.BushSmall, d, "bushSmall");
            Assign(ref s.RockLarge, d, "rockLarge");
            Assign(ref s.RockMedium, d, "rockMedium");
        }

        // ── .js 随机分支覆盖层(逐行移植;未知 biome 无覆盖)──

        private static void ApplyJsOverlay(BiomeSet s, string name, RmgenRng rng)
        {
            switch (name)
            {
                case "temperate": OverlayTemperate(s, rng); break;
                case "savanna": OverlaySavanna(s, rng); break;
                case "sahara": OverlaySahara(s, rng); break;
                case "nubia": OverlayNubia(s, rng); break;
                case "aegean": OverlayAegean(s, rng); break;
            }
        }

        /// <summary>rmbiome/generic/temperate.js:randBool 二选一地形组 + 两组 pickRandom 树种。</summary>
        private static void OverlayTemperate(BiomeSet s, RmgenRng rng)
        {
            if (rng.RandBool())
            {
                s.MainTerrain.Clear(); s.MainTerrain.Add("temperate_grass_04");
                s.ForestFloor1 = "temperate_forestfloor_01";
                s.ForestFloor2 = "temperate_forestfloor_02";
                s.Tier1Terrain = "temperate_grass_dirt_02";
                s.Tier2Terrain = "temperate_grass_03";
                s.Tier3Terrain = "temperate_grass_04";
                s.Tier4Terrain = "temperate_grass_01";
            }
            else
            {
                s.MainTerrain.Clear(); s.MainTerrain.Add("temperate_grass_05");
                s.ForestFloor1 = "temperate_forestfloor_02_autumn";
                s.ForestFloor2 = "temperate_forestfloor_01_autumn";
                s.Tier1Terrain = "temperate_grass_dirt_01";
                s.Tier2Terrain = "temperate_grass_dirt_02";
                s.Tier3Terrain = "temperate_grass_mud_01";
                s.Tier4Terrain = "temperate_grass_02";
            }

            var t12 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/oak", "gaia/tree/oak_hungarian" },
                new[] { "gaia/tree/oak_holly", "gaia/tree/maple" },
                new[] { "gaia/tree/oak_hungarian", "gaia/tree/oak_holly" },
            });
            s.Tree1 = t12[0]; s.Tree2 = t12[1];

            var t45 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/pine", "gaia/tree/pine_maritime" },
                new[] { "gaia/tree/pine", "gaia/tree/pine" },
                new[] { "gaia/tree/pine_maritime", "gaia/tree/pine_maritime" },
            });
            s.Tree4 = t45[0]; s.Tree5 = t45[1];
        }

        /// <summary>savanna.js:主猎物四选一。</summary>
        private static void OverlaySavanna(BiomeSet s, RmgenRng rng)
        {
            s.MainHuntableAnimal = rng.PickRandom(new[]
            {
                "gaia/fauna_wildebeest", "gaia/fauna_zebra", "gaia/fauna_giraffe", "gaia/fauna_gazelle",
            });
        }

        /// <summary>sahara.js:tree1/2 二选一组合,tree4/5 同源二选一。</summary>
        private static void OverlaySahara(BiomeSet s, RmgenRng rng)
        {
            var t12 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/cretan_date_palm_short", "gaia/tree/date_palm" },
                new[] { "gaia/tree/date_palm", "gaia/tree/cretan_date_palm_tall" },
            });
            s.Tree1 = t12[0]; s.Tree2 = t12[1];
            var t45 = rng.PickRandom(new[] { "gaia/tree/date_palm", "gaia/tree/cretan_date_palm_patch" });
            s.Tree4 = t45; s.Tree5 = t45;
        }

        /// <summary>nubia.js:主猎物五选一 + tree3 二选一 + tree4/5 三选一组合(逐行移植)。</summary>
        private static void OverlayNubia(BiomeSet s, RmgenRng rng)
        {
            s.MainHuntableAnimal = rng.PickRandom(new[]
            {
                "gaia/fauna_wildebeest", "gaia/fauna_zebra", "gaia/fauna_giraffe",
                "gaia/fauna_elephant_african_bush", "gaia/fauna_gazelle",
            });
            s.Tree3 = rng.PickRandom(new[] { "gaia/tree/baobab_4_dead", "gaia/tree/baobab_3_mature" });
            var t45 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/date_palm", "gaia/tree/acacia" },
                new[] { "gaia/tree/date_palm", "gaia/tree/palm_doum" },
                new[] { "gaia/tree/baobab_3_mature", "gaia/tree/bush_tropic" },
            });
            s.Tree4 = t45[0]; s.Tree5 = t45[1];
        }

        /// <summary>aegean.js:tree1/2 单组合(注意单元素 pickRandom 也消费一次抽数,
        /// 与上游 draw 计数一致)+ tree3 五选一 + tree4/5 单组合 + fruitBush 二选一。</summary>
        private static void OverlayAegean(BiomeSet s, RmgenRng rng)
        {
            var t12 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/cypress_wild", "gaia/tree/pine_maritime_short", "gaia/tree/cretan_date_palm_tall" },
            });
            s.Tree1 = t12[0]; s.Tree2 = t12[1];
            s.Tree3 = rng.PickRandom(new[]
            {
                "gaia/tree/olive", "gaia/tree/juniper_prickly", "gaia/tree/date_palm",
                "gaia/tree/cretan_date_palm_short", "gaia/tree/medit_fan_palm",
            });
            var t45 = rng.PickRandom(new[]
            {
                new[] { "gaia/tree/poplar_lombardy", "gaia/tree/carob", "gaia/tree/medit_fan_palm", "gaia/tree/cretan_date_palm_tall" },
            });
            s.Tree4 = t45[0]; s.Tree5 = t45[1];
            s.FruitBush = rng.PickRandom(new[] { "gaia/fruit/berry_01", "gaia/fruit/grapes" });
        }
    }
}
