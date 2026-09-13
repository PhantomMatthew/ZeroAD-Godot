using Godot;

namespace ZeroAD.Godot;

/// <summary>地图天空盒(原版 art/environments 的 &lt;SkySet&gt; →
/// art/textures/skies/{name}/ 贴图)。C++ SkyManager 装 6 面立方体:
/// front/back/left/right/top,底面复用 top——地平线以下仍是天空,
/// 地图边缘不会露出虚空黑块。</summary>
public static class SkyBox
{
    /// <summary>按 SkySet 名加载天空。优先 6 面 cubemap(底=top);
    /// 缺面时用 front 全景。无贴图返回 null(调用方回落程序化天空)。</summary>
    public static Sky? Load(string skySet)
    {
        if (FindSkyDir(skySet) == null) return null;

        Sky? cube = TryLoadCubemap(skySet);
        if (cube != null) return cube;

        Texture2D? front = LoadFaceTex(skySet, "front") ?? LoadFaceTex(skySet, "top");
        if (front == null) return null;

        var shader = GD.Load<Shader>("res://Shaders/sky_panorama.gdshader");
        if (shader != null)
        {
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("panorama", front);
            return new Sky { SkyMaterial = mat };
        }

        return new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial { Panorama = front },
        };
    }

    /// <summary>程序化天空兜底。地面半球贴地平线色——C++ cubemap 底面是 top 贴图,
    /// 图缘看到的是天空而不是黑。天顶/地面深处略暗即可。</summary>
    public static Sky CreateProcedural()
    {
        var horizon = new Color(0.65f, 0.72f, 0.85f);
        var mat = new ProceduralSkyMaterial
        {
            SkyHorizonColor = horizon,
            SkyTopColor = new Color(0.35f, 0.5f, 0.75f),
            GroundHorizonColor = horizon,
            GroundBottomColor = new Color(0.45f, 0.55f, 0.70f),
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

    /// <summary>C++ SkyManager:层序 front/back/top/top/right/left 上传成立方体。
    /// Godot cubemap 层序是 X+ X- Y+ Y- Z+ Z-(Y+ 上,Z- 前)。</summary>
    private static Sky? TryLoadCubemap(string skySet)
    {
        Image? right = LoadFaceImage(skySet, "right");
        Image? left = LoadFaceImage(skySet, "left");
        Image? top = LoadFaceImage(skySet, "top");
        Image? back = LoadFaceImage(skySet, "back");
        Image? front = LoadFaceImage(skySet, "front");
        if (right == null || left == null || top == null || back == null || front == null)
            return null;

        Image bottom = new Image();
        bottom.CopyFrom(top);

        var fmt = right.GetFormat();
        foreach (var img in new[] { right, left, top, bottom, back, front })
        {
            if (img.GetWidth() != right.GetWidth() || img.GetHeight() != right.GetHeight())
                return null;
            if (img.GetFormat() != fmt)
                img.Convert(fmt);
        }

        var cube = new Cubemap();
        var err = cube.CreateFromImages(new global::Godot.Collections.Array<Image>
        {
            right, left, top, bottom, back, front,
        });
        if (err != Error.Ok) return null;

        var shader = GD.Load<Shader>("res://Shaders/sky_cubemap.gdshader");
        if (shader == null) return null;
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("source_panorama", cube);
        return new Sky { SkyMaterial = mat };
    }

    private static Texture2D? LoadFaceTex(string skySet, string stem)
    {
        var img = LoadFaceImage(skySet, stem);
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }

    private static Image? LoadFaceImage(string skySet, string stem)
    {
        foreach (var name in new[] { stem + ".png", stem + ".dds" })
        {
            string? path = RuntimePaths.FindPublicPath("art", "textures", "skies", skySet, name);
            if (path == null) continue;
            try
            {
                var img = Image.LoadFromFile(path);
                if (img != null) return img;
            }
            catch { /* 下一扩展名 */ }
        }
        return null;
    }

    private static string? FindSkyDir(string skySet) =>
        RuntimePaths.FindPublicPath("art", "textures", "skies", skySet);
}
