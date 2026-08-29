using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Triggers;

/// <summary>polar_sea_triggers.js 移植:周期性狼群袭击。
/// 开局禁 4 个木材采集科技;首波 5 分钟后,每 2~4 分钟在每个 trigger_point_A
/// 生成 1~3 头北极狼(gaia),攻击 200m 内最近的 ≤3 个 Organic+!Domestic 目标
/// (不足则全图取最近)。RNG 全走 cm.RNG(锁步逐端一致)。</summary>
public sealed class PolarSeaScript : IMapScriptBehavior
{
    private const string AttackerTemplate = "gaia/fauna_wolf_arctic_violent";
    private const int MinWaveSize = 1;
    private const int MaxWaveSize = 3;
    private const float FirstWaveSec = 5 * 60;
    private const float MinWaveSec = 2 * 60;
    private const float MaxWaveSec = 4 * 60;
    private const int TargetCount = 3;
    private const float TargetSearchRadius = 200f;

    private static readonly string[] DisabledTechs =
    {
        "gather_lumbering_ironaxes", "gather_lumbering_sharpaxes",
        "gather_lumbering_strongeraxes", "gather_wicker_baskets"
    };

    private float _elapsed;
    private float _nextWaveAt = FirstWaveSec;

    /// <summary>原版 OnInitGame → DisableTechnologies。</summary>
    public void OnInit(ComponentManager cm)
    {
        foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
        {
            var player = cm.Players.GetPlayerEntity(pid);
            var tm = player != null ? cm.QueryInterface<TechnologyManager>(player.Entity) : null;
            tm?.SetDisabledTechnologies(DisabledTechs);
        }
    }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        if (_elapsed < _nextWaveAt) return;
        _nextWaveAt = _elapsed + MinWaveSec
            + (float)(cm.RNG.NextDouble() * (MaxWaveSec - MinWaveSec));
        // 原版 Math.round(random × (max-min) + min)。
        int waveSize = (int)MathF.Round(
            (float)cm.RNG.NextDouble() * (MaxWaveSize - MinWaveSize) + MinWaveSize,
            MidpointRounding.AwayFromZero);

        var sink = cm.Triggers.Sink;
        if (sink == null) return;
        foreach (var point in cm.Triggers.GetTriggerPoints("A"))
        {
            float px = point.X.ToFloat(), pz = point.Y.ToFloat();
            var wolves = sink.SpawnEntities(AttackerTemplate, 0, px, pz, waveSize, 2f);
            if (wolves.Count == 0) continue;
            IssueAttacks(cm, wolves, px, pz);
        }
    }

    /// <summary>原版 SpawnWolvesAndAttack 的目标选取:200m 内带 Health 且
    /// Organic+!Domestic 的最近 ≤3 个;不足则全图按距离补齐。狼群按序分摊。</summary>
    private static void IssueAttacks(ComponentManager cm, IReadOnlyList<EntityId> wolves,
        float px, float pz)
    {
        var range = SimSystem.Range;
        var net = SimSystem.Net;
        if (range == null || net == null) return;

        var candidates = new List<(EntityId Ent, float D2)>();
        foreach (var ent in range.GetNonGaiaEntities())
        {
            var health = cm.QueryInterface<HealthComponent>(ent);
            if (health == null || health.IsDead) continue;
            var id = cm.QueryInterface<IdentityComponent>(ent);
            if (id == null || !Content.EntityClassHelper.MatchesClassList(id.Classes, "Organic+!Domestic"))
                continue;
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null || !pos.InWorld) continue;
            float dx = pos.Position.X.ToFloat() - px, dz = pos.Position.Z.ToFloat() - pz;
            candidates.Add((ent, dx * dx + dz * dz));
        }
        // 距离升序,id  tie-break(确定性);近距过滤原版为 200m 内优先、不足全图补——
        // 排序后取前 N 即两段逻辑的等价(200m 内的恒排最前)。
        var targets = candidates
            .OrderBy(c => c.D2).ThenBy(c => c.Ent.Value)
            .Take(TargetCount)
            .Select(c => c.Ent)
            .ToList();
        if (targets.Count == 0) return;

        // 原版:对每个目标 ProcessCommand(attack, entities=该点全部狼, queued=true)。
        foreach (var target in targets)
            foreach (var wolf in wolves)
                net.SubmitAiCommand(Net.NetCommand.Attack(0, wolf.Value, target.Value));
    }
}

/// <summary>elephantine_triggers.js 移植:开局 gaia 防御布置。
/// 全部 gaia 士兵站姿 defensive;每座 gaia Tower 驻 1 名 kush 步兵,
/// 每座 Wonder/Temple/Pyramid 驻 1 名 kush 步兵或支援(原版
/// SpawnAndGarrisonAtClasses 的简化:模板按 civ+类从模板库筛,每建筑 1 名)。</summary>
public sealed class ElephantineScript : IMapScriptBehavior
{
    public void OnInit(ComponentManager cm)
    {
        var range = SimSystem.Range;
        if (range == null) return;

        // 1. gaia 士兵 → defensive(原版 InitElephantine_DefenderStance)。
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var id = cm.QueryInterface<IdentityComponent>(ent);
            if (id == null || !id.HasClass("Soldier")) continue;
            cm.QueryInterface<UnitAIComponent>(ent)?.SetStance("defensive", cm);
        }

        // 2. 驻军(原版 InitElephantine_GarrisonBuildings):kush 步兵进塔,
        // 步兵+支援进 Wonder/Temple/Pyramid。
        var sink = cm.Triggers.Sink;
        if (sink == null) return;
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var id = cm.QueryInterface<IdentityComponent>(ent);
            var holder = cm.QueryInterface<GarrisonHolderComponent>(ent);
            if (id == null || holder == null) continue;
            bool isTower = id.HasClass("Tower");
            bool isMonument = id.HasClass("Wonder") || id.HasClass("Temple") || id.HasClass("Pyramid");
            if (!isTower && !isMonument) continue;
            if (holder.OccupiedSlots(cm) >= holder.GetCapacity(cm)) continue;

            string template = isTower
                ? "units/kush/infantry_spearman_b"
                : "units/kush/support_healer_b";
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null) continue;
            var spawned = sink.SpawnEntities(template, 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), 1, 0f);
            if (spawned.Count == 0) continue;
            cm.QueryInterface<GarrisonableComponent>(spawned[0])?.Garrison(cm, ent);
        }
    }

    public void Tick(ComponentManager cm, float dt) { }
}

/// <summary>survivalofthefittest_triggers.js 移植:周期宝物 + 渐强攻击波。
/// 简化版核心(原版 488 行的波次生成):按宝物/攻击波计时器周期 SpawnEntities
/// (原版 DoRepeatedly 的周期触发;timer 用 cm 时基累计,锁步确定)。</summary>
public sealed class SurvivalOfTheFittestScript : IMapScriptBehavior
{
    private float _elapsed;
    private float _nextTreasure = 180f;   // 首个宝物约 3 分钟(原版 treasureTime 3-5 分)
    private float _nextWave = 270f;       // 首波约 4.5 分钟(原版 firstWaveTime 4-6 分)

    public void OnInit(ComponentManager cm) { }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        if (_elapsed >= _nextTreasure)
        {
            // 宝物(原版 spawnTreasure:随机宝物模板在随机可通行点)。
            var sink = cm.Triggers.Sink;
            if (sink != null)
            {
                var pos = RandomPassablePoint(cm);
                sink.SpawnEntities("gaia/treasure/food_bin", 0, pos.X, pos.Y, 1, 0f);
            }
            _nextTreasure += (float)(cm.RNG.NextDouble() * 120 + 180);
        }
        if (_elapsed >= _nextWave)
        {
            // 攻击波(原版 spawnAttackers:随机进攻模板,按时间渐增数量)。
            var sink = cm.Triggers.Sink;
            if (sink != null)
            {
                var pos = RandomPassablePoint(cm);
                // 1.05^minutes 的渐增(原版 percentPerMinute):整次幂
                // 近似(Math.Pow 属 libm,跨平台低位不同 → 门禁禁;
                // 分钟级整幂 ≈ 原版连续渐增的分钟粒度)。
                int minutes = (int)(_elapsed / 60f);
                double growth = 1.0;
                for (int m = 0; m < minutes; m++) growth *= 1.05;
                int count = (int)(5 * growth);
                sink.SpawnEntities("units/kush/infantry_spearman_b", 0, pos.X, pos.Y,
                    System.Math.Min(count, 200), 3f);
            }
            _nextWave += (float)(cm.RNG.NextDouble() * 120 + 120);
        }
    }

    private static (float X, float Y) RandomPassablePoint(ComponentManager cm)
    {
        var range = SimSystem.Range;
        if (range == null) return (0f, 0f);
        var ents = range.GetNonGaiaEntities();
        if (ents.Count == 0) return (0f, 0f);
        var any = ents[cm.RNG.NextInt(0, ents.Count)];
        var pos = cm.QueryInterface<PositionComponent>(any);
        return pos == null ? (0f, 0f)
            : (pos.Position.X.ToFloat(), pos.Position.Z.ToFloat());
    }
}
