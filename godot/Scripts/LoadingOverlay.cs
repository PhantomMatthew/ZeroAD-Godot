using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace ZeroAD.Godot;

// 加载等待页(逐元素对齐原版 gui/page_loading.xml + gui/loading/loading.xml):
// ModernWindow 全屏底(12,12,12 + global/modern/background.png 拉伸,内缩 12/22);
// 顶部居中进度条(50%±256, y 4..36);地图标题(LargeTitleText:sans-bold-24 白,
// "Loading “map”" / random 图 "Generating “map”");中央提示卡(50%±452 × 50%±196 =
// 904×392:左侧 512 方图 + 底部渐变 + 金线框,右侧羊皮纸底黑字标题/正文);
// 底部名言条(QuoteDisplay:50%±448, 50%+230..100%-16,sans-bold-stroke-14 白,
// 随机取 gui/text/quotes.txt 一行,\[..]/\n 转义还原)。加载期间指针切 cursor-wait
// (原版 loading.js 的 SetCursor),退出恢复 default-arrow。
public sealed partial class LoadingOverlay : CanvasLayer
{
    private readonly LoadingProgressBar _bar;
    private CursorService? _cursor;

    public LoadingOverlay(string title, bool isRandom = false)
    {
        Layer = 100; // above everything

        string? binDir = FindBinariesDir();

        // ── ModernWindow 底:backcolor 12,12,12 + background.png(12 22 100%-12 100%-12)──
        var bg = new ColorRect { Color = new Color(12f / 255f, 12f / 255f, 12f / 255f, 1f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(bg);
        var bgTex = LoadModern(binDir, "background.png");
        if (bgTex != null)
        {
            var bgImg = new TextureRect
            {
                Texture = bgTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            bgImg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            bgImg.OffsetLeft = 12; bgImg.OffsetTop = 22;
            bgImg.OffsetRight = -12; bgImg.OffsetBottom = -12;
            AddChild(bgImg);
        }

        // ── 顶部居中进度条(50%±256, y 4..36;贴图合成见 LoadingProgressBar)──
        _bar = new LoadingProgressBar
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -256, OffsetRight = 256, OffsetTop = 4, OffsetBottom = 36,
        };
        _bar.Init(binDir);
        AddChild(_bar);

        // ── 地图标题(LargeTitleText:sans-bold-24 白居中;区域 36..提示卡顶,垂直居中)──
        // 原版 TitleDisplay:random 图 "Generating “X”",其余 "Loading “X”"(中文引号同款)。
        var titleLbl = new Label
        {
            Text = isRandom ? $"Generating “{title}”" : $"Loading “{title}”",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0.5f,
            OffsetLeft = -452, OffsetRight = 452, OffsetTop = 36, OffsetBottom = -196,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 24);
        titleLbl.AddThemeColorOverride("font_color", Colors.White);
        titleLbl.AddThemeColorOverride("font_outline_color", Colors.Black);
        titleLbl.AddThemeConstantOverride("outline_size", 4);
        AddChild(titleLbl);

        BuildTipCard(binDir);
        BuildQuote(binDir);
    }

    public override void _Ready()
    {
        // 原版 loading.js init:Engine.SetCursor("cursor-wait")。
        _cursor = GetNodeOrNull<CursorService>("/root/CursorService");
        _cursor?.SetWaitCursor();
    }

    public override void _ExitTree()
    {
        // 原版 reallyStartGame:Engine.ResetCursor() 回 default-arrow。
        _cursor?.RestoreDefaultCursor();
        _cursor = null;
    }

    /// <summary>进度 0..1(对齐原版 ProgressBar 百分比)。</summary>
    public void SetProgress(float fraction) => _bar.SetProgress(fraction);

    // ── 中央提示卡(904×392;TipDisplay.xml)──

    private void BuildTipCard(string? binDir)
    {
        var card = new Control
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -452, OffsetRight = 452, OffsetTop = -196, OffsetBottom = 196,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(card);

        var (tipTitle, tipBody, tipTex) = PickTip();

        // 左:金线框(0 4, 520×392)+ 方图(4 8, 512×384 居中)+ 底部渐变罩。
        var frame = new Control
        {
            Position = new Vector2(0, 4),
            Size = new Vector2(520, 392),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        card.AddChild(frame);
        if (tipTex != null)
        {
            var img = new TextureRect
            {
                Texture = tipTex,
                Position = new Vector2(4, 4),
                Size = new Vector2(512, 384),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            frame.AddChild(img);
            var gradient = LoadPublic(binDir, "tipdisplay/tip-image-gradient.png");
            if (gradient != null)
            {
                var gradRect = new TextureRect
                {
                    Texture = gradient,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                gradRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                img.AddChild(gradRect);
            }
        }
        AddBorderLines(binDir, frame);

        // 右:羊皮纸底(556 0, 348×392)+ 黑字标题(sans-bold-16 居中)+ 装饰线 + 正文(14)。
        var textArea = new Control
        {
            Position = new Vector2(556, 0),
            Size = new Vector2(348, 392),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        card.AddChild(textArea);
        var parchment = LoadPublicCropped(binDir, "tipdisplay/parchment.png", new Rect2I(0, 0, 318, 391));
        if (parchment != null)
        {
            var parch = new TextureRect
            {
                Texture = parchment,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            parch.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            textArea.AddChild(parch);
        }
        else
        {
            // 素材缺失回退:米色平底(羊皮纸色)。
            var flat = new ColorRect { Color = new Color(0.87f, 0.80f, 0.66f) };
            flat.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            flat.MouseFilter = Control.MouseFilterEnum.Ignore;
            textArea.AddChild(flat);
        }

        var titleLbl = new Label
        {
            Text = tipTitle,
            AnchorLeft = 0f, AnchorRight = 1f,
            OffsetLeft = 20, OffsetRight = -20, OffsetTop = 25, OffsetBottom = 45,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 16);
        titleLbl.AddThemeColorOverride("font_color", Colors.Black);
        textArea.AddChild(titleLbl);

        // TipTitleDecoration:标题下的左右金饰线(tipdisplay/title-ornament.png)。
        var ornament = LoadPublic(binDir, "tipdisplay/title-ornament.png");
        if (ornament != null)
        {
            var orn = new TextureRect
            {
                Texture = ornament,
                Position = new Vector2(30, 48),
                Size = new Vector2(348 - 60, 8),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Tile,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            textArea.AddChild(orn);
        }

        var bodyLbl = new Label
        {
            Text = tipBody,
            // 锚点定宽(左 30 右 30 于 textArea)而非手动 Size——任何缩放/主题重排下
            // 宽度都跟随纸张,WordSmart 自动回行不会溢出纸外(羊皮纸原件 318 宽,
            // 留足左右边距避让装饰边框)。
            AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = 30, OffsetRight = -30, OffsetTop = 73, OffsetBottom = -60,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Top,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bodyLbl.AddThemeFontSizeOverride("font_size", 14);
        bodyLbl.AddThemeColorOverride("font_color", Colors.Black);
        textArea.AddChild(bodyLbl);
    }

    /// <summary>TipImageFrame:四边 4px 金线(line_horiz/line_vert)+ 四角 4×4 角件。</summary>
    private static void AddBorderLines(string? binDir, Control frame)
    {
        var horiz = LoadPublic(binDir, "global/border/line_horiz.png");
        var vert = LoadPublic(binDir, "global/border/line_vert.png");
        if (horiz == null || vert == null) return;

        TextureRect Line(Texture2D tex, float l, float t, float r, float b)
        {
            var tr = new TextureRect
            {
                Texture = tex,
                Position = new Vector2(l, t),
                Size = new Vector2(r, b),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Tile,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            frame.AddChild(tr);
            return tr;
        }
        // 上边/下边(内缩 4):横线拉伸
        Line(horiz, 4, 0, 512, 4);
        Line(horiz, 4, 388, 512, 4);
        // 左边/右边
        Line(vert, 0, 4, 4, 384);
        Line(vert, 516, 4, 4, 384);
        // 四角
        foreach (var (file, x, y) in new[]
        {
            ("line_corner_top_left.png", 0f, 0f), ("line_corner_top_right.png", 516f, 0f),
            ("line_corner_bottom_left.png", 0f, 388f), ("line_corner_bottom_right.png", 516f, 388f),
        })
        {
            var tex = LoadPublic(binDir, "global/border/" + file);
            if (tex == null) continue;
            frame.AddChild(new TextureRect
            {
                Texture = tex,
                Position = new Vector2(x, y),
                Size = new Vector2(4, 4),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Keep,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
    }

    // ── 底部名言条(QuoteDisplay:quotes.txt 随机一行)──

    private void BuildQuote(string? binDir)
    {
        string quote = PickQuote(binDir);
        if (quote.Length == 0) return;
        var lbl = new Label
        {
            Text = quote,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 1f,
            OffsetLeft = -448, OffsetRight = 448, OffsetTop = 230, OffsetBottom = -16,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        // LoadingText:sans-bold-stroke-14 白(黑描边)。
        lbl.AddThemeFontSizeOverride("font_size", 14);
        lbl.AddThemeColorOverride("font_color", Colors.White);
        lbl.AddThemeColorOverride("font_outline_color", Colors.Black);
        lbl.AddThemeConstantOverride("outline_size", 4);
        AddChild(lbl);
    }

    private static string PickQuote(string? binDir)
    {
        try
        {
            if (binDir == null) return "";
            string path = Path.Combine(binDir,
                "data", "mods", "public", "gui", "text", "quotes.txt");
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            for (int tries = 0; tries < 8; tries++)
            {
                string line = lines[(int)(rng.Randi() % lines.Length)].Trim();
                if (line.Length == 0) continue;
                // 原版文本转义:\[ \] 是字面方括号(GUI 文本引擎转义),\n 是换行。
                return line.Replace("\\[", "[").Replace("\\]", "]").Replace("\\n", "\n");
            }
        }
        catch (Exception ex) { ZeroAD.Sim.Diag.Warn("Loading", $"quote pick failed: {ex.Message}"); }
        return "";
    }

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
            ZeroAD.Sim.Diag.Err("Main", $"LoadingOverlay.PickTip failed: {ex.Message}");
            return ("Loading", "", null);
        }
    }

    // ── 贴图读取(binaries junction)──

    /// <summary>mods/mod 的 modern 贴图(global/modern/xxx.png)。</summary>
    private static Texture2D? LoadModern(string? binDir, string file)
    {
        if (binDir == null) return null;
        var img = Image.LoadFromFile(Path.Combine(binDir,
            "data", "mods", "mod", "art", "textures", "ui", "global", "modern", file));
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>mods/public 的 ui 贴图(相对 art/textures/ui/,如 "tipdisplay/parchment.png")。</summary>
    private static Texture2D? LoadPublic(string? binDir, string relPath)
    {
        if (binDir == null) return null;
        var img = Image.LoadFromFile(Path.Combine(binDir,
            "data", "mods", "public", "art", "textures", "ui",
            relPath.Replace('/', Path.DirectorySeparatorChar)));
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>LoadPublic + 源图裁剪(原版 real_texture_placement 语义:parchment.png 是
    /// 512×512 画布,纸张本体只占左上 ~318×391,直接拉伸会把文字区留白算进去)。</summary>
    private static Texture2D? LoadPublicCropped(string? binDir, string relPath, Rect2I region)
    {
        if (binDir == null) return null;
        var img = Image.LoadFromFile(Path.Combine(binDir,
            "data", "mods", "public", "art", "textures", "ui",
            relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (img == null) return null;
        if (region.Size.X < img.GetWidth() || region.Size.Y < img.GetHeight())
            img = img.GetRegion(region);
        return ImageTexture.CreateFromImage(img);
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
