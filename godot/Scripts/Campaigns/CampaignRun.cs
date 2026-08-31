using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZeroAD.Godot.Campaigns;

// CampaignRun — 原版 gui/common/campaigns/CampaignRun.js 的 C# 端口。
// 一次战役"通关进度"存档:轻量 JSON(meta 用户描述 / data 进度数据 / template_identifier),
// 存 user://saves/campaigns/{filename}.0adcampaign(原版 saves/campaigns/ 同款文件名)。
// "当前 run" 记用户配置 currentcampaign(原版 ConfigDB 同名字段),主菜单 Continue Campaign 依此。
public sealed class CampaignRun
{
    /// <summary>存档文件名(不含目录/扩展;原版 new_modal 用 template_时间戳_随机数)。</summary>
    public required string Filename { get; init; }
    /// <summary>meta.userDescription — 用户给这次 run 起的名字。</summary>
    public string UserDescription { get; set; } = "";
    /// <summary>data.completedLevels — 已完成关卡 id 列表(markLevelComplete 追加)。</summary>
    public List<string> CompletedLevels { get; } = new();
    /// <summary>data 其余键原样往返(战役脚本的自定义进度数据,未来触发器接入用)。</summary>
    public JsonObject ExtraData { get; } = new();
    public CampaignTemplate? Template { get; set; }

    // ── 当前 run(原版 ConfigDB "currentcampaign")──
    private const string CurrentRunKey = "currentcampaign";

    /// <summary>UserConfig 读取委托(MainMenu/_Ready 注入,避免本类静态查询 autoload 节点)。
    /// 签名:(key)→user 值或 null。</summary>
    public static Func<string, string?>? ReadUserConfig;
    /// <summary>UserConfig 写入委托(key, value;value=null → 移除并保存)。</summary>
    public static Action<string, string?>? WriteUserConfig;

    public static string CurrentRunFilename => ReadUserConfig?.Invoke(CurrentRunKey) ?? "";
    public static bool HasCurrentRun => CurrentRunFilename.Length > 0;

    public static void ClearCurrentRun() => WriteUserConfig?.Invoke(CurrentRunKey, null);

    public CampaignRun SetCurrent()
    {
        WriteUserConfig?.Invoke(CurrentRunKey, Filename);
        return this;
    }

    public bool IsCurrent() => Filename == CurrentRunFilename;

    // ── 进度语义(原版 campaigns/default_menu/utils.js)──

    /// <summary>markLevelComplete:未完成才追加并落盘。</summary>
    public CampaignRun MarkLevelComplete(string levelId)
    {
        if (!IsCompleted(levelId))
        {
            CompletedLevels.Add(levelId);
            Save();
        }
        return this;
    }

    public bool IsCompleted(string levelId) => CompletedLevels.Contains(levelId);

    /// <summary>meetsRequirements:Requires 为空即满足;否则按 MatchesClassList 语义——
    /// 空格分隔 token,"-" 前缀 = 该关卡不得已完成,其余 = 必须已完成。</summary>
    public bool MeetsRequirements(CampaignLevel level)
    {
        if (string.IsNullOrWhiteSpace(level.Requires)) return true;
        foreach (var token in level.Requires.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('-'))
            {
                if (IsCompleted(token[1..])) return false;
            }
            else if (!IsCompleted(token))
                return false;
        }
        return true;
    }

    /// <summary>getLabel(full):full=false 且描述与模板名相同时只显示描述,
    /// 否则 "userDesc - templateName"(原版同款)。</summary>
    public string GetLabel(bool full = false)
    {
        string templateName = Template?.Name ?? "?";
        if (!full && UserDescription == templateName)
            return UserDescription;
        return $"{UserDescription} - {templateName}";
    }

    // ── 持久化 ──

    private static string SavesDir =>
        global::Godot.ProjectSettings.GlobalizePath("user://saves/campaigns/");

    public static string FilePathOf(string filename) =>
        Path.Combine(SavesDir, filename + ".0adcampaign");

    /// <summary>列出全部已存 run(坏档以 BrokenRun 占位返回,原版 LoadModal 同款容错)。</summary>
    public static List<CampaignRun> ListRuns(string? dataRoot)
    {
        var runs = new List<CampaignRun>();
        if (!Directory.Exists(SavesDir)) return runs;
        foreach (var file in Directory.GetFiles(SavesDir, "*.0adcampaign")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var run = Load(dataRoot, name);
            runs.Add(run ?? new CampaignRun { Filename = name, Broken = true });
        }
        return runs;
    }

    /// <summary>BrokenRun 等价物:加载失败的 run(可删除不可启动)。</summary>
    public bool Broken { get; private init; }

    public static CampaignRun? Load(string? dataRoot, string filename)
    {
        string path = FilePathOf(filename);
        if (!File.Exists(path)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var run = new CampaignRun { Filename = filename };
            if (node["meta"] is JsonObject meta
                && meta["userDescription"] is JsonValue desc
                && desc.TryGetValue<string>(out var d))
                run.UserDescription = d;
            if (node["data"] is JsonObject data)
            {
                if (data["completedLevels"] is JsonArray cl)
                    foreach (var e in cl)
                        if (e is JsonValue v && v.TryGetValue<string>(out var id))
                            run.CompletedLevels.Add(id);
                foreach (var kv in data)
                    if (kv.Key != "completedLevels" && kv.Value != null)
                        run.ExtraData[kv.Key] = kv.Value.DeepClone();
            }
            string templateId = node["template_identifier"]?.GetValue<string>() ?? "";
            run.Template = CampaignTemplate.GetTemplate(dataRoot, templateId);
            if (run.Template == null)
            {
                // 模板缺失(可能来自未移植 mod)——原版在此抛错 → 坏档处理。
                ZeroAD.Sim.Diag.Log("Campaign", $"run '{filename}': template '{templateId}' missing");
                return new CampaignRun { Filename = filename, Broken = true };
            }
            return run;
        }
        catch (Exception ex)
        {
            ZeroAD.Sim.Diag.Log("Campaign", $"run '{filename}' unreadable: {ex.Message}");
            return new CampaignRun { Filename = filename, Broken = true };
        }
    }

    public CampaignRun Save()
    {
        Directory.CreateDirectory(SavesDir);
        var data = new JsonObject();
        data["completedLevels"] = new JsonArray(CompletedLevels.Select(id => JsonValue.Create(id)).ToArray<JsonNode?>());
        foreach (var kv in ExtraData)
            data[kv.Key] = kv.Value?.DeepClone();
        var root = new JsonObject
        {
            ["data"] = data,
            ["meta"] = new JsonObject { ["userDescription"] = UserDescription },
            ["template_identifier"] = Template?.Identifier ?? "",
        };
        File.WriteAllText(FilePathOf(Filename), root.ToJsonString());
        return this;
    }

    public void Destroy()
    {
        string path = FilePathOf(Filename);
        if (File.Exists(path)) File.Delete(path);
        if (CurrentRunFilename == Filename) ClearCurrentRun();
    }
}
