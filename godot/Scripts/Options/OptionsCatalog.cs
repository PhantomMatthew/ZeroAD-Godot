using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Godot;

namespace ZeroAD.Godot.Options;

// options.json(端口自原版 gui/options/options.json)的反序列化模型 + 加载。
// 原版 JS 是动态对象,此处定型;字段语义逐一对齐 options.js 的 g_OptionType 使用方式。

/// <summary>下拉项。value 加载时统一字符串化(对齐原版 CGUIList 把数值全字符串化,
/// numeric 1.0→"1"),使 list_data.indexOf(configValue) 的字符串比对在 C# 侧等价。</summary>
public sealed record OptionListEntry(string Value, string Label, string? Tooltip);

/// <summary>依赖项(归一形):字符串形依赖(key,须 =="true")加载时归一为
/// {Config=key, Op="==", Value="true"};对象形 {config, op(缺省"=="), value(字符串化)}。</summary>
public sealed record OptionDependency(string Config, string Op, string Value);

public sealed class OptionDef
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public required string Tooltip { get; init; }
    public required string Config { get; init; }
    public string? Function { get; init; }
    /// <summary>min/max 原样(JSON 可 int/float/str,原版靠 Math 强制转换);tooltip 用原文。</summary>
    public string? MinRaw { get; init; }
    public string? MaxRaw { get; init; }
    public double TimeoutMs { get; init; }
    public IReadOnlyList<OptionListEntry>? List { get; init; }
    public IReadOnlyList<OptionDependency>? Dependencies { get; init; }

    /// <summary>数值约束(无则 ±Infinity,对齐原版 option.min ?? -Infinity)。</summary>
    public double MinValue => Coerce(MinRaw, double.NegativeInfinity);
    public double MaxValue => Coerce(MaxRaw, double.PositiveInfinity);

    private static double Coerce(string? raw, double dflt) =>
        raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : dflt;
}

public sealed class OptionCategory
{
    public required string Label { get; init; }
    public required string Tooltip { get; init; }
    public required IReadOnlyList<OptionDef> Options { get; init; }
}

public static class OptionsCatalog
{
    public const string JsonPath = "res://data/options.json";

    private static List<OptionCategory>? _cache;

    public static IReadOnlyList<OptionCategory> Categories => _cache ??= Load();

    private static List<OptionCategory> Load()
    {
        using var f = global::Godot.FileAccess.Open(JsonPath, global::Godot.FileAccess.ModeFlags.Read);
        if (f == null)
        {
            ZeroAD.Sim.Diag.Err("Options", $"OptionsCatalog: cannot open {JsonPath}");
            return new List<OptionCategory>();
        }

        using var doc = JsonDocument.Parse(f.GetAsText());
        var cats = new List<OptionCategory>();
        foreach (var catEl in doc.RootElement.EnumerateArray())
        {
            var options = new List<OptionDef>();
            foreach (var optEl in catEl.GetProperty("options").EnumerateArray())
                options.Add(ParseOption(optEl));
            cats.Add(new OptionCategory
            {
                Label = catEl.GetProperty("label").GetString() ?? "",
                Tooltip = catEl.GetProperty("tooltip").GetString() ?? "",
                Options = options,
            });
        }
        return cats;
    }

    private static OptionDef ParseOption(JsonElement el)
    {
        return new OptionDef
        {
            Type = el.GetProperty("type").GetString() ?? "",
            Label = el.GetProperty("label").GetString() ?? "",
            Tooltip = el.GetProperty("tooltip").GetString() ?? "",
            Config = el.GetProperty("config").GetString() ?? "",
            Function = el.TryGetProperty("function", out var fn) ? fn.GetString() : null,
            MinRaw = el.TryGetProperty("min", out var mn) ? Stringify(mn) : null,
            MaxRaw = el.TryGetProperty("max", out var mx) ? Stringify(mx) : null,
            TimeoutMs = el.TryGetProperty("timeout", out var to) ? to.GetDouble() : 0,
            List = el.TryGetProperty("list", out var list) ? ParseList(list) : null,
            Dependencies = el.TryGetProperty("dependencies", out var deps) ? ParseDependencies(deps) : null,
        };
    }

    private static List<OptionListEntry> ParseList(JsonElement list)
    {
        var entries = new List<OptionListEntry>();
        foreach (var item in list.EnumerateArray())
        {
            entries.Add(new OptionListEntry(
                Stringify(item.GetProperty("value")),
                ParseLabel(item.GetProperty("label")),
                item.TryGetProperty("tooltip", out var tip) ? ParseLabel(tip) : null));
        }
        return entries;
    }

    /// <summary>label/tooltip 可为纯字符串或 l10n 消息对象 {"_string": "...", "context": "..."}
    /// (如 shadowquality 四项);移植版无 l10n 系统,取 _string 原文。</summary>
    private static string ParseLabel(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Object when el.TryGetProperty("_string", out var s) => s.GetString() ?? "",
        _ => el.GetRawText(),
    };

    private static List<OptionDependency> ParseDependencies(JsonElement deps)
    {
        var result = new List<OptionDependency>();
        foreach (var dep in deps.EnumerateArray())
        {
            if (dep.ValueKind == JsonValueKind.String)
            {
                // 字符串形:该键须 == "true"(options.js isDependencyMet 的 string 分支)。
                result.Add(new OptionDependency(dep.GetString() ?? "", "==", "true"));
            }
            else
            {
                result.Add(new OptionDependency(
                    dep.GetProperty("config").GetString() ?? "",
                    dep.TryGetProperty("op", out var op) ? op.GetString() ?? "==" : "==",
                    Stringify(dep.GetProperty("value"))));
            }
        }
        return result;
    }

    /// <summary>对齐 JS 的隐式字符串化:number 1.0→"1"、0.5→"0.5"(.NET double 不变区域
    /// 格式与此一致);string 原样;bool 小写。config 值在 ConfigDB 中全为字符串,此转换使
    /// 下拉 list 值与 config 值可直接字符串比对。</summary>
    private static string Stringify(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => el.GetRawText(),
    };
}
