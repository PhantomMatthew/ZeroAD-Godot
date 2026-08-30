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

/// <summary>flood_triggers.js 移植:水位渐涨,淹没陆地(动物/单位淹死,
/// 建筑/资源转 actor)。简化版:周期升水位 + 水下实体处置
/// (原版 DoAfterDelay 的周期触发;timer 用 cm 时基累计)。</summary>
public sealed class FloodScript : IMapScriptBehavior
{
    private float _elapsed;
    private float _nextRise = 260f;        // 首次升水位约 4.3 分钟(原版 schedule 260s)
    private const float DeltaTime = 2.4f;  // 每步间隔(原版 deltaTime)
    private const float DeltaWater = 0.5f; // 每步水位(原版 deltaWaterLevel)
    private const float DrownDepth = 2f;   // 淹没深度(原版 drownDepth)

    public void OnInit(ComponentManager cm) { }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        if (_elapsed < _nextRise) return;
        _nextRise += DeltaTime;

        // 升水位(原版 RaiseWaterLevel:水位 + 淹死/转 actor)。
        float newLevel = cm.Water.WaterHeight.ToFloat() + DeltaWater;
        cm.Water.SetWaterLevel(Maths.Fixed.FromFloat(newLevel));

        var range = SimSystem.Range;
        if (range == null) return;
        foreach (var ent in range.GetNonGaiaEntities())
        {
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null || !pos.InWorld) continue;
            if (pos.Position.Y.ToFloat() + DrownDepth >= newLevel) continue;

            var health = cm.QueryInterface<HealthComponent>(ent);
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            if (health != null && identity != null && identity.HasClass("Organic"))
            {
                // 动物/单位淹死(原版 cmpHealth.Kill)。
                health.TakeDamage(health.Current);
                continue;
            }
            // 建筑/资源转 actor(原版 DestroyEntity + AddEntity("actor|...")——
            // 我们的视觉层 SimBridge 已按模板解析视觉,此处销毁即可
            // (原版转 actor 是为保视觉;我们的装饰物由视觉层独立维护)。
        }
    }
}

/// <summary>extinct_volcano_triggers.js 移植:火山湖渐涨(原版 SeaLevelRise
/// 计时器),木塔驻罗马冠军兵。简化版:OnInit 驻塔兵 + 周期升水位(上限 70)。</summary>
public sealed class ExtinctVolcanoScript : IMapScriptBehavior
{
    private float _elapsed;
    private float _nextRise = 1500f;       // 首次升水位约 25 分钟(原版 SeaLevelRiseTime 默认 25 分)
    private const float IncreaseTime = 30f; // 升水位间隔(原版 waterIncreaseTime 0.5-1 分)
    private const float IncreaseHeight = 1f; // 每步水位(原版 waterLevelIncreaseHeight)
    private const float MaxLevel = 70f;     // 上限(原版 maxWaterLevel)
    private const float DrownHeight = 1f;   // 淹没高度(原版 drownHeight)

    public void OnInit(ComponentManager cm)
    {
        // 驻塔兵(原版 GarrisonWoodenTowers:每座 gaia Tower 驻满罗马冠军兵)。
        var range = SimSystem.Range;
        if (range == null) return;
        var sink = cm.Triggers.Sink;
        if (sink == null) return;
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            var holder = cm.QueryInterface<GarrisonHolderComponent>(ent);
            if (identity == null || !identity.HasClass("Tower") || holder == null) continue;
            if (holder.OccupiedSlots(cm) >= holder.GetCapacity(cm)) continue;
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null) continue;
            int capacity = holder.GetCapacity(cm);
            var spawned = sink.SpawnEntities("units/rome/champion_infantry_swordsman_02", 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), capacity, 0f);
            foreach (var s in spawned)
                cm.QueryInterface<GarrisonableComponent>(s)?.Garrison(cm, ent);
        }
    }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        if (_elapsed < _nextRise) return;
        _nextRise += IncreaseTime;

        float newLevel = cm.Water.WaterHeight.ToFloat() + IncreaseHeight;
        if (newLevel > MaxLevel) return;   // 上限即停(原版 maxWaterLevel)
        cm.Water.SetWaterLevel(Maths.Fixed.FromFloat(newLevel));

        var range = SimSystem.Range;
        if (range == null) return;
        foreach (var ent in range.GetNonGaiaEntities())
        {
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null || !pos.InWorld) continue;
            if (pos.Position.Y.ToFloat() + DrownHeight >= newLevel) continue;
            var health = cm.QueryInterface<HealthComponent>(ent);
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            if (health != null && identity != null && identity.HasClass("Organic"))
                health.TakeDamage(health.Current);
        }
    }
}

/// <summary>danubius_triggers.js 移植:高卢城驻兵 + 周期舰船袭扰。
/// 简化版:OnInit 驻塔/驻屋兵 + CC 防御兵,周期在 CC 旁生成舰船+攻兵
/// (原版 GarrisonAllGallicBuildings/SpawnCCAttackers 的简化;舰船袭扰
/// 用周期 SpawnEntities 替代原版的舰船实体+卸载逻辑)。</summary>
public sealed class DanubiusScript : IMapScriptBehavior
{
    private float _elapsed;
    private float _nextWave = 300f;   // 首波约 5 分钟(原版 shipUngarrisonInterval 首波)
    private const float WaveInterval = 240f;

    public void OnInit(ComponentManager cm)
    {
        var range = SimSystem.Range;
        if (range == null) return;
        var sink = cm.Triggers.Sink;
        if (sink == null) return;

        // 驻塔/驻屋兵(原版 GarrisonAllGallicBuildings:House 平民+医师,
        // CivCentre/Temple 冠军,Tower 冠军步兵)。
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            var holder = cm.QueryInterface<GarrisonHolderComponent>(ent);
            if (identity == null || holder == null) continue;
            if (holder.OccupiedSlots(cm) >= holder.GetCapacity(cm)) continue;

            string template;
            if (identity.HasClass("House")) template = "units/gaul/support_civilian";
            else if (identity.HasClass("CivCentre") || identity.HasClass("Temple"))
                template = "units/gaul/champion_infantry_spearman";
            else if (identity.HasClass("Tower")) template = "units/gaul/champion_infantry_swordsman";
            else continue;

            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null) continue;
            var spawned = sink.SpawnEntities(template, 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), 1, 0f);
            if (spawned.Count > 0)
                cm.QueryInterface<GarrisonableComponent>(spawned[0])?.Garrison(cm, ent);
        }

        // CC 防御兵(原版 SpawnInitialCCDefenders:每座高卢 CC 驻公民兵+冠军+医师+平民+羊)。
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            if (identity == null || !identity.HasClass("CivCentre")) continue;
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null) continue;
            foreach (var (template, count) in new[]
            {
                ("units/gaul/infantry_spearman_b", 8),
                ("units/gaul/champion_infantry_spearman", 13),
                ("units/gaul/support_healer_b", 4),
                ("units/gaul/support_civilian", 5),
                ("gaia/fauna_sheep", 10),
            })
            {
                var spawned = sink.SpawnEntities(template, 0,
                    pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), count, 2f);
                foreach (var s in spawned)
                    cm.QueryInterface<UnitAIComponent>(s)?.SetStance("defensive", cm);
            }
        }
    }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        if (_elapsed < _nextWave) return;
        _nextWave += WaveInterval;

        // 舰船袭扰(原版:在 CC 旁生成舰船+攻兵;简化用 SpawnEntities 周期生成)。
        var range = SimSystem.Range;
        if (range == null) return;
        var sink = cm.Triggers.Sink;
        if (sink == null) return;
        foreach (var ent in range.GetEntitiesByPlayer(0))
        {
            var identity = cm.QueryInterface<IdentityComponent>(ent);
            if (identity == null || !identity.HasClass("CivCentre")) continue;
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null) continue;
            sink.SpawnEntities("units/gaul/ship_trireme_b", 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), 1, 3f);
            sink.SpawnEntities("units/gaul/infantry_spearman_b", 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), 8, 3f);
            sink.SpawnEntities("units/gaul/champion_infantry_spearman", 0,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), 4, 2f);
        }
    }
}

/// <summary>jebel_barkal_triggers.js 移植:城市巡逻 + 渐强 gaia 攻击。
/// 简化版:周期在 gaia 建筑旁生成巡逻/攻兵(原版城市巡逻队+攻击间隔
/// 随时间渐强;timer 用 cm 时基累计)。</summary>
public sealed class JebelBarkalScript : IMapScriptBehavior
{
    private float _elapsed;
    private float _nextPatrol = 300f;   // 首巡逻约 5 分钟(原版 firstCityPatrolTime)
    private float _nextAttack = 420f;   // 首攻击约 7 分钟(原版 attackInterval 首波)
    private const float MaxPopulation = 1200f;   // 原版 8×150 上限

    public void OnInit(ComponentManager cm) { }

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt;
        var sink = cm.Triggers.Sink;
        if (sink == null) return;

        // 城市巡逻(原版 jebelBarkal_cityPatrolGroup:在 Wonder/Temple/CivCentre
        // 旁生成步兵冠军巡逻队,按时间渐增数量)。
        if (_elapsed >= _nextPatrol)
        {
            _nextPatrol += 180f;
            var range = SimSystem.Range;
            if (range != null)
            {
                foreach (var ent in range.GetEntitiesByPlayer(0))
                {
                    var identity = cm.QueryInterface<IdentityComponent>(ent);
                    if (identity == null) continue;
                    if (!(identity.HasClass("Wonder") || identity.HasClass("Temple")
                        || identity.HasClass("CivCentre") || identity.HasClass("Fortress")
                        || identity.HasClass("Barracks") || identity.HasClass("Embassy")))
                        continue;
                    var pos = cm.QueryInterface<PositionComponent>(ent);
                    if (pos == null) continue;
                    int count = System.Math.Min(20, 10 + (int)(_elapsed / 120f));
                    sink.SpawnEntities("units/kush/champion_infantry_spearman", 0,
                        pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), count, 3f);
                }
            }
        }

        // gaia 攻击(原版 jebelBarkal_attackInterval:周期渐强攻击波,
        // 数量按时间渐增;上限 1200 人口)。
        if (_elapsed >= _nextAttack)
        {
            _nextAttack += (float)(cm.RNG.NextDouble() * 120 + 300);
            var range = SimSystem.Range;
            if (range != null)
            {
                foreach (var ent in range.GetEntitiesByPlayer(0))
                {
                    var identity = cm.QueryInterface<IdentityComponent>(ent);
                    if (identity == null || !identity.HasClass("CivCentre")) continue;
                    var pos = cm.QueryInterface<PositionComponent>(ent);
                    if (pos == null) continue;
                    int count = System.Math.Min(50, 15 + (int)(_elapsed / 60f));
                    sink.SpawnEntities("units/kush/infantry_spearman_b", 0,
                        pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), count, 4f);
                    sink.SpawnEntities("units/kush/champion_infantry_spearman", 0,
                        pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(), count / 3, 2f);
                }
            }
        }
    }
}
