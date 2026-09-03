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
| M5 GUI | ★★★★☆ | 页面基本齐(prelobby/campaigns/viewer/credits/mod 对话框/autostart 全落);剩引擎级 mod 挂载、GuiInterface 桥收敛 |
| M6 网络 | ★★★★★ | 锁步+OOS+大厅+观战+STUN+掉线 AI 接管(4b19ce4);真重连 beyond-upstream |
| M7 存档录像 | ★★★★★ | 双闭环,亮点项 |
| M8 Petra AI | ★★★★☆ | 攻防军+运输+编组+存档骑缝全落地;剩 bombingAttacks/外交应答/圣物编排支线 |
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
| P1 | **UnitAI.cs** | ✅ CHASING/FINDINGNEWTARGET+Heal/Treasure 续单+偏好索敌(71344aa);余:Pickup 接送/编队控制组切换/炮塔站姿 |
| P1 | **Combat.cs** | ✅ 全清:多攻击型分立(1bbcffc)+溅射范围伤害+弹道延迟(b7b907d)+回血再生(40124c0)+DeathDamage 联动(1754989) |
| P1 | **Production.cs** | ✅ 逐个出兵 + autoQueue(ea13f60);Upkeep 数据无条目(0 科技/模板带 upkeep,机制存在无需激活) |
| P1 | **Technology.cs** | ✅ 全清:研究队列+取消退款(68f8e24)+训练 requirements 科技门(521f3b3) |
| P1 | **PromotionComponent** | ✅ 已落地(40124c0):模板驱动换模板晋升(同位同向同主/血量折算/XP 结转),装配修复 |
| P1 | **BarterSystem.cs** | ✅ 价漂移已落地(40124c0):每笔推涨+周期回落+存档骑缝;仅 per-player BarterMultiplier 待接(科技修正值管线) |
| P2 | **UnitMotion.cs**(408 行 vs 原 C++ ~2000) | 异步路径请求架构(现同步解算+0.3s 节流 L41–47)、朝向更新、waypoints 序列化(L38–40 瞬态) |
| P2 | **UnitSeparation.cs** | pushing-pressure、编队控制组豁免、中途 nudge、per-template weight、CheckMovement 不可通行钳制(TODO L99);O(n²) → 空间分格 |
| P2 | **Formation.cs** | ✅ scatter + 双编队分簇合并 + 编队光环(6935798);余 LoadFormation 换模板/IsRearrangementAllowed |
| P2 | **Garrison.cs/Turret.cs/GateComponent.cs** | ✅ 外交翻面即时逐出(e84c6bc);余 Pickup 接送/initGarrison/initTurrets/门自动开关 |
| P2 | **BuildingAI.cs** | ✅ 偏好排序+手动集火(752ae63) |
| P2 | TerritoryManager.cs | 影响力模型/BFS 连通/blink 为重建式近似(L8–10 自述),建议对照 CCmpTerritoryManager.cpp 校准 |
| P3 | PathfinderComponent.cs | 增量阻挡更新=全量 RebuildGrid 替代(L118–119,P1);通行类仅 default/ship |

---

## 4. M0/M3/M6 内核基础设施

| 项 | 状态 | 缺口 |
|---|---|---|
| 寻路 pass class 数据驱动 | ✅ | pathfinder.xml 9 类注册表+XML 加载(d5f0b94);水陆分类生效 |
| 寻路异步任务化 | 🟡 | LongPathfinder 线程池模式:请求在回合边界收割,结果确定 |
| 寻路增量更新 | 🟡 | 阻挡变化局部刷新导航块(现全量重建) |
| push-out / 圆形障碍 | 🟡 | 对照 CCmpObstructionManager 的 shape 体系补齐 |
| 序列化类型覆盖 | 🟡 | U64/I64/Float、backref 共享对象;如需与原版存档互通再对齐二进制格式(当前自研格式 v11,可不对齐) |
| TurnManager 节奏/超时 | 🟡 | 回合超时节奏控制、客户端落后踢出策略 |
- [x] 观战者/spectator(4b19ce4:observer 不占槽+全图视野+命令拦截;ClientHello 握手)
- [x] STUN 打洞接入直连流程(4b19ce4:host 建服探测公网地址+大厅状态展示)
- [x] 局中掉线优雅降级(4b19ce4:槽位转 AI 接管+全端广播);真正断线重连
  (状态转移+回合追赶)超出上游 0.29 能力面(原版亦无)——标 beyond-upstream
- [ ] Templates:`actor|...` 模板装载、template_not_found 占位语义
- [x] **模板 schema 全量校验(Xeromyces 级)**(本波):`Content/Schema/`——
  RelaxNG 子集校验器(RngPattern/RngValidator:语料全构造 element/attribute/optional/
  choice/zeroOrMore/oneOrMore/interleave/ref/text/data+param/value/empty/list/anyName;
  结构-内容分离两阶段匹配,interleave 按名分划,空文本元素合法);
  组件 Schema 受限 JS 求值提取(ComponentSchemaExtractor:字符串/反引号/注释/
  Resources.BuildSchema/BuildChoicesSchema/RequirementsHelper/AttackHelper/同文件
  prototype 引用;MotionBall.js → MotionBallScripted 注册名)+ 原生 GetSchema 逐字表
  (NativeComponentSchemas:12 真 schema + 系统空壳);grammar = GenerateSchema 骨架
  (decimal/nonNegativeDecimal/positiveDecimal/anything + 每组件 interleave define +
  anyName 根 + optional parent);strict 拒载(上游语义,m_TemplateSchemaValidity memo)。
  校验对象是**合并树**(含 ParamNode 修复:@datatype 保留 + mul_round op 补全)。
  sweep:全部具体模板 0 错误;177 个 template_* 抽象父 + special/actor 上游同结局
  拒载(从不独立请求,记录在测试注释)。mixins//special/filter/ 图层不独立校验
  (上游同)。测试:SchemaValidatorTests 23 + ExtractorTests 15 + HotloadTests 5 +
  SweepTests 3(全语料金丝雀)。
- [x] **模板 hotloading**(超越上游——上游 15 年 TODO,ICmpTemplateManager.h:127):
  TemplateLoader.Invalidate/InvalidateAll + Godot 侧 TemplateHotReloader
  (FileSystemWatcher 分层监视 templates/+components/,300ms 去抖,mixin/filter/组件
  变更全失效+grammar 重建,strict 重校验进 Diag,RebuildAllVisuals 视觉重组装;
  仅 debug 构建 + 单机——MP 热载必 OOS)。存量实体 sim 字段重灌仍缺
  (上游同款 TODO;EntityAssembler add-only,需逐组件 InitFromStats 通道——留 backlog)

---

## 5. M8 Petra AI(~20% 体量,最大单项缺口)

模块骨架齐全(headquarters/attackPlan/baseManager/worker/defense/naval/trade/garrison/queue/research/diplomacy 均有对应),但每个大幅简化。上游:`binaries/data/mods/public/simulation/ai/petra/`(31 个 .js ≈26k 行)。

- [x] 合并 Managers/PetraManagers 判明:AIComponent 只编一套(PetraManagers 全套+Headquarters);Petra/Managers.cs 是游戏层选中的另一套(都活),非真重复——选定统一路径=PetraManagers(2cd3a93 工人命令通道即此路径)
- [x] attackPlan 深化(a39ed51/9583b8c/ee5452f 集结相/撤退判定;a181b83 全量重写:多波次
  编组 buildOrders/trainMoreUnits/assignUnits/addSiegeUnits、围攻路线 getPathToTarget/
  setRallyPoint 领土边界集结/checkTargetObstruction 城墙阻断、attackManager getEnemyPlayer
  +Raid/Rush/Attack/Huge 轮换);余缺口:overseas 海军运输、bombingAttacks
- [x] headquarters 选址评分(0be4127/c45b980;34cf5fd 全量:findEconomicCCLocation 领土图
  网格扫描+资源密度 splat+CC/DP 距离门+可放置校验;checkBaseExpansion/buildNewBase 逐字门控)
  ;余缺口:人口规划(targetNumWorkers 动态)
- [x] worker 精细基址分配(f4ffc4c:dropsite 三层补给 nearby/medium/faraway + startGathering
  七级行走(宝藏→猎→本基地→他基地→助建地基→faraway→冷却)+ 拥塞/敌领土/采集速率表过滤
  + pickMostNeededResources 需求序;d5ba82f/1e25ddc 为其前置)
- [x] data.json 配置体系(5075dbb:queues 时间窗阈值+unusedNoAllyTechs 补全;难度/性格/经济/防御/优先级/队列全量)
- [x] mapMask 掩码工具(MapMask.cs 常量 + PetraMapModule.CreateBorderMap:地图外/边界 + 领土窄/宽前线,原版 mapMask.js + createBorderMap 语义)
- [x] common-api 补全(AIEntity 能力面 19 项+EntityCollection 质心/近似位置/HasEntId;Filters 26/26 全)
- [x] defenseManager 军队模型(881b8c6:编组/合并/分裂/夺回/转攻,defenseArmy.js 657 行移植)
- [x] navalManager 跨海运输(e48f00b:TransportPlan 状态机+分船+运船训练;overseas 进攻接入)
- [x] AI 存档序列化(0104867:计划/队列/攻防军/基地骑缝,存档 v12+录像 v2)
- [ ] bombingAttacks 攻城游击、diplomacyManager 请求-应答、victoryManager 圣物编排(支线)
- [x] researchManager 优先级(a3643df:人口/贸易/wanted/兜底四级,原版 update 核心语义)

---

## 6. M9 rmgen / 触发器 / 教程

### rmgen
- 库核心齐(RandomMap/TileClass/Area/Constraint/Objects/Terrains/Noise/Library/HeightmapLib + SafeMath)
- [x] Placers:EntitiesObstructionPlacer、RandomPathPlacer(53e87f7)
- [x] Painters:CityPainter、ElevationBlendingPainter、TerrainTextureArrayPainter(53e87f7)
- [x] library.js 尾量(53e87f7/0746e5f:getObstructionSize/extractHeightmap/convertHeightmap1Dto2D/getDifficulties 全量)
- [x] **rmgen2 库**(setup.js + gaia.js 全量:tile class 表/addElements/createBases + addBluffs/Hills/Lakes/Mountains/Plateaus/Valleys/Decoration/Props/LayeredPatches/Forests/Metal/SmallMetal/Stone/Berries/Animals/Fish/StragglerTrees + bluff 几何);配套补 createPassage/getTeamsArray/placeLine/placeStronghold/AdjacentToAreaConstraint,playerPlacementByPattern 五种模式齐
- [x] **environment.js**(Rmgen/Environment.cs:天空/太阳/环境光/水体/雾/后处理;62 张图的 set* 序列由 MapEnvironments.cs 表驱动施加,RNG 消耗位置对齐上游;Godot 侧 MapEnvironment.FromRmgen + WaterRenderer.FromRmgen 接线)
- [x] **图专属 rmbiome**(alpine/、fields_of_meroe/、gulf_of_bothnia/、persian_highlands/ 四套 + cappadocian_badlands/flood/island_stronghold 的显式白名单)
- [x] **地图脚本忠实度:84/84 全部逐字移植**
  - rmgen2 系 14 张全量:ambush/bahrain/empire/frontier/harbor/hells_pass/lions_den/marmara/mediterranean/ngorongoro/pompeii/ratumacos/red_sea/stronghold
  - 早前 10 张:mainland/saharan_oases/hellas/wild_lake/oasis/alpine_valley/arctic_summer/aegean_sea/archipelago/african_plains/ardennes_forest
  - 其余 60 张按批全量化(PortF–PortS),含 jebel_barkal(PMP 地形 + 确定性兜底)、unknown(多布局随机器,全子布局齐)、continent(此前顶着 Map2 名字跑 mainland 算法,整张图没有海)
  - 触发器脚本 8 张全量(polar_sea/elephantine/survivalofthefittest/flood/extinct_volcano/danubius/jebel_barkal,wall_demo 原版空文件无需移植)
  - 新增棘轮测试(Every_Registered_Map_Implements_Its_Own_Generator):任何图退化回"跑基类 mainland 算法"会被点名,不再能悄悄发生

### 触发器(Triggers/TriggerSystem.cs vs Trigger.js ~1100 行)
- [x] 事件总线全量接线(66e4f51:原版 eventNames 全表;OnDeserialized 读档尾;
  ConstructionStarted/EntityRenamed 新事件;过场双事件经 CinemaManager 转 CallEvent)
- [x] TriggerHelper 通用库(66e4f51:32 函数全量,Triggers/TriggerHelper.cs)
- [ ] 事件条件/动作表达力:数据驱动模型仍远小于原版任意 JS(架构性重设计,保留)
- [x] 触发器状态序列化持久化(早前已落;读档 OnDeserialized 已接)

### 教程(TutorialEngine.cs)
- [x] starting_economy_walkthrough 移植(66e4f51:26 目标全量,按图名选引擎,
  战役 eco_walkthrough 关卡直通)
- [ ] 教程 JSON 数据驱动(现 C# 目标表硬编;CampaignLevel/GoalSpec 框架已具备)
- [x] goal Delay 计时器(早前已落)+ TriggerHelper 成体系(66e4f51)

---

## 7. M4 渲染(godot/)

- [x] **粒子系统**:EnvironmentParticles.cs(c16f21b 后):原版 art/particles/*.xml schema(emissionrate/lifetime uniform/velocity/size/color/blend)→ GPUParticles3D 映射装配;LoadDef 缓存 + BuildByName 直装(cloud/smoke/water_splash/...) 与 ImpactEffectPool(命中血雾/扬尘)互补——环境粒子就绪,需注册触发点的水面溅花/烟尘触发逻辑后续按需接
- [x] **CinemaManager 过场动画**(3b48944+c3947ed:相机路径队列播放+OnCinemaPathEnded/QueueEnded 事件广播;数据驱动地图 <Paths> 段注册+触发器驱动剧情)
- [x] **天空盒**(ef5d6b5:SkyBox.cs——<SkySet>名 → 5 面贴图 + 程序化天空兜底)
- [x] **战场贴花**(4bb75a1+5c0eea8:BattleDecals——击杀血斑+炮击弹坑/建筑毁坏贴花,45s/90s 消融回收;与 ImpactEffectPool 互补)
- [x] CCmpDecay 尸体消融表现(4bb75a1:贴花线性淡出+缩小消融回收)
- [x] 后处理对齐原版选项(5999290 bloom/MSAA/sharpness;ded5353 水质两档 + fa3b170 DOF 远距模糊)
- ✅ 已存在勿重复造:单位血条(DrawHealth/HealthBar)、集结点标记+路径线(Main.cs:2482)、投射物视觉池(ProjectilePool/ImpactEffectPool)、迷雾小地图层(FogTextureBuilder)

## 8. M5 GUI / 音频 / 相机 / GuiInterface

### GUI 页面(对照原版 page_*.xml)
- [x] prelobby 三页(entrance/login/register;5d4c2d3)
- [x] viewer(actor 查看器;fc7637a)
- [x] mapbrowser(68027b1:网格浏览)
- [x] campaigns 战役页(28c9f9d:CampaignTemplate/Run 数据层 + setup/menu/new/load 四页,胜利回写 MarkLevelComplete;末关 endgame 页与 useGameSetup 分支待触发器成熟)
- [x] splashscreen/tips(199c7b5/d9216b8)
- [x] credits(580bc7d)
- [x] userreport(b5d5a9c)
- [x] locale_advanced(b5d5a9c:LocalePanel Advanced 按钮 + 自定义 locale)
- [x] mod 框架对话框全套(fb43094:msgbox/timedconfirmation/termsdialog+terms 框架/colormixer/incompatible_mods/modmod 全量/modio;mod.io v1 REST 实接,缺 minisigs Ed25519 验签——只验存在性)
- [x] autostart(88811b6:CLI -autostart-* 全参数体系,含 MP host/client 分支与 AI 难度接线;CI 可用)
- [x] 收敛 LobbyUI.cs 与 MainMenu.cs 两套主菜单(7769ad8:假主菜单删除,MainMenu.tscn 唯一;Mode=Lobby 弹回)
- [x] GUI 尾巴(ec989d0:gamesetup/大厅 AI 难度+性格下拉、外交停火倒计时+盟友攻击请求
  (锁步命令+AI 评估)、大厅排行榜 UI、手册热键动态替换、加载页引言、易物漂移 1s 刷新);
  间谍请求待逐对 LOS 共享基建
- [x] 引擎级 mod 挂载(1050faf:VfsResolver 数据分层挂载,sim 数据下一局生效;美术重启重导)

### 音频(AudioManager.cs)
- [x] 3D 空间化(b260e15:AudioStreamPlayer3D 池,攻击/死亡/训练位置衰减;
  人声留 2D——原版选令语音本就非空间化);环境音单层限制(原版多轨叠加)未移植

### 相机(RTSCamera.cs vs GameView.cpp)
- [x] 跟随选中单位(6dc3c4a:F 键,输入打断)
- [x] 平滑加减速(6dc3c4a:CSmoothedValue 指数平滑)
- [x] 俯仰限制、右键 pan 拖拽(6aa8abe)
- [x] 缩放锚定鼠标指向点(8d7fef9:zoom-to-cursor)
- [x] 滚轮旋转热键映射对齐(8d7fef9:Shift+Wheel)

### GuiInterface(sim→UI 桥)
- [x] TradePanel/DiplomacyPanel/MatchSettingsPanel 绕桥直查已收敛(fb43094 后 commit:桥新增 GetDiplomacyState/GetBarterQuote/GetPlayerRoster DTO,面板零内核直查)
- [ ] 覆盖面约原版 1/5:HUD/Minimap 热路径仍有零散 QueryInterface 直读(每帧性能敏感段,按需补桥)

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
