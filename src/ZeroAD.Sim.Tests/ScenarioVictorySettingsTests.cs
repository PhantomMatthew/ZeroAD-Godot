using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZeroAD.Sim.Content;
using Xunit;

namespace ZeroAD.Sim.Tests;

// ScriptSettings 胜利条件解析(EndGameManager 注入源;原版 GameTypeSettings)。
public sealed class ScenarioVictorySettingsTests
{
    private static string WriteTempXml(string scriptSettingsJson)
    {
        string path = Path.Combine(Path.GetTempPath(), $"victory_test_{Path.GetRandomFileName()}.xml");
        string escaped = System.Security.SecurityElement.Escape(scriptSettingsJson) ?? "";
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Scenario>" +
            $"<ScriptSettings>{escaped}</ScriptSettings>" +
            "<Entities/></Scenario>");
        return path;
    }

    [Fact]
    public void ParsesVictoryConditionsAndDurations_MinutesToSeconds()
    {
        string path = WriteTempXml(
            "{\"Name\":\"T\",\"VictoryConditions\":[\"conquest\",\"wonder\",\"ceasefire\"]," +
            "\"WonderVictoryDuration\":20,\"RelicVictoryDuration\":15,\"Ceasefire\":30}");
        try
        {
            var data = ScenarioLoader.Load(path);
            Assert.Equal(new[] { "conquest", "wonder", "ceasefire" }, data.VictoryConditions);
            Assert.Equal(1200f, data.WonderVictoryDuration);
            Assert.Equal(900f, data.RelicVictoryDuration);
            Assert.Equal(1800f, data.CeasefireDuration);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingVictorySettings_KeepsDefaults()
    {
        string path = WriteTempXml("{\"Name\":\"T\"}");
        try
        {
            var data = ScenarioLoader.Load(path);
            Assert.Empty(data.VictoryConditions);   // 空 = 默认征服
            Assert.Equal(600f, data.WonderVictoryDuration);
            Assert.Equal(0f, data.CeasefireDuration);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyVictoryConditionsArray_StaysEmpty()
    {
        string path = WriteTempXml("{\"VictoryConditions\":[]}");
        try
        {
            var data = ScenarioLoader.Load(path);
            Assert.Empty(data.VictoryConditions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RealMaps_ParseVictoryConditions()
    {
        // 真实数据冒烟:全部 scenario/skirmish XML 都能解析,胜利条件只含已知值。
        // ("domination" 是更新版上游的条件,本上游树无实现——解析放行,sim 侧忽略未知条件。)
        string? dataRoot = FindDataRoot();
        if (dataRoot == null) return;   // 无 junction 环境(CI)跳过
        var known = new HashSet<string>
        {
            "conquest", "conquest_units", "conquest_civic_centers",
            "wonder", "capture_the_relic", "ceasefire", "regicide", "endless", "domination"
        };
        int withConditions = 0;
        foreach (string dir in new[] { "mods/public/maps/scenarios", "mods/public/maps/skirmishes" })
        {
            string full = Path.Combine(dataRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (string xml in Directory.GetFiles(full, "*.xml"))
            {
                var data = ScenarioLoader.Load(xml);
                foreach (string cond in data.VictoryConditions)
                    Assert.Contains(cond, known);
                if (data.VictoryConditions.Count > 0) withConditions++;
            }
        }
        Assert.True(withConditions > 0, "expected at least one map with explicit victory conditions");
    }

    private static string? FindDataRoot()
    {
        // 从测试程序集向上找 binaries/data(仓库 junction)。
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "binaries", "data");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
