using System;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// 统一数据根解析器:所有"运行时直读文件"的入口。发行包与开发环境共用一套语义——
/// 候选顺序:ZEROAD_DATA_DIR 环境变量 → 可执行文件旁目录(含 data/ 子目录)→
/// 开发期 res:// 上溯 ../binaries、../../binaries(junction,见 AGENTS.md)。
/// 返回 null 表示没找到;调用方保持原有 null 回退(静默/默认)。
/// </summary>
public static class RuntimePaths
{
	/// <summary>显式覆盖环境变量:指向含 data/ 子目录的根(等价开发期的 binaries/)。</summary>
	public const string DataDirEnvVar = "ZEROAD_DATA_DIR";

	private static string? _binariesRoot;
	private static bool _binariesRootSearched;

	/// <summary>
	/// 数据根(开发期 = binaries/,发行包 = 可执行文件旁目录)。保证返回的目录含 data/ 子目录。
	/// </summary>
	public static string? FindBinariesRoot()
	{
		if (_binariesRootSearched) return _binariesRoot;
		_binariesRootSearched = true;

		string? env = System.Environment.GetEnvironmentVariable(DataDirEnvVar);
		if (!string.IsNullOrEmpty(env) && HasDataDir(env))
		{
			_binariesRoot = Path.GetFullPath(env);
			return _binariesRoot;
		}

		// 发行包:可执行文件旁(export 后 res:// 不可写/虚拟,exe 目录才是真实路径)。
		string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
		if (exeDir.Length > 0 && HasDataDir(exeDir))
		{
			_binariesRoot = Path.GetFullPath(exeDir);
			return _binariesRoot;
		}

		// macOS .app 包:exe 在 Contents/MacOS/,数据放在 .app 旁或包内 Contents/Resources/。
		if (exeDir.Length > 0)
		{
			string resources = Path.GetFullPath(Path.Combine(exeDir, "..", "Resources"));
			if (HasDataDir(resources))
			{
				_binariesRoot = resources;
				return _binariesRoot;
			}
		}

		// 开发期:工程根上溯 binaries junction。
		string projRoot = ProjectSettings.GlobalizePath("res://");
		foreach (string up in new[] { "..", "../.." })
		{
			string candidate = Path.GetFullPath(Path.Combine(projRoot, up, "binaries"));
			if (HasDataDir(candidate))
			{
				_binariesRoot = candidate;
				return _binariesRoot;
			}
		}
		return null;
	}

	/// <summary>mods/public 根(sim 数据 + 美术直读的主根)。</summary>
	public static string? FindPublicModRoot()
	{
		string? root = FindBinariesRoot();
		if (root == null) return null;
		string publicDir = Path.Combine(root, "data", "mods", "public");
		return Directory.Exists(publicDir) ? publicDir : null;
	}

	/// <summary>探测 mods/public 下的文件或目录;不存在返回 null。</summary>
	public static string? FindPublicPath(params string[] relParts)
	{
		string? root = FindPublicModRoot();
		if (root == null) return null;
		string path = Path.GetFullPath(Path.Combine(root, Path.Combine(relParts)));
		return File.Exists(path) || Directory.Exists(path) ? path : null;
	}

	/// <summary>探测 data/config 下的配置文件(如 default.cfg);不存在返回 null。</summary>
	public static string? FindConfigFile(string name)
	{
		string? root = FindBinariesRoot();
		if (root == null) return null;
		string path = Path.GetFullPath(Path.Combine(root, "data", "config", name));
		return File.Exists(path) ? path : null;
	}

	/// <summary>data/ 下任意相对路径探测(l10n 等);不存在返回 null。</summary>
	public static string? FindDataSubPath(params string[] relParts)
	{
		string? root = FindBinariesRoot();
		if (root == null) return null;
		string path = Path.GetFullPath(Path.Combine(root, "data", Path.Combine(relParts)));
		return File.Exists(path) || Directory.Exists(path) ? path : null;
	}

	/// <summary>仅测试/工具用:清缓存让下次查找重新探测。</summary>
	public static void ResetCache()
	{
		_binariesRoot = null;
		_binariesRootSearched = false;
	}

	private static bool HasDataDir(string root)
		=> Directory.Exists(Path.Combine(root, "data", "mods", "public"));
}
