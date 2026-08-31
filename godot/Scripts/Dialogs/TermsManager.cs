using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// TermsManager — 原版 gui/mod/gui/common/terms.js 的端口:
// initTerms(注册若干条款页) / openTerms(弹 TermsDialog,结果落配置 md5(salt+正文))
// / checkTerms(返回首个未接受页) / loadTermsAcceptance(配置值==当前哈希 → 视为已接受)。
// 条款文件相对 gui/ 目录(mod 包优先,同 TermsDialog);配置读写注入 UserConfig。
public static class TermsManager
{
    public sealed record Spec(
        string Title,
        string File,                                    // 相对 gui/(如 "modio/Disclaimer.txt")
        string Config,                                  // UserConfig 键(如 "modio.disclaimer")
        IReadOnlyDictionary<string, string>? Sprintf = null,
        IReadOnlyList<TermsDialog.UrlButton>? UrlButtons = null,
        string? TermsUrl = null,
        Func<string>? Salt = null,                      // 原版 salt():掺入哈希的动态值
        string? Instruction = null,                     // checkTerms 返回值覆盖
        Action<bool>? Callback = null);                 // 弹窗关闭回调(accepted)

    private static readonly Dictionary<string, Spec> _terms = new();
    private static readonly HashSet<string> _accepted = new();

    /// <summary>UserConfig 读写注入(同 CampaignRun 的模式,MainMenu._Ready 统一挂)。</summary>
    public static Func<string, string?>? ReadUserConfig;
    public static Action<string, string>? WriteUserConfig;

    /// <summary>initTerms:批量注册。</summary>
    public static void InitTerms(Dictionary<string, Spec> terms)
    {
        foreach (var kv in terms)
            _terms[kv.Key] = kv.Value;
    }

    public static bool IsRegistered(string page) => _terms.ContainsKey(page);

    /// <summary>openTerms:弹条款页;接受/拒绝都写配置(接受 = 哈希,拒绝 = "0")并触发回调。</summary>
    public static void OpenTerms(string page, Node parent)
    {
        if (!_terms.TryGetValue(page, out var spec)) return;
        TermsDialog.Show(parent, spec.Title, spec.File, spec.Sprintf, spec.UrlButtons, spec.TermsUrl,
            accepted =>
            {
                if (accepted) _accepted.Add(page);
                else _accepted.Remove(page);
                WriteUserConfig?.Invoke(spec.Config, accepted ? GetTermsHash(spec) : "0");
                spec.Callback?.Invoke(accepted);
            });
    }

    /// <summary>checkTerms:首个未接受页的 instruction(或页名);全接受 = ""。</summary>
    public static string CheckTerms()
    {
        foreach (var kv in _terms)
            if (!_accepted.Contains(kv.Key))
                return kv.Value.Instruction ?? kv.Key;
        return "";
    }

    /// <summary>loadTermsAcceptance:启动时重放——配置值与当前文件哈希一致即已接受
    /// (文件更新 → 哈希变 → 自动要求重新接受,原版同款机制)。</summary>
    public static void LoadTermsAcceptance()
    {
        _accepted.Clear();
        foreach (var kv in _terms)
            if (ReadUserConfig?.Invoke(kv.Value.Config) == GetTermsHash(kv.Value))
                _accepted.Add(kv.Key);
    }

    public static bool IsAccepted(string page) => _accepted.Contains(page);

    /// <summary>getTermsHash:md5(salt + 文件正文) hex。</summary>
    private static string GetTermsHash(Spec spec)
    {
        string content = spec.Salt?.Invoke() ?? "";
        content += ReadTermsFile(spec.File);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ReadTermsFile(string file)
    {
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) return "";
        foreach (var modDir in new[] { "mod", "public" })
        {
            string path = Path.Combine(binDir, "data", "mods", modDir, "gui",
                file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return "";
    }
}
