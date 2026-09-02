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

    // 收敛注记(2026-08):此类曾是 MainMenu.tscn 存在前的假主菜单(SetupMainMenu/BuildMenuItems
    // 一堆 action=null 死项)。真主菜单在 MainMenu.cs;本类只保留 MP 连接表单 + 槽位大厅
    // (gamesetup_mp 端口)。Mode=Lobby(裸跑 session)由 Main._Ready 弹回 MainMenu.tscn。

    public event System.Action<int, uint>? OnHostStart;
    /// <summary>client 连接(地址/端口/观战勾选;原版 join 无观战框——
    /// observer 是我们的扩展,对齐原版 lateobservers 语义)。</summary>
    public event System.Action<string, int, bool>? OnClientConnect;

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

    /// <summary>Lobby civ choices offered in each slot's civ dropdown(全部 15 文明,
    /// 与 SP gamesetup 同表)。</summary>
    private static readonly (string Code, string Name)[] CivChoices =
    {
        ("athen", "Athenians"), ("brit", "Britons"), ("cart", "Carthaginians"),
        ("gaul", "Gauls"), ("germ", "Germans"), ("han", "Han"), ("iber", "Iberians"),
        ("kush", "Kushites"), ("mace", "Macedonians"), ("maur", "Mauryas"),
        ("ptol", "Ptolemies"), ("rome", "Romans"), ("sele", "Seleucids"),
        ("spart", "Spartans"), ("achae", "Achaemenids"),
    };

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

    /// <summary>客户端收到 host 的地图广播 → 刷新地图选择/预览/描述
    /// (host 自己改下拉时本端也经此刷新展示)。</summary>
    public void SetMapDisplay(string relPath)
    {
        if (_mapLabel != null) _mapLabel.Text = DisplayNameOf(relPath);
        var m = string.IsNullOrEmpty(relPath)
            ? null
            : _lobbyMaps.Find(e => e.RelPath == relPath);
        if (m == null && _lobbyFiltered.Count > 0)
            m = _lobbyFiltered.Find(e => e.RelPath == relPath);
        if (_nameLabel2 != null) _nameLabel2.Text = m?.DisplayName ?? DisplayNameOf(relPath);
        if (_descLabel2 != null) _descLabel2.Text = m?.Description ?? "";
        if (_preview2 != null) SetMapTexture(m);
        if (m != null && _lobbyMapSelectOpt != null)
        {
            int idx = _lobbyFiltered.IndexOf(m);
            if (idx >= 0 && _lobbyMapSelectOpt.Selected != idx)
                _lobbyMapSelectOpt.Selected = idx;
        }
    }

    public override void _Ready()
    {
        SetupBackground();
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


    private Control? _lobbyPanel;

    /// <summary>从 MainMenu 以明确 MP 意图进入(Host New Game / Connect by IP):
    /// 跳过本场景遗留的旧菜单面板,直显连接/主持表单(对齐原版 gamesetup_mp 入口)。</summary>
    public void EnterMpDirect(bool isHost)
    {
        ShowLobbyPanel(isHost);
    }

    private LineEdit _nameEdit = null!;
    private CheckBox? _observerBox;

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
        // 观战勾选(原版 observer 加入;不占玩家槽、全图视野、不出令)。
        if (!isHost)
        {
            _observerBox = new CheckBox { Text = Localization.Tr("Join as observer") };
            UITheme.ApplyCheckboxIcons(_observerBox);
            _observerBox.Position = new Vector2(460f / 2 + 10, y);
            _observerBox.Size = new Vector2(460f / 2 - 30, 24);
            panel.AddChild(_observerBox);
            y += 40;
        }
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
                OnClientConnect?.Invoke(_addressEdit.Text, int.Parse(_portEdit.Text),
                    _observerBox?.ButtonPressed ?? false);
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
    /// <summary>gamesetup_mp 风格的全屏对局设置页（对齐原版 Multiplayer Match Setup:
    /// 左列 = 玩家面板+地图描述,右列 = 预览+设置选项卡(Map/Player/Game Type),
    /// 底部 = 聊天栏 + Cancel/Start）。host 可编辑设置并广播;client 全部只读,
    /// 随 host 广播刷新。maps = MapCatalog 目录;currentMap = 当前地图 rel。</summary>
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
        panel.AnchorLeft = 0.02f; panel.AnchorRight = 0.98f;
        panel.AnchorTop = 0.03f; panel.AnchorBottom = 0.97f;
        panel.OffsetLeft = 0; panel.OffsetRight = 0; panel.OffsetTop = 0; panel.OffsetBottom = 0;
        panel.Theme = UITheme.GetTheme();
        AddChild(panel);
        _lobbyPanel = panel;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 12; vbox.OffsetTop = 12;
        vbox.OffsetRight = -12; vbox.OffsetBottom = -12;
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        var title = new Label { Text = Localization.Tr("Multiplayer Match Setup"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        // ── 主体两列(对齐原版:左 = 玩家面板+描述;右 = 预览+设置选项卡)──
        var cols = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        cols.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(cols);

        // ══ 左列:玩家面板 + 地图名/描述 ══
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 8);
        cols.AddChild(left);

        var playersBox = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        playersBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.12f, 0.11f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.72f, 0.60f, 0.35f),
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ContentMarginLeft = 6, ContentMarginRight = 6,
        });
        left.AddChild(playersBox);
        var playersInner = new VBoxContainer();
        playersInner.AddThemeConstantOverride("separation", 4);
        playersBox.AddChild(playersInner);
        var playersHeader = new Label { Text = Localization.Tr("Players") };
        playersHeader.AddThemeFontSizeOverride("font_size", 16);
        playersInner.AddChild(playersHeader);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        void AddHead(string text, float minW)
        {
            var l = new Label { Text = Localization.Tr(text), CustomMinimumSize = new Vector2(minW, 0) };
            l.AddThemeFontSizeOverride("font_size", 11);
            l.Modulate = new Color(1, 1, 1, 0.6f);
            header.AddChild(l);
        }
        AddHead("Player Name", 150);
        AddHead("Kind", 110);
        AddHead("Civilization", 150);
        AddHead("Team", 60);
        playersInner.AddChild(header);

        _slotRowsBox = new VBoxContainer();
        _slotRowsBox.AddThemeConstantOverride("separation", 2);
        playersInner.AddChild(_slotRowsBox);

        var slots = initialSlots ?? DefaultFourSlots();
        int n = System.Math.Min(slots.Count, PlayerSlotSetupCodec.MaxSlots);
        for (int i = 0; i < n; i++)
            BuildSlotRow(_slotRowsBox, slots[i], editable: isHost);

        _nameLabel2 = new Label { Text = "" };
        _nameLabel2.AddThemeFontSizeOverride("font_size", 14);
        left.AddChild(_nameLabel2);
        _descLabel2 = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _descLabel2.AddThemeFontSizeOverride("font_size", 12);
        left.AddChild(_descLabel2);

        // ══ 右列:预览 + 设置区(内容左置 + 右缘纵向页签条,对齐 A28 gamesetup)══
        var right = new VBoxContainer
        {
            // 右列加宽:内容区(原 TabContainer 页) + 右侧 150px 纵向页签条。
            CustomMinimumSize = new Vector2(560, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        right.AddThemeConstantOverride("separation", 8);
        cols.AddChild(right);

        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.02f, 0.02f),
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = new Color(0, 0, 0),
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ContentMarginLeft = 6, ContentMarginRight = 6,
        });
        _preview2 = new TextureRect
        {
            CustomMinimumSize = new Vector2(360, 200),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        frame.AddChild(_preview2);
        right.AddChild(frame);

        _lobbyTabPages = new Control[]
        {
            BuildLobbyMapTab(isHost), BuildLobbyPlayerTab(isHost), BuildLobbyGameTypeTab(isHost),
        };
        var settingsRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        settingsRow.AddThemeConstantOverride("separation", 8);
        // PanelContainer 宿主:只排可见页,最小尺寸随当前页(裸 Control 宿主最小尺寸为 0,
        // 会把外层布局压塌——SP MapPickerPanel 同款教训)。
        var pageHost = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        pageHost.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        for (int i = 0; i < _lobbyTabPages.Length; i++)
        {
            _lobbyTabPages[i].Visible = i == 0;
            pageHost.AddChild(_lobbyTabPages[i]);
        }
        settingsRow.AddChild(pageHost);
        _lobbyTabStrip = new VerticalTabStrip(new[]
            { Localization.Tr("Map"), Localization.Tr("Player"), Localization.Tr("Game Type") })
        {
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        _lobbyTabStrip.TabSelected += idx =>
        {
            for (int i = 0; i < _lobbyTabPages.Length; i++)
                _lobbyTabPages[i].Visible = i == idx;
        };
        settingsRow.AddChild(_lobbyTabStrip);
        right.AddChild(settingsRow);

        // ── 底部:聊天栏(左) + 状态/按钮(右)──
        var bottomRow = new HBoxContainer { CustomMinimumSize = new Vector2(0, 110) };
        bottomRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(bottomRow);

        // 聊天(gamesetup_mp 惯例:消息列表 + 输入行)
        var chatBox = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        chatBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.09f, 0.08f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            ContentMarginTop = 4, ContentMarginBottom = 4,
            ContentMarginLeft = 6, ContentMarginRight = 6,
        });
        bottomRow.AddChild(chatBox);
        var chatV = new VBoxContainer();
        chatBox.AddChild(chatV);
        var chatScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _chatList = new VBoxContainer();
        chatScroll.AddChild(_chatList);
        chatV.AddChild(chatScroll);
        var chatInputRow = new HBoxContainer();
        chatInputRow.AddThemeConstantOverride("separation", 6);
        _chatEdit = new LineEdit
        {
            PlaceholderText = Localization.Tr("Chat message…"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _chatEdit.TextSubmitted += text => { if (text.Length > 0) { OnChatSend?.Invoke(text); _chatEdit.Clear(); } };
        chatInputRow.AddChild(_chatEdit);
        var sendBtn = new Button { Text = Localization.Tr("Send"), CustomMinimumSize = new Vector2(70, 0) };
        sendBtn.Pressed += () =>
        {
            if (_chatEdit.Text.Length > 0) { OnChatSend?.Invoke(_chatEdit.Text); _chatEdit.Clear(); }
        };
        chatInputRow.AddChild(sendBtn);
        chatV.AddChild(chatInputRow);

        var rightBtns = new VBoxContainer();
        rightBtns.AddThemeConstantOverride("separation", 6);
        bottomRow.AddChild(rightBtns);

        _statusLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.3f));
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        rightBtns.AddChild(_statusLabel);

        var btnCancel = new Button
        {
            Text = Localization.Tr("Cancel"),
            CustomMinimumSize = new Vector2(110, 30),
        };
        btnCancel.Pressed += () =>
        {
            panel.QueueFree();
            _lobbyPanel = null;
            OnCancelRequested?.Invoke();
        };
        rightBtns.AddChild(btnCancel);

        if (isHost)
        {
            var btnStart = new Button
            {
                Text = Localization.Tr("Start Game"),
                CustomMinimumSize = new Vector2(150, 30),
                TooltipText = "Start the match for all players.",
            };
            btnStart.Pressed += () => OnStartGameRequested?.Invoke();
            rightBtns.AddChild(btnStart);
        }
        else
        {
            var wait = new Label
            {
                Text = Localization.Tr("Waiting for host to start…"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            wait.AddThemeFontSizeOverride("font_size", 13);
            rightBtns.AddChild(wait);
        }

        // 初始填充地图下拉(random 默认视图)并选中当前图/首项(顺带刷预览/描述)。
        RefillLobbyMaps();
        if (!string.IsNullOrEmpty(currentMap))
        {
            int idx = _lobbyFiltered.FindIndex(e => e.RelPath == currentMap);
            if (idx >= 0)
            {
                _lobbyMapSelectOpt.Selected = idx;
                SetMapDisplay(currentMap);
            }
        }
        EmitOptionsIfHost();
    }

    // ── Map 页签(host 可编辑;client 全只读)──
    private Control BuildLobbyMapTab(bool editable)
    {
        var page = new VBoxContainer { Name = Localization.Tr("Map") };
        page.AddThemeConstantOverride("separation", 4);

        _lobbyMapTypeOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var f in new[] { "Random", "Skirmish", "Scenario" })
            _lobbyMapTypeOpt.AddItem(Localization.Tr(f));
        _lobbyMapTypeOpt.Selected = 0;
        _lobbyMapTypeOpt.Disabled = !editable;
        _lobbyMapTypeOpt.ItemSelected += _ => { RefillLobbyMaps(); EmitOptionsIfHost(); };
        page.AddChild(MakeLobbyRow("Map Type", _lobbyMapTypeOpt));

        _lobbyMapSelectOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _lobbyMapSelectOpt.Disabled = !editable;
        _lobbyMapSelectOpt.ItemSelected += idx =>
        {
            var m = _lobbyFiltered[(int)idx];
            OnMapEdit?.Invoke(m.RelPath);
            SetMapDisplay(m.RelPath);
        };
        page.AddChild(MakeLobbyRow("Map Selection", _lobbyMapSelectOpt));

        _lobbyMapSizeOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (name, tiles) in LobbyMapSizes)
            _lobbyMapSizeOpt.AddItem($"{Localization.Tr(name)} ({tiles})");
        _lobbyMapSizeOpt.Selected = 2;
        _lobbyMapSizeOpt.Disabled = !editable;
        _lobbyMapSizeOpt.ItemSelected += _ => EmitOptionsIfHost();
        page.AddChild(MakeLobbyRow("Map Size", _lobbyMapSizeOpt));

        _lobbyPlacementOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (_, name) in LobbyPlacements) _lobbyPlacementOpt.AddItem(Localization.Tr(name));
        _lobbyPlacementOpt.Selected = 0;
        _lobbyPlacementOpt.Disabled = !editable;
        _lobbyPlacementOpt.ItemSelected += _ => EmitOptionsIfHost();
        page.AddChild(MakeLobbyRow("Player Placement", _lobbyPlacementOpt));

        _nomadBox = AddLobbyCheck(page, "Nomad", editable, _ => EmitOptionsIfHost());
        _treasuresBox = AddLobbyCheck(page, "Treasures", editable, _ => EmitOptionsIfHost(), pressed: true);
        _exploredBox = AddLobbyCheck(page, "Explored Map", editable, _ => EmitOptionsIfHost());
        _revealedBox = AddLobbyCheck(page, "Revealed Map", editable, _ => EmitOptionsIfHost());
        _alliedViewBox = AddLobbyCheck(page, "Allied View", editable, _ => EmitOptionsIfHost(), pressed: true);
        return page;
    }

    // ── Player 页签 ──
    private Control BuildLobbyPlayerTab(bool editable)
    {
        var page = new VBoxContainer { Name = Localization.Tr("Player") };
        page.AddThemeConstantOverride("separation", 4);

        _popCapTypeOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var t in new[] { "Player Population", "Team Population", "World Population" })
            _popCapTypeOpt.AddItem(Localization.Tr(t));
        _popCapTypeOpt.Selected = 0;
        _popCapTypeOpt.Disabled = !editable;
        _popCapTypeOpt.ItemSelected += _ => { UpdateLobbyPopCapLabel(); EmitOptionsIfHost(); };
        page.AddChild(MakeLobbyRow("Population Cap Type", _popCapTypeOpt));

        var capRow = new HBoxContainer();
        capRow.AddThemeConstantOverride("separation", 8);
        var capLabel = new Label
        {
            Text = Localization.Tr("Player Population Cap"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        capLabel.AddThemeFontSizeOverride("font_size", 13);
        capLabel.Modulate = new Color(1, 1, 1, 0.75f);
        capRow.AddChild(capLabel);
        _popCapSlider = new HSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0.01, Value = 0.5,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Choose the population cap (rightmost = Unlimited).",
        };
        if (!editable) _popCapSlider.MouseFilter = Control.MouseFilterEnum.Ignore;
        _popCapSlider.ValueChanged += _ => { UpdateLobbyPopCapLabel(); EmitOptionsIfHost(); };
        capRow.AddChild(_popCapSlider);
        _popCapValue = new Label { Text = "300", CustomMinimumSize = new Vector2(64, 0) };
        _popCapValue.AddThemeFontSizeOverride("font_size", 13);
        capRow.AddChild(_popCapValue);
        page.AddChild(capRow);

        _startResOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (name, amount) in LobbyStartResources)
            _startResOpt.AddItem($"{Localization.Tr(name)} ({amount})");
        _startResOpt.Selected = 1;
        _startResOpt.Disabled = !editable;
        _startResOpt.ItemSelected += _ => EmitOptionsIfHost();
        page.AddChild(MakeLobbyRow("Starting Resources", _startResOpt));

        _spiesBox = AddLobbyCheck(page, "Spies", editable, _ => EmitOptionsIfHost());
        _cheatsBox = AddLobbyCheck(page, "Cheats", editable, _ => EmitOptionsIfHost());
        return page;
    }

    // ── Game Type 页签 ──
    private Control BuildLobbyGameTypeTab(bool editable)
    {
        var page = new VBoxContainer { Name = Localization.Tr("Game Type") };
        page.AddThemeConstantOverride("separation", 4);

        _victoryBoxes.Clear();
        foreach (var (id, name, def) in LobbyVictoryChoices)
        {
            var box = AddLobbyCheck(page, name, editable, _ => EmitOptionsIfHost(), pressed: def);
            _victoryBoxes[id] = box;
        }

        _gameSpeedOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var s in LobbyGameSpeeds) _gameSpeedOpt.AddItem($"{s:0.##}×");
        _gameSpeedOpt.Selected = 4;
        _gameSpeedOpt.Disabled = !editable;
        _gameSpeedOpt.ItemSelected += _ => EmitOptionsIfHost();
        page.AddChild(MakeLobbyRow("Game Speed", _gameSpeedOpt));

        var cfRow = new HBoxContainer();
        cfRow.AddThemeConstantOverride("separation", 8);
        var cfLabel = new Label
        {
            Text = Localization.Tr("Ceasefire"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cfLabel.AddThemeFontSizeOverride("font_size", 13);
        cfLabel.Modulate = new Color(1, 1, 1, 0.75f);
        cfRow.AddChild(cfLabel);
        _ceasefireSlider = new HSlider
        {
            MinValue = 0, MaxValue = 45, Step = 1, Value = 0,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        if (!editable) _ceasefireSlider.MouseFilter = Control.MouseFilterEnum.Ignore;
        _ceasefireSlider.ValueChanged += v =>
        {
            _ceasefireValue.Text = v < 0.5 ? Localization.Tr("Off") : $"{(int)v} min";
            EmitOptionsIfHost();
        };
        cfRow.AddChild(_ceasefireSlider);
        _ceasefireValue = new Label { Text = Localization.Tr("Off"), CustomMinimumSize = new Vector2(64, 0) };
        _ceasefireValue.AddThemeFontSizeOverride("font_size", 13);
        cfRow.AddChild(_ceasefireValue);
        page.AddChild(cfRow);

        _lockedTeamsBox = AddLobbyCheck(page, "Locked Teams", editable, _ => EmitOptionsIfHost());
        _lastManBox = AddLobbyCheck(page, "Last Man Standing", editable, _ => EmitOptionsIfHost());
        return page;
    }

    private CheckBox AddLobbyCheck(Control parent, string text, bool editable,
        System.Action<bool>? onToggled = null, bool pressed = false)
    {
        var box = new CheckBox
        {
            Text = Localization.Tr(text),
            Disabled = !editable,
            ButtonPressed = pressed,
        };
        UITheme.ApplyCheckboxIcons(box);
        if (onToggled != null)
            box.Toggled += pressed => onToggled(pressed);
        parent.AddChild(box);
        return box;
    }

    private static Control MakeLobbyRow(string label, Control widget)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var l = new Label
        {
            Text = Localization.Tr(label),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.Modulate = new Color(1, 1, 1, 0.75f);
        row.AddChild(l);
        row.AddChild(widget);
        return row;
    }

    private void UpdateLobbyPopCapLabel()
    {
        double v = _popCapSlider.Value;
        if (v >= 0.995)
        {
            _popCapValue.Text = Localization.Tr("Unlimited");
            return;
        }
        double factor = LobbyPopCapFactors[_popCapTypeOpt.Selected];
        int cap = (int)System.Math.Round((1 / (1 - v) + 28 * v / (1 + 5 * v)) * factor / 6 / 10) * 10;
        _popCapValue.Text = cap.ToString();
    }

    /// <summary>host 编辑 → 汇总当前选项并广播(host-only,no-op on client)。</summary>
    private void EmitOptionsIfHost()
    {
        if (!_lobbyIsHost || OnOptionsEdit == null) return;
        var o = ReadLobbyOptions();
        OnOptionsEdit.Invoke(o);
    }

    private MultiplayerController.MpLobbyOptions ReadLobbyOptions()
    {
        var o = new MultiplayerController.MpLobbyOptions
        {
            MapSize = LobbyMapSizes[_lobbyMapSizeOpt.Selected].Tiles,
            PlayerPlacement = LobbyPlacements[_lobbyPlacementOpt.Selected].Id,
            StartingResources = LobbyStartResources[_startResOpt.Selected].Amount,
            PopulationCapTypeIdx = _popCapTypeOpt.Selected,
            GameSpeed = LobbyGameSpeeds[_gameSpeedOpt.Selected],
            CeasefireMinutes = (int)_ceasefireSlider.Value,
            Nomad = _nomadBox.ButtonPressed,
            Treasures = _treasuresBox.ButtonPressed,
            ExploredMap = _exploredBox.ButtonPressed,
            RevealedMap = _revealedBox.ButtonPressed,
            AlliedView = _alliedViewBox.ButtonPressed,
            LockedTeams = _lockedTeamsBox.ButtonPressed,
            Cheats = _cheatsBox.ButtonPressed,
            Spies = _spiesBox.ButtonPressed,
            LastManStanding = _lastManBox.ButtonPressed,
        };
        double v = _popCapSlider.Value;
        if (v < 0.995)
        {
            double factor = LobbyPopCapFactors[_popCapTypeOpt.Selected];
            o.PopulationCap = (int)System.Math.Round((1 / (1 - v) + 28 * v / (1 + 5 * v)) * factor / 6 / 10) * 10;
        }
        else
            o.PopulationCap = 0;   // Unlimited(0 = 不改,模板默认)
        o.VictoryConditions = _victoryBoxes
            .Where(kv => kv.Value.ButtonPressed).Select(kv => kv.Key).ToList();
        return o;
    }

    /// <summary>client:host 选项广播到达 → 刷新全部只读控件(host 不回刷,避免打断编辑)。</summary>
    public void RefreshOptions(MultiplayerController.MpLobbyOptions o)
    {
        if (_lobbyIsHost) return;
        int sizeIdx = System.Array.FindIndex(LobbyMapSizes, s => s.Tiles == o.MapSize);
        if (sizeIdx >= 0) _lobbyMapSizeOpt.Selected = sizeIdx;
        int plIdx = System.Array.FindIndex(LobbyPlacements, p => p.Id == o.PlayerPlacement);
        if (plIdx >= 0) _lobbyPlacementOpt.Selected = plIdx;
        _popCapTypeOpt.Selected = o.PopulationCapTypeIdx;
        int resIdx = System.Array.FindIndex(LobbyStartResources, r => r.Amount == o.StartingResources);
        if (resIdx >= 0) _startResOpt.Selected = resIdx;
        int speedIdx = System.Array.FindIndex(LobbyGameSpeeds, s => System.Math.Abs(s - o.GameSpeed) < 0.001f);
        if (speedIdx >= 0) _gameSpeedOpt.Selected = speedIdx;
        _ceasefireSlider.Value = o.CeasefireMinutes;
        _ceasefireValue.Text = o.CeasefireMinutes == 0
            ? Localization.Tr("Off") : $"{o.CeasefireMinutes} min";
        if (o.PopulationCap > 0)
        {
            double factor = LobbyPopCapFactors[_popCapTypeOpt.Selected];
            // 反推滑条位置(近似,仅供显示)
            double target = o.PopulationCap / (factor / 6.0);
            double vv = System.Math.Min(0.99, System.Math.Max(0.0,
                (target - 2) / (target + 4)));
            _popCapSlider.Value = vv;
            _popCapValue.Text = o.PopulationCap.ToString();
        }
        else
        {
            _popCapSlider.Value = 1;
            _popCapValue.Text = Localization.Tr("Unlimited");
        }
        _nomadBox.ButtonPressed = o.Nomad;
        _treasuresBox.ButtonPressed = o.Treasures;
        _exploredBox.ButtonPressed = o.ExploredMap;
        _revealedBox.ButtonPressed = o.RevealedMap;
        _alliedViewBox.ButtonPressed = o.AlliedView;
        _lockedTeamsBox.ButtonPressed = o.LockedTeams;
        _cheatsBox.ButtonPressed = o.Cheats;
        _spiesBox.ButtonPressed = o.Spies;
        _lastManBox.ButtonPressed = o.LastManStanding;
        foreach (var (id, _, _) in LobbyVictoryChoices)
            if (_victoryBoxes.TryGetValue(id, out var box))
                box.ButtonPressed = o.VictoryConditions.Contains(id);
    }

    /// <summary>dev 截图钩子:切到指定大厅页签(0=Map/1=Player/2=Game Type)。</summary>
    public void DevSelectTab(int idx)
    {
        if (_lobbyTabStrip == null) return;
        _lobbyTabStrip.Select(idx);
    }

    /// <summary>dev 截图钩子:预选大厅 Map Type(0=random/1=skirmish/2=scenario)——
    /// 程序化 Selected 不发 ItemSelected,须手动 RefillLobbyMaps。</summary>
    public void DevShowMapType(int idx)
    {
        if (_lobbyMapTypeOpt == null) return;
        _lobbyMapTypeOpt.Selected = idx;
        RefillLobbyMaps();
    }

    private void RefillLobbyMaps()
    {
        string type = _lobbyMapTypeOpt.Selected switch
        {
            0 => "random",
            1 => "skirmish",
            2 => "scenario",
            _ => "random",
        };
        _lobbyFiltered = _lobbyMaps.Where(m => m.MapType == type).ToList();
        _lobbyMapSelectOpt.Clear();
        foreach (var m in _lobbyFiltered)
            _lobbyMapSelectOpt.AddItem(m.DisplayName);
        if (_lobbyFiltered.Count > 0)
        {
            _lobbyMapSelectOpt.Selected = 0;
            SetMapDisplay(_lobbyFiltered[0].RelPath);
        }
    }

    private void SetMapTexture(MapEntry? m)
    {
        _preview2.Texture = null;
        if (m?.PreviewPath == null) return;
        var img = Image.LoadFromFile(m.PreviewPath);
        if (img != null)
            _preview2.Texture = ImageTexture.CreateFromImage(img);
    }

    // ── 控件字段(MP 大厅专用)──
    private VBoxContainer _slotRowsBox = null!;
    private TextureRect _preview2 = null!;
    private Label _nameLabel2 = null!;
    private Label _descLabel2 = null!;
    private Control[] _lobbyTabPages = System.Array.Empty<Control>();
    private VerticalTabStrip _lobbyTabStrip = null!;
    private OptionButton _lobbyMapTypeOpt = null!;
    private OptionButton _lobbyMapSelectOpt = null!;
    private OptionButton _lobbyMapSizeOpt = null!;
    private OptionButton _lobbyPlacementOpt = null!;
    private CheckBox _nomadBox = null!;
    private CheckBox _treasuresBox = null!;
    private CheckBox _exploredBox = null!;
    private CheckBox _revealedBox = null!;
    private CheckBox _alliedViewBox = null!;
    private OptionButton _popCapTypeOpt = null!;
    private HSlider _popCapSlider = null!;
    private Label _popCapValue = null!;
    private OptionButton _startResOpt = null!;
    private CheckBox _spiesBox = null!;
    private CheckBox _cheatsBox = null!;
    private readonly Dictionary<string, CheckBox> _victoryBoxes = new();
    private OptionButton _gameSpeedOpt = null!;
    private HSlider _ceasefireSlider = null!;
    private Label _ceasefireValue = null!;
    private CheckBox _lockedTeamsBox = null!;
    private CheckBox _lastManBox = null!;
    private VBoxContainer _chatList = null!;
    private LineEdit _chatEdit = null!;
    private List<MapEntry> _lobbyFiltered = new();

    // 选项表(与 SP gamesetup 同一份上游数据)。
    private static readonly (string Name, int Tiles)[] LobbyMapSizes =
    {
        ("Tiny", 128), ("Small", 192), ("Normal", 256), ("Medium", 320),
        ("Large", 384), ("Very Large", 448), ("Giant", 512),
    };
    private static readonly (string Id, string Name)[] LobbyPlacements =
    {
        ("circle", "Circle"), ("river", "River"), ("groupedLines", "Grouped Lines"),
        ("randomGroup", "Random Group"), ("stronghold", "Stronghold"),
    };
    private static readonly int[] LobbyPopCapFactors = { 300, 400, 600 };
    private static readonly (string Name, int Amount)[] LobbyStartResources =
    {
        ("Very Low", 100), ("Low", 300), ("Medium", 500),
        ("High", 1000), ("Very High", 3000), ("Deathmatch", 50000),
    };
    private static readonly float[] LobbyGameSpeeds =
        { 0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 5f, 10f, 20f };
    private static readonly (string Id, string Name, bool Default)[] LobbyVictoryChoices =
    {
        ("conquest", "Conquest", true),
        ("wonder", "Wonder Victory", false),
        ("capture_the_relic", "Capture the Relic", false),
        ("regicide", "Regicide", false),
        ("conquest_civic_centers", "Conquest Civic Centers", false),
        ("conquest_structures", "Conquest Structures", false),
        ("conquest_units", "Conquest Units", false),
    };

    /// <summary>聊天追加一行("P{id}: text";AppendChat 由 OnChatReceived 驱动)。</summary>
    public void AppendChat(int playerId, string text)
    {
        if (_chatList == null) return;
        var line = new Label { Text = $"P{playerId}: {text}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        line.AddThemeFontSizeOverride("font_size", 12);
        _chatList.AddChild(line);
    }

    /// <summary>host 的选项编辑事件(ReadLobbyOptions → HostSetOptions)。</summary>
    public event System.Action<MultiplayerController.MpLobbyOptions>? OnOptionsEdit;
    /// <summary>聊天发送事件(Main → _mp.SendChat)。</summary>
    public event System.Action<string>? OnChatSend;

    /// <summary>One slot row. Items are added in enum/array order so the OptionButton index
    /// equals the enum value (Closed/Human/AI → 0/1/2) or the CivChoices index, letting us
    /// read/write via <c>.Selected</c>. Slot 1 (the host) is fully locked — its edits are
    /// rejected by <c>HostSetSlot</c>, so allowing them would leave the UI stale.</summary>
    /// <summary>One slot row(gamesetup_mp 玩家行:整行玩家色底;kind=Open/AI/Closed,
    /// 已被 peer 认领的 Human 槽显示 Peer N 且锁定;slot 1 = host(You)全锁)。
    /// Kind 下拉:Open(1)/AI(2)/Closed(0),index == PlayerSlotKind 值。</summary>
    private void BuildSlotRow(VBoxContainer parent, PlayerSlotSetup slot, bool editable)
    {
        int idx = slot.PlayerId - 1;
        bool locked = slot.PlayerId == 1;
        bool claimed = _peerLookup != null && _peerLookup(slot.PlayerId);
        bool rowLocked = locked || claimed;

        var card = new PanelContainer();
        var rowColor = LobbyRowColors[System.Math.Min(idx, LobbyRowColors.Length - 1)];
        card.CustomMinimumSize = new Vector2(0, 32);
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = rowColor,
            ContentMarginTop = 2, ContentMarginBottom = 2,
            ContentMarginLeft = 8, ContentMarginRight = 8,
        });
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        card.AddChild(row);

        string nameText = slot.PlayerId == 1
            ? Localization.Tr("You (host)")
            : claimed
                ? Localization.Tr("Peer") + " " + (_peerNameLookup?.Invoke(slot.PlayerId)?.ToString() ?? "")
                : $"Player {slot.PlayerId}";
        var nameLabel = new Label
        {
            Text = nameText,
            CustomMinimumSize = new Vector2(150, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(nameLabel);

        // Kind: Closed(0) / Open-Human(1) / AI(2) — index == enum value。
        var kind = new OptionButton { CustomMinimumSize = new Vector2(110, 0) };
        kind.AddItem(Localization.Tr("Closed"), (int)PlayerSlotKind.Closed);
        kind.AddItem(Localization.Tr("Open"), (int)PlayerSlotKind.Human);
        kind.AddItem(Localization.Tr("AI"), (int)PlayerSlotKind.AI);
        kind.Selected = (int)slot.Kind;
        kind.Disabled = !editable || rowLocked;
        row.AddChild(kind);
        _kindOpts[idx] = kind;

        // Civ: index into CivChoices(+1 位 0=Random)。
        var civ = new OptionButton
        {
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        civ.AddItem(Localization.Tr("Random"));
        foreach (var (_, name) in CivChoices) civ.AddItem(name);
        int civSel = System.Array.FindIndex(CivChoices, c => c.Code == slot.Civ);
        civ.Selected = civSel >= 0 ? civSel + 1 : 0;
        civ.Disabled = !editable || rowLocked;
        row.AddChild(civ);
        _civOpts[idx] = civ;

        // Team: -1 = FFA, 0+ = allied team。
        var team = new SpinBox
        {
            MinValue = -1, MaxValue = 3, Value = slot.Team,
            CustomMinimumSize = new Vector2(60, 0),
            Editable = editable && !rowLocked,
        };
        row.AddChild(team);
        _teamSpins[idx] = team;

        if (editable && !rowLocked)
        {
            // Read fresh control values at emit time (closures capture slot.PlayerId, a constant).
            void Emit() => OnSlotEdit?.Invoke(
                slot.PlayerId,
                (PlayerSlotKind)kind.Selected,
                civ.Selected > 0 ? CivChoices[civ.Selected - 1].Code : CivChoices[GD.RandRange(0, CivChoices.Length - 1)].Code,
                (int)team.Value);
            kind.ItemSelected += _ => Emit();
            civ.ItemSelected += _ => Emit();
            team.ValueChanged += _ => Emit();
        }

        parent.AddChild(card);
    }

    /// <summary>槽位认领查询(Main 注入,指向 _mp.IsSlotClaimedByPeer)。</summary>
    public System.Func<int, bool>? _peerLookup;
    /// <summary>槽位 → peer 显示名查询(Main 注入,指向 _mp.PeerIdOfSlot)。</summary>
    public System.Func<int, int?>? _peerNameLookup;

    /// <summary>大厅玩家行底色(玩家色暗化,同 SP gamesetup)。</summary>
    private static readonly Color[] LobbyRowColors =
    {
        new(0.082f, 0.216f, 0.584f),
        new(0.588f, 0.078f, 0.078f),
        new(0.337f, 0.706f, 0.121f),
        new(0.906f, 0.784f, 0.020f),
        new(0.588f, 0.078f, 0.588f),
        new(0.078f, 0.627f, 0.784f),
        new(0.902f, 0.471f, 0.078f),
        new(0.784f, 0.314f, 0.471f),
    };

    /// <summary>Client-only: repaint the disabled slot rows from the host's broadcast table.
    /// The host is the source of truth (editable rows) and never repaints — repainting would
    /// rebuild mid-edit and lose input focus.</summary>
    public void RefreshSlotDisplay(IReadOnlyList<PlayerSlotSetup> slots)
    {
        if (_lobbyIsHost) return;
        // client 只读——整表重建(行名(Peer N/You)也随认领状态刷新)。
        if (_slotRowsBox == null) return;
        foreach (var c in _slotRowsBox.GetChildren()) c.QueueFree();
        for (int i = 0; i < _kindOpts.Length; i++)
        {
            _kindOpts[i] = null;
            _civOpts[i] = null;
            _teamSpins[i] = null;
        }
        int n = System.Math.Min(slots.Count, PlayerSlotSetupCodec.MaxSlots);
        for (int i = 0; i < n; i++)
            BuildSlotRow(_slotRowsBox, slots[i], editable: false);
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
