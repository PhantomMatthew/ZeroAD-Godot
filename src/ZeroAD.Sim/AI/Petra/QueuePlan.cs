using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>队列计划基类（原版 petra/queueplan.js）。</summary>
public abstract class QueuePlan
{
    public string Type = "";        // 解析后的模板名（{civ} 已替换）
    public string Category = "";    // "unit"/"building"/"technology"
    public int Number = 1;
    public ResourcesManager Cost = new();
    public Dictionary<string, object> Metadata = new();

    public abstract bool IsInvalid(GameState gameState);
    public virtual bool IsGo(GameState gameState) => true;
    public virtual bool CanStart(GameState gameState) => false;
    public abstract void Start(GameState gameState);

    public ResourcesManager GetCost()
    {
        var c = new ResourcesManager(Cost.Wood, Cost.Food, Cost.Stone, Cost.Metal);
        if (Number != 1)
        {
            c.Wood *= Number; c.Food *= Number; c.Stone *= Number; c.Metal *= Number;
        }
        return c;
    }

    public void AddItem(int amount = 1) => Number += amount;
}

/// <summary>训练计划（原版 petra/queueplanTraining.js）。</summary>
public sealed class TrainingPlan : QueuePlan
{
    private List<uint> _trainers = new();
    public int MaxMerge = 5;

    public TrainingPlan(GameState gameState, string type, Dictionary<string, object>? metadata = null, int number = 1, int maxMerge = 5)
    {
        Type = gameState.ApplyCiv(type);
        Category = "unit";
        Number = number;
        MaxMerge = maxMerge;
        Metadata = metadata ?? new();
        var tmpl = gameState.GetTemplate(Type);
        if (tmpl != null)
            Cost = new ResourcesManager(tmpl.CostWood, tmpl.CostFood, tmpl.CostStone, tmpl.CostMetal);
    }

    public override bool IsInvalid(GameState gameState)
        => gameState.GetTemplate(Type) == null;

    public override bool CanStart(GameState gameState)
    {
        _trainers = GetBestTrainers(gameState);
        return _trainers.Count > 0;
    }

    private List<uint> GetBestTrainers(GameState gameState)
    {
        var trainers = gameState.FindTrainers(Type);
        if (!trainers.HasEntities()) return new();
        // 简化：取所有可用训练设施（原版按 costSum 排序取最小，此处简化）
        return trainers.ToIdArray().ToList();
    }

    public override void Start(GameState gameState)
    {
        if (_trainers.Count == 0) return;
        // 通过 SubmitAiCommand 发训练命令
        var net = gameState.Cm;  // ComponentManager 持有 NetTurnManager 引用
        // AI 命令走 SubmitAiCommand（Phase 0 的通道）
        // 简化版：选第一个训练设施，发 Train 命令
        // 完整版需要 NetTurnManager 引用——经 gameState 或 AIComponent 传入
        // TODO: Phase 2 后续接入 NetTurnManager.SubmitAiCommand
    }
}

/// <summary>研究计划（原版 petra/queueplanResearch.js）。</summary>
public sealed class ResearchPlan : QueuePlan
{
    public ResearchPlan(GameState gameState, string type, Dictionary<string, object>? metadata = null)
    {
        Type = gameState.ApplyCiv(type);
        Category = "technology";
        Number = 1;
        Metadata = metadata ?? new();
        // 从科技目录取 cost
        if (gameState.TechCatalog.Technologies.TryGetValue(Type, out var def))
            Cost = new ResourcesManager(def.Wood, def.Food, def.Stone, def.Metal);
    }

    public override bool IsInvalid(GameState gameState)
        => !gameState.TechCatalog.Technologies.ContainsKey(Type)
        && !gameState.TechCatalog.Pairs.ContainsKey(Type);

    public override bool CanStart(GameState gameState)
    {
        var researchers = gameState.FindResearchers(Type);
        return researchers.HasEntities();
    }

    public override void Start(GameState gameState)
    {
        var researchers = gameState.FindResearchers(Type);
        if (!researchers.HasEntities()) return;
        // TODO: SubmitAiCommand(Research)
    }
}
