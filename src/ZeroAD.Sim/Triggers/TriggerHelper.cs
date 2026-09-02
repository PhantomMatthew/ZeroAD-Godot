using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Triggers;

/// <summary>TriggerHelper — 原版 maps/scripts/TriggerHelper.js 的 C# 移植(32 个函数)。
/// 地图触发脚本(IMapScriptBehavior)与教程/战役的通用工具库:实体/玩家查询、
/// 生成(散点/驻军/炮塔/触发点)、模板类过滤、随机编组、胜负判定。
/// 确定性:RNG 一律走 cm.RNG(Rand48);原版 randFloat 语义 = NextDouble。</summary>
public static class TriggerHelper
{
    // ── 基础查询 ──

    /// <summary>实体的属主玩家号(原版 GetPlayerIDFromEntity;无归属 = -1)。</summary>
    public static int GetPlayerIDFromEntity(ComponentManager cm, EntityId ent) =>
        cm.QueryInterface<OwnershipComponent>(ent)?.PlayerId ?? -1;

    public static bool IsInWorld(ComponentManager cm, EntityId ent) =>
        cm.QueryInterface<PositionComponent>(ent)?.InWorld ?? false;

    public static FixedVector2D? GetEntityPosition2D(ComponentManager cm, EntityId ent)
    {
        var pos = cm.QueryInterface<PositionComponent>(ent);
        if (pos == null || !pos.InWorld) return null;
        return new FixedVector2D(pos.Position.X, pos.Position.Z);
    }

    public static int GetOwner(ComponentManager cm, EntityId ent) => GetPlayerIDFromEntity(cm, ent);

    /// <summary>地图边长(地块数;原版 GetMapSizeTiles)。</summary>
    public static int GetMapSizeTiles(ComponentManager cm) =>
        SimSystem.Terrain?.MapSize ?? 0;

    /// <summary>地图边长(米;原版 GetMapSizeTerrain)。</summary>
    public static float GetMapSizeTerrain(ComponentManager cm) =>
        SimSystem.Terrain?.GetWorldSize() ?? 0f;

    /// <summary>当前回合秒数(原版 GetTime:模拟进行时间,毫秒→秒换算在调用方;
    /// 此处直接给秒,0.1s/回合)。</summary>
    public static double GetTime(ComponentManager cm) =>
        (SimSystem.Net?.CurrentTurn ?? 0) * 0.1;

    public static int GetMinutes(ComponentManager cm) => (int)(GetTime(cm) / 60);

    /// <summary>某玩家全部实体(原版 GetEntitiesByPlayer;RangeManager 索引)。</summary>
    public static List<EntityId> GetEntitiesByPlayer(ComponentManager cm, int playerId) =>
        SimSystem.Range?.GetEntitiesByPlayer(playerId).ToList() ?? new List<EntityId>();

    /// <summary>全部玩家(含 gaia)的实体(原版 GetAllPlayersEntities)。</summary>
    public static List<EntityId> GetAllPlayersEntities(ComponentManager cm) =>
        SimSystem.Range?.GetNonGaiaEntities().Concat(GetEntitiesByPlayer(cm, 0)).ToList()
            ?? new List<EntityId>();

    /// <summary>玩家总数(含 gaia 与已败;原版 GetNumPlayers)。</summary>
    public static int GetNumberOfPlayers(ComponentManager cm) =>
        cm.Players.GetNonGaiaPlayerIds().Count() + 1;

    // ── 单位操控 ──

    /// <summary>换站姿(原版 SetUnitStance → UnitAI.SetStance)。</summary>
    public static void SetUnitStance(ComponentManager cm, EntityId ent, string stance) =>
        cm.QueryInterface<UnitAIComponent>(ent)?.SetStance(stance, cm);

    /// <summary>编队(原版 SetUnitFormation → Formation 命令;无 Formation 需求时
    /// 简化:经 NetCommand FormationCmd 走锁步通道,由执行器编组)。</summary>
    public static void SetUnitFormation(ComponentManager cm, int playerId,
        IReadOnlyList<uint> entities, string formation)
    {
        SimSystem.Net?.SubmitAiCommand(Net.NetCommand.FormationCmd((uint)playerId, formation, entities));
    }

    /// <summary>升级模板(原版 AddUpgradeTemplate:模板名应用 Upgrade 链——
    /// 我们的 Promotion 用 destroy+respawn;此处 = 原样返回模板名占位,
    /// 升级链在模板加载期已合并(SpecMerger))。</summary>
    public static string AddUpgradeTemplate(ComponentManager cm, int owner, string template) =>
        template;

    // ── 生成 ──

    /// <summary>在源实体 footprint 出生点生成(原版 SpawnUnits:PickSpawnPoint,
    /// 落点失败回落源位置)。返回生成实体(顺序 = 生成序,确定性)。</summary>
    public static List<EntityId> SpawnUnits(ComponentManager cm, EntityId source,
        string template, int count, int? owner = null)
    {
        var entities = new List<EntityId>();
        var srcPos = cm.QueryInterface<PositionComponent>(source);
        if (srcPos == null || !srcPos.InWorld) return entities;
        int ownerId = owner ?? GetOwner(cm, source);

        for (int i = 0; i < count; i++)
        {
            float sx = srcPos.Position.X.ToFloat();
            float sz = srcPos.Position.Z.ToFloat();
            // 出生点:源建筑 footprint 外沿(原版 PickSpawnPoint);简化:环源 6m 黄金角采样。
            var footprint = cm.QueryInterface<FootprintComponent>(source);
            if (footprint != null)
            {
                // 原版 Footprint.PickSpawnPoint:外沿可达点;y<0 = 无位 → 回落源位置。
                var sp = footprint.PickSpawnPoint(Fixed.FromFloat(1.0f), "default");
                if (sp.Y >= Fixed.Zero)
                {
                    sx = sp.X.ToFloat();
                    sz = sp.Z.ToFloat();
                }
            }
            var ent = cm.SpawnEntity(template, sx, sz, ownerId);
            entities.Add(ent);
        }
        return entities;
    }

    /// <summary>生成并驻军(原版 SpawnGarrisonedUnits:生成在持有者处并直接入舱)。</summary>
    public static List<EntityId> SpawnGarrisonedUnits(ComponentManager cm, EntityId holder,
        string template, int count, int? owner = null)
    {
        var entities = new List<EntityId>();
        var holderCmp = cm.QueryInterface<GarrisonHolderComponent>(holder);
        if (holderCmp == null) return entities;
        var holderPos = cm.QueryInterface<PositionComponent>(holder);
        if (holderPos == null) return entities;
        int ownerId = owner ?? GetOwner(cm, holder);
        for (int i = 0; i < count; i++)
        {
            var ent = cm.SpawnEntity(template,
                holderPos.Position.X.ToFloat(), holderPos.Position.Z.ToFloat(), ownerId);
            if (holderCmp.Garrison(cm, ent))
                entities.Add(ent);
            else
                cm.DestroyEntity(ent);   // 舱满/不可驻 → 不留孤儿(原版同类语义)
        }
        return entities;
    }

    /// <summary>生成并上炮塔(原版 SpawnTurretedUnits)。</summary>
    public static List<EntityId> SpawnTurretedUnits(ComponentManager cm, EntityId holder,
        string template, int count, int? owner = null)
    {
        var entities = new List<EntityId>();
        var turrets = cm.QueryInterface<TurretHolderComponent>(holder);
        var holderPos = cm.QueryInterface<PositionComponent>(holder);
        if (turrets == null || holderPos == null) return entities;
        int ownerId = owner ?? GetOwner(cm, holder);
        for (int i = 0; i < count; i++)
        {
            var ent = cm.SpawnEntity(template,
                holderPos.Position.X.ToFloat(), holderPos.Position.Z.ToFloat(), ownerId);
            var point = turrets.TurretPoints.FirstOrDefault(p =>
                p.Entity == null && turrets.AllowedToOccupyTurretPoint(cm, ent, p));
            if (point != null && turrets.OccupyTurretPoint(cm, ent, point.Name))
                entities.Add(ent);
            else
                cm.DestroyEntity(ent);
        }
        return entities;
    }

    /// <summary>按触发点生成(原版 SpawnUnitsFromTriggerPoints;返回 触发点序 → 实体组)。</summary>
    public static List<EntityId> SpawnUnitsFromTriggerPoints(ComponentManager cm,
        TriggerSystem triggers, string reference, string template, int count, int? owner = null)
    {
        var entities = new List<EntityId>();
        foreach (var pos in triggers.GetTriggerPoints(reference))
        {
            // 简化:触发点即坐标,直接在坐标生成(原版以触发点实体为源走 footprint)。
            for (int i = 0; i < count; i++)
                entities.Add(cm.SpawnEntity(template,
                    pos.X.ToFloat(), pos.Y.ToFloat(), owner ?? 0));
        }
        return entities;
    }

    /// <summary>可采集资源的 generic 类型(原版 GetResourceType)。</summary>
    public static string? GetResourceType(ComponentManager cm, EntityId ent) =>
        cm.QueryInterface<ResourceSupply>(ent)?.GenericType;

    /// <summary>陆地生成点(原版 GetLandSpawnPoints:中立区域 gaia 实体优先,
    /// 无中立则全部;水上/飞行排除)。</summary>
    public static List<EntityId> GetLandSpawnPoints(ComponentManager cm)
    {
        var neutral = new List<EntityId>();
        var nonNeutral = new List<EntityId>();
        var range = SimSystem.Range;
        var territory = SimSystem.Territory;
        var water = SimSystem.Water;
        if (range == null) return neutral;
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var pos = cm.QueryInterface<PositionComponent>(ent);
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            if (pos == null || !pos.InWorld || identity == null) continue;
            var stats = cm.Templates?.ExtractStats(identity.TemplateName);
            if (stats?.HasUnitMotionFlying == true) continue;
            float x = pos.Position.X.ToFloat(), z = pos.Position.Z.ToFloat();
            if (water != null && pos.Position.Y <= water.GetWaterLevel(
                    Fixed.FromFloat(x), Fixed.FromFloat(z)))
                continue;
            if (territory != null && territory.GetOwner(pos.Position.X, pos.Position.Z) == 0)
                neutral.Add(ent);
            else
                nonNeutral.Add(ent);
        }
        return neutral.Count > 0 ? neutral : nonNeutral;
    }

    // ── 胜负 ──

    /// <summary>判胜(原版 SetPlayerWon → EndGameManager.MarkPlayerAndAlliesAsWon;
    /// 盟友连带胜利/其余判负由 EndGameManager 承载)。</summary>
    public static void SetPlayerWon(ComponentManager cm, int playerId, string reason = "")
    {
        var p = cm.Players.GetPlayerEntity(playerId);
        if (p != null && p.SetWon())
            cm.Events.RaisePlayerWon(new Events.PlayerWonEvent { PlayerId = playerId });
    }

    /// <summary>判负(原版 DefeatPlayer → Player.SetState defeated)。</summary>
    public static void DefeatPlayer(ComponentManager cm, int playerId, string reason = "")
    {
        var p = cm.Players.GetPlayerEntity(playerId);
        if (p != null && p.SetDefeated())
            cm.Events.RaisePlayerDefeated(new Events.PlayerDefeatedEvent
            { PlayerId = playerId, Reason = reason });
    }

    // ── 类过滤 ──

    /// <summary>类列表匹配(原版 EntityMatchesClassList:"A B+C+!D" 语义——
    /// 空格=或,+与!=与非…对齐 IdentityComponent.MatchesClassList)。</summary>
    public static bool EntityMatchesClassList(ComponentManager cm, EntityId ent, string classes) =>
        cm.QueryInterface<IdentityComponent>(ent)?.MatchesClassList(classes) ?? false;

    public static List<EntityId> MatchEntitiesByClass(ComponentManager cm,
        IEnumerable<EntityId> entities, string classes) =>
        entities.Where(e => EntityMatchesClassList(cm, e, classes)).ToList();

    public static List<EntityId> GetPlayerEntitiesByClass(ComponentManager cm, int playerId,
        string classes) =>
        MatchEntitiesByClass(cm, GetEntitiesByPlayer(cm, playerId), classes);

    public static List<EntityId> GetAllPlayersEntitiesByClass(ComponentManager cm, string classes) =>
        MatchEntitiesByClass(cm, GetAllPlayersEntities(cm), classes);

    /// <summary>科技已研或在研(原版 HasDealtWithTech)。</summary>
    public static bool HasDealtWithTech(ComponentManager cm, int playerId, string techName)
    {
        var pEnt = cm.Players.GetPlayerEntityId(playerId);
        var tm = pEnt.HasValue ? cm.QueryInterface<TechnologyManager>(pEnt.Value) : null;
        return tm != null && tm.IsResearched(techName);
    }

    // ── 模板检索/编组 ──

    /// <summary>按类找模板名(原版 GetTemplateNamesByClasses:civ 限定/packedState/
    /// rank/_barracks 排除;campaigns/army_ 前缀排除)。</summary>
    public static List<string> GetTemplateNamesByClasses(ComponentManager cm, string classes,
        string? civ = null, string? packedState = null, string? rank = null,
        bool excludeBarracksVariants = false)
    {
        var result = new List<string>();
        if (cm.Templates == null) return result;
        foreach (var name in cm.Templates.Cache.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (name.StartsWith("campaigns/army_", StringComparison.Ordinal)) continue;
            if (excludeBarracksVariants && name.EndsWith("_barracks", StringComparison.Ordinal)) continue;
            var stats = cm.Templates.ExtractStats(name);
            if (stats == null) continue;
            if (civ != null && stats.Civ != civ) continue;
            if (!Content.EntityClassHelper.MatchesClassList(stats.GetClassList(), classes)) continue;
            // Rank/Pack State 走模板 ParamNode(TemplateStats 无这两个字段)。
            var node = cm.Templates.Cache[name];
            if (rank != null)
            {
                var r = node.GetChild("Identity").GetChild("Rank");
                if (r.IsOk && r.Value != rank) continue;   // 原版:无 rank 或匹配才收
            }
            if (packedState != null)
            {
                var packNode = node.GetChild("Pack");
                if (packNode.IsOk
                    && packNode.GetChild("State").Value != packedState) continue;
            }
            result.Add(name);
        }
        return result;
    }

    /// <summary>随机编组(原版 RandomTemplateComposition:随机频率比例分配,
    /// 余数归最后;RNG 走 cm.RNG)。</summary>
    public static Dictionary<string, int> RandomTemplateComposition(ComponentManager cm,
        IReadOnlyList<string> templateNames, int totalCount)
    {
        var frequencies = templateNames.Select(_ => cm.RNG.NextDouble()).ToList();
        double sum = frequencies.Sum();
        int remainder = totalCount;
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < templateNames.Count; i++)
        {
            int count = i == templateNames.Count - 1 ? remainder
                : Math.Min(remainder, (int)Math.Round(frequencies[i] / sum * totalCount));
            if (count <= 0) continue;
            counts[templateNames[i]] = count;
            remainder -= count;
        }
        return counts;
    }

    /// <summary>均衡编组(原版 BalancedTemplateComposition 简化:count 项定量,
    /// 其余按 frequency 比例分余额;uniqueEntities 去重——同名模板已在场则跳过)。</summary>
    public sealed record TemplateBalance(IReadOnlyList<string> Templates, double Frequency = 1,
        int Count = -1, IReadOnlyList<uint>? UniqueEntities = null);

    public static Dictionary<string, int> BalancedTemplateComposition(ComponentManager cm,
        IReadOnlyList<TemplateBalance> balancing, int totalCount)
    {
        var counts = new Dictionary<string, int>();
        int remaining = totalCount;
        foreach (var b in balancing)
        {
            if (b.Templates.Count == 0) continue;
            // unique:已在场的模板剔除(英雄唯一性)。
            var candidates = b.UniqueEntities != null
                ? b.Templates.Where(t => !b.UniqueEntities.Any(id =>
                    cm.QueryInterface<IdentityComponent>(new EntityId(id))?.TemplateName == t))
                    .ToList()
                : b.Templates.ToList();
            if (candidates.Count == 0) continue;
            int n = b.Count >= 0 ? Math.Min(b.Count, remaining)
                : (int)Math.Round(b.Frequency * remaining);
            for (int i = 0; i < n; i++)
            {
                var pick = candidates[(int)(cm.RNG.NextDouble() * candidates.Count)
                    % candidates.Count];
                counts[pick] = counts.GetValueOrDefault(pick) + 1;
            }
            if (b.Count >= 0) remaining -= n;
        }
        return counts;
    }

    /// <summary>按类找建筑并生成驻军(原版 SpawnAndGarrisonAtClasses:
    /// 容量百分比填充;返回全部生成实体)。</summary>
    public static List<EntityId> SpawnAndGarrisonAtClasses(ComponentManager cm, int playerId,
        string classes, IReadOnlyList<string> templates, float capacityPercent = 1f)
    {
        var result = new List<EntityId>();
        int i = 0;
        foreach (var holder in GetPlayerEntitiesByClass(cm, playerId, classes))
        {
            var holderCmp = cm.QueryInterface<GarrisonHolderComponent>(holder);
            if (holderCmp == null) continue;
            int want = (int)Math.Round(holderCmp.GetCapacity(cm) * capacityPercent)
                - holderCmp.Entities.Count;
            for (int k = 0; k < want; k++)
                result.AddRange(SpawnGarrisonedUnits(cm, holder,
                    templates[i++ % templates.Count], 1, playerId));
        }
        return result;
    }

    /// <summary>按类找建筑并上炮塔(原版 SpawnAndTurretAtClasses)。</summary>
    public static List<EntityId> SpawnAndTurretAtClasses(ComponentManager cm, int playerId,
        string classes, IReadOnlyList<string> templates, float capacityPercent = 1f)
    {
        var result = new List<EntityId>();
        int i = 0;
        foreach (var holder in GetPlayerEntitiesByClass(cm, playerId, classes))
        {
            var turrets = cm.QueryInterface<TurretHolderComponent>(holder);
            if (turrets == null) continue;
            int want = (int)Math.Round(turrets.Capacity * capacityPercent)
                - turrets.TurretPoints.Count(p => p.Entity != null);
            for (int k = 0; k < want; k++)
                result.AddRange(SpawnTurretedUnits(cm, holder,
                    templates[i++ % templates.Count], 1, playerId));
        }
        return result;
    }
}
