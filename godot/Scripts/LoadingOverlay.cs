using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace ZeroAD.Godot;

// 加载等待页(对齐原版 gui/page_loading.xml + gui/loading/loading.xml 布局):
// 全屏暗底 + 顶部居中进度条(50%±256, y 4..36,带百分比文本)+ 地图标题(LargeTitleText)
// + 中央提示卡(对齐 TipDisplay:左图右文,50%±452 × 50%±196)。
// 提示数据端口自原版 reference/tips:tipfiles.json 按 loadingScreenOccurrence_SP 加权选类,
// 随机取一条(textFile 首行=标题其余=正文,imageFiles 随机一图,图在 art/textures/ui/tips/)。
// 底部名言条(QuoteDisplay)留 backlog。
public sealed partial class LoadingOverlay : CanvasLayer
{
    private readonly ProgressBar _bar;

    public LoadingOverlay(string title)
    {
        Layer = 100; // above everything

        // 全屏暗底(ModernWindow 风格)。
        var bg = new ColorRect { Color = new Color(0.05f, 0.045f, 0.04f, 1f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(bg);

        // 顶部居中进度条(50%±256, y 4..36)。
        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = true,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -256, OffsetRight = 256, OffsetTop = 4, OffsetBottom = 36,
        };
        AddChild(_bar);

        // 地图标题(y ~44,LargeTitleText 金色大号)。
        var titleLbl = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -256, OffsetRight = 256, OffsetTop = 44, OffsetBottom = 76,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 24);
        titleLbl.AddThemeColorOverride("font_color", new Color(1f, 0.89f, 0.58f));
        AddChild(titleLbl);

        // 中央提示卡(50%±452 × 50%±196,左图右文)。
        var tipBox = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -452, OffsetRight = 452, OffsetTop = -196, OffsetBottom = 196,
        };
        var tipBg = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.07f, 0.055f, 1f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
        };
        tipBg.SetContentMarginAll(12);
        tipBox.AddThemeStyleboxOverride("panel", tipBg);
        AddChild(tipBox);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 16);
        tipBox.AddChild(hbox);

        var (tipTitle, tipBody, tipTex) = PickTip();
        if (tipTex != null)
        {
            var img = new TextureRect
            {
                Texture = tipTex,
                CustomMinimumSize = new Vector2(368, 368),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            };
            hbox.AddChild(img);
        }

        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 10);
        hbox.AddChild(vbox);

        var tipTitleLbl = new Label
        {
            Text = tipTitle,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        tipTitleLbl.AddThemeFontSizeOverride("font_size", 20);
        tipTitleLbl.AddThemeColorOverride("font_color", new Color(1f, 0.89f, 0.58f));
        vbox.AddChild(tipTitleLbl);

        var tipTextLbl = new Label
        {
            Text = tipBody,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        tipTextLbl.AddThemeFontSizeOverride("font_size", 15);
        tipTextLbl.AddThemeColorOverride("font_color", new Color(0.88f, 0.86f, 0.82f));
        vbox.AddChild(tipTextLbl);
    }

    /// <summary>进度 0..1(对齐原版 ProgressBar 百分比)。</summary>
    public void SetProgress(float fraction) => _bar.Value = Mathf.Clamp(fraction, 0f, 1f) * 100;

    // ── 提示目录(reference/tips 端口) ──

    private sealed class TipEntry
    {
        [JsonPropertyName("textFile")] public string TextFile { get; set; } = "";
        [JsonPropertyName("imageFiles")] public List<string> ImageFiles { get; set; } = new();
    }

    private sealed class TipCategory
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("loadingScreenOccurrence_SP")] public double OccurrenceSP { get; set; }
        [JsonPropertyName("files")] public List<TipEntry> Files { get; set; } = new();
    }

    private static List<TipCategory>? _catalog;

    /// <summary>按 loadingScreenOccurrence_SP 加权选类 → 随机一条 → 读文本(首行标题)+ 随机图。
    /// 任一资源缺失则退回无图纯文案(加载页不得因此失败)。</summary>
    private static (string title, string body, Texture2D? tex) PickTip()
    {
        try
        {
            string? binDir = FindBinariesDir();
            if (binDir == null) return ("Loading", "", null);

            _catalog ??= JsonSerializer.Deserialize<List<TipCategory>>(
                File.ReadAllText(Path.Combine(binDir,
                    "data", "mods", "public", "gui", "reference", "tips", "tipfiles.json")));
            if (_catalog == null || _catalog.Count == 0) return ("Loading", "", null);

            var rng = new RandomNumberGenerator();
            rng.Randomize();

            // 加权选类(原版 tips.js 同款 occurrence 权重)。
            double total = 0;
            foreach (var c in _catalog) total += c.OccurrenceSP;
            double roll = rng.Randf() * total;
            TipCategory chosen = _catalog[^1];
            foreach (var c in _catalog)
            {
                roll -= c.OccurrenceSP;
                if (roll <= 0) { chosen = c; break; }
            }
            if (chosen.Files.Count == 0) return ("Loading", "", null);

            var entry = chosen.Files[(int)(rng.Randi() % chosen.Files.Count)];

            string textPath = Path.Combine(binDir,
                "data", "mods", "public", "gui", "reference", "tips", "texts", entry.TextFile);
            string title = chosen.Name, body = "";
            if (File.Exists(textPath))
            {
                var lines = File.ReadAllLines(textPath);
                if (lines.Length > 0)
                {
                    title = lines[0].Trim();
                    body = string.Join("\n", lines[1..]).Trim();
                }
            }

            Texture2D? tex = null;
            if (entry.ImageFiles.Count > 0)
            {
                string imgPath = Path.Combine(binDir,
                    "data", "mods", "public", "art", "textures", "ui", "tips",
                    entry.ImageFiles[(int)(rng.Randi() % entry.ImageFiles.Count)]);
                var img = Image.LoadFromFile(imgPath);
                if (img != null)
                    tex = ImageTexture.CreateFromImage(img);
            }
            return (title, body, tex);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"LoadingOverlay.PickTip failed: {ex.Message}");
            return ("Loading", "", null);
        }
    }

    /// <summary>binaries/ 目录定位(与 FindTemplatesPath 同款 ../、../../ 回退)。</summary>
    private static string? FindBinariesDir()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var candidate in new[]
        {
            Path.GetFullPath(Path.Combine(projRoot, "..", "binaries")),
            Path.GetFullPath(Path.Combine(projRoot, "..", "..", "binaries")),
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
