# 未完成项清单(2026-09-03 快照)

> 来源:[PORTING-GAPS.md](PORTING-GAPS.md) 全表核对。每条注明状态与上游对照。
> 完成项不在此列;架构性"判定不搬"见末节。

## 1. 模拟组件尾巴(§3B,对照 `simulation/components/*.js`)

| 组件 | 缺口 |
|---|---|
| **UnitAI** | Pickup 接送、编队控制组 obstruction 切换、炮塔站姿 |
| **UnitMotion**(408 行 vs 原 C++ ~2000) | 异步路径请求架构(现同步解算+0.3s 节流)、朝向更新、waypoints 序列化 |
| **UnitSeparation** | pushing-pressure、编队豁免、中途 nudge、per-template weight、CheckMovement 不可通行钳制、O(n²)→空间分格 |
| **Formation** | LoadFormation 换模板、IsRearrangementAllowed |
| **Garrison/Turret/Gate** | initGarrison/initTurrets、门自动开关 |
| **TerritoryManager** | 影响力模型/BFS 连通/blink 为重建式近似,需对照 CCmpTerritoryManager.cpp 校准 |
| **PathfinderComponent**(建议 P1) | 增量阻挡更新目前是"全量 RebuildGrid"替代 |
| **Barter** | per-player BarterMultiplier 接科技修正值管线 |

## 2. 内核基础设施(§4)

| 项 | 状态 | 缺口 |
|---|---|---|
| 寻路异步任务化 | 🟡 | LongPathfinder 请求在回合边界收割,结果确定;未线程池化 |
| 寻路增量更新 | 🟡 | 阻挡变化应局部刷新导航块(现全量重建) |
| push-out / 圆形障碍 | 🟡 | 对照 CCmpObstructionManager 的 shape 体系补齐 |
| 序列化类型覆盖 | 🟡 | U64/I64/Float、backref 共享对象;如需与原版存档互通再对齐二进制格式(当前自研格式 v13) |
| TurnManager 节奏/超时 | 🟡 | 回合超时节奏控制、客户端落后踢出策略 |
| Templates | ⬜ | `actor\|...` 合成模板装载、template_not_found 占位语义 |

## 3. Petra AI(§5)

- 人口规划(targetNumWorkers 动态)。
- 圣物治疗者编排细节、海图换面 attackPlansEncounteredWater。
- (bombingAttacks/外交请求-应答/圣物夺取编排已于 142df83 落地。)

## 4. 渲染 / 音频 / GUI(§7–8)

- **渲染**:天气系统;蒙皮阴影动画姿势(当前为绑定姿势投影);粒子触发点(水面溅花/烟尘按需求接)。
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

按路线表,**P1 性能三件套**挡着"300 单位不卡"目标:
1. 寻路增量阻挡更新(§2/§4)
2. UnitMotion 异步架构
3. UnitSeparation push-pressure + 空间分格

其次:§3B 组件尾巴批量清扫(Pickup/门开关/initGarrison 等一揽子)。
