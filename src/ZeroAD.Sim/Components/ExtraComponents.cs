using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// GarrisonHolder 已迁至 Garrison.cs(MS5:GarrisonHolder.js 行为件,替换 P0 计数版死代码)。

// MarketComponent 已并入 Trader.cs(MS5:Trader.js/Market.js 行为 + 保留 P0 barter 字段)。

[Component("RallyPoint", "RallyPoint")]
public sealed class RallyPointComponent : ComponentBase, IComponentMessageHandler
{
    public Maths.FixedVector2D Position;

    protected override void OnInit()
    {
        Position = new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero);
    }

    public void Set(Maths.FixedVector2D pos) => Position = pos;

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("x", Position.X);
        s.NumberFixed("z", Position.Y);
    }

    public override void Deserialize(IDeserializer d)
    {
        Position = new Maths.FixedVector2D(d.NumberFixed("x"), d.NumberFixed("z"));
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Vision", "Vision")]
public sealed class VisionComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>Base vision range in meters (fixed-point — this feeds LOS tick math).
    /// Template value comes from &lt;Vision&gt;&lt;Range&gt;; techs adjust the effective
    /// range through the modifiers pipeline ("Vision/Range").</summary>
    public Maths.Fixed Range;

    protected override void OnInit() => Range = Maths.Fixed.FromInt(20);

    public override void Serialize(ISerializer s) =>
        s.NumberFixed("range", Range);

    public override void Deserialize(IDeserializer d) =>
        Range = d.NumberFixed("range");

    public void HandleMessage(IMessage message) { }
}

[Component("Promotion", "Promotion")]
public sealed class PromotionComponent : ComponentBase, IComponentMessageHandler
{
    public int XP;
    public int Level = 1;
    public int XpNext = 20;
    /// <summary>Promotion/Entity:晋升目标模板(空 = 无晋升链,如 elite 段/英雄)。</summary>
    public string PromoteTo = "";

    public void AddXP(ComponentManager cm, int amount)
    {
        XP += amount;
        // 原版 Promotion.js:XP ≥ RequiredXp 即 Promote(ChangeEntityTemplate 换模板,
        // 位置/朝向/属主保持,血量按比例折算,余量 XP 结转新段)。
        if (PromoteTo.Length > 0 && XP >= XpNext && cm != null)
        {
            Promote(cm, XP - XpNext);
            return;
        }
        while (XP >= XpNext && PromoteTo.Length == 0)
        {
            // 无晋升链(原版到顶):等级继续累计(供表现层军衔条)。
            XP -= XpNext;
            Level++;
            XpNext = (int)(XpNext * 1.5f);
        }
    }

    /// <summary>旧签名(无 cm):只累计不晋升,行为兼容。</summary>
    public void AddXP(int amount) => AddXP(null!, amount);

    /// <summary>换模板晋升(原版 ChangeEntityTemplate 语义):同位同向同主重建,
    /// 血量比例折算,余量 XP 结转。新实体的组件字段由装配器按新模板注入。</summary>
    private void Promote(ComponentManager cm, int carryXp)
    {
        var identity = cm.QueryInterface<IdentityComponent>(Entity);
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var health = cm.QueryInterface<HealthComponent>(Entity);
        if (identity == null || pos == null || owner == null) return;

        string target = PromoteTo.Replace("{civ}",
            cm.GetPlayerEntity(owner.PlayerId)?.Civ ?? "");
        if (target.Contains('{') || cm.Templates?.TemplateExists(target) != true) return;

        float x = pos.Position.X.ToFloat();
        float z = pos.Position.Z.ToFloat();
        var yaw = pos.Rotation.Y;
        float frac = health != null && health.Max > 0
            ? (float)health.Current / health.Max : 1f;
        cm.DestroyEntity(Entity);
        var promoted = cm.SpawnEntity(target, x, z, owner.PlayerId);
        var newPos = cm.QueryInterface<PositionComponent>(promoted);
        if (newPos != null)
            newPos.Rotation = new Maths.FixedVector3D(pos.Rotation.X, yaw, pos.Rotation.Z);
        var newHealth = cm.QueryInterface<HealthComponent>(promoted);
        if (newHealth != null && frac < 1f)
            newHealth.Current = (int)MathF.Round(newHealth.Max * frac);
        var newPromotion = cm.QueryInterface<PromotionComponent>(promoted);
        if (newPromotion != null && carryXp > 0)
            newPromotion.XP = carryXp;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("xp", XP);
        s.NumberI32("lvl", Level);
        s.NumberI32("next", XpNext);
        s.StringASCII("to", PromoteTo);
    }

    public override void Deserialize(IDeserializer d)
    {
        XP = d.NumberI32("xp");
        Level = d.NumberI32("lvl");
        XpNext = d.NumberI32("next");
        PromoteTo = d.StringASCII("to");
    }

    public void HandleMessage(IMessage message) { }
}
