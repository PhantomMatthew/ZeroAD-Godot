using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 领土影响力(对齐原版 TerritoryInfluence —— 原版是模板数据,由 C++ TerritoryManager
/// 消费)。纯数据组件:<see cref="Radius"/>(米)/<see cref="Weight"/>(默认 1)/
/// <see cref="Root"/>(默认 false;root = 领土锚点,决定区域连通性)。
/// 模板无 &lt;TerritoryInfluence&gt; 节点即不装配(大多数单位无影响力)。
/// </summary>
[Component("TerritoryInfluence", "TerritoryInfluence")]
public sealed class TerritoryInfluenceComponent : ComponentBase, IComponentMessageHandler
{
    public Maths.Fixed Radius;
    public int Weight = 1;
    public bool Root;

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("radius", Radius);
        s.NumberI32("weight", Weight);
        s.Bool("root", Root);
    }

    public override void Deserialize(IDeserializer d)
    {
        Radius = d.NumberFixed("radius");
        Weight = d.NumberI32("weight");
        Root = d.Bool("root");
    }

    public void HandleMessage(IMessage message) { }
}
