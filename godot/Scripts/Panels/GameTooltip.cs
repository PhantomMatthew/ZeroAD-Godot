using System;
using Godot;

namespace ZeroAD.Godot;

/// <summary>自绘游戏内 tooltip(替代 Godot 内建 TooltipText)。
/// 内建 tooltip 画在根视口的基础画布层——本项目的模态面板全在 CanvasLayer 55+,
/// 内建弹窗被整层压住永远不可见(structree 悬停无反应的根因)。本类在高 CanvasLayer
/// (100)上自建一张跟随鼠标的说明卡:Attach(控件, 取文本) 挂 MouseEntered/Exited。
/// 文本支持 \n 多行;卡片避让屏幕右/下边缘。</summary>
public sealed partial class GameTooltip : CanvasLayer
{
    private static GameTooltip? _instance;

    public static GameTooltip Instance =>
        _instance ??= Create();

    private PanelContainer _card = null!;
    private Label _label = null!;
    private Control? _owner;

    private static GameTooltip Create()
    {
        var t = new GameTooltip();
        // 挂到树根(SceneTree.root)而非某个场景——面板销毁不带走 tooltip 层。
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(t);
        return t;
    }

    public override void _Ready()
    {
        Layer = 100;   // 高于全部模态面板(55)与 HUD(45-50)
        ProcessMode = ProcessModeEnum.Always;
        _card = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.05f, 0.96f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(4);
        _card.AddThemeStyleboxOverride("panel", sb);
        _label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 13);
        _label.AddThemeColorOverride("font_color", new Color(0.93f, 0.88f, 0.75f));
        _card.AddChild(_label);
        _card.Visible = false;
        _card.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_card);
    }

    public override void _Process(double delta)
    {
        if (_card.Visible && _owner != null)
            PlaceAt(GetViewport().GetMousePosition());
    }

    /// <summary>给控件挂 tooltip(悬停显示 text,多行用 \n)。</summary>
    public static void Attach(Control control, Func<string> text)
    {
        control.MouseEntered += () => Instance.ShowFor(control, text());
        control.MouseExited += () => Instance.Hide(control);
    }

    private void ShowFor(Control owner, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _owner = owner;
        _label.Text = text;
        _card.Visible = true;
        PlaceAt(GetViewport().GetMousePosition());
    }

    private void Hide(Control owner)
    {
        if (_owner != owner) return;   // 已切给别的控件,不打断
        _owner = null;
        _card.Visible = false;
    }

    private void PlaceAt(Vector2 mouse)
    {
        // 尺寸需先量:强制布局后 GetRect 才是内容实际大小。
        _card.CustomMinimumSize = Vector2.Zero;
        var size = _card.GetCombinedMinimumSize();
        float x = mouse.X + 18;
        float y = mouse.Y + 14;
        var vp = GetViewport().GetVisibleRect().Size;
        if (x + size.X > vp.X) x = mouse.X - size.X - 8;
        if (y + size.Y > vp.Y) y = mouse.Y - size.Y - 8;
        _card.Position = new Vector2(MathF.Max(x, 4), MathF.Max(y, 4));
        _card.Size = size;
    }
}
