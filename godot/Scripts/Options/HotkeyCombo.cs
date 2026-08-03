using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Options;

/// <summary>热键组合字符串 ↔ Godot InputEvent 双向序列化。
/// 组合格式（对齐原版 default.cfg）："Ctrl+A"、"Q"、"WheelUp"、"Shift+Space"。
/// 修饰键前缀 Ctrl/Shift/Alt/Meta；主键支持字母/数字/方向键/功能键/特殊键/鼠标按钮。</summary>
public static class HotkeyCombo
{
    // ── 0 A.D. 键名 ↔ Godot Key 枚举 映射 ──
    private static readonly Dictionary<string, Key> KeyMap = new()
    {
        // 方向键
        ["UpArrow"] = Key.Up, ["DownArrow"] = Key.Down,
        ["LeftArrow"] = Key.Left, ["RightArrow"] = Key.Right,
        // 特殊键
        ["Space"] = Key.Space, ["Escape"] = Key.Escape, ["Return"] = Key.Enter,
        ["Enter"] = Key.KpEnter, ["Tab"] = Key.Tab, ["Backspace"] = Key.Backspace,
        ["Delete"] = Key.Delete, ["Insert"] = Key.Insert, ["Home"] = Key.Home,
        ["End"] = Key.End, ["PageUp"] = Key.Pageup, ["PageDown"] = Key.Pagedown,
        ["Pause"] = Key.Pause, ["BackQuote"] = Key.Quoteleft,
        // 符号键
        ["Plus"] = Key.Equal, ["Minus"] = Key.Minus,
        ["NumPlus"] = Key.KpAdd, ["NumMinus"] = Key.KpSubtract,
        // 鼠标（特殊标记，Parse 时转 InputEventMouseButton）
        ["WheelUp"] = Key.None, ["WheelDown"] = Key.None,
        ["MouseMiddle"] = Key.None, ["MouseX1"] = Key.None, ["MouseX2"] = Key.None,
    };

    // 反向：Godot Key → 0 A.D. 键名（用于 Format 显示）
    private static readonly Dictionary<Key, string> ReverseKeyMap = BuildReverseKeyMap();

    private static Dictionary<Key, string> BuildReverseKeyMap()
    {
        var m = new Dictionary<Key, string>();
        foreach (var kvp in KeyMap)
            if (kvp.Value != Key.None) m[kvp.Value] = kvp.Key;
        m[Key.Up] = "UpArrow"; m[Key.Down] = "DownArrow";
        m[Key.Left] = "LeftArrow"; m[Key.Right] = "RightArrow";
        return m;
    }

    // 鼠标按钮名 → MouseButton
    private static readonly Dictionary<string, MouseButton> MouseMap = new()
    {
        ["WheelUp"] = MouseButton.WheelUp, ["WheelDown"] = MouseButton.WheelDown,
        ["MouseMiddle"] = MouseButton.Middle,
        ["MouseX1"] = MouseButton.Xbutton1, ["MouseX2"] = MouseButton.Xbutton2,
    };

    /// <summary>组合字符串 → InputEvent。"Ctrl+A" → InputEventKey(Ctrl+A)。返回 null 表示无法解析。</summary>
    public static InputEvent? Parse(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        string s = combo.Trim();
        bool ctrl = false, shift = false, alt = false, meta = false;

        // 解析修饰键前缀（按 + 分割，最后一段是主键）。
        var parts = s.Split('+');
        string main = parts[^1].Trim();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string mod = parts[i].Trim();
            switch (mod)
            {
                case "Ctrl": ctrl = true; break;
                case "Shift": shift = true; break;
                case "Alt": alt = true; break;
                case "Meta": case "Super": case "Win": meta = true; break;
            }
        }

        // 鼠标按钮？
        if (MouseMap.TryGetValue(main, out var mb))
        {
            return new InputEventMouseButton
            {
                ButtonIndex = mb,
                CtrlPressed = ctrl, ShiftPressed = shift,
                AltPressed = alt, MetaPressed = meta,
            };
        }

        // 主键解析：先查 KeyMap，再尝试单字母/数字。
        Key key = Key.None;
        if (KeyMap.TryGetValue(main, out var mapped)) key = mapped;
        else if (main.Length == 1)
        {
            // 单字符：大写字母或数字。
            char c = main[0];
            if (char.IsLetter(c)) key = (Key)char.ToUpper(c);   // A→Key.A=65
            else if (char.IsDigit(c)) key = (Key)(c - '0' + (int)Key.Key0);
        }
        // F1-F12
        else if (main.Length >= 2 && main[0] == 'F' && int.TryParse(main.Substring(1), out int fn) && fn >= 1 && fn <= 12)
            key = Key.F1 + (fn - 1);

        if (key == Key.None) return null;

        return new InputEventKey
        {
            Keycode = key,
            CtrlPressed = ctrl, ShiftPressed = shift,
            AltPressed = alt, MetaPressed = meta,
        };
    }

    /// <summary>InputEvent → 组合字符串（用于显示当前绑定）。InputEventKey/InputEventMouseButton。</summary>
    public static string Format(InputEvent evt)
    {
        bool ctrl = false, shift = false, alt = false, meta = false;
        string main = "";

        if (evt is InputEventKey k)
        {
            ctrl = k.CtrlPressed; shift = k.ShiftPressed; alt = k.AltPressed; meta = k.MetaPressed;
            var kc = k.Keycode;
            if (ReverseKeyMap.TryGetValue(kc, out var name)) main = name;
            else if (kc >= Key.A && kc <= Key.Z) main = ((char)kc).ToString();
            else if (kc >= Key.Key0 && kc <= Key.Key9) main = ((int)(kc - Key.Key0)).ToString();
            else if (kc >= Key.F1 && kc <= Key.F12) main = "F" + (int)(kc - Key.F1 + 1);
            else main = kc.ToString();
        }
        else if (evt is InputEventMouseButton m)
        {
            ctrl = m.CtrlPressed; shift = m.ShiftPressed; alt = m.AltPressed; meta = m.MetaPressed;
            foreach (var kvp in MouseMap)
                if (kvp.Value == m.ButtonIndex) { main = kvp.Key; break; }
            if (main == "") main = m.ButtonIndex.ToString();
        }
        else return "";

        var prefix = "";
        if (ctrl) prefix += "Ctrl+";
        if (shift) prefix += "Shift+";
        if (alt) prefix += "Alt+";
        if (meta) prefix += "Meta+";
        return prefix + main;
    }
}
