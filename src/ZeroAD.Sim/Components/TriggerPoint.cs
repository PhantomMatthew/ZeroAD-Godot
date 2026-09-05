using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>TriggerPoint 组件(原版 TriggerPoint.js 移植):持模板
/// &lt;TriggerPoint&gt;&lt;Reference&gt; 的引用名。装配时由 EntityAssembler 按模板
/// 自动注册进 TriggerSystem(原版 Init → Trigger.RegisterTriggerPoint),销毁时由
/// ComponentManager.DestroyEntity 移除(原版 OnDestroy → RemoveRegisteredTriggerPoint)。
/// 实体本身是 OnRange 主动查询的 source(RangeManager.CreateActiveQuery),
/// 坐标经 PositionComponent 取,注册表只存 EntityId。</summary>
[Component("TriggerPoint", "TriggerPoint")]
public sealed class TriggerPointComponent : ComponentBase
{
    /// <summary>触发点引用名(模板 TriggerPoint/Reference,如 "A")。空 = 未配置。</summary>
    public string Reference = "";

    public override void Serialize(ISerializer serializer) =>
        serializer.StringASCII("ref", Reference);

    public override void Deserialize(IDeserializer deserializer) =>
        Reference = deserializer.StringASCII("ref");
}
