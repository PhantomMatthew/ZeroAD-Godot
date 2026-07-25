# LOS / 战争迷雾 设计(对齐原版 CCmpRangeManager + Fogging/Mirage)

日期:2026-07-25 · 状态:已确认(三个决策点见 §8)· 分支:`feat/los-fog-of-war`

## 1. 目标

把 0 A.D. 原版的 LOS/战争迷雾完整移植进 C# 内核 + Godot 表现层,一期做全:

- 每玩家三态视野:未探索(UNEXPLORED)/已探索(EXPLORED)/可见(VISIBLE)
- 实体可见性:HIDDEN(完全隐藏)/FOGGED(雾中可见,变暗)/VISIBLE
- Fogging/Mirage:实体出雾变雾时生成冻结替身,回可见时切回真身
- 小地图迷雾层 + 世界空间地形雾贴图(shader)
- 全定点整数、进 state hash、序列化可回放、MP 锁步兼容

依据:GitHub 0ad/0ad master `source/simulation2/components/CCmpRangeManager.cpp`(2525 行)、`source/simulation2/helpers/Los.h`、`ICmpRangeManager.h`、本地 `Fogging.js`/`Mirage.js`/`VisionSharing.js`。

## 2. 原版机制要点(移植规格)

### 2.1 网格与编码

- 顶点格,边长 `LOS_TILE_SIZE = 4` 米(与 terrain tile 同);`verticesPerSide = worldMeters/4 + 1`
- `LosState`:每顶点每玩家 2-bit 打包进 `u32[]`,玩家 p(1..16)占偏移 `2*(p-1)`;0=UNEXPLORED,1=EXPLORED,2=VISIBLE,掩码 3
- 每玩家一张 `u16[]` 计数网格(lazy 分配):看到该顶点的单位数
- 状态迁移:计数 0→1 置 `VISIBLE|EXPLORED`;1→0 只清 VISIBLE(EXPLORED 永存,无衰减)
- 探索计数 `m_ExploredVertices[p]` 增量维护 → `GetPercentMapExplored`

### 2.2 更新模型:事件驱动 + 回合收尾(不是全图重算)

- **即时层**:PositionChanged → `LosMove`(MVP 用 remove+add,砍掉 `LosUpdateHelperIncremental` 双圆差集);OwnershipChanged/VisionRangeChanged → 旧主/旧射程 remove + 新主/新射程 add;Create/Destroy → 插入/移除 EntityData
- **回合层**(每 sim turn):`UpdateVisibilityData` —— 对脏实体集(替代原版 region 桶,更简单)逐个 `ComputeLosVisibility`,与 EntityData 里的 2-bit 缓存对比,变化则写回并发 `VisibilityChanged(player, entity, old, new)` 事件
- 计算链 `ComputeLosVisibility(ent, player)`(对齐原版顺序):
  1. 不在世界 → HIDDEN
  2. 是 mirage 且顶点可见 → HIDDEN(与真身互斥,防双显)
  3. reveal-all → VISIBLE(off-world/mirage 除外)
  4. 顶点可见 → VISIBLE(mirage 反而 HIDDEN)
  5. 顶点未探索 → HIDDEN
  6. RetainInFog → FOGGED
  7. Fogging 已激活且(未见过 或 已被 mirage 替代)→ HIDDEN
  8. 否则 → FOGGED

### 2.3 strip 圆填充(无 raycast!)

原版 LOS **没有高度遮挡**,纯圆形近似(GPG2 模型;高度只用于攻击抛物线,与视野无关)。移植 `LosUpdateHelper` 逐行 strip 填充:每行 j 以上行端点 i0/i1 为初值,向内外微调直到 `dy² + dx² <= r²`,全程 Fixed 平方比较,无 sqrt、无 float。MVP 移动 = 旧圆 remove + 新圆 add。

### 2.4 Fogging / Mirage

- `Fogging`(每实体组件,建筑/gaia 资源模板带 `<Fogging/>`):记录每玩家 seen/miraged/mirageId;收到 `VIS_FOGGED` 且已激活 → 生成 mirage 实体(冻结 owner/位置/朝向 + 白名单数据拷贝:Health/Identity/ResourceSupply/Foundation 等),然后 `RequestVisibilityUpdate(mirage)`
- `Mirage`(替身实体上的组件):存 parent + player + 冻结数据(GUI 查询用);收到本玩家 `VIS_HIDDEN`(=真身重新可见)→ 通知表现层切回真身;parent 已销毁则自毁
- 互斥在 `ComputeLosVisibility`(§2.2 第 2、4 条)——真身可见时 mirage 必 HIDDEN
- 所有权变 -1(销毁中):雾中 mirage 直接销毁
- 机制偏差说明:原版靠模板前缀 `"mirage|X"` 裁组件;我们走专用 spawn 路径 `SpawnMirage(parent, player)` 直接装配精简组件集(Identity/Ownership/Position/Mirage + 冻结数据),语义等价

### 2.5 序列化与确定性

- 只序列化:LOS 状态网格(u32[] 整体)+ 每玩家 explored 计数 + Fogging 每玩家 seen/miraged/mirageId(排序)+ mirage 实体本身(就是普通实体)
- 反序列化后:计数网格清零 → 遍历有视野实体重新 LosAdd 重建(确定性,原版同款);VISIBLE 位由计数重算,EXPLORED 取 max(存档值, 当前可见)
- 全部 Fixed/整数;脏实体集按 EntityId 排序遍历(事件分发顺序确定)

## 3. 内核架构(新增/改动)

| 件 | 位置 | 说明 |
|---|---|---|
| `LosGrid` | `src/ZeroAD.Sim/Components/LosGrid.cs`(新) | u32 状态网格 + lazy u16 计数 + strip 圆 add/remove + LosQuerier + explored 计数 + 序列化 |
| `RangeManager` | `Components/RangeManager.cs`(改) | EntityData += visionRange(Fixed)/visibilities(u32)/flags(InWorld·RetainInFog·IsMirage);事件接线;`SetBounds` 重建(修 256m 硬编码 bug);每回合 `UpdateVisibilityData` |
| `VisionComponent` | `ExtraComponents.cs`(改) | 复活:Range 改 Fixed;`EffectiveRange` 过修正值管线 `"Vision/Range"`;EntityAssembler 从 `TemplateStats.VisionRange` 装配(死数据激活) |
| `FoggingComponent` / `MirageComponent` | `Components/Fogging.cs`(新) | §2.4;模板 `<Fogging/>`/`<RetainInFog>` 解析进 TemplateStats |
| `LosManagerComponent` | 挂系统实体(照 TerrainComponent 模式) | 持有全局 LOS 状态 → 自动进 SerializeFullState + state hash |
| 事件 | `SimEventBus` | `VisibilityChangedEvent { Player, Entity, Old, New }` |

砍单(对齐原版 MVP 建议):VisionSharing/spy/bribe、盟军共享掩码(硬编码只含自己,留接口)、RevealShore、ExploreTerritories、圆形地图、ScriptedVisibility、增量双圆 strip、region 桶。

## 4. 表现层

1. **实体显隐**:SimBridge 每回合按 LocalPlayer 查询实体可见性 → `EntityNodes[ent].Visible`;FOGGED → 变暗(材质 modulation);修 Minimap 硬编码 `playerId==1` → LocalPlayerId
2. **小地图雾层**:每回合从 LosQuerier 生成 R8 贴图(可见255/已探索127/未探索0),叠加在 Minimap 背景上
3. **世界雾**:TerrainRenderer 材质换 ShaderMaterial,采样同一张 R8 雾图(世界 XZ → UV),hidden 全黑、explored 半暗;7-tap 二项式模糊(1,6,15,20,15,6,1)/64 横纵各一遍(对齐 LOSTexture.cpp)
4. **Mirage 视觉**:冻结 actor 节点(灰调);真身回可见时换回(切 node 引用即可)

## 5. 性能预算

教程图 768m → 193×193 顶点:状态网格 145KB + 每玩家计数 73KB;strip add/remove 量级 O(range) 顶点;站桩零开销。雾贴图 193² ≈ 37KB/帧上传可忽略。远在预算内。

## 6. 测试计划(TDD)

1. LosGrid:位打包、计数增减、0↔1 状态迁移、strip 圆 vs 暴力圆、explored 保留
2. RangeManager 集成:生成/移动/销毁/换主 → 计数与状态;SetBounds 重建
3. 可见性:ComputeLosVisibility 全链路(含 mirage 互斥)、VisibilityChanged 事件、reveal-all、percent explored
4. 序列化:往返一致、反序列化重建计数等价、1000 回合确定性 hash(带移动单位)、基线 247 全绿
5. Fogging/Mirage:全生命周期(探索→出雾→替身→回可见→替身消)、parent 销毁、序列化
6. 修正值:科技改 `"Vision/Range"` → 计数重布
7. 表现层:headless 冒烟(雾图生成、显隐切换无异常)

## 7. 风险

- **Mirage 复杂度高**:互斥/销毁时序是原版最绕的部分 → 测试先行,严格照链实现
- **世界雾 shader**:TerrainRenderer 目前是 SurfaceTool 生成网格 + 标准材质 → 换 ShaderMaterial,UV 用世界 XZ 直接算,不依赖已有 UV
- **地图尺寸修复面**:`FillPassabilityFromPmp` 已有 Obstructions.SetBounds 先例,RangeManager/LOS 照此挂接
- **教程重负载**:1864 实体 + LOS 每回合脏集 → 实测帧率验收

## 8. 已确认决策(2026-07-25)

1. Mirage:**完整版一期做完**(对齐原版)
2. 世界雾贴图:**一期就要**
3. 位打包:**保留 16 玩家 u32**(对齐原版)
