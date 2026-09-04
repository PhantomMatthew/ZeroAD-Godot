using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>建筑升级组件(原版 Upgrade.js 全量,364 行):
/// 原地升级——建筑保留,进度走时间(原版 250ms 心跳;我们 0.1s 锁步拍累加),
/// 完成时 ChangeEntityTemplate 换模板;开销在启动时扣,取消/被毁/易主全额退还。
/// GetProgress 供 GUI 进度条(原版 GetProgress 同款)。
/// 记录不移植:ChangeUpgradedEntityCount(产能暂停等价,我方无该计数)、
/// 完成音效(上游音频目录无 upgraded 组)、Variant 动画切到 idle(视觉侧还原)。</summary>
[Component("Upgrade", "Upgrade")]
public sealed class UpgradeComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>升级目标模板(空 = 未在升级)。</summary>
    public string TargetTemplate = "";
    /// <summary>已耗秒数(原版 elapsedTime;0.1s 拍累加)。</summary>
    public float ElapsedTime;
    /// <summary>所需秒数(原版 GetUpgradeTime;读档恢复)。</summary>
    public float RequiredTime;
    /// <summary>已扣资源(取消退还用;原版 expendedResources)。</summary>
    public int ExpendedWood, ExpendedFood, ExpendedStone, ExpendedMetal;
    /// <summary>升级中动画变体名(原版 Variant;Godot 侧由 ActorComposer 读)。</summary>
    public string Variant = "";

    public bool IsUpgrading => TargetTemplate.Length > 0;

    private ComponentManager? _subscribedCm;

    protected override void OnInit()
    {
        // 易主取消(原版 OnOwnershipChanged:换主即 CancelUpgrade(退还旧主))。
        var cm = SimSystem.Sim;
        if (cm == null || _subscribedCm != null) return;
        _subscribedCm = cm;
        cm.OwnerChanged += OnAnyOwnershipChanged;
    }

    protected override void OnDeinit()
    {
        if (_subscribedCm == null) return;
        _subscribedCm.OwnerChanged -= OnAnyOwnershipChanged;
        _subscribedCm = null;
    }

    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        if (entity != Entity) return;
        var cm = _subscribedCm;
        if (cm == null || !IsUpgrading) return;
        // 原版:CancelUpgrade(msg.from)——退还给**旧主**(换主后再开由新主决定)。
        var player = cm.GetPlayerEntity(from);
        if (player != null)
        {
            player.AddResource(ResourceType.Wood, ExpendedWood);
            player.AddResource(ResourceType.Food, ExpendedFood);
            player.AddResource(ResourceType.Stone, ExpendedStone);
            player.AddResource(ResourceType.Metal, ExpendedMetal);
        }
        TargetTemplate = "";
        ResetResources();
        Variant = "";
    }

    /// <summary>原版 GetProgress:0..1(时间 0 → 1)。</summary>
    public float GetProgress()
    {
        if (!IsUpgrading) return -1f;
        if (RequiredTime <= 0f) return 1f;
        return System.Math.Min(ElapsedTime / RequiredTime, 1f);
    }

    /// <summary>启动升级(原版 Upgrade():扣费 + 设变体;产能检查在调用方)。</summary>
    public bool StartUpgrade(ComponentManager cm, string targetTemplate, float time,
        int wood, int food, int stone, int metal, string variant, PlayerComponent player)
    {
        if (IsUpgrading) return false;
        // 原子扣费(原版 TrySubtractResources:全部够才扣)。
        if (!player.CanAfford(wood, food, stone, metal)) return false;
        player.TrySpend(ResourceType.Wood, wood);
        player.TrySpend(ResourceType.Food, food);
        player.TrySpend(ResourceType.Stone, stone);
        player.TrySpend(ResourceType.Metal, metal);
        TargetTemplate = targetTemplate;
        RequiredTime = time > 0 ? time : 0f;
        ElapsedTime = 0f;
        ExpendedWood = wood;
        ExpendedFood = food;
        ExpendedStone = stone;
        ExpendedMetal = metal;
        Variant = variant;
        if (RequiredTime <= 0f)
            Complete(cm);   // 原版:Time=0 立即完成
        return true;
    }

    /// <summary>拍推进(原版 UpgradeProgress 的 250ms 心跳 → 我们 0.1s 锁步拍)。</summary>
    public void Tick(ComponentManager cm, float dt)
    {
        if (!IsUpgrading) return;
        ElapsedTime += dt;
        if (ElapsedTime >= RequiredTime)
            Complete(cm);
    }

    /// <summary>完成(原版 UpgradeProgress 的完成段):ChangeEntityTemplate 换模板
    /// (Pack.Transform 同款:同位/同向/同主 spawn 新实体 + 血量比例 + 毁旧)。</summary>
    private void Complete(ComponentManager cm)
    {
        string target = TargetTemplate;
        TargetTemplate = "";
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos == null) { ResetResources(); return; }
        var posVec = pos.Position;
        var rot = pos.Rotation;
        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        float healthFrac = cm.QueryInterface<HealthComponent>(Entity) is { } hp && hp.Max > 0
            ? (float)hp.Current / hp.Max : 1f;
        var newEnt = cm.SpawnEntity(target, posVec.X.ToFloat(), posVec.Z.ToFloat(), owner);
        var newPos = cm.QueryInterface<PositionComponent>(newEnt);
        if (newPos != null) newPos.Rotation = rot;
        var newHealth = cm.QueryInterface<HealthComponent>(newEnt);
        if (newHealth != null)
            newHealth.Current = System.Math.Max(1, (int)(newHealth.Max * healthFrac));
        ResetResources();
        cm.DestroyEntity(Entity);
    }

    /// <summary>取消(原版 CancelUpgrade:退还全部开销 + 清状态)。
    /// 由被毁前钩子(RemoveDeadEntities)与易主事件调用。</summary>
    public void CancelUpgrade(ComponentManager cm)
    {
        if (!IsUpgrading) return;
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var player = owner != null ? cm.GetPlayerEntity(owner.PlayerId) : null;
        if (player != null)
        {
            player.AddResource(ResourceType.Wood, ExpendedWood);
            player.AddResource(ResourceType.Food, ExpendedFood);
            player.AddResource(ResourceType.Stone, ExpendedStone);
            player.AddResource(ResourceType.Metal, ExpendedMetal);
        }
        TargetTemplate = "";
        ResetResources();
        Variant = "";
    }

    private void ResetResources()
    {
        ExpendedWood = ExpendedFood = ExpendedStone = ExpendedMetal = 0;
        ElapsedTime = 0f;
    }

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("target", TargetTemplate);
        s.NumberFixed("elapsed", Fixed.FromFloat(ElapsedTime));
        s.NumberFixed("required", Fixed.FromFloat(RequiredTime));
        s.NumberI32("ew", ExpendedWood);
        s.NumberI32("ef", ExpendedFood);
        s.NumberI32("es", ExpendedStone);
        s.NumberI32("em", ExpendedMetal);
        s.StringASCII("variant", Variant);
    }

    public override void Deserialize(IDeserializer d)
    {
        TargetTemplate = d.StringASCII("target");
        ElapsedTime = d.NumberFixed("elapsed").ToFloat();
        RequiredTime = d.NumberFixed("required").ToFloat();
        ExpendedWood = d.NumberI32("ew");
        ExpendedFood = d.NumberI32("ef");
        ExpendedStone = d.NumberI32("es");
        ExpendedMetal = d.NumberI32("em");
        Variant = d.StringASCII("variant");
    }

    public void HandleMessage(IMessage message) { }
}
