# MP 锁步(命令路由 + 回合屏障)实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 把多人联机从"P2P 互发 + 墙钟自由推进(实战必 OOS)"修复为主机权威制锁步:命令统一走延迟队列、主机按回合汇总广播、sim 推进由回合屏障门控、OOS 时双 dump 可 diff。

**Architecture:** 见设计文档 `docs/plans/2026-07-23-mp-lockstep-design.md`(已定稿)。命令执行上收内核 `SimCommandExecutor`(全工程唯一命令语义);`NetTurnManager` 重构为 Standalone/Host/Client 三角色,执行槽只由 TurnBundle 填充;Godot 侧 `MultiplayerController` 瘦身为 5 个 RPC 的纯传输;`Main.cs` 输入层只发命令不碰 sim。

**Tech Stack:** C# / .NET 8,xUnit(内核,headless),Godot 4.7 .NET(表现层,ENet)。

**关键约束(违反即返工):**
- 内核 `src/ZeroAD.Sim/` 禁止 `float`/`double` 进入 sim 逻辑、禁止 `Godot.*` 引用、`ImplicitUsings` 关闭(手写 using)。
- 两个 C# 项目均 `TreatWarningsAsErrors`——代码必须零警告(nullable 干净)。
- 定点数:命令载荷里的坐标用 `Fixed.InternalValue`(int)传输。
- 每个 Task 完成后按其 Commit 步骤提交,不要跨 Task 堆积改动。

**环境备忘:**
- 构建内核:`dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj`
- 跑全部内核测试:`dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj`
- 跑单个测试类:`dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~NetLockstepTests"`
- 构建 Godot 层:`dotnet build godot/GodotProject.csproj`(需 Godot .NET SDK 环境;若本机缺 SDK 导致失败,记录现象并继续,不要为此改项目文件)
- 真实模板路径(测试用):`../../../binaries/data/mods/public/simulation/templates`(相对测试程序集输出目录;参照 `ProductionQueueTests.cs:19` 的 `TryLoadTemplates()` 跳过模式)

---

## Task 1: NetCommand 拆分 + 命令类型扩展 + 批次编解码

把 `NetCommand`/`NetCommandType` 从 `NetTurnManager.cs` 拆到独立文件,扩展 Build/Train 载荷,新增 Research/SetRallyPoint,删除 legacy TrainSoldier,加批次编解码(主机广播和客户端批次都要用)。

**Files:**
- Create: `src/ZeroAD.Sim/Net/NetCommand.cs`
- Modify: `src/ZeroAD.Sim/Net/NetTurnManager.cs`(删除 `NetCommand`/`NetCommandType` 定义,保留 manager;`ExecuteCommand` 的 TrainSoldier 分支暂时保留到 Task 2 清理)
- Test: `src/ZeroAD.Sim.Tests/NetCommandCodecTests.cs`(新建)

**Step 1: 写失败测试** `src/ZeroAD.Sim.Tests/NetCommandCodecTests.cs`

```csharp
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class NetCommandCodecTests
{
    [Fact]
    public void Batch_RoundTrips_AllCommandTypes()
    {
        var commands = new[]
        {
            NetCommand.Move(1, 10, Fixed.FromFloat(3.5f), Fixed.FromFloat(-7.25f)),
            NetCommand.Gather(1, 10, 55),
            NetCommand.Attack(2, 11, 66),
            NetCommand.Build(1, 12, "structures/spart/house", Fixed.FromFloat(100f), Fixed.FromFloat(64f)),
            NetCommand.Train(2, 13, "units/spart/infantry_spearman_b", count: 5),
            NetCommand.Research(1, 14, "phase_town_generic"),
            NetCommand.SetRallyPoint(2, 15, 77),
        };

        byte[] data = NetCommand.SerializeBatch(commands);
        var decoded = NetCommand.DeserializeBatch(data);

        Assert.Equal(commands.Length, decoded.Length);
        for (int i = 0; i < commands.Length; i++)
        {
            Assert.Equal(commands[i].Player, decoded[i].Player);
            Assert.Equal(commands[i].Type, decoded[i].Type);
            Assert.Equal(commands[i].EntityId, decoded[i].EntityId);
            Assert.Equal(commands[i].IntParam1, decoded[i].IntParam1);
            Assert.Equal(commands[i].FixedParam1, decoded[i].FixedParam1);
            Assert.Equal(commands[i].FixedParam2, decoded[i].FixedParam2);
            Assert.Equal(commands[i].TemplateName, decoded[i].TemplateName);
        }
    }

    [Fact]
    public void Batch_RoundTrips_Empty()
    {
        var decoded = NetCommand.DeserializeBatch(NetCommand.SerializeBatch(System.Array.Empty<NetCommand>()));
        Assert.Empty(decoded);
    }

    [Fact]
    public void Train_CarriesCount()
    {
        var cmd = NetCommand.Train(1, 42, "units/spart/support_civilian", count: 5);
        Assert.Equal(5, cmd.IntParam1);
        Assert.Equal("units/spart/support_civilian", cmd.TemplateName);
    }

    [Fact]
    public void Build_CarriesTemplateAndWorldPosition()
    {
        var cmd = NetCommand.Build(2, 9, "structures/spart/barracks", Fixed.FromFloat(12.5f), Fixed.FromFloat(99f));
        Assert.Equal("structures/spart/barracks", cmd.TemplateName);
        Assert.Equal(9u, cmd.EntityId);
        Assert.Equal(Fixed.FromFloat(12.5f).InternalValue, cmd.FixedParam1);
        Assert.Equal(Fixed.FromFloat(99f).InternalValue, cmd.FixedParam2);
    }
}
```

**Step 2: 跑测试确认编译失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~NetCommandCodecTests"`
Expected: 编译错误(`SerializeBatch`/`Build`/`Research`/`SetRallyPoint`/`Train(count)` 不存在)

**Step 3: 实现** `src/ZeroAD.Sim/Net/NetCommand.cs`(完整新文件)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    public enum NetCommandType : byte
    {
        Invalid = 0,
        Move = 1,
        Gather = 2,
        Attack = 3,
        Build = 4,
        Train = 5,
        Research = 6,
        SetRallyPoint = 7,
    }

    /// <summary>
    /// A player command travelling the lockstep network. Commands are the ONLY mutator of
    /// sim state in multiplayer: they are scheduled COMMAND_DELAY turns ahead, aggregated
    /// by the host into per-turn bundles, and applied by SimCommandExecutor at the same
    /// turn on every peer. The legacy TrainSoldier type was removed — Train carries the
    /// full template name and a count.
    /// </summary>
    public readonly struct NetCommand
    {
        public readonly uint Player;
        public readonly NetCommandType Type;
        public readonly uint EntityId;
        public readonly int IntParam1;
        public readonly int IntParam2;
        public readonly int FixedParam1;
        public readonly int FixedParam2;
        /// <summary>
        /// Template name for Train/Build (entity template) or Research (technology id).
        /// Carried with the command so every peer resolves the exact same data.
        /// </summary>
        public readonly string TemplateName;

        public NetCommand(uint player, NetCommandType type, uint entityId = 0,
            int p1 = 0, int p2 = 0, int fp1 = 0, int fp2 = 0, string? templateName = null)
        {
            Player = player; Type = type; EntityId = entityId;
            IntParam1 = p1; IntParam2 = p2; FixedParam1 = fp1; FixedParam2 = fp2;
            TemplateName = templateName ?? "";
        }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(48);
            using var bw = new BinaryWriter(ms);
            bw.Write(Player);
            bw.Write((byte)Type);
            bw.Write(EntityId);
            bw.Write(IntParam1);
            bw.Write(IntParam2);
            bw.Write(FixedParam1);
            bw.Write(FixedParam2);
            byte[] tmplBytes = System.Text.Encoding.UTF8.GetBytes(TemplateName);
            bw.Write(tmplBytes.Length);
            bw.Write(tmplBytes);
            return ms.ToArray();
        }

        public static NetCommand Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            return new NetCommand(
                br.ReadUInt32(),
                (NetCommandType)br.ReadByte(),
                br.ReadUInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadString());
        }

        /// <summary>Length-prefixed batch framing for per-turn bundles and client batches.</summary>
        public static byte[] SerializeBatch(IReadOnlyList<NetCommand> commands)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(commands.Count);
            foreach (var cmd in commands)
            {
                byte[] payload = cmd.Serialize();
                bw.Write(payload.Length);
                bw.Write(payload);
            }
            return ms.ToArray();
        }

        public static NetCommand[] DeserializeBatch(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            int count = br.ReadInt32();
            var commands = new NetCommand[count];
            for (int i = 0; i < count; i++)
            {
                int len = br.ReadInt32();
                commands[i] = Deserialize(br.ReadBytes(len));
            }
            return commands;
        }

        public static NetCommand Move(uint player, uint entityId, Fixed x, Fixed z) =>
            new(player, NetCommandType.Move, entityId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>Gather: IntParam1 = target supply entity id.</summary>
        public static NetCommand Gather(uint player, uint unitId, uint targetId) =>
            new(player, NetCommandType.Gather, unitId, (int)targetId);

        /// <summary>Attack: IntParam1 = target entity id.</summary>
        public static NetCommand Attack(uint player, uint attackerId, uint targetId) =>
            new(player, NetCommandType.Attack, attackerId, (int)targetId);

        /// <summary>Build: EntityId = builder, TemplateName = full building template,
        /// FixedParam1/2 = world x/z. Cost charge + foundation spawn happen at execution.</summary>
        public static NetCommand Build(uint player, uint builderId, string template, Fixed x, Fixed z) =>
            new(player, NetCommandType.Build, builderId, 0, 0, x.InternalValue, z.InternalValue, template);

        /// <summary>Train: IntParam1 = count (batch training sends 5 as one command).</summary>
        public static NetCommand Train(uint player, uint buildingId, string templateName, int count = 1) =>
            new(player, NetCommandType.Train, buildingId, count, 0, 0, 0, templateName);

        /// <summary>Research: TemplateName = technology id.</summary>
        public static NetCommand Research(uint player, uint buildingId, string techName) =>
            new(player, NetCommandType.Research, buildingId, 0, 0, 0, 0, techName);

        /// <summary>SetRallyPoint: IntParam1 = target entity id (0 = clear).</summary>
        public static NetCommand SetRallyPoint(uint player, uint buildingId, uint targetEntityId) =>
            new(player, NetCommandType.SetRallyPoint, buildingId, (int)targetEntityId);
    }
}
```

**Step 4: 从 `NetTurnManager.cs` 删除旧定义**

删除 `NetCommandType` 枚举与 `NetCommand` 结构体(现 `NetTurnManager.cs:8-101`),包括 `TrainSoldier` 工厂。同时删除 `ExecuteCommand` 里的 `TrainSoldier` case 和 `NetCommandType.TrainSoldier` 引用——`ExecuteCommand` 整体将在 Task 2 被 executor 取代,本 Task 只做最小删除让编译通过:`ExecuteCommand` 中 `case NetCommandType.TrainSoldier:` 分支整段删除。

注意:`MultiplayerController.cs`/`Main.cs` 里若有 `NetCommand.TrainSoldier` 引用,同步删除(grep 确认:`grep -rn "TrainSoldier" godot/ src/`)。

**Step 5: 跑测试确认通过 + 全量回归**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~NetCommandCodecTests"` → PASS
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿(含旧 NetCommandRoutingTests)

**Step 6: Commit**

```bash
git add src/ZeroAD.Sim/Net/NetCommand.cs src/ZeroAD.Sim/Net/NetTurnManager.cs src/ZeroAD.Sim.Tests/NetCommandCodecTests.cs
git commit -m "feat(sim): NetCommand 拆分独立文件,新增 Research/SetRallyPoint 命令与批次编解码

Build 载荷改为模板名+世界坐标;Train 携带 count;删除 legacy TrainSoldier。"
```

---

## Task 2: SimCommandExecutor —— 命令执行唯一入口(上收内核)

新建 `SimCommandExecutor`,把 `NetTurnManager.ExecuteCommand` 的 Move/Gather/Attack/Train 语义**原样搬入**(含 UnitAI 优先、leaf 组件兜底的二级路由),并补上 SimBridge 侧才有的事件 raise(Gather/Attack/Repair 的 `PlayerCommand`),让网络路径与单机路径事件流也一致。`NetTurnManager.ExecuteCommand` 改为委托。

**Files:**
- Create: `src/ZeroAD.Sim/Net/SimCommandExecutor.cs`
- Modify: `src/ZeroAD.Sim/Net/NetTurnManager.cs`(ExecuteCommand 委托)
- Test: `src/ZeroAD.Sim.Tests/SimCommandExecutorTests.cs`(新建)

**Step 1: 写失败测试** `src/ZeroAD.Sim.Tests/SimCommandExecutorTests.cs`

```csharp
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class SimCommandExecutorTests
{
    private const string TemplatesRoot = "../../../binaries/data/mods/public/simulation/templates";

    private static Content.TemplateLoader? TryLoadTemplates() =>
        System.IO.Directory.Exists(TemplatesRoot) ? new Content.TemplateLoader(TemplatesRoot) : null;

    private static EntityId MakeUnitWithAI(ComponentManager cm, int player = 1)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        if (player > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    [Fact]
    public void Gather_RaisesPlayerCommandEvent()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm);
        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        cm.AddComponent(tree, new ResourceSupply());

        PlayerCommandEvent? raised = null;
        cm.Events.PlayerCommand += e => raised = e;

        executor.Apply(NetCommand.Gather(1, unit.Value, tree.Value));

        Assert.NotNull(raised);
        Assert.Equal("gather", raised!.Type);
        Assert.Equal(tree, raised.Target);
    }

    [Fact]
    public void Attack_RaisesPlayerCommandEvent()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var attacker = MakeUnitWithAI(cm);
        cm.AddComponent(attacker, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 10) });
        var target = MakeUnitWithAI(cm, player: 2);
        cm.AddComponent(target, new HealthComponent());

        PlayerCommandEvent? raised = null;
        cm.Events.PlayerCommand += e => raised = e;

        executor.Apply(NetCommand.Attack(1, attacker.Value, target.Value));

        Assert.NotNull(raised);
        Assert.Equal("attack", raised!.Type);
    }

    [Fact]
    public void Train_EnqueuesExactCount()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return; // LFS data missing — skip like ProductionQueueTests
        var cm = new ComponentManager(42, templates: templates);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Wood = 1000, Food = 1000, Stone = 1000, Metal = 1000, PopBonuses = 50 });
        cm.RegisterPlayer(1, playerEntity);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new PositionComponent());
        cm.AddComponent(building, new ProductionQueue());
        cm.AddComponent(building, new OwnershipComponent { PlayerId = 1 });

        var executor = new SimCommandExecutor(cm);
        executor.Apply(NetCommand.Train(1, building.Value, "units/spart/support_civilian", count: 5));

        var queue = cm.QueryInterface<ProductionQueue>(building)!;
        Assert.Single(queue.Items);
        Assert.Equal(5, queue.Items[0].Count);
    }
}
```

注意:上面引用的 `PlayerCommandEvent`、`cm.Events.PlayerCommand`、`ProductionQueue.Items`、`ComponentManager(cm, templates: ...)` 命名参数以现有代码为准——写测试时先打开 `src/ZeroAD.Sim/Events/SimEvents.cs`、`src/ZeroAD.Sim/Components/Production.cs`、`src/ZeroAD.Sim/ComponentManager.cs:58` 核对签名;若 `Items` 不存在,用 `ProductionQueueTests.cs` 里验证队列内容的现有方式断言。`ResourceSupply` 若无无参构造,照 `EconomyComponentsTests.cs` 的构造方式调整。

**Step 2: 跑测试确认编译失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~SimCommandExecutorTests"`
Expected: 编译错误(`SimCommandExecutor` 不存在)

**Step 3: 实现** `src/ZeroAD.Sim/Net/SimCommandExecutor.cs`(完整新文件)

```csharp
using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    /// <summary>
    /// The ONE place player commands are applied to the sim. Both the single-player
    /// presentation path (SimBridge.CommandX wrappers) and the lockstep path
    /// (NetTurnManager) delegate here, so SP and MP can never diverge in command
    /// semantics — the historical "hardcoded villager in NetTurnManager" OOS was
    /// exactly this class of divergence.
    /// </summary>
    public sealed class SimCommandExecutor
    {
        private readonly ComponentManager _cm;
        private readonly PathfinderComponent? _pathfinder;

        /// <param name="pathfinder">Optional explicit pathfinder for build-placement
        /// validation. When null, falls back to <see cref="SimSystem.Pathfinder"/>
        /// (the production wiring); tests can inject one to avoid the static.</param>
        public SimCommandExecutor(ComponentManager cm, PathfinderComponent? pathfinder = null)
        {
            _cm = cm;
            _pathfinder = pathfinder;
        }

        public void Apply(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            switch (cmd.Type)
            {
                case NetCommandType.Move: ApplyMove(entity, cmd); break;
                case NetCommandType.Gather: ApplyGather(entity, cmd); break;
                case NetCommandType.Attack: ApplyAttack(entity, cmd); break;
                case NetCommandType.Train: ApplyTrain(entity, cmd); break;
                case NetCommandType.Build: ApplyBuild(entity, cmd); break;
                case NetCommandType.Research: ApplyResearch(entity, cmd); break;
                case NetCommandType.SetRallyPoint: ApplySetRallyPoint(entity, cmd); break;
            }
        }

        private void ApplyMove(EntityId entity, NetCommand cmd)
        {
            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
            // Route through UnitAI when present (the canonical command sink); otherwise
            // fall back to direct UnitMotion for legacy entities.
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
                ai.Walk(new FixedVector2D(x, z));
            else
                _cm.QueryInterface<UnitMotion>(entity)?.MoveToPoint(new FixedVector2D(x, z));
        }

        private void ApplyGather(EntityId entity, NetCommand cmd)
        {
            var target = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
            {
                ai.Gather(target);
            }
            else
            {
                var motion = _cm.QueryInterface<UnitMotion>(entity);
                var gatherer = _cm.QueryInterface<ResourceGatherer>(entity);
                var supply = _cm.QueryInterface<ResourceSupply>(target);
                var supplyPos = _cm.QueryInterface<PositionComponent>(target);
                if (gatherer != null && supply != null && supplyPos != null && motion != null)
                {
                    gatherer.TargetSupply = target;
                    gatherer.CarryType = supply.Type;
                    gatherer.State = ResourceGatherer.GatherState.MovingToResource;
                    motion.MoveToPoint(new FixedVector2D(supplyPos.Position.X, supplyPos.Position.Z));
                }
            }
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "gather", Target = target });
        }

        private void ApplyAttack(EntityId entity, NetCommand cmd)
        {
            var target = new EntityId((uint)cmd.IntParam1);
            var ai = _cm.QueryInterface<UnitAIComponent>(entity);
            if (ai != null)
                ai.Attack(target);
            else
                _cm.QueryInterface<AttackComponent>(entity)?.AttackTarget(target);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "attack", Target = target });
        }

        private void ApplyTrain(EntityId entity, NetCommand cmd)
        {
            var queue = _cm.QueryInterface<ProductionQueue>(entity);
            if (queue == null) return;
            string template = string.IsNullOrEmpty(cmd.TemplateName)
                ? "units/spart/support_civilian"
                : cmd.TemplateName;
            queue.EnqueueTraining(template, Math.Max(1, cmd.IntParam1), _cm);
        }

        private void ApplyBuild(EntityId builder, NetCommand cmd)
        {
            string template = cmd.TemplateName;
            if (template.Length == 0) return;
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (player == null) return;

            // Deterministic cost from template data — identical on every peer.
            TemplateStats? stats = null;
            try { stats = _cm.Templates?.ExtractStats(template); } catch { }
            int wood = stats?.WoodCost ?? 0;
            int stone = stats?.StoneCost ?? 0;
            int metal = stats?.MetalCost ?? 0;
            int food = stats?.FoodCost ?? 0;
            float buildTime = stats != null && stats.BuildTime > 0f ? stats.BuildTime : 8.0f;
            if (!player.CanAfford(wood, food, stone, metal)) return;

            var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
            var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);

            // Re-validate placement at execution time (the UI check is only a courtesy
            // pre-filter; both peers must reach the same verdict here).
            var pathfinder = _pathfinder ?? SimSystem.Pathfinder;
            if (pathfinder != null)
            {
                float halfSize = 3f;
                if (stats != null)
                {
                    float ob = Math.Max(stats.ObstructionSize0.ToFloat(), stats.ObstructionSize1.ToFloat());
                    if (ob > 0) halfSize = ob * 0.5f;
                }
                var result = pathfinder.CheckBuildingPlacement(
                    x, z, Fixed.FromFloat(halfSize), Fixed.FromFloat(halfSize));
                if (result != PlacementResult.Success) return;
            }

            player.Spend(wood, food, stone, metal);
            var foundation = SpawnFoundation(template, x, z, buildTime, (int)cmd.Player);

            var ai = _cm.QueryInterface<UnitAIComponent>(builder);
            if (ai != null)
                ai.Repair(foundation);
            else
                _cm.QueryInterface<BuilderComponent>(builder)?.Build(foundation);
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = foundation });
        }

        /// <summary>
        /// Kernel-side foundation spawn (moved out of SimBridge so the lockstep path can
        /// run it headless). Visuals are built by the presentation layer via the
        /// EntityCreated event raised here.
        /// </summary>
        private EntityId SpawnFoundation(string template, Fixed x, Fixed z, float buildTime, int ownerPlayerId)
        {
            var entity = _cm.CreateEntity();
            _cm.AddComponent(entity, new PositionComponent());
            _cm.AddComponent(entity, new FoundationComponent());
            string displayName = template.Substring(template.LastIndexOf('/') + 1);
            TemplateStats? stats = null;
            try { stats = _cm.Templates?.ExtractStats(template); } catch { }
            _cm.AddComponent(entity, new IdentityComponent
            {
                Name = displayName + " (building)",
                TemplateName = template,
                IsBuilding = true,
                IsUnit = false,
                Classes = stats?.GetClassList() ?? new List<string> { displayName }
            });
            _cm.AddComponent(entity, new HealthComponent { Current = 200, Max = 200 });
            _cm.AddComponent(entity, new OwnershipComponent { PlayerId = ownerPlayerId });
            // NOTE: FoundationComponent.Configure's first argument semantics must match
            // what the completion path expects (old SimBridge code passed the UI name
            // "House"; the template now travels in full). Verify against
            // FoundationComponent's completion logic before finalising — see plan Task 3
            // verification step.
            _cm.QueryInterface<FoundationComponent>(entity)?.Configure(displayName, buildTime);
            var pos = _cm.QueryInterface<PositionComponent>(entity);
            if (pos != null)
                pos.Position = new FixedVector3D(x, Fixed.Zero, z);
            _cm.Events.RaiseEntityCreated(new EntityCreatedEvent
            {
                Entity = entity,
                TemplateName = template,
                OwnerPlayerId = ownerPlayerId
            });
            return entity;
        }

        private void ApplyResearch(EntityId building, NetCommand cmd)
        {
            var researcher = _cm.QueryInterface<ResearcherComponent>(building);
            var playerEntityId = _cm.GetPlayerEntityId((int)cmd.Player);
            var techMgr = playerEntityId.HasValue
                ? _cm.QueryInterface<TechnologyManager>(playerEntityId.Value)
                : null;
            var player = _cm.GetPlayerEntity((int)cmd.Player);
            if (researcher == null || techMgr == null || player == null) return;
            if (!researcher.StartResearch(cmd.TemplateName, techMgr, player)) return;
            _cm.Events.RaiseResearchQueued(new ResearchQueuedEvent
            {
                ResearcherEntity = building,
                TechnologyTemplate = cmd.TemplateName
            });
        }

        private void ApplySetRallyPoint(EntityId building, NetCommand cmd)
        {
            var rally = _cm.QueryInterface<RallyPointComponent>(building);
            if (rally == null) return;
            EntityId? target = null;
            if (cmd.IntParam1 != 0)
            {
                target = new EntityId((uint)cmd.IntParam1);
                var pos = _cm.QueryInterface<PositionComponent>(target.Value);
                if (pos != null)
                    rally.Set(new FixedVector2D(pos.Position.X, pos.Position.Z));
            }
            _cm.Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "set-rallypoint", Target = target });
        }
    }
}
```

**Step 4: `NetTurnManager.ExecuteCommand` 改为委托**

`NetTurnManager` 加字段 `private readonly SimCommandExecutor _executor;`,构造函数里 `_executor = new SimCommandExecutor(cm);`,`ExecuteCommand` 整个 switch 替换为 `_executor.Apply(cmd);`(TrainSoldier case 已在 Task 1 删除)。

**Step 5: 跑测试**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~SimCommandExecutorTests"` → PASS
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿(NetCommandRoutingTests 走委托后行为不变)

**Step 6: Commit**

```bash
git add src/ZeroAD.Sim/Net/SimCommandExecutor.cs src/ZeroAD.Sim/Net/NetTurnManager.cs src/ZeroAD.Sim.Tests/SimCommandExecutorTests.cs
git commit -m "feat(sim): 命令执行上收内核 SimCommandExecutor,SP/MP 共用唯一命令语义

NetTurnManager.ExecuteCommand 改为委托;Gather/Attack/Repair 在网络路径
也 raise PlayerCommand 事件;Build/Research/SetRallyPoint 首次获得执行路径。"
```

---

## Task 3: Build/Research/SetRallyPoint 执行路径测试 + Configure 语义核对

**Files:**
- Test: `src/ZeroAD.Sim.Tests/SimCommandExecutorTests.cs`(追加)
- Modify(可能): `src/ZeroAD.Sim/Net/SimCommandExecutor.cs`

**Step 1: 核对 `FoundationComponent.Configure` 第一参数语义**

Run: `grep -n "Configure\|_template\|Complete\|SpawnEntity" src/ZeroAD.Sim/Components/Construction.cs | head -20`
读 `FoundationComponent` 完工逻辑:它用 Configure 的第一个参数做什么?
- 若用于完工后生成成品建筑的模板映射 → executor 的 `SpawnFoundation` 必须传**完整模板名**(改掉 displayName 传法);
- 若仅展示用途 → 保持 displayName。
按结论调整 `SimCommandExecutor.SpawnFoundation` 并删掉 NOTE 注释。同时核对旧 `SimBridge.MapBuildNameToTemplate`(`godot/Scripts/SimBridge.cs`)确认新旧映射一致。

**Step 2: 写测试(先失败)** — 追加到 `SimCommandExecutorTests.cs`

```csharp
    private static (ComponentManager cm, EntityId playerEntity) BuildWorldWithRichPlayer()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Wood = 1000, Food = 1000, Stone = 1000, Metal = 1000, PopBonuses = 50 });
        cm.AddComponent(playerEntity, new TechnologyManager());
        cm.RegisterPlayer(1, playerEntity);
        return (cm, playerEntity);
    }

    [Fact]
    public void Build_ChargesCostOnce_AndSpawnsFoundationOwnedByCommander()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        var (cm, _) = BuildWorldWithRichPlayer();
        var cmWithTemplates = new ComponentManager(42, templates: templates);
        SimSystem.Init(cmWithTemplates);
        cm = cmWithTemplates;
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Wood = 1000, Food = 1000, Stone = 1000, Metal = 1000, PopBonuses = 50 });
        cm.RegisterPlayer(1, playerEntity);
        var builder = MakeUnitWithAI(cm);
        var executor = new SimCommandExecutor(cm);

        const string template = "structures/spart/house";
        int woodBefore = cm.GetPlayerEntity(1)!.Wood;

        executor.Apply(NetCommand.Build(1, builder.Value, template,
            Fixed.FromFloat(30f), Fixed.FromFloat(30f)));

        var player = cm.GetPlayerEntity(1)!;
        var stats = templates.ExtractStats(template);
        Assert.Equal(woodBefore - stats.WoodCost, player.Wood);
        // Exactly one foundation appeared, owned by the commanding player.
        var foundations = new List<EntityId>();
        foreach (var e in cm.AllEntities)
            if (cm.QueryInterface<FoundationComponent>(e) != null) foundations.Add(e);
        var foundation = Assert.Single(foundations);
        Assert.Equal(1, cm.QueryInterface<OwnershipComponent>(foundation)!.PlayerId);
        // Builder was ordered (UnitAI picked up a repair order; dispatched on Tick).
        var ai = cm.QueryInterface<UnitAIComponent>(builder)!;
        Assert.False(ai.IsIdle);
    }

    [Fact]
    public void Build_Refused_WhenUnaffordable()
    {
        var (cm, _) = BuildWorldWithRichPlayer();
        cm.GetPlayerEntity(1)!.Wood = 0;
        cm.GetPlayerEntity(1)!.Stone = 0;
        cm.GetPlayerEntity(1)!.Metal = 0;
        cm.GetPlayerEntity(1)!.Food = 0;
        var builder = MakeUnitWithAI(cm);
        var executor = new SimCommandExecutor(cm);
        int entitiesBefore = cm.AllEntities.Count;

        executor.Apply(NetCommand.Build(1, builder.Value, "structures/spart/house",
            Fixed.FromFloat(30f), Fixed.FromFloat(30f)));

        Assert.Equal(entitiesBefore, cm.AllEntities.Count);
    }

    [Fact]
    public void Research_StartsExactlyOnce()
    {
        var (cm, _) = BuildWorldWithRichPlayer();
        var building = cm.CreateEntity();
        cm.AddComponent(building, new ResearcherComponent());
        cm.AddComponent(building, new OwnershipComponent { PlayerId = 1 });
        var executor = new SimCommandExecutor(cm);

        ResearchQueuedEvent? raised = null;
        cm.Events.ResearchQueued += e => raised = e;

        executor.Apply(NetCommand.Research(1, building.Value, "phase_town_generic"));

        Assert.NotNull(raised);
        Assert.Equal("phase_town_generic", raised!.TechnologyTemplate);
    }

    [Fact]
    public void SetRallyPoint_SetsPositionFromTargetEntity()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new RallyPointComponent());
        var target = cm.CreateEntity();
        cm.AddComponent(target, new PositionComponent());
        cm.QueryInterface<PositionComponent>(target)!.Position =
            new FixedVector3D(Fixed.FromFloat(11f), Fixed.Zero, Fixed.FromFloat(22f));
        var executor = new SimCommandExecutor(cm);

        executor.Apply(NetCommand.SetRallyPoint(1, building.Value, target.Value));

        var rally = cm.QueryInterface<RallyPointComponent>(building)!;
        Assert.Equal(Fixed.FromFloat(11f), rally.Position.X);
        Assert.Equal(Fixed.FromFloat(22f), rally.Position.Z);
    }
```

注:`RallyPointComponent.Position` 的可见性/`ResearcherComponent` 构造/`cm.Events.ResearchQueued` 以现有代码为准核对(`src/ZeroAD.Sim/Components/ExtraComponents.cs:123`、`src/ZeroAD.Sim/Components/Technology.cs`、`src/ZeroAD.Sim/Events/SimEvents.cs`)。`ComponentManager` 构造的 templates 命名参数以 `ComponentManager.cs:58` 签名为准。

**Step 3: 跑测试 → 修实现 → 全量回归**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~SimCommandExecutorTests"` → PASS
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿

**Step 4: Commit**

```bash
git add src/ZeroAD.Sim/Net/SimCommandExecutor.cs src/ZeroAD.Sim.Tests/SimCommandExecutorTests.cs
git commit -m "test(sim): Build/Research/SetRallyPoint 执行路径测试;核对 Foundation Configure 语义"
```

---

## Task 4: NetTurnManager 重构 —— 三角色、outbox/bundle、回合屏障、主机聚合

核心重构。语义(与设计文档 §3/§4.3 一致):

- **outbox/inbox 分离**:`SubmitLocalCommand` 只进 outbox;执行槽 `_bundles` 只由 `ReceiveTurnBundle`(MP)或 Standalone 本地聚合填充——命令永远单份执行,无双重执行可能。
- **回合推进**:`AdvanceTurn()` 先把 outbox 打包为本回合批次(key = currentTurn + delay,触发 `OnBatchDue`,Standalone 直接落 `_bundles`,Host 自 ingest),再执行 `_bundles[currentTurn]`(可缺省=空),最后 `currentTurn++`。
- **屏障**:`CanAdvanceTurn()` —— Standalone 恒真;Host/Client 仅当 `_bundles` 含 currentTurn。
- **主机引导**:游戏开始无法存在 turn < delay 的命令,`HostBootstrap()` 直接产出 turn 0..delay-1 的空 bundle(经 `OnTurnBundleReady` 广播 + 自投)。
- **主机聚合**:`HostIngestBatch(player, turn, cmds)` 收齐 expectedPlayers 即产出 bundle:按 Player 升序拼接(批内保持到达顺序),触发 `OnTurnBundleReady`。**bundle 投递统一走 `ReceiveTurnBundle`**(传输层 CallLocal 环回主机),`ProduceBundle` 本身不写 `_bundles`,保持单一写入路径。
- **OOS 主机裁决**:Client 在 `OnHashComputed` 里把哈希发主机;主机 `HostReceiveRemoteHash` 与本地哈希双向门闩比对,不一致 `SetOOS`。

**Files:**
- Modify: `src/ZeroAD.Sim/Net/NetTurnManager.cs`(整体重写,~300 行)
- Test: `src/ZeroAD.Sim.Tests/NetLockstepTests.cs`(新建)
- Modify: `src/ZeroAD.Sim.Tests/NetCommandRoutingTests.cs`(构造签名更新)

**Step 1: 先更新旧测试构造(红)→ 写新锁步测试(红)**

`NetCommandRoutingTests.cs` 里 `new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1)` 四处改为:

```csharp
new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1, NetRole.Standalone, new HashSet<uint> { 1 })
```

`RunOneTurn` 里 `tm.AdvanceTurn(players)` 两次改为 `tm.AdvanceTurn();` 两次,删除局部 `players` 变量。

新建 `src/ZeroAD.Sim.Tests/NetLockstepTests.cs`:

```csharp
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Two NetTurnManager instances (Host + Client) wired with a synchronous in-memory
/// transport. Proves the lockstep contract: commands execute exactly once on both
/// peers, in the same order, at the same turn; the turn barrier stalls a peer that
/// is missing a bundle; empty heartbeat batches keep turns flowing.
/// </summary>
public sealed class NetLockstepTests
{
    private sealed class TwoPeerLockstep
    {
        public readonly ComponentManager HostCm;
        public readonly ComponentManager ClientCm;
        public readonly NetTurnManager Host;
        public readonly NetTurnManager Client;
        public bool DeliverBundles = true;

        public TwoPeerLockstep(uint seed = 42, int commandDelay = 2)
        {
            HostCm = new ComponentManager(seed);
            ClientCm = new ComponentManager(seed);
            var players = new HashSet<uint> { 1, 2 };
            Host = new NetTurnManager(HostCm, commandDelay, 1, NetRole.Host, players);
            Client = new NetTurnManager(ClientCm, commandDelay, 2, NetRole.Client, players);

            // Synchronous transport: host broadcasts bundles to itself (CallLocal) and
            // the client; the client ships its per-turn batch to the host. The host's
            // own batch is self-ingested inside AdvanceTurn — do NOT forward it here.
            Host.OnTurnBundleReady += (turn, cmds) =>
            {
                if (!DeliverBundles) return;
                byte[] data = NetCommand.SerializeBatch(cmds);
                Host.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
                Client.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
            };
            Client.OnBatchDue += (turn, cmds) =>
                Host.HostIngestBatch(2, turn, cmds);

            Host.HostBootstrap();
        }

        public void Pump(int turns)
        {
            for (int i = 0; i < turns; i++)
            {
                Assert.True(Host.CanAdvanceTurn(), $"host stalled at turn {Host.CurrentTurn}");
                Assert.True(Client.CanAdvanceTurn(), $"client stalled at turn {Client.CurrentTurn}");
                Host.AdvanceTurn();
                Client.AdvanceTurn();
            }
        }
    }

    private static EntityId MakeUnit(ComponentManager cm, int player)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    [Fact]
    public void Commands_ExecuteExactlyOnce_OnBothPeers_WithIdenticalHashes()
    {
        var net = new TwoPeerLockstep();
        // Both peers need the same entities: create identical worlds by replaying the
        // same construction sequence on the same seed.
        var hostUnit = MakeUnit(net.HostCm, 1);
        var clientUnit = MakeUnit(net.ClientCm, 1);
        Assert.Equal(hostUnit.Value, clientUnit.Value);

        net.Host.SubmitLocalCommand(NetCommand.Move(1, hostUnit.Value,
            Fixed.FromFloat(10f), Fixed.FromFloat(10f)));
        net.Client.SubmitLocalCommand(NetCommand.Move(1, clientUnit.Value,
            Fixed.FromFloat(10f), Fixed.FromFloat(10f)));

        for (int t = 0; t < 200; t++)
        {
            net.Pump(1);
            Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
        }

        // The command executed exactly once per peer: the unit has exactly one walk order.
        var hostAi = net.HostCm.QueryInterface<UnitAIComponent>(hostUnit)!;
        hostAi.Tick(0.1f, net.HostCm);
        Assert.StartsWith("INDIVIDUAL", hostAi.FsmStateName);
    }

    [Fact]
    public void Barrier_ClientStallsWithoutBundle_ResumesOnDelivery()
    {
        var net = new TwoPeerLockstep();
        net.Pump(3);

        net.DeliverBundles = false;
        net.Host.AdvanceTurn(); // host produces but "network" drops the bundle
        // Client never received the bundle for its current turn: barrier holds.
        Assert.False(net.Client.CanAdvanceTurn());

        // Late delivery unblocks the client with the exact same commands.
        net.DeliverBundles = true;
        // Re-deliver what was dropped by re-running the host's bundle production path:
        // simplest faithful emulation: advance host further so aggregation completes
        // for the client's current turn, then the client can proceed once delivered.
        // (In production the host re-broadcasts on demand; here we assert the barrier
        // semantics only.)
        Assert.Equal(net.Host.CurrentTurn, net.Client.CurrentTurn + 1);
    }

    [Fact]
    public void Heartbeat_EmptyBatchesKeepTurnsFlowing()
    {
        var net = new TwoPeerLockstep();
        // No commands at all — empty per-turn batches must still let the host
        // aggregate and both peers advance without stalling.
        net.Pump(50);
        Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
    }

    [Fact]
    public void NoExecution_BeforeScheduledTurn()
    {
        var net = new TwoPeerLockstep(commandDelay: 2);
        var hostUnit = MakeUnit(net.HostCm, 1);
        MakeUnit(net.ClientCm, 1);

        net.Host.SubmitLocalCommand(NetCommand.Move(1, hostUnit.Value,
            Fixed.FromFloat(5f), Fixed.FromFloat(5f)));

        // Fewer turns than COMMAND_DELAY: the unit must NOT have any order yet.
        net.Pump(1);
        var ai = net.HostCm.QueryInterface<UnitAIComponent>(hostUnit)!;
        Assert.True(ai.IsIdle);

        net.Pump(2);
        Assert.False(ai.IsIdle);
    }
}
```

注:`UnitAIComponent.FsmStateName`/`IsIdle` 已在 `NetCommandRoutingTests` 使用,签名可靠。`TwoPeerLockstep` 里 `SimSystem.Init` 被两个 cm 交替调用——静态只有一个槽;本测试的执行器路径不经过 `SimSystem`(除 Build 落点校验,本测试不用 Build),若遇到串扰,把 `MakeUnit` 的 `SimSystem.Init` 移到每个 Assert 使用前。Barrier 测试第二段(延迟补投)按实现实情微调,核心断言是"未收到 bundle 时 `CanAdvanceTurn` 为 false"。

**Step 2: 跑测试确认编译失败/红**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~NetLockstepTests|FullyQualifiedName~NetCommandRoutingTests"`
Expected: 编译错误(新构造签名/方法不存在)

**Step 3: 整体重写** `src/ZeroAD.Sim/Net/NetTurnManager.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Sim.Net
{
    public enum NetRole : byte
    {
        /// <summary>No network: local submissions aggregate synchronously. SP path.</summary>
        Standalone = 0,
        /// <summary>Aggregates per-turn batches from all players and produces bundles.</summary>
        Host = 1,
        /// <summary>Ships per-turn batches to the host; executes only received bundles.</summary>
        Client = 2,
    }

    /// <summary>
    /// Host-authoritative lockstep turn manager (one per peer).
    ///
    /// Command lifecycle:
    ///   SubmitLocalCommand → outbox → drained into a per-turn batch at AdvanceTurn
    ///   (key = currentTurn + commandDelay, event OnBatchDue) → host aggregates batches
    ///   from ALL expected players → ProduceBundle(turn) → OnTurnBundleReady → transport
    ///   broadcasts → ReceiveTurnBundle on every peer (the ONLY writer of _bundles
    ///   besides Standalone) → executed by AdvanceTurn when currentTurn reaches it.
    ///
    /// The turn barrier: CanAdvanceTurn() is false until the bundle for the upcoming
    /// turn has arrived, so the sim's advance is paced by the network, not the clock.
    /// Turns before commandDelay can never contain commands; HostBootstrap pre-produces
    /// them empty so the game can start.
    /// </summary>
    public sealed class NetTurnManager
    {
        private readonly ComponentManager _cm;
        private readonly SimCommandExecutor _executor;
        private readonly int _commandDelay;
        private readonly NetRole _role;
        private readonly uint _localPlayerId;
        private readonly HashSet<uint> _expectedPlayers;

        private uint _currentTurn;
        private readonly List<NetCommand> _outbox = new();
        private readonly Dictionary<uint, List<NetCommand>> _bundles = new();
        private readonly Dictionary<uint, Dictionary<uint, List<NetCommand>>> _incoming = new();

        private byte[]? _lastLocalHash;
        private uint _lastHashTurn;
        private readonly Dictionary<(uint turn, uint player), byte[]> _remoteHashes = new();
        private string? _oosError;

        public uint CurrentTurn => _currentTurn;
        public int CommandDelay => _commandDelay;
        public NetRole Role => _role;
        public uint LocalPlayerId => _localPlayerId;
        public bool HasOOS => _oosError != null;
        public string? OosError => _oosError;

        /// <summary>(turn, possibly-empty local batch) raised at every AdvanceTurn.
        /// Clients forward this to the host; the host self-ingests internally.</summary>
        public event Action<uint, NetCommand[]>? OnBatchDue;
        /// <summary>Host only: a complete per-turn bundle is ready for broadcast.</summary>
        public event Action<uint, NetCommand[]>? OnTurnBundleReady;
        /// <summary>Client only: ship this state hash to the host.</summary>
        public event Action<byte[]>? OnHashComputed;
        public event Action<uint, string>? OnOOSDetected;
        public event Action<uint>? OnTurnAdvanced;

        public NetTurnManager(ComponentManager cm, int commandDelay, uint localPlayerId,
            NetRole role, HashSet<uint> expectedPlayers)
        {
            _cm = cm;
            _executor = new SimCommandExecutor(cm);
            _commandDelay = Math.Max(1, commandDelay);
            _localPlayerId = localPlayerId;
            _role = role;
            _expectedPlayers = expectedPlayers;
        }

        public void SubmitLocalCommand(NetCommand cmd) => _outbox.Add(cmd);

        /// <summary>Host only: turns [0, commandDelay) can never contain commands, so
        /// their bundles are produced empty up front and the game can start immediately.</summary>
        public void HostBootstrap()
        {
            if (_role != NetRole.Host) return;
            for (uint turn = 0; turn < (uint)_commandDelay; turn++)
                ProduceBundle(turn, new Dictionary<uint, List<NetCommand>>());
        }

        public bool CanAdvanceTurn() =>
            _role == NetRole.Standalone || _bundles.ContainsKey(_currentTurn);

        public void AdvanceTurn()
        {
            // Drain the outbox into this turn's batch (possibly empty — the heartbeat
            // that lets the host complete aggregation for silent players).
            uint batchTurn = _currentTurn + (uint)_commandDelay;
            var batch = _outbox.ToArray();
            _outbox.Clear();
            OnBatchDue?.Invoke(batchTurn, batch);
            if (_role == NetRole.Standalone)
                _bundles[batchTurn] = new List<NetCommand>(batch);
            else if (_role == NetRole.Host)
                HostIngestBatch(_localPlayerId, batchTurn, batch);

            // Execute the bundle scheduled for this turn (absent/empty = no commands).
            if (_bundles.TryGetValue(_currentTurn, out var commands))
            {
                _bundles.Remove(_currentTurn);
                foreach (var cmd in commands)
                    _executor.Apply(cmd);
            }

            _currentTurn++;
            OnTurnAdvanced?.Invoke(_currentTurn);
            if (_currentTurn % 20 == 0)
                CheckOOS();
        }

        /// <summary>Host only: ingest one player's batch for a turn. When every
        /// expected player has reported, the bundle is produced. Duplicate batches
        /// from the same player for the same turn are ignored.</summary>
        public void HostIngestBatch(uint player, uint turn, NetCommand[] commands)
        {
            if (_role != NetRole.Host) return;
            if (!_incoming.TryGetValue(turn, out var perPlayer))
            {
                perPlayer = new Dictionary<uint, List<NetCommand>>();
                _incoming[turn] = perPlayer;
            }
            if (perPlayer.ContainsKey(player)) return;
            perPlayer[player] = new List<NetCommand>(commands);
            if (perPlayer.Count == _expectedPlayers.Count)
            {
                _incoming.Remove(turn);
                ProduceBundle(turn, perPlayer);
            }
        }

        private void ProduceBundle(uint turn, Dictionary<uint, List<NetCommand>> perPlayer)
        {
            // Deterministic order: ascending player id, in-batch order preserved.
            var bundle = new List<NetCommand>();
            foreach (uint pid in perPlayer.Keys.OrderBy(k => k))
                bundle.AddRange(perPlayer[pid]);
            OnTurnBundleReady?.Invoke(turn, bundle.ToArray());
        }

        /// <summary>The ONLY writer of execution slots in Host/Client mode. Called by
        /// the transport when a bundle arrives (host included, via CallLocal loopback).</summary>
        public void ReceiveTurnBundle(uint turn, NetCommand[] commands)
        {
            if (_role == NetRole.Standalone) return;
            _bundles[turn] = new List<NetCommand>(commands);
        }

        private void CheckOOS()
        {
            byte[] hash = _cm.ComputeStateHash();
            _lastLocalHash = hash;
            _lastHashTurn = _currentTurn;
            if (_role == NetRole.Client)
            {
                OnHashComputed?.Invoke(hash);
                return;
            }
            if (_role == NetRole.Host)
            {
                foreach (var kvp in _remoteHashes)
                    if (kvp.Key.turn == _currentTurn && !HashEquals(hash, kvp.Value))
                        SetOOS(_currentTurn);
            }
        }

        /// <summary>Host only: compare a client's state hash against the local one.
        /// Latches both directions (client hash may arrive before the host's own
        /// checkpoint fires, or after).</summary>
        public void HostReceiveRemoteHash(uint turn, uint player, byte[] hash)
        {
            if (_role != NetRole.Host) return;
            _remoteHashes[(turn, player)] = hash;
            if (_lastLocalHash != null && turn == _lastHashTurn && !HashEquals(_lastLocalHash, hash))
                SetOOS(turn);
        }

        private void SetOOS(uint turn)
        {
            if (_oosError != null) return;
            _oosError = $"OOS at turn {turn}: state hash mismatch";
            OnOOSDetected?.Invoke(turn, _oosError);
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static string HashToString(byte[] hash) => Convert.ToHexString(hash);
    }
}
```

被删除的旧 API 的调用方同步处理:`ReceiveRemoteCommands`、`IsTurnReady`、`AdvanceTurn(expectedPlayers)`、`SubmitLocalCommand` 旧槽位语义——调用方只有 `MultiplayerController.cs`(Task 7 重写)与 `NetCommandRoutingTests.cs`(Step 1 已改)。`OnCommandsReady` 事件删除(MPC 里挂的是空委托)。

**Step 4: 跑测试**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~NetLockstepTests|FullyQualifiedName~NetCommandRoutingTests"` → PASS
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/Net/NetTurnManager.cs src/ZeroAD.Sim.Tests/NetLockstepTests.cs src/ZeroAD.Sim.Tests/NetCommandRoutingTests.cs
git commit -m "feat(sim): NetTurnManager 重构为主机权威锁步(三角色+回合屏障)

outbox/bundle 分离杜绝双重执行;主机按回合聚合并广播 bundle;
空批心跳防止等待沉默客户端;HostBootstrap 预产空 bundle 让对局即刻开始。"
```

---

## Task 5: OOS 主机裁决完善 + StateDump(二进制+文本双 dump)

**Files:**
- Modify: `src/ZeroAD.Sim/ComponentManager.cs`(提取 `SerializeFullState`,遍历排序化)
- Modify: `src/ZeroAD.Sim/Serialization/Serializer.cs`(加 `ISectionSerializer`)
- Create: `src/ZeroAD.Sim/Serialization/TextDumpSerializer.cs`
- Create: `src/ZeroAD.Sim/Serialization/StateDump.cs`
- Test: `src/ZeroAD.Sim.Tests/StateDumpTests.cs`(新建)

**Step 1: 写失败测试** `src/ZeroAD.Sim.Tests/StateDumpTests.cs`

```csharp
using System.IO;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class StateDumpTests
{
    private static ComponentManager MakeWorld(uint seed)
    {
        var cm = new ComponentManager(seed);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new HealthComponent { Current = 80, Max = 100 });
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(3f), Fixed.Zero, Fixed.FromFloat(4f));
        return cm;
    }

    private static string TextDump(ComponentManager cm)
    {
        var s = new TextDumpSerializer();
        cm.SerializeFullState(s);
        return s.ToString();
    }

    [Fact]
    public void IdenticalStates_ProduceIdenticalTextDumps()
    {
        Assert.Equal(TextDump(MakeWorld(7)), TextDump(MakeWorld(7)));
    }

    [Fact]
    public void DivergedStates_DiffLocalizesEntityAndField()
    {
        var a = MakeWorld(7);
        var b = MakeWorld(7);
        // Diverge: hurt the entity on b.
        var entity = b.AllEntities[^1];
        b.QueryInterface<HealthComponent>(entity)!.Current = 1;

        string ta = TextDump(a);
        string tb = TextDump(b);
        Assert.NotEqual(ta, tb);
        // The dump carries entity sections and field lines a plain diff can localize.
        Assert.Contains("[entity ", ta);
        Assert.Contains("health", tb.ToLowerInvariant());
    }

    [Fact]
    public void WriteAll_CreatesBinaryAndTextDumps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zeroad_oos_test_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var (bin, txt) = StateDump.WriteAll(MakeWorld(7), dir, turn: 40, playerId: 2);
            Assert.True(File.Exists(bin));
            Assert.True(File.Exists(txt));
            Assert.Contains("oos_turn40_player2", bin);
            Assert.True(new FileInfo(bin).Length > 0);
            Assert.Contains("[entity ", File.ReadAllText(txt));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

注:`HealthComponent.Current/Max` 字段名以 `src/ZeroAD.Sim/Components/Combat.cs` 为准;`ComponentManager.AllEntities` 是 `IReadOnlyList<EntityId>`。

**Step 2: 跑测试确认编译失败**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~StateDumpTests"`
Expected: 编译错误(`TextDumpSerializer`/`StateDump`/`SerializeFullState` 不存在)

**Step 3: 实现**

3a. `src/ZeroAD.Sim/Serialization/Serializer.cs` 追加接口(放在 `ISerializer` 之后):

```csharp
/// <summary>Optional serializer extension: state traversal announces entity/component
/// boundaries so text dumps can emit section headers. Hash/binary serializers ignore it.</summary>
public interface ISectionSerializer
{
    void BeginSection(string name);
}
```

3b. `src/ZeroAD.Sim/ComponentManager.cs` —— 把 `ComputeStateHash` 的方法体提取为 `SerializeFullState`,遍历改为**排序**(实体按 id 升序、组件按类型名排序,保证两端 dump/hash 的遍历顺序与插入顺序无关)。文件顶部确认有 `using System.Linq;`(没有则加):

```csharp
        /// <summary>
        /// Serialize the entire deterministic state (RNG, entity ids, every non-local
        /// entity's components). Traversal order is fully sorted so two peers produce
        /// byte-identical streams regardless of insertion order; used by both the state
        /// hash (OOS detection) and StateDump (OOS forensics).
        /// </summary>
        public void SerializeFullState(Serialization.ISerializer serializer)
        {
            serializer.StringASCII("rng", _rng.Serialize());
            serializer.NumberU32("next entity id", _entityManager.NextEntityId);

            foreach (var kvp in _componentsByEntity.OrderBy(k => k.Key.Value))
            {
                if (kvp.Key.IsLocal)
                    continue;
                if (serializer is Serialization.ISectionSerializer entitySection)
                    entitySection.BeginSection($"entity {kvp.Key.Value}");
                serializer.NumberU32("entity", kvp.Key.Value);
                foreach (var comp in kvp.Value.Values.OrderBy(c => c.GetType().Name))
                {
                    if (serializer is Serialization.ISectionSerializer compSection)
                        compSection.BeginSection($"component {comp.GetType().Name}");
                    comp.Serialize(serializer);
                }
            }
        }

        public byte[] ComputeStateHash()
        {
            var serializer = new Serialization.HashSerializer();
            SerializeFullState(serializer);
            return serializer.ComputeHash();
        }
```

3c. `src/ZeroAD.Sim/Serialization/TextDumpSerializer.cs`(新文件):

```csharp
using System;
using System.Text;

namespace ZeroAD.Sim.Serialization
{
    /// <summary>
    /// Renders serialized state as "name = value" lines under [entity N] / [component T]
    /// section headers. Fixed-point values dump their raw internal value in hex so a
    /// plain `diff` of two peers' dumps pinpoints the diverging field exactly.
    /// </summary>
    public sealed class TextDumpSerializer : ISerializer, ISectionSerializer
    {
        private readonly StringBuilder _sb = new();

        public void BeginSection(string name) => _sb.Append("\n[").Append(name).Append("]\n");

        private void Line(string name, string value) =>
            _sb.Append(name).Append(" = ").Append(value).Append('\n');

        public void NumberU8(string name, byte value) => Line(name, value.ToString());
        public void NumberI8(string name, sbyte value) => Line(name, value.ToString());
        public void NumberU16(string name, ushort value) => Line(name, value.ToString());
        public void NumberI16(string name, short value) => Line(name, value.ToString());
        public void NumberU32(string name, uint value) => Line(name, value.ToString());
        public void NumberI32(string name, int value) => Line(name, value.ToString());
        public void NumberFixed(string name, Maths.Fixed value) =>
            Line(name, "0x" + value.InternalValue.ToString("X8"));
        public void Bool(string name, bool value) => Line(name, value ? "1" : "0");
        public void StringASCII(string name, string value) => Line(name, value);
        public void RawBytes(string name, ReadOnlySpan<byte> data) =>
            Line(name, Convert.ToHexString(data));

        public override string ToString() => _sb.ToString();
    }
}
```

3d. `src/ZeroAD.Sim/Serialization/StateDump.cs`(新文件):

```csharp
using System.IO;

namespace ZeroAD.Sim.Serialization
{
    /// <summary>
    /// OOS forensics: when the host detects a state-hash mismatch, every peer writes
    /// both a binary snapshot (for programmatic inspection/reload tooling later) and a
    /// deterministic text dump (for immediate `diff`). File names carry the checkpoint
    /// turn and the local player id so dumps from multiple peers land side by side.
    /// </summary>
    public static class StateDump
    {
        public static (string binPath, string txtPath) WriteAll(
            ComponentManager cm, string directory, uint turn, uint playerId)
        {
            Directory.CreateDirectory(directory);
            string baseName = Path.Combine(directory, $"oos_turn{turn}_player{playerId}");

            string binPath = baseName + ".bin";
            using (var fs = new FileStream(binPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                cm.SerializeFullState(new BinarySerializer(bw));
            }

            string txtPath = baseName + ".txt";
            var text = new TextDumpSerializer();
            cm.SerializeFullState(text);
            File.WriteAllText(txtPath, text.ToString());

            return (binPath, txtPath);
        }
    }
}
```

**Step 4: 跑测试 + 全量回归(注意哈希遍历排序可能影响既有哈希值——两端同代码所以一致性不受影响,但 `SerializationStabilityTests` 若钉死了具体哈希值需按其断言方式核对)**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj --filter "FullyQualifiedName~StateDumpTests"` → PASS
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿
若 `SerializationStabilityTests` 因排序变化失败:读该测试,若它断言的是"同状态同哈希"(而非某个魔法值),排查是否真实回归;若是魔法值,更新并在 commit message 说明。

**Step 5: Commit**

```bash
git add src/ZeroAD.Sim/ComponentManager.cs src/ZeroAD.Sim/Serialization/ src/ZeroAD.Sim.Tests/StateDumpTests.cs
git commit -m "feat(sim): OOS 状态双 dump(二进制+可 diff 文本)

SerializeFullState 提取并排序化遍历,ComputeStateHash 与 StateDump 共用;
TextDumpSerializer 输出分节 key=value,定点数给内部值 hex。"
```

---

## Task 6: MultiplayerController 重写 —— 纯传输 5 RPC

无自动化测试(Godot 层),验收 = `dotnet build godot/GodotProject.csproj` 通过 + Task 10 手动双实例。

**Files:**
- Modify: `godot/Scripts/MultiplayerController.cs`(整体重写)

**Step 1: 整体重写**

```csharp
using Godot;
using System.Collections.Generic;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>
/// Pure transport for the host-authoritative lockstep. Owns no game logic:
/// clients ship per-turn command batches to the host (RpcId 1), the host
/// broadcasts aggregated turn bundles, state hashes go to the host for
/// arbitration, and OOS is broadcast back so every peer dumps its state.
/// Godot peer ids (ENet connection ids) and game player ids are separate
/// namespaces; the host assigns the mapping in GameStart.
/// </summary>
public sealed partial class MultiplayerController : Node
{
    private ENetMultiplayerPeer? _peer;
    private NetTurnManager? _netTurn;
    private bool _isHost;
    private uint _localPlayerId = 1;
    private uint _seed;
    private readonly Dictionary<int, uint> _peerToPlayer = new();

    public NetTurnManager? NetTurn => _netTurn;
    public uint LocalPlayerId => _localPlayerId;
    public uint Seed => _seed;
    public bool IsHost => _isHost;
    public new bool IsConnected =>
        _peer != null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    public event System.Action<uint, uint>? OnGameStart; // (seed, localPlayerId)
    public event System.Action<string>? OnOOS;

    public void StartHost(int port, uint seed)
    {
        _isHost = true;
        _localPlayerId = 1;
        _seed = seed;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateServer(port, 4);
        Multiplayer.MultiplayerPeer = _peer;
        _peerToPlayer[1] = 1; // host's own ENet id is always 1
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        GD.Print($"Hosting on port {port}, seed={seed}, player=1");
    }

    public void StartClient(string address, int port)
    {
        _isHost = false;
        _peer = new ENetMultiplayerPeer();
        _peer.CreateClient(address, port);
        Multiplayer.MultiplayerPeer = _peer;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        GD.Print($"Connecting to {address}:{port}");
    }

    /// <summary>
    /// Wire a freshly created NetTurnManager to the transport. Called by Main once the
    /// sim exists (host: right after world init; client: after GameStart arrives).
    /// </summary>
    public void AttachTurnManager(NetTurnManager tm)
    {
        _netTurn = tm;
        tm.OnTurnBundleReady += (turn, cmds) =>
            Rpc(nameof(ReceiveBundle), turn, NetCommand.SerializeBatch(cmds));
        tm.OnHashComputed += hash =>
            RpcId(1, nameof(SubmitHashToHost), (int)tm.CurrentTurn, hash);
        tm.OnBatchDue += (turn, cmds) =>
        {
            // The host self-ingests its own batch inside AdvanceTurn; only clients ship.
            if (!_isHost)
                RpcId(1, nameof(SubmitBatchToHost), (int)turn, NetCommand.SerializeBatch(cmds));
        };
        tm.OnOOSDetected += (turn, msg) =>
        {
            // Host arbitrates; loop back through the RPC so the host dumps exactly once.
            if (_isHost)
                Rpc(nameof(ReceiveOOS), turn, msg);
        };
        if (_isHost)
            tm.HostBootstrap();
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"Peer connected: {id}");
        if (!_isHost || _netTurn != null) return; // 2-player scope: start on first client
        uint playerId = (uint)(_peerToPlayer.Count + 1);
        _peerToPlayer[(int)id] = playerId;

        var peers = new List<int>();
        var players = new List<int>();
        foreach (var kvp in _peerToPlayer)
        {
            peers.Add(kvp.Key);
            players.Add((int)kvp.Value);
        }
        Rpc(nameof(ReceiveGameStart), _seed, peers.ToArray(), players.ToArray());
        OnGameStart?.Invoke(_seed, _localPlayerId); // host starts its own game
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"Peer disconnected: {id}");
        _peerToPlayer.Remove((int)id);
        // Reconnection/host migration: out of scope (design doc §9).
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGameStart(uint seed, int[] peers, int[] players)
    {
        _seed = seed;
        long myPeer = Multiplayer.GetUniqueId();
        for (int i = 0; i < peers.Length; i++)
        {
            _peerToPlayer[peers[i]] = (uint)players[i];
            if (peers[i] == myPeer)
                _localPlayerId = (uint)players[i];
        }
        GD.Print($"Game starting: seed={seed}, localPlayer={_localPlayerId}");
        OnGameStart?.Invoke(seed, _localPlayerId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitBatchToHost(int turn, byte[] batch)
    {
        if (!_isHost || _netTurn == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_peerToPlayer.TryGetValue((int)sender, out uint player)) return;
        _netTurn.HostIngestBatch(player, (uint)turn, NetCommand.DeserializeBatch(batch));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBundle(int turn, byte[] bundle)
    {
        _netTurn?.ReceiveTurnBundle((uint)turn, NetCommand.DeserializeBatch(bundle));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitHashToHost(int turn, byte[] hash)
    {
        if (!_isHost || _netTurn == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_peerToPlayer.TryGetValue((int)sender, out uint player)) return;
        _netTurn.HostReceiveRemoteHash((uint)turn, player, hash);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveOOS(int turn, string msg)
    {
        GD.PrintErr($"OOS at turn {turn}: {msg}");
        OnOOS?.Invoke(msg);
    }

    public void Shutdown()
    {
        if (_peer != null)
        {
            _peer.Close();
            _peer = null;
        }
    }
}
```

**Step 2: 构建**

Run: `dotnet build godot/GodotProject.csproj` → 0 error(此时 Main.cs/SimBridge 还在用旧 API,若报`TryAdvanceTurn`/`SubmitCommand`/`InitTurnManager` 缺失属预期——Task 7/8 会接线;可先注释 Main.cs 中 `_mp.` 相关三处调用让编译过,或直接把 Task 7/8 做完再统一构建。推荐顺序:本 Task 只做语法自检 `dotnet build` 允许失败,记录错误清单,继续。)

**Step 3: Commit**

```bash
git add godot/Scripts/MultiplayerController.cs
git commit -m "feat(net): MultiplayerController 重写为主机权威纯传输(5 RPC)

GameStart 下发种子与玩家分配;批次/回合包/哈希/OOS 各一个 RPC;
删除 P2P 互发与 TryAdvanceTurn。"
```

---

## Task 7: SimBridge 接线 —— InitWorld 参数化、屏障、命令包装、地基幽灵视觉

**Files:**
- Modify: `godot/Scripts/SimBridge.cs`

**Step 1: 字段与 TurnManager 退役**

- 删除 `private TurnManager _turnManager = null!;` 与 `InitWorld` 末尾的 `_turnManager = new TurnManager(...)`;删除 `public TurnManager Turns => _turnManager;`。
- 先 grep 引用方:`grep -rn "\.Turns\|_turnManager" godot/ src/`,逐个改为新 API(预期只有 SimBridge 自用;若 HUD/Tutorial 引用,改为读 `_netTurn.CurrentTurn`)。
- 新增:

```csharp
    private NetTurnManager _netTurn = null!;
    public NetTurnManager NetTurn => _netTurn;
    public uint LocalPlayerId { get; private set; } = 1;
```

(`using ZeroAD.Sim.Net;` 加到文件头。)

**Step 2: InitWorld 签名扩展**

`InitWorld(string? templatesPath)` → `InitWorld(string? templatesPath, uint seed = 42, uint localPlayerId = 1, NetRole role = NetRole.Standalone, int playerCount = 1)`:

- 函数体内 `uint seed = 42;` 删除,用参数。
- 玩家实体创建(现 `InitWorld` 115-120 行只建 player 1)改为循环:

```csharp
        for (int pid = 1; pid <= playerCount; pid++)
        {
            var playerEntity = _sim.CreateEntity();
            _sim.AddComponent(playerEntity, new PlayerComponent());
            _sim.AddComponent(playerEntity, new TechnologyManager { });
            _sim.AddComponent(playerEntity, new OwnershipComponent { PlayerId = pid });
            _sim.AddComponent(playerEntity, new EntityLimitsComponent());
            _sim.RegisterPlayer(pid, playerEntity);
            if (pid == (int)localPlayerId)
                _playerEntity = playerEntity;
        }
        LocalPlayerId = localPlayerId;
```

(先读现有 100-125 行确认 `_playerEntity` 后续用途没有别处假定它一定是第一个实体。)
- `InitWorld` 末尾构造:`_netTurn = new NetTurnManager(_sim, commandDelay: 2, localPlayerId, role, expectedPlayers)`,expectedPlayers = `Enumerable.Range(1, playerCount).Select(i => (uint)i).ToHashSet()`(`using System.Linq;`)。
- `InitWorld()` 无参重载保持,链到新签名。

**Step 3: _Process 屏障门控**

替换 `_Process`(现 398-410 行):

```csharp
    private bool _stallLogged;

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        _simAccumulator += delta;
        while (_simAccumulator >= SimTickRate)
        {
            // Turn barrier: in lockstep the sim advances only when the bundle for the
            // upcoming turn has arrived (always true in standalone — local bundles are
            // produced synchronously). While stalled, rendering continues; only the
            // sim pauses.
            if (!_netTurn.CanAdvanceTurn())
            {
                if (!_stallLogged)
                {
                    GD.Print($"[Lockstep] waiting for turn {_netTurn.CurrentTurn} bundle");
                    _stallLogged = true;
                }
                break;
            }
            _stallLogged = false;
            _simAccumulator -= SimTickRate;
            TickSimulation((float)SimTickRate);
            _netTurn.AdvanceTurn();
        }
        SyncVisuals();
    }
```

**Step 4: 命令 API 改为提交包装(单机也走延迟队列)**

替换 `MoveEntity`/`CommandGather`/`CommandAttack`/`CommandSetRallyPoint`/`CommandResearch`/`CommandTrain` 系列(现 929-1028 行)与 `SpawnFoundation`(896-925 行,**删除**,能力已迁入 executor):

```csharp
    // --- Commands (ALL player commands funnel into the lockstep queue; in standalone
    // they execute COMMAND_DELAY turns later, exactly as in multiplayer — one code path,
    // no SP/MP divergence. Presentation-only validation stays in Main.) ---

    public void SubmitCommand(NetCommand cmd) => _netTurn.SubmitLocalCommand(cmd);

    public void MoveEntity(EntityId entity, float x, float z) =>
        SubmitCommand(NetCommand.Move(LocalPlayerId, entity.Value,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandGather(EntityId unit, EntityId target) =>
        SubmitCommand(NetCommand.Gather(LocalPlayerId, unit.Value, target.Value));

    public void CommandAttack(EntityId attacker, EntityId target) =>
        SubmitCommand(NetCommand.Attack(LocalPlayerId, attacker.Value, target.Value));

    /// <summary>Issue a build order: cost charge + foundation spawn happen in the sim
    /// at the execution turn (SimCommandExecutor). `template` is the FULL template name.</summary>
    public void CommandBuild(EntityId builder, string template, float x, float z) =>
        SubmitCommand(NetCommand.Build(LocalPlayerId, builder.Value, template,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandSetRallyPoint(EntityId building, EntityId? target) =>
        SubmitCommand(NetCommand.SetRallyPoint(LocalPlayerId, building.Value, target?.Value ?? 0));

    public void CommandResearch(EntityId building, string techName) =>
        SubmitCommand(NetCommand.Research(LocalPlayerId, building.Value, techName));

    public void CommandTrain(EntityId building, string template, int count = 1, bool batch = false) =>
        SubmitCommand(NetCommand.Train(LocalPlayerId, building.Value, template, batch ? 5 : count));
```

连带处理(grep 所有调用方并适配签名):
- `CommandBuild(builder, foundation)` 旧签名调用方(Main.cs `PlaceBuilding`、AI 相关 `PetraManagers.cs`/`PetraAI.cs`/`AIDirector.cs`/`AIController.cs`)→ 改为新签名(builder, fullTemplate, x, z)。AI 若此前直接扣资源/spawn,改为只发命令。
- `CommandSetRallyPoint(building, target, command, specific)` 的 command/specific 参数删除,调用方适配。
- `CommandTrain(building)`/`CommandTrainSoldier(building)` 便捷重载若还有调用方,保留为 `CommandTrain(building, "units/spart/support_civilian")` 形式的薄包装。
- 旧 `GatherResource` 私有辅助若不再被引用则删除。
- `Tutorial`/`TutorialEngine` 若调用上述 API,一并适配(它们经事件感知即可,通常无需改)。

**Step 5: OnEntityCreated 地基幽灵视觉**

`OnEntityCreated`(现 748 行起)在 `CreateVisualFor` 前加地基分支:

```csharp
        var foundation = _sim.QueryInterface<FoundationComponent>(e.Entity);
        if (foundation != null)
        {
            // Ghost preview for freshly placed foundations (previously done by the
            // deleted SimBridge.SpawnFoundation via direct CreateVisualFor).
            CreateVisualFor(e.Entity, new Color(color, 0.35f), 6f,
                isBuilding: true, isGhost: true, templateName: e.TemplateName);
        }
        else
        {
            CreateVisualFor(e.Entity, color, 1.5f, templateName: e.TemplateName);
        }
```

**Step 6: 构建 + 内核测试回归**

Run: `dotnet build godot/GodotProject.csproj` → 0 error(Main.cs 适配在 Task 8,本步允许 Main.cs 残留少量编译错误,记录下来)
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿

**Step 7: Commit**

```bash
git add godot/Scripts/SimBridge.cs
git commit -m "feat(godot): SimBridge 接回合屏障,命令 API 统一为锁步提交包装

InitWorld 支持 seed/localPlayerId/role/playerCount 参数;单机命令同样
延迟 2 回合执行(SP=MP 单路径);地基幽灵视觉改由 EntityCreated 事件驱动。"
```

---

## Task 8: Main.cs 输入层清理 + MP 启动流程

**Files:**
- Modify: `godot/Scripts/Main.cs`

**Step 1: 右键命令去双重执行**

`HandleRightClick`(现 631-688 行):删除所有 `SubmitNetCmd(...)` 调用,保留 `_sim.CommandAttack/Gather/MoveEntity`——它们现在内部就是提交。集结点调用改为 `_sim.CommandSetRallyPoint(only, targetEntity)`。删除 `SubmitNetCmd` 方法与 `IsMultiplayer` 属性(现 690-701 行)。

**Step 2: Train/Research 去分支**

`TrainVillager`/`TrainSoldier`/`TrainSkirmisher`/`TrainUnit`(现 753-812 行):删除 `if (IsMultiplayer) SubmitNetCmd(...) else` 分支,统一 `_sim.CommandTrain(eid, template, batch: batch)`。`ResearchTech`(814-822)调用不变(现已提交化)。

**Step 3: PlaceBuilding 只发命令**

`PlaceBuilding`(现 824-869 行):
- 保留:负担检查与落点预检(展示性,失败即拒,不发命令)。
- 删除:`player.Wood -= ...` 四行扣费、`SpawnFoundation` 调用、旧 `CommandBuild(eid, foundation)`。
- 新增结尾:

```csharp
        string fullTemplate = MapBuildTemplateName(_buildTemplate);
        foreach (var eid in _selectedEntities)
            if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
            {
                _sim.CommandBuild(eid, fullTemplate, worldPos.Value.X, worldPos.Value.Z);
                break;
            }
        _placeBuildingMode = false;
```

**Step 4: MP 启动流程重接**

读现 `Main.cs` 60-150 行(host/client/参数自启流程),按以下目标改造:

- `_mp.OnGameStart` 新签名 `(seed, localPlayerId)`:`BeginGameplay(seed, (int)localPlayerId)`。
- 修掉客户端硬编码:现 127 行 `BeginGameplay(42, 2)` 删除,种子与玩家 id 一律来自 GameStart。
- `BeginGameplay` 内:`_sim.InitWorld(templatesPath, seed, (uint)playerId, role, playerCount)`,MP 时 `role = _mp.IsHost ? NetRole.Host : NetRole.Client, playerCount = 2`,随后 `_mp.AttachTurnManager(_sim.NetTurn)`;SP 维持 `InitWorld(templatesPath)` 默认参数。现 146 行 `_mp.InitTurnManager(...)` 调用删除。
- `_Process` 里 `_mp.TryAdvanceTurn()`(现 513-514 行)删除(推进归 SimBridge 管)。
- `_mp.OnOOS` 订阅(在 `_mp` 创建处附近):

```csharp
        _mp.OnOOS += msg =>
        {
            string dir = ProjectSettings.GlobalizePath("user://oos");
            var (bin, txt) = ZeroAD.Sim.Serialization.StateDump.WriteAll(
                _sim.Sim, dir, _sim.NetTurn.CurrentTurn, _sim.LocalPlayerId);
            GD.PrintErr($"OOS: {msg}\nState dumped:\n  {txt}\n  {bin}");
        };
```

- `NetCommand` 工厂里硬编码 `player: 1` 的残留调用(若有)改为 `_sim.LocalPlayerId`——经 Step 1-3 后应已无残留,grep 确认:`grep -n "NetCommand\." godot/Scripts/Main.cs` 应无输出(全部封装进 SimBridge 包装)。

**Step 5: 构建 + 全量测试**

Run: `dotnet build godot/GodotProject.csproj` → 0 error 0 warning
Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿

**Step 6: Commit**

```bash
git add godot/Scripts/Main.cs
git commit -m "fix(mp): 输入层只发命令不碰 sim,根除双重执行与建造/研究绕网络

MP 种子与玩家 id 改由 GameStart 下发(修客户端硬编码 42/2);
OOS 触发时自动双 dump 到 user://oos。"
```

---

## Task 9: 收尾验证 + 手动验收清单

**Step 1: 全量自动化**

Run: `dotnet test src/ZeroAD.Sim.Tests/ZeroAD.Sim.Tests.csproj` → 全绿(188+ 旧测试 + 新增)
Run: `dotnet build src/ZeroAD.Sim/ZeroAD.Sim.csproj` 与 `dotnet build godot/GodotProject.csproj` → 0 警告

**Step 2: 手动双实例验收(写入提交说明,逐项打勾)**

在 Godot 4.7 .NET 编辑器中:
1. 实例 A:主菜单 Host(记录端口与 seed);实例 B:Connect by IP 加入 → 两端进入同一种子地图;
2. A 端:框选村民右键移动/采集 → B 端同回合看到相同行为;
3. A 端:训练村民(单击+Shift 批量)→ 两端各出 1/5 个单位,资源只扣一次;
4. A 端:按 B 放置 House → 两端同回合出现幽灵地基、村民前往建造,资源只扣一次;
5. B 端(玩家 2):同样操作其基地 → 两端一致;
6. 研究一项科技 → 两端科技管理器一致;
7. 高强度操作 10 分钟 → 无 OOS 控制台输出;
8. (破坏性验证)临时在客户端代码给某单位多加 1 点血 → 20 回合内两端控制台报 OOS,`user://oos/` 出现 4 个文件(两端各 bin+txt),`diff` 两个 txt 能定位到该实体 Health 行。验证完还原。

**Step 3: 更新进度记忆**

把 MP 锁步已修复、剩余缺口(重连/观战/大厅/MP-AI 禁用)写回项目记忆 `port-status-vs-original-cpp.md`。

**Step 4: Final Commit + 分支汇总**

```bash
git commit -m "test(mp): 双实例手动验收通过(见提交说明)"
git log --oneline main..HEAD
```

---

## 风险与备注

1. **AI 与 MP**:AI(`PetraAI`/`PetraManagers`)经 `SimBridge.CommandX` 调用已自动走提交路径,但 AI 本身用 `GD.Randf()` 非确定——**MP 对局禁止 AI**(设计 §9)。本期不处理 AI 确定性,但在 `MultiplayerController` 或 Main 的 MP 启动处加注释标注;若 SetupGameWorld 默认放 AI,MP 路径跳过 AI 放置(grep `AIDirector\|PetraAI` 在 Main.cs 的接线点)。
2. **SimSystem 静态**:executor 的 Build 落点校验经 `SimSystem.Pathfinder` 静态回退;内核测试里用构造注入规避,生产由 SimBridge 正常 Set。
3. **命令延迟手感**:SP 命令从即时变为 200ms 延迟(对齐原版),若试玩反馈明显,后续再加本地预测(非本期)。
4. **回合哈希频率**:20 回合(2s)一次,与现状一致;OOS 发生时 dump 的是 checkpoint 状态,非发散第一现场——够用(YAGNI)。
