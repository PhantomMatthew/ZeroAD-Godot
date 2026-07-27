using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Content;

/// <summary>
/// 把 tech/aura JSON 的 <c>modifications[]</c> 数组派生成内核 <see cref="Modification"/> 列表。
/// 每项形态:<c>{ value, add?, multiply?, replace?, affects? }</c>。<c>value</c> 是数值路径
/// (如 <c>ResourceGatherer/Rates/food.grain</c>),<c>affects</c> 缺省回退到调用方传入的
/// <c>defaultAffects</c>(tech/aura 根级 affects)。
///
/// 由 <see cref="TechnologyLoader"/> 与 <see cref="AuraLoader"/> 共用,避免两份重复解析。
/// 对齐原版 <c>DeriveModificationsFromTech</c>(simulation/helpers/ModificationTemplates.js)。
/// </summary>
public static class ModificationParser
{
    /// <summary>从含 <c>modifications</c> 数组的 JSON 元素派生 Modification 列表。
    /// 无 modifications 节点或非数组 → 空列表(调用方按原样收下)。</summary>
    public static IReadOnlyList<Modification> Derive(JsonElement root, IReadOnlyList<string> defaultAffects)
    {
        var mods = new List<Modification>();
        if (!root.TryGetProperty("modifications", out var modsEl) || modsEl.ValueKind != JsonValueKind.Array)
            return mods;

        foreach (var m in modsEl.EnumerateArray())
        {
            if (!m.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;
            var affects = TryGetAffects(m, out var ma) ? ma : defaultAffects;
            mods.Add(new Modification(
                v.GetString()!,
                TryGetNumber(m, "add", out var a) ? a : null,
                TryGetNumber(m, "multiply", out var mu) ? mu : null,
                m.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                affects));
        }
        return mods;
    }

    /// <summary>affects 形态:string(可空格分词)或 string[]。统一成列表。公开:tech/aura
    /// 根级 affects 也走此解析(作为 <see cref="Derive"/> 的默认回退)。</summary>
    public static bool TryGetAffects(JsonElement el, out IReadOnlyList<string> affects)
    {
        affects = Array.Empty<string>();
        if (!el.TryGetProperty("affects", out var a)) return false;
        if (a.ValueKind == JsonValueKind.String)
        {
            affects = new List<string> { a.GetString()! };
            return true;
        }
        if (a.ValueKind == JsonValueKind.Array)
        {
            affects = a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList();
            return true;
        }
        return false;
    }

    /// <summary>数值字段读取(add/multiply/researchTime/radius 等)。internal:跨 loader
    /// 共用,不对外部程序集暴露。</summary>
    internal static bool TryGetNumber(JsonElement el, string key, out float value)
    {
        value = 0f;
        if (!el.TryGetProperty(key, out var n) || n.ValueKind != JsonValueKind.Number) return false;
        value = n.GetSingle();
        return true;
    }
}
