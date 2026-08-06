using Godot;
using System.Collections.Generic;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

public sealed partial class LobbyUI : CanvasLayer
{
    private LineEdit _portEdit = null!;
    private LineEdit _addressEdit = null!;
    private LineEdit _seedEdit = null!;
    private Label _statusLabel = null!;

    private Panel _mainPanel = null!;
    private Panel _submenuPanel = null!;
    private VBoxContainer _buttonList = null!;
    private VBoxContainer _submenuList = null!;

    private readonly List<MenuButton> _menuButtons = new();

    public event System.Action<int, uint>? OnHostStart;
    public event System.Action<string, int>? OnClientConnect;
    public event System.Action<uint>? OnSinglePlayer;
    public event System.Action? OnTutorialStart;

    // --- Slot-lobby events (Task #10): host edits slots, host starts the game ---
    /// <summary>Host edited a slot (playerId, kind, civ, team). Wired to
    /// <c>MultiplayerController.HostSetSlot</c>.</summary>
    public event System.Action<int, PlayerSlotKind, string, int>? OnSlotEdit;
    /// <summary>Host clicked Start Game. Wired to <c>MultiplayerController.HostStartGame</c>.</summary>
    public event System.Action? OnStartGameRequested;
    /// <summary>Host 改选大厅地图(rel pmp / "random/name" / "" = 默认)。接 HostSetMap。</summary>
    public event System.Action<string>? OnMapEdit;
    /// <summary>任一 MP 面板的 Cancel/Close：返回主菜单（Main 负责关 peer + 切场景）。
    /// 之前只 QueueFree 面板——用户被丢在无菜单的 session 场景里出不去。</summary>
    public event System.Action? OnCancelRequested;

    /// <summary>Lobby civ choices offered in each slot's civ dropdown.</summary>
    private static readonly string[] CivChoices = { "athen", "spart", "gaul" };

    // Per-slot row controls, indexed by slot PlayerId-1. The host's rows are editable (built once
    // in ShowSlotLobby); the client's rows are disabled and repainted by RefreshSlotDisplay.
    private readonly OptionButton?[] _kindOpts = new OptionButton?[PlayerSlotSetupCodec.MaxSlots];
    private readonly OptionButton?[] _civOpts = new OptionButton?[PlayerSlotSetupCodec.MaxSlots];
    private readonly SpinBox?[] _teamSpins = new SpinBox?[PlayerSlotSetupCodec.MaxSlots];
    private bool _lobbyIsHost;
    private List<MapEntry> _lobbyMaps = new();
    private OptionButton? _mapOpt;    // host 的地图下拉
    private Label? _mapLabel;         // client 的只读地图行

    /// <summary>rel 路径 → 显示名("" → "Default (Arcadia)";目录查不到回退原始路径)。</summary>
    private string DisplayNameOf(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return "Default (Arcadia)";
        var m = _lobbyMaps.Find(e => e.RelPath == relPath);
        return m?.DisplayName ?? relPath;
    }

    /// <summary>客户端收到 host 的地图广播 → 刷新只读地图行(host 自己改下拉,不经此)。</summary>
    public void SetMapDisplay(string relPath)
    {
        if (_mapLabel != null) _mapLabel.Text = DisplayNameOf(relPath);
    }

    private sealed record MenuItem(string Caption, string Tooltip, System.Action? OnPress, MenuItem[]? Submenu = null);

    public override void _Ready()
    {
        SetupBackground();
        SetupMainMenu();
        SetupSubmenu();
        SetupLobbyPanel();
    }

    private void SetupBackground()
    {
        var bg = new TextureRect
        {
            Texture = UITheme.TryLoad("res://assets/ui/bg.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.2f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);

        var logo = new TextureRect
        {
            Texture = UITheme.TryLoad("res://assets/ui/logo.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            // 顶部居中 500×200:直写 anchors+offsets(CenterTop+Position 写法会跑偏)。
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -250, OffsetRight = 250, OffsetTop = 30, OffsetBottom = 230,
            GrowHorizontal = Control.GrowDirection.Both,
        };
        AddChild(logo);
    }

    private void SetupMainMenu()
    {
        _mainPanel = new Panel();
        _mainPanel.Position = new Vector2(50, 0);
        _mainPanel.Size = new Vector2(240, 0);
        _mainPanel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _mainPanel.OffsetTop = -2;
        _mainPanel.OffsetBottom = 2;
        _mainPanel.OffsetLeft = 50;
        _mainPanel.OffsetRight = 290;

        var panelBg = new TextureRect
        {
            Texture = UITheme.TryLoad("res://assets/ui/menu_panel.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Tile,
        };
        panelBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _mainPanel.AddChild(panelBg);

        var goldBorder = new ColorRect { Color = new Color(0.90f, 0.745f, 0.314f) };
        goldBorder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        goldBorder.OffsetLeft = 0; goldBorder.OffsetTop = 0;
        goldBorder.OffsetRight = 0; goldBorder.OffsetBottom = 0;
        _mainPanel.AddChild(goldBorder);

        var innerPanel = new ColorRect { Color = new Color(0, 0, 0, 0) };
        innerPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        innerPanel.OffsetLeft = 2; innerPanel.OffsetTop = 2;
        innerPanel.OffsetRight = -2; innerPanel.OffsetBottom = -2;
        _mainPanel.AddChild(innerPanel);

        _buttonList = new VBoxContainer();
        _buttonList.OffsetLeft = 8; _buttonList.OffsetTop = 150;
        _buttonList.OffsetRight = -8; _buttonList.OffsetBottom = -8;
        _buttonList.AddThemeConstantOverride("separation", 2);
        _buttonList.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _mainPanel.AddChild(_buttonList);

        AddChild(_mainPanel);

        BuildMenuItems();
    }

    private void BuildMenuItems()
    {
        var items = new[]
        {
            new MenuItem("Learn to Play", "", null, new MenuItem[]
            {
                new("Tutorial", "Start the introductory tutorial", () => OnTutorialStart?.Invoke()),
                new("Structure Tree", "View unit/building tree", null),
                new("Civilization Overview", "Browse civilizations", null),
            }),
            new MenuItem("Single-player", "", null, new MenuItem[]
            {
                new("Matches", "Start a new game", () => OnSinglePlayer?.Invoke(42)),
                new("Load Game", "Load a saved game", null),
                new("Replays", "Playback previous games", null),
            }),
            new MenuItem("Multiplayer", "", null, new MenuItem[]
            {
                new("Game Lobby", "Join the multiplayer lobby", null),
                new("Host New Game", "Host a multiplayer game", () =>
                {
                    _submenuPanel.Visible = false;
                    ShowLobbyPanel(isHost: true);
                }),
                new("Connect by IP", "Join via IP address", () =>
                {
                    _submenuPanel.Visible = false;
                    ShowLobbyPanel(isHost: false);
                }),
            }),
            new MenuItem("Settings", "", null, new MenuItem[]
            {
                new("Options", "Adjust game settings", null),
                new("Hotkeys", "Configure hotkeys", null),
                new("Language", "Choose language", null),
            }),
            new MenuItem("Scenario Editor", "Open the map editor", null),
            new MenuItem("Credits", "Show credits", null),
            new MenuItem("Exit", "Quit the game", () => GetTree().Quit()),
        };

        foreach (var item in items)
            AddMenuButton(item);
    }

    private void AddMenuButton(MenuItem item)
    {
        var btn = new Button
        {
            Text = item.Caption,
            CustomMinimumSize = new Vector2(0, 28),
            Theme = CreateStoneButtonTheme(),
            TooltipText = item.Tooltip,
        };
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        btn.Pressed += () =>
        {
            if (item.Submenu != null)
                ShowSubmenu(item);
            else
                item.OnPress?.Invoke();
        };

        _buttonList.AddChild(btn);
    }

    private void SetupSubmenu()
    {
        _submenuPanel = new Panel();
        _submenuPanel.Position = new Vector2(290, 0);
        _submenuPanel.Size = new Vector2(220, 0);
        _submenuPanel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _submenuPanel.OffsetLeft = 290;
        _submenuPanel.OffsetRight = 510;
        _submenuPanel.OffsetTop = -2;
        _submenuPanel.OffsetBottom = 2;
        _submenuPanel.Visible = false;

        var panelBg = new TextureRect
        {
            Texture = UITheme.TryLoad("res://assets/ui/menu_panel.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Tile,
        };
        panelBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _submenuPanel.AddChild(panelBg);

        var goldBorder = new ColorRect { Color = new Color(0.90f, 0.745f, 0.314f) };
        goldBorder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _submenuPanel.AddChild(goldBorder);

        var inner = new ColorRect { Color = new Color(0, 0, 0, 0) };
        inner.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        inner.OffsetLeft = 2; inner.OffsetTop = 2;
        inner.OffsetRight = -2; inner.OffsetBottom = -2;
        _submenuPanel.AddChild(inner);

        _submenuList = new VBoxContainer();
        _submenuList.OffsetLeft = 8; _submenuList.OffsetTop = 150;
        _submenuList.OffsetRight = -8; _submenuList.OffsetBottom = -8;
        _submenuList.AddThemeConstantOverride("separation", 2);
        _submenuList.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _submenuPanel.AddChild(_submenuList);

        AddChild(_submenuPanel);
    }

    private void ShowSubmenu(MenuItem parent)
    {
        foreach (var child in _submenuList.GetChildren())
            child.QueueFree();

        if (parent.Submenu == null)
        {
            _submenuPanel.Visible = false;
            return;
        }

        foreach (var sub in parent.Submenu)
        {
            var btn = new Button
            {
                Text = sub.Caption,
                CustomMinimumSize = new Vector2(0, 28),
                Theme = CreateStoneButtonTheme(),
                TooltipText = sub.Tooltip,
                Disabled = sub.OnPress == null,
            };
            btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            if (sub.OnPress != null)
            {
                btn.Pressed += () =>
                {
                    _submenuPanel.Visible = false;
                    sub.OnPress();
                };
            }
            _submenuList.AddChild(btn);
        }

        _submenuPanel.Visible = true;
    }

    private Control? _lobbyPanel;

    private void SetupLobbyPanel()
    {
    }

    /// <summary>从 MainMenu 以明确 MP 意图进入(Host New Game / Connect by IP):
    /// 跳过本场景遗留的旧菜单面板,直显连接/主持表单(对齐原版 gamesetup_mp 入口)。</summary>
    public void EnterMpDirect(bool isHost)
    {
        _mainPanel.Visible = false;
        _submenuPanel.Visible = false;
        ShowLobbyPanel(isHost);
    }

    private LineEdit _nameEdit = null!;

    private void ShowLobbyPanel(bool isHost)
    {
        if (_lobbyPanel != null)
        {
            _lobbyPanel.QueueFree();
            _lobbyPanel = null;
        }

        // 原版 gamesetup_mp.xml:ModernDialog 460×240 居中(size 50%±230 × 50%±120),
        // 标题 "Multiplayer" 顶带,副标题,右对齐标签 + ModernInput 行,
        // 底部 Cancel(左半)/Continue(右半)ModernButtonRed。
        var panel = new Panel();
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 0.5f; panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -230; panel.OffsetRight = 230; panel.OffsetTop = -120; panel.OffsetBottom = 120;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        UITheme.ApplyModernDialog(panel);
        AddChild(panel);
        _lobbyPanel = panel;

        var cfg = GetNode<UserConfig>("/root/UserConfig");

        // 标题带(原版 ModernLabelText,"Multiplayer")。
        var title = new Label
        {
            Text = Localization.Tr("Multiplayer"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", Colors.White);
        title.Position = new Vector2(0, 4);
        title.Size = new Vector2(460, 22);
        panel.AddChild(title);

        // 副标题(原版 pageJoin/pageHost 首行)。
        var subtitle = new Label
        {
            Text = Localization.Tr(isHost ? "Set up your server to host." : "Joining an existing game."),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 14);
        subtitle.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        subtitle.Position = new Vector2(0, 32);
        subtitle.Size = new Vector2(460, 22);
        panel.AddChild(subtitle);

        // 行布局(原版:label x20..50% 右对齐;input x50%+10..100%-20,高 24;行距 40):
        // join = Player Name / Server Hostname or IP / Server Port;
        // host = Player Name / Server Port(+ 我们的 Seed 扩展行,面板加高一行)。
        float y = 66;
        _nameEdit = AddModernRow(panel, Localization.Tr("Player Name") + ":",
            cfg.GetEffective("playername") is { Length: > 0 } n ? n : "Player", y);
        y += 40;
        if (!isHost)
        {
            _addressEdit = AddModernRow(panel, Localization.Tr("Server Hostname or IP") + ":",
                cfg.GetEffective("multiplayerserver") is { Length: > 0 } a ? a : "127.0.0.1", y);
            y += 40;
        }
        _portEdit = AddModernRow(panel, Localization.Tr("Server Port") + ":",
            cfg.GetEffective("multiplayerhosting.port") is { Length: > 0 } p ? p : "25565", y);
        y += 40;
        if (isHost)
        {
            // 同 SP 选图:预填随机种子(原版 gamesetup 行为),手改可锁种子。
            _seedEdit = AddModernRow(panel, Localization.Tr("Seed") + ":",
                ((uint)GD.RandRange(0, 999999)).ToString(), y);
            y += 40;
        }

        // 状态行(原版 hostFeedback,红色)。
        _statusLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.Position = new Vector2(20, y + 4);
        _statusLabel.Size = new Vector2(420, 20);
        panel.AddChild(_statusLabel);

        // 底部按钮(原版:Cancel 左半 18..50%-5,Continue 右半 50%+5..100%-18,高 28)。
        float panelH = isHost ? 280 : 240;   // host 多一行 Seed → 加高 40
        float btnY = panelH - 43;
        var btnCancel = new Button
        {
            Text = Localization.Tr("Cancel"),
            Theme = UITheme.GetRedButtonTheme(),
            Position = new Vector2(18, btnY),
            Size = new Vector2(460f / 2 - 5 - 18, 28),
        };
        btnCancel.Pressed += () =>
        {
            panel.QueueFree();
            _lobbyPanel = null;
            OnCancelRequested?.Invoke();
        };
        panel.AddChild(btnCancel);

        var btnContinue = new Button
        {
            Text = Localization.Tr("Continue"),
            Theme = UITheme.GetRedButtonTheme(),
            Position = new Vector2(460f / 2 + 5, btnY),
            Size = new Vector2(460f / 2 - 5 - 18, 28),
        };
        btnContinue.Pressed += () =>
        {
            // 持久化输入(原版 gamesetup_mp 写回 user config)。
            cfg.SetUserValue("playername", _nameEdit.Text);
            cfg.SetUserValue("multiplayerhosting.port", _portEdit.Text);
            if (!isHost) cfg.SetUserValue("multiplayerserver", _addressEdit.Text);
            cfg.Save();
            if (isHost)
                OnHostStart?.Invoke(int.Parse(_portEdit.Text), uint.Parse(_seedEdit.Text));
            else
                OnClientConnect?.Invoke(_addressEdit.Text, int.Parse(_portEdit.Text));
        };
        panel.AddChild(btnContinue);

        // host 多一行 Seed → 面板加高 40(460×280),重设底边。
        if (isHost)
        {
            panel.OffsetTop = -140; panel.OffsetBottom = 140;
        }
    }

    /// <summary>gamesetup_mp 行:右对齐标签(x20..50%)+ ModernInput(x50%+10..100%-20)。
    /// 面板固定 460 宽,直接写像素。</summary>
    private LineEdit AddModernRow(Panel panel, string label, string defaultValue, float y)
    {
        var lbl = new Label
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector2(20, y),
            Size = new Vector2(460f / 2 - 20, 24),
        };
        lbl.AddThemeFontSizeOverride("font_size", 14);
        lbl.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        panel.AddChild(lbl);

        var edit = new LineEdit
        {
            Text = defaultValue,
            Position = new Vector2(460f / 2 + 10, y),
            Size = new Vector2(460f / 2 - 30, 24),
        };
        UITheme.ApplyModernInput(edit);
        panel.AddChild(edit);
        return edit;
    }

    private LineEdit AddRow(VBoxContainer parent, string label, string defaultValue)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var lbl = new Label { Text = label + ":", CustomMinimumSize = new Vector2(80, 0) };
        row.AddChild(lbl);
        var edit = new LineEdit { Text = defaultValue, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        UITheme.ApplyModernInput(edit);
        row.AddChild(edit);
        parent.AddChild(row);
        return edit;
    }

    /// <summary>Build the slot-config lobby. Called by Main after transport is up:
    /// host (isHost=true, initialSlots = the host's editable slot table) or client
    /// (isHost=false, initialSlots=null — rows built as disabled placeholders, populated by
    /// <see cref="RefreshSlotDisplay"/> when the host's table arrives).
    /// maps = MapCatalog 目录(host 得可编辑下拉;client 得只读行,随广播刷新);
    /// currentMap = 当前地图 rel 路径("" = 默认)。</summary>
    public void ShowSlotLobby(bool isHost, IReadOnlyList<PlayerSlotSetup>? initialSlots,
        List<MapEntry>? maps = null, string currentMap = "")
    {
        _lobbyIsHost = isHost;
        _lobbyMaps = maps ?? new List<MapEntry>();
        if (_lobbyPanel != null) { _lobbyPanel.QueueFree(); _lobbyPanel = null; }
        for (int i = 0; i < _kindOpts.Length; i++)
        {
            _kindOpts[i] = null;
            _civOpts[i] = null;
            _teamSpins[i] = null;
        }

        var panel = new Panel();
        // 居中 520×440:直写 anchors+offsets(Center+Position 写法会跑偏)。
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 0.5f; panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -260; panel.OffsetRight = 260; panel.OffsetTop = -220; panel.OffsetBottom = 220;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        UITheme.ApplyModernDialog(panel);
        AddChild(panel);
        _lobbyPanel = panel;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 20; vbox.OffsetTop = 20;
        vbox.OffsetRight = -20; vbox.OffsetBottom = -20;
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        var title = new Label { Text = Localization.Tr("Game Lobby"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        // 地图行:host = 可编辑下拉(原版 gamesetup 的 map 选择);client = 只读,随广播刷新。
        var mapRow = new HBoxContainer();
        mapRow.AddThemeConstantOverride("separation", 8);
        mapRow.AddChild(new Label { Text = Localization.Tr("Map"), CustomMinimumSize = new Vector2(50, 0) });
        if (isHost)
        {
            _mapOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _mapOpt.AddItem("Default (Arcadia)");
            foreach (var m in _lobbyMaps) _mapOpt.AddItem(m.DisplayName);
            int sel = _lobbyMaps.FindIndex(m => m.RelPath == currentMap);
            _mapOpt.Selected = sel >= 0 ? sel + 1 : 0;
            _mapOpt.ItemSelected += idx =>
                OnMapEdit?.Invoke(idx == 0 ? "" : _lobbyMaps[(int)idx - 1].RelPath);
            mapRow.AddChild(_mapOpt);
        }
        else
        {
            _mapLabel = new Label { Text = DisplayNameOf(currentMap), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            mapRow.AddChild(_mapLabel);
        }
        vbox.AddChild(mapRow);

        // Column header.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(new Label { Text = Localization.Tr("Slot"),  CustomMinimumSize = new Vector2(50, 0) });
        header.AddChild(new Label { Text = Localization.Tr("Kind"),  CustomMinimumSize = new Vector2(110, 0) });
        header.AddChild(new Label { Text = Localization.Tr("Civ"),   CustomMinimumSize = new Vector2(110, 0) });
        header.AddChild(new Label { Text = Localization.Tr("Team"),  CustomMinimumSize = new Vector2(60, 0) });
        vbox.AddChild(header);

        var slots = initialSlots ?? DefaultFourSlots();
        int n = System.Math.Min(slots.Count, PlayerSlotSetupCodec.MaxSlots);
        for (int i = 0; i < n; i++)
            BuildSlotRow(vbox, slots[i], editable: isHost);

        _statusLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(_statusLabel);

        // 底部按钮行(原版 gamesetup_mp 惯例:Cancel/Close 左半,主按钮右半,红石按钮)。
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(btnRow);

        var btnCancel = new Button
        {
            Text = Localization.Tr("Close"),
            Theme = UITheme.GetRedButtonTheme(),
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        btnCancel.Pressed += () =>
        {
            panel.QueueFree();
            _lobbyPanel = null;
            OnCancelRequested?.Invoke();
        };
        btnRow.AddChild(btnCancel);

        if (isHost)
        {
            var btnStart = new Button
            {
                Text = Localization.Tr("Start Game"),
                Theme = UITheme.GetRedButtonTheme(),
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            btnStart.Pressed += () => OnStartGameRequested?.Invoke();
            btnRow.AddChild(btnStart);
        }
        else
        {
            var wait = new Label
            {
                Text = Localization.Tr("Waiting for host to start…"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            wait.AddThemeFontSizeOverride("font_size", 13);
            btnRow.AddChild(wait);
        }
    }

    /// <summary>One slot row. Items are added in enum/array order so the OptionButton index
    /// equals the enum value (Closed/Human/AI → 0/1/2) or the CivChoices index, letting us
    /// read/write via <c>.Selected</c>. Slot 1 (the host) is fully locked — its edits are
    /// rejected by <c>HostSetSlot</c>, so allowing them would leave the UI stale.</summary>
    private void BuildSlotRow(VBoxContainer parent, PlayerSlotSetup slot, bool editable)
    {
        int idx = slot.PlayerId - 1;
        bool locked = slot.PlayerId == 1;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new Label { Text = $"P{slot.PlayerId}", CustomMinimumSize = new Vector2(50, 0) });

        // Kind: Closed(0) / Human(1) / AI(2) — index == enum value.
        var kind = new OptionButton { CustomMinimumSize = new Vector2(110, 0) };
        kind.AddItem("Closed", (int)PlayerSlotKind.Closed);
        kind.AddItem("Human", (int)PlayerSlotKind.Human);
        kind.AddItem("AI", (int)PlayerSlotKind.AI);
        kind.Selected = (int)slot.Kind;
        kind.Disabled = !editable || locked;
        row.AddChild(kind);
        _kindOpts[idx] = kind;

        // Civ: index into CivChoices.
        var civ = new OptionButton { CustomMinimumSize = new Vector2(110, 0) };
        foreach (var c in CivChoices) civ.AddItem(c);
        int civSel = System.Array.IndexOf(CivChoices, slot.Civ);
        civ.Selected = civSel >= 0 ? civSel : 0;
        civ.Disabled = !editable || locked;
        row.AddChild(civ);
        _civOpts[idx] = civ;

        // Team: -1 = FFA, 0+ = allied team.
        var team = new SpinBox
        {
            MinValue = -1, MaxValue = 3, Value = slot.Team,
            CustomMinimumSize = new Vector2(60, 0),
            Editable = editable && !locked,
        };
        row.AddChild(team);
        _teamSpins[idx] = team;

        if (editable && !locked)
        {
            // Read fresh control values at emit time (closures capture slot.PlayerId, a constant).
            void Emit() => OnSlotEdit?.Invoke(
                slot.PlayerId,
                (PlayerSlotKind)kind.Selected,
                CivChoices[civ.Selected],
                (int)team.Value);
            kind.ItemSelected += _ => Emit();
            civ.ItemSelected += _ => Emit();
            team.ValueChanged += _ => Emit();
        }

        parent.AddChild(row);
    }

    /// <summary>Client-only: repaint the disabled slot rows from the host's broadcast table.
    /// The host is the source of truth (editable rows) and never repaints — repainting would
    /// rebuild mid-edit and lose input focus.</summary>
    public void RefreshSlotDisplay(IReadOnlyList<PlayerSlotSetup> slots)
    {
        if (_lobbyIsHost) return;
        int n = System.Math.Min(slots.Count, PlayerSlotSetupCodec.MaxSlots);
        for (int i = 0; i < n; i++)
        {
            var s = slots[i];
            int idx = s.PlayerId - 1;
            if (idx < 0 || idx >= _kindOpts.Length) continue;
            if (_kindOpts[idx] is { } k) k.Selected = (int)s.Kind;
            if (_civOpts[idx] is { } c)
            {
                int sel = System.Array.IndexOf(CivChoices, s.Civ);
                c.Selected = sel >= 0 ? sel : 0;
            }
            if (_teamSpins[idx] is { } t) t.Value = s.Team;
        }
    }

    /// <summary>Placeholder 4-slot table (all Closed) for a client before the host's first
    /// lobby broadcast arrives — gives RefreshSlotDisplay rows to populate.</summary>
    private static IReadOnlyList<PlayerSlotSetup> DefaultFourSlots()
    {
        var list = new List<PlayerSlotSetup>(PlayerSlotSetupCodec.MaxSlots);
        for (int i = 1; i <= PlayerSlotSetupCodec.MaxSlots; i++)
            list.Add(new PlayerSlotSetup { PlayerId = i, Kind = PlayerSlotKind.Closed });
        return list;
    }

    private static Theme CreateStoneButtonTheme()
    {
        var theme = new Theme();

        var btnNormal = MakeStoneStyle("res://assets/ui/btn_normal.png", Colors.White);
        var btnHover = MakeStoneStyle("res://assets/ui/btn_hover.png", new Color(1.1f, 1.05f, 1.0f));
        var btnPressed = MakeStoneStyle("res://assets/ui/btn_hover.png", new Color(0.85f, 0.8f, 0.7f));
        var btnDisabled = MakeStoneStyle("res://assets/ui/btn_normal.png", new Color(0.5f, 0.5f, 0.5f, 0.6f));

        theme.SetStylebox("normal", "Button", btnNormal);
        theme.SetStylebox("hover", "Button", btnHover);
        theme.SetStylebox("pressed", "Button", btnPressed);
        theme.SetStylebox("disabled", "Button", btnDisabled);
        theme.SetColor("font_color", "Button", Colors.White);
        theme.SetColor("font_hover_color", "Button", Colors.White);
        theme.SetColor("font_disabled_color", "Button", new Color(0.8f, 0.8f, 0.8f, 0.6f));
        theme.SetFontSize("font_size", "Button", 14);

        return theme;
    }

    private static StyleBox MakeStoneStyle(string texPath, Color modulate)
    {
        var tex = UITheme.TryLoad(texPath);
        if (tex == null)
        {
            var flat = new StyleBoxFlat { BgColor = new Color(0.35f, 0.25f, 0.15f) * modulate };
            flat.SetContentMarginAll(6);
            return flat;
        }
        var style = new StyleBoxTexture { Texture = tex, ModulateColor = modulate };
        style.SetContentMarginAll(6);
        style.SetTextureMarginAll(4);
        style.AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch;
        style.AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch;
        return style;
    }

    public void SetStatus(string msg) => _statusLabel.Text = msg;
    public new void Hide() => Visible = false;
    public new void Show() => Visible = true;
}
