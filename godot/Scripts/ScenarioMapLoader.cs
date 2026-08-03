using Godot;
using System.Collections.Generic;
using System.Xml.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed class ScenarioMapLoader
{
    public sealed class ScenarioEntity
    {
        public string Template = "";
        public float X, Z;
        public int Player;
        public float Orientation;
        public bool IsActor;       // actor| 前缀：纯装饰物（不进 sim，只放视觉）
        public bool IsTrigger;     // trigger| 前缀：触发器区域
        public bool IsSkirmish;    // skirmish| 前缀：skirmish 替换实体
    }

    public sealed class ScenarioData
    {
        public PmpMap? Terrain;
        public List<ScenarioEntity> Entities = new();
        public string Name = "";
        public string Description = "";
        public string MapType = "scenario";  // scenario/skirmish
        public List<PlayerData> PlayerData = new();
    }

    public sealed class PlayerData
    {
        public string Civ = "athen";
        public int Team = -1;
    }

    public static ScenarioData Load(string baseName, string mapsRoot)
    {
        var data = new ScenarioData();
        string pmpPath = System.IO.Path.Combine(mapsRoot, baseName + ".pmp");
        string xmlPath = System.IO.Path.Combine(mapsRoot, baseName + ".xml");

        if (System.IO.File.Exists(pmpPath))
            data.Terrain = PmpMap.Load(pmpPath);

        if (!System.IO.File.Exists(xmlPath))
            return data;

        var doc = XDocument.Load(xmlPath);

        var settings = doc.Root?.Element("ScriptSettings");
        if (settings != null)
        {
            var json = settings.Value.Trim();
            var nameMatch = System.Text.RegularExpressions.Regex.Match(json, @"""Name""\s*:\s*""([^""]+)""");
            if (nameMatch.Success) data.Name = nameMatch.Groups[1].Value;
            var descMatch = System.Text.RegularExpressions.Regex.Match(json, @"""Description""\s*:\s*""([^""]+)""");
            if (descMatch.Success) data.Description = descMatch.Groups[1].Value;
        }

        foreach (var entEl in doc.Root?.Elements("Entity") ?? new List<XElement>())
        {
            var template = entEl.Element("Template")?.Value ?? "";
            if (string.IsNullOrEmpty(template)) continue;

            // 分类标记（不再跳过——全部加载，sim 侧按需处理）
            bool isActor = template.StartsWith("actor|");
            bool isTrigger = template.StartsWith("trigger|");
            bool isSkirmish = template.StartsWith("skirmish|");

            var posEl = entEl.Element("Position");
            var playerEl = entEl.Element("Player");
            var orientEl = entEl.Element("Orientation");

            float x = 0, z = 0;
            if (posEl != null)
            {
                x = float.TryParse(posEl.Attribute("x")?.Value, out var px) ? px : 0;
                z = float.TryParse(posEl.Attribute("z")?.Value, out var pz) ? pz : 0;
            }

            int player = 0;
            if (playerEl != null && int.TryParse(playerEl.Value, out var pp))
                player = pp;

            float orient = 0;
            if (orientEl != null && float.TryParse(orientEl.Attribute("y")?.Value, out var oy))
                orient = oy;

            data.Entities.Add(new ScenarioEntity
            {
                Template = template,
                X = x, Z = z,
                Player = player,
                Orientation = orient,
                IsActor = isActor,
                IsTrigger = isTrigger,
                IsSkirmish = isSkirmish,
            });
        }

        GD.Print($"ScenarioMapLoader: loaded {data.Entities.Count} entities from {baseName} " +
                 $"(actors:{data.Entities.Count(e => e.IsActor)}, " +
                 $"triggers:{data.Entities.Count(e => e.IsTrigger)}, " +
                 $"skirmish:{data.Entities.Count(e => e.IsSkirmish)})");
        return data;
    }
}
