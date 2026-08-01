using System;
using System.Collections.Generic;
using Godot;
using ZeroAD.Godot.Options;

namespace ZeroAD.Godot;

// 用户配置持久化(autoload),Options 页(Phase 3)与音量等用。底层 ConfigFile(user://settings.cfg)。
// "user" section 镜像原版 ConfigDB 的 CFG_USER 命名空间:键为点分 config 键,值全为字符串,
// 读取向下回落 default.cfg(GetEffective),改动即时生效但仅 Save 持久化,HasChanges 跟踪未落盘改动。
public partial class UserConfig : Node
{
    private const string Path = "user://settings.cfg";
    private const string Section = "settings";   // 旧定 section API 保留(向后兼容)
    private const string UserSection = "user";   // ConfigDB "user" 命名空间等价

    private readonly ConfigFile _cfg = new();
    private bool _hasChanges;

    /// <summary>对齐原版 fireConfigChangeHandlers:Set/ResetUserValue/批量重应用后触发,
    /// 参数为改动的 config 键集,供 HUD/相机/音频等消费方订阅。</summary>
    public event Action<IReadOnlyList<string>>? ConfigChanged;

    /// <summary>对齐 ConfigDB_HasChanges("user"):有未落盘改动(Save/Revert 清位)。</summary>
    public bool HasChanges => _hasChanges;

    public override void _Ready()
    {
        // 首次运行无文件:建空文件,后续读写才有所依托。
        if (_cfg.Load(Path) != Error.Ok)
            _cfg.Save(Path);
    }

    // ── ConfigDB "user" 命名空间语义(Options 页用) ──

    /// <summary>用户命名空间的值;null = 未覆盖(对齐 ConfigDB 中 user 层无此键)。</summary>
    public string? GetUserValue(string key) =>
        _cfg.HasSectionKey(UserSection, key) ? (string)_cfg.GetValue(UserSection, key) : null;

    /// <summary>写用户值:即时生效(发 ConfigChanged)、标 dirty;持久化须 Save()。
    /// 对齐原版 ConfigDB_CreateValue("user") + fireConfigChangeHandlers。</summary>
    public void SetUserValue(string key, string value)
    {
        _cfg.SetValue(UserSection, key, value);
        _hasChanges = true;
        ConfigChanged?.Invoke(new[] { key });
    }

    /// <summary>删用户值使其回落默认(原版 ConfigDB_RemoveValue);无覆盖时 no-op。</summary>
    public void ResetUserValue(string key)
    {
        if (!_cfg.HasSectionKey(UserSection, key)) return;
        _cfg.EraseSectionKey(UserSection, key);
        _hasChanges = true;
        ConfigChanged?.Invoke(new[] { key });
    }

    /// <summary>default.cfg 默认值;无则 null(原版回落链底返回 ""——此处区分"无默认"便于 UI 标注)。</summary>
    public static string? GetDefault(string key) => DefaultConfig.Get(key);

    /// <summary>有效值 = user ?? default ?? ""(对齐 ConfigDB_GetValue("user") 向下回落)。</summary>
    public string GetEffective(string key) => GetUserValue(key) ?? GetDefault(key) ?? "";

    /// <summary>批量重应用后统一广播(对齐 Revert/Reset 后 fireConfigChangeHandlers(changedKeys))。</summary>
    public void FireConfigChanged(IReadOnlyList<string> keys) => ConfigChanged?.Invoke(keys);

    /// <summary>持久化并清 dirty(原版 ConfigDB_SaveChanges)。</summary>
    public Error Save()
    {
        var err = _cfg.Save(Path);
        if (err == Error.Ok) _hasChanges = false;
        return err;
    }

    /// <summary>重读盘、丢弃未存改动并清 dirty(原版 ConfigDB_Reload)。</summary>
    public void Revert()
    {
        _cfg.Load(Path);
        _hasChanges = false;
    }

    /// <summary>清空全部用户命名空间值(原版 Reset 的 RemoveValue 循环);不落盘、不发信号——
    /// 由调用方(Options 页 Reset)统一 Save + 广播。</summary>
    public void ClearUserNamespace()
    {
        if (!_cfg.HasSection(UserSection)) return;
        foreach (var key in _cfg.GetSectionKeys(UserSection))
            _cfg.EraseSectionKey(UserSection, key);
        _hasChanges = true;
    }

    // ── 旧定 section API(保留,暂无消费方) ──

    public string GetString(string key, string dflt) => (string)_cfg.GetValue(Section, key, dflt);
    public void SetString(string key, string val) => _cfg.SetValue(Section, key, val);

    public double GetNumber(string key, double dflt) => (double)_cfg.GetValue(Section, key, dflt);
    public void SetNumber(string key, double val) => _cfg.SetValue(Section, key, val);

    public bool GetBool(string key, bool dflt) => (bool)_cfg.GetValue(Section, key, dflt);
    public void SetBool(string key, bool val) => _cfg.SetValue(Section, key, val);

    public void Reset() { _cfg.Clear(); _cfg.Save(Path); }
}
