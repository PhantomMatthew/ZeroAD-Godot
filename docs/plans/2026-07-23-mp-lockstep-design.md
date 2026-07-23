# MP 锁步:命令路由 + 回合屏障 设计文档

> 日期:2026-07-23 · 状态:已定稿(经 brainstorming 逐段确认)
> 范围:0 A.D. → Godot 重写的多人锁步修复。对齐原版 `source/network/NetTurnManager` 语义。

## 1. 背景与问题

当前 MP 实现(`godot/Scripts/MultiplayerController.cs` + `src/ZeroAD.Sim/Net/NetTurnManager.cs`)存在结构性缺陷,实战必 OOS:

1. **P2P 互发,无主机权威**:`SubmitCommand` 直接 `Rpc("RemoteCommand")` 单条广播,无回合汇总。
2. **无回合屏障**:`SimBridge._Process` 按墙钟 0.1s 自由推进 sim,不等远端命令到位。
3. **命令双重执行**:`Main.cs:668-686` 右键 Move/Gather/Attack 先本地立即执行、又提交网络命令。
4. **建造/研究/集结点不走网络**:`PlaceBuilding`(`Main.cs:824`)、`ResearchTech`(`Main.cs:814`)、`CommandSetRallyPoint` 直接改 sim。
5. **命令语义两套**:`NetTurnManager.ExecuteCommand` 有一份简化实现,`SimBridge`/`Main.cs` 有另一份"真实"实现,必然漂移。
6. OOS 只比哈希,无状态 dump;种子与玩家 ID 硬编码(seed=42,host=1/client=2)。

## 2. 决策记录(用户已确认)

| 决策点 | 结论 |
|---|---|
| 锁步架构形态 | **主机权威制**:客户端命令发主机,主机按回合汇总广播回合包,各端(含主机)只在收到回合包后推进 |
| 单机命令路径 | **与联机统一**:单机也走本地延迟命令队列(COMMAND_DELAY=2 回合,200ms),SP/MP 同一条代码路径 |
| OOS dump | **二进制+文本双 dump**:文本 dump 全确定性排序,两端直接 `diff` 定位 |

## 3. 总体架构与数据流

```
玩家输入 (Main.cs)
   │
   ▼
统一入口 SubmitCommand(NetCommand)          ← 所有命令类型唯一入口,无 IsMultiplayer 分支
   │
   ▼
本地命令槽:排入 turn = currentTurn + COMMAND_DELAY(2)
   │
   ├── 单机:本地 NetTurnManager 即"主机",回合到点直接就绪
   └── 联机:
        客户端 ──SubmitCommandsToHost(turn=N+2, batch)──▶ 主机
        主机收齐 expectedPlayers 在 turn N 的命令(含自己)
        主机 ──BroadcastTurnBundle(turn=N, 全体命令)──▶ 全体(含自己,CallLocal)
   │
   ▼
各端收到 TurnBundle(N) → 落槽 → 屏障解除
   │
   ▼
SimBridge._Process 每 0.1s 尝试推进:
   if (!NetTurnManager.CanAdvanceTurn()) → 本 tick sim 停摆(渲染继续)
   else → 执行该回合全部命令(按 Player 排序) → TickSimulation → AdvanceTurn
```

要点:

- **回合屏障在 SimBridge._Process**:推进权由网络层供给,不由墙钟。
- **主机与客户端执行序列逐字节一致**:主机自己的命令同样排队、随 bundle 发回自己再执行。
- **空批心跳**:每端每推进一回合,向主机发送 `currentTurn+2` 回合的命令批次(可为空);主机据此判定"收齐"。没有它主机会永久等待沉默客户端(原版机制)。
- **单机 = 退化情形**:同一 NetTurnManager 本地即"收齐",无网络收发。
- **OOS 主机裁决**:各端 20 回合哈希上报主机,主机比对全体,不一致广播 OOS,各端各自 dump。

## 4. 内核侧改动(`src/ZeroAD.Sim/`)

### 4.1 SimCommandExecutor(新,`Net/SimCommandExecutor.cs`)

命令执行上收内核,全工程只剩一份命令语义:

- `Apply(cmd)` 单入口,分发 `ApplyMove/ApplyGather/ApplyAttack/ApplyTrain/ApplyBuild/ApplyResearch/ApplyRallyPoint`。
- `SpawnFoundation`、建造成本校验/扣费、`CommandResearch` 从 Godot 层**搬进内核**。UI 只保留按钮置灰的展示性预判,不碰 sim 状态。
- `NetTurnManager.ExecuteCommand` 删除自有实现,委托 executor;`SimBridge.CommandX` 同样委托。

### 4.2 NetCommand 类型补全

| 类型 | 改动 |
|---|---|
| Move/Gather/Attack/Train | 不动 |
| Build | 改载荷:模板名 + 世界坐标(Fixed x/z)+ builder 实体 id(现载荷是过时的 grid 坐标且无执行分支) |
| Research | 新增;tech 名复用 `TemplateName` 字段 |
| SetRallyPoint | 新增:建筑 id + 目标实体/坐标 + 类型 |

### 4.3 NetTurnManager 重构(每端一份)

- 保留:回合槽、按 Player 排序执行、每 20 回合哈希、OOS 事件。
- 新增主机职责:`HostIngestCommands(player, turn, cmds)`;收齐后产出 TurnBundle,触发 `OnTurnBundleReady` 由传输层广播。
- 新增客户端屏障语义:`CanAdvanceTurn()` 仅当 turn N 的 bundle 已落槽;`AdvanceTurn()` 只消费 bundle。
- OOS 改主机裁决(见 §3)。

## 5. Godot 侧改动(`godot/Scripts/`)

### 5.1 MultiplayerController 瘦身为纯传输管道(5 个 RPC,全部 Reliable)

| RPC | 方向 | 作用 |
|---|---|---|
| `SubmitCommandsToHost(turn, playerId, batch)` | 客户端→主机(`RpcId(1, ...)`) | 每回合命令批次(可空=心跳) |
| `BroadcastTurnBundle(turn, bundle)` | 主机→全体(CallLocal) | 回合包;主机自己也走此路执行 |
| `SubmitHashToHost(turn, hash)` | 客户端→主机 | 20 回合哈希上报 |
| `BroadcastOOS(turn)` | 主机→全体 | 通知各端各自 dump |
| `GameStart(seed, playerAssignments)` | 主机→全体 | 种子主机选定下发;玩家 ID 主机分配(修硬编码) |

批次序列化:count + 重复 NetCommand 体。Godot peer id(ENet 连接 id)与游戏玩家 id 两个命名空间,GameStart 建映射。

### 5.2 SimBridge 屏障接线

- 单机也持有 NetTurnManager(本地即主机,无网络);每 0.1s 推进前统一问 `CanAdvanceTurn()`。联机未就绪则 sim 停摆,渲染继续。
- 现有 72 行单机 `TurnManager` 类保留(DeterminismTests 在用),SimBridge 不再调用。
- 每推进一回合后向主机发 `currentTurn+2` 批次;主机 ingest 后检查聚合,完成即广播。
- 停摆时 `GD.Print` 一次(HUD 提示 YAGNI)。

### 5.3 Main.cs 输入层只发命令

- `HandleRightClick`:删 `_sim.CommandAttack/Gather/MoveEntity` 直调,一律 SubmitCommand;`IsMultiplayer` 分支全部消失。
- `PlaceBuilding`:只留展示性校验(负担/落点,用于拒绝反馈);发 Build 命令,扣费与地基生成 2 回合后由内核执行。
- `ResearchTech`、集结点:同样改为发命令。
- NetCommand 工厂硬编码 `player: 1` 改为 GameStart 分配的 `_localPlayerId`。

## 6. OOS dump(`Serialization/StateDump.cs`,新)

- `WriteBinaryDump(dir)`:BinarySerializer 全量快照 → `oos_turn{N}_player{P}.bin`。
- `WriteTextDump(dir)`:实体 id 升序 → 组件按类型名排序 → 字段 `key=value`(定点数输出内部值,十六进制)。全确定性排序,两端文本直接 `diff` 定位发散实体/组件/字段。
- 触发链:主机哈希比对失败 → `BroadcastOOS(N)` → 各端写双 dump 到 `user://oos/` → 控制台输出路径。专用 diff 工具不做(YAGNI)。

## 7. 测试(TDD,内核 xUnit,headless)

新增 `NetLockstepTests.cs` + `StateDumpTests.cs`,内存假传输接"主机+客户端"两个 NetTurnManager:

1. **锁步一致性**:两端混合命令流(Move/Train/Build/Research/RallyPoint)泵 200 回合——每命令每端恰好执行一次、顺序一致、每 20 回合哈希相等;
2. **屏障**:客户端未收 bundle 不推进,收到后推进;
3. **空批心跳**:沉默客户端空批次到达前主机不出 bundle,到达后推进;
4. **双重执行回归**:命令提交后到计划回合前 sim 零变化;
5. **Build/Research 经 executor**:费用恰好扣一次、地基在计划回合出现、研究恰好启动一次;
6. **文本 dump**:相同状态 → 两份文本逐字节相等;注入发散 → diff 命中预期实体;
7. 现有 188 测试保持绿,旧 `TurnManager` 不动。

Godot 侧无测试 harness,手动双实例对局验收。

## 8. 验收标准

- `dotnet test` 全绿(TreatWarningsAsErrors);
- 双实例对局:全类型命令两端同回合执行,无双重扣费/双重生成;高强度命令 10 分钟无 OOS;
- 人为注入 OOS → 双 dump 落盘,`diff` 可定位到组件。

## 9. 明确不做(本期)

重连、主机迁移、观战、大厅/XMPP、暂停 UI、回合时长自适应、>2 人局(设计不排斥 N 人,只测 2 人)、MP 带 AI(AI 当前非确定,联机对局先禁 AI,代码标注)。

## 10. 关键文件清单

| 文件 | 改动 |
|---|---|
| `src/ZeroAD.Sim/Net/SimCommandExecutor.cs` | 新增:命令执行唯一入口 |
| `src/ZeroAD.Sim/Net/NetTurnManager.cs` | 重构:主机聚合/客户端屏障/executor 委托 |
| `src/ZeroAD.Sim/Serialization/StateDump.cs` | 新增:二进制+文本双 dump |
| `src/ZeroAD.Sim.Tests/NetLockstepTests.cs` / `StateDumpTests.cs` | 新增测试 |
| `godot/Scripts/MultiplayerController.cs` | 重写:纯传输,5 RPC |
| `godot/Scripts/SimBridge.cs` | 屏障接线;SpawnFoundation/成本扣费/CommandResearch 移出到内核 |
| `godot/Scripts/Main.cs` | 输入层只发命令;IsMultiplayer 分支删除;playerId/seed 用分配值 |
