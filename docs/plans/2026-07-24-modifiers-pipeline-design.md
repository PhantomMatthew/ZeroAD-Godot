# 修正值管线(ModifiersManager)+ 科技 JSON 化 — 设计文档

日期:2026-07-24 | 分支:feat/modifiers-pipeline | 原则:对齐原版(`simulation/components/ModifiersManager.js` + `technologies/*.json`)

## 1. 背景与问题

现状(`src/ZeroAD.Sim/Components/Technology.cs`):

- `TechnologyManager.OnInit` C# 硬编码 10 个科技,`Effects` 为临时字符串键 float 字典
- `GetModifier(key)` **全代码库无人调用** —— 科技研究完成后修改值被算出但从未接到任何数值管线(死值)
- 仓库内已有真实数据资产无人消费:`simulation/data/technologies/*.json`(几十个)、`simulation/data/civs/*.json`(12 文明)
- 原版路径化修改(`Attack/Ranged/Damage/Hack` ×1.15 + `affects: ["Soldier"]` 类过滤)完全没有对应物

## 2. 范围(已确认)

**做:** ModifiersManager 系统组件 + tech JSON 加载(替换 C# 硬编码)+ 核心 use-sites 接线(攻击/血量/采集/人口/建造·训练时间)+ civ 起始科技自动研究 + pair 互斥。

**不做(留位):** Auras(英雄光环/阵型/奇迹)—— 存储结构按 `target` 分层(玩家级/实体级),Auras 后续只是另一种 `AddModifiers` 调用方。

## 3. 架构总览

```
technologies/*.json ──加载──▶ TechnologyManager(重写,数据驱动)
                                    │ 研究完成
                                    ▼
   civs/*.json ─起始科技(免费)──▶ ModifiersManager(新,内核持有)
                                    ▲ 存储:(属性路径, 目标实体) → [Modification]
   实体基值(TemplateStats,现有)     │ AddModifiers(modId=科技名, mods, target=玩家实体)
        │                           │ 查询时合成
        ▼                           │
   use-sites: Apply(path, baseValue, entity)
   ├─ Attack: 近战/远程 × Hack/Pierce/Crush
   ├─ Health: Max(基值保留,Current 按比例缩放)
   ├─ ResourceGatherer / UnitMotion / Builder / ProductionQueue
   └─ 人口上限(玩家实体级)
```

三个核心决策:

1. **基值不动,查询合成** — 组件保留模板基值,用值时过 `Apply()`。修改管线是唯一活数据源
2. **先玩家级、后实体级**(原版语义)— 科技存玩家实体上;实体本地修改(Auras 预留)后应用、优先级更高
3. **Auras 不做但留位** — 存储 target 分层即预留

## 4. ModifiersManager(内核类,ComponentManager 持有)

```csharp
public sealed record Modification(string Path, float? Add, float? Multiply,
    string? Replace, IReadOnlyList<string> Affects);

public sealed class ModifiersManager
{
    public void AddModifiers(string modId, IReadOnlyList<Modification> mods, EntityId target);
    public void RemoveAllModifiers(string modId, EntityId target);
    public float Apply(string path, float baseValue, EntityId entity);
    public float ApplyTemplate(string path, float baseValue,
        IReadOnlyList<string> classes, EntityId playerEntity);
}
```

存储:`(属性路径, 目标实体) → modId → [Modification]`;同 modId 重复添加 = 拒绝(原版 MultiKeyMap 语义)。

**`Apply` 查询语义(对齐原版 `ApplyModifiers`):**

1. 取实体 `IdentityComponent.Classes`;无 Identity → 返回 baseValue(原版同款短路)
2. 取 `OwnershipComponent.PlayerId` → 玩家实体 → 先应用玩家级修改
3. 再应用实体本地修改
4. `affects` 过滤:空 = 生效;非空 = AND 语义空格分词(`"Soldier Melee"` 要求两者都在类表中,原版 `DoesModificationApply`)
5. 合成顺序:**先全部 add,再全部 multiply,replace 直接覆盖**;跨科技按 modId 排序固定顺序

**与原版的刻意差异(文档化):**

- **无缓存** — 原版缓存是 JS 性能优化(代码内自认复杂),C# 内核小规模实体查询时计算足够;语义不变
- **不序列化** — 派生态,由 TechnologyManager 重放重建
- **合成顺序固定化** — 原版逐条按插入序;本设计排序固定,比原版更稳(实践中同路径无 add+multiply 冲突科技)

`Apply` 返回 float;int 基值调用点(人口上限)`round`。

## 5. 科技 JSON 管线

新文件 `Content/TechnologyLoader.cs`:扫描 `simulation/data/technologies/*.json` 解析为 `TechnologyDefinition`(Name/GenericName/Cost 四资源/ResearchTime/Requirements/Modifications/Affects/Supersedes)。

Requirements 支持:`{tech}`(前置已研究)、`{civ}`、嵌套 `{any: [...]}`;其余形态(entity/numberOf 等)跳过。

**TechnologyManager 重写:**

- 删除 OnInit 全部硬编码科技;构造注入 loader 结果(测试可注入假数据)
- `CanResearch(tech, civ, researched)`:requirements 判定 + pair 未锁定
- `StartResearch`(ResearcherComponent)加 `CanResearch` 检查
- **pair 互斥**:`pair_*.json` 解析 (techA, techB) → `_pairOf`;研究 A 后 `CanResearch(B)=false`(确切 JSON 字段计划阶段核实)
- **civ 起始科技**:`simulation/data/civs/{civ}.json` 起始科技列表,玩家初始化逐个 `ApplyResearch`(免费瞬时;字段名计划阶段核实)
- 删除死 API `GetModifier(key)`
- 旧科技名映射:`PetraManagers.cs`/`TutorialEngine.cs` 硬编码名(`infantry_attack` 等)→ 真实 JSON 名,编译期暴露

**序列化:** 只存已研究科技名(现有模式);反序列化按科技序重放 `ApplyResearch`(免费路径,不扣资源)重建 ModifiersManager。

## 6. use-sites 接线

原则:基值字段保留(序列化格式不变),读值点改 `Apply(path, base, entity)`。

| 组件 | 修改路径(对齐原版) | 改动 |
|---|---|---|
| `AttackComponent` | `Attack/Melee\|Ranged/Damage/{Hack,Pierce,Crush}` | 出伤时逐类型过 Apply,`DamageBlock` 保留 base |
| `HealthComponent` | `Health/Max` | 新增 `BaseMax`;`Max` 变只读计算值;序列化存 BaseMax+Current |
| `ResourceGatherer` | `ResourceGatherer/Rates/{type}` | 采集结算过 Apply |
| `UnitMotion` | `UnitMotion/WalkSpeed` | 移速读点过 Apply |
| `BuilderComponent` | `Builder/Rate` | 建造速度过 Apply |
| `ProductionQueue` | `Cost/BuildTime` | 训练时间走 `ApplyTemplate`(单位未出生,类列表+玩家实体) |
| 人口上限 | `Player/PopCapBonus`(确切路径名以 JSON 为准) | 玩家实体级 add |

**变更事件(原版 `MT_ValueModification` 最小对应):**

- `ApplyResearch` 落修改后 → 内核事件 `ValueModifiedEvent { PlayerId, Path }`,走 `cm.Events` 现有管线,无 Godot 依赖,MP 锁步内确定性
- **唯一必须响应:`HealthComponent`** —— `Health/Max` 变化时 `Current = Current × newMax/oldMax`(原版同款比例缩放,防止血量科技白送差值)
- 其余组件查询时计算,天然新鲜,不订阅

不动:模板加载、伤害公式(0.9^resistance)、研究扣资源、HUD(下轮)。

## 7. 测试与确定性

TDD(xUnit,内核无 Godot 依赖):

1. `ModifiersManagerTests` — 合成/优先级/affects 过滤(含 AND)/短路/Remove/重复拒绝
2. `TechnologyLoaderTests` — 真实 JSON 解析(cost/requirements/modifications/affects/any 嵌套/pair)
3. `TechnologyManagerTests` — 前置拒绝/放行、pair 锁、civ 起始科技、序列化重放一致
4. `UseSiteModifierTests` — 士兵伤害/塔血缩放/采集/移速/训练时间各一例;非 affects 类不受影响
5. 确定性:两 ComponentManager 同序研究 → 状态哈希逐字节一致

红线:合成只含 float `+`/`×`;modId 排序固定;无 RNG;无无序遍历进入结果。

## 8. 任务切分(6 个 TDD 任务)

1. Modification 模型 + ModifiersManager 存储/查询
2. TechnologyLoader(JSON + pair + requirements)
3. TechnologyManager 重写 + civ 起始科技 + 重放序列化
4. Attack/Health 接线 + 缩放事件
5. Gatherer/UnitMotion/Builder/Queue/人口接线
6. 收尾:全量测试 + 编译 + 记忆回写 + 合并

实施计划:`docs/plans/2026-07-24-modifiers-pipeline-plan.md`(另文)
