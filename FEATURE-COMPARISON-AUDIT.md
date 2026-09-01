# 功能对比稽核:0 A.D. 原版(C++/JS)↔ ZeroAD-Godot 重写版(C#)

> **本文与 `PORTING-GAPS.md` 的关系**:`PORTING-GAPS.md` 是随每次提交增量勾选的"进度跟踪表"(维护者视角,逐 commit 更新)。本文件是 **2026-08-31 的一次独立、从零核对的横向稽核**——不依赖也不假设 `PORTING-GAPS.md` 已勾选项一定属实,而是重新读取两侧源码逐项验证,目的是给出当前时点尽量客观、可复现的差距快照。两文件建议都保留:本文件适合"整体评估现状/找方向",`PORTING-GAPS.md` 适合"按 commit 追踪具体任务"。
>
> **对比基准**:原版 0 A.D. 检出于 `/Users/matthew/SourceCode/gitea/0ad`(macOS)。重写版检出于本仓库,核对时 HEAD 为 `34cf5fd`(2026-08-31)。
> **方法**:三路子代理并行对原版与重写版做文件级枚举 + 关键逻辑抽查(非仅按文件名字符串匹配),文中出现的行号/文件路径均来自实际读取。
> **图例**:✅ 完整对等 · 🟡 部分实现(有量化或定性缺口) · ❌ 完全缺失 · ⚠️ 架构性重新定位(非能力缺口)

---

## 0. 总览

| 子系统 | 完成度(定性) | 一句话结论 |
|---|---|---|
| 模拟组件(83 个 JS 组件) | 高(核心闭环齐全) | 完全缺失仅 2 个真实缺口(弹道物理 MotionBall、Settlement);其余为 6+ 处中等粒度缺口(护卫互救、集结点多点排队、外交翻面即时逐出、编队特殊队形/光环等) |
| GUI(约 283 js / 230 xml 页面) | 约 80% | 页面覆盖广度高,深层子功能(AI 难度配置、大厅排行榜 UI、外交面板停火/间谍、手册热键动态替换)未接线 |
| 渲染 | 约 79% | 地形/水面/迷雾/相机/贴花/过场均语义对齐但实现路径不同;阴影靠"镜像代理"绕过负缩放限制,后处理只有 Glow 近似 Bloom/HDR、无 DOF |
| 音频 | 约 75% | 3D 空间化完全缺失(全部 `AudioStreamPlayer` 非 `AudioStreamPlayer3D`) |
| 网络/多人 | 锁步内核扎实,外围薄弱 | OOS+锁步完整;❌ 无断线重连/主机迁移;❌ 无观战者;大厅/STUN 为可用骨架非完整实现 |
| AI(Petra) | ~26.5%(19255 行→5107 行) | 决策主循环骨架逐字对齐,但 defenseManager(~10%)、navalManager(~8%,跨海运输恒失败)等大量子系统被砍成桩 |
| 寻路 | 算法层完整,数据/并发层缺 | 分层寻路/JPS/可见性图三层算法完整;❌ 通行分类未数据驱动(仅 2/10 类硬编码);❌ 无异步任务化/增量更新 |
| 随机地图(rmgen) | 图元 100%,地图忠实度中 | 84/84 地图"入口"全注册,39 张逐字移植(rmgen2 系 14 + 标志性地形 15 + 早前 10);rmgen2 库与 environment.js 已全量,图专属 rmbiome 已接;余 45 张仍是"贴皮通用大陆图" |
| 触发器 | 事件总线 ~32%,通用库 0% | 5 条件/6 动作数据驱动模型;`TriggerHelper.js` 32 个通用函数 0 个移植 |
| 教程/战役 | 主线完整 | `introductory_tutorial` 完整且修复了原版 bug;`starting_economy_walkthrough` 未移植 |
| 存档/录像 | 架构对等 | 双闭环完整;AI 的 AttackPlan/TransportPlan 等状态未纳入序列化 |

---

## 1. 模拟组件(`simulation/components/*.js` vs `src/ZeroAD.Sim/Components/*.cs`)

**统计口径**:原版 JS 组件 83 个,原版 C++ 引擎侧组件(`CCmp*.cpp`)约 30 个;重写版通过 `[Component("Name", ...)]` 注册的接口名 63 个,分布在 41 个 `.cs` 文件(存在多组件合并于一文件的情况,如 `Combat.cs`=Health+Attack)。

### 1.1 完全缺失(真实能力缺口)

| 原版文件 | 影响 |
|---|---|
| `MotionBall.js` + C++ `CCmpMotionBall.cpp` | 弹道/抛射体物理本体未移植。`DelayedDamage.cs` 用回合计数延迟队列代替真实弹道飞行,溅射伤害以目标**当前坐标**结算而非**真实弹着点**,对远程武器打移动目标的落点精度有连带影响 |
| `Settlement.js` | 全仓无任何引用,完全未移植 |

### 1.2 架构性重新定位(不计入缺口,但记录以防遗漏)

- `AIProxy.js` / `AIInterface.js`:原版是"JS 脚本 AI↔引擎"的差量快照桥;重写版 Petra AI 用 C# 原生直接调用 `ComponentManager`,该桥不再需要
- `Sound.js` / `StatusBars.js` / `RangeOverlayManager.js`:纯表现层职责,已下沉到 Godot 侧,内核无对应属预期设计(**待确认**:Godot `Main.cs`/`HUD.cs` 是否已消费 `<StatusBars>`/`<RangeOverlay>` 模板 tag 实现等价视觉规则)
- `Timer.js`:原版全局定时器服务;重写版各组件自行 `Tick(dt, cm)` 计时,功能等价但无统一服务类

### 1.3 部分实现且有明显缺口(按影响排序)

| 组件 | 缺口 |
|---|---|
| **UnitAI.cs**(原版 6842 行→重写版 ~2893 行) | `Guard.js` 只有"我护卫谁"单向记录,**没有**反向"谁在护卫我"列表——被护卫方遇袭不会广播通知护卫者反击(`MT_GuardedAttacked`/`CheckGuards` 全仓无对应);`AttackEntitiesByPreference` 按类型排序目标未移植,索敌退化为最近距离;多市场贸易路线切换(`SetupTradeRoute`/`SwitchMarketOrder`)简化为单跳;`AttemptObstructionMitigation` 绕障重试策略无对应 |
| **RallyPointComponent**(`ExtraComponents.cs:11-35`) | **严重简化**:原版支持每玩家多点排队 + 逐点关联指令类型(先去伐木点/先去修理再去驻扎等)+ 目标实体动态跟拍;重写版只有单个 `Position` 字段,消费处(`Production.cs:250`)只下发一次性 `Walk` |
| **Formation.cs** | `special`/`scatter` 特殊队形未移植(`:47`);双编队合并只存字段不执行(`:58,67`);编队光环 `ApplyFormationAura` 未移植(`:164`);RangeManager "normal" 标志未移植(`:362`) |
| **Garrison.cs / Turret.cs** | 外交翻面/易主时驻军与炮塔的**即时逐出**未实现,靠 Tick 轮询兜底,存在延迟窗口(已知缺口,作者自述);Pickup 接送外部锁定、`initGarrison`/`initTurrets` 地图预置驻军均未移植 |
| **BuildingAI.cs** | 攻击偏好排序表未移植(退化为最近距离);手动集火(`unitAITarget`/`focusTargets`)未移植——玩家无法手动指挥防御塔集火 |
| **Trader.cs / MarketComponent** | 战争迷雾内市场镜像切换未移植;海上贸易航线中继点(waypoints)未移植 |
| **DiplomacyComponent.cs** | "停火期间禁止宣战"门恒为 `false`(仅占位,`:44`);停火主体逻辑本身在 `EndGameManager.cs` 已落地 |
| **AuraComponent.cs**(原版 551 行) | 头注自称覆盖 137/151≈91%,约 9% 光环类型/数据未覆盖(疑似与 Formation 光环同一缺口) |
| **UnitMotion.cs** | 船只走水路 / 陆军走陆地的通行网格**分类选择逻辑**未移植(现一律走同一套判定),影响水陆混合地图船只寻路精度 |
| **UnitSeparation.cs** | 移动/静止单位推挤只处理简化情形,`CheckMovement` 越界裁剪(TODO `:99-126`)未移植 |
| **Combat.cs 溅射** | 衰减公式已移植,但衰减圆心用当前坐标而非 MotionBall 弹着点(与 1.1 的 MotionBall 缺失连带) |
| **TriggerSystem.cs** | `<TriggerPoint><Reference>` 模板 tag(9 个坐标标记模板,战役/遭遇战常用)**没有自动摄入路径**——仅单测手工调用 `RegisterTriggerPoint`,`TemplateLoader`/`EntityAssembler` 均未解析该 tag,直接照搬原版地图时触发点坐标不会自动可用 |

### 1.4 完整对等(经方法级抽查确认)

Health/Attack→Combat.cs、ResourceGatherer/Supply/Dropsite→Resources.cs、ProductionQueue→Production.cs、TechnologyManager/Researcher→Technology.cs、Repairable、Pack、StatusEffectsReceiver、DeathDamage/AutoBuildable/Upkeep/AttackDetection/BattleDetection/AlertRaiser→GameplayClosure.cs、Promotion、TerritoryDecay+CCmpTerritoryManager→Territory.cs+TerritoryManager.cs、EndGameManager+CeasefireManager+Wonder→EndGameManager.cs、Gate、ModifiersManager/ValueModificationManager、Upgrade(数据+执行分离)、FormationAttack 射程聚合部分、SkirmishReplacer、PopulationCapManager/Population、Loot/Looter、Treasure/TreasureCollector。

---

## 2. GUI

统计:原版约 283 个 `.js` + 230 个 `.xml`(`gui/` 全树);重写版约 130 个 `.cs`(`godot/Scripts/`,含 `Panels/`、`Lobby/`、`Options/`、`Replay/`、`Campaigns/` 子目录),采用"代码构建 UI"而非逐控件 `.tscn` 对照,故按**功能覆盖**而非文件数比对。

| 页面/功能 | 状态 | 缺口 |
|---|---|---|
| 主菜单 | ✅ | — |
| 单机游戏设置 gamesetup | 🟡 | **AI 难度/行为(Sandbox~VeryHard, Aggressive/Defensive/Balanced)下拉全仓无匹配**,槽位选 AI 后只有笼统 `AI` 选项;`MatchSettingsPanel.cs:61` 自述地图/胜利条件/种子未真正接线(硬编码 seed=42、civ=athen) |
| 多人大厅 lobby/prelobby | 🟡 | 评分数据已拉取(`GetBoardList()`)但**排行榜 UI 未渲染**;无 `AccountSettingsPage`/`ProfilePage`;游戏列表过滤器(玩家数/评分/地图尺寸等)未实现 |
| 对局内 HUD session | ✅(细节缺) | 外交面板停火倒计时/攻击请求/间谍请求/外交颜色切换均标注"not yet wired";交易面板缺价格随时间漂移的 UI 反馈(内核已有 drift,UI 显示静态价) |
| 加载页 | ✅(细节缺) | 缺引言展示(`QuoteDisplay.js`对应物) |
| 结算页 summary | ✅ | — |
| 选项/热键 | ✅ | — |
| 存档/读档 | ✅ | — |
| 回放菜单 | ✅(细节缺) | 缺回放过滤器(地图/时长/评分) |
| 文明百科 civinfo | 🟡 | 缺分小节展示(Heroes/Technologies/Bonuses/Structures 细分),目前只有笼统 Bonuses 一节 |
| 科技树 structree | ✅ | 坐标公式级复刻 |
| 地图浏览器 | ✅ | 内嵌于 MapPickerPanel 而非独立页,功能对等 |
| 战役 campaigns | ✅ | — |
| 教程面板 | ✅ | — |
| 手册 manual | 🟡 | 热键占位符(`hotkey.xxx`)未动态替换为当前绑定键 |
| 启动画面/制作名单/mod 管理/语言/用户报告 | ✅ | mod 面板自述"尚无运行时挂载,重启后暂不生效,仅配置持久化" |

**GUI 综合完成度估算**≈ **83%**(18 项计分基数,✅=1/🟡=0.5/❌=0)。

---

## 3. 渲染 / 音频

| 功能项 | 状态 | 缺口 |
|---|---|---|
| 地形渲染 | ✅(实现路径不同) | 原版实时 GPU 混合;重写版 `SplatBaker.cs` 加载时 CPU 预烘焙静态贴图——**运行期地形贴图不可再变**(无法做实时污染/雪覆盖等动态效果) |
| 水面渲染 | ✅(细节缺) | 无水下折射/焦散,仅 1 档水质(原版有多档 + 水下雾) |
| 战争迷雾/LOS | ✅ | — |
| 粒子系统 | ✅(覆盖面未证实全量) | 未见证据表明覆盖原版全部粒子分类(建筑烟尘、天气雨雪等) |
| 天空盒/天气 | 🟡 | 仅静态天空盒,无天气系统(云层变化等,原版本身也弱) |
| 阴影 | 🟡 | `ShadowProxyManager.cs` 用"镜像代理层"绕过 Godot 对负缩放实例不投影阴影的限制;蒙皮网格代理无骨架,只能以绑定姿势投影——**动画中的影子实际是静止姿势**,已知精度损失 |
| 后处理(bloom/HDR/AA) | 🟡 | Glow 近似 Bloom/HDR,无可运行时切换的等价链,**无景深(DOF)**;抗锯齿(FXAA/MSAA 2x-16x)映射完整 |
| 战场贴花 | ✅ | — |
| 过场动画 | ✅(数据源简化) | 硬编码路径表而非原版地图 XML `<Cinema>` 段动态注册 |
| 小地图/相机 | ✅ | — |
| UI 特效(选择圈/血条) | ✅(旗帜未确认) | 未见"旗帜/据点旗"专门渲染实现,可能靠 actor prop 系统承载 |
| **3D 空间音频** | ❌ | 全部使用 `AudioStreamPlayer`(非 `AudioStreamPlayer3D`),无位置衰减/立体声定位 |
| 音乐系统 | ✅(切换方式不同) | 原版战斗↔和平 crossfade,重写版直切 |
| 环境音效层 | ✅(单层限制) | 只能同时播一层环境音(原版可多轨叠加,如风声+鸟鸣+水声同时) |
| 语音提示/音量分层 | ✅ | — |

**渲染综合完成度**≈ **79%**,**音频**≈ **75%**。

---

## 4. 网络 / 多人

| 功能点 | 状态 | 依据/缺口 |
|---|---|---|
| 锁步命令队列/回合延迟 | ✅ | `NetTurnManager.cs` |
| OOS 检测 | ✅ | 每 20 回合 MD5 哈希双向比对 |
| **断线重连/主机迁移** | ❌ | `MultiplayerController.OnPeerDisconnected` 注释明写"out of scope" |
| **观战者模式** | ❌ | 全仓无 Observer/Spectator 槽类型 |
| XMPP 大厅 | 🟡 | 登录/MUC/聊天/游戏列表/排行榜数据结构齐全,但无封禁/踢人管理端逻辑,注释自称"接口先行" |
| STUN 打洞 | 🟡 | 地址发现(RFC5389)已实现,但**未接入 ENet 实际连接建立流程**(结果未见被消费) |

---

## 5. AI(Petra)

**行数统计**:原版 31 个文件共 **19,255 行** → 重写版 19 个文件共 **5,107 行**,约 **26.5%**。

| 模块 | 原版行数 | 覆盖率 | 关键缺口 |
|---|---|---|---|
| headquarters.js | 2455 | ~30% | 主循环顺序逐字对齐;绝大多数具体决策函数(建市场/防御选址/奇观/阶段判断)简化 |
| basesManager.js | 809 | ~6% | 无 dropsite 层级平衡、无跨基地征调、无码头分基地创建 |
| attackManager.js | 867 | ~40% | `bombingAttacks`(攻城游击)、`switchDefenseToAttack` 未移植 |
| attackPlan.js | 2305 | ~45% | 覆盖度较高;**无 Serialize(存档后进攻计划丢失)**,海运整合缺失 |
| **defenseManager+defenseArmy** | 988+657 | **~10%(最大缺口之一)** | 仅"就近拦截"简化模型;军队合并/分裂/资本化夺回完全未移植 |
| diplomacyManager.js | 586 | ~19% | 仅贡品+背叛逻辑;外交请求-应答状态机未移植 |
| victoryManager.js | 768 | ~8% | 无 capture_the_relic 的袭击/守卫/治疗编排 |
| tradeManager.js | 729 | ~25% | `prospectForNewMarket` 空 TODO,动态换路线未实现 |
| **navalManager+transportPlan** | 921+750 | **~8%(跨海运输恒失败)** | `TransportPlan.Update` 是桩代码(`State=Failed` 恒定)——**跨海攻击/资源调度完全不工作** |
| garrisonManager.js | 389 | ~31% | 无疗伤驻军、无衰变建筑驻军防腐 |
| researchManager.js | 245 | ~49% | — |
| queueManager.js | 636 | **~67%(覆盖度最高)** | — |
| worker.js | 1150 | ~30% | fisher 空 TODO,无建筑工避让/供给点重试判定 |
| baseManager.js | 1232 | ~24% | 采集分派链条逐字对齐;无多建筑并行调度/批量切资源 |
| config.js | 353 | ~59% | per-civ 建筑表仅填 2/15 文明,Cheat() 未移植 |
| entityExtend.js | 446 | ~26% | `AllowCapture` 恒 false,`GetAttackBonus` 恒 1.0 |
| chatHelper.js | 263 | 0% | 完全未移植(非玩法必需) |
| mapModule.js | 216 | ~66%(含关键桩) | **`CreateObstructionMap` 是返回 1×1 空图的桩函数**——依赖它的建造选址退化 |
| 其余(queue/queueplan*/buildManager/emergencyManager/startingStrategy) | — | 9%~54% | 详见子代理报告 |

**结论**:核心决策骨架完整,但海运(navalManager)、防御军编排(defenseManager)是两个"名义存在、实质不可用"的重大功能缺口,直接影响 AI 能否打出跨海攻势与有组织防御。

---

## 6. 寻路

| 功能点 | 状态 | 缺口 |
|---|---|---|
| 分层寻路 | ✅ | 全量同步 `Recompute`,无增量脏区更新(注释自认 P1 optimization) |
| 长程 JPS | ✅ | 完整实现,`JumpPointCache` 有意省略(原版默认也关) |
| 短程可见性图 | ✅(未做性能优化) | AABB 近似而非原版四象限剪枝+边桶优化 |
| 单位互推 | ✅ | 数值核对与原版吻合 |
| **通行性分类数据驱动** | ❌ | 原版 `pathfinder.xml` 定义 10 个通行类;C# `PassabilityGrid.cs` **只硬编码 2 个**(Default/Ship),`large`/`building-land`/`building-shore`/`unrestricted` 等 Petra 建造选址依赖类全缺(与 §5 的 `CreateObstructionMap` 桩函数互为因果) |
| **异步任务化** | ❌ | 全目录无 `async`/`Task.Run`,寻路同步阻塞主线程 |
| **增量更新** | ❌ | 每次阻挡变化触发全量重建,非 dirty-rectangle |

---

## 7. 随机地图生成(rmgen)

**精确统计**(非旧文档口径):原版去重后共 **84 张**可选地图(8 张带独立 `*_triggers.js`);rmgen/rmgen2 库文件共 **36+6+2+4=约 48 个**。

| 功能点 | 状态 | 缺口 |
|---|---|---|
| 地图入口注册 | ✅ 84/84 | 姓名逐一核对无遗漏无多余 |
| 图元库(placer/painter) | ✅ 10/10 + 13/13 | 图元原语齐全 |
| **rmgen2 库(gaia.js + setup.js)** | ✅ | 全量移植;14 张依赖它的图(ambush/bahrain/empire/frontier/harbor/hells_pass/lions_den/marmara/mediterranean/ngorongoro/pompeii/ratumacos/red_sea/stronghold)已逐字接线 |
| **environment.js** | ✅ | 天空/太阳/环境光/水体/雾/后处理全量;62 张图的 set* 序列表驱动施加(RNG 位置对齐上游),Godot 侧已接线 |
| **地图生成忠实度** | 🟡 39/84 | 39 张逐字移植:rmgen2 系 14 张 + 标志性地形 15 张(islands/rivers/english_channel/canyon/gear/coast_range/cycladic_archipelago/corinthian_isthmus/dodecanese/corsica/river_archipelago/pyrenean_sierra/belgian_uplands/schwarzwald/caledonian_meadows)+ 早前 10 张。**余 45 张仍只覆盖材质/生物群系参数**,未覆盖 `GenerateTerrain`/`GenerateResources`,实际生成的是"贴皮通用大陆图" |
| rmbiome 专属群系 | ✅ | alpine/、fields_of_meroe/、gulf_of_bothnia/、persian_highlands/ 四套图专属 biome 已接;cappadocian_badlands/flood/island_stronghold 的显式白名单一并对齐 |

---

## 8. 触发器系统

| 功能点 | 状态 | 缺口 |
|---|---|---|
| 事件总线 | 🟡 约 6/19(~32%) | 已接线:`OnOwnershipChanged/OnStructureBuilt/OnTrainingFinished/OnResearchFinished/OnTreasureCollected` + `OnInterval` 轮询。**未接线**:`OnPlayerCommand/OnPlayerDefeated/OnPlayerWon/OnDiplomacyChanged/OnAttackDetected/OnConstructionStarted/OnEntityRenamed/OnResearchQueued/OnTrainingQueued/OnCinemaPath(Queue)Ended/OnDeserialized` |
| 条件/动作模型 | 🟡 架构性重设计 | 数据驱动 5 条件/6 动作枚举,取代原版任意 JS 脚本,表达力远小于原版 |
| **TriggerHelper.js 通用函数库** | ❌ 0/32(0%) | `GetPlayerIDFromEntity/SpawnUnits/SpawnGarrisonedUnits/SetUnitStance/GetLandSpawnPoints` 等 32 个函数无一对应可复用 API,等价逻辑散落在各地图脚本类里手写,复用性低 |
| 具体地图触发脚本 | 🟡 7/8 | `wall_demo_triggers.js`(演示地图)未移植,其余 7 个均为简化版 |

---

## 9. 教程 / 战役

| 功能点 | 状态 | 缺口 |
|---|---|---|
| `introductory_tutorial` | ✅ 完整 | ~25 个教学目标逐条对应,且**修复了原版 Delay 目标从未被 Tick 驱动的 bug** |
| `starting_economy_walkthrough` | ❌ | 未见对应关卡定义或注册 |
| 教程框架可扩展性 | 🟡 | `CampaignLevel`/`GoalSpec` 数据驱动框架已具备,但仅 introductory 一档双轨维护(代码版+数据版) |

---

## 10. 存档 / 录像

| 功能点 | 状态 | 缺口 |
|---|---|---|
| 序列化框架 | ✅ | `HashSerializer`(MD5,对应 OOS)+ `BinarySerializer`(存档/网络)双实现,小端序显式处理 |
| 存档 | ✅(AI 部分缺) | Petra `AttackPlan`/`TransportPlan`/多个 queueplan 类未实现 Serialize/Deserialize——读档后 AI 进攻/运输状态会丢失需重新生成 |
| 录像 | ✅ 架构对等 | 自研 `"0ADREPL"` 格式(初始状态+命令流+哈希校验尾段),与原版思路一致但格式不互通(设计上本就不追求兼容,见 `godot-rewrite-plan.md` 非目标) |

---

## 11. 优先级建议(基于本次稽核的影响面排序)

| 优先级 | 内容 | 理由 |
|---|---|---|
| P0 | AI `navalManager.TransportPlan` 桩函数修复(跨海运输恒失败) | "名义存在、实质不可用",直接卡死跨海地图的 AI 行为 |
| P0 | 寻路通行性分类数据驱动(补齐 10 类) + `PetraMapModule.CreateObstructionMap` | 二者互为因果,直接影响 AI 建造选址与城墙/舰船寻路正确性 |
| P0 | AI `defenseManager` 军队编组/合并/分裂 | 现状"就近拦截"过于简化,AI 防御几乎不设防 |
| P1 | rmgen 余 45 张地图的地形结构忠实移植(rmgen2 系 14 张与标志性地形 15 张已完成) | 当前"贴皮通用大陆图"直接影响地图多样性体验 |
| P1 | `RallyPointComponent` 多点排队 + 指令类型 | 影响新造单位的自动化分派体验(先采集/先修理等) |
| P1 | Guard 双向反击、Garrison/Turret 外交翻面即时逐出 | 影响战斗细节手感,已知延迟窗口 |
| P1 | 触发器事件总线补齐(尤其 `OnPlayerDefeated/OnPlayerWon/OnDiplomacyChanged`) + `TriggerHelper` 通用库 | 是战役/自定义地图内容扩展的基础设施,当前 0% 复用库会拖慢后续每张地图触发脚本的移植速度 |
| P2 | 网络断线重连/观战者 | 单机与固定对局可玩,但正式发布前的多人体验刚需 |
| P2 | 音频 3D 空间化 | 相对独立、修复成本可控,但目前是"完全缺失"级别 |
| P2 | AI 存档序列化补全(AttackPlan/TransportPlan/queueplan) | 不影响确定性哈希,但读档后 AI 行为会有轻微跳变 |

---

## 附:与 `PORTING-GAPS.md` 的口径差异说明

`PORTING-GAPS.md` 在多处标注"✅ 已落地"的项目,本次独立核对发现部分属于"骨架/主链路已通,但支线大幅简化"(例如 Petra 的 attackPlan/headquarters/worker/baseManager 均已被 `PORTING-GAPS.md` 标记为进展项,但本次逐文件行数对比显示实际覆盖率普遍在 25%-45% 区间,defenseManager/navalManager 更低至 ~8-10%)。建议后续更新 `PORTING-GAPS.md` 时,对已勾选项补充量化覆盖率或明确"骨架完成度 vs 逐字忠实度"的区分,避免"✅"被解读为"零缺口"。
