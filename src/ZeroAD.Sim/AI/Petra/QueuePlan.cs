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
        // 经 AI 本地通道发训练命令(与玩家 Train 命令同路径同延迟;
        // 原版 queueplanTraining.start 的 PostCommand 等价)。
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Train(
            (uint)gameState.PlayerId, _trainers[0], Type, Number));
    }
}

/// <summary>研究计划（原版 petra/queueplanResearch.js）。</summary>
public sealed class ResearchPlan : QueuePlan
{
    public ResearchPlan(GameState gameState, string type, Dictionary<string, object>? metadata = null)
    {
        // {civ} 展开后再归一(phase 无特制文件文明 → *_generic,gaul 等)。
        Type = gameState.ResolveTechName(gameState.ApplyCiv(type));
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
        // 经 AI 本地通道发研究命令(与玩家 Research 同路径;原版 ResearchPlan.start
        // 的 PostCommand 等价)。取首个研究建筑。
        uint researcher = researchers.ToIdArray()[0];
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Research(
            (uint)gameState.PlayerId, researcher, Type));
    }
}
