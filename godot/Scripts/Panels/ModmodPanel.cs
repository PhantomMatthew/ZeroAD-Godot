using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using ZeroAD.Godot.Dialogs;

namespace ZeroAD.Godot;

// ModmodPanel — 原版 gui/mod/gui/modmod/modmod.js 的端口(mod 管理页):
// 双列(已启用 | 可用)mod 列表,启用/禁用(含依赖级联禁用)、依赖校验(name + 版本比较
// =/</>/<=/>=)、文本/取反/仅兼容过滤、启用列上下移(加载序)、描述框、Visit Website、
// Save Configuration(写 UserConfig mod.enabledmods)、Start Mods(保存+重启提示——
// 本引擎尚无运行时 mod 挂载,重启后亦暂不生效,仅配置持久化,见 PORTING-GAPS §8)。
// 有互不兼容 mod 时自动弹 IncompatibleModsDialog(原版 init 同款)。
public sealed partial class ModmodPanel : ModalPanelBase
{
    /// <summary>mod.json 条目(folder = 目录名,即列表标识;name 用于依赖比较)。</summary>
    public sealed record ModEntry(
        string Folder, string Name, string Version, string Label,
        string Url, string Description, IReadOnlyList<string> Dependencies);

    private static readonly ModEntry FakeMod = new("", "This mod does not exist", "", "", "", "",
        System.Array.Empty<string>());

    private readonly Dictionary<string, ModEntry> _mods = new();
    private List<string> _enabled = new();
    private List<string> _disabled = new();
    private readonly Dictionary<string, bool> _compat = new();
    private List<string> _installedByModIo = new();   // mod.io 刚装的(mod name),绿色标记

    private Tree _enabledTree = null!;
    private Tree _disabledTree = null!;
    private LineEdit _filter = null!;
    private CheckBox _negateFilter = null!;
    private CheckBox _compatFilter = null!;
    private Button _toggleButton = null!;
    private Button _upButton = null!;
    private Button _downButton = null!;
    private Button _visitButton = null!;
    private Button _saveButton = null!;
    private Button _startButton = null!;
    private Label _description = null!;
    private Label _message = null!;
    private UserConfig _cfg = null!;

    /// <summary>mod.io 页面刚装完的 mod 名(ModioPanel.OnModDownloaded 注入)。</summary>
    public void AddInstalled(string modName)
    {
        if (!_installedByModIo.Contains(modName)) _installedByModIo.Add(modName);
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell("Mod Selection", 960);
        _cfg = GetNode<UserConfig>("/root/UserConfig");

        // 过滤行(原版 modGenericFilter / negateFilter / modCompatibleFilter)。
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 8);
        filterRow.AddChild(MakeLabel(Localization.Tr("Filter:"), 13));
        _filter = new LineEdit
        {
            PlaceholderText = Localization.Tr("Filter"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        UITheme.ApplyModernInput(_filter);
        _filter.TextChanged += _ => DisplayLists();
        filterRow.AddChild(_filter);
        _negateFilter = new CheckBox { Text = Localization.Tr("Negate") };
        UITheme.ApplyCheckboxIcons(_negateFilter);
        _negateFilter.Toggled += _ => DisplayLists();
        filterRow.AddChild(_negateFilter);
        _compatFilter = new CheckBox
        {
            Text = Localization.Tr("Filter compatible mods"),
            ButtonPressed = true,
        };
        UITheme.ApplyCheckboxIcons(_compatFilter);
        _compatFilter.Toggled += _ => DisplayLists();
        filterRow.AddChild(_compatFilter);
        content.AddChild(filterRow);

        // 双列(原版 modsEnabledList / modsDisabledList:folder/label/version/dependencies 列)。
        var lists = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        lists.AddThemeConstantOverride("separation", 12);
        content.AddChild(lists);
        _enabledTree = BuildModList(lists, "Enabled Mods");
        _disabledTree = BuildModList(lists, "Available Mods");

        // 启用列排序按钮(原版 enabledModUp/Down;过滤中禁用——过滤位次无法映射回真实序)。
        var orderRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        orderRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(orderRow);
        _upButton = AddButton(orderRow, "Move Up", () => MoveCurrent(true), disabled: true);
        _downButton = AddButton(orderRow, "Move Down", () => MoveCurrent(false), disabled: true);

        _description = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _description.AddThemeFontSizeOverride("font_size", 13);
        content.AddChild(_description);

        _message = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _message.AddThemeFontSizeOverride("font_size", 13);
        _message.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f));
        content.AddChild(_message);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 10);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", () => { Close(); });
        _visitButton = AddButton(buttons, "Visit Mod Website", VisitWebsite, disabled: true);
        _toggleButton = AddButton(buttons, "Enable", OnToggle, disabled: true);
        _saveButton = AddButton(buttons, "Save Configuration", SaveMods, disabled: true);
        _startButton = AddButton(buttons, "Start Mods", StartMods, disabled: true);
        AddButton(buttons, "Download Mods", OpenModIo);   // modmodio.js downloadModsButton
    }

    private Tree BuildModList(Control parent, string heading)
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 4);
        parent.AddChild(col);
        var head = MakeLabel(Localization.Tr(heading), 15);
        col.AddChild(head);
        var tree = new Tree
        {
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(400, 300),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        tree.SetColumnTitle(0, "Folder");
        tree.SetColumnTitle(1, "Label");
        tree.SetColumnTitle(2, "Version");
        tree.SetColumnTitle(3, "Dependencies");
        tree.ItemSelected += () => OnModSelected(tree);
        col.AddChild(tree);
        return tree;
    }

    protected override void OnOpen()
    {
        LoadMods();
        LoadEnabledMods();
        RecomputeCompatibility();
        DisplayLists();

        // 原版:HasIncompatibleMods → 自动弹提示页。我们的"不兼容"= 启用列里依赖不满足。
        if (_enabled.Any(f => !_compat.GetValueOrDefault(f, false)))
            IncompatibleModsDialog.Show(this);
    }

    // ── 数据(原版 loadMods/loadEnabledMods/recomputeCompatibility/validateMods)──

    private void LoadMods()
    {
        _mods.Clear();
        // 扫 binaries/data/mods/*/mod.json + user://mods/*(mod.io 下载落点)。
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir != null)
            ScanModsDir(Path.Combine(binDir, "data", "mods"));
        ScanModsDir(ProjectSettings.GlobalizePath("user://mods"));
    }

    private void ScanModsDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, System.StringComparer.Ordinal))
        {
            string folder = Path.GetFileName(sub);
            string jsonPath = Path.Combine(sub, "mod.json");
            if (!File.Exists(jsonPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                var deps = new List<string>();
                if (root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
                    foreach (var e in d.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String) deps.Add(e.GetString()!);
                // 必填:name/version/label/description(缺任一 → 视为无效目录,跳过)。
                string name = GetStr(root, "name"), version = GetStr(root, "version"),
                    label = GetStr(root, "label"), description = GetStr(root, "description");
                if (name.Length == 0 || version.Length == 0 || label.Length == 0 || description.Length == 0)
                    continue;
                _mods[folder] = new ModEntry(folder, name, version, label,
                    GetStr(root, "url"), description, deps);
            }
            catch { }
        }
    }

    private static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private ModEntry GetMod(string folder) => _mods.GetValueOrDefault(folder, FakeMod);

    private void LoadEnabledMods()
    {
        // 原版:Engine.GetEnabledMods() 过滤 "mod" 与不存在的目录。
        string configured = _cfg.GetEffective("mod.enabledmods");
        if (configured.Length == 0) configured = "mod public";
        _enabled = configured.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f != "mod" && _mods.ContainsKey(f)).ToList();
        _disabled = _mods.Keys.Where(f => !_enabled.Contains(f)).ToList();
    }

    private void RecomputeCompatibility()
    {
        _compat.Clear();
        foreach (var folder in _mods.Keys)
            _compat[folder] = AreDependenciesMet(folder);
    }

    private bool AreDependenciesMet(string folder)
    {
        if (!_mods.ContainsKey(folder)) return false;
        foreach (var dep in GetMod(folder).Dependencies)
            if (!IsDependencyMet(dep))
                return false;
        return true;
    }

    /// <summary>isDependencyMet:依赖 = "name" 或 "name&lt;op&gt;version"(op ∈ = &lt; &gt; &lt;= &gt;=)。
    /// 由任一已启用 mod 满足(name 匹配 + 版本比较)。</summary>
    private bool IsDependencyMet(string dependency)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dependency, @"(<=|>=|=|<|>)");
        string name = m.Success ? dependency[..m.Index] : dependency;
        string? version = m.Success ? dependency[(m.Index + m.Length)..] : null;
        string? op = m.Success ? m.Value : null;
        return _enabled.Any(folder =>
        {
            var mod = GetMod(folder);
            return mod.Name == name && (op == null || VersionSatisfied(mod.Version, op, version!));
        });
    }

    /// <summary>versionSatisfied 端口:'-'/'_' 后忽略,纯数字段;"5.3" &lt; "5.3.0"。</summary>
    public static bool VersionSatisfied(string version1, string op, string version2)
    {
        string[] Split(string v) => v.Split('-', '_')[0].Split('.');
        var l1 = Split(version1);
        var l2 = Split(version2);
        bool eq = op.Contains('='), lt = op.Contains('<'), gt = op.Contains('>');
        for (int i = 0; i < System.Math.Min(l1.Length, l2.Length); i++)
        {
            int diff = (int.TryParse(l1[i], out int a) ? a : 0) - (int.TryParse(l2[i], out int b) ? b : 0);
            if (gt && diff > 0 || lt && diff < 0) return true;
            if (gt && diff < 0 || lt && diff > 0 || eq && diff != 0) return false;
        }
        int ldiff = l1.Length - l2.Length;
        if (ldiff == 0) return eq;
        if (ldiff < 0) return lt;   // 2.3 < 2.3.0
        return gt;
    }

    /// <summary>sortEnabledMods:依赖拓扑(原版比较器排序——f1 依赖 f2 → f2 排前)。</summary>
    private void SortEnabledMods()
    {
        var deps = _enabled.ToDictionary(f => f,
            f => GetMod(f).Dependencies
                .Select(d => System.Text.RegularExpressions.Regex.Split(d, @"<=|>=|=|<|>")[0])
                .ToList());
        _enabled.Sort((f1, f2) =>
            deps[f1].Contains(GetMod(f2).Name) ? 1 :
            deps[f2].Contains(GetMod(f1).Name) ? -1 : 0);
    }

    // ── 显示(原版 displayModList/colorMod)──

    private void DisplayLists()
    {
        DisplayList(_disabledTree, _disabled.Where(FilterDisabled), enabled: false);
        DisplayList(_enabledTree, _enabled.Where(FilterEnabled), enabled: true);
        UpdateButtons();
    }

    private bool MatchesText(string folder)
    {
        string t = _filter.Text;
        if (t.Length == 0) return true;
        var mod = GetMod(folder);
        return folder.Contains(t) || mod.Name.Contains(t) || mod.Label.Contains(t)
            || mod.Url.Contains(t) || mod.Version.Contains(t) || mod.Description.Contains(t)
            || string.Join(" ", mod.Dependencies).Contains(t);
    }

    private bool FilterDisabled(string folder)
    {
        bool match = MatchesText(folder);
        if (_negateFilter.ButtonPressed) match = !match;
        if (!match) return false;
        // 仅兼容过滤只作用于可用列(原版 displayModList 的 modsDisabledList 分支)。
        if (_compatFilter.ButtonPressed && !_compat.GetValueOrDefault(folder, false)) return false;
        return true;
    }

    private bool FilterEnabled(string folder)
    {
        bool match = MatchesText(folder);
        return _negateFilter.ButtonPressed ? !match : match;
    }

    private void DisplayList(Tree tree, IEnumerable<string> folders, bool enabled)
    {
        tree.Clear();
        var root = tree.CreateItem();
        foreach (var folder in folders)
        {
            var mod = GetMod(folder);
            var item = tree.CreateItem(root);
            item.SetText(0, folder);
            item.SetText(1, mod.Label);
            item.SetText(2, mod.Version);
            item.SetText(3, string.Join(" ", mod.Dependencies));
            // getModColor:依赖不满足 → 启用列红/可用列灰;mod.io 刚装 → 绿。
            Color? color = null;
            if (!_compat.GetValueOrDefault(folder, false))
                color = enabled ? new Color(1f, 0.4f, 0.4f) : new Color(0.5f, 0.5f, 0.5f);
            else if (_installedByModIo.Contains(mod.Name))
                color = new Color(0.4f, 1f, 0.4f);
            if (color.HasValue)
                for (int c = 0; c < 4; c++)
                    item.SetCustomColor(c, color.Value);
            item.SetMetadata(0, folder);
        }
    }

    private string? SelectedFolder(Tree tree) => tree.GetSelected()?.GetMetadata(0).AsString();

    private void OnModSelected(Tree tree)
    {
        // 选一列即清空另一列(原版 selectedMod:otherListObject.selected = -1)。
        var other = tree == _enabledTree ? _disabledTree : _enabledTree;
        other.DeselectAll();
        string? folder = SelectedFolder(tree);
        _description.Text = folder != null
            ? GetMod(folder).Description
            : Localization.Tr("No mod has been selected.");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        string? en = SelectedFolder(_enabledTree);
        string? dis = SelectedFolder(_disabledTree);
        bool filtering = _filter.Text.Length > 0;
        _toggleButton.Text = Localization.Tr(dis != null ? "Enable" : "Disable");
        _toggleButton.Disabled = dis != null
            ? !_compat.GetValueOrDefault(dis, false)
            : en == null;
        _upButton.Disabled = _downButton.Disabled = en == null || filtering;
        string? url = en != null ? GetMod(en).Url : dis != null ? GetMod(dis).Url : null;
        _visitButton.Disabled = string.IsNullOrEmpty(url);
        _startButton.Disabled = _enabled.Count == 0;
        // 原版:启用列空 → 红字提示(至少启用 0ad 本体)。
        _message.Text = _enabled.Count == 0 ? Localization.Tr("Enable at least 0ad mod") : "";
    }

    // ── 动作 ──

    private void OnToggle()
    {
        if (SelectedFolder(_disabledTree) is { } dis) EnableMod(dis);
        else if (SelectedFolder(_enabledTree) is { } en) DisableMod(en);
    }

    /// <summary>enableMod:仅兼容时可启用。</summary>
    private void EnableMod(string folder)
    {
        if (!_compat.GetValueOrDefault(folder, false)) return;
        _enabled.Add(folder);
        _disabled.Remove(folder);
        RecomputeCompatibility();
        _saveButton.Disabled = false;
        DisplayLists();
    }

    /// <summary>disableMod:移除 + 级联移除依赖它的已启用 mod(排序后扫描,
    /// 依赖不满足者一并下线,原版 disableMod 的 cascade 循环同款)。</summary>
    private void DisableMod(string folder)
    {
        _enabled.Remove(folder);
        if (_mods.ContainsKey(folder)) _disabled.Add(folder);
        SortEnabledMods();
        for (int i = 0; i < _enabled.Count; i++)
            if (!AreDependenciesMet(_enabled[i]))
            {
                _disabled.Add(_enabled[i]);
                _enabled.RemoveAt(i);
                i--;
            }
        _saveButton.Disabled = false;
        RecomputeCompatibility();
        DisplayLists();
    }

    /// <summary>moveCurrItem:启用列内上下移(加载序);过滤中禁止(原版同款)。</summary>
    private void MoveCurrent(bool up)
    {
        string? folder = SelectedFolder(_enabledTree);
        if (folder == null || _filter.Text.Length > 0) return;
        int idx = _enabled.IndexOf(folder);
        int idx2 = idx + (up ? -1 : 1);
        if (idx < 0 || idx2 < 0 || idx2 >= _enabled.Count) return;
        (_enabled[idx], _enabled[idx2]) = (_enabled[idx2], _enabled[idx]);
        _saveButton.Disabled = false;
        DisplayLists();
    }

    private void VisitWebsite()
    {
        string? url = SelectedFolder(_enabledTree) is { } e ? GetMod(e).Url
            : SelectedFolder(_disabledTree) is { } d ? GetMod(d).Url : null;
        if (string.IsNullOrEmpty(url)) return;
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "http://" + url;
        OS.ShellOpen(url);
    }

    /// <summary>saveMods:排序后写回 mod.enabledmods("mod" 恒前缀,原版同款)。</summary>
    private void SaveMods()
    {
        SortEnabledMods();
        _cfg.SetUserValue("mod.enabledmods", "mod " + string.Join(' ', _enabled));
        _cfg.Save();
        _saveButton.Disabled = true;
    }

    /// <summary>startMods:原版 SetModsAndRestartEngine 换引擎重启;本引擎尚无运行时挂载——
    /// 保存 + 提示(经 msgbox),不重开进程。</summary>
    private void StartMods()
    {
        if (_enabled.Count == 0)
        {
            _message.Text = Localization.Tr("Enable at least 0ad mod");
            return;
        }
        SaveMods();
        // VFS 分层挂载已落地(sim 数据:模板/科技/光环;下一局生效)。
        // 美术资源(godot/assets 导入产物)仍需重启进程重导——原版亦重启。
        GameMsgBox.Show(this, 500, 200,
            Localization.Tr("Mod configuration saved. Data mods apply from the next match; " +
                "art assets need a game restart to re-import."),
            Localization.Tr("Mods"));
    }

    /// <summary>Download Mods → mod.io 页(modmodio.js downloadModsButton:先 Disclaimer 条款门,
    /// 接受才开页;页关后刷新本地 mod 列表)。</summary>
    private void OpenModIo()
    {
        const string page = "Disclaimer";
        if (!TermsManager.IsRegistered(page))
            TermsManager.InitTerms(new Dictionary<string, TermsManager.Spec>
            {
                [page] = new TermsManager.Spec(
                    Title: Localization.Tr("Disclaimer"),
                    File: "modio/Disclaimer.txt",
                    Config: "modio.disclaimer",
                    UrlButtons: new[]
                    {
                        new TermsDialog.UrlButton(Localization.Tr("mod.io Terms"), "https://mod.io/terms"),
                        new TermsDialog.UrlButton(Localization.Tr("mod.io Privacy Policy"), "https://mod.io/privacy"),
                    },
                    Callback: accepted => { if (accepted) ShowModioPanel(); }),
            });
        TermsManager.LoadTermsAcceptance();
        if (TermsManager.IsAccepted(page))
            ShowModioPanel();   // 已接受过 → 直开(原版 loadTermsAcceptance + checkTerms 语义)
        else
            TermsManager.OpenTerms(page, this);
    }

    private void ShowModioPanel()
    {
        var panel = new Modio.ModioPanel();
        panel.OnModDownloaded += name =>
        {
            AddInstalled(name);
            // 新装的 mod 进可用列(重扫目录)。
            LoadMods();
            LoadEnabledMods();
            RecomputeCompatibility();
            DisplayLists();
        };
        AddChild(panel);
        panel.Open();
    }
}
