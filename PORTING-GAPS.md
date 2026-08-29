# 待移植功能跟踪(PORTING GAPS)

> 对比基准:上游 0 A.D. `0.29.0`(macOS 检出:`/Users/matthew/SourceCode/gitea/0ad`)
> 盘点日期:2026-08-22 · 方法:三路并行探索(C# 组件逐文件核对 / 基础设施子系统对照 / 表现层对照)
> 使用方式:完成一项就把 `- [ ]` 改为 `- [x]` 并在行尾追加 `(commit <hash>)`。新增缺口直接往对应表里加行。
>
> 配套文档:总体规划见 `godot-rewrite-plan.md`;引擎分析见 `claude-analyze/*.md`。

**图例**:✅ 完整 · 🟡 部分(附缺口)· ❌ 缺失 · 🔵 有等价替代实现

---

## 1. 总览(M0–M10)

| 模块 | 完成度 | 一句话状态 |
|---|---|---|
| M0 确定性内核 | ★★★★☆ | 序列化+哈希/OOS+锁步可用;🔴 存在系统性浮点违规(§2) |
| M2 模拟组件 | ★★★★☆ | 83 JS 组件:22 ✅ / 15 🟡偏完整 / ~11 ❌ |
| M3 寻路 | ★★★★☆ | 三引擎齐;缺异步任务、增量更新、pass class 数据驱动 |
| M4 渲染 | ★★★☆☆ | 地形/水/迷雾/领土/血条/集结点线齐;缺粒子、过场、天空、战场贴花 |
| M5 GUI | ★★★☆☆ | 约 60%;缺 prelobby/campaigns/viewer/credits 等 10+ 页 |
| M6 网络 | ★★★★☆ | 锁步+OOS+大厅协议齐;缺重连、观战 |
| M7 存档录像 | ★★★★★ | 双闭环,亮点项 |
| M8 Petra AI | ★★☆☆☆ | 模块名齐全但约 **20% 体量**(≈5–6k 行 vs 原 ≈26k 行) |
| M9 rmgen | ★★☆☆☆ | 库核心齐;地图仅移植 ~12/~173(≈7%) |
| 触发器/教程 | ★★~★★★ | 触发器=骨架(5 条件/6 动作);教程硬编非 JSON |

---

## 2. 🔴 P0:确定性债务(所有联机/OOS 工作的前置)

内核 `src/ZeroAD.Sim/` 大面积使用 `float`/`MathF`/`double`,违反 AGENTS.md「模拟内禁浮点」硬约束。跨平台哈希对拍是定时炸弹。

- [x] **OOS 真源清零**(commit 7caf8cd):实测真风险 = libm 超越函数 12 个 sim 调用点,全部定点化(Trig.SinCosApprox/Atan2Approx + BuilderTimeMultiplier 查表)。剩余 float 均为 IEEE 精确运算(Sqrt/Floor/Round/基本算术,.NET 跨平台逐位一致)+ 表现参数,无 OOS 风险——逐位对齐 Fixed 的完整清零转为长期忠实性任务(非确定性风险)
- [x] **Rmgen 层确定性**(commit 7caf8cd):全树仅 HellasMap 干涉/RmgenCommon 出生环/HeightmapLib Pow 三处直调 System.Math,已改 SafeMath;其余本就走 SafeMath
- [x] **AI 层确定性**(commit 7caf8cd):ConstructionPlan 选址角定点 sincos;InfoMap/PetraConfig 的 Sqrt 属 IEEE 精确运算(无风险)
- [x] **`Fixed.Pow`**(commit 7caf8cd):整数幂精确实现 + num^0.7 预计算查表(原版 C++ 亦无通用 fixed Pow)
- [x] Geometry.h 其余函数(commit 7caf8cd):DistanceToSquare(Squared)/NearestPointOnSquare/DistanceSquareToSquare/MaxDistanceToSquare(Squared)/TestRaySquare/AASquare/DistanceToSegment 九函数全移植(注:原版无 PointIsInCircle/TestLineInSquare,系笔误)
- [x] CI 门禁(commit 7caf8cd):DeterminismGateTests——xUnit 源码扫描,禁 libm 超越函数/非 Rand48 随机/时钟读取(白名单 SafeMath/Fixed/Trig + 逐文件良性豁免),dotnet test/CI 均拦截

---

## 3. M2 模拟组件

### 3A. 完全缺失的 JS 组件(`binaries/data/mods/public/simulation/components/`)

- [x] **DeathDamage** — 死亡自爆(commit 1754989);RemoveDeadEntities 销毁前经 DelayedDamage.ApplyHit 结算
- [x] **Upkeep** — 维护费(commit 1754989;当前数据 0 模板,语义完整:周期扣费+欠费标记)
- [x] **AttackDetection** — 受击警报(commit 1754989);抑制表去重 + PlayerAttackedAlertEvent
- [x] **AlertRaiser** — 警铃(commit 1754989);RaiseAlert 平民入楼/EndAlert 放出
- [x] **BattleDetection** — 战区聚簇跟踪(commit 1754989;简化版)
- [x] **AutoBuildable** — 自动完工(commit 1754989;当前数据 0 模板)
- [x] **UnitMotionFlying** — 飞行运动(commit 1754989);UnitMotion.IsFlying 直线分支+巡航高度
- [ ] 🔵 PopulationCapManager — 职能已折叠进 PlayerComponent.MaxPopCap/PopBonuses(L366–368),无需单独移植
- [ ] 🔵 Upgrade — 命令层等价已存在(SimCommandExecutor.ApplyUpgrade L561–589);如需原版语义(Upgrade 组件+进度条 UI)再补
- [ ] ⚪ MotionBall / Settlement — 演示/标记件,不移植

### 3B. 部分实现的补齐点(按影响排序)

| 优先 | 文件(vs 原版) | 缺口 |
|---|---|---|
| P1 | **UnitAI.cs** | ✅ CHASING/FINDINGNEWTARGET(16903fa)+Heal/Treasure 换目标续单(c241b13);余缺口:Pickup 接送/编队控制组切换(属内核寻路层,列 §4) |
| P1 | **Combat.cs** | ✅ 全清:多攻击型分立(1bbcffc)+溅射范围伤害+弹道延迟(b7b907d)+回血再生(40124c0)+DeathDamage 联动(1754989) |
| P1 | **Production.cs** | ✅ 逐个出兵 + autoQueue(ea13f60);Upkeep 数据无条目(0 科技/模板带 upkeep,机制存在无需激活) |
| P1 | **Technology.cs** | ✅ 全清:研究队列+取消退款(68f8e24)+训练 requirements 科技门(521f3b3) |
| P1 | **PromotionComponent** | ✅ 已落地(40124c0):模板驱动换模板晋升(同位同向同主/血量折算/XP 结转),装配修复 |
| P1 | **BarterSystem.cs** | ✅ 价漂移已落地(40124c0):每笔推涨+周期回落+存档骑缝;仅 per-player BarterMultiplier 待接(科技修正值管线) |
| P2 | **UnitMotion.cs**(408 行 vs 原 C++ ~2000) | 异步路径请求架构(现同步解算+0.3s 节流 L41–47)、朝向更新、waypoints 序列化(L38–40 瞬态) |
| P2 | **UnitSeparation.cs** | pushing-pressure、编队控制组豁免、中途 nudge、per-template weight、CheckMovement 不可通行钳制(TODO L99);O(n²) → 空间分格 |
| P2 | **Formation.cs** | scatter 队形、双编队合并定时器、编队光环、LoadFormation 换模板、IsRearrangementAllowed(L27–31) |
| P2 | **Garrison.cs/Turret.cs/GateComponent.cs** | Pickup 接送、外交翻面即时逐出、initGarrison/initTurrets(Garrison L14–18,Turret L13–16);友军接近自动开门、开合动画状态(Gate L5–7) |
| P2 | **BuildingAI.cs** | attack preference 排序表(L9 取最近敌替代)、手动集火 unitAITarget/focusTargets(L10) |
| P2 | TerritoryManager.cs | 影响力模型/BFS 连通/blink 为重建式近似(L8–10 自述),建议对照 CCmpTerritoryManager.cpp 校准 |
| P3 | PathfinderComponent.cs | 增量阻挡更新=全量 RebuildGrid 替代(L118–119,P1);通行类仅 default/ship |

---

## 4. M0/M3/M6 内核基础设施

| 项 | 状态 | 缺口 |
|---|---|---|
| 寻路 pass class 数据驱动 | 🟡 | 读 `pathfinder.xml` 全通行类(现仅 default/ship 硬编码) |
| 寻路异步任务化 | 🟡 | LongPathfinder 线程池模式:请求在回合边界收割,结果确定 |
| 寻路增量更新 | 🟡 | 阻挡变化局部刷新导航块(现全量重建) |
| push-out / 圆形障碍 | 🟡 | 对照 CCmpObstructionManager 的 shape 体系补齐 |
| 序列化类型覆盖 | 🟡 | U64/I64/Float、backref 共享对象;如需与原版存档互通再对齐二进制格式(当前自研格式 v11,可不对齐) |
| TurnManager 节奏/超时 | 🟡 | 回合超时节奏控制、客户端落后踢出策略 |
- [ ] 断线重连(NetworkDelayOverlay/NetworkStatusOverlay 无对应)
- [ ] 观战者/spectator 支持
- [ ] Templates:`actor|...` 模板装载、template_not_found 占位语义、schema 校验(Xeromyces 对应物)、hotloading
- [ ] STUN 打洞接入直连流程(StunClient.cs 存在,未确认接线)

---

## 5. M8 Petra AI(~20% 体量,最大单项缺口)

模块骨架齐全(headquarters/attackPlan/baseManager/worker/defense/naval/trade/garrison/queue/research/diplomacy 均有对应),但每个大幅简化。上游:`binaries/data/mods/public/simulation/ai/petra/`(31 个 .js ≈26k 行)。

- [x] 合并 Managers/PetraManagers 判明:AIComponent 只编一套(PetraManagers 全套+Headquarters);Petra/Managers.cs 是游戏层选中的另一套(都活),非真重复——选定统一路径=PetraManagers(2cd3a93 工人命令通道即此路径)
- [x] attackPlan 深化(a39ed51/9583b8c/ee5452f):集结相 Rallying+参战阈值+撤退判定(敌优比超限撤基地)+目标评分;余缺口:多波次编组、围攻路线(原版 comportment 全量)
- [x] headquarters 选址评分(0be4127 土地过滤+CC 适中距)+基地扩张评分(c45b980 近资源+离 CC 适中距);余缺口:人口规划
- [x] worker 供给评分(d5ba82f 性价比+拥塞+敌领土)+种田/狩猎分流(1e25ddc 先猎后种);余缺口:精细基址分配(原版 base 驻留/迁移)
- [x] data.json 配置体系(5075dbb:queues 时间窗阈值+unusedNoAllyTechs 补全;难度/性格/经济/防御/优先级/队列全量)
- [ ] mapMask 掩码工具
- [x] common-api 补全(AIEntity 能力面 19 项+EntityCollection 质心/近似位置/HasEntId;Filters 26/26 全)
- [x] researchManager 优先级(a3643df:人口/贸易/wanted/兜底四级,原版 update 核心语义)

---

## 6. M9 rmgen / 触发器 / 教程

### rmgen
- 库核心齐(RandomMap/TileClass/Area/Constraint/Objects/Terrains/Noise/Library/HeightmapLib + SafeMath)
- [ ] Placers:EntitiesObstructionPlacer、RandomPathPlacer(缺 2/10)
- [ ] Painters:CityPainter、ElevationBlendingPainter、TerrainTextureArrayPainter(缺 3/13)
- [ ] library.js 尾量:getObstructionSize、extractHeightmap、convertHeightmap1Dto2D、getDifficulties
- [ ] **地图脚本批量翻译:~12/~173 张已完成**,剩余按热度排期(Mainland/ Sahara/ Mediterranean 等先做)

### 触发器(Triggers/TriggerSystem.cs,279 行 vs Trigger.js ~1100 行)
- [ ] 事件类型:5 种条件/6 种动作 → 50+(OnTreasureCollected/OnStructureBuilt/OnDiplomacyChanged…)
- [ ] 定时器调度(DoAfterDelay/IntervalRepeats;现为纯轮询)
- [ ] 事件总线(CallEvent)
- [ ] 触发器状态序列化持久化

### 教程(TutorialEngine.cs,720 行)
- [ ] 改为读取 `simulation/tutorial/*.json` 数据驱动(现 IntroductoryTutorial 硬编 C#)
- [ ] goal Delay 计时器(现退化为 Ready 按钮)
- [ ] TriggerHelper 通用检查函数库成体系

---

## 7. M4 渲染(godot/)

- [x] **粒子系统**:EnvironmentParticles.cs(c16f21b 后):原版 art/particles/*.xml schema(emissionrate/lifetime uniform/velocity/size/color/blend)→ GPUParticles3D 映射装配;LoadDef 缓存 + BuildByName 直装(cloud/smoke/water_splash/...) 与 ImpactEffectPool(命中血雾/扬尘)互补——环境粒子就绪,需注册触发点的水面溅花/烟尘触发逻辑后续按需接
- [ ] **CinemaManager 过场动画**
- [ ] **天空盒代码**(ProceduralSky/SkyBox 无引用;MapEnvironment 需扩展)
- [ ] **战场贴花**(血迹/弹孔;注意 Actors 里现有 decal 是材质贴花,不是这个)
- [ ] CCmpDecay 尸体消融表现
- [ ] 后处理对齐原版选项(bloom/HQ 2x·4x 上采样/sharpness;已有 Lambert 光照基础)
- ✅ 已存在勿重复造:单位血条(DrawHealth/HealthBar)、集结点标记+路径线(Main.cs:2482)、投射物视觉池(ProjectilePool/ImpactEffectPool)、迷雾小地图层(FogTextureBuilder)

## 8. M5 GUI / 音频 / 相机 / GuiInterface

### GUI 页面(对照原版 page_*.xml)
- [ ] prelobby 三页(entrance/login/register)
- [ ] viewer(actor 查看器)
- [ ] mapbrowser
- [ ] campaigns 战役页(依赖触发器成熟)
- [ ] splashscreen/tips
- [ ] credits
- [ ] userreport
- [ ] locale_advanced(LocalePanel 扩展)
- [ ] mod 框架对话框全套(modmod/modio/incompatible_mods/termsdialog/msgbox/timedconfirmation/colormixer)
- [ ] autostart(自动化测试入口,CI 有用)
- [ ] 收敛 LobbyUI.cs 与 MainMenu.cs 两套主菜单(LobbyUI 部分子项 action=null)

### 音频(AudioManager.cs)
- [ ] 3D 空间化(无 AudioStreamPlayer3D 引用;现距离衰减靠手动增益)

### 相机(RTSCamera.cs vs GameView.cpp)
- [ ] 跟随选中单位
- [ ] 平滑加减速
- [ ] 俯仰限制、右键 pan 拖拽
- [ ] 缩放锚定鼠标指向点(现仅改 distance)
- [ ] 滚轮旋转热键映射对齐(camera.rotate.wheel.cw/ccw)

### GuiInterface(sim→UI 桥)
- [ ] 覆盖面约原版 1/5;TradePanel/DiplomacyPanel 绕桥直查 SimBridge 组件 → 统一走桥协议

---

## 9. 建议路线(执行顺序)

| 波次 | 内容 | 理由 |
|---|---|---|
| **P0** | §2 确定性清理(浮点清零 + Fixed.Pow + 门禁) | 所有联机/OOS 前置,越晚修成本越高 |
| **P0** | §3A DeathDamage/Upkeep/AttackDetection + §3B Combat 多攻击类型/再生/Promotion 换模板/Barter 漂移/Production 粒度+研究队列 | 单机玩法闭环完整性 |
| **P1** | UnitMotion 异步架构 + UnitSeparation push-pressure + 寻路增量更新/pass class 数据驱动 | 300 单位性能目标 |
| **P1** | Petra 深化(先合并双 Manager,再 attackPlan/headquarters/worker) | AI 是"能不能打完一局"的关键 |
| **P1** | 渲染五件套:粒子/天空盒/3D 音频/(过场、贴花可后置) | 表现力达标 |
| **P2** | rmgen 地图铺量 + 触发器事件总线 + 教程 JSON 化 + GUI 缺失页面 | 内容与体验对齐 |
