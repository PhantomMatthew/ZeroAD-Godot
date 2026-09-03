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
        /// <summary>预驻防(原版 MapReader &lt;Garrison&gt;):地图初始进驻本实体的单位 uid 表。</summary>
        public List<uint> InitGarrisonUids = new();
        /// <summary>预占炮塔(原版 &lt;Turrets&gt;):(点位名, 单位 uid) 对。</summary>
        public List<(string Point, uint Uid)> InitTurretPairs = new();
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
        /// <summary>场景相机航向(Camera/Rotation@angle,弧度;0 = 朝 +z 北)。</summary>
        public float CameraRotation;
        /// <summary>场景相机俯角(Camera/Declination@angle,弧度;正值向下看)。</summary>
        public float CameraDeclination;
        /// <summary>XML 含 &lt;Camera&gt; 元素(场景地图作者机位;含此才做开局相机恢复)。</summary>
        public bool HasCamera;
        /// <summary>胜利条件列表(EndGameManager;空 = 默认征服)。来自 ScriptSettings.VictoryConditions。</summary>
        public List<string> VictoryConditions = new();
        /// <summary>奇观胜利所需保有秒数(ScriptSettings.WonderVictoryDuration,分钟 → 秒;原版默认 10 分钟)。</summary>
        public float WonderVictoryDuration = 600f;
        /// <summary>圣物胜利所需保有秒数(ScriptSettings.RelicVictoryDuration,分钟 → 秒)。</summary>
        public float RelicVictoryDuration = 600f;
        /// <summary>停战秒数(ScriptSettings.Ceasefire,分钟 → 秒;0 = 无停战)。</summary>
        public float CeasefireDuration;
        /// <summary>锁定队伍(ScriptSettings.LockTeams,默认 false)。</summary>
        public bool LockTeams;
        /// <summary>最后一人站立模式(ScriptSettings.LastManStanding,默认 false)。
        /// 同盟共胜 = LockTeams || !LastManStanding(原版 Setup.js)。</summary>
        public bool LastManStanding;
        /// <summary>弑君英雄可驻军(ScriptSettings.RegicideGarrison,默认 false)。</summary>
        public bool RegicideGarrison;
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
                data.HasCamera = true;
                var pos = camera.Element("Position");
                if (pos != null)
                {
                    data.CameraX = ParseFloat(pos.Attribute("x")?.Value);
                    data.CameraY = ParseFloat(pos.Attribute("y")?.Value);
                    data.CameraZ = ParseFloat(pos.Attribute("z")?.Value);
                }
                // 航向/俯角(原版 GameView 开局视角由这三者直接决定;缺省 0)。
                data.CameraRotation = ParseFloat(camera.Element("Rotation")?.Attribute("angle")?.Value);
                data.CameraDeclination = ParseFloat(camera.Element("Declination")?.Attribute("angle")?.Value);
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

            // 胜利条件体系(EndGameManager 的 GameTypeSettings)。
            if (root.TryGetProperty("VictoryConditions", out var vc) && vc.ValueKind == JsonValueKind.Array)
            {
                foreach (var cond in vc.EnumerateArray())
                {
                    var s = cond.GetString();
                    if (!string.IsNullOrEmpty(s)) data.VictoryConditions.Add(s!);
                }
            }
            // 时长在 ScriptSettings 中以分钟存储(见 gamesetup 的 GameTypeSettings)。
            if (root.TryGetProperty("WonderVictoryDuration", out var wvd) && wvd.TryGetDouble(out var wmin))
                data.WonderVictoryDuration = (float)(wmin * 60.0);
            if (root.TryGetProperty("RelicVictoryDuration", out var rvd) && rvd.TryGetDouble(out var rmin))
                data.RelicVictoryDuration = (float)(rmin * 60.0);
            if (root.TryGetProperty("Ceasefire", out var cf) && cf.TryGetDouble(out var cmin))
                data.CeasefireDuration = (float)(cmin * 60.0);
            // 队伍/模式设置(原版 Setup.js:alliedVictory = LockTeams || !LastManStanding)。
            if (root.TryGetProperty("LockTeams", out var lt) && lt.ValueKind == JsonValueKind.True)
                data.LockTeams = true;
            if (root.TryGetProperty("LastManStanding", out var lms) && lms.ValueKind == JsonValueKind.True)
                data.LastManStanding = true;
            if (root.TryGetProperty("RegicideGarrison", out var rg) && rg.ValueKind == JsonValueKind.True)
                data.RegicideGarrison = true;

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
            // skirmish| 实体：在原版由 SkirmishReplacer 组件替换为对应文明的实际模板。
            // 简化处理：直接尝试替换为 structures/{civ}/civil_centre 或 units/{civ}/...
            // 当前不替换——作为普通实体加载（模板名含 skirmish/，sim 侧会忽略未知的）。
            // trigger| 实体：触发器区域，当前无触发器视觉——跳过（不影响 sim）。
            def.IsSimulationEntity = !def.IsActor && !template.StartsWith("trigger|") &&
                (def.Template.StartsWith("gaia/", StringComparison.Ordinal) ||
                 def.Template.StartsWith("units/", StringComparison.Ordinal) ||
                 def.Template.StartsWith("structures/", StringComparison.Ordinal) ||
                 def.Template.StartsWith("skirmish/", StringComparison.Ordinal));

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

            // 原版 MapReader.cpp:1052-1076:Garrison/Turrets 子元素(uid 引用)。
            var garrison = ent.Element("Garrison");
            if (garrison != null)
                foreach (var ge in garrison.Elements())
                    if (uint.TryParse(ge.Attribute("uid")?.Value, out var guid))
                        def.InitGarrisonUids.Add(guid);
            var turrets = ent.Element("Turrets");
            if (turrets != null)
                foreach (var tp in turrets.Elements())
                {
                    string point = tp.Attribute("turret")?.Value ?? "";
                    if (uint.TryParse(tp.Attribute("uid")?.Value, out var tuid))
                        def.InitTurretPairs.Add((point, tuid));
                }

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
