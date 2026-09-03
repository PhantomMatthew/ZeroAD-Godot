# 未完成项清单(2026-09-03 快照)

> 来源:[PORTING-GAPS.md](PORTING-GAPS.md) 全表核对。每条注明状态与上游对照。
> 完成项不在此列;架构性"判定不搬"见末节。

## 1. 模拟组件尾巴(§3B,对照 `simulation/components/*.js`)

| 组件 | 缺口 |
|---|---|
| ~~UnitAI~~ ✅ | Pickup 接送(乘客发起+运输侧 PICKUP 双子态+取消即完成握手)、编队控制组 obstruction 切换、炮塔站姿(standground 强切+还原)——本波全落 |
| **UnitMotion** | ~~异步路径~~✅ ~~朝向物理~~✅(PerformMove 转向块逐字:原地转/cos 减速/到站面向);余:waypoints 序列化(瞬态为刻意设计) |
| ~~UnitSeparation~~ ✅ | push-pressure 全量(编队豁免/交叉 nudge/per-template Weight/压力累积减速/CheckMovement 钳)+ 20m 空间分格——本波全落 |
| ~~Formation~~ ✅ | LoadFormation 换模板(executor 单编队换形分支)+ IsRearrangementAllowed(>5% 关键态禁排)——本波全落 |
| ~~Garrison/Turret/Gate~~ ✅ | initGarrison/initTurrets(场景 XML 解析+生成末统一应用)、门自动开关(盟友感应+门洞占用重试+阻挡旗态机)——本波全落 |
| ~~TerritoryManager~~ ✅ | 本波校准:成本加权洪泛(8m 瓦+costGrid)+8 向连通+blink 纯驱动+百分比 |
| ~~PathfinderComponent~~ ✅ | 增量阻挡更新:两格模型+脏区打点+脏 chunk 分层局部重连(本波) |
| **Barter** | per-player BarterMultiplier 接科技修正值管线 |

## 2. 内核基础设施(§4)

| 项 | 状态 | 缺口 |
|---|---|---|
| 寻路异步任务化 | ✅ | ticket+索引槽位+次回合收割(确定性);后台单任务(多 worker 需 per-worker LongPathfinder 实例——30MB scratch 驻留,按需再扩) |
| 寻路增量更新 | ✅ | 阻挡变化按脏矩形补丁+脏 chunk 局部重连(上游 UpdateGrid/HierUpdate 移植) |
| push-out / 圆形障碍 | 🟡 | 对照 CCmpObstructionManager 的 shape 体系补齐 |
| 序列化类型覆盖 | 🟡 | U64/I64/Float、backref 共享对象;如需与原版存档互通再对齐二进制格式(当前自研格式 v13) |
| TurnManager 节奏/超时 | 🟡 | 回合超时节奏控制、客户端落后踢出策略 |
| Templates | ⬜ | `actor\|...` 合成模板装载、template_not_found 占位语义 |

## 3. Petra AI(§5)

- ~~人口规划~~✅(trainMoreWorkers 全量:在训/在队计数、popPhase2 一阶门、saveResources、饱和闸、support/soldier 指数饱和、自适应批量);余:BuildDefenses 塔楼全量门控、StartingStrategy 低木 saveResources 联动。
- 圣物治疗者编排细节、海图换面 attackPlansEncounteredWater。
- (bombingAttacks/外交请求-应答/圣物夺取编排已于 142df83 落地。)

## 4. 渲染 / 音频 / GUI(§7–8)

- **渲染**:~~天气(上游本无天气系统=静态环境+地图粒子 actor;粒子 actor 装配本波落地)~~;~~蒙皮阴影动画姿势(共轭骨架代理,本波落地)~~;粒子触发点(水面溅花/烟尘按需求接)。
- **音频**:环境音多轨叠加(现单层)。
- **GUI**:间谍请求(需逐对 LOS 共享基建);mod.io minisigs Ed25519 验签(现只验存在性);campaigns 末关 endgame 页/useGameSetup 分支(初标"待触发器成熟",触发器总线 66e4f51 后已成熟,可重估)。
- **GuiInterface 桥**:覆盖面约原版 1/5,HUD/Minimap 热路径仍有零散 QueryInterface 直读(每帧性能敏感段,按需补桥)。

## 5. 模板 hotloading 尾巴(本波新增)

- 存量实体 sim 字段重灌(上游同款 15 年 TODO;EntityAssembler 为 add-only 组装,需逐组件 InitFromStats 通道)。当前 hotload 语义:新 spawn 立即生效 + 视觉全量重组装,仅 debug+单机。

## 6. 架构性保留项(判定不搬 / beyond-upstream)

- 触发器任意 JS 表达力——数据驱动模型是刻意的架构选择。
- 教程 JSON 化——上游目标表也在地图 JS 里,C# 目标表是等价物。
- 真断线重连(状态转移+回合追赶)——0 A.D. 0.29 亦无此能力。
- MotionBall/Settlement(演示/空壳件)、PopulationCapManager(职能已折叠进 PlayerComponent)、Upgrade 组件(命令层等价已存在,如需原版进度条 UI 再补)。

## 建议下一波

~~P1 性能三件套 + §3B 组件尾巴批量~~(2026-09-03 全落:增量寻路/异步路径/推挤压力
+ 门自动开关/编队组切换/炮塔站姿/重排闸门/LoadFormation/initGarrison/Pickup)。
存量最大项(2026-09-03 更新,TerritoryManager/转向/天气/阴影/人口规划均已落):
**BuildDefenses 塔楼门控全量**、Petra StartingStrategy saveResources 联动、
触发器表达力之外的 §4 序列化覆盖(U64/I64/Float)、GuiInterface 桥扩面。
