using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>全局鼠标指针(autoload)。对齐 C++ 版:VideoMode.cpp 的
/// DEFAULT_CURSOR_NAME = "default-arrow",cursorbackend="sdl"——SDL 色指针加载
/// art/textures/cursors/default-arrow.png(32×32,热点 .txt 定义 1,1),且从主菜单到
/// 对局、文本框全场景统一这一枚(原版没有任何系统 I 型/手型指针)。
/// 实现走原生 Input.SetCustomMouseCursor(macOS 独立窗口有效;编辑器内嵌运行有已知
/// 刷新限制 godot#110800,仅影响开发调试)。macOS 全屏/掠过程序坞会回退系统指针
/// (godot#76038/#104892)——窗口重聚焦/变尺寸时用两张视觉一致的纹理交替强刷规避
/// (同图重设是引擎缓存 no-op,官方 issue 下的确认 workaround)。
/// 对局内的动作光标(attack/gather/...)仍走 Main.cs 的软件精灵(内嵌调试验证过),
/// 其隐藏/恢复 OS 指针与本服务无冲突:恢复可见时即显示本服务装的 default-arrow。</summary>
public sealed partial class CursorService : Node
{
    private const string DefaultCursorName = "default-arrow";

    // default-arrow.txt:热点 (1,1)(原版全部光标 .txt 均为 1 1,Main.cs 软件光标同款)。
    private static readonly Vector2 Hotspot = new(1, 1);

    private Texture2D? _arrow;
    private Texture2D? _arrowPadded;  // 33×33 透明垫边版(视觉一致),刷新规避用
    private Texture2D? _wait;         // cursor-wait(加载页;热点 16,16 居中)
    private bool _toggle;
    private bool _waitActive;

    /// <summary>切到等待指针(原版 loading.js:Engine.SetCursor("cursor-wait"),
    /// 热点 16,16 居中)。贴图缺失时保持箭头。</summary>
    public void SetWaitCursor()
    {
        _wait ??= LoadCursorTexture("cursor-wait");
        if (_wait == null) return;
        _waitActive = true;
        ApplyAll(_wait, new Vector2(16, 16));
    }

    /// <summary>恢复默认箭头(原版 reallyStartGame 的 Engine.ResetCursor())。</summary>
    public void RestoreDefaultCursor()
    {
        _waitActive = false;
        if (_arrow != null) ApplyAll(_arrow, Hotspot);
    }

    public override void _Ready()
    {
        _arrow = LoadCursorTexture(DefaultCursorName);
        if (_arrow == null)
        {
            ZeroAD.Sim.Diag.Err("Cursor", "default-arrow.png 缺失,保留系统指针");
            return;
        }
        _arrowPadded = MakePaddedVariant(_arrow);
        // 推迟一帧:等窗口完成首帧再装指针,避开"窗口未显示时设置被吞"的时序坑。
        CallDeferred(nameof(InstallNow));

        var win = GetWindow();
        win.FocusEntered += Reapply;
        win.SizeChanged += Reapply;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && IsInsideTree())
        {
            var win = GetWindow();
            win.FocusEntered -= Reapply;
            win.SizeChanged -= Reapply;
        }
        base.Dispose(disposing);
    }

    private void InstallNow()
    {
        if (_arrow != null) ApplyAll(_arrow, Hotspot);
    }

    /// <summary>焦点/尺寸变化后重设(macOS 全屏回退 workaround):交替两张视觉一致的
    /// 图——同一 Texture2D 重设会被引擎指针缓存当 no-op,换图才触发 OS 层重装。</summary>
    private void Reapply()
    {
        if (_arrow == null || _arrowPadded == null) return;
        if (_waitActive) { if (_wait != null) ApplyAll(_wait, new Vector2(16, 16)); return; }
        ApplyAll(_toggle ? _arrow : _arrowPadded, Hotspot);
        _toggle = !_toggle;
    }

    /// <summary>全部指针形状统一为同一纹理(Arrow/Ibeam/PointingHand/… 共 16 形)。</summary>
    private static void ApplyAll(Texture2D tex, Vector2 hotspot)
    {
        for (int i = 0; i <= (int)Input.CursorShape.Help; i++)
            Input.SetCustomMouseCursor(tex, (Input.CursorShape)i, hotspot);
    }

    /// <summary>优先 vendored 资源 res://assets/ui/cursors/{name}.png;缺失时从 binaries
    /// junction 直读(default-arrow 在 mods/mod,action-* 在 mods/public,两边都试)。</summary>
    private static Texture2D? LoadCursorTexture(string name)
    {
        string resPath = $"res://assets/ui/cursors/{name}.png";
        if (ResourceLoader.Exists(resPath))
        {
            var tex = GD.Load<Texture2D>(resPath);
            if (tex != null) return tex;
        }
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) return null;
        foreach (var mod in new[] { "mod", "public" })
        {
            string p = Path.Combine(binDir, "data", "mods", mod,
                "art", "textures", "cursors", name + ".png");
            var img = Image.LoadFromFile(p);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        return null;
    }

    /// <summary>底/右各垫 1px 透明边的同图副本(热点不变,视觉完全相同)。</summary>
    private static Texture2D? MakePaddedVariant(Texture2D tex)
    {
        var img = tex.GetImage();
        if (img == null) return null;
        var padded = Image.CreateEmpty(img.GetWidth() + 1, img.GetHeight() + 1, false, Image.Format.Rgba8);
        padded.BlitRect(img, new Rect2I(0, 0, img.GetWidth(), img.GetHeight()), Vector2I.Zero);
        return ImageTexture.CreateFromImage(padded);
    }
}
