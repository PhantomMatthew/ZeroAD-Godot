using System;
using System.Globalization;
using Godot;

namespace ZeroAD.Godot.Options;

// Options 即时生效映射层。原版两条生效路径在此合并:① options.js 的 Engine[option.function](newValue)
// (5 音量 + gui.scale + pauseonfocusloss);② C++ 引擎各系统在配置变更时自读 config(图形/显示项)。
// Godot 无对应引擎自读,集中在此按 config 键分发到 Godot API。三档(对齐用户选定的"全列出并近似实现"):
// ✅直接等价(windowed/vsync/gui.scale/音量/AA/渲染缩放/鼠标钳制/FPS 上限) /
// 🔶近似(shadows→DirectionalLight,fog/postproc→WorldEnvironment,shadowquality→阴影图集,
//    shadowscutoffdistance→阴影距离,upscale.technique→Scaling3DMode) /
// ⬜列出但无对应(rendererbackend/gpuskinning/silhouettes/材质与水面系/adaptivefps 以外的
//    玩法与联网项——持久化,消费方随 gameplay/MP 后续落地)。
// Apply 幂等:单项改动、Revert/Reset 重放、场景启动(ApplyAll)全走同一入口。
public static class OptionsApplier
{
    // Main._Ready 注册会话场景节点;MainMenu 无 → 场景相关项 no-op(值仍持久化,进 session 后
    // ApplyAll 重放生效)。ChangeScene 时 Main._ExitTree 注销,防静态引用悬垂。
    private static DirectionalLight3D? _light;
    private static WorldEnvironment? _worldEnv;

    public static void RegisterSceneNodes(DirectionalLight3D? light, WorldEnvironment? env)
    {
        _light = light;
        _worldEnv = env;
    }

    /// <summary>读选项的生效值(用户值优先,否则默认)——场景建世界时用,避免硬编码
    /// 覆盖用户选择(如 fog 关)。</summary>
    public static bool GetBool(string configKey, bool fallback)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var cfg = tree?.Root.GetNodeOrNull<UserConfig>("/root/UserConfig");
        if (cfg == null) return fallback;
        return cfg.GetEffective(configKey) == "true";
    }

    /// <summary>全量重放(启动/Revert/Reset):先建音频总线,再按 catalog 顺序应用全部 96 项。
    /// inGame 决定 adaptivefps 取 session 还是 menu 值(原版分别在局内/菜单限帧)。</summary>
    public static void ApplyAll(UserConfig cfg, SceneTree tree, bool inGame)
    {
        EnsureAudioBuses();
        foreach (var cat in OptionsCatalog.Categories)
            foreach (var opt in cat.Options)
                Apply(opt, cfg.GetEffective(opt.Config), cfg, tree, inGame);
    }

    /// <summary>应用单项(改动即时生效路径)。value 为 config 字符串(控件层已字符串化)。</summary>
    public static void Apply(OptionDef opt, string value, UserConfig cfg, SceneTree tree, bool inGame)
    {
        switch (opt.Config)
        {
            // ── 7 个 function 项(原版 Engine[fn](newValue)) ──
            case "sound.mastergain": SetBusGain("Master", value); break;
            case "sound.musicgain": SetBusGain("Music", value); break;
            case "sound.ambientgain": SetBusGain("Ambient", value); break;
            case "sound.actiongain": SetBusGain("Action", value); break;
            case "sound.uigain": SetBusGain("UI", value); break;
            case "gui.scale":
                // 原版 g_VideoMode.Rescale(scale) 整体缩放 GUI。
                tree.Root.ContentScaleFactor = Num(value, 1f);
                break;
            case "pauseonfocusloss":
                // 无即时调用——Main 的 NotificationWMApplicationFocusOut 处实时读 GetEffective。
                break;

            // ── 显示(✅直接等价) ──
            case "windowed":
                DisplayServer.WindowSetMode(Bool(value)
                    ? DisplayServer.WindowMode.Windowed
                    : DisplayServer.WindowMode.Fullscreen);
                ApplyMouseGrab(cfg);   // 窗口模式变化后重估鼠标钳制
                break;
            case "vsync":
                // 上游 default.cfg 是 vsync=false;但 Engine.MaxFps 的 sleep 在 macOS 粒度太粗
                // (上限 60 实测只出 ~50fps),vsync 关闭时它是唯一节拍器。故用户未显式选择时
                // 默认开(显示节拍精确,实测 98fps@120Hz);用户在选项里关掉则回退到上限 pacing。
                bool vsyncOn = cfg.GetUserValue("vsync") is { } userVsync
                    ? userVsync == "true"
                    : true;
                DisplayServer.WindowSetVsyncMode(vsyncOn
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                ApplyFpsCap(cfg, inGame);   // vsync 开关节拍权变化,重估 FPS 上限
                break;
            case "window.mousegrabinfullscreen":
            case "window.mousegrabinwindowmode":
                ApplyMouseGrab(cfg);
                break;
            case "renderer.scale":
                tree.Root.Scaling3DScale = Mathf.Clamp(Num(value, 1f), 0.33f, 4f);
                ApplyScaling3DMode(cfg, tree);
                break;
            case "antialiasing":
                ApplyAntialiasing(value, tree);
                break;

            // ── 图形(🔶近似) ──
            case "shadows":
                if (_light != null) _light.ShadowEnabled = Bool(value);
                break;
            case "shadowscutoffdistance":
                if (_light != null) _light.DirectionalShadowMaxDistance = Num(value, 300f);
                break;
            case "shadowquality":
                // 原版阴影贴图分辨率档(-1/0/1/2)→ Godot 方向光阴影图集尺寸。
                RenderingServer.DirectionalShadowAtlasSetSize(value switch
                {
                    "-1" => 1024,
                    "1" => 4096,
                    "2" => 8192,
                    _ => 2048,   // "0" 默认 Medium
                }, true);
                break;
            case "fog":
                // 距离雾(WorldEnvironment)——非战争迷雾(FogWorldRenderer),两者无关。
                if (_worldEnv?.Environment != null)
                    _worldEnv.Environment.FogEnabled = Bool(value);
                break;
            case "postproc":
                // 原版 HDR/Bloom/DOF 后处理链 → Godot 用 Glow 近似(无可运行时切换的等价链)。
                if (_worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowEnabled = Bool(value);
                break;
            case "renderer.upscale.technique":
                ApplyScaling3DMode(cfg, tree);
                break;
            case "adaptivefps.menu":
                if (!inGame) ApplyFpsCap(cfg, inGame);
                break;
            case "adaptivefps.session":
                if (inGame) ApplyFpsCap(cfg, inGame);
                break;

            // ── ⬜ 其余 70 余项:列出并持久化,Godot 无对应或消费方未落地(rendererbackend/
            //    gpuskinning/silhouettes/max_actor_quality/variant_diversity/materialmgr.quality/
            //    sharpening/sharpness/shadowpcf/shadowscovermap/textures.*/water 系/particles/
            //    renderer.renderwhenoutoffocus/playername.*/gui.*/lobby.*/network.*/chat.*/sound.notify.*) ──
        }
    }

    /// <summary>建 Master 之外的 4 条音量总线(Music/Ambient/Action/UI,对齐原版 5 路 gain)。
    /// 运行时建(代替 default_bus_layout.tres),幂等;项目暂无音频资源,总线结构先行真实。</summary>
    public static void EnsureAudioBuses()
    {
        foreach (var name in new[] { "Music", "Ambient", "Action", "UI" })
        {
            if (AudioServer.GetBusIndex(name) != -1) continue;
            int at = AudioServer.BusCount;
            AudioServer.AddBus(at);
            AudioServer.SetBusName(at, name);
            AudioServer.SetBusSend(at, "Master");
        }
    }

    private static void SetBusGain(string bus, string value)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0) return;
        float gain = Mathf.Max(Num(value, 1f), 0f);
        // 原版 OpenAL 线性 gain(0-2)→ Godot 总线 dB;0 → 静音(-80dB)。
        AudioServer.SetBusVolumeDb(idx, gain <= 0.0001f ? -80f : Mathf.LinearToDb(gain));
    }

    private static void ApplyAntialiasing(string value, SceneTree tree)
    {
        var vp = tree.Root;
        switch (value)
        {
            case "fxaa":
                vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa;
                vp.Msaa3D = Viewport.Msaa.Disabled;
                break;
            case "msaa2": vp.Msaa3D = Viewport.Msaa.Msaa2X; vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; break;
            case "msaa4": vp.Msaa3D = Viewport.Msaa.Msaa4X; vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; break;
            case "msaa8": vp.Msaa3D = Viewport.Msaa.Msaa8X; vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; break;
            // Godot MSAA 上限 8x——msaa16 钳到 8x(近似)。
            case "msaa16": vp.Msaa3D = Viewport.Msaa.Msaa8X; vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; break;
            default:    // "disabled"
                vp.Msaa3D = Viewport.Msaa.Disabled;
                vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
                break;
        }
    }

    private static void ApplyScaling3DMode(UserConfig cfg, SceneTree tree)
    {
        // 仅在渲染缩放 ≠ 1 时模式才有意义;pixelated 无 Godot 对应,回落 Bilinear(近似)。
        // FSR 仅 Forward+/Mobile 支持——compatibility(opengl3)下设置会触发原生报错,强制回落 Bilinear。
        string technique = cfg.GetEffective("renderer.upscale.technique");
        bool fsrCapable = RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";
        tree.Root.Scaling3DMode = technique == "fsr" && fsrCapable
            ? Viewport.Scaling3DModeEnum.Fsr
            : Viewport.Scaling3DModeEnum.Bilinear;
    }

    private static void ApplyMouseGrab(UserConfig cfg)
    {
        bool fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
        bool grab = Bool(cfg.GetEffective(fullscreen
            ? "window.mousegrabinfullscreen"
            : "window.mousegrabinwindowmode"));
        Input.MouseMode = grab ? Input.MouseModeEnum.Confined : Input.MouseModeEnum.Visible;
    }

    /// <summary>FPS 上限:vsync 开启时由显示节拍 pacing,装 Engine.MaxFps 反而有害——
    /// 其 sleep 粒度在 macOS 上把 ~6ms 的帧过冲到 ~20ms(实测上限 60 只跑出 ~50fps;
    /// vsync-only 98fps)。vsync 关闭时才按 adaptivefps.menu/.session 装上限。</summary>
    private static void ApplyFpsCap(UserConfig cfg, bool inGame)
    {
        if (DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Enabled)
        {
            Engine.MaxFps = 0;
            return;
        }
        string key = inGame ? "adaptivefps.session" : "adaptivefps.menu";
        Engine.MaxFps = (int)Mathf.Clamp(Num(cfg.GetEffective(key), 60f), 20f, 360f);
    }

    /// <summary>对齐原版 configToValue:boolean 即 =="true"。</summary>
    private static bool Bool(string value) => value == "true";

    private static float Num(string value, float dflt) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;
}
