using System.Collections.Generic;
using System.Linq;
using Godot;
using ZeroAD.Godot.Diagnostics;

namespace ZeroAD.Godot;

/// <summary>诊断日志面板(F11 切换):列出运行时遇到的日志 tag,勾选=静音该通道;
/// 底部实时显示最近日志(err 红/warn 黄/log 白)。对齐 ModalPanelBase 外壳 + Esc 关闭。
/// 静音状态写入 ZeroAD.Sim.Diag(tag 级过滤),启动期过滤另见 ZEROAD_LOG 环境变量。</summary>
public sealed partial class DiagPanel : ModalPanelBase
{
    private readonly int _layer;
    private VBoxContainer _tagRows = null!;
    private RichTextLabel _logView = null!;
    private VBoxContainer _content = null!;

    public DiagPanel(int layer = 132) : base() => _layer = layer;

    public override void _Ready()
    {
        Layer = _layer;
        var (content, _status) = BuildShell("Diagnostics Log", 640);
        _content = content;

        // 顶行:tag 勾选区(滚动,超出折叠)+ 清空/全选按钮。
        var tagScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _tagRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tagScroll.AddChild(_tagRows);
        content.AddChild(tagScroll);

        // 日志视图(最近 200 条,新→旧)。
        _logView = new RichTextLabel
        {
            BbcodeEnabled = true,
            CustomMinimumSize = new Vector2(0, 220),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollActive = false,
        };
        content.AddChild(_logView);
    }

    protected override void OnOpen()
    {
        RebuildTagRows();
        RefreshLog();
    }

    public override void _Process(double delta)
    {
        if (Visible) RefreshLog();
    }

    private void RebuildTagRows()
    {
        foreach (var c in _tagRows.GetChildren()) c.QueueFree();
        // 收集当前缓冲里出现过的 tag(去重,字母序,确定性)。
        var tags = DiagGodot.Recent(500).Select(e => e.Tag).Distinct().OrderBy(t => t).ToList();
        foreach (var tag in tags)
        {
            var cb = new CheckBox
            {
                Text = tag,
                ButtonPressed = !ZeroAD.Sim.Diag.IsMuted(tag),   // 勾选=放行
                TooltipText = "取消勾选 = 静音该通道",
            };
            string captured = tag;
            cb.Toggled += pressed =>
            {
                if (pressed) ZeroAD.Sim.Diag.Unmute(captured);
                else ZeroAD.Sim.Diag.Mute(captured);
            };
            _tagRows.AddChild(cb);
        }
    }

    private void RefreshLog()
    {
        var entries = DiagGodot.Recent(200);
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            string color = e.Level switch
            {
                ZeroAD.Sim.DiagLevel.Err => "red",
                ZeroAD.Sim.DiagLevel.Warn => "yellow",
                _ => "white",
            };
            sb.Append($"[color={color}][{e.Tag}] {Escape(e.Message)}[/color]\n");
        }
        _logView.Text = sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("[", "[lb]").Replace("]", "[rb]");
}
