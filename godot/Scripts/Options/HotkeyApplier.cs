using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Options;

/// <summary>热键 InputMap 应用器。启动时 + 重绑时把组合字符串翻译成 InputMap 操作。
/// InputMap 是 session-only（不写 project.godot），故每次启动须从 UserConfig 恢复。
///
/// action 名约定：HotkeyCatalog 的 FullName（如 "hotkey.camera.left"）直接作 InputMap action 名。
/// 原 project.godot 的 6 个 cam_* action 保留不动（RTSCamera 仍读它们）；热键页额外定义原版全部
/// hotkey.* action，多数当前无消费代码但可重绑+持久化（面向未来）。</summary>
public static class HotkeyApplier
{
    /// <summary>启动时调用：遍历全部 hotkey.* action，从 UserConfig（覆盖 DefaultConfig）读组合，
    /// 在 InputMap 创建 action 并绑定。幂等（重复调用安全）。</summary>
    public static void ApplyAll(UserConfig cfg)
    {
        foreach (var action in HotkeyCatalog.AllActions)
            ApplyAction(cfg, action.FullName, GetCurrentCombos(cfg, action));
    }

    /// <summary>单个 action 重绑：写 UserConfig + 立即应用到 InputMap。</summary>
    public static void Apply(UserConfig cfg, string fullActionName, string comboString)
    {
        cfg.SetUserValue(fullActionName, comboString);
        cfg.Save();
        var combos = string.IsNullOrWhiteSpace(comboString)
            ? System.Array.Empty<string>() : new[] { comboString };
        ApplyAction(cfg, fullActionName, combos);
    }

    /// <summary>重置为默认：清除 UserConfig 覆盖 + 恢复 default.cfg 默认组合。</summary>
    public static void Reset(UserConfig cfg, string fullActionName)
    {
        cfg.ResetUserValue(fullActionName);
        cfg.Save();
        var action = FindAction(fullActionName);
        if (action != null)
            ApplyAction(cfg, fullActionName, action.DefaultCombos);
    }

    /// <summary>获取某 action 当前的有效组合列表（user 覆盖 ?? default）。</summary>
    public static IReadOnlyList<string> GetCurrentCombos(UserConfig cfg, HotkeyAction action)
    {
        var userVal = cfg.GetUserValue(action.FullName);
        if (!string.IsNullOrWhiteSpace(userVal))
            return new[] { userVal };
        return action.DefaultCombos;
    }

    private static void ApplyAction(UserConfig cfg, string actionName, IReadOnlyList<string> combos)
    {
        // 确保 InputMap 有该 action（很多 hotkey.* 不在 project.godot，需运行时创建）。
        if (!InputMap.HasAction(actionName))
            InputMap.AddAction(actionName, deadzone: 0.5f);
        InputMap.ActionEraseEvents(actionName);
        foreach (var combo in combos)
        {
            var evt = HotkeyCombo.Parse(combo);
            if (evt != null)
                InputMap.ActionAddEvent(actionName, evt);
        }
    }

    private static HotkeyAction? FindAction(string fullName)
    {
        foreach (var a in HotkeyCatalog.AllActions)
            if (a.FullName == fullName) return a;
        return null;
    }
}
