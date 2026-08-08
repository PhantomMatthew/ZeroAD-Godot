using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// 模态面板外壳:CanvasLayer + 全屏遮罩(挡点击,不暂停 sim)+ 居中 PanelContainer + 标题/内容/关闭。
// 镜像 PauseMenu/GameOverOverlay 的叠层模式,供第二梯队 4 个菜单面板复用(GameSpeed/Diplomacy/
// Trade/MatchSettings)。这些面板原版均不暂停游戏(只模态挡鼠标),故 Open 不设 SimBridge.Paused。
// Layer=55:在 HUD/GameOverOverlay(50)之上、PauseMenu(60)之下。
public abstract partial class ModalPanelBase : CanvasLayer
{
    protected ModalPanelBase() => ProcessMode = ProcessModeEnum.Always;

    /// <summary>构建外壳,返回(内容容器, 状态标签)。子类在 _Ready 调用并把动态内容加进 content。</summary>
    protected (VBoxContainer content, Label status) BuildShell(string title, float minWidth = 420)
    {
        Layer = 55;
        Visible = false;

        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(dim);

        // 锚点居中(非 CenterContainer):四锚 0.5 + 双向 Grow——面板始终以视口中心对称展开,
        // 超屏时对称溢出(对齐原版 50%±w/2 的居中语义)。CenterContainer 在子项大于容器时会把
        // 子项钳到 0,0(gui.scale>1 使逻辑画布缩小时,面板被甩到左上角)——锚点方案无此问题。
        // 保留 PanelContainer:它按内容自动撑出对话框尺寸;ModernDialog 的角饰/标题栏要溢出
        // 边缘绘制,不能容器布局,故作 PanelContainer 的兄弟节点(直接挂本层),靠 Resized 跟随。
        var panel = new PanelContainer
        {
            // 主题挂在外壳根上——Godot 主题沿树下传,面板内所有控件(含子类动态建的行)自动继承
            // 原版贴图按钮/标签/输入框样式,无需逐控件 Theme= 赋值(此前 HotkeysPanel 漏赋导致
            // 弹窗内容退回 Godot 默认灰色主题,与 C++ 版贴图样式不一致)。
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(minWidth, 0),
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // 窄屏保护:内容最小宽超过逻辑视口时,居中对称溢出会把左右两侧一起截掉
        // (科技树 920px 在 gui.scale 放大/小窗下必中)——钳到视口留边,内容走滚动。
        _shellPanel = panel;
        _shellMinWidth = minWidth;
        ClampShellToViewport();
        GetViewport().SizeChanged += ClampShellToViewport;

        // ModernDialog 贴图(mods/mod/gui/common/modern/sprites.xml L104);binaries 缺失时回退平底+描边。
        // 装饰件先于 panel 挂树 → 画在内容之下;标题文字后挂 → 画在最上。
        bool textured = TryBuildModernDialogSkin(out var skinPieces);
        if (!textured)
        {
            // 回退:不透明平底 + 棕金描边(原 BuildShell 样式)。
            var bg = new StyleBoxFlat
            {
                BgColor = new Color(0.06f, 0.05f, 0.04f, 1.0f),
                BorderColor = new Color(0.55f, 0.45f, 0.30f),
            };
            bg.SetBorderWidthAll(3);
            bg.SetCornerRadiusAll(6);
            panel.AddThemeStyleboxOverride("panel", bg);
        }
        foreach (var piece in skinPieces) AddChild(piece.C);
        AddChild(panel);

        // 装饰件跟随 panel 矩形(布局完成后才有尺寸;尺寸变化时重摆)。
        if (textured)
        {
            _skinPanel = panel;
            _skinPieces = skinPieces;
            panel.Resized += OnSkinPanelResized;
            CallDeferred(nameof(OnSkinPanelResized));
        }

        // 内容内边距:贴图壳按原版内容区(对话框内缩 ~16px,标题在壳外);回退壳给 20。
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        int pad = textured ? 16 : 20;
        margin.AddThemeConstantOverride("margin_left", pad);
        margin.AddThemeConstantOverride("margin_right", pad);
        margin.AddThemeConstantOverride("margin_top", textured ? 14 : pad);
        margin.AddThemeConstantOverride("margin_bottom", pad);
        panel.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        // 原版标题绘制在标题栏贴图上(对话框外溢,y -16..16),贴图壳时标题移出内容区;
        // 回退壳保留原内容区内标题。
        if (textured)
        {
            var titleLbl = new Label
            {
                Text = title,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            // 原版 ModernLabelText:sans-bold-stroke-14 白字黑描边。
            titleLbl.AddThemeFontSizeOverride("font_size", 14);
            titleLbl.AddThemeColorOverride("font_color", Colors.White);
            titleLbl.AddThemeColorOverride("font_outline_color", Colors.Black);
            titleLbl.AddThemeConstantOverride("outline_size", 4);
            skinPieces.Add(new SkinPiece(titleLbl, 0.5f, 0f, 0.5f, 0f, -128, -16, 128, 16));
            AddChild(titleLbl);
        }
        else
        {
            var titleLbl = new Label
            {
                Text = title,
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            titleLbl.AddThemeFontSizeOverride("font_size", 24);
            vbox.AddChild(titleLbl);
        }

        var status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        status.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(status);

        return (vbox, status);
    }

    /// <summary>外壳装饰件:挂在本层(CanvasLayer)下、相对 panel 矩形定位(锚+偏移,语义同原版
    /// sprite size 四元组)。不作 panel 子节点——容器会接管子项布局,角饰/标题栏需溢出边缘。</summary>
    private sealed class SkinPiece
    {
        public readonly Control C;
        public readonly float AL, AT, AR, AB, OL, OT, OR, OB;
        public SkinPiece(Control c, float al, float at, float ar, float ab,
            float ol, float ot, float orr, float ob)
        { C = c; AL = al; AT = at; AR = ar; AB = ab; OL = ol; OT = ot; OR = orr; OB = ob; }
    }

    private PanelContainer? _skinPanel;
    private PanelContainer? _shellPanel;
    private float _shellMinWidth;

    /// <summary>外壳宽度钳到视口(留 48px 边距);视口 SizeChanged 时重算。</summary>
    private void ClampShellToViewport()
    {
        if (_shellPanel == null) return;
        float vw = GetViewport().GetVisibleRect().Size.X;
        float maxW = Mathf.Max(320f, vw - 48f);
        _shellPanel.CustomMinimumSize = new Vector2(Mathf.Min(_shellMinWidth, maxW), 0);
    }
    private List<SkinPiece>? _skinPieces;

    private void OnSkinPanelResized()
    {
        if (_skinPanel != null && _skinPieces != null)
            LayoutSkinPieces(_skinPanel, _skinPieces);
    }

    /// <summary>按 panel 当前矩形摆放全部装饰件(等价原版 sprite 的锚点+偏移解析)。</summary>
    private static void LayoutSkinPieces(Control panel, List<SkinPiece> pieces)
    {
        var pos = panel.Position;
        var size = panel.Size;
        foreach (var p in pieces)
        {
            float x1 = pos.X + size.X * p.AL + p.OL;
            float y1 = pos.Y + size.Y * p.AT + p.OT;
            float x2 = pos.X + size.X * p.AR + p.OR;
            float y2 = pos.Y + size.Y * p.AB + p.OB;
            p.C.Position = new Vector2(x1, y1);
            p.C.Size = new Vector2(Mathf.Max(x2 - x1, 0), Mathf.Max(y2 - y1, 0));
        }
    }

    /// <summary>ModernDialog 合成贴图端口(原版 sprite:底图 + 上下阴影 + 上下金边 + 四角饰 +
    /// 标题栏三件套)。装饰件填入 pieces(未挂树,调用方按绘制序 AddChild + LayoutSkinPieces 定位)。
    /// 返回是否拿到贴图(否则调用方走平底回退)。</summary>
    private bool TryBuildModernDialogSkin(out List<SkinPiece> pieces)
    {
        var list = new List<SkinPiece>();
        pieces = list;
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) { pieces = new List<SkinPiece>(); return false; }
        string modernDir = Path.Combine(binDir,
            "data", "mods", "mod", "art", "textures", "ui", "global", "modern");
        Texture2D? Load(string file)
        {
            var img = Image.LoadFromFile(Path.Combine(modernDir, file));
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }

        var bgTex = Load("background.png");
        var borderTex = Load("border.png");
        if (bgTex == null || borderTex == null) { pieces = new List<SkinPiece>(); return false; }

        void Piece(Texture2D tex, float al, float at, float ar, float ab,
            float ol, float ot, float orr, float ob, bool flipH = false, bool flipV = false)
        {
            var tr = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                FlipH = flipH,
                FlipV = flipV,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            list.Add(new SkinPiece(tr, al, at, ar, ab, ol, ot, orr, ob));
        }

        // 背景:size 4 0 100%-4 100%-4(原版 background.png 512×512 区拉伸)。
        Piece(bgTex, 0, 0, 1, 1, 4, 0, -4, -4);

        // 上下阴影(shadow-low.png,顶部上下翻转)。
        var shadowTex = Load("shadow-low.png");
        if (shadowTex != null)
        {
            Piece(shadowTex, 0, 1, 1, 1, 4, -132, -4, -4);
            Piece(shadowTex, 0, 0, 1, 0, 4, 0, -4, 128, flipV: true);
        }

        // 上下金边(border.png 2048×8 横条,顶:4 -4..4,底:100%-8..100%)。
        Piece(borderTex, 0, 0, 1, 0, 4, -4, -4, 4);
        Piece(borderTex, 0, 1, 1, 1, 4, -8, -4, 0);

        // 四角饰(64×32,右件水平翻转;上件 y -21..11,下件 100%-22..100%+10)。
        var decoTop = Load("dialog-deco-top.png");
        if (decoTop != null)
        {
            Piece(decoTop, 0, 0, 0, 0, -14, -21, 50, 11);
            Piece(decoTop, 1, 0, 1, 0, -50, -21, 14, 11, flipH: true);
        }
        var decoBottom = Load("dialog-deco-bottom.png");
        if (decoBottom != null)
        {
            Piece(decoBottom, 0, 1, 0, 1, -31, -22, 33, 10);
            Piece(decoBottom, 1, 1, 1, 1, -33, -22, 31, 10, flipH: true);
        }

        // 标题栏三件套(middle 128×32 居中 y -18..15,left 32×32 两翼,右翼水平翻转)。
        var titleMid = Load("titlebar-middle.png");
        var titleLeft = Load("titlebar-left.png");
        if (titleMid != null && titleLeft != null)
        {
            Piece(titleMid, 0.5f, 0, 0.5f, 0, -108, -18, 108, 15);
            Piece(titleLeft, 0.5f, 0, 0.5f, 0, -134, -18, -102, 15);
            Piece(titleLeft, 0.5f, 0, 0.5f, 0, 102, -18, 134, 15, flipH: true);
        }

        return true;
    }

    protected static Button AddButton(Control parent, string label, Action onPressed,
        bool disabled = false, float minWidth = 150)
    {
        var btn = new Button
        {
            // 全局面板按钮统一过 gettext(msgid = 英文;未翻译时原样)。
            Text = Localization.Tr(label),
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(minWidth, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            Disabled = disabled,
        };
        // 原版对话框按钮统一 ModernButtonRed(modern 九宫格红钮);缺贴图时保留 UITheme 回退。
        ModernButtonStyle.Apply(btn, StoneButtonStyle.FindBinariesDir());
        btn.Pressed += onPressed;
        parent.AddChild(btn);
        return btn;
    }

    protected static Label MakeLabel(string text, int fontSize = 14)
    {
        var l = new Label
        {
            Text = text,
            Theme = UITheme.GetTheme(),
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        return l;
    }

    // ── 资源视图(Diplomacy/Trade 共用):原版规范序 Food/Wood/Stone/Metal + 原版资源色。 ──
    protected static readonly ResourceType[] AllResources =
        { ResourceType.Food, ResourceType.Wood, ResourceType.Stone, ResourceType.Metal };

    protected static string ResourceName(ResourceType t) => t switch
    {
        ResourceType.Food => "Food",
        ResourceType.Wood => "Wood",
        ResourceType.Stone => "Stone",
        ResourceType.Metal => "Metal",
        _ => t.ToString(),
    };

    // 原版资源色(对齐 session/atlas.json 资源图标底色):Food 红 / Wood 棕 / Stone 灰 / Metal 蓝。
    protected static Color ResourceColor(ResourceType t) => t switch
    {
        ResourceType.Food => new Color(0.86f, 0.27f, 0.27f),
        ResourceType.Wood => new Color(0.62f, 0.45f, 0.27f),
        ResourceType.Stone => new Color(0.70f, 0.70f, 0.70f),
        ResourceType.Metal => new Color(0.40f, 0.62f, 0.86f),
        _ => new Color(0.8f, 0.8f, 0.8f),
    };

    // 资源小色块 + 名字一行(Trade/Diplomacy 进贡/易物列头与按钮用)。
    protected static HBoxContainer MakeResourceTag(ResourceType t)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        row.AddThemeConstantOverride("separation", 4);
        var swatch = new ColorRect
        {
            Color = ResourceColor(t),
            CustomMinimumSize = new Vector2(12, 12),
        };
        row.AddChild(swatch);
        row.AddChild(MakeLabel(ResourceName(t), 13));
        return row;
    }

    public void Open()
    {
        Visible = true;
        // 外壳贴图件在隐藏期尺寸为 0;显示触发布局后重摆(Resized 亦会兜底,此为双保险)。
        CallDeferred(nameof(OnSkinPanelResized));
        OnOpen();
    }

    public void Close() => Visible = false;

    /// <summary>Esc 关闭(原版面板行为;消费事件,不再穿透到游戏层清选择/开菜单)。</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>面板打开时刷新动态内容(子类重写:重读 sim 状态重建行/数值)。</summary>
    protected virtual void OnOpen() { }
}
