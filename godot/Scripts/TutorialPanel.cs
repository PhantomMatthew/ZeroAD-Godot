using Godot;
using System.Collections.Generic;

namespace ZeroAD.Godot;

public sealed partial class TutorialPanel : Panel
{
    private RichTextLabel _tutorialText = null!;
    private Label _tutorialWarning = null!;
    private Button _tutorialReady = null!;
    private readonly List<string> _messages = new();

    public event System.Action? OnReadyPressed;
    public event System.Action? OnQuitPressed;

    public override void _Ready()
    {
        Visible = false;
        // 对齐原版 session/tutorial_panel.xml:size 50%-380 38 50%+380 292(顶部居中 760×254)。
        // 必须直写 anchors+offsets——CenterTop+Position 写法会被 Position setter
        // 换算成相对锚点的偏移,导致面板随窗口宽度跑偏(贴左缘)。
        AnchorLeft = 0.5f; AnchorRight = 0.5f; AnchorTop = 0f; AnchorBottom = 0f;
        OffsetLeft = -380; OffsetRight = 380; OffsetTop = 38; OffsetBottom = 292;
        GrowHorizontal = GrowDirection.Both;
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.07f, 0.06f, 0.88f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthBottom = 2,
            BorderWidthTop = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        bg.SetContentMarginAll(10);
        AddThemeStyleboxOverride("panel", bg);

        _tutorialText = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = true,
            Size = new Vector2(740, 180),
            Position = new Vector2(10, 10),
        };
        _tutorialText.AddThemeFontSizeOverride("normal_font_size", 14);
        _tutorialText.AddThemeColorOverride("default_color", new Color(1f, 0.89f, 0.58f));
        AddChild(_tutorialText);

        _tutorialWarning = new Label
        {
            Position = new Vector2(10, 196),
            Size = new Vector2(560, 24),
            Text = "",
        };
        _tutorialWarning.AddThemeFontSizeOverride("font_size", 13);
        _tutorialWarning.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.35f));
        AddChild(_tutorialWarning);

        _tutorialReady = new Button
        {
            Text = "Ready",
            Theme = UITheme.GetTheme(),
            Position = new Vector2(600, 192),
            Size = new Vector2(140, 32),
            Visible = false,
        };
        _tutorialReady.Pressed += () =>
        {
            if (_tutorialReady.Text == "Quit")
                OnQuitPressed?.Invoke();
            else
                OnReadyPressed?.Invoke();
        };
        AddChild(_tutorialReady);
    }

    public void ShowTutorial()
    {
        Visible = true;
    }

    public void Toggle()
    {
        if (!Visible && _messages.Count == 0) return;
        Visible = !Visible;
    }

    public void UpdateTutorial(IReadOnlyList<string> newLines, string? warning, bool readyButton, bool leave)
    {
        Visible = true;

        if (!string.IsNullOrEmpty(warning))
        {
            _tutorialWarning.Text = warning;
            return;
        }

        foreach (var line in newLines)
            _messages.Add(line);

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _messages.Count; i++)
        {
            if (i == _messages.Count - 1)
                sb.Append($"[color=#ffe295]{_messages[i]}[/color]");
            else
                sb.Append(_messages[i]);
            if (i < _messages.Count - 1)
                sb.Append('\n');
        }
        _tutorialText.Text = sb.ToString();

        if (readyButton)
        {
            _tutorialReady.Visible = true;
            if (leave)
            {
                _tutorialWarning.Text = "Click to quit this tutorial.";
                _tutorialReady.Text = "Quit";
            }
            else
            {
                _tutorialWarning.Text = "Click when ready.";
                _tutorialReady.Text = "Ready";
            }
        }
        else
        {
            _tutorialWarning.Text = "Follow the instructions.";
            _tutorialReady.Visible = false;
        }
    }
}
