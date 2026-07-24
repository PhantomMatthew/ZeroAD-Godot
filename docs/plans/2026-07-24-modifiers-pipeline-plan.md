# 修正值管线 + 科技 JSON 化 实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 用数据驱动的 ModifiersManager 修正值管线替换死掉的硬编码科技系统,让科技/文明加成真正改变单位数值。

**Architecture:** 对齐原版 `ModifiersManager.js`:修改值按 `(属性路径, 目标实体)` 存储,查询时先玩家级后实体级合成(add→multiply→replace);科技从 `simulation/data/technologies/*.json` 加载;组件保留模板基值、用值时过 `Apply()`;派生态不序列化,由已研究科技名重放重建。设计文档:`docs/plans/2026-07-24-modifiers-pipeline-design.md`。

**Tech Stack:** C# / net8.0 / xUnit / System.Text.Json(无新依赖)。内核 `src/ZeroAD.Sim` 无 Godot 依赖,测试 `src/ZeroAD.Sim.Tests`。

**已核实的 JSON 事实(执行时不必再查):**
- pair 文件:`{ "genericName": "...", "pair": ["techA","techB"], "requirements": {"civ":"han"} }`
- autoResearch:`"autoResearch": true`(phase_village、civ 加成科技、单位升阶)— 满足 requirements 即自动免费研究
- `replaces: ["phase_town"]` / `supersedes: "phase_village"`:研究后这些名字也计入已研究(原版 `ResearchTechnology` 同款行为)
- 修改路径实例:`Attack/Ranged/Damage/Pierce`(multiply)、`Health/Max`、`ResourceGatherer/Rates/wood.tree`(multiply,子类型路径)、`Cost/BuildTime`(multiply)、`Population/Bonus`(add/multiply)、`Capturable/…`、`TerritoryInfluence/…`(本任务不接线,加载即可)
- requirements 形态:`{tech}` `{civ}` `{any:[…]}` `{all:[…]}`;`{entity:{class,number}}` **视为满足**(否则阶段科技永远无法研究,文档化 TODO)
- per-modification `"affects"` 覆盖 tech 级 `"affects"`;affects 为字符串(空格 AND)或字符串数组(任一命中)

**测试里的科技目录定位(所有测试文件共用此 helper):**

```csharp
private static string RepoDir(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
        dir = dir.Parent;
    Assert.True(dir != null, $"repo marker not found: {relative}");
    return Path.Combine(dir!.FullName, relative);
}
// 用法:RepoDir("binaries/data/mods/public/simulation/data/technologies")
```

---

### Task 1: Modification 模型 + ModifiersManager

**Files:**
- Create: `src/ZeroAD.Sim/Components/ModifiersManager.cs`
- Modify: `src/ZeroAD.Sim/ComponentManager.cs`(挂 `Modifiers` 属性)
- Test: `src/ZeroAD.Sim.Tests/ModifiersManagerTests.cs`

**Step 1: 写失败测试**

```csharp
// src/ZeroAD.Sim.Tests/ModifiersManagerTests.cs
using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

public sealed class ModifiersManagerTests
{
    private static (ComponentManager cm, EntityId playerEnt, EntityId unit) World()
    {
        var cm = new ComponentManager();
        var playerEnt = cm.CreatePlayerEntity(1); // 若签名不同按 ComponentManager 现有 API 调整
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new IdentityComponent { Classes = new List<string> { "Unit", "Soldier", "Melee" } });
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        return (cm, playerEnt, unit);
    }

    [Fact]
    public void Apply_ReturnsBase_WhenNoModifiers()
    {
        var (cm, _, unit) = World();
        Assert.Equal(10f, cm.Modifiers.Apply("Attack/Melee/Damage/Hack", 10f, unit));
    }

    [Fact]
    public void Apply_PlayerWide_MultiplyThenAdd_Order()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("tech_add", new[]
        {
            new Modification("Health/Max", Add: 20f, Multiply: null, Replace: null, Affects: new List<string>())
        }, playerEnt);
        cm.Modifiers.AddModifiers("tech_mul", new[]
        {
            new Modification("Health/Max", null, 1.5f, null, new List<string>())
        }, playerEnt);
        // add 先于 multiply:(100 + 20) * 1.5 = 180
        Assert.Equal(180f, cm.Modifiers.Apply("Health/Max", 100f, unit));
    }

    [Fact]
    public void Apply_AffectsFilter_MatchesSpaceSeparatedAnd()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[]
        {
            new Modification("Health/Max", null, 2f, null, new List<string> { "Soldier Melee" })
        }, playerEnt);
        Assert.Equal(200f, cm.Modifiers.Apply("Health/Max", 100f, unit));

        // 不匹配:unit 无 Ranged 类
        cm.Modifiers.AddModifiers("t2", new[]
        {
            new Modification("Health/Max", null, 3f, null, new List<string> { "Soldier Ranged" })
        }, playerEnt);
        Assert.Equal(200f, cm.Modifiers.Apply("Health/Max", 100f, unit)); // 仍只有 ×2
    }

    [Fact]
    public void Apply_NoIdentity_ReturnsBase()
    {
        var (cm, playerEnt, _) = World();
        var bare = cm.CreateEntity(); // 无 Identity
        cm.Modifiers.AddModifiers("t", new[]
        {
            new Modification("Health/Max", null, 2f, null, new List<string>())
        }, playerEnt);
        Assert.Equal(100f, cm.Modifiers.Apply("Health/Max", 100f, bare));
    }

    [Fact]
    public void Apply_EntityLocal_OverridesAfterPlayerWide()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("pw", new[]
        {
            new Modification("Health/Max", 10f, null, null, new List<string>())
        }, playerEnt);
        cm.Modifiers.AddModifiers("aura", new[]
        {
            new Modification("Health/Max", 100f, null, null, new List<string>())
        }, unit);
        // 玩家级 add 10 与实体级 add 100 叠加:(50+10+100)
        Assert.Equal(160f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void AddModifiers_SameModId_Rejected()
    {
        var (cm, playerEnt, unit) = World();
        var mods = new[] { new Modification("Health/Max", 5f, null, null, new List<string>()) };
        cm.Modifiers.AddModifiers("t", mods, playerEnt);
        cm.Modifiers.AddModifiers("t", mods, playerEnt); // 重复
        Assert.Equal(55f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void RemoveAllModifiers_RemovesByModId()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[]
        {
            new Modification("Health/Max", 5f, null, null, new List<string>())
        }, playerEnt);
        cm.Modifiers.RemoveAllModifiers("t", playerEnt);
        Assert.Equal(50f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }

    [Fact]
    public void ApplyPrefix_MatchesSubPaths()
    {
        var (cm, playerEnt, unit) = World();
        cm.Modifiers.AddModifiers("t", new[]
        {
            new Modification("ResourceGatherer/Rates/wood.tree", null, 1.15f, null, new List<string>()),
            new Modification("ResourceGatherer/Rates/wood.ruins", null, 1.15f, null, new List<string>()),
            new Modification("ResourceGatherer/Rates/food.grain", null, 9f, null, new List<string>())
        }, playerEnt);
        // 两条 wood.* 都命中前缀,food 不命中:10 × 1.15 × 1.15
        Assert.Equal(10f * 1.15f * 1.15f, cm.Modifiers.ApplyPrefix("ResourceGatherer/Rates/wood", 10f, unit), 3);
    }

    [Fact]
    public void Deterministic_CrossTechOrder_SortedByModId()
    {
        var (cm, playerEnt, unit) = World();
        // 反序插入,结果必须一致(排序固定)
        cm.Modifiers.AddModifiers("b_mul", new[] { new Modification("Health/Max", null, 2f, null, new List<string>()) }, playerEnt);
        cm.Modifiers.AddModifiers("a_add", new[] { new Modification("Health/Max", 10f, null, null, new List<string>()) }, playerEnt);
        Assert.Equal((50f + 10f) * 2f, cm.Modifiers.Apply("Health/Max", 50f, unit));
    }
}
```

**Step 2: 跑测试确认编译失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter ModifiersManagerTests`
Expected: 编译错误(`Modification`/`Modifiers` 不存在)

**Step 3: 实现**

`src/ZeroAD.Sim/Components/ModifiersManager.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Sim.Components;

/// <summary>一条修改(对应 tech JSON modifications[] 的一项)。数值路径用 Add/Multiply;
/// Replace 为字符串类属性预留(数值路径忽略)。</summary>
public sealed record Modification(string Path, float? Add, float? Multiply,
    string? Replace, IReadOnlyList<string> Affects);

/// <summary>
/// 修正值管线(对齐原版 ModifiersManager.js)。存储 (属性路径, 目标实体) → modId → [mod]。
/// 查询:先玩家级(目标=玩家实体)后实体级;affects 过滤;add 全加完再 multiply。
/// 跨科技按 modId 排序,顺序与插入无关(确定性)。派生态:不序列化。
/// </summary>
public sealed class ModifiersManager
{
    private readonly ComponentManager _cm;
    private readonly Dictionary<(string path, EntityId target), Dictionary<string, List<Modification>>> _storage = new();

    public ModifiersManager(ComponentManager cm) { _cm = cm; }

    public void AddModifiers(string modId, IReadOnlyList<Modification> mods, EntityId target)
    {
        foreach (var group in mods.GroupBy(m => m.Path))
        {
            var key = (group.Key, target);
            if (!_storage.TryGetValue(key, out var byId))
                byId = _storage[key] = new Dictionary<string, List<Modification>>();
            if (byId.ContainsKey(modId)) continue; // 原版 MultiKeyMap:同 modId 拒绝重复
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
        var classes = identity.Classes;
        float value = baseValue;
        var owner = _cm.QueryInterface<OwnershipComponent>(entity);
        if (owner != null && owner.PlayerId > 0)
        {
            var playerEntity = _cm.GetPlayerEntityId(owner.PlayerId);
            if (playerEntity.HasValue)
                value = ApplyToTarget(path, value, classes, playerEntity.Value);
        }
        return ApplyToTarget(path, value, classes, entity);
    }

    /// <summary>模板值查询(单位未出生,如训练时间):只走玩家级。</summary>
    public float ApplyTemplate(string path, float baseValue, IReadOnlyList<string> classes, EntityId playerEntity)
        => ApplyToTarget(path, baseValue, classes, playerEntity);

    /// <summary>前缀查询(采集速率等子类型路径:wood → wood.tree/wood.ruins 全命中)。</summary>
    public float ApplyPrefix(string pathPrefix, float baseValue, EntityId entity)
    {
        float value = baseValue;
        var identity = _cm.QueryInterface<IdentityComponent>(entity);
        if (identity == null) return baseValue;
        var owner = _cm.QueryInterface<OwnershipComponent>(entity);
        if (owner != null && owner.PlayerId > 0)
        {
            var pe = _cm.GetPlayerEntityId(owner.PlayerId);
            if (pe.HasValue)
                value = ApplyPrefixToTarget(pathPrefix, value, identity.Classes, pe.Value);
        }
        return ApplyPrefixToTarget(pathPrefix, value, identity.Classes, entity);
    }

    private float ApplyPrefixToTarget(string prefix, float value, IReadOnlyList<string> classes, EntityId target)
    {
        var mods = new List<(string modId, Modification mod)>();
        foreach (var key in _storage.Keys.Where(k => k.target == target &&
                     (k.path == prefix || k.path.StartsWith(prefix + "/", StringComparison.Ordinal)))
                     .OrderBy(k => k.path, StringComparer.Ordinal))
            foreach (var modId in _storage[key].Keys.OrderBy(k => k, StringComparer.Ordinal))
                foreach (var m in _storage[key][modId]) mods.Add((modId, m));
        return Compose(mods, value, classes);
    }

    private float ApplyToTarget(string path, float value, IReadOnlyList<string> classes, EntityId target)
    {
        var mods = new List<(string modId, Modification mod)>();
        if (_storage.TryGetValue((path, target), out var byId))
            foreach (var modId in byId.Keys.OrderBy(k => k, StringComparer.Ordinal))
                foreach (var m in byId[modId]) mods.Add((modId, m));
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
```

`ComponentManager.cs` 添加(构造函数里初始化):

```csharp
public Components.ModifiersManager Modifiers { get; }
// ctor 内: Modifiers = new Components.ModifiersManager(this);
```

**Step 4: 跑测试确认通过**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter ModifiersManagerTests`
Expected: 9 passed

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/Components/ModifiersManager.cs src/ZeroAD.Sim/ComponentManager.cs src/ZeroAD.Sim.Tests/ModifiersManagerTests.cs
git commit -m "feat(sim): ModifiersManager 修正值存储/查询管线(玩家级+实体级,affects 过滤)"
```

---

### Task 2: TechnologyLoader(JSON + pair + requirements)

**Files:**
- Create: `src/ZeroAD.Sim/Content/TechnologyLoader.cs`
- Test: `src/ZeroAD.Sim.Tests/TechnologyLoaderTests.cs`

**Step 1: 写失败测试**

```csharp
// src/ZeroAD.Sim.Tests/TechnologyLoaderTests.cs
using System;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Content;

public sealed class TechnologyLoaderTests
{
    private static string TechDir() => RepoDir("binaries/data/mods/public/simulation/data/technologies");
    private static string RepoDir(string relative) { /* 见计划头部 helper */ throw null!; }

    [Fact]
    public void Loads_AllJsonFiles()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Technologies.Count > 50, $"expected dozens of techs, got {defs.Technologies.Count}");
        Assert.True(defs.Technologies.ContainsKey("phase_town_generic"));
    }

    [Fact]
    public void Parses_Cost_Time_Modifications()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        Assert.Equal(200, t.Wood);
        Assert.Equal(100, t.Metal);
        Assert.Equal(20f, t.ResearchTime);
        Assert.Contains(t.Modifications, m => m.Path == "Attack/Ranged/Damage/Pierce" && m.Multiply == 1.15f);
    }

    [Fact]
    public void Parses_TechLevelAffects_AsDefault()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        // tech 级 affects: ["Soldier"] 落到每条 mod
        Assert.All(t.Modifications, m => Assert.Contains("Soldier", m.Affects));
    }

    [Fact]
    public void Parses_PerModAffects_OverridesTechLevel()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["phase_town_generic"];
        var territory = t.Modifications.First(m => m.Path == "TerritoryInfluence/Radius");
        Assert.Contains("CivCentre", territory.Affects);
    }

    [Fact]
    public void Parses_AutoResearch_And_Supersedes_Replaces()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Technologies["phase_village"].AutoResearch);
        Assert.Equal("phase_village", defs.Technologies["phase_town_generic"].Supersedes);
        Assert.Contains("phase_town", defs.Technologies["phase_town_generic"].Replaces);
    }

    [Fact]
    public void Parses_Pairs()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Pairs.Count > 0);
        Assert.Contains(defs.Pairs, p => p.Value.Contains("civil_service_01") && p.Value.Contains("civil_service_02"));
    }

    [Fact]
    public void Parses_Requirements_TechCivAny()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        Assert.Contains(t.Requirements, r => r.Tech == "phase_town");
    }
}
```

**Step 2: 跑测试确认编译失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter TechnologyLoaderTests`
Expected: 编译错误

**Step 3: 实现**

`src/ZeroAD.Sim/Content/TechnologyLoader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Content;

public sealed record TechRequirement(string? Tech, string? Civ,
    IReadOnlyList<TechRequirement>? Any, IReadOnlyList<TechRequirement>? All);
// {entity:{class,number}} 不建模:解析时视为满足(否则阶段科技无法研究,见设计文档)

public sealed record TechnologyDefinition(
    string Name, string GenericName,
    int Wood, int Food, int Stone, int Metal, float ResearchTime,
    IReadOnlyList<TechRequirement> Requirements,
    IReadOnlyList<Modification> Modifications,
    bool AutoResearch,
    string? Supersedes,
    IReadOnlyList<string> Replaces);

public sealed record TechCatalog(
    IReadOnlyDictionary<string, TechnologyDefinition> Technologies,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Pairs); // pair文件名 → [techA, techB]

public static class TechnologyLoader
{
    public static TechCatalog LoadAll(string technologiesDir)
    {
        var techs = new Dictionary<string, TechnologyDefinition>();
        var pairs = new Dictionary<string, IReadOnlyList<string>>();
        if (!Directory.Exists(technologiesDir)) return new TechCatalog(techs, pairs);

        foreach (var file in Directory.GetFiles(technologiesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("pair", out var pairEl) && pairEl.ValueKind == JsonValueKind.Array)
                {
                    pairs[name] = pairEl.EnumerateArray().Select(e => e.GetString()!).ToList();
                    continue;
                }
                techs[name] = ParseTech(name, root);
            }
            catch { /* 单个坏文件不阻塞整体(与模板加载同款容错) */ }
        }
        return new TechCatalog(techs, pairs);
    }

    private static TechnologyDefinition ParseTech(string name, JsonElement root)
    {
        var cost = root.TryGetProperty("cost", out var c) ? c : default;
        // tech 级 affects(字符串或数组)→ 每条 mod 的默认值;per-mod affects 覆盖
        var techAffects = ParseAffects(root, out _);
        var mods = new List<Modification>();
        if (root.TryGetProperty("modifications", out var modsEl))
            foreach (var m in modsEl.EnumerateArray())
            {
                var affects = ParseAffects(m, out var has) ? GetAffects(m) : techAffects;
                mods.Add(new Modification(
                    m.GetProperty("value").GetString()!,
                    m.TryGetProperty("add", out var a) ? a.GetSingle() : null,
                    m.TryGetProperty("multiply", out var mu) ? mu.GetSingle() : null,
                    m.TryGetProperty("replace", out var r) ? r.GetString() : null,
                    affects));
            }
        return new TechnologyDefinition(
            name,
            root.TryGetProperty("genericName", out var g) ? g.GetString() ?? name : name,
            GetInt(cost, "wood"), GetInt(cost, "food"), GetInt(cost, "stone"), GetInt(cost, "metal"),
            root.TryGetProperty("researchTime", out var t) ? t.GetSingle() : 0f,
            ParseRequirements(root),
            mods,
            root.TryGetProperty("autoResearch", out var ar) && ar.GetBoolean(),
            root.TryGetProperty("supersedes", out var su) ? su.GetString() : null,
            root.TryGetProperty("replaces", out var re) ? re.EnumerateArray().Select(e => e.GetString()!).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>());
    }
    // ParseAffects/GetAffects:处理 affects 为 string 或 string[] 两种形态
    // ParseRequirements:tech/civ 为 string;any/all 为数组递归;entity → 忽略(视为满足)
    // GetInt(JsonElement cost, string key):cost 缺省 → 0
}
```

(helper 方法体直白,执行时补全;`ParseAffects(JsonElement, out bool)` 返回是否存在 affects 键,`GetAffects` 统一成 `IReadOnlyList<string>`。)

**Step 4: 跑测试确认通过**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter TechnologyLoaderTests`
Expected: 7 passed

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/Content/TechnologyLoader.cs src/ZeroAD.Sim.Tests/TechnologyLoaderTests.cs
git commit -m "feat(sim): 科技 JSON 加载器(cost/requirements/modifications/pair/autoResearch)"
```

---

### Task 3: TechnologyManager 重写 + civ 起始科技 + 重放序列化

**Files:**
- Modify: `src/ZeroAD.Sim/Components/Technology.cs`(TechnologyManager 重写;ResearcherComponent 签名调整)
- Modify: `src/ZeroAD.Sim/Components/Production.cs`:`PlayerComponent` 加 `Civ` 字段(序列化 `"civ"`,默认 `"athen"`)
- Modify: `godot/Scripts/SimBridge.cs`:InitWorld 注入科技目录 + 设置 civ + 玩家初始化后跑 autoResearch;researcher.Tick 调用点(约 752 行)补 cm 参数 + 完成后跑 autoResearch
- Test: `src/ZeroAD.Sim.Tests/TechnologyManagerTests.cs`

**Step 1: 写失败测试**(用真实 JSON + 手工小目录双轨)

```csharp
[Fact] public void CanResearch_BlockedUntilPrereq() { /* soldier_attack_ranged_01 需 phase_town:
    直接 CanResearch=false;ApplyResearch("phase_town_generic") 后(replaces 含 phase_town)= true */ }
[Fact] public void ApplyResearch_AppliesModsToPlayerEntity() { /* 研究后 cm.Modifiers.Apply 攻击路径 ×1.15 */ }
[Fact] public void ApplyResearch_MarksReplacesAndSupersedes() { /* phase_town_generic 研究后
    IsResearched("phase_town")==true 且 IsResearched("phase_village")==true */ }
[Fact] public void Pair_ResearchingOne_LocksOther() { /* civil_service_01 研究后 CanResearch(civil_service_02)=false,
    且 pair 伪科技名 IsResearched("pair_unlock_civil_service_han")==true */ }
[Fact] public void AutoResearch_RunsAtInit() { /* 新玩家 UpdateAutoResearch 后 IsResearched("phase_village")==true */ }
[Fact] public void AutoResearch_CivGated() { /* civ=athen 不研究 requirements civ=han 的 autoResearch 科技 */ }
[Fact] public void SerializeDeserialize_ReplayRebuildsModifiers() { /* 研究若干科技→序列化→新 manager 反序列化
    +RebuildModifiers→两个 cm 的 Apply 结果逐值一致 */ }
[Fact] public void StartResearch_Refuses_WhenLocked() { /* ResearcherComponent.StartResearch 调 CanResearch */ }
[Fact] public void StartResearch_ChargesAllFourResources() { /* stone/metal 也校验+扣除 */ }
```

**Step 2: 跑测试确认失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter TechnologyManagerTests`
Expected: 编译错误/失败

**Step 3: 实现**

`Technology.cs` 重写要点(完整保留 ResearcherComponent 外壳):

```csharp
[Component("TechnologyManager", "TechnologyManager")]
public sealed class TechnologyManager : ComponentBase, IComponentMessageHandler
{
    private readonly HashSet<string> _researched = new();
    private readonly Dictionary<string, string> _pairOf = new();   // tech → pairName
    private readonly HashSet<string> _lockedByPair = new();
    private TechCatalog _catalog = new(new Dictionary<string, TechnologyDefinition>(),
                                       new Dictionary<string, IReadOnlyList<string>>());
    private string _civ = "athen";

    public IReadOnlySet<string> Researched => _researched;

    /// <summary>注入数据目录(构造后、研究前调用)。civ 用于 requirements {civ} 判定。</summary>
    public void Configure(TechCatalog catalog, string civ)
    {
        _catalog = catalog;
        _civ = civ;
        _pairOf.Clear();
        foreach (var (pairName, members) in catalog.Pairs)
            foreach (var m in members) _pairOf[m] = pairName;
    }

    public bool IsResearched(string tech) => _researched.Contains(tech);

    public bool CanResearch(string tech)
    {
        if (!_catalog.Technologies.TryGetValue(tech, out var def)) return false;
        if (_researched.Contains(tech) || _lockedByPair.Contains(tech)) return false;
        return def.Requirements.All(ReqMet);
    }

    private bool ReqMet(TechRequirement r)
    {
        if (r.Tech != null) return _researched.Contains(r.Tech);
        if (r.Civ != null) return string.Equals(r.Civ, _civ, StringComparison.OrdinalIgnoreCase);
        if (r.Any != null) return r.Any.Any(ReqMet);
        if (r.All != null) return r.All.All(ReqMet);
        return true; // entity 等其他形态:视为满足(设计文档 §5)
    }

    /// <summary>研究落地(免费路径):标记已研究(含 replaces/supersedes/pair 伪科技),
    /// 修改值写入 ModifiersManager(目标=本组件所在玩家实体)。cm 用于取 Modifiers。</summary>
    public void ApplyResearch(string techName, ComponentManager cm)
    {
        if (!_catalog.Technologies.TryGetValue(techName, out var def)) return;
        if (_researched.Contains(techName)) return;
        MarkResearched(techName, def);
        cm.Modifiers.AddModifiers(techName, def.Modifications, Entity);
    }

    private void MarkResearched(string techName, TechnologyDefinition def)
    {
        _researched.Add(techName);
        foreach (var r in def.Replaces) _researched.Add(r);
        if (def.Supersedes != null) _researched.Add(def.Supersedes);
        if (_pairOf.TryGetValue(techName, out var pairName))
        {
            _researched.Add(pairName); // 原版:pair 伪科技在任一成员研究后视为已研究
            foreach (var member in _catalog.Pairs[pairName])
                if (member != techName) _lockedByPair.Add(member);
        }
    }

    /// <summary>autoResearch 扫描(原版 UpdateAutoResearch):满足条件即免费研究。
    /// 排序遍历保证确定性;返回本次新研究的科技名(供调用方做血量重算等后续)。</summary>
    public IReadOnlyList<string> UpdateAutoResearch(ComponentManager cm)
    {
        var done = new List<string>();
        foreach (var name in _catalog.Technologies.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var def = _catalog.Technologies[name];
            if (!def.AutoResearch || _researched.Contains(name)) continue;
            if (def.Requirements.All(ReqMet)) { ApplyResearch(name, cm); done.Add(name); }
        }
        return done;
    }

    /// <summary>反序列化后重放(派生态重建):按科技名排序重新 ApplyResearch。</summary>
    public void RebuildModifiers(ComponentManager cm)
    {
        foreach (var name in _researched.OrderBy(k => k, StringComparer.Ordinal))
            if (_catalog.Technologies.TryGetValue(name, out var def))
                cm.Modifiers.AddModifiers(name, def.Modifications, Entity);
    }

    // Serialize/Deserialize 保持现有格式(count + 逐个 tech 名);Deserialize 只回填 _researched
    // 与 _lockedByPair(由 pair 推导),不动 Modifiers(由 RebuildModifiers 统一重建)。
}
```

`ResearcherComponent`:`StartResearch` 增加 `if (!techMgr.CanResearch(techName)) return false;` 并把资源校验补全四样(`player.Stone < tech.StoneCost || player.Metal < tech.MetalCost` 也 return false);`Tick(float dt, TechnologyManager techMgr)` → `Tick(float dt, TechnologyManager techMgr, ComponentManager cm)`,完成时 `techMgr.ApplyResearch(_currentTech, cm)`。定义查询从 `techMgr.Available` 改为 `techMgr.GetDefinition(name)`(提供该访问器)。

`PlayerComponent`:`public string Civ = "athen";` + Serialize `s.StringASCII("civ", Civ)` + Deserialize 读回。

`SimBridge`:
- `InitWorld(...)` 加参数 `string civ = "athen"`;玩家实体创建循环里 `player.Civ = civ; techMgr.Configure(TechCatalogCache, civ);`
- 科技目录加载:`_sim.TryLoadTemplates()` 附近加 `TechnologyLoader.LoadAll(<technologies 目录>)`(目录 = templatesPath 推出来的 `simulation/data/technologies`;路径推导与模板加载同根)。
- 玩家初始化尾部:`var auto = techMgr.UpdateAutoResearch(_sim); if (auto.Count > 0) ValueModificationApplier.RescaleHealth(_sim, playerEntityId);`(Task 4 提供该类前先留 TODO 注释,Task 4 后补 —— 或把 Task 4 的类提前到本任务末尾,二选一,执行时按编译顺序决定)
- 约 752 行:`researcher.Tick(dt, techMgr)` → `researcher.Tick(dt, techMgr, _sim)`;返回非空时同样跑 `UpdateAutoResearch` + 血量重算(Task 4)。

**Step 4: 跑测试确认通过**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter TechnologyManagerTests`
Expected: 9 passed;`dotnet test`(全量)其余不红

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/Components/Technology.cs src/ZeroAD.Sim/Components/Production.cs godot/Scripts/SimBridge.cs src/ZeroAD.Sim.Tests/TechnologyManagerTests.cs
git commit -m "feat(sim): TechnologyManager 数据驱动重写(JSON+requirements+pair+autoResearch+civ)"
```

---

### Task 4: Attack/Health 接线 + 血量重算

**Files:**
- Modify: `src/ZeroAD.Sim/Components/Combat.cs`(Health 加 `BaseMax`;Attack 加 `IsRanged`;PerformAttack 走 Apply)
- Create: `src/ZeroAD.Sim/Components/ValueModificationApplier.cs`(血量重算 helper)
- Modify: `src/ZeroAD.Sim/EntityAssembler.cs`(装配点设 BaseMax/IsRanged)
- Modify: `src/ZeroAD.Sim/Content/TemplateLoader.cs`(`ExtractStats` 加 `AttackIsRanged`:模板存在 `Attack/Ranged` 节点)
- Modify: `godot/Scripts/SimBridge.cs` + `src/ZeroAD.Sim/Net/SimCommandExecutor.cs`:全部 `new HealthComponent{...}` 点补 `BaseMax`(grep 找全,含 SpawnFoundation 的 200)
- Test: `src/ZeroAD.Sim.Tests/UseSiteModifierTests.cs`(本文件跨 Task 4/5 累积)

**Step 1: 写失败测试**

```csharp
[Fact] public void Research_RangedAttack_SoldierDamageUp_CivilianUnchanged()
{
    // 世界:玩家+士兵(类含 Soldier,远程)+平民;研究 soldier_attack_ranged_01
    // 士兵 PerformAttack 计划的 DamageBlock.Pierce = round(base × 1.15);平民 base 不变
    // 断言用 DelayedDamage 队列或捕获 ScheduleHit 参数(按现有 DelayedDamage API 选最简断言)
}
[Fact] public void Research_TowerHealth_MaxUp_CurrentScalesProportionally()
{
    // 塔 Health{Current=200, Max=400, BaseMax=400};研究 tower_health(+add 或 multiply,读真实 JSON)
    // ValueModificationApplier.RescaleHealth 后:Max 变为修改后值,Current = 200 × newMax/400
}
[Fact] public void Health_Serialize_RoundTrips_BaseMax() { /* "basemax" 键读写一致 */ }
```

**Step 2: 跑测试确认失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter UseSiteModifierTests`

**Step 3: 实现**

`HealthComponent`:

```csharp
public int BaseMax;   // 模板基值(修改管线输入);OnInit 与 Max 同值,装配点三值同设
// Serialize 增加: s.NumberI32("bmax", BaseMax); Deserialize: BaseMax = d.NumberI32("bmax");
```

`AttackComponent`:`public bool IsRanged;`(Serialize `s.Bool("ranged", IsRanged)`,Deserialize 读回)。`PerformAttack` 改:

```csharp
public void PerformAttack(ComponentManager cm)
{
    if (Target == null) return;
    string prefix = IsRanged ? "Attack/Ranged/Damage/" : "Attack/Melee/Damage/";
    var mod = new DamageBlock { Capture = Damage.Capture };
    foreach (var kv in Damage.Amounts.OrderBy(k => (int)k.Key)) // 排序保确定
        mod.Amounts[kv.Key] = (int)MathF.Round(
            cm.Modifiers.Apply(prefix + kv.Key, kv.Value, Entity), MidpointRounding.AwayFromZero);
    DelayedDamage.ScheduleHit(cm, Entity, Target.Value, mod, delayTurns: 0);
    Cooldown = 1.0f / Rate;
}
```

`ValueModificationApplier.cs`:

```csharp
namespace ZeroAD.Sim.Components;

/// <summary>修改值变更后的实体刷新(原版 MT_ValueModification 最小对应)。
/// 只有 Health 需要响应:Max 变化时 Current 按比例缩放(原版 Health.js 同款)。
/// 其余组件查询时计算,天然新鲜。由研究完成/autoResearch 完成后调用。</summary>
public static class ValueModificationApplier
{
    public static void RescaleHealth(ComponentManager cm, EntityId playerEntity)
    {
        var owner = cm.QueryInterface<PlayerComponent>(playerEntity);
        if (owner == null) return;
        int playerId = playerEntity /* 由 Ownership 反查或从 PlayerManager 取 playerId;按现有 API 落地 */;
        foreach (var ent in cm.AllEntities)
        {
            var own = cm.QueryInterface<OwnershipComponent>(ent);
            if (own == null || own.PlayerId != playerId) continue;
            var hp = cm.QueryInterface<HealthComponent>(ent);
            if (hp == null) continue;
            int newMax = Math.Max(1, (int)MathF.Round(
                cm.Modifiers.Apply("Health/Max", hp.BaseMax, ent), MidpointRounding.AwayFromZero));
            if (newMax == hp.Max) continue;
            hp.Current = hp.Max > 0
                ? Math.Clamp((int)MathF.Round(hp.Current * (float)newMax / hp.Max, MidpointRounding.AwayFromZero), 0, newMax)
                : newMax;
            hp.Max = newMax;
        }
    }
}
```

装配点(`EntityAssembler.AssembleUnit` 等):`new HealthComponent { Current = maxHp, Max = maxHp, BaseMax = maxHp }`;Attack 装配 `IsRanged = stats?.AttackIsRanged ?? false`。`ExtractStats` 增加 `AttackIsRanged`(查 ParamNode 有无 `Attack/Ranged` 子节点)。

SimBridge 研究完成点(Task 3 已接线处)补:`ValueModificationApplier.RescaleHealth(_sim, playerEntityId);`

**Step 4: 跑测试确认通过**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter UseSiteModifierTests`
Expected: 3 passed

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/Components/Combat.cs src/ZeroAD.Sim/Components/ValueModificationApplier.cs src/ZeroAD.Sim/EntityAssembler.cs src/ZeroAD.Sim/Content/TemplateLoader.cs godot/Scripts/SimBridge.cs src/ZeroAD.Sim/Net/SimCommandExecutor.cs src/ZeroAD.Sim.Tests/UseSiteModifierTests.cs
git commit -m "feat(sim): Attack/Health 接入修正值管线;血量上限变化按比例缩放"
```

---

### Task 5: Gatherer/UnitMotion/Builder/Queue/人口接线

**Files:**
- Modify: `src/ZeroAD.Sim/Components/Resources.cs`(ResourceGatherer 速率结算点)
- Modify: `src/ZeroAD.Sim/Components/UnitMotion.cs`(移动推进点用修改后速度;先读 `SimSystem` 静态访问器确认取 cm 方式)
- Modify: `src/ZeroAD.Sim/Components/Construction.cs`(`BuilderComponent.Tick` 的 AddProgress)
- Modify: `src/ZeroAD.Sim/Components/Production.cs`(`ProductionQueue.EnqueueTraining` 训练时间走 ApplyTemplate)
- Modify: `src/ZeroAD.Sim/PlayerManager.cs`(`RecomputePlayerPopBonus` 每栋建筑 Apply `Population/Bonus`)
- Test: 继续在 `UseSiteModifierTests.cs`

**Step 1: 写失败测试(逐个红)**

```csharp
[Fact] public void GatherRate_WoodTech_AppliesPrefixMatch() { /* gather_lumbering_sharpaxes 研究后
    wood 采集结算用 round(rate × 1.15);food 不变 */ }
[Fact] public void WalkSpeed_Tech_AppliesAtMoveAdvance() { /* 研究移速科技后同一 tick 位移距离变大 */ }
[Fact] public void BuilderRate_Tech_SpeedsUpFoundation() { /* civil_engineering 类科技后 AddProgress 步进变大 */ }
[Fact] public void TrainTime_CostTech_Reduced() { /* siege_cost_time 研究后 EnqueueTraining 的 BuildTime = base × 0.9 */ }
[Fact] public void PopulationBonus_Tech_IncreasesLimit() { /* 有房(PopulationComponent.Bonus=10)玩家研究
    pop_house_01(affects Colony 时不匹配普通 House → 用无 affects 的样例科技或 wagon_trains multiply 1.2)后
    RecomputePlayerPopBonus 结果按 Apply 变化 */ }
```

**Step 2: 跑测试确认失败**

**Step 3: 实现**(每处一行级改动,grep 找消费点)

- 采集:找 `GatherRate` 消费点(SimBridge TickGatherers 或 UnitAI 采集态)→ `rate = (int)MathF.Round(cm.Modifiers.ApplyPrefix("ResourceGatherer/Rates/" + g.CarryType.ToString().ToLowerInvariant(), g.GatherRate, entity), AwayFromZero)`
- 移速:`UnitMotion` 推进方法里 `Speed` 读点 → `Fixed.FromFloat(cm.Modifiers.Apply("UnitMotion/WalkSpeed", Speed.ToFloat(), Entity))`(cm 经 SimSystem 现有静态取法,与 `SimSystem.GetComponent` 同源)
- Builder:`foundation.AddProgress(cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity) * 0.1f)`
- 训练时间:`EnqueueTraining` 内 `buildTime = cm.Modifiers.ApplyTemplate("Cost/BuildTime", stats.BuildTime, stats.GetClassList(), playerEntity)`;playerEntity 由 `OwnershipComponent`(建筑= Entity)→ `cm.GetPlayerEntityId`
- 人口:`PlayerManager.RecomputePlayerPopBonus`:`total += (int)MathF.Round(_cm.Modifiers.Apply("Population/Bonus", pop.Bonus, entity), MidpointRounding.AwayFromZero);`

**Step 4: 全量测试**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj`
Expected: 全绿(206 + 新增 ~30)

**Step 5: 确定性测试(状态哈希)**

```csharp
[Fact] public void Determinism_SameResearchOrder_SameStateHash()
{
    // 两个独立 ComponentManager,同序研究 3 个科技,ComputeStateHash() 逐字节一致
}
```

**Step 6: Commit**

```bash
git add -A src/ godot/Scripts/SimBridge.cs
git commit -m "feat(sim): 采集/移速/建造/训练时间/人口上限接入修正值管线"
```

---

### Task 6: 收尾(旧科技名映射 + 全量验证 + 记忆回写)

**Files:**
- Modify: `godot/Scripts/PetraManagers.cs`、`src/ZeroAD.Sim/Tutorial/TutorialEngine.cs`(旧科技名→真实 JSON 名)

**Step 1: 映射旧名**(grep `IsResearched("` 找全硬编码):

| 旧名(硬编码) | 真实 JSON 名 |
|---|---|
| `phase_town` | `phase_town_generic` |
| `phase_city` | `phase_city_generic` |
| `gather_capacity` | `gather_capacity_wheelbarrow` |
| `gather_wood` | `gather_lumbering_sharpaxes` |
| `gather_food` | `gather_farming_plows` |
| `infantry_attack` | `soldier_attack_ranged_01`(或 melee 变体,按 AI 意图) |
| `infantry_armor` | `soldier_resistance_pierce_01` |

(TutorialEngine 的具体引用以 grep 结果为准;不存在的名字逐一换成语义最接近的真实科技。)

**Step 2: 全量验证**

```bash
dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj   # 全绿
dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj              # 0 警告
# Godot 编译:按仓库惯例(godot/ 下 C# 工程,上一任务验证过 0 警告)
```

**Step 3: 记忆回写**

更新 `port-status-vs-original-cpp.md`:修正值管道从缺口清单移除;注明剩余(Resistance/VisionRange/RepeatTime/aura 源/缓存优化)。`mp-lockstep-implementation-done.md` 无需动。

**Step 4: Commit + 汇报**

```bash
git add -A && git commit -m "fix(ai,tutorial): 旧硬编码科技名映射到真实 JSON 科技"
```

汇报:测试数、编译状态、剩余缺口;合并回 main 由用户拍板(沿用上次流程:先本地验证再合并)。

---

## 执行顺序与依赖

Task 1 → 2 → 3(依赖 1 的 Modifiers 和 2 的 Catalog)→ 4(依赖 3 的研究落地)→ 5(依赖 1)→ 6。
每 Task 一个 commit;Task 3 的 SimBridge 两处与 Task 4 的 RescaleHealth 有编译依赖,执行时若顺序冲突,先在 Task 3 留空壳 static class,Task 4 填实现。
