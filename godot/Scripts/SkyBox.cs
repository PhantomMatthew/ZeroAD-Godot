using Godot;

namespace ZeroAD.Godot;

/// <summary>地图天空盒(原版 art/environments 的 <SkySet>name</SkySet> →
/// art/textures/skies/{name}/ 5 面贴图)。原版 C++ 用 SkyBox 六面体贴图;
/// Godot 用 Sky + PanoramaSkyMaterial(单张全景)或 ProceduralSkyMaterial
/// (程序化天空)——原版 5 面 DDS 以 cubemap 载入(5 张拼 PanoramaSkyMaterial
/// 的简易全景近似,原版贴图本身即全景渲染)。SkySet 缺失走程序化天空兜底。
/// 由 MapEnvironment.Apply 在加载环境后调用(背景模式换 Sky)。</summary>
public static class SkyBox
{
    /// <summary>按 SkySet 名加载天空(art/textures/skies/{name}/)。
    /// 返回 Sky(含材质),无贴图返回 null(调用方回落程序化天空)。</summary>
    public static Sky? Load(string skySet)
    {
        string? dir = FindSkyDir(skySet);
        if (dir == null) return null;

        // 原版 5 面 DDS(back/front/left/right/top)。拼 PanoramaSkyMaterial:
        // Godot PanoramaSkyMaterial 接受单张全景贴图;原版贴图即渲染全景,
        // 用 front(主视野)作全景近似(5 面完整 cubemap 为 Godot 渲染器
        // 限制——原版 C++ SkyBox 用六面体,PanoramaSkyMaterial 只接全景)。
        string frontPath = System.IO.Path.Combine(dir, "front.dds");
        if (!System.IO.File.Exists(frontPath))
            frontPath = System.IO.Path.Combine(dir, "top.dds");
        if (!System.IO.File.Exists(frontPath)) return null;

        Texture2D? tex = LoadTexture(frontPath);
        if (tex == null) return null;

        return new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial
            {
                Panorama = tex,
            },
        };
    }

    /// <summary>程序化天空兜底(原版无 SkySet 时的回退——比纯色背景生动;
    /// 太阳角度/云量由 MapEnvironment 的 SunColor/Fog 段调色)。</summary>
    public static Sky CreateProcedural()
    {
        var mat = new ProceduralSkyMaterial
        {
            SkyHorizonColor = new Color(0.65f, 0.72f, 0.85f),
            SkyTopColor = new Color(0.35f, 0.5f, 0.75f),
            GroundHorizonColor = new Color(0.7f, 0.75f, 0.8f),
            GroundBottomColor = new Color(0.35f, 0.4f, 0.45f),
            SunAngleMax = 25f,
            SunCurve = 0.15f,
        };
        return new Sky { SkyMaterial = mat };
    }

    /// <summary>施加到 WorldEnvironment(原版 SkyBox 装载时背景模式换 Sky)。</summary>
    public static void Apply(global::Godot.Environment env, Sky? sky)
    {
        if (sky == null) return;
        env.BackgroundMode = global::Godot.Environment.BGMode.Sky;
        env.Sky = sky;
        env.SkyRotation = Vector3.Zero;
    }

    private static Texture2D? LoadTexture(string path)
    {
        try
        {
            var img = Image.LoadFromFile(path);
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
        catch { return null; }
    }

    private static string? FindSkyDir(string skySet)
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "skies", skySet));
            if (System.IO.Directory.Exists(dir)) return dir;
        }
        return null;
    }
}
