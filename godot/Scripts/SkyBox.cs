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

        // 原版 5 面(back/front/left/right/top)。拼 PanoramaSkyMaterial:Godot 接受单张
        // 全景贴图;用 front(主视野)作全景近似(top 次选)。逐"文件"探测(经
        // RuntimePaths):镜像里 DDS 已转 PNG(运行时解不了 DDS),文件级查询才会
        // 命中镜像;junction 原目录仍可用(.dds 兜底)。
        string? frontPath = null;
        foreach (var name in new[] { "front.png", "front.dds", "top.png", "top.dds" })
        {
            frontPath = RuntimePaths.FindPublicPath(
                "art", "textures", "skies", skySet, name);
            if (frontPath != null) break;
        }
        if (frontPath == null) return null;

        Texture2D? tex = LoadTexture(frontPath);
        if (tex == null) return null;

        // 自定义 sky shader(地平线以下纯黑,见 shader 注释);加载失败退回
        // PanoramaSkyMaterial(地平线以下会裹贴图下半部,略亮,仅为兜底)。
        var shader = GD.Load<Shader>("res://Shaders/sky_panorama.gdshader");
        if (shader != null)
        {
            var mat = new ShaderMaterial();
            mat.SetShaderParameter("panorama", tex);
            return new Sky { SkyMaterial = mat };
        }

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
            // 地面半球纯黑:C++ SkyBox 只装 5 面(无底面),地平线以下漏清屏黑;
            // 图外虚空因此是黑的。此前地面半球近白,拖到地图边缘露出白色空白。
            GroundHorizonColor = Colors.Black,
            GroundBottomColor = Colors.Black,
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

    private static string? FindSkyDir(string skySet) =>
        RuntimePaths.FindPublicPath("art", "textures", "skies", skySet);
}
