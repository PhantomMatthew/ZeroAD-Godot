using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace ZeroAD.Sim.Content
{
    public sealed class ScenarioEntityDef
    {
        public uint Uid;
        public string Template = "";
        public int Player = -1;
        public float X;
        public float Z;
        public float OrientationY;
        public bool IsActor;
        public bool IsSimulationEntity;
    }

    public sealed class ScenarioPlayerData
    {
        public int PlayerId;
        public string Civ = "";
        public string Name = "";
        public int Wood;
        public int Food;
        public int Stone;
        public int Metal;
        /// <summary>Team id from the scenario's PlayerData (-1 = no team / solo). Drives
        /// alliance shared-LOS seeding (same team → mutual ally). Default -1.</summary>
        public int Team = -1;
    }

    public sealed class ScenarioData
    {
        public string Name = "";
        public string Description = "";
        public List<ScenarioEntityDef> Entities = new();
        public List<ScenarioPlayerData> Players = new();
        public float CameraX;
        public float CameraY;
        public float CameraZ;
    }

    public static class ScenarioLoader
    {
        public static ScenarioData Load(string xmlPath)
        {
            var doc = XDocument.Load(xmlPath);
            var root = doc.Root ?? throw new InvalidDataException("Missing scenario root");
            var data = new ScenarioData();

            var settingsEl = root.Element("ScriptSettings");
            if (settingsEl != null)
                ParseScriptSettings(settingsEl.Value, data);

            var camera = root.Element("Camera");
            if (camera != null)
            {
                var pos = camera.Element("Position");
                if (pos != null)
                {
                    data.CameraX = ParseFloat(pos.Attribute("x")?.Value);
                    data.CameraY = ParseFloat(pos.Attribute("y")?.Value);
                    data.CameraZ = ParseFloat(pos.Attribute("z")?.Value);
                }
            }

            var entities = root.Element("Entities");
            if (entities != null)
            {
                foreach (var ent in entities.Elements("Entity"))
                    data.Entities.Add(ParseEntity(ent));
            }

            return data;
        }

        private static void ParseScriptSettings(string json, ScenarioData data)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Name", out var name))
                data.Name = name.GetString() ?? "";
            if (root.TryGetProperty("Description", out var desc))
                data.Description = desc.GetString() ?? "";

            if (!root.TryGetProperty("PlayerData", out var players))
                return;

            int playerIndex = 0;
            foreach (var player in players.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object)
                {
                    playerIndex++;
                    continue;
                }

                var pd = new ScenarioPlayerData { PlayerId = playerIndex };
                if (player.TryGetProperty("Civ", out var civ))
                    pd.Civ = civ.GetString() ?? "";
                if (player.TryGetProperty("Name", out var pname))
                    pd.Name = pname.GetString() ?? "";
                if (player.TryGetProperty("Team", out var team) && team.TryGetInt32(out var teamId))
                    pd.Team = teamId;
                if (player.TryGetProperty("Resources", out var res))
                {
                    pd.Wood = res.TryGetProperty("wood", out var w) ? w.GetInt32() : 0;
                    pd.Food = res.TryGetProperty("food", out var f) ? f.GetInt32() : 0;
                    pd.Stone = res.TryGetProperty("stone", out var s) ? s.GetInt32() : 0;
                    pd.Metal = res.TryGetProperty("metal", out var m) ? m.GetInt32() : 0;
                }
                data.Players.Add(pd);
                playerIndex++;
            }
        }

        private static ScenarioEntityDef ParseEntity(XElement ent)
        {
            var def = new ScenarioEntityDef
            {
                Uid = uint.TryParse(ent.Attribute("uid")?.Value, out var uid) ? uid : 0,
            };

            var template = ent.Element("Template")?.Value ?? "";
            def.Template = template.Replace('|', '/');
            def.IsActor = template.StartsWith("actor|", StringComparison.Ordinal);
            def.IsSimulationEntity = !def.IsActor &&
                (def.Template.StartsWith("gaia/", StringComparison.Ordinal) ||
                 def.Template.StartsWith("units/", StringComparison.Ordinal) ||
                 def.Template.StartsWith("structures/", StringComparison.Ordinal));

            var player = ent.Element("Player");
            if (player != null && int.TryParse(player.Value, out var pid))
                def.Player = pid;

            var pos = ent.Element("Position");
            if (pos != null)
            {
                def.X = ParseFloat(pos.Attribute("x")?.Value);
                def.Z = ParseFloat(pos.Attribute("z")?.Value);
            }

            var orient = ent.Element("Orientation");
            if (orient != null)
                def.OrientationY = ParseFloat(orient.Attribute("y")?.Value);

            return def;
        }

        private static float ParseFloat(string? value) =>
            float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

        public static string? FindScenarioPath(string dataRoot, string mapRelPath)
        {
            string rel = mapRelPath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.Combine(dataRoot, rel + ".xml");
            return File.Exists(full) ? full : null;
        }

        public static string? FindPmpPath(string dataRoot, string mapRelPath)
        {
            string rel = mapRelPath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.Combine(dataRoot, rel + ".pmp");
            return File.Exists(full) ? full : null;
        }
    }
}
