# 0 A.D. → Godot 4.x + C# 全量重写功能规划(路线 B)

> 状态:规划草案 v1 · 依据:对本仓库的实际盘点(2026-07)
> 规模基准:83 个 JS 模拟组件、78 个 C++ 模拟组件、~25 个 GUI 页面、
> Petra AI、rmgen 随机地图库、XMPP 多人大厅、录像回放、Atlas 编辑器

---

## 1. 目标与非目标

### 目标
- 用 Godot 4.x(.NET 版)+ C# 重写 0 A.D.,达到与当前 `0.29.0`(Empires Ascendant)对等的核心玩法
- 保持**跨平台确定性锁步多人联机**(这是不可妥协的硬约束)
- 尽可能复用现有数据资产(实体模板 XML、贴图、音频、地图数据、平衡数值)
- 保持 mod 化数据驱动架构(数据与代码分离)

### 非目标(首版明确不做)
- Atlas 编辑器完整重写(用 Godot Editor 插件替代,功能子集)
- 与原版 Pyrogenesis 的联机互通(协议不兼容,不尝试)
- OOS 级别兼容旧录像文件(格式重新设计)
- Web 导出(Godot C# Web 支持不成熟)

---

## 2. 总体架构

```
┌─────────────────────────────────────────────────┐
│ Godot 表现层(非确定性,可用 float)              │
│  渲染绑定 · GUI(Control)· 音频 · 输入 · 特效    │
└──────────────────┬──────────────────────────────┘
          单向状态同步(每模拟回合→插值渲染)
┌──────────────────┴──────────────────────────────┐
│ 确定性内核(纯 C#,零 Godot 依赖,可独立单测)    │
│  定点数学 · ECS · 回合管理 · 寻路 · 序列化/哈希   │
└──────────────────┬──────────────────────────────┘
┌──────────────────┴──────────────────────────────┐
│ 数据层:实体模板 XML · 技术树 · 地图 · l10n       │
└─────────────────────────────────────────────────┘
```

**核心原则**:
1. 模拟世界是纯 C# 对象(不是 Godot Node),Godot 场景树只做渲染表现
2. 模拟内全部使用定点数(移植 `source/maths/Fixed.h` 的 CFixed_15_16),禁止 float
3. 确定性内核作为独立 .csproj,CI 中跑跨平台(Linux/macOS/Windows)哈希一致性测试
4. 组件间通信保留消息机制(对应现有 `TypeList.h` 的 MT_*/IID_*/CID_* 体系)

---

## 3. 模块分解

### M0 — 确定性内核(其他一切的地基)
| 功能 | 对应现有代码 | 验收标准 |
|---|---|---|
| 定点数学库(Fixed、Vector2D/3D、三角函数查表、sqrt) | `source/maths/Fixed*.h` | 与 C++ 版逐位对拍一致 |
| 确定性 PRNG | `source/simulation2/helpers/` | 相同种子跨平台序列一致 |
| ECS:ComponentManager、实体生命周期、消息广播/订阅 | `source/simulation2/system/` | 支持动态查询 interface,消息顺序确定 |
| 回合管理(TurnManager,500ms 回合 + 命令延迟) | `source/simulation2/system/TurnManager*` | 单机/回放/联机共用同一驱动 |
| 状态序列化 + 快速哈希(OOS 检测、存档基础) | `SerializeTemplates.h` 等 | 序列化→反序列化→再模拟 N 回合哈希一致 |
| 实体模板加载器(XML,含 parent 继承/`merge`/`filtered` 规则) | `source/simulation2/system/ParamNode` | 直接读取现有 `simulation/templates/*.xml` 不改格式 |

### M1 — 资产转换管线(与 M0 并行)
| 功能 | 说明 |
|---|---|
| DAE/PMD → glTF 批量转换工具 | 含骨骼动画、prop 挂点(prop point);校验参考 `checkrefs.py` |
| Actor XML → C# 解析器 + Godot 场景装配 | variant/group 随机化系统需在运行时重现 |
| PMP 地图 + 场景 XML → 中间格式 | 高度图、地形贴图混合、实体摆放 |
| DDS 贴图直用或转 .ctex | Godot 支持 DDS,优先直用 |
| 音频(OGG)直用 | 音频组 XML(`audio/`)解析为 C# |
| l10n:.po → Godot Translation | 复用 Transifex 产出 |

### M2 — 核心模拟组件(C# 移植,决定"能不能玩")
优先级 P0(约 25 个,凑齐即可单机采集-建造-战斗闭环):
- 空间:Position、Obstruction、ObstructionManager、RangeManager(四叉树视野/范围查询)、Terrain、WaterManager
- 单位:UnitMotion(与寻路耦合)、UnitAI(**最大单体**,现有 JS 状态机 ~6000 行)、Health、Resistance、Attack、DelayedDamage、Timer
- 经济:ResourceGatherer、ResourceSupply、ResourceDropsite、Cost、Player、PlayerManager、Population、EntityLimits
- 建造:Builder、Foundation、BuildRestrictions、ProductionQueue、TrainingRestrictions
- 元:Identity、Ownership、TemplateManager、GuiInterface(sim→GUI 的唯一查询边界)

P1(玩法完整性,约 40 个):Formation/FormationAttack、Garrisonable/GarrisonHolder、Capturable、Gate、Pack、Promotion、Auras、ModifiersManager、Researcher/TechnologyManager(技术树 JSON 直读)、Market/Barter/Trader、Heal、Repairable、RallyPoint、Fogging/Mirage(战争迷雾镜像)、Loot/Looter、Diplomacy、CeasefireManager、EndGameManager(胜利条件)、StatisticsTracker 等

P2(锦上添花):AlertRaiser、BattleDetection、AttackDetection、SkirmishReplacer、Settlement、AutoBuildable、MotionBall 等其余组件

### M3 — 寻路(确定性,C++ → C#)
| 功能 | 对应现有代码 |
|---|---|
| 长程分层寻路(HierarchicalPathfinder,导航网格分块) | `source/simulation2/helpers/HierarchicalPathfinder*` |
| 短程向量寻路(vertex pathfinder,单位间避让) | `Pathfinding.h`、`VertexPathfinder*` |
| 通行性分类(passability classes,来自 `pathfinder.xml`) | 数据格式不变 |
| 异步任务化(多线程但结果确定:任务在回合边界收割) | `LongPathfinder` 线程池模式 |

验收:1v1 大地图 300+ 单位混战不掉帧、无穿模死锁、录像回放路径逐位一致。

### M4 — 渲染表现层(Godot 侧,非确定性)
- 地形:高度图 mesh 分块 + 多层贴图混合 shader(现有 alpha 混合贴图直接用)
- 单位:sim 位置/朝向 → Node3D 插值;动画状态机(actor variant → AnimationTree)
- 战争迷雾:LOS 纹理(来自 RangeManager)+ 迷雾 shader + Mirage 幻影实体
- 选择圈、血条、旗帜、投射物、粒子(现有粒子 XML 转 GPUParticles)
- 小地图(地形 + 实体点 + 视野遮罩)
- 相机:RTS 相机(平移/旋转/缩放/边缘滚动),对应 `source/graphics/GameView`

### M5 — GUI(Godot Control 重建,~25 页)
P0:主菜单、单机游戏设置(gamesetup)、会话内 HUD(session:选择面板、指令面板、资源栏、聊天、外交、交易)、加载页、总结页(summary)
P1:选项、热键设置、存档/读档、回放菜单、文明百科(civinfo)、科技树(structree)、地图浏览器
P2:多人 gamesetup、大厅全套(prelobby 注册/登录 + lobby)、战役、教程、手册、启动画面
说明:现有 GUI 全部是自研 XML+JS,**无自动转换可能**,全部手工重做;但 GuiInterface 的数据协议可保留,减少 sim 侧改动。

### M6 — 网络与多人
- 传输:Godot ENetMultiplayerPeer(替代自带 ENet 封装)
- 锁步命令队列:客户端命令 → 主机汇总 → 广播 → 延迟 N 回合执行(对应 `source/network/NetTurnManager`)
- OOS 检测:每 N 回合交换状态哈希,不一致即 dump 序列化状态用于 diff
- 重连/主机迁移(P1)、观战(P1)
- 大厅:XMPP(gloox 的 C# 替代:如 XMPP .NET 库)+ 评分/排行(P2,可后置)

### M7 — 录像与存档
- 录像 = 初始状态 + 命令流(确定性内核的免费产物),回放 UI
- 存档 = 完整状态序列化(M0 已具备),兼容版本号机制

### M8 — AI(Petra → C#)
- common-api 层(AIInterface/AIProxy 的差量状态快照协议保留)
- Petra:经济管理、军事管理、建造规划、攻防决策(JS ~3 万行)
- 运行于 worker 线程,回合边界注入命令(与人类玩家走同一命令队列,天然确定)
- 阶段目标:P0 只需"能采集能造兵能进攻"的极简 AI;完整 Petra 为 P1/P2

### M9 — 地图与内容
- 场景/遭遇战地图加载(M1 转换产物)
- **rmgen 随机地图**:现有 JS 库(`maps/random/` + rmgen 库)体量大;方案:P0 先只支持固定地图;P1 将 rmgen 核心库移植为 C#,地图脚本逐个翻译(或评估嵌入 JS 引擎仅用于地图生成——生成发生在游戏开始前,不破坏确定性)
- 战役/教程(触发器系统 `Trigger.js` → C# 脚本接口)(P2)

### M10 — 工具链与 mod 支持
- Godot Editor 插件:地图编辑(替代 Atlas 子集:地形笔刷、实体摆放、玩家设置)
- mod 加载:保持 mods 目录叠加语义(VFS 优先级),C# 侧数据全部走该 VFS
- 模板校验工具(移植/复用 `checkrefs.py`)

---

## 4. 里程碑(每个都必须"可运行、可演示")

| 里程碑 | 内容 | 依赖 |
|---|---|---|
| **MS1 内核验证** | M0 完成;命令行跑 1000 回合模拟,三平台哈希一致 | — |
| **MS2 看得见** | M1+M4 骨架:加载一张现有地图,地形+单位渲染+RTS 相机 | MS1 |
| **MS3 单机闭环** | M2-P0 + M3 + M5-P0:采集→建造→造兵→打赢一场遭遇战 | MS2 |
| **MS4 多人可玩** | M6 锁步 + OOS 检测;2 人局打完不掉线不 OOS | MS3 |
| **MS5 玩法对齐** | M2-P1 + M5-P1 + M7 + M8 极简 AI | MS3 |
| **MS6 内容对齐** | M8 完整 Petra + M9 rmgen + M5-P2 大厅 | MS4/MS5 |
| **MS7 发布候选** | M10 工具 + 性能达标(300 单位 60fps)+ 全平台打包 | MS6 |

---

## 5. 关键风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| C# 浮点/JIT 不确定性泄漏进模拟 | 🔴 | 内核 csproj 用 Roslyn Analyzer 禁 float/double;CI 跨平台哈希对拍 |
| UnitAI 状态机移植失真(6000 行 JS,隐式行为多) | 🔴 | 先给 JS 版补行为测试用例,C# 版对拍;分状态逐个迁移 |
| 寻路性能不达标(C# vs 优化过的 C++) | 🟡 | Span/struct 化热路径;必要时该模块降级为 GDExtension C++ |
| rmgen 体量失控 | 🟡 | P0 砍掉;评估"嵌入 JS 仅做赛前地图生成"逃生舱 |
| GUI 25 页手工重做工期爆炸 | 🟡 | 严格 P0/P1/P2 分级;GuiInterface 协议不动降低联调成本 |
| Godot .NET 平台坑(移动端/导出) | 🟢 | 首版只做桌面三平台 |

---

## 6. 许可与合规
- 代码 GPL-2.0+(本仓库现状)→ 重写代码建议同为 GPL-2.0+;Godot(MIT)兼容
- 美术 CC-BY-SA → 可直接复用,保留署名
- 需替换的第三方:SpiderMonkey(不再需要)、gloox→C# XMPP 库、NVTT(Godot 自带纹理压缩)

---

## 7. 工作量量级(粗估,单位:人月)

| 模块 | 估算 |
|---|---|
| M0 内核 | 4–6 |
| M1 资产管线 | 3–4 |
| M2 模拟组件(83 JS + 78 C++ 中约 100 个需移植) | 18–30 |
| M3 寻路 | 4–6 |
| M4 渲染 | 5–8 |
| M5 GUI | 8–12 |
| M6 网络 | 4–6 |
| M7 录像存档 | 1–2 |
| M8 AI | 6–10 |
| M9 地图内容 | 4–8 |
| M10 工具 | 3–5 |
| **合计** | **约 60–95 人月**(不含打磨与平衡) |

> 参照:0 A.D. 原项目由社区开发 20+ 年。上表假设执行者对两侧代码库均熟悉。

---

## 附录 A — 现有资源文件复用性逐类判定

> 依据:对 `binaries/data/mods/public/` 的实测盘点(2026-07)。
> 总量约 1.6 万个资源文件,按文件数 **~85% 可直接使用或仅需解析器**;
> ~5500 个 DAE 需转换管线(中等风险);归零重做的仅 shaders、GUI 布局、rmgen 脚本。

### A.1 直接可用(零转换或近零成本)

| 资源 | 数量 | 说明 |
|---|---|---|
| 贴图 PNG/TGA | 7481 | Godot 原生导入 |
| 贴图 DDS | 1176 | Godot 4 支持 DDS 导入(BC 压缩) |
| 音频 OGG | 1101 | 原生支持;362 个音频组 XML 写 C# 解析器即可 |
| 翻译 .po | 全部 | Godot 原生支持 gettext PO,零成本 |
| 字体 TTF | — | 原生支持 |

### A.2 数据可用,需自写解析器(格式不变,成本低且可控)

| 资源 | 数量 | 说明 |
|---|---|---|
| 实体模板 XML | 1978 | 含 parent 继承/merge 规则,解析器一次性成本(M0 已含) |
| Actor XML | 5705 | variant/group 系统需运行时重现;内部 DAE 路径重映射到转换后资产 |
| 粒子/材质 XML | 107 | 粒子参数 → GPUParticles 映射 |
| PMP 地图(二进制) | 150 | 自定义格式,解析参考 `source/graphics/MapReader.cpp`(高度图 + 地形贴图索引) |
| 场景 XML / 地图 JSON | ~260 | 纯数据 |

### A.3 可用但为最大风险点:DAE 模型与动画

数量:4305 网格 + 1220 动画。**Godot 4 已移除 COLLADA 导入**(仅 Godot 3 支持),
必须走转换管线(Blender headless 批量 DAE→glTF,或 assimp)。三个 0 A.D. 特有的雷:

1. **骨骼重映射**:引擎用 `art/skeletons/*.xml` 将各建模工具导出的骨骼名统一映射——
   这些 DAE 是 20 年间不同工具导出的 COLLADA 1.4.1,批量转换必有一批骨骼对不上。
2. **动画与网格分离**:动画是独立 DAE,由 actor XML 在运行时组合到网格;
   glTF 管线需重建"一套骨骼、多套动画"的装配方式。
3. **Prop 挂点**:武器/装饰通过命名骨骼(`prop_*`)挂接,转换时必须保留这些空骨骼。

预期 80–90% 自动转换成功,10–20%(数百文件)需人工修复。对应 M1 的 3–4 人月估算。

### A.4 无法复用,必须重做

| 资源 | 数量 | 原因 | 对应模块 |
|---|---|---|---|
| 着色器(GLSL vs/fs + XML 效果定义) | ~34 | 自研渲染管线专用,与 Godot 着色语言不兼容;数量少 | M4 |
| GUI 布局 XML + JS | ~25 页 | 自研 GUI 引擎格式,无对应物,全部用 Control 重建 | M5 |
| rmgen 随机地图 JS | 173 | 是代码不是数据;翻译为 C#,或嵌 JS 引擎仅做赛前生成 | M9 |
| Atlas 相关资源 | — | 编辑器不重写(非目标) | M10 |

### A.5 操作提醒

美术资源走 **git-lfs**。转换管线必须消费 LFS 完整拉取后的文件,
并把 LFS 完整性(`git-lfs fsck`,参考 `checkrefs.yml`)作为管线前置检查,
否则会把 LFS pointer 文本当作模型文件喂给转换器。
