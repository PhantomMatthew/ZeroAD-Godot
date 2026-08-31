using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Godot.Campaigns;

namespace ZeroAD.Godot;

// CampaignMenuPanel — 战役主菜单(原版 campaigns/default_menu/CampaignMenu.js):
// 关卡列表(按模板 Order 排序;状态列 Completed/绿色 Available/锁定关卡名灰显),
// 右侧关卡名/描述/预览;Start 开局(写 GameLaunchConfig 战役上下文 → session);
// Saved Games → 读档页;Back → 保存 run 关闭。
public sealed partial class CampaignMenuPanel : ModalPanelBase
{
    private readonly CampaignRun _run;
    private readonly string? _dataRoot;

    private Tree _tree = null!;
    private Label _name = null!;
    private Label _desc = null!;
    private TextureRect _preview = null!;
    private Button _startButton = null!;

    private List<CampaignLevel> _levels = new();
    private CampaignLevel? _selected;

    public CampaignMenuPanel(CampaignRun run, string? dataRoot)
    {
        _run = run;
        _dataRoot = dataRoot;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell(_run.GetLabel(), 880);

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        content.AddChild(split);

        // 左:关卡列表(原版 levelSelection COList:name + status 两列)。
        _tree = new Tree
        {
            Columns = 2,
            HideRoot = true,
            CustomMinimumSize = new Vector2(360, 400),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _tree.SetColumnExpand(0, true);
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, 90);
        _tree.ItemSelected += OnSelected;
        _tree.ItemActivated += StartScenario;   // 双击 = Start(原版同款)
        split.AddChild(_tree);

        // 右:关卡详情(scenarioName/levelPreviewBox/scenarioDesc)。
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);

        _name = MakeLabel("", 18);
        right.AddChild(_name);

        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.02f, 0.02f),
            BorderColor = Colors.Black,
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
        });
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(400, 225),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        frame.AddChild(_preview);
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

        // 底部按钮(原版 backToMain / savedGamesButton / startButton)。
        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Back to Main Menu", GoBack);
        AddButton(buttons, "Saved Games", OpenSavedGames);
        _startButton = AddButton(buttons, "Start", StartScenario, disabled: true);
    }

    protected override void OnOpen()
    {
        // shouldShowLevel:ShowUnavailable=true 全显(锁定灰显);否则只显可玩的。
        var levels = new List<CampaignLevel>();
        foreach (var kv in _run.Template!.Levels)
            if (_run.Template.ShowUnavailable || _run.MeetsRequirements(kv.Value))
                levels.Add(kv.Value);
        // displayLevelsList 按模板 Order 排序(未列名的排尾,按 id 字典序稳定)。
        var order = _run.Template.Order;
        levels.Sort((a, b) =>
        {
            int ia = order.IndexOf(a.Id), ib = order.IndexOf(b.Id);
            if (ia < 0) ia = int.MaxValue;
            if (ib < 0) ib = int.MaxValue;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a.Id, b.Id);
        });
        _levels = levels;
        _selected = null;
        Populate();
        UpdateDetails();
    }

    private void Populate()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var level in _levels)
        {
            var item = _tree.CreateItem(root);
            string name = LevelName(level);
            string status = "";
            if (_run.IsCompleted(level.Id))
                status = Localization.Tr("Completed");
            else if (_run.MeetsRequirements(level))
                status = Localization.Tr("Available");
            else
                item.SetCustomColor(0, new Color(0.55f, 0.55f, 0.55f));   // 锁定灰显
            item.SetText(0, name);
            item.SetText(1, status);
            if (_run.MeetsRequirements(level) && !_run.IsCompleted(level.Id))
                item.SetCustomColor(1, new Color(0.35f, 0.85f, 0.35f));   // Available 绿(原版 coloredText)
            item.SetMetadata(0, level.Id);
        }
    }

    private void OnSelected()
    {
        string? id = _tree.GetSelected()?.GetMetadata(0).AsString();
        _selected = _levels.FirstOrDefault(l => l.Id == id);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        bool canStart = _selected != null && _run.MeetsRequirements(_selected);
        _startButton.Disabled = !canStart;
        if (_selected == null)
        {
            _name.Text = "";
            _desc.Text = "";
            _preview.Texture = null;
            return;
        }
        _name.Text = LevelName(_selected);
        // 描述:模板覆盖 > 地图 XML 描述(原版 getLevelDescription 同款回退)。
        _desc.Text = Localization.Tr(_selected.Description ?? MapDescription(_selected));
        _preview.Texture = LoadPreview(_selected);
    }

    private string LevelName(CampaignLevel level) =>
        level.Name != null ? Localization.Tr(level.Name) : MapNameOf(level);

    // ── 地图元数据回退(原版 MapCache:getTranslatableMapName/getTranslatedMapDescription/
    // getMapPreview 从地图 XML script settings 读)──

    private string MapXmlPath(CampaignLevel level) =>
        Path.Combine(_dataRoot ?? "", "maps", level.Map.Replace('/', Path.DirectorySeparatorChar));

    private string MapNameOf(CampaignLevel level)
    {
        var (name, _) = ReadMapXml(level);
        return name ?? Path.GetFileNameWithoutExtension(level.Map);
    }

    private string MapDescription(CampaignLevel level)
    {
        var (_, desc) = ReadMapXml(level);
        return desc ?? "";
    }

    private (string? name, string? desc) ReadMapXml(CampaignLevel level)
    {
        if (_dataRoot == null) return (null, null);
        string path = MapXmlPath(level);
        if (!File.Exists(path)) return (null, null);
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(path);
            var settings = doc.DocumentElement?["ScriptSettings"];
            return (settings?["Name"]?.InnerText, settings?["Description"]?.InnerText);
        }
        catch { return (null, null); }
    }

    private Texture2D? LoadPreview(CampaignLevel level)
    {
        if (_dataRoot == null) return null;
        // 模板 Preview 覆盖 > 地图 XML Preview > nopreview。
        string? rel = level.Preview;
        if (rel == null)
        {
            string path = MapXmlPath(level);
            if (File.Exists(path))
                try
                {
                    var doc = new System.Xml.XmlDocument();
                    doc.Load(path);
                    rel = doc.DocumentElement?["ScriptSettings"]?["Preview"]?.InnerText;
                }
                catch { }
        }
        rel ??= "session/icons/mappreview/nopreview.png";
        var img = Image.LoadFromFile(Path.Combine(_dataRoot, "art", "textures", "ui",
            rel.Replace('/', Path.DirectorySeparatorChar)));
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }

    // ── 动作 ──

    /// <summary>startScenario:meetsRequirements 门 → 写 launch config(含战役上下文
    /// campaignData)→ session。原版 CheatsEnabled=true(战役关卡允许作弊)。</summary>
    private void StartScenario()
    {
        if (_selected == null || !_run.MeetsRequirements(_selected)) return;
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.MapPath = "maps/" + Path.ChangeExtension(_selected.Map, ".pmp").Replace('\\', '/');
        cfg.Seed = 42;
        cfg.Cheats = true;
        cfg.CampaignRunFile = _run.Filename;
        cfg.CampaignLevelId = _selected.Id;
        // 教程战役首关(introductory_tutorial)走 Tutorial 模式——引导引擎随图启动
        // (原版靠地图自带触发脚本;我们的 TutorialEngine 是等价物)。
        cfg.Mode = cfg.MapPath.EndsWith("tutorials/introductory_tutorial.pmp", System.StringComparison.Ordinal)
            ? GameLaunchConfig.LaunchMode.Tutorial
            : GameLaunchConfig.LaunchMode.SinglePlayer;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    /// <summary>savedGamesButton → 读档页(原版限本 run 的存档;我们复用通用读档页)。</summary>
    private void OpenSavedGames()
    {
        var panel = new LoadGamePanel(layer: 62);
        GetParent().AddChild(panel);
        panel.Open();
    }

    /// <summary>backToMain:run.save() 后关闭(原版回 page_pregame;此处面板即主菜单叠层)。</summary>
    private void GoBack()
    {
        _run.Save();
        Close();
        QueueFree();
    }
}
