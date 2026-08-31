using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>进攻计划（原版 petra/attackPlan.js，2305 行——Petra 最大单体文件）。
/// 本端口覆盖的完整机制:
///   多波次编组:buildOrders(按类分槽 targetSize/batchSize/priority/interests)+
///     trainMoreUnits(最落后槽优先,queue/queueChamp/queueSiege 分道)+
///     assignUnits(无角色捞人 + outOfPlan 回收 + worker 超配征收,keep 阈值按性格)+
///     addSiegeUnits(Siege 槽,siegeState 三态机)
///   围攻路线:getPathToTarget(真实寻路)+ setRallyPoint(领土边界集结)+
///     checkTargetObstruction(城墙/门阻断检测 → 改打阻断物)
///   状态机:Unstarted(筹备) → Completing(集结)→ Started(推进)→ Completed/Aborted。
/// 海军运输(overseas)未移植——无运输船时跨海目标由 chooseTarget 的可达性过滤拒绝。</summary>
public sealed class AttackPlan
{
    // 原版常量
    public const string TypeRush = "Rush";
    public const string TypeRaid = "Raid";
    public const string TypeDefault = "Attack";
    public const string TypeHugeAttack = "HugeAttack";

    public enum PreparationResult { Failed = 0, KeepGoing = 1, Start = 2 }
    public enum AttackState { Unstarted, Completing, Started, Completed, Aborted }
    private const int SiegeNotTested = 0, SiegeNoTrainer = 1, SiegeAdded = 2;

    public readonly string Type;
    public readonly int Name;
    public readonly PetraConfig Config;
    public AttackState State { get; private set; }
    public bool Paused;

    /// <summary>参与单位(entity id 集;metadata plan == Name 为其登记方式)。</summary>
    public readonly HashSet<uint> UnitCollection = new();
    public uint? Target;
    public int? TargetPlayer;
    public FixedVector2D? RallyPoint;
    public FixedVector2D? TargetPos;
    /// <summary>到目标的真实路径(集结点→目标;原版 this.path,首元素是下一步)。</summary>
    public List<FixedVector2D>? Path;
    /// <summary>被城墙/门阻断 → Target 改指阻断物(原版 isBlocked)。</summary>
    public bool IsBlocked;

    // ── 多波次编组(原版 unitStat/buildOrders)──
    /// <summary>单位槽位定义(原版 unitStat 项):类过滤 + 规模/批量 + 优先级 + 评分兴趣。</summary>
    public sealed record UnitStat(
        double Priority, int MinSize, int TargetSize, int BatchSize,
        string[] Classes, (string Interest, double Weight)[] Interests);

    /// <summary>编组订单:[当前计数(含在训), 类过滤, 槽位, 槽名](原版 buildOrders 行)。</summary>
    public sealed class BuildOrder
    {
        public int CurrentCount;
        public string[] Classes = Array.Empty<string>();
        public UnitStat Stats = new(1, 0, 0, 1, Array.Empty<string>(),
            Array.Empty<(string, double)>());
        public string Name = "";
    }
    public readonly List<BuildOrder> BuildOrders = new();
    private readonly Dictionary<string, UnitStat> _unitStats = new();
    private int _siegeState = SiegeNotTested;
    public bool CanBuildUnits;
    /// <summary>筹备期上限(秒;原版 maxCompletingTime——超时强制推)。</summary>
    private double _maxCompletingTime;
    /// <summary>集结开始时刻(秒)。</summary>
    private double _completingSince = -1;

    // 队列名(原版 queueManager.addQueue("plan_" + name…))。
    public string QueueName => "plan_" + Name;
    public string QueueChampName => QueueName + "_champ";
    public string QueueSiegeName => QueueName + "_siege";

    public AttackPlan(GameState gameState, int name, string type, PetraConfig config,
        int? rushTargetSize = null)
    {
        Type = type;
        Name = name;
        Config = config;
        State = AttackState.Unstarted;
        CanBuildUnits = true;

        // 原版构造函数的单位槽位表(attackPlan.js:124-174)。
        if (type == TypeRush)
        {
            _unitStats["Infantry"] = new(1, 10, rushTargetSize ?? 20, 2,
                new[] { "Infantry" },
                new[] { ("strength", 1.0), ("costsResource", 0.5), ("costsResource", 0.6) });
            _unitStats["FastMoving"] = new(1, 2, 4, 2,
                new[] { "FastMoving", "CitizenSoldier" }, new[] { ("strength", 1.0) });
        }
        else if (type == TypeRaid)
        {
            _unitStats["FastMoving"] = new(1, 3, 4, 2,
                new[] { "FastMoving", "CitizenSoldier" }, new[] { ("strength", 1.0) });
        }
        else if (type == TypeHugeAttack)
        {
            _unitStats["RangedInfantry"] = new(0.7, 5, 20, 5,
                new[] { "Infantry", "Ranged", "CitizenSoldier" }, new[] { ("strength", 3.0) });
            _unitStats["MeleeInfantry"] = new(0.7, 5, 20, 5,
                new[] { "Infantry", "Melee", "CitizenSoldier" }, new[] { ("strength", 3.0) });
            _unitStats["ChampRangedInfantry"] = new(1, 3, 18, 3,
                new[] { "Infantry", "Ranged", "Champion" }, new[] { ("strength", 3.0) });
            _unitStats["ChampMeleeInfantry"] = new(1, 3, 18, 3,
                new[] { "Infantry", "Melee", "Champion" }, new[] { ("strength", 3.0) });
            _unitStats["RangedFastMoving"] = new(0.7, 4, 20, 4,
                new[] { "FastMoving", "Ranged", "CitizenSoldier" }, new[] { ("strength", 2.0) });
            _unitStats["MeleeFastMoving"] = new(0.7, 4, 20, 4,
                new[] { "FastMoving", "Melee", "CitizenSoldier" }, new[] { ("strength", 2.0) });
            _unitStats["Hero"] = new(1, 0, 1, 1, new[] { "Hero" }, new[] { ("strength", 2.0) });
        }
        else   // Attack(默认)
        {
            _unitStats["RangedInfantry"] = new(1, 6, 16, 3,
                new[] { "Infantry", "Ranged" },
                new[] { ("canGather", 1.0), ("strength", 1.6) });
            _unitStats["MeleeInfantry"] = new(1, 6, 16, 3,
                new[] { "Infantry", "Melee" },
                new[] { ("canGather", 1.0), ("strength", 1.6) });
            _unitStats["FastMoving"] = new(1, 2, 6, 2,
                new[] { "FastMoving", "CitizenSoldier" }, new[] { ("strength", 1.0) });
        }

        // 原版:规模随机化 randFloat(0.8,1.2)(走 Rand48 保确定)+ 难度缩放 + popScaling。
        double variation = 0.8 + gameState.Cm.RNG.NextDouble() * 0.4;
        if (Config.Difficulty < DifficultyLevel.Easy) variation *= 0.2;
        else if (Config.Difficulty < DifficultyLevel.Medium) variation *= 0.6;
        foreach (var key in _unitStats.Keys.ToList())
        {
            var u = _unitStats[key];
            int target = (int)Math.Ceiling(variation * u.TargetSize);
            int min = Config.Difficulty < DifficultyLevel.Easy
                ? Math.Min(target, Math.Min(u.MinSize, 2))
                : Math.Min(u.MinSize, target);
            int batch = Config.Difficulty < DifficultyLevel.Easy ? min : u.BatchSize;
            target = (int)Math.Ceiling(Config.PopScaling * target);
            min = (int)Math.Ceiling(Config.PopScaling * min);
            _unitStats[key] = u with { TargetSize = target, MinSize = min, BatchSize = batch };
        }
    }

    /// <summary>原版 init:注册三条计划队列(优先级按类型)+ 建 buildOrders。</summary>
    public void Init(GameState gameState, QueueManager queues)
    {
        int priority = Type switch
        {
            TypeRush => 250, TypeRaid => 150, TypeHugeAttack => 90, _ => 70,
        };
        queues.AddQueue(QueueName, priority);
        queues.AddQueue(QueueChampName, priority + 1);
        queues.AddQueue(QueueSiegeName, priority);
        foreach (var kv in _unitStats)
        {
            BuildOrders.Add(new BuildOrder
            {
                CurrentCount = 0,
                Classes = kv.Value.Classes,
                Stats = kv.Value,
                Name = kv.Key,
            });
        }
    }

    public bool IsStarted() => State is AttackState.Started or AttackState.Completed;
    public bool HasSiegeUnits() => _siegeState == SiegeAdded;

    // ── 筹备(原版 updatePreparation)──

    public PreparationResult UpdatePreparation(GameState gameState, QueueManager queues,
        AttackManager attackManager)
    {
        if (State == AttackState.Completing)
        {
            // 集结中:目标没了 → 回 Unstarted 重选;到齐/超时 → Start。
            if (Target == null || gameState.GetEntityById(Target.Value) == null)
            {
                State = AttackState.Unstarted;
                Target = null;
                return PreparationResult.KeepGoing;
            }
            if (RallyReachedFraction(gameState) < 0.8f
                && gameState.ElapsedTime < _completingSince + _maxCompletingTime)
                return PreparationResult.KeepGoing;
            return PreparationResult.Start;
        }

        // 波次人员补充(原版 assignUnits 每轮跑——单位流失即补)。
        AssignUnits(gameState, attackManager);
        // 有 Raid 筹备中时,让出一个 FastMoving 加速其编组(原版 reassignFastUnit)。
        if (Type != TypeRaid
            && attackManager.GetAttackInPreparation(TypeRaid) is { } raid)
            ReassignFastUnit(gameState, raid);

        // 终局加速(原版:有攻城器且兵力 > 20 + 2×敌总兵力 → 强推)。
        if (gameState.Net?.CurrentTurn % 5 == 0 && HasSiegeUnits())
        {
            int totEnemies = 0;
            bool hasEnemies = false;
            foreach (var p in gameState.GetEnemies())
            {
                if (attackManager.IsDefeated(p)) continue;
                hasEnemies = true;
                totEnemies += gameState.GetEnemyUnits().Values().Count(e => e.Owner == p);
            }
            if (hasEnemies && UnitCollection.Count > 20 + 2 * totEnemies)
                return ForceStart(gameState, queues);
        }

        // 满人口临近:够格就收尾,否则流产让别的计划收人(原版 PREPARATION_FAILED 分支)。
        if (gameState.GetPopulationMax() - gameState.GetPopulation() < 5)
        {
            int lengthMin = 16;
            if (gameState.GetPopulationMax() < 300)
                lengthMin -= (int)(8 * (300 - gameState.GetPopulationMax()) / 300);
            if (CanStart() || UnitCollection.Count > lengthMin)
                EmptyQueues(queues);
            else
                return PreparationResult.Failed;
        }
        else if (MustStart())
        {
            // 还有在编训的 → 清空队列等它们训完。
            if (CountQueued(queues) > 0)
            {
                EmptyQueues(queues);
                return PreparationResult.KeepGoing;
            }
        }
        else
        {
            if (CanBuildUnits)
            {
                // 攻城器补槽(原版:SIEGE_NOT_TESTED 或 NO_TRAINER 每 5 回合重试)。
                if (_siegeState == SiegeNotTested
                    || _siegeState == SiegeNoTrainer && gameState.Net?.CurrentTurn % 5 == 0)
                    AddSiegeUnits(gameState);
                TrainMoreUnits(gameState, queues);
                if (BuildOrders.Count == 0)
                    return PreparationResult.Failed;   // 无训练设施,槽位全废
            }
            return PreparationResult.KeepGoing;
        }

        // ── 进入集结(原版 PREPARATION_START 前的 completing 段)──
        State = AttackState.Completing;
        _completingSince = gameState.ElapsedTime;
        _maxCompletingTime = Type == TypeRaid ? 20 : Type == TypeRush ? 40 : 60;

        if (Target == null && !ChooseTarget(gameState, attackManager))
            return PreparationResult.Failed;
        if (!ComputePathToTarget(gameState, attackManager))
            return PreparationResult.Failed;

        // 集结下令:各单位 moveToRange(rally, 0..15);载货的先回投放站(原版 returnResources)。
        if (RallyPoint.HasValue)
            foreach (var id in UnitCollection)
            {
                var ent = gameState.GetEntityById(id);
                if (ent == null || ent.Position2D == default) continue;
                gameState.Metadata.Set(id, "role", WorkerRoles.RoleAttack);
                gameState.Metadata.Set(id, "subrole", WorkerRoles.SubroleCompleting);
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                    (uint)gameState.PlayerId, id, RallyPoint.Value.X, RallyPoint.Value.Y));
            }
        RemoveQueues(queues);
        return PreparationResult.KeepGoing;
    }

    /// <summary>原版 canStart:兵力达各槽 minSize 之和(近似:总数 ≥ ΣminSize)。</summary>
    public bool CanStart()
    {
        int minTotal = 0;
        foreach (var o in BuildOrders) minTotal += o.Stats.MinSize;
        return UnitCollection.Count >= Math.Max(minTotal, 1);
    }

    /// <summary>原版 mustStart:总兵力 ≥ ΣtargetSize(或满人口挤压)。</summary>
    public bool MustStart()
    {
        int targetTotal = 0;
        foreach (var o in BuildOrders) targetTotal += o.Stats.TargetSize;
        return targetTotal > 0 && UnitCollection.Count >= targetTotal;
    }

    private PreparationResult ForceStart(GameState gameState, QueueManager queues)
    {
        EmptyQueues(queues);
        State = AttackState.Completing;
        _completingSince = gameState.ElapsedTime;
        _maxCompletingTime = 0;   // 立即推
        return PreparationResult.KeepGoing;
    }

    // ── 波次训练(原版 trainMoreUnits)──

    private void TrainMoreUnits(GameState gameState, QueueManager queues)
    {
        // 计数 = 计划内该类的单位 + 三条计划队列里在排的该槽单位。
        foreach (var order in BuildOrders)
        {
            string special = $"Plan_{Name}_{order.Name}";
            int queued = CountQueuedInPlanQueues(queues, special);
            order.CurrentCount = CountPlanUnitsByClasses(gameState, order.Classes) + queued;
        }
        // 最落后槽优先:current/target - priority 升序;完成槽 +1000 沉底;并列槽名倒序。
        BuildOrders.Sort((a, b) =>
        {
            double va = (double)a.CurrentCount / Math.Max(a.Stats.TargetSize, 1) - a.Stats.Priority;
            if (a.CurrentCount >= a.Stats.TargetSize) va += 1000;
            double vb = (double)b.CurrentCount / Math.Max(b.Stats.TargetSize, 1) - b.Stats.Priority;
            if (b.CurrentCount >= b.Stats.TargetSize) vb += 1000;
            int cmp = va.CompareTo(vb);
            return cmp != 0 ? cmp : string.CompareOrdinal(b.Name, a.Name);
        });

        var first = BuildOrders[0];
        if (first.CurrentCount >= first.Stats.TargetSize) return;

        // 分道:Siege/Hero → siege 队列;Champion → champ;其余 → 主队列。
        string queueName = first.Name == "Siege" || first.Classes.Contains("Hero")
            ? QueueSiegeName
            : first.Classes.Contains("Champion") ? QueueChampName : QueueName;
        var queue = queues.GetQueue(queueName);
        if (queue == null || queue.Length > 5) return;

        string? template = Headquarters.FindBestTrainableUnit(gameState,
            first.Classes, first.Stats.Interests);
        if (template == null)
        {
            // 无此类可训模板 → 废槽(原版 HACK 同款)。
            _unitStats.Remove(first.Name);
            BuildOrders.RemoveAt(0);
            return;
        }
        int batch = Math.Min(first.Stats.BatchSize, first.Stats.TargetSize - first.CurrentCount);
        var metadata = new Dictionary<string, object>
        {
            ["plan"] = Name,
            ["special"] = $"Plan_{Name}_{first.Name}",
            ["base"] = 0,
            // CitizenSoldier 训完归 worker 角色(原版同款 role 预标)。
            ["role"] = gameState.GetTemplate(template)?.HasClass("CitizenSoldier") == true
                ? WorkerRoles.RoleWorker : WorkerRoles.RoleAttack,
        };
        queues.AddPlan(queueName, new TrainingPlan(gameState, template, metadata, batch, batch));
    }

    private int CountQueuedInPlanQueues(QueueManager queues, string special)
    {
        int n = 0;
        foreach (var qn in new[] { QueueName, QueueChampName, QueueSiegeName })
            n += queues.GetQueue(qn)?.CountQueuedUnitsWithMetadata("special", special) ?? 0;
        return n;
    }

    private int CountQueued(QueueManager queues)
    {
        int n = 0;
        foreach (var qn in new[] { QueueName, QueueChampName, QueueSiegeName })
            n += queues.GetQueue(qn)?.CountQueuedUnits() ?? 0;
        return n;
    }

    private int CountPlanUnitsByClasses(GameState gameState, string[] classes)
    {
        int n = 0;
        foreach (var id in UnitCollection)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null) continue;
            bool match = true;
            foreach (var cls in classes)
                if (!ent.HasClass(cls)) { match = false; break; }
            if (match) n++;
        }
        return n;
    }

    // ── 人员补充(原版 assignUnits)──

    private void AssignUnits(GameState gameState, AttackManager attackManager)
    {
        // 不可造兵(无训练设施)→ 捞全部可用(原版 canBuildUnits=false 分支)。
        if (!CanBuildUnits)
        {
            foreach (var ent in gameState.GetOwnUnits().Values())
                if (IsAvailableUnit(gameState, ent))
                    AddUnit(gameState, ent);
            return;
        }

        if (Type == TypeRaid)
        {
            // Raid:快单位全收(留 2 只打猎——原版 num++ < 2 continue)。
            int num = 0;
            foreach (var ent in gameState.GetOwnUnits().Values())
            {
                if (!ent.HasClass("FastMoving") || !IsAvailableUnit(gameState, ent)) continue;
                if (num++ < 2) continue;
                AddUnit(gameState, ent);
            }
            return;
        }

        // 1) 无角色单位(Ship/Support 除外,需有攻击能力)。
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            var role = gameState.Metadata.GetObject(ent.Id, "role");
            if (role != null) continue;
            if (ent.HasClass("Ship") || ent.HasClass("Support") || !ent.CanAttack) continue;
            if (!IsAvailableUnit(gameState, ent)) continue;
            AddUnit(gameState, ent);
        }
        // 2) outOfPlan 回收池(离计划/打完仗的单位)。
        foreach (var id in attackManager.OutOfPlan)
        {
            var ent = gameState.GetEntityById(id);
            if (ent != null && IsAvailableUnit(gameState, ent))
                AddUnit(gameState, ent);
        }
        attackManager.OutOfPlan.Clear();

        // 3) worker 超配征收(Easy 以下不征;原版 keep 阈值:
        //    非 Rush = 6 + 4×敌数 + 8×防御性格,Rush = 8;每基地至少留 5)。
        if (Config.Difficulty <= DifficultyLevel.Easy) return;
        int numKept = 0;
        var numbase = new Dictionary<int, int>();
        int keep = Type != TypeRush
            ? 6 + 4 * gameState.GetEnemies().Count + (int)(8 * Config.Personality.Defensive)
            : 8;
        keep = (int)Math.Round(Config.PopScaling * keep);
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            var role = gameState.Metadata.GetObject(ent.Id, "role")?.ToString();
            if (role != WorkerRoles.RoleWorker) continue;
            if (!ent.HasClass("CitizenSoldier") || !IsAvailableUnit(gameState, ent)) continue;
            int baseId = gameState.Metadata.GetObject(ent.Id, "base") is int b ? b : 0;
            numbase[baseId] = numbase.GetValueOrDefault(baseId) + 1;
            if (numKept++ < keep || numbase[baseId] < 5) continue;
            // 非 Rush 只征 idle(原版:忙着的工人不动)。
            if (Type != TypeRush
                && gameState.Metadata.GetObject(ent.Id, "subrole")?.ToString() != WorkerRoles.SubroleIdle)
                continue;
            AddUnit(gameState, ent);
        }
    }

    /// <summary>原版 isAvailableUnit:有位置 + 无 plan/transport 元数据。</summary>
    private bool IsAvailableUnit(GameState gameState, AIEntity ent)
    {
        if (ent.Position2D == default) return false;
        var plan = gameState.Metadata.GetObject(ent.Id, "plan");
        if (plan is int p && p != -1) return false;
        if (gameState.Metadata.GetObject(ent.Id, "transport") != null) return false;
        if (gameState.Metadata.GetObject(ent.Id, "transporter") != null) return false;
        return true;
    }

    private void AddUnit(GameState gameState, AIEntity ent)
    {
        gameState.Metadata.Set(ent.Id, "plan", Name);
        UnitCollection.Add(ent.Id);
    }

    /// <summary>原版 reassignFastUnit:每轮让出一个 FastMoving+CitizenSoldier 给筹备中的 Raid。</summary>
    private void ReassignFastUnit(GameState gameState, AttackPlan raid)
    {
        foreach (var id in UnitCollection)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null || ent.Position2D == default) continue;
            if (!ent.HasClass("FastMoving") || !ent.HasClass("CitizenSoldier")) continue;
            gameState.Metadata.Set(id, "plan", raid.Name);
            UnitCollection.Remove(id);
            raid.UnitCollection.Add(id);
            return;
        }
    }

    // ── 攻城器补槽(原版 addSiegeUnits)──

    private void AddSiegeUnits(GameState gameState)
    {
        if (_siegeState == SiegeAdded || State != AttackState.Unstarted) return;
        string[][] classes = {
            new[] { "Siege", "Melee" }, new[] { "Siege", "Ranged" }, new[] { "Elephant", "Melee" },
        };
        var hasTrainer = new bool[3];
        string civ = gameState.GetPlayerCiv();
        foreach (var ent in gameState.GetOwnTrainingFacilities().Values())
        {
            var trainables = ent.Template.TrainableEntities;
            if (trainables == null) continue;
            foreach (var t in trainables.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var template = gameState.GetTemplate(gameState.ApplyCiv(t));
                if (template == null) continue;
                for (int ci = 0; ci < classes.Length; ci++)
                    if (classes[ci].All(template.HasClass)) hasTrainer[ci] = true;
            }
        }
        if (hasTrainer.All(h => !h))
        {
            _siegeState = SiegeNoTrainer;
            return;
        }
        // 原版:i = this.name % 3 起旋到首个有训练设施的类。
        int i = Name % classes.Length;
        for (int k = 0; k < classes.Length; k++, i = (i + 1) % classes.Length)
            if (hasTrainer[i]) break;

        _siegeState = SiegeAdded;
        int targetSize;
        if (Config.Difficulty < DifficultyLevel.Medium)
            targetSize = Type == TypeHugeAttack
                ? Math.Max(Config.Difficulty, 1) : Math.Max(Config.Difficulty - 1, 0);
        else
            targetSize = Type == TypeHugeAttack ? Config.Difficulty + 1 : Config.Difficulty - 1;
        targetSize = Math.Max((int)Math.Round(Config.PopScaling * targetSize),
            Type == TypeHugeAttack ? 1 : 0);
        if (targetSize == 0) return;
        _unitStats["Siege"] = new UnitStat(1, 0, targetSize, Math.Min(targetSize, 2),
            classes[i], new[] { ("siegeStrength", 3.0) });
        BuildOrders.Add(new BuildOrder
        {
            CurrentCount = 0,
            Classes = classes[i],
            Stats = _unitStats["Siege"],
            Name = "Siege",
        });
        // 原版 addBuildOrder(resetQueue=true):新槽清空旧队列重排。
        EmptyQueues(GetQueuesRef);
    }

    // addSiegeUnits 需要 queues 引用——UpdatePreparation 传入缓存。
    private QueueManager GetQueuesRef => _queuesRef
        ?? throw new InvalidOperationException("AttackPlan queues not wired");
    private QueueManager? _queuesRef;

    // ── 目标选择(原版 chooseTarget/getNearestTarget + 三 finder)──

    public bool ChooseTarget(GameState gameState, AttackManager attackManager)
    {
        TargetPlayer ??= attackManager.GetEnemyPlayer(gameState, this);
        if (TargetPlayer == null) return false;

        var target = GetNearestTarget(gameState, RallyPoint ?? FixedVector2D.Zero, attackManager);
        if (target == null)
        {
            // 全部已毁?重选目标玩家再来一次(原版同款)。
            TargetPlayer = attackManager.GetEnemyPlayer(gameState, this);
            if (TargetPlayer == null) return false;
            target = GetNearestTarget(gameState, RallyPoint ?? FixedVector2D.Zero, attackManager);
            if (target == null) return false;
        }
        Target = target.Id;
        TargetPos = target.Position2D;

        // 原版:目标与 rally 不同陆 → 找同陆最近基地作新 rally(按岛大小加权);
        // 不同陆且无运输 → 不可达,放弃。我们无运输船:不同陆即换目标玩家重选由
        // getEnemyPlayer 的同陆 CC 偏好兜底;仍不同陆 → 拒。
        if (gameState.Accessibility != null)
        {
            ushort targetAccess = gameState.Accessibility.GetAccessValue(
                target.Position2D.X.ToFloat(), target.Position2D.Y.ToFloat());
            ushort rallyAccess = RallyPoint.HasValue
                ? gameState.Accessibility.GetAccessValue(
                    RallyPoint.Value.X.ToFloat(), RallyPoint.Value.Y.ToFloat())
                : targetAccess;
            if (targetAccess != rallyAccess)
            {
                FixedVector2D? rallySame = null;
                float distSame = float.MaxValue;
                if (attackManager.Hq == null) return false;
                foreach (var b in attackManager.Hq.BasesManager.Bases)
                {
                    if (b.AnchorId == null) continue;
                    var anchor = gameState.GetEntityById(b.AnchorId.Value);
                    if (anchor == null || anchor.Position2D == default) continue;
                    ushort baseAccess = gameState.Accessibility.GetAccessValue(
                        anchor.Position2D.X.ToFloat(), anchor.Position2D.Y.ToFloat());
                    if (baseAccess != targetAccess) continue;
                    float dx = anchor.Position2D.X.ToFloat() - target.Position2D.X.ToFloat();
                    float dz = anchor.Position2D.Y.ToFloat() - target.Position2D.Y.ToFloat();
                    float dist = dx * dx + dz * dz;
                    if (dist < distSame) { distSame = dist; rallySame = anchor.Position2D; }
                }
                if (rallySame == null) return false;   // 不可达(无海军)
                RallyPoint = rallySame;
            }
        }
        return true;
    }

    /// <summary>原版 getNearestTarget:类型分派 finder → 最近(字段惩罚)→
    /// checkTargetObstruction(被墙挡则改打阻断物)。</summary>
    private AIEntity? GetNearestTarget(GameState gameState, FixedVector2D position,
        AttackManager attackManager)
    {
        IsBlocked = false;
        var targets = Type == TypeRaid
            ? RaidTargetFinder(gameState, attackManager)
            : Type is TypeRush or TypeDefault
                ? RushTargetFinder(gameState, TargetPlayer)
                : null;
        if (targets == null || targets.Count == 0)
        {
            // Rush/Default 找不到孤立建筑时,有攻城器则回落默认 finder(原版同款)。
            if (Type is TypeRush or TypeDefault && (HasSiegeUnits() || targets == null))
                targets = DefaultTargetFinder(gameState, TargetPlayer);
            targets ??= DefaultTargetFinder(gameState, TargetPlayer);
        }
        if (targets.Count == 0) return null;

        AIEntity? best = null;
        float minDist = float.MaxValue;
        foreach (var ent in targets)
        {
            if (!IsValidTarget(gameState, ent)) continue;
            float dx = ent.Position2D.X.ToFloat() - position.X.ToFloat();
            float dz = ent.Position2D.Y.ToFloat() - position.Y.ToFloat();
            float dist = dx * dx + dz * dz;
            // 非 Rush/Raid 不优先打田(原版 +100000 惩罚)。
            if (Type != TypeRush && Type != TypeRaid && ent.HasClass("Field"))
                dist += 100000;
            if (dist < minDist) { minDist = dist; best = ent; }
        }
        if (best == null) return null;
        best = CheckTargetObstruction(gameState, best, position);
        if (best != null) TargetPlayer = best.Owner;
        return best;
    }

    /// <summary>原版 isValidTarget:有位置 + 非 decaying(尸体不算目标)。</summary>
    private static bool IsValidTarget(GameState gameState, AIEntity ent)
    {
        if (ent.Position2D == default) return false;
        return !ent.IsDead;
    }

    /// <summary>原版 defaultTargetFinder:征服关键目标分级——
    /// CivCentre → ConquestCritical → Town → Village → 任意征服关键(含单位,除船)。</summary>
    private List<AIEntity> DefaultTargetFinder(GameState gameState, int? playerEnemy)
    {
        var structures = gameState.GetEnemyStructures().Values()
            .Where(e => playerEnemy == null || e.Owner == playerEnemy)
            .Where(e => IsValidTarget(gameState, e))
            .ToList();
        foreach (var cls in new[] { "CivCentre", "ConquestCritical", "Town", "Village" })
        {
            var tier = structures.Where(e => e.HasClass(cls)).ToList();
            if (tier.Count > 0) return tier;
        }
        return gameState.GetEnemyUnits().Values()
            .Where(e => playerEnemy == null || e.Owner == playerEnemy)
            .Where(e => e.HasClass("ConquestCritical") && !e.HasClass("Ship"))
            .Where(e => IsValidTarget(gameState, e))
            .ToList();
    }

    /// <summary>原版 rushTargetFinder:孤立无防建筑(80m 内无防御火力)里最近的。</summary>
    private List<AIEntity> RushTargetFinder(GameState gameState, int? playerEnemy)
    {
        var buildings = gameState.GetEnemyStructures().Values()
            .Where(e => playerEnemy == null || e.Owner == playerEnemy)
            .Where(e => e.Owner != 0)
            .ToList();
        if (buildings.Count == 0) return new List<AIEntity>();

        var position = CentrePosition(gameState) ?? RallyPoint ?? FixedVector2D.Zero;
        AIEntity? target = null;
        float minDist = float.MaxValue;
        foreach (var building in buildings)
        {
            if (building.HasDefensiveFire || !IsValidTarget(gameState, building)) continue;
            bool defended = buildings.Any(defense =>
                defense.HasDefensiveFire && defense.Position2D != default
                && SquareDist(building.Position2D, defense.Position2D) < 6400);
            if (defended) continue;
            float dist = SquareDist(building.Position2D, position);
            if (dist >= minDist) continue;
            minDist = dist;
            target = building;
        }
        return target != null ? new List<AIEntity> { target } : new List<AIEntity>();
    }

    /// <summary>原版 raidTargetFinder:defenseManager.targetList 里的地基。</summary>
    private static List<AIEntity> RaidTargetFinder(GameState gameState, AttackManager attackManager)
    {
        // 原版:defenseManager.targetList(遭我方反击的敌地基)即 Raid 目标集。
        var list = new List<AIEntity>();
        if (attackManager.Hq == null) return list;
        foreach (var id in attackManager.Hq.DefenseManager.TargetList)
        {
            var ent = gameState.GetEntityById(id);
            if (ent != null && ent.Position2D != default) list.Add(ent);
        }
        return list;
    }

    /// <summary>计划兵力中心(原版 unitCollection.getCentrePosition)。</summary>
    private FixedVector2D? CentrePosition(GameState gameState)
    {
        float sx = 0, sz = 0;
        int n = 0;
        foreach (var id in UnitCollection)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null || ent.Position2D == default) continue;
            sx += ent.Position2D.X.ToFloat();
            sz += ent.Position2D.Y.ToFloat();
            n++;
        }
        return n > 0
            ? new FixedVector2D(Fixed.FromFloat(sx / n), Fixed.FromFloat(sz / n))
            : null;
    }

    // ── 围攻路线(原版 checkTargetObstruction/getPathToTarget/setRallyPoint)──

    /// <summary>原版 checkTargetObstruction:同陆时计算真实路径;路径终点距目标超
    /// (阻挡半径+10)说明路被挡——沿"终点→目标"方向找阻断建筑(城墙/门),改打阻断物。
    /// 简化:障碍形状判定用 OBB 近似(Static 矩形旋转;Gate 门洞宽放行;
    /// Math.Cos/Sin 禁 → Trig.SinCosApprox)。</summary>
    private AIEntity? CheckTargetObstruction(GameState gameState, AIEntity target, FixedVector2D position)
    {
        var pathfinder = SimSystem.Pathfinder;
        if (pathfinder == null || gameState.Accessibility == null) return target;
        // 不同陆不查(原版同款——海路目标由 chooseTarget 可达性兜底)。
        ushort targetAccess = gameState.Accessibility.GetAccessValue(
            target.Position2D.X.ToFloat(), target.Position2D.Y.ToFloat());
        ushort posAccess = gameState.Accessibility.GetAccessValue(
            position.X.ToFloat(), position.Y.ToFloat());
        if (targetAccess != posAccess) return target;

        var goal = PathGoal.Point(target.Position2D.X, target.Position2D.Y);
        var path = pathfinder.ComputePath(position, goal);
        if (path.IsEmpty) return null;   // 原版:无路径 → 目标不可达

        // 路径终点(距目标最近点)。
        var last = path.Waypoints[0];
        var pathPos = new FixedVector2D(last.X, last.Z);
        float distDx = (target.Position2D.X - pathPos.X).ToFloat();
        float distDz = (target.Position2D.Y - pathPos.Y).ToFloat();
        float dist = MathF.Sqrt(distDx * distDx + distDz * distDz);
        float radius = ObstructionRadiusOf(target);
        if (dist < radius + 10) return target;   // 可达——但原版仍查"是否穿过敌门"(见下)

        // 终点再向目标方向推 1.8m(原版 1+0.8 clearance),落在某敌建筑障碍内 → 阻断物。
        float dirX = (target.Position2D.X.ToFloat() - pathPos.X.ToFloat()) / Math.Max(dist, 0.01f);
        float dirZ = (target.Position2D.Y.ToFloat() - pathPos.Y.ToFloat()) / Math.Max(dist, 0.01f);
        foreach (var s in gameState.GetEnemyStructures().Values())
        {
            if (s.Position2D == default || s.HasClass("Field")) continue;
            if (dist < radius + 10 && !s.HasClass("Gate")) continue;   // 原版:已近目标只查门
            if (PointInObstruction(s,
                    pathPos.X.ToFloat() + 1.8f * dirX, pathPos.Y.ToFloat() + 1.8f * dirZ))
            {
                IsBlocked = true;
                return s;   // 改打阻断物(原版:blocker 成为新 target)
            }
        }
        return target;
    }

    /// <summary>点是否在建筑障碍内(OBB:Static 宽深+旋转;无角度信息时按 AABB 近似;
    /// Gate 门洞中央 doorHalfWidth 内放行——原版 Obstructions/Door 语义)。</summary>
    private static bool PointInObstruction(AIEntity s, float px, float pz)
    {
        float ox = s.Position2D.X.ToFloat(), oz = s.Position2D.Y.ToFloat();
        float angle = 0f;
        // 朝向(Position/Rotation 不可读时按 0——方形墙段主轴向)。
        var pos = s.Cm.QueryInterface<PositionComponent>(s.Entity);
        if (pos != null) angle = pos.Rotation.Y.ToFloat();
        float width = s.Template.GetFloat("Obstruction/Static/@width");
        float depth = s.Template.GetFloat("Obstruction/Static/@depth");
        if (width <= 0)
        {
            // 门/组合障碍(原版 Obstructions 三段合并宽深;门洞中央放行)。
            float door = s.Template.GetFloat("Obstruction/Obstructions/Door/@width");
            float left = s.Template.GetFloat("Obstruction/Obstructions/Left/@width");
            float right = s.Template.GetFloat("Obstruction/Obstructions/Right/@width");
            width = door + left + right;
            depth = Math.Max(s.Template.GetFloat("Obstruction/Obstructions/Door/@depth"),
                Math.Max(s.Template.GetFloat("Obstruction/Obstructions/Left/@depth"),
                    s.Template.GetFloat("Obstruction/Obstructions/Right/@depth")));
            if (width <= 0) return false;
            // 门洞放行(原版 doorHalfWidth 检查)。
            Trig.SinCosApprox(Fixed.FromFloat(angle), out Fixed sinD, out Fixed cosD);
            float relX = px - ox, relZ = pz - oz;
            float u = relX * cosD.ToFloat() - relZ * sinD.ToFloat();
            if (Math.Abs(u) < door / 2) return false;
        }
        if (angle == 0f)
        {
            return Math.Abs(px - ox) < width / 2 && Math.Abs(pz - oz) < depth / 2;
        }
        Trig.SinCosApprox(Fixed.FromFloat(angle), out Fixed sinA, out Fixed cosA);
        float dx = px - ox, dz = pz - oz;
        float ru = dx * cosA.ToFloat() - dz * sinA.ToFloat();
        float rv = dx * sinA.ToFloat() + dz * cosA.ToFloat();
        return Math.Abs(ru) < width / 2 && Math.Abs(rv) < depth / 2;
    }

    private static float ObstructionRadiusOf(AIEntity ent)
    {
        float w = ent.Template.GetFloat("Obstruction/Static/@width");
        float d = ent.Template.GetFloat("Obstruction/Static/@depth");
        if (w > 0 || d > 0) return Math.Max(w, d) / 2f;
        return ent.Template.GetFloat("Obstruction/Unit/@radius");
    }

    /// <summary>原版 getPathToTarget + setRallyPoint:真实寻路(rally→target),
    /// 集结点改到领土边界(路径上首个非我方领土点的前一点;前一点危险再退一格)。</summary>
    private bool ComputePathToTarget(GameState gameState, AttackManager attackManager)
    {
        if (Target == null || !TargetPos.HasValue || !RallyPoint.HasValue) return false;
        var pathfinder = SimSystem.Pathfinder;
        if (pathfinder == null)
        {
            Path = null;
            return true;   // 无寻路器(测试环境)→ 直推目标
        }
        if (gameState.Accessibility != null)
        {
            ushort a = gameState.Accessibility.GetAccessValue(
                RallyPoint.Value.X.ToFloat(), RallyPoint.Value.Y.ToFloat());
            ushort b = gameState.Accessibility.GetAccessValue(
                TargetPos.Value.X.ToFloat(), TargetPos.Value.Y.ToFloat());
            if (a != b) return false;   // 不同陆(原版走海;我们无运输 → 拒)
        }
        var goal = PathGoal.Point(TargetPos.Value.X, TargetPos.Value.Y);
        var wp = pathfinder.ComputePath(RallyPoint.Value, goal);
        if (wp.IsEmpty) return false;

        // 原版 path 顺序:rally → … → target(反转后首元素是第一步)。
        Path = new List<FixedVector2D>();
        for (int i = wp.Waypoints.Count - 1; i >= 0; i--)
            Path.Add(new FixedVector2D(wp.Waypoints[i].X, wp.Waypoints[i].Z));

        // setRallyPoint:首个非我方领土点的前一点作集结点(危险再退)。
        var territory = SimSystem.Territory;
        if (territory != null && Path.Count > 0)
        {
            for (int i = 0; i < Path.Count; i++)
            {
                if (territory.GetOwner(Path[i].X, Path[i].Y) == gameState.PlayerId)
                    continue;
                if (i == 0) { /* rally 不变 */ }
                else if (i > 1 && attackManager.Hq != null
                    && attackManager.Hq.IsDangerousLocation(gameState, Path[i - 1], 20))
                {
                    RallyPoint = Path[i - 2];
                    Path.RemoveRange(0, i - 2);
                }
                else
                {
                    RallyPoint = Path[i - 1];
                    Path.RemoveRange(0, i - 1);
                }
                break;
            }
        }
        return true;
    }

    // ── 推进(原版 StartAttack + update)──

    /// <summary>原版 StartAttack:姿态 aggressive(packable → standground 此处省略——
    /// 姿态组件未接),全军沿 path[0] 推进。</summary>
    public bool StartAttack(GameState gameState)
    {
        if (Target == null || gameState.GetEntityById(Target.Value) == null)
        {
            // 目标在筹备期被毁 → 重选由调用方(AttackManager)走 UpdatePreparation。
            return false;
        }
        foreach (var id in UnitCollection)
            gameState.Metadata.Set(id, "subrole", WorkerRoles.SubroleWalking);
        State = AttackState.Started;
        IssueAttackCommands(gameState);
        return true;
    }

    /// <summary>推进/战斗更新(原版 update 的 walking 段简化):
    /// 清死单位 → 兵力耗尽 Abort → 敌优比超限撤退 → 目标摧毁换目标 → 完成。</summary>
    public bool Update(GameState gameState, AttackManager attackManager)
    {
        UnitCollection.RemoveWhere(id =>
        {
            var e = gameState.GetEntityById(id);
            return e == null || e.IsDead;
        });
        if (UnitCollection.Count == 0) return false;

        if (ShouldRetreat(gameState))
        {
            Retreat(gameState);
            return false;
        }

        if (Target.HasValue)
        {
            var target = gameState.GetEntityById(Target.Value);
            if (target == null || target.IsDead)
            {
                if (!ChooseTarget(gameState, attackManager))
                {
                    State = AttackState.Completed;
                    return false;
                }
                IssueAttackCommands(gameState);
            }
        }
        else if (!ChooseTarget(gameState, attackManager))
        {
            State = AttackState.Completed;
            return false;
        }
        return true;
    }

    /// <summary>撤退判定(原版 comportment 兵力比评估):目标 60m 内敌兵×1 + 敌防御×3
    /// vs 我方兵力;敌优比 > 阈值(Rush 0.8 / 其余 0.5)即撤。</summary>
    private bool ShouldRetreat(GameState gameState)
    {
        if (!TargetPos.HasValue) return false;
        var tp = TargetPos.Value;
        int enemyStrength = 0;
        foreach (var e in gameState.GetEnemyUnits().Values())
        {
            if (!e.CanAttack || e.Position2D == default) continue;
            if (SquareDist(e.Position2D, tp) <= 60f * 60f) enemyStrength += 1;
        }
        foreach (var s in gameState.GetEnemyStructures().Values())
        {
            if (!s.HasDefensiveFire || s.Position2D == default) continue;
            if (SquareDist(s.Position2D, tp) <= 60f * 60f) enemyStrength += 3;
        }
        if (UnitCollection.Count == 0) return true;
        float threshold = Type == TypeRush ? 0.8f : 0.5f;
        return enemyStrength > UnitCollection.Count / threshold;
    }

    /// <summary>撤退:全军回最近基地,单位进 outOfPlan 回收池(原版 Abort 语义)。</summary>
    private void Retreat(GameState gameState)
    {
        var rally = PickFallbackRally(gameState);
        foreach (var id in UnitCollection)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null) continue;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                (uint)gameState.PlayerId, id, rally.X, rally.Y));
            gameState.Metadata.Remove(id, "plan");
            gameState.Metadata.Set(id, "subrole", WorkerRoles.SubroleIdle);
        }
        UnitCollection.Clear();
    }

    private void IssueAttackCommands(GameState gameState)
    {
        if (!TargetPos.HasValue) return;
        foreach (var id in UnitCollection)
        {
            if (gameState.GetEntityById(id) == null) continue;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.AttackWalk(
                (uint)gameState.PlayerId, id, TargetPos.Value.X, TargetPos.Value.Y));
        }
    }

    private float RallyReachedFraction(GameState gameState)
    {
        if (!RallyPoint.HasValue || UnitCollection.Count == 0) return 0f;
        int reached = 0, alive = 0;
        foreach (var id in UnitCollection)
        {
            var ent = gameState.GetEntityById(id);
            if (ent == null || ent.IsDead) continue;
            alive++;
            if (ent.Position2D == default) continue;
            if (SquareDist(ent.Position2D, RallyPoint.Value) <= 15f * 15f) reached++;
        }
        return alive > 0 ? (float)reached / alive : 0f;
    }

    private static FixedVector2D PickFallbackRally(GameState gameState)
    {
        var anchor = gameState.GetOwnStructures().Values()
            .FirstOrDefault(s => s.HasClass("CivCentre") && s.Position2D != default)
            ?? gameState.GetOwnStructures().Values().FirstOrDefault(s => s.Position2D != default);
        if (anchor != null) return anchor.Position2D;
        var ent = gameState.GetOwnEntities().Values().FirstOrDefault(e => e.Position2D != default);
        return ent?.Position2D ?? FixedVector2D.Zero;
    }

    /// <summary>集结点初始值(原版:rally 起点是最近基地 anchor;chooseTarget 可能改址)。</summary>
    public void SetInitialRallyPoint(GameState gameState)
    {
        RallyPoint = PickFallbackRally(gameState);
    }

    // ── 队列管理(原版 emptyQueues/removeQueues)──

    public void EmptyQueues(QueueManager queues)
    {
        foreach (var qn in new[] { QueueName, QueueChampName, QueueSiegeName })
            queues.GetQueue(qn)?.Plans.Clear();
    }

    public void RemoveQueues(QueueManager queues)
    {
        foreach (var qn in new[] { QueueName, QueueChampName, QueueSiegeName })
            queues.RemoveQueue(qn);
    }

    /// <summary>中止(原版 Abort):单位全部进回收池。</summary>
    public void Abort(GameState gameState, AttackManager attackManager, QueueManager queues)
    {
        foreach (var id in UnitCollection)
        {
            gameState.Metadata.Remove(id, "plan");
            gameState.Metadata.Set(id, "subrole", WorkerRoles.SubroleIdle);
            attackManager.OutOfPlan.Add(id);
        }
        UnitCollection.Clear();
        RemoveQueues(queues);
        State = AttackState.Aborted;
    }

    private static float SquareDist(FixedVector2D a, FixedVector2D b)
    {
        float dx = a.X.ToFloat() - b.X.ToFloat();
        float dz = a.Y.ToFloat() - b.Y.ToFloat();
        return dx * dx + dz * dz;
    }

    /// <summary>UpdatePreparation 的 queues 缓存(addSiegeUnits 的 EmptyQueues 用)。</summary>
    public void WireQueues(QueueManager queues) => _queuesRef = queues;
}
