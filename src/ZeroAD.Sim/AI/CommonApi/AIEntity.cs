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

    // ── 元数据（Phase 0 EntityMetadata）──
    // 由 AIComponent.Metadata 持有，通过 GameState 传入。此处不直接持有——
    // 调用方经 GameState.Metadata.Get/Set(entityId, key, ...) 访问。
}
