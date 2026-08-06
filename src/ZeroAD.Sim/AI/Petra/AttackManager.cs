using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>进攻管理器（原版 petra/attackManager.js，870 行）。
/// 管理多个 AttackPlan 的生命周期：创建/取消/调度。
/// 骨架版——update 结构 + attackPlan 列表管理。</summary>
public sealed class AttackManager
{
    private readonly PetraConfig _config;
    public readonly List<AttackPlan> UpcomingAttacks = new();  // 筹备中的进攻
    public readonly List<AttackPlan> StartedAttacks = new();   // 进行中的进攻

    public AttackManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 attackManager.update）。</summary>
    public void Update(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        // 更新筹备中的进攻
        foreach (var plan in UpcomingAttacks.ToList())
        {
            plan.Update(gameState, queues);
            if (plan.State == AttackPlan.AttackState.Started)
            {
                StartedAttacks.Add(plan);
                UpcomingAttacks.Remove(plan);
            }
            else if (plan.State == AttackPlan.AttackState.Aborted)
                UpcomingAttacks.Remove(plan);
        }

        // 更新进行中的进攻
        foreach (var plan in StartedAttacks.ToList())
        {
            plan.Update(gameState, queues);
            if (plan.State == AttackPlan.AttackState.Completed || plan.State == AttackPlan.AttackState.Aborted)
                StartedAttacks.Remove(plan);
        }

        // 检查是否应发起新进攻
        CheckNewAttacks(gameState);
    }

    /// <summary>检查是否应发起新进攻（原版 attackManager 的轮换逻辑）。
    /// 简化版：兵力达阈值时创建 Rush（快攻）计划。</summary>
    private void CheckNewAttacks(GameState gameState)
    {
        // 检查已有进攻数（避免同时筹备太多）
        if (UpcomingAttacks.Count + StartedAttacks.Count >= 2) return;

        // 兵力检查
        var soldiers = gameState.GetOwnUnits().Filter(e => e.CanAttack && !e.HasClass("Support"));
        if (!soldiers.HasEntities() || soldiers.Length < 5) return;

        // 创建 Rush（简化的进攻类型）
        var plan = new AttackPlan(gameState, "Rush", _config);
        UpcomingAttacks.Add(plan);
    }
}

/// <summary>进攻计划（原版 petra/attackPlan.js，2308 行）。
/// Petra 最大单体文件。管理进攻全流程：组军 → 集结 → 选目标 → 推进 → 撤退/重整。
/// 骨架版——状态机 + 核心方法签名。完整版需：addUnit/chooseTarget/comportment/</summary>
public sealed class AttackPlan
{
    public readonly string Type;  // Rush/Raid/Attack/HugeAttack/Siege
    public readonly PetraConfig Config;

    public enum AttackState { Unstarted, Completory, Started, Completed, Aborted }
    public AttackState State { get; private set; }

    // 参与单位
    public readonly HashSet<uint> UnitCollection = new();
    // 目标
    public uint? Target;  // 目标 entity id
    public FixedVector2D? RallyPoint;  // 集结点
    public FixedVector2D? TargetPos;  // 目标位置

    // 进攻参数
    public int MaxForces;  // 最大兵力
    public bool Overran;

    public AttackPlan(GameState gameState, string type, PetraConfig config)
    {
        Type = type;
        Config = config;
        State = AttackState.Unstarted;
        MaxForces = type switch
        {
            "Rush" => 15,
            "Raid" => 8,
            "Attack" => 40,
            "HugeAttack" => 80,
            _ => 20,
        };
    }

    /// <summary>更新（原版 attackPlan.update，~400 行）。
    /// 状态机：Unstarted → Completory（组军）→ Started（推进）→ Completed/Aborted。
    /// 骨架版——状态转换逻辑。</summary>
    public void Update(GameState gameState, QueueManager queues)
    {
        switch (State)
        {
            case AttackState.Unstarted:
                // 开始组军
                State = AttackState.Completory;
                goto case AttackState.Completory;

            case AttackState.Completory:
                // 检查兵力是否足够
                if (UnitCollection.Count >= MaxForces || Overran)
                {
                    ChooseTarget(gameState);
                    State = AttackState.Started;
                    IssueAttackCommands(gameState);   // 首次推进即下攻击移动
                }
                else
                {
                    RecruitUnits(gameState, queues);
                }
                break;

            case AttackState.Started:
                // 推进/战斗
                UpdateStarted(gameState);
                break;
        }
    }

    /// <summary>组军（原版 recruitUnits）。
    /// 简化版：加入所有 idle 兵力。</summary>
    private void RecruitUnits(GameState gameState, QueueManager queues)
    {
        var soldiers = gameState.GetOwnUnits().Filter(e => e.CanAttack && !e.HasClass("Support") && e.IsIdle);
        foreach (var s in soldiers.Values())
            if (UnitCollection.Count < MaxForces) UnitCollection.Add(s.Id);
    }

    /// <summary>选目标（原版 chooseTarget，~300 行）。
    /// 简化版：最近的敌方建筑。</summary>
    private void ChooseTarget(GameState gameState)
    {
        var enemies = gameState.GetEnemyStructures().Values().ToList();
        if (enemies.Count == 0)
        {
            // 无建筑 → 打单位
            var enemyUnits = gameState.GetEnemyUnits().Values().ToList();
            if (enemyUnits.Count > 0)
                Target = enemyUnits[0].Id;
            return;
        }
        // 选最近我方基地的敌方建筑
        // 简化：取第一个
        Target = enemies[0].Id;
        TargetPos = enemies[0].Position2D;
    }

    /// <summary>推进/战斗更新（原版 ~600 行）。
    /// 简化版：无目标 → 完成；兵力耗尽 → 中止。</summary>
    private void UpdateStarted(GameState gameState)
    {
        // 清理死单位
        UnitCollection.RemoveWhere(id => gameState.GetEntityById(id) == null);

        if (UnitCollection.Count == 0)
        {
            State = AttackState.Aborted;
            return;
        }
        if (Target.HasValue)
        {
            var target = gameState.GetEntityById(Target.Value);
            if (target == null || target.IsDead)
            {
                // 目标摧毁 → 选新目标或完成
                ChooseTarget(gameState);
                if (!Target.HasValue) State = AttackState.Completed;
                else IssueAttackCommands(gameState);   // 换目标后重下推进命令
            }
        }
    }

    /// <summary>对参与单位下攻击移动(原版 attackPlan 的 comportment 简化版:
    /// 全军 attack-walk 到目标位置——沿途自动交战、打完继续推进,
    /// UnitAI WalkAndFight 状态机已承载该语义)。目标无位置(TargetPos 缺失)不下。</summary>
    private void IssueAttackCommands(GameState gameState)
    {
        if (!TargetPos.HasValue) return;
        foreach (var id in UnitCollection)
        {
            if (gameState.GetEntityById(id) == null) continue;   // 死单位跳过
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.AttackWalk(
                (uint)gameState.PlayerId, id,
                ZeroAD.Sim.Maths.Fixed.FromFloat(TargetPos.Value.X.ToFloat()),
                ZeroAD.Sim.Maths.Fixed.FromFloat(TargetPos.Value.Y.ToFloat())));
        }
    }

    /// <summary>释放单位（进攻取消/完成时）。</summary>
    public void ReleaseAll(GameState gameState)
    {
        foreach (var id in UnitCollection)
        {
            gameState.Metadata.Remove(id, "plan");
            gameState.Metadata.Set(id, "subrole", WorkerRoles.SubroleIdle);
        }
        UnitCollection.Clear();
    }
}
