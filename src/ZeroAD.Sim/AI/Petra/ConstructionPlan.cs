using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
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

        // 2. 选址(metadata "position" 显式指定优先——码头等岸线建筑由
        // NavalManager 经 Accessibility.TryFindShoreline 预算;否则默认选址)。
        BuildPosition? pos = null;
        if (Metadata.TryGetValue("position", out var pobj)
            && pobj is FixedVector2D explicitPos)
        {
            pos = new BuildPosition
            {
                X = explicitPos.X,
                Z = explicitPos.Y,
                Angle = DefaultPlacementAngle,
                Base = 1,
                Access = 0,
            };
        }
        // 新基地选址(metadata base=-1 + resource:原版 findEconomicCCLocation
        // 语义——离最近资源评分,非"近 CC"默认选址)。
        if (pos == null
            && Metadata.TryGetValue("base", out var bobj)
            && bobj is int baseId && baseId == -1)
        {
            string resource = Metadata.TryGetValue("resource", out var robj)
                ? robj as string ?? "wood"
                : "wood";
            pos = FindEconomicCCLocation(gameState, resource);
        }
        pos ??= FindGoodPosition(gameState);
        if (pos == null) return;

        // 3. 设 metadata（base + access）
        if (!Metadata.ContainsKey("base"))
            Metadata["base"] = pos.Base;
        Metadata["access"] = pos.Access;

        // 4. 下达 Build 命令(经 AI 本地通道,与玩家建造同路径同延迟;
        // 原版 queueplanBuilding.start 的 PostCommand 等价)。朝向用 GUI 默认 3π/4
        // (原版 placement.js:6 PlacementSupport.DEFAULT_ANGLE — AI 不旋转故取默认)。
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Build(
            (uint)gameState.PlayerId, builder.Id, Type, pos.X, pos.Z,
            Maths.Fixed.FromFloat(pos.Angle)));
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

        // 选址评分(原版 headquarters findConstructionLocation 的简化):
        // 多候选角/距采样,Rand48 派生(确定);过滤不可行位置,选综合最优
        // (原版按资源邻近/坡度/无障碍评分,这里用土地性 + 离 CC 适中距)。
        var rng = gameState.Cm.RNG;
        const int candidates = 16;
        BuildPosition? best = null;
        float bestScore = float.MinValue;
        ushort access = EntityExtend.GetLandAccess(gameState, refPoint);

        for (int i = 0; i < candidates; i++)
        {
            float angle = (float)(rng.NextDouble() * Math.PI * 2);
            float dist = 15f + (float)(rng.NextDouble() * 15);
            Trig.SinCosApprox(Fixed.FromFloat(angle), out Fixed planSin, out Fixed planCos);
            float x = refPoint.Position2D.X.ToFloat() + dist * planCos.ToFloat();
            float z = refPoint.Position2D.Y.ToFloat() + dist * planSin.ToFloat();

            // 土地性过滤(原版 CheckBuildingPlacement 的地形半边;水域/悬崖拒)。
            var fx = Fixed.FromFloat(x);
            var fz = Fixed.FromFloat(z);
            var terrain = SimSystem.Terrain;
            if (terrain != null && !terrain.IsLand(fx, fz)) continue;

            // 评分:离 CC 20-25m 最优(原版开阔地偏好;太近挤、太远散)。
            float distScore = dist >= 20f && dist <= 25f ? 100f : -MathF.Abs(dist - 22f) * 2f;
            if (distScore > bestScore)
            {
                bestScore = distScore;
                best = new BuildPosition
                {
                    X = fx,
                    Z = fz,
                    Angle = DefaultPlacementAngle,
                    Base = 1,
                    Access = access,
                };
            }
        }
        return best;
    }

    /// <summary>新基地选址(原版 headquarters.findEconomicCCLocation 核心语义的
    /// 简化):候选采样评分——近资源(原版"近资源优先扩张")+ 离最近 CC 适中距
    /// (原版"不太近不太远")+ 土地过滤。基地锚 = 最近同类资源(原版 resource 驱动)。</summary>
    private BuildPosition? FindEconomicCCLocation(GameState gameState, string resource)
    {
        // 资源锚:最近同类资源(原版"靠近资源扩张新基地"语义)。
        var ccs = gameState.GetOwnStructures().Filter(e => e.HasClass("CivCentre"));
        var anchorCC = ccs.HasEntities() ? ccs.Values().First() : null;
        var supplies = gameState.GetResourceSupplies(resource);
        AIEntity? refPoint = anchorCC;
        if (supplies.HasEntities() && anchorCC != null)
        {
            // 选距 CC 最近的资源(原版"CC 附近资源")。
            var nearest = supplies.FilterNearest(anchorCC.Position2D, 1);
            if (nearest.HasEntities())
                refPoint = nearest.Values().First();
        }
        if (refPoint == null)
            refPoint = anchorCC ?? gameState.GetOwnStructures().Values().FirstOrDefault();
        if (refPoint == null) return null;

        var rng = gameState.Cm.RNG;
        const int candidates = 16;
        BuildPosition? best = null;
        float bestScore = float.MinValue;
        ushort access = EntityExtend.GetLandAccess(gameState, refPoint);

        for (int i = 0; i < candidates; i++)
        {
            float angle = (float)(rng.NextDouble() * Math.PI * 2);
            float dist = 25f + (float)(rng.NextDouble() * 25);
            Trig.SinCosApprox(Fixed.FromFloat(angle), out Fixed sinA, out Fixed cosA);
            float x = refPoint.Position2D.X.ToFloat() + dist * cosA.ToFloat();
            float z = refPoint.Position2D.Y.ToFloat() + dist * sinA.ToFloat();

            var fx = Fixed.FromFloat(x);
            var fz = Fixed.FromFloat(z);
            var terrain = SimSystem.Terrain;
            if (terrain != null && !terrain.IsLand(fx, fz)) continue;

            // 评分:离 CC 60-120m 最优(原版扩张偏好;太近挤、太远散);
            // 距资源锚 <30m 加分(原版近资源)。
            float score = 0f;
            if (anchorCC != null)
            {
                float ccDx = x - anchorCC.Position2D.X.ToFloat();
                float ccDz = z - anchorCC.Position2D.Y.ToFloat();
                float ccDist = MathF.Sqrt(ccDx * ccDx + ccDz * ccDz);
                if (ccDist >= 60f && ccDist <= 120f) score += 100f;
                else score -= MathF.Abs(ccDist - 90f) * 0.5f;
            }
            float resDx = x - refPoint.Position2D.X.ToFloat();
            float resDz = z - refPoint.Position2D.Y.ToFloat();
            float resDist = MathF.Sqrt(resDx * resDx + resDz * resDz);
            if (resDist < 30f) score += 50f;

            if (score > bestScore)
            {
                bestScore = score;
                best = new BuildPosition
                {
                    X = fx,
                    Z = fz,
                    Angle = DefaultPlacementAngle,
                    Base = -1,
                    Access = access,
                };
            }
        }
        return best;
    }

    /// <summary>原版 GUI 默认放置朝向 placement.js:6(3π/4 = 135°)。AI 不旋转故取此值。</summary>
    private const float DefaultPlacementAngle = MathF.PI * 3f / 4f;

    private sealed class BuildPosition
    {
        public Fixed X, Z;
        public float Angle;
        public int Base;
        public ushort Access;
    }
}
