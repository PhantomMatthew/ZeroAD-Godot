using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Actors;

/// <summary>
/// Scans converted game assets under res://assets/ once (lazily, thread-safely) and
/// resolves original 0 A.D. art paths (DAE / PNG-with-skins-prefix) to Godot-relative paths.
/// </summary>
public sealed class AssetPathResolver
{
	public sealed record Result<T>(T? Value, bool Found, string OriginalPath)
	{
		public static Result<T> Miss(string originalPath) => new(default, false, originalPath);
	}

	private static readonly Lazy<AssetPathResolver> _instance = new(CreateInstance);
	public static AssetPathResolver Instance => _instance.Value;

	private readonly HashSet<string> _meshRelPaths;
	private readonly HashSet<string> _texRelPaths;
	private readonly HashSet<string> _animRelPaths;
	private readonly Dictionary<string, string> _meshByBasename;
	private readonly Dictionary<string, string> _texByBasename;
	private readonly Dictionary<string, string> _animByBasename;
	private readonly HashSet<string> _warned = new();
	private readonly object _warnLock = new();

	private AssetPathResolver(
		HashSet<string> meshRelPaths,
		HashSet<string> texRelPaths,
		HashSet<string> animRelPaths,
		Dictionary<string, string> meshByBasename,
		Dictionary<string, string> texByBasename,
		Dictionary<string, string> animByBasename)
	{
		_meshRelPaths = meshRelPaths;
		_texRelPaths = texRelPaths;
		_animRelPaths = animRelPaths;
		_meshByBasename = meshByBasename;
		_texByBasename = texByBasename;
		_animByBasename = animByBasename;
	}

	private static AssetPathResolver CreateInstance()
	{
		string assetsAbs = ProjectSettings.GlobalizePath("res://assets");
		var meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		string meshesRoot = Path.Combine(assetsAbs, "meshes");
		if (Directory.Exists(meshesRoot))
		{
			foreach (var f in Directory.GetFiles(meshesRoot, "*.glb", SearchOption.AllDirectories))
				if (!f.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
					meshes.Add(Path.GetRelativePath(meshesRoot, f).Replace('\\', '/'));
		}

		string texRoot = Path.Combine(assetsAbs, "textures");
		if (Directory.Exists(texRoot))
		{
			foreach (var f in Directory.GetFiles(texRoot, "*.png", SearchOption.AllDirectories))
				if (!f.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
					textures.Add(Path.GetRelativePath(texRoot, f).Replace('\\', '/'));
		}

		var animations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string animRoot = Path.Combine(assetsAbs, "animations");
		if (Directory.Exists(animRoot))
		{
			// .glb (legacy Blender conversions) and .dae (Godot-native import —
			// Blender 5 dropped Collada, so new clips ship as raw DAE and load
			// through ResourceLoader instead of GltfDocument).
			foreach (var pattern in new[] { "*.glb", "*.dae" })
				foreach (var f in Directory.GetFiles(animRoot, pattern, SearchOption.AllDirectories))
					if (!f.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
						animations.Add(Path.GetRelativePath(animRoot, f).Replace('\\', '/'));
		}

		return new AssetPathResolver(
			meshes, textures, animations,
			BuildBasenameIndex(meshes),
			BuildBasenameIndex(textures),
			BuildBasenameIndex(animations));
	}

	private static Dictionary<string, string> BuildBasenameIndex(HashSet<string> relPaths)
	{
		var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var rel in relPaths)
		{
			string base_ = Path.GetFileName(rel);
			if (!index.ContainsKey(base_))
				index[base_] = rel;
		}
		return index;
	}

	public bool Exists(string relPath) =>
		_meshRelPaths.Contains(relPath) || _texRelPaths.Contains(relPath);

	public Result<string> ResolveMesh(string rawDaePath)
	{
		if (string.IsNullOrEmpty(rawDaePath))
			return Result<string>.Miss(rawDaePath);

		string glbRel = SwapOrStripExt(rawDaePath, ".glb");
		if (_meshRelPaths.Contains(glbRel))
			return new Result<string>(glbRel, true, rawDaePath);

		string basename = Path.GetFileName(glbRel);
		if (_meshByBasename.TryGetValue(basename, out var relocated))
			return new Result<string>(relocated, true, rawDaePath);

		WarnOnceMiss($"AssetPathResolver.ResolveMesh miss: '{rawDaePath}' (tried '{glbRel}', basename '{basename}')");
		return Result<string>.Miss(rawDaePath);
	}

	public Result<string> ResolveTexture(string rawTexPath)
	{
		if (string.IsNullOrEmpty(rawTexPath))
			return Result<string>.Miss(rawTexPath);

		// .dds inputs swap to .png (DDS→PNG conversion done in pipeline).
		string png = rawTexPath;
		if (png.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
			png = Path.ChangeExtension(png, ".png");

		// C++ uses art/textures/skins/ prefix; try with skins/ first, then without.
		string withSkins = png.StartsWith("skins/", StringComparison.OrdinalIgnoreCase)
			? png
			: "skins/" + png;
		if (_texRelPaths.Contains(withSkins))
			return new Result<string>(withSkins, true, rawTexPath);

		if (_texRelPaths.Contains(png))
			return new Result<string>(png, true, rawTexPath);

		string basename = Path.GetFileName(png);
		if (_texByBasename.TryGetValue(basename, out var texRelocated))
			return new Result<string>(texRelocated, true, rawTexPath);

		WarnOnceMiss($"AssetPathResolver.ResolveTexture miss: '{rawTexPath}' (tried '{withSkins}', '{png}', basename '{basename}')");
		return Result<string>.Miss(rawTexPath);
	}

	public Result<string> ResolveAnimation(string rawDaePath)
	{
		if (string.IsNullOrEmpty(rawDaePath))
			return Result<string>.Miss(rawDaePath);

		// Prefer a converted .glb when one exists (matches the mesh GLB import path
		// exactly), else fall back to the raw .dae (Godot-native import).
		string glbRel = SwapOrStripExt(rawDaePath, ".glb");
		if (_animRelPaths.Contains(glbRel))
			return new Result<string>(glbRel, true, rawDaePath);

		string glbBasename = Path.GetFileName(glbRel);
		if (_animByBasename.TryGetValue(glbBasename, out var animRelocated))
			return new Result<string>(animRelocated, true, rawDaePath);

		string daeRel = SwapOrStripExt(rawDaePath, ".dae");
		if (_animRelPaths.Contains(daeRel))
			return new Result<string>(daeRel, true, rawDaePath);

		string daeBasename = Path.GetFileName(daeRel);
		if (_animByBasename.TryGetValue(daeBasename, out var daeRelocated))
			return new Result<string>(daeRelocated, true, rawDaePath);

		WarnOnceMiss($"AssetPathResolver.ResolveAnimation miss: '{rawDaePath}' (tried '{glbRel}', '{daeRel}')");
		return Result<string>.Miss(rawDaePath);
	}

	private static string SwapOrStripExt(string path, string newExt)
	{
		if (path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase))
			return Path.ChangeExtension(path, newExt);
		if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
			return path;
		return Path.ChangeExtension(path, newExt);
	}

	private void WarnOnceMiss(string message)
	{
		lock (_warnLock)
		{
			if (!_warned.Add(message))
				return;
		}
		GD.PushWarning(message);
	}
}
