using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Sim.Components;

/// <summary>一条修改(对应 tech JSON modifications[] 的一项)。数值路径用 Add/Multiply;
/// Replace 为字符串类属性预留(数值路径忽略)。</summary>
public sealed record Modification(string Path, float? Add, float? Multiply,
    string? Replace, IReadOnlyList<string> Affects);

/// <summary>
/// 修正值管线(对齐原版 simulation/components/ModifiersManager.js)。
/// 存储 (属性路径, 目标实体) → modId → [mod];查询时先玩家级(目标=玩家实体)后实体级。
/// 合成:add 全加完再 multiply(顺序与插入无关,跨科技按 modId 排序,确定性)。
/// 派生态:不序列化,由 TechnologyManager 重放已研究科技重建。
/// </summary>
public sealed class ModifiersManager
{
    private readonly ComponentManager _cm;
    private readonly Dictionary<(string path, EntityId target), Dictionary<string, List<Modification>>> _storage = new();

    public ModifiersManager(ComponentManager cm) { _cm = cm; }

    /// <summary>同一 modId 对同一路径+目标重复添加 = 拒绝(原版 MultiKeyMap 语义)。</summary>
    public void AddModifiers(string modId, IReadOnlyList<Modification> mods, EntityId target)
    {
        foreach (var group in mods.GroupBy(m => m.Path))
        {
            var key = (group.Key, target);
            if (!_storage.TryGetValue(key, out var byId))
                byId = _storage[key] = new Dictionary<string, List<Modification>>();
            if (byId.ContainsKey(modId)) continue;
            byId[modId] = group.ToList();
        }
    }

    public void RemoveAllModifiers(string modId, EntityId target)
    {
        foreach (var key in _storage.Keys.Where(k => k.target == target).ToList())
        {
            var byId = _storage[key];
            if (byId.Remove(modId) && byId.Count == 0)
                _storage.Remove(key);
        }
    }

    /// <summary>实体值查询:先玩家级后实体级。无 Identity 短路返回 baseValue(原版同款)。</summary>
    public float Apply(string path, float baseValue, EntityId entity)
    {
        var identity = _cm.QueryInterface<IdentityComponent>(entity);
        if (identity == null) return baseValue;
        float value = baseValue;
        var owner = _cm.QueryInterface<OwnershipComponent>(entity);
        if (owner != null && owner.PlayerId > 0)
        {
            var playerEntity = _cm.GetPlayerEntityId(owner.PlayerId);
            if (playerEntity.HasValue)
                value = ApplyToTarget(path, value, identity.Classes, playerEntity.Value);
        }
        return ApplyToTarget(path, value, identity.Classes, entity);
    }

    /// <summary>模板值查询(单位未出生,如训练时间):只走玩家级。</summary>
    public float ApplyTemplate(string path, float baseValue, IReadOnlyList<string> classes, EntityId playerEntity)
        => ApplyToTarget(path, baseValue, classes, playerEntity);

    /// <summary>前缀查询(采集速率等子类型路径:wood → wood.tree/wood.ruins 全命中)。</summary>
    public float ApplyPrefix(string pathPrefix, float baseValue, EntityId entity)
    {
        var identity = _cm.QueryInterface<IdentityComponent>(entity);
        if (identity == null) return baseValue;
        float value = baseValue;
        var owner = _cm.QueryInterface<OwnershipComponent>(entity);
        if (owner != null && owner.PlayerId > 0)
        {
            var playerEntity = _cm.GetPlayerEntityId(owner.PlayerId);
            if (playerEntity.HasValue)
                value = ApplyPrefixToTarget(pathPrefix, value, identity.Classes, playerEntity.Value);
        }
        return ApplyPrefixToTarget(pathPrefix, value, identity.Classes, entity);
    }

    private float ApplyToTarget(string path, float value, IReadOnlyList<string> classes, EntityId target)
    {
        var mods = new List<(string modId, Modification mod)>();
        if (_storage.TryGetValue((path, target), out var byId))
            foreach (var modId in byId.Keys.OrderBy(k => k, StringComparer.Ordinal))
                foreach (var m in byId[modId]) mods.Add((modId, m));
        return Compose(mods, value, classes);
    }

    private float ApplyPrefixToTarget(string prefix, float value, IReadOnlyList<string> classes, EntityId target)
    {
        var mods = new List<(string modId, Modification mod)>();
        foreach (var key in _storage.Keys
                     .Where(k => k.target == target &&
                            (k.path == prefix || k.path.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                             k.path.StartsWith(prefix + ".", StringComparison.Ordinal)))
                     .OrderBy(k => k.path, StringComparer.Ordinal))
            foreach (var modId in _storage[key].Keys.OrderBy(k => k, StringComparer.Ordinal))
                foreach (var m in _storage[key][modId]) mods.Add((modId, m));
        return Compose(mods, value, classes);
    }

    private static float Compose(List<(string modId, Modification mod)> mods, float value, IReadOnlyList<string> classes)
    {
        if (mods.Count == 0) return value;
        foreach (var (_, m) in mods)
            if (m.Add.HasValue && AffectsMatch(m.Affects, classes)) value += m.Add.Value;
        foreach (var (_, m) in mods)
            if (m.Multiply.HasValue && AffectsMatch(m.Affects, classes)) value *= m.Multiply.Value;
        return value;
    }

    /// <summary>affects:空=生效;数组任一元素命中=生效;元素内空格分词 AND(原版 DoesModificationApply)。</summary>
    internal static bool AffectsMatch(IReadOnlyList<string> affects, IReadOnlyList<string> classes)
    {
        if (affects == null || affects.Count == 0) return true;
        foreach (var term in affects)
        {
            var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts.All(p => classes.Contains(p))) return true;
        }
        return false;
    }
}
