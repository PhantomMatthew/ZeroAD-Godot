using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.AI;

/// <summary>AI 的 per-entity 元数据存储（原版 SharedScript._entityMetadata[playerId][entityId][key]）。
/// Petra 到处读写元数据：role（gatherer/builder/soldier）、subrole、plan、access（陆地区域 ID）、
/// base（隶属哪个基地）、transport、gather-type、garrison管理 等。
///
/// 存储模型：per AIComponent（即 per AI 玩家）一个 EntityMetadata 实例，按 EntityId 索引。
/// 值类型：string/int/float/bool（覆盖 Petra 的 99% 用例；复杂结构用 JSON-ish 字符串）。
/// 序列化：参与 OOS 哈希 + 存档（metadata 影响决策，各端必须一致）。</summary>
public sealed class EntityMetadata
{
    // key 用命名 tuple（entityId, key）。C# 的值类型 tuple 天然可比/可哈希。
    private readonly Dictionary<(uint entityId, string key), object> _store = new();
    // 注意：C# tuple 字段名在运行时保留，但遍历时需用 .entityId/.key（编译期有效）。

    public void Set(uint entityId, string key, object value) => _store[(entityId, key)] = value;

    public T? Get<T>(uint entityId, string key)
    {
        if (!_store.TryGetValue((entityId, key), out var v)) return default;
        return v is T t ? t : (T?)Convert.ChangeType(v, typeof(T));
    }

    public object? GetObject(uint entityId, string key)
        => _store.TryGetValue((entityId, key), out var v) ? v : null;

    public bool TryGet(uint entityId, string key, out object? value)
        => _store.TryGetValue((entityId, key), out value);

    public void Remove(uint entityId, string key) => _store.Remove((entityId, key));

    /// <summary>删除某实体的全部元数据（实体死亡时清理）。</summary>
    public void RemoveAll(uint entityId)
    {
        var keys = new List<(uint, string)>();
        foreach (var kvp in _store)
            if (kvp.Key.entityId == entityId) keys.Add(kvp.Key);
        foreach (var k in keys) _store.Remove(k);
    }

    /// <summary>序列化（OOS 哈希 + 存档）。逐条写 (entityId, key, valueType, value)。
    /// 顺序按 (entityId, key) 排序确保跨端逐位一致。</summary>
    public void Serialize(ISerializer s)
    {
        s.NumberU32("meta_n", (uint)_store.Count);
        foreach (var kvp in SortedEntries())
        {
            s.NumberU32("meta_eid", kvp.Key.Item1);
            s.StringASCII("meta_key", kvp.Key.Item2);
            WriteValue(s, "meta_val", kvp.Value);
        }
    }

    public void Deserialize(IDeserializer d)
    {
        _store.Clear();
        uint n = d.NumberU32("meta_n");
        for (uint i = 0; i < n; i++)
        {
            uint eid = d.NumberU32("meta_eid");
            string key = d.StringASCII("meta_key");
            object value = ReadValue(d, "meta_val");
            _store[(eid, key)] = value;
        }
    }

    private List<KeyValuePair<(uint, string), object>> SortedEntries()
    {
        var list = new List<KeyValuePair<(uint, string), object>>(_store);
        list.Sort((a, b) =>
        {
            int c = a.Key.Item1.CompareTo(b.Key.Item1);
            return c != 0 ? c : string.CompareOrdinal(a.Key.Item2, b.Key.Item2);
        });
        return list;
    }

    private static void WriteValue(ISerializer s, string name, object value)
    {
        switch (value)
        {
            case int i: s.StringASCII(name + "_t", "i"); s.NumberI32(name, i); break;
            case float f: s.StringASCII(name + "_t", "f"); s.NumberFixed(name, Maths.Fixed.FromFloat(f)); break;
            case bool b: s.StringASCII(name + "_t", "b"); s.Bool(name, b); break;
            default: s.StringASCII(name + "_t", "s"); s.StringASCII(name, value.ToString() ?? ""); break;
        }
    }

    private static object ReadValue(IDeserializer d, string name)
    {
        string t = d.StringASCII(name + "_t");
        return t switch
        {
            "i" => d.NumberI32(name),
            "f" => d.NumberFixed(name).ToFloat(),
            "b" => d.Bool(name),
            _ => d.StringASCII(name),
        };
    }
}
