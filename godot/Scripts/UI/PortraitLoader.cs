using Godot;

namespace ZeroAD.Godot;

/// <summary>肖像/图标加载器（从 HUD.LoadPortraitFromIcon 提取的共享 helper）。
/// 加载 binaries/.../art/textures/ui/session/portraits/{icon}。</summary>
public static class PortraitLoader
{
    /// <summary>从 Identity/Icon 路径加载肖像纹理。icon 如 "units/athen/infantry_spearman.png"。
    /// 返回 null 表示文件不存在（调用方应 fallback 到占位图标）。</summary>
    public static Texture2D? Load(string icon)
    {
        if (icon.Length == 0) return null;
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "ui", "session", "portraits",
                icon.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(p)) continue;
            var img = Image.LoadFromFile(p);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        return null;
    }
}
