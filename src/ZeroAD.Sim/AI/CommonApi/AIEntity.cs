using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>实体门面（原版 common-api/entity.js 的 Entity 类，~490 行）。
/// 封装 (ComponentManager, EntityId)，通过 live QueryInterface 读实时状态。
/// 非 snapshot 模式——直接查活组件（匹配现有 AIComponent/RebuildOwned 模式）。</summary>
public sealed class AIEntity
{
    public readonly ComponentManager Cm;
    public readonly EntityId Entity;
    public readonly AITemplate Template;

    public AIEntity(ComponentManager cm, EntityId entity, AITemplate template)
    { Cm = cm; Entity = entity; Template = template; }

    public uint Id => Entity.Value;

    // ── 组件查询辅助 ──
    private T? Get<T>() where T : class, IComponent => Cm.QueryInterface<T>(Entity);

    // ── 位置/朝向 ──
    private PositionComponent? Pos => Get<PositionComponent>();
    public FixedVector3D Position => Pos?.Position ?? new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
    public FixedVector2D Position2D => new(Position.X, Position.Z);
    public float Angle => Pos?.Rotation.Y.ToFloat() ?? 0f;

    // ── 归属 ──
    public int Owner => Get<OwnershipComponent>()?.PlayerId ?? 0;
    public bool IsOwn(int playerId) => Owner == playerId;

    // ── 生命 ──
    public int Hitpoints => Get<HealthComponent>()?.Current ?? 0;
    public int MaxHitpoints => Get<HealthComponent>()?.Max ?? 0;
    public bool IsHurt => Hitpoints < MaxHitpoints;
    public float HealthLevel => MaxHitpoints > 0 ? (float)Hitpoints / MaxHitpoints : 0f;
    public bool IsDead => Get<HealthComponent>()?.IsDead ?? false;

    // ── 身份/类 ──
    public string GenericName => Template.GenericName;
    public bool HasClass(string cls) => Template.HasClass(cls);
    public bool IsUnit => Template.IsUnit;
    public bool IsStructure => Template.IsStructure;

    // ── UnitAI 状态 ──
    public bool IsIdle => Get<UnitAIComponent>()?.FsmStateName?.Contains("IDLE") ?? true;
    public string? UnitAIState => Get<UnitAIComponent>()?.FsmStateName;

    // ── 生产队列 ──
    public bool HasTrainingQueue => Get<ProductionQueue>()?.Queue.Count > 0;

    // ── 建造（地基）──
    public float FoundationProgress
    {
        get
        {
            var f = Get<FoundationComponent>();
            return f?.Progress ?? 1f;  // 无 Foundation = 已建成
        }
    }
    public bool IsFoundation => Get<FoundationComponent>() != null;

    // ── 资源 ──
    public int ResourceSupplyAmount => Get<ResourceSupply>()?.Amount ?? 0;
    public int ResourceCarrying => Get<ResourceGatherer>()?.CarryAmount ?? 0;
    public ResourceType CarryType => Get<ResourceGatherer>()?.CarryType ?? ResourceType.Wood;

    // ── 驻军 ──
    public int GarrisonedCount => Get<GarrisonHolderComponent>()?.Entities.Count ?? 0;

    // ── 占领 ──
    public float CapturePoints
        => Get<CapturableComponent>()?.MaxCapturePoints.ToFloat() ?? 0f;

    // ── 攻击 ──
    public bool CanAttack => Get<AttackComponent>() != null;
    public bool IsRanged => Get<AttackComponent>()?.IsRanged ?? false;

    // ── 能力查询(原版 entity.js 模板能力面补全;Petra 决策判读件)──
    /// <summary>可建造(原版 isBuilder:Builder 组件存在)。</summary>
    public bool IsBuilder => Get<BuilderComponent>() != null;
    /// <summary>可采集(原版 isGatherer:ResourceGatherer 组件存在)。</summary>
    public bool IsGatherer => Get<ResourceGatherer>() != null;
    /// <summary>可治疗(原版 isHealable:HealComponent 存在)。</summary>
    public bool IsHealer => Get<HealComponent>() != null;
    /// <summary>可修理(原版 isRepairable:RepairableComponent 存在且未禁用)。</summary>
    public bool IsRepairable => Get<RepairableComponent>() is { IsRepairable: true };
    /// <summary>可驻防(原版 isGarrisonHolder:GarrisonHolder 组件存在)。</summary>
    public bool IsGarrisonHolder => Get<GarrisonHolderComponent>() != null;
    /// <summary>可炮塔持有(原版 isTurretHolder:TurretHolder 组件存在)。</summary>
    public bool IsTurretHolder => Get<TurretHolderComponent>() != null;
    /// <summary>可上炮塔(原版 canOccupyTurret:Turretable 组件存在)。</summary>
    public bool CanOccupyTurret => Get<TurretableComponent>() != null;
    /// <summary>可寻宝(原版 isTreasureCollector:TreasureCollector 组件存在)。</summary>
    public bool IsTreasureCollector => Get<TreasureCollectorComponent>() != null;
    /// <summary>可治疗目标(原版 isHealable:Health 存在且未不可治疗)。</summary>
    public bool IsHealable => Get<HealthComponent>() is { Unhealable: false };
    /// <summary>需要治疗(原版 needsHeal:受伤且可治疗)。</summary>
    public bool NeedsHeal => IsHurt && IsHealable;
    /// <summary>需要修理(原版 needsRepair:受伤且可修理)。</summary>
    public bool NeedsRepair => IsHurt && IsRepairable;
    /// <summary>防御火力(原版 hasDefensiveFire:BuildingAI 存在)。</summary>
    public bool HasDefensiveFire => Get<BuildingAIComponent>() != null;
    /// <summary>视野范围(原版 visionRange:VisionComponent/Range)。</summary>
    public float VisionRange => Get<VisionComponent>()?.Range.ToFloat() ?? 0f;
    /// <summary>贸易增益(原版 gainMultiplier:TraderComponent/GainMultiplier)。</summary>
    public float GainMultiplier => Get<TraderComponent>()?.GainMultiplier ?? 0f;
    /// <summary>晋升目标(原版 promotion:PromotionComponent 下一 rank)。</summary>
    public string Promotion => Get<PromotionComponent>()?.PromoteTo ?? "";
    /// <summary>可打包(原版 isPackable:PackComponent 存在)。</summary>
    public bool IsPackable => Get<PackComponent>() != null;

    // ── 元数据（Phase 0 EntityMetadata）──
    // 由 AIComponent.Metadata 持有，通过 GameState 传入。此处不直接持有——
    // 调用方经 GameState.Metadata.Get/Set(entityId, key, ...) 访问。
}
