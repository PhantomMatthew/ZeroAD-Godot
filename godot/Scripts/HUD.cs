using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed partial class HUD : CanvasLayer
{
    private readonly SimBridge _sim;
    private readonly Main _main;

    private TextureRect _topBar = null!;
    private readonly List<(TextureRect icon, Label count)> _resourceSlots = new();
    private Label _selectionLabel = null!;
    private Minimap _minimap = null!;
    private Panel _bottomBar = null!;

    private static readonly string[] _resNames = { "wood", "food", "stone", "metal" };
    private static readonly string[] _resLabels = { "Wood", "Food", "Stone", "Metal" };

    public HUD(SimBridge sim, Main main) { _sim = sim; _main = main; }

    public override void _Ready()
    {
        SetupTopBar();
        SetupBottomBar();
        SetupMinimap();
    }

    private void SetupTopBar()
    {
        _topBar = new TextureRect
        {
            Texture = LoadTex("top_bar.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        _topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBar.OffsetBottom = 36;
        AddChild(_topBar);

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        hbox.OffsetLeft = 4; hbox.OffsetTop = 2;
        hbox.OffsetBottom = 36;
        hbox.AddThemeConstantOverride("separation", 4);
        _topBar.AddChild(hbox);

        for (int i = 0; i < _resNames.Length; i++)
        {
            var slot = CreateResourceSlot(_resNames[i]);
            _resourceSlots.Add(slot);
            hbox.AddChild(slot.icon);
        }

        var popSlot = CreateResourceSlot("population");
        _resourceSlots.Add(popSlot);
        hbox.AddChild(popSlot.icon);

        _selectionLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _selectionLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _selectionLabel.OffsetLeft = 300; _selectionLabel.OffsetTop = 8;
        _selectionLabel.OffsetRight = -10; _selectionLabel.OffsetBottom = 30;
        _selectionLabel.AddThemeFontSizeOverride("font_size", 14);
        _selectionLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.8f));
        _selectionLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _selectionLabel.AddThemeConstantOverride("outline_size", 3);
        _topBar.AddChild(_selectionLabel);
    }

    private (TextureRect icon, Label count) CreateResourceSlot(string resName)
    {
        var container = new TextureRect
        {
            Texture = LoadTex($"icon_{resName}.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            CustomMinimumSize = new Vector2(32, 32),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };

        var count = new Label
        {
            Text = "0",
            OffsetLeft = 34, OffsetTop = 6,
            OffsetRight = 100, OffsetBottom = 28,
        };
        count.AddThemeFontSizeOverride("font_size", 14);
        count.AddThemeColorOverride("font_color", Colors.White);
        count.AddThemeColorOverride("font_outline_color", Colors.Black);
        count.AddThemeConstantOverride("outline_size", 3);
        container.AddChild(count);

        return (container, count);
    }

    private void SetupBottomBar()
    {
        _bottomBar = new Panel();
        _bottomBar.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _bottomBar.OffsetTop = -44;
        _bottomBar.CustomMinimumSize = new Vector2(0, 44);

        var stoneBg = new TextureRect
        {
            Texture = LoadTex("session_panel.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Tile,
        };
        stoneBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bottomBar.AddChild(stoneBg);

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        hbox.OffsetLeft = 10; hbox.OffsetBottom = -4;
        hbox.OffsetTop = -40;
        hbox.AddThemeConstantOverride("separation", 6);
        _bottomBar.AddChild(hbox);

        AddButton(hbox, "Train Villager\n(50F)", () => _main.TrainVillager());
        AddButton(hbox, "Train Soldier\n(80F 20M)", () => _main.TrainSoldier());
        AddButton(hbox, "Build House\n(50W)", () => _main.EnterBuildMode("House"));

        if (_main.IsTutorial)
        {
            AddButton(hbox, "Skirmisher\n(Shift)", () => _main.TrainSkirmisher(true));
            AddButton(hbox, "Storehouse", () => _main.EnterBuildMode("Storehouse"));
            AddButton(hbox, "Farmstead", () => _main.EnterBuildMode("Farmstead"));
            AddButton(hbox, "Field", () => _main.EnterBuildMode("Field"));
            AddButton(hbox, "Barracks", () => _main.EnterBuildMode("Barracks"));
            AddButton(hbox, "Outpost", () => _main.EnterBuildMode("Outpost"));
            AddButton(hbox, "Tower", () => _main.EnterBuildMode("Tower"));
            AddButton(hbox, "Forge", () => _main.EnterBuildMode("Forge"));
            AddButton(hbox, "Market", () => _main.EnterBuildMode("Market"));
            AddButton(hbox, "Temple", () => _main.EnterBuildMode("Temple"));
            AddButton(hbox, "Arsenal", () => _main.EnterBuildMode("Arsenal"));
            AddButton(hbox, "Town Phase", () => _main.ResearchTech("phase_town_generic"));
            AddButton(hbox, "City Phase", () => _main.ResearchTech("phase_city_generic"));
            AddButton(hbox, "Infantry Training", () => _main.ResearchTech("infantry_attack"));
            AddButton(hbox, "Battering Ram", () => _main.TrainUnit("units/spart/siege_ram"));
        }

        var help = new Label { Text = "  LMB=select  RMB=move/gather/attack  ESC=cancel" };
        help.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
        help.AddThemeFontSizeOverride("font_size", 12);
        hbox.AddChild(help);

        AddChild(_bottomBar);
    }

    private void AddButton(HBoxContainer parent, string text, System.Action onPressed)
    {
        var btn = new Button
        {
            Text = text,
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(120, 34),
        };
        btn.Pressed += onPressed;
        parent.AddChild(btn);
    }

    private void SetupMinimap()
    {
        var minimapContainer = new Control();
        minimapContainer.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        minimapContainer.OffsetLeft = -152; minimapContainer.OffsetTop = -196;
        minimapContainer.OffsetBottom = -8; minimapContainer.OffsetRight = -8;
        AddChild(minimapContainer);

        var ring = new TextureRect
        {
            Texture = LoadTex("minimap_ring.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        minimapContainer.AddChild(ring);

        _minimap = new Minimap(_sim, _main);
        _minimap.Position = new Vector2(12, 12);
        _minimap.Size = new Vector2(120, 120);
        minimapContainer.AddChild(_minimap);
    }

    public override void _Process(double delta)
    {
        var player = _sim.GetPlayer();
        if (player != null)
        {
            _resourceSlots[0].count.Text = player.Wood.ToString();
            _resourceSlots[1].count.Text = player.Food.ToString();
            _resourceSlots[2].count.Text = player.Stone.ToString();
            _resourceSlots[3].count.Text = player.Metal.ToString();
            _resourceSlots[4].count.Text = $"{player.Population}/{player.PopulationLimit}";
        }

        var selected = _main.SelectedEntities;
        if (selected.Count == 0)
        {
            _selectionLabel.Text = "";
        }
        else
        {
            string name = "Units";
            foreach (var eid in selected)
            {
                var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
                if (identity != null) { name = identity.Name; break; }
            }
            _selectionLabel.Text = $"{selected.Count}x {name}";
        }
    }

    private static Texture2D? LoadTex(string file)
    {
        string path = ProjectSettings.GlobalizePath($"res://assets/ui/{file}");
        if (!System.IO.File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }
}
