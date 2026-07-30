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

    public void AddXP(int amount)
    {
        XP += amount;
        while (XP >= XpNext)
        {
            XP -= XpNext;
            Level++;
            XpNext = (int)(XpNext * 1.5f);
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("xp", XP);
        s.NumberI32("lvl", Level);
        s.NumberI32("next", XpNext);
    }

    public override void Deserialize(IDeserializer d)
    {
        XP = d.NumberI32("xp");
        Level = d.NumberI32("lvl");
        XpNext = d.NumberI32("next");
    }

    public void HandleMessage(IMessage message) { }
}
