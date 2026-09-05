namespace ZeroAD.Sim.Serialization;

/// <summary>存档格式版本的唯一真源(表现层 godot/Scripts/SaveGameManager.cs 引用),
/// 以及读档期的版本上下文:BinaryDeserializer 是纯位置流,组件 Deserialize 无法自查
/// "这个字段在旧档里存不存在",故由读档方(SaveGameManager.Load)在喂载荷前把文件版本
/// 写入 <see cref="LoadedVersion"/>,组件据以跳过新增字段(缺失按默认值/空表)。
/// 默认 <see cref="CurrentVersion"/>:新写档/录像初始载荷/测试直读路径都是当前格式。</summary>
public static class SaveFormat
{
    /// <summary>当前格式版本。v20: TriggerSystem 增触发点注册表尾段
    /// (ref → 实体 id 列表;v19 及更早档无此段,注册表读为空)。v19: RallyPointData
    /// 增 TargetClasses 尾段 + UnitOrder 增 AutoContinue/贸易航线 Route 尾段。</summary>
    public const uint CurrentVersion = 20;

    /// <summary>可读入的最低版本(v17 档缺 v18 尾段,按空表/零计时读)。</summary>
    public const uint MinReadableVersion = 17;

    /// <summary>正在读取的存档版本。读档方在 DeserializeSaveGame 前设置、读毕恢复
    /// CurrentVersion;内核组件只读。</summary>
    public static uint LoadedVersion = CurrentVersion;
}
