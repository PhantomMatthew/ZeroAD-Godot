using Godot;
using System.Collections.Generic;

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
            CustomMinimumSize = new Vector2(500, 200),
        };
        logo.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        logo.Position = new Vector2(-250, 30);
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

    private void ShowLobbyPanel(bool isHost)
    {
        if (_lobbyPanel != null)
        {
            _lobbyPanel.QueueFree();
            _lobbyPanel = null;
        }

        var panel = new Panel { Theme = UITheme.GetTheme() };
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.Position = new Vector2(-200, -150);
        panel.Size = new Vector2(400, 300);
        AddChild(panel);
        _lobbyPanel = panel;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 20; vbox.OffsetTop = 20;
        vbox.OffsetRight = -20; vbox.OffsetBottom = -20;
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        var title = new Label
        {
            Text = isHost ? "Host Game" : "Join Game",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        if (isHost)
        {
            _seedEdit = AddRow(vbox, "Seed", "42");
        }
        _portEdit = AddRow(vbox, "Port", "25565");
        if (!isHost)
        {
            _addressEdit = AddRow(vbox, "Address", "127.0.0.1");
        }

        var btnStart = new Button
        {
            Text = isHost ? "Start Hosting" : "Connect",
            Theme = UITheme.GetTheme(),
        };
        btnStart.Pressed += () =>
        {
            if (isHost)
                OnHostStart?.Invoke(int.Parse(_portEdit.Text), uint.Parse(_seedEdit.Text));
            else
                OnClientConnect?.Invoke(_addressEdit.Text, int.Parse(_portEdit.Text));
        };
        vbox.AddChild(btnStart);

        var btnCancel = new Button { Text = "Cancel", Theme = UITheme.GetTheme() };
        btnCancel.Pressed += () =>
        {
            panel.QueueFree();
            _lobbyPanel = null;
        };
        vbox.AddChild(btnCancel);

        _statusLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        vbox.AddChild(_statusLabel);
    }

    private LineEdit AddRow(VBoxContainer parent, string label, string defaultValue)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var lbl = new Label { Text = label + ":", CustomMinimumSize = new Vector2(80, 0) };
        row.AddChild(lbl);
        var edit = new LineEdit { Text = defaultValue, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(edit);
        parent.AddChild(row);
        return edit;
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
