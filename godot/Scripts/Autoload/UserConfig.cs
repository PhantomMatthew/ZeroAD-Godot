using Godot;

namespace ZeroAD.Godot;

// 用户配置持久化(autoload),Options 页(Phase 3)与音量等用。底层 ConfigFile(user://settings.cfg),
// 仿原版 ConfigDB 的 Get/Set/Save/Reset。所有值存于 "settings" section。
public partial class UserConfig : Node
{
    private const string Path = "user://settings.cfg";
    private const string Section = "settings";
    private readonly ConfigFile _cfg = new();

    public override void _Ready()
    {
        // 首次运行无文件:建空文件,后续读写才有所依托。
        if (_cfg.Load(Path) != Error.Ok)
            _cfg.Save(Path);
    }

    public string GetString(string key, string dflt) => (string)_cfg.GetValue(Section, key, dflt);
    public void SetString(string key, string val) => _cfg.SetValue(Section, key, val);

    public double GetNumber(string key, double dflt) => (double)_cfg.GetValue(Section, key, dflt);
    public void SetNumber(string key, double val) => _cfg.SetValue(Section, key, val);

    public bool GetBool(string key, bool dflt) => (bool)_cfg.GetValue(Section, key, dflt);
    public void SetBool(string key, bool val) => _cfg.SetValue(Section, key, val);

    public Error Save() => _cfg.Save(Path);
    public void Reset() { _cfg.Clear(); _cfg.Save(Path); }
}
