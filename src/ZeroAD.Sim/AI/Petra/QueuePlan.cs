using System;
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

    // ── 序列化(原版 queueplan*.js Serialize;存档后 AI 计划不丢)──
    // 种类标签:0=Training 1=Construction 2=Research(写序与 Deserialize 逐位一致)。
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("kind", this is TrainingPlan ? 0 : this is ConstructionPlan ? 1 : 2);
        s.StringASCII("type", Type);
        s.NumberI32("number", Number);
        if (this is TrainingPlan tp) s.NumberI32("maxMerge", tp.MaxMerge);
        if (this is ConstructionPlan cp)
        {
            var pos = cp.Position;
            s.Bool("hasPos", pos.HasValue);
            if (pos.HasValue)
            {
                s.NumberFixed("px", pos.Value.X);
                s.NumberFixed("pz", pos.Value.Y);
            }
        }
        s.NumberI32("metaCount", Metadata.Count);
        foreach (var kv in Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            s.StringASCII("key", kv.Key);
            // 类型标签:0=string 1=int 2=FixedVector2D(生产者收敛:plan/special/
            // role=string,base/sea=int,position=FixedVector2D)。
            switch (kv.Value)
            {
                case int i:
                    s.NumberI32("tag", 1);
                    s.NumberI32("ival", i);
                    break;
                case Maths.FixedVector2D v:
                    s.NumberI32("tag", 2);
                    s.NumberFixed("vx", v.X);
                    s.NumberFixed("vz", v.Y);
                    break;
                default:
                    s.NumberI32("tag", 0);
                    s.StringASCII("sval", kv.Value?.ToString() ?? "");
                    break;
            }
        }
    }

    /// <summary>重建(种类标签分发;ConstructionPlan 的 Cost 由 gameState 模板重算)。</summary>
    public static QueuePlan Deserialize(Serialization.IDeserializer d, GameState gameState)
    {
        int kind = d.NumberI32("kind");
        string type = d.StringASCII("type");
        int number = d.NumberI32("number");
        QueuePlan plan;
        if (kind == 0)
        {
            int maxMerge = d.NumberI32("maxMerge");
            plan = new TrainingPlan(gameState, type, number: number, maxMerge: maxMerge);
        }
        else if (kind == 1)
        {
            var cp = new ConstructionPlan(gameState, type);
            if (d.Bool("hasPos"))
                cp.Position = new Maths.FixedVector2D(d.NumberFixed("px"), d.NumberFixed("pz"));
            plan = cp;
        }
        else
            plan = new ResearchPlan(gameState, type);
        int metaCount = d.NumberI32("metaCount");
        for (int i = 0; i < metaCount; i++)
        {
            string key = d.StringASCII("key");
            int tag = d.NumberI32("tag");
            plan.Metadata[key] = tag switch
            {
                1 => d.NumberI32("ival"),
                2 => new Maths.FixedVector2D(d.NumberFixed("vx"), d.NumberFixed("vz")),
                _ => (object)d.StringASCII("sval"),
            };
        }
        return plan;
    }

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
