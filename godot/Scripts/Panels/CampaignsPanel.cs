using System.Collections.Generic;
using System.IO;
using Godot;
using ZeroAD.Godot.Campaigns;

namespace ZeroAD.Godot;

// CampaignsPanel — 战役选择页(原版 campaigns/setup/CampaignSetupPage.js:可用战役列表 +
// 标题/描述/配图 + Start Campaign(→ new_modal 命名)/ Load Campaign(→ load_modal 管理已有 run)。
// 数据源 = binaries campaigns/*.json;MainMenu "Single-player → New Campaign" 打开。
public sealed partial class CampaignsPanel : ModalPanelBase
{
    private ItemList _list = null!;
    private Label _title = null!;
    private Label _desc = null!;
    private TextureRect _image = null!;
    private Button _startButton = null!;
    private Button _loadButton = null!;

    private IReadOnlyList<CampaignTemplate> _templates = System.Array.Empty<CampaignTemplate>();
    private CampaignTemplate? _selected;
    private string? _dataRoot;

    public override void _Ready()
    {
        var (content, _) = BuildShell("Campaigns", 860);

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        content.AddChild(split);

        // 左:战役列表(原版 campaignSelection COList)。
        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(280, 380),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _list.ItemSelected += idx => { _selected = _templates[(int)idx]; UpdateDetails(); };
        _list.ItemActivated += idx => { _selected = _templates[(int)idx]; StartNew(); };
        split.AddChild(_list);

        // 右:标题/配图/描述(原版 campaignTitle/campaignImage/campaignDesc)。
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);

        _title = MakeLabel("No campaign selected.", 18);
        right.AddChild(_title);

        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.02f, 0.02f),
            BorderColor = Colors.Black,
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
        });
        _image = new TextureRect
        {
            CustomMinimumSize = new Vector2(400, 225),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        frame.AddChild(_image);
        right.AddChild(frame);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _desc = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _desc.AddThemeFontSizeOverride("font_size", 14);
        scroll.AddChild(_desc);
        right.AddChild(scroll);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Main Menu", Close);
        _loadButton = AddButton(buttons, "Load Campaign", OpenLoadModal);
        _startButton = AddButton(buttons, "Start Campaign", StartNew, disabled: true);
    }

    protected override void OnOpen()
    {
        _dataRoot = FindDataRoot();
        _templates = CampaignTemplate.GetAvailableTemplates(_dataRoot);
        _selected = null;
        _list.Clear();
        foreach (var t in _templates)
            _list.AddItem(Localization.Tr(t.Name));
        _loadButton.Disabled = CampaignRun.ListRuns(_dataRoot).Count == 0;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        _startButton.Disabled = _selected == null;
        if (_selected == null)
        {
            _title.Text = Localization.Tr("No campaign selected.");
            _desc.Text = "";
            _image.Texture = null;
            return;
        }
        _title.Text = Localization.Tr(_selected.Name);
        _desc.Text = Localization.Tr(_selected.Description);
        _image.Texture = LoadArtTexture(_selected.Image);
    }

    /// <summary>战役配图(Image 字段相对 art/;缺图回退 nopreview)。</summary>
    private Texture2D? LoadArtTexture(string? rel)
    {
        if (_dataRoot == null) return null;
        string path = Path.Combine(_dataRoot, "art", "textures", "ui",
            (rel ?? "session/icons/mappreview/nopreview.png").Replace('/', Path.DirectorySeparatorChar));
        var img = Image.LoadFromFile(path);
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }

    /// <summary>Start Campaign → new_modal(命名 run)→ 创建并置当前 → 开战役主菜单。</summary>
    private void StartNew()
    {
        if (_selected == null) return;
        var modal = new NewCampaignModal(_selected, _dataRoot);
        modal.OnRunCreated += run =>
        {
            Close();
            var menu = new CampaignMenuPanel(run, _dataRoot);
            GetParent().AddChild(menu);
            menu.Open();
        };
        AddChild(modal);
        modal.Open();
    }

    private void OpenLoadModal()
    {
        var modal = new CampaignLoadModal(_dataRoot);
        modal.OnRunLoaded += run =>
        {
            Close();
            var menu = new CampaignMenuPanel(run, _dataRoot);
            GetParent().AddChild(menu);
            menu.Open();
        };
        // 删除/加载后刷新 Load 按钮可用态。
        modal.TreeExited += () => _loadButton.Disabled = CampaignRun.ListRuns(_dataRoot).Count == 0;
        AddChild(modal);
        modal.Open();
    }

    /// <summary>binaries 数据根(data/mods/public)定位。</summary>
    private static string? FindDataRoot()
    {
        string? binDir = StoneButtonStyle.FindBinariesDir();
        return binDir == null ? null : Path.Combine(binDir, "data", "mods", "public");
    }
}
