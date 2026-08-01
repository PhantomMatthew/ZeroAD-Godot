using System;
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
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(minWidth, 0),
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var bg = new StyleBoxFlat
        {
            // 不透明(对齐原版 ModernDialog 实心底)——半透明会让下层主菜单亮色按钮透上来成残影。
            BgColor = new Color(0.06f, 0.05f, 0.04f, 1.0f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthBottom = 3, BorderWidthTop = 3, BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        bg.SetContentMarginAll(20);
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        var titleLbl = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 24);
        vbox.AddChild(titleLbl);

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

    protected static Button AddButton(Control parent, string label, Action onPressed,
        bool disabled = false, float minWidth = 150)
    {
        var btn = new Button
        {
            Text = label,
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(minWidth, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            Disabled = disabled,
        };
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
        OnOpen();
    }

    public void Close() => Visible = false;

    /// <summary>面板打开时刷新动态内容(子类重写:重读 sim 状态重建行/数值)。</summary>
    protected virtual void OnOpen() { }
}
