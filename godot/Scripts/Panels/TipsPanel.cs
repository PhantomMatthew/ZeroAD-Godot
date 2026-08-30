using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>小贴士(原版 gui/reference/tips/page_tips.xml + TipsPage.js):
/// 按 texts/*.txt 的标题+正文逐条滚动展示(原版 tipScrolling=true 的
/// 连续翻页;左/右键翻页)。ModalPanelBase 外壳(模态不暂停 sim)。</summary>
public sealed partial class TipsPanel : ModalPanelBase
{
    private readonly List<(string Title, string Body, string ImagePath)> _tips = new();
    private int _index;
    private Label _titleLabel = null!;
    private RichTextLabel _bodyLabel = null!;
    private TextureRect _imageRect = null!;
    private Label _counterLabel = null!;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Tips and Tricks", 600);
        status.Text = "";

        // 加载 tips(原版 TipDisplay 的 tipfiles.json 索引 + texts/*.txt)。
        LoadTips();

        var topRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddChild(topRow);

        var prevBtn = new Button { Text = "◀", CustomMinimumSize = new Vector2(40, 0) };
        prevBtn.Pressed += () => Navigate(-1);
        topRow.AddChild(prevBtn);

        _counterLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        topRow.AddChild(_counterLabel);

        var nextBtn = new Button { Text = "▶", CustomMinimumSize = new Vector2(40, 0) };
        nextBtn.Pressed += () => Navigate(1);
        topRow.AddChild(nextBtn);

        _titleLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _titleLabel.AddThemeFontSizeOverride("font_size", 16);
        content.AddChild(_titleLabel);

        var bodyRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        bodyRow.AddThemeConstantOverride("separation", 12);
        content.AddChild(bodyRow);

        _imageRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(200, 200),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false,
        };
        bodyRow.AddChild(_imageRect);

        _bodyLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _bodyLabel.AddThemeFontSizeOverride("normal_font_size", 14);
        bodyRow.AddChild(_bodyLabel);

        AddButton(content, "Close", Close, minWidth: 160);
    }

    protected override void OnOpen()
    {
        if (_tips.Count > 0) ShowTip(0);
    }

    /// <summary>加载 tips(原版 TipDisplay:tipfiles.json 索引 texts/*.txt;
    /// 每文件首行 = 标题,余下 = 正文,图 = texts/<name>.png)。</summary>
    private void LoadTips()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string dir = Path.GetFullPath(Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "gui", "reference", "tips", "texts"));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.txt"))
            {
                var lines = File.ReadAllLines(file);
                if (lines.Length == 0) continue;
                string title = lines[0].Trim();
                string body = string.Join("\n", lines[1..]).Trim();
                // 同名图(texts/<name>.png;原版 tipImage)。
                string imagePath = Path.ChangeExtension(file, ".png");
                string image = File.Exists(imagePath) ? imagePath : "";
                _tips.Add((title, body, image));
            }
            return;
        }
    }

    private void Navigate(int delta)
    {
        if (_tips.Count == 0) return;
        ShowTip((_index + delta + _tips.Count) % _tips.Count);
    }

    private void ShowTip(int index)
    {
        _index = index;
        var tip = _tips[index];
        _titleLabel.Text = tip.Title;
        _bodyLabel.Text = tip.Body;
        _counterLabel.Text = $"{index + 1} / {_tips.Count}";
        if (tip.ImagePath.Length > 0)
        {
            var img = Image.LoadFromFile(tip.ImagePath);
            if (img != null)
            {
                _imageRect.Texture = ImageTexture.CreateFromImage(img);
                _imageRect.Visible = true;
            }
        }
        else
        {
            _imageRect.Visible = false;
        }
    }
}
