using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZeroAD.Sim.Content;

/// <summary>VFS-lite — 原版 mod 挂载的 VFS 分层解析(数据面)。
/// 原版:mod.enabledmods 列表按序挂载,**列表越靠后优先级越高**(同名文件后者覆盖前者;
/// modmod 上移 = 提优先)。本解析器对"数据根相对路径"提供两件套:
///   ResolveFile(rel):逐层倒序(高优先先查)找首个存在的文件;
///   EnumerateLayered(relDir, pattern):全部层的并集,同名(rel 路径)高优先覆盖低优先。
/// 用法:SimBridge/加载器按 UserConfig "mod.enabledmods" 构建一次注入;
/// 未配置 → ["mod","public"] 默认(原版型录默认)。
/// 注:仅 sim 数据(模板/科技/光环/地图/rmgen);godot/assets 美术资源走导入管线,
/// 运行时挂载不在此列(记 PORTING-GAPS)。</summary>
public sealed class VfsResolver
{
    private readonly string _modsRoot;
    /// <summary>启用 mod 目录名,优先级升序(末位最高)。</summary>
    private readonly IReadOnlyList<string> _mods;

    public VfsResolver(string modsRoot, IReadOnlyList<string>? enabledMods)
    {
        _modsRoot = modsRoot;
        _mods = enabledMods is { Count: > 0 } ? enabledMods : new[] { "mod", "public" };
    }

    /// <summary>UserConfig 值构建("mod public xxx" 空格分词)。</summary>
    public static VfsResolver FromConfig(string modsRoot, string enabledModsValue) =>
        new(modsRoot, enabledModsValue.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>单文件解析:高优先层先命中。返回 null = 全部层都无。</summary>
    public string? ResolveFile(string relativePath)
    {
        string rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (int i = _mods.Count - 1; i >= 0; i--)
        {
            string candidate = Path.Combine(_modsRoot, _mods[i], rel);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>分层目录枚举:并集,同名(rel 路径)高优先层覆盖低优先层。
    /// 返回 rel(正斜杠,供模板名/键)→ 绝对路径。</summary>
    public Dictionary<string, string> EnumerateLayered(string relativeDir, string searchPattern)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in _mods)   // 升序扫,后写覆盖 → 末位最高优先
        {
            string dir = Path.Combine(_modsRoot, mod,
                relativeDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                result[rel] = file;
            }
        }
        return result;
    }
}
