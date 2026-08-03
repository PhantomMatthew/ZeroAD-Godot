using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>建造计划（原版 petra/queueplanBuilding.js，956 行）。
/// 负责选址 + 下达建造命令。findGoodPosition 是最复杂的方法（~800 行）：
///   1. createObstructionMap（障碍图，过滤不可建造区域）
///   2. territory 过滤（只在我方/盟友/中立领土）
///   3. minDistance 排除（离同类建筑太近）
///   4. findBestTile（从障碍图找最佳位置）
///   5. shore 特殊处理（码头）
///
/// 骨架版——简化选址（CC 附近 + 随机偏移），完整版需 createObstructionMap
/// 和 territory map 的完整实现。</summary>
public sealed class ConstructionPlan : QueuePlan
{
    private FixedVector2D _position;

    public ConstructionPlan(GameState gameState, string type, Dictionary<string, object>? metadata = null)
    {
        Type = gameState.ApplyCiv(type);
        Category = "building";
        Number = 1;
        Metadata = metadata ?? new();
        var tmpl = gameState.GetTemplate(Type);
        if (tmpl != null)
            Cost = new ResourcesManager(tmpl.CostWood, tmpl.CostFood, tmpl.CostStone, tmpl.CostMetal);
        _position = default;
    }

    public override bool IsInvalid(GameState gameState)
        => gameState.GetTemplate(Type) == null;

    public override bool IsGo(GameState gameState) => true;

    public override bool CanStart(GameState gameState)
    {
        // 需要 builder + 可建造位置
        var builders = gameState.FindBuilder(Type);
        return builders.HasEntities();
    }

    /// <summary>启动建造（原版 start，40-109 行）。</summary>
    public override void Start(GameState gameState)
    {
        // 1. 找 builder
        var builders = gameState.FindBuilder(Type);
        if (!builders.HasEntities()) return;
        var builder = builders.Values().First();
        if (builder == null) return;

        // 2. 选址
        var pos = FindGoodPosition(gameState);
        if (pos == null) return;

        // 3. 设 metadata（base + access）
        if (!Metadata.ContainsKey("base"))
            Metadata["base"] = pos.Base;
        Metadata["access"] = pos.Access;

        // 4. 下达 Build 命令
        // TODO: 通过 SubmitAiCommand 下达 Build 命令
        // 简化版：记录位置，完整版需 NetCommand.Build + builderId + x/z
        _position = new FixedVector2D(pos.X, pos.Z);
    }

    /// <summary>选址（原版 findGoodPosition，~800 行 → 简化版 ~40 行）。
    /// 简化版：在最近的 CC 附近 + 随机偏移找位置。
    /// 完整版需：createObstructionMap + territory 过滤 + minDistance + findBestTile。</summary>
    private BuildPosition? FindGoodPosition(GameState gameState)
    {
        // 找最近的 CC 作参考点
        var ccs = gameState.GetOwnStructures().Filter(e => e.HasClass("CivCentre"));
        AIEntity? refPoint;
        if (ccs.HasEntities())
            refPoint = ccs.Values().First();
        else
        {
            // 无 CC → 用第一个建筑
            var bldgs = gameState.GetOwnStructures();
            refPoint = bldgs.HasEntities() ? bldgs.Values().First() : null;
        }
        if (refPoint == null) return null;

        // 简化：CC 附近 15-30 单位随机位置
        var rng = gameState.Cm.RNG;
        float angle = (float)(rng.NextDouble() * Math.PI * 2);
        float dist = 15f + (float)(rng.NextDouble() * 15);
        float x = refPoint.Position2D.X.ToFloat() + dist * MathF.Cos(angle);
        float z = refPoint.Position2D.Y.ToFloat() + dist * MathF.Sin(angle);

        // base = 第一个 base 的 ID（简化）
        int baseId = 1;
        ushort access = EntityExtend.GetLandAccess(gameState, refPoint);

        return new BuildPosition
        {
            X = Fixed.FromFloat(x),
            Z = Fixed.FromFloat(z),
            Angle = angle,
            Base = baseId,
            Access = access,
        };
    }

    private sealed class BuildPosition
    {
        public Fixed X, Z;
        public float Angle;
        public int Base;
        public ushort Access;
    }
}
