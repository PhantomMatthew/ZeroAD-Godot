using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>资源涓流(原版 ResourceTrickle.js 移植):每隔 Interval 毫秒给属主发放
/// Rates 资源。奇观(1.0×4 / 2000ms)、牲口棚、部分特殊建筑;玩家模板亦有(0 率,
/// 供科技修正)。速率读取过修正值管线("ResourceTrickle/Rates/{res}");整型资源库下
/// 小数率逐间隔结转(原版浮点资源库无此问题,语义等价)。</summary>
[Component("ResourceTrickle", "ResourceTrickle")]
public sealed class ResourceTrickleComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>模板 Interval(毫秒)。≤0 = 停用(原版 interval &lt; 0 取消定时器)。</summary>
    public float IntervalMs = 1000f;
    // 模板 Rates(每次发放的基准量;可为小数)。
    public float FoodRate;
    public float WoodRate;
    public float StoneRate;
    public float MetalRate;

    private float _elapsedMs;
    // 小数结转(如 0.5/次 → 每两次发 1)。
    private float _fracFood, _fracWood, _fracStone, _fracMetal;

    protected override void OnInit() { }

    public bool HasAnyRate => FoodRate != 0 || WoodRate != 0 || StoneRate != 0 || MetalRate != 0;

    /// <summary>每 sim 回合驱动(dt=回合秒数)。属主缺失/无速率/间隔停用 → 跳过。</summary>
    public void Tick(ComponentManager cm, float dt)
    {
        if (IntervalMs <= 0 || !HasAnyRate) return;
        _elapsedMs += dt * 1000f;
        if (_elapsedMs < IntervalMs) return;
        _elapsedMs -= IntervalMs;

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var player = own != null ? cm.GetPlayerEntity(own.PlayerId) : null;
        // 玩家实体自身的涓流(template_player)无 Ownership —— 原版 QueryOwnerInterface
        // 失败后回退查询自身 Player 组件;本处同理回退"实体即玩家"的情况。
        if (player == null)
        {
            var selfPlayer = cm.QueryInterface<PlayerComponent>(Entity);
            if (selfPlayer != null) player = selfPlayer;
        }
        if (player == null) return;

        Pay(cm, player, ResourceType.Food, "ResourceTrickle/Rates/food", FoodRate, ref _fracFood);
        Pay(cm, player, ResourceType.Wood, "ResourceTrickle/Rates/wood", WoodRate, ref _fracWood);
        Pay(cm, player, ResourceType.Stone, "ResourceTrickle/Rates/stone", StoneRate, ref _fracStone);
        Pay(cm, player, ResourceType.Metal, "ResourceTrickle/Rates/metal", MetalRate, ref _fracMetal);
    }

    private void Pay(ComponentManager cm, PlayerComponent player, ResourceType type,
        string modPath, float baseRate, ref float frac)
    {
        if (baseRate == 0) return;
        float rate = cm.Modifiers.Apply(modPath, baseRate, Entity);
        float total = rate + frac;
        int whole = (int)MathF.Floor(total);
        frac = total - whole;
        if (whole > 0) player.AddResource(type, whole);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("interval", Maths.Fixed.FromFloat(IntervalMs));
        s.NumberFixed("fr", Maths.Fixed.FromFloat(FoodRate));
        s.NumberFixed("wr", Maths.Fixed.FromFloat(WoodRate));
        s.NumberFixed("sr", Maths.Fixed.FromFloat(StoneRate));
        s.NumberFixed("mr", Maths.Fixed.FromFloat(MetalRate));
        s.NumberFixed("elap", Maths.Fixed.FromFloat(_elapsedMs));
        s.NumberFixed("ff", Maths.Fixed.FromFloat(_fracFood));
        s.NumberFixed("wf", Maths.Fixed.FromFloat(_fracWood));
        s.NumberFixed("sf", Maths.Fixed.FromFloat(_fracStone));
        s.NumberFixed("mf", Maths.Fixed.FromFloat(_fracMetal));
    }

    public override void Deserialize(IDeserializer d)
    {
        IntervalMs = d.NumberFixed("interval").ToFloat();
        FoodRate = d.NumberFixed("fr").ToFloat();
        WoodRate = d.NumberFixed("wr").ToFloat();
        StoneRate = d.NumberFixed("sr").ToFloat();
        MetalRate = d.NumberFixed("mr").ToFloat();
        _elapsedMs = d.NumberFixed("elap").ToFloat();
        _fracFood = d.NumberFixed("ff").ToFloat();
        _fracWood = d.NumberFixed("wf").ToFloat();
        _fracStone = d.NumberFixed("sf").ToFloat();
        _fracMetal = d.NumberFixed("mf").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
