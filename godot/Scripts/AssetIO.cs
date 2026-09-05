using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// res:// 资产直读的 PCK 兼容层:导出后 res:// 是虚拟路径,.NET System.IO 读不到,
/// 这里统一走 FileAccess/DirAccess(引擎级,PCK 可列可读),开发期散件回退
/// GlobalizePath + System.IO。只改读取机制,不改调用方的路径拼法、缓存与 null 回退语义。
/// </summary>
public static class AssetIO
{
    /// <summary>res:// 文件存在性(FileAccess 优先;失败回退开发期散件的 System.IO)。</summary>
    public static bool ExistsRes(string resPath)
    {
        if (global::Godot.FileAccess.FileExists(resPath)) return true;
        return File.Exists(ProjectSettings.GlobalizePath(resPath));
    }

    /// <summary>读 res:// 文件全字节;不存在/读失败返回 null。</summary>
    public static byte[]? ReadBytes(string resPath)
    {
        var f = global::Godot.FileAccess.Open(resPath, global::Godot.FileAccess.ModeFlags.Read);
        if (f != null)
        {
            byte[] bytes = f.GetBuffer((long)f.GetLength());
            f.Close();
            return bytes;
        }
        string abs = ProjectSettings.GlobalizePath(resPath);
        return File.Exists(abs) ? File.ReadAllBytes(abs) : null;
    }

    /// <summary>按扩展名从字节解码图像(png/jpg/tga/dds/webp;其他返回 null)。
    /// 与 Image.LoadFromFile 同语义:读源文件字节,不走导入缓存。</summary>
    public static Image? LoadImageRes(string resPath)
    {
        byte[]? bytes = ReadBytes(resPath);
        if (bytes == null || bytes.Length == 0) return null;
        var img = new Image();
        Error err = Path.GetExtension(resPath).ToLowerInvariant() switch
        {
            ".png" => img.LoadPngFromBuffer(bytes),
            ".jpg" or ".jpeg" => img.LoadJpgFromBuffer(bytes),
            ".tga" => img.LoadTgaFromBuffer(bytes),
            ".dds" => img.LoadDdsFromBuffer(bytes),
            ".webp" => img.LoadWebpFromBuffer(bytes),
            _ => Error.FileUnrecognized,
        };
        return err == Error.Ok ? img : null;
    }

    /// <summary>从 res:// 字节加载自包含 GLB → PackedScene(AppendFromBuffer,
    /// basePath 传 resPath 目录部分防外部引用;Pack 前统一设 Owner,与调用方原逻辑一致)。</summary>
    public static PackedScene? LoadGlbRes(string resPath)
    {
        byte[]? bytes = ReadBytes(resPath);
        if (bytes == null) return null;
        var doc = new GltfDocument();
        var state = new GltfState();
        int slash = resPath.LastIndexOf('/');
        string basePath = slash > 0 ? resPath[..slash] : "";
        if (doc.AppendFromBuffer(bytes, basePath, state) != Error.Ok) return null;
        var root = doc.GenerateScene(state);
        if (root == null) return null;
        PackedScene? result = null;
        SetOwnerRecursive(root, root);
        var packed = new PackedScene();
        if (packed.Pack(root) == Error.Ok)
            result = packed;
        root.QueueFree();
        return result;
    }

    /// <summary>列 res:// 目录内文件名(不含子目录;DirAccess 优先,回退 System.IO)。</summary>
    public static string[] ListFilesRes(string resDir)
    {
        var d = DirAccess.Open(resDir);
        if (d != null)
            return d.GetFiles();
        string abs = ProjectSettings.GlobalizePath(resDir);
        if (!Directory.Exists(abs)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (string f in Directory.GetFiles(abs))
        {
            string? name = Path.GetFileName(f);
            if (name != null) names.Add(name);
        }
        return names.ToArray();
    }

    /// <summary>列 res:// 目录内子目录名(DirAccess 优先,回退 System.IO)。</summary>
    public static string[] ListDirsRes(string resDir)
    {
        var d = DirAccess.Open(resDir);
        if (d != null)
            return d.GetDirectories();
        string abs = ProjectSettings.GlobalizePath(resDir);
        if (!Directory.Exists(abs)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (string sub in Directory.GetDirectories(abs))
        {
            string? name = Path.GetFileName(sub);
            if (name != null) names.Add(name);
        }
        return names.ToArray();
    }

    /// <summary>递归列 res:// 目录下全部文件(返回相对 resDir 的 '/' 路径)。</summary>
    public static string[] ListFilesRecursiveRes(string resDir)
    {
        var result = new List<string>();
        CollectRecursive(resDir, "", result);
        return result.ToArray();
    }

    private static void CollectRecursive(string resDir, string prefix, List<string> result)
    {
        foreach (string f in ListFilesRes(resDir))
            result.Add(prefix + f);
        foreach (string sub in ListDirsRes(resDir))
            CollectRecursive(resDir + "/" + sub, prefix + sub + "/", result);
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            SetOwnerRecursive(child, root);
        }
    }
}
