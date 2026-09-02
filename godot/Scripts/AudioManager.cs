using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Godot;

/// <summary>音频管理器(纯表现层,零 sim 依赖,不进存档/锁步)。
/// 移植原版 SoundManager + Sound.js 的核心语义:
///  - 模板 <Sound><SoundGroups> 事件 → 声音组 XML({lang}/{phenotype} 占位替换);
///  - 声音组 XML:Path 前缀 + Sound 变体列表 + Gain/RandPitch(随机变体+随机音高);
///  - 音乐播放列表(MENU/PEACE,对齐 gui/common/music.js 曲目表,shuffle + 循环)。
/// 音量走 UserConfig:sound.mastergain/musicgain/uigain/actiongain(与原版 options 同键)。</summary>
public static class AudioManager
{
    private const int PoolSize = 12;
    private const int Pool3DSize = 16;
    private static readonly List<AudioStreamPlayer> _pool = new();
    // 3D 池(原版 CSoundManager 位置音:战斗/死亡/建造等世界事件按相机距离衰减)。
    private static readonly List<AudioStreamPlayer3D> _pool3D = new();
    private static int _pool3DNext;
    private static Node3D? _worldHost;   // 3D 播放器的挂载点(Main 场景根;菜单 = null)
    private static int _poolNext;
    private static AudioStreamPlayer? _music;
    private static AudioStreamPlayer? _ambient;

    private static string? _dataRoot;
    private static readonly Dictionary<string, SoundGroupDef?> _groupCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AudioStream?> _streamCache = new(StringComparer.Ordinal);
    private static readonly Random _rng = new();   // 表现层随机(变体/音高),不进 sim

    private static float _masterGain = 1f, _musicGain = 1f, _uiGain = 1f, _actionGain = 1f, _ambientGain = 1f;

    private sealed class SoundGroupDef
    {
        public string[] Files = Array.Empty<string>();
        public float Gain = 1f;
        public float PitchLower = 1f, PitchUpper = 1f;
    }

    /// <summary>初始化(每场景一次):host = 挂播放器的节点(场景根),dataRoot =
    /// binaries/data/mods/public(null → 全部静默)。读音量配置。
    /// 音频文件解析与原版 VFS 一致:public 优先,回落 mod(如 ui_button_click 在
    /// mods/mod/audio 而不在 public)。
    /// 播放器随场景销毁:重进场景时检测失效引用并重建(否则场景切换后 AddChild 悬垂节点)。</summary>
    public static void Init(Node host, string? dataRoot)
    {
        _dataRoot = dataRoot;

        if (_pool.Count > 0 && !GodotObject.IsInstanceValid(_pool[0]))
            _pool.Clear();   // 上一场景的播放器已随场景销毁
        if (_pool.Count == 0)
        {
            for (int i = 0; i < PoolSize; i++)
                _pool.Add(new AudioStreamPlayer { Bus = "Master" });
            _poolNext = 0;
        }
        foreach (var p in _pool)
            if (p.GetParent() == null) host.AddChild(p);

        if (_music != null && !GodotObject.IsInstanceValid(_music))
            _music = null;
        if (_music == null)
        {
            _music = new AudioStreamPlayer { Bus = "Master" };
            // 播放列表推进:一曲播完自动下一首(原版 startPlayList 循环语义)。
            _music.Finished += OnMusicFinished;
        }
        if (_music.GetParent() == null) host.AddChild(_music);

        if (_ambient != null && !GodotObject.IsInstanceValid(_ambient))
            _ambient = null;

        ReadVolumes(host);
    }

    private static void OnMusicFinished()
    {
        if (_playlist.Count == 0) return;
        _playlistIndex = (_playlistIndex + 1) % _playlist.Count;
        PlayInternal(_playlist[_playlistIndex]);
    }

    private static void ReadVolumes(Node host)
    {
        try
        {
            var cfg = host.GetNode<UserConfig>("/root/UserConfig");
            _masterGain = ParseGain(cfg.GetEffective("sound.mastergain"), 1f);
            _musicGain = ParseGain(cfg.GetEffective("sound.musicgain"), 1f);
            _uiGain = ParseGain(cfg.GetEffective("sound.uigain"), 1f);
            _actionGain = ParseGain(cfg.GetEffective("sound.actiongain"), 1f);
            _ambientGain = ParseGain(cfg.GetEffective("sound.ambientgain"), 1f);
        }
        catch (Exception) { /* 工具/测试环境静默 */ }
    }

    /// <summary>音量配置改动后重读(UserConfig.ConfigChanged 订阅方调用)。
    /// 正在播放的音乐/环境音立即应用新增益;SFX 池下次播放生效。</summary>
    public static void RefreshVolumes(Node host)
    {
        ReadVolumes(host);
        if (_music != null && _music.Playing)
            _music.VolumeDb = Mathf.LinearToDb(Mathf.Max(_masterGain * _musicGain, 0.0001f));
        if (_ambient != null && _ambient.Playing)
            _ambient.VolumeDb = Mathf.LinearToDb(Mathf.Max(_masterGain * _ambientGain, 0.0001f));
    }

    // ── 环境音(ambient;loop 长音景)──

    /// <summary>开始环境音循环(如 "ambient/dayscape/day_temperate.xml";ambientgain 链)。
    /// 单实例:重复调用先停旧的。</summary>
    public static void StartAmbient(string groupPath, Node host)
    {
        if (_dataRoot == null) return;
        var def = LoadGroup(groupPath);
        if (def == null || def.Files.Length == 0) return;
        // 组内多变体随机取一(环境音景每次进入不同氛围,同 RandOrder 语义)
        var stream = LoadStream(ResolveAudio(def.Files[_rng.Next(def.Files.Length)]));
        if (stream == null) return;
        if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;

        if (_ambient == null)
            _ambient = new AudioStreamPlayer { Bus = "Master" };
        if (_ambient.GetParent() == null) host.AddChild(_ambient);
        _ambient.Stream = stream;
        _ambient.VolumeDb = Mathf.LinearToDb(Mathf.Max(_masterGain * _ambientGain * def.Gain, 0.0001f));
        _ambient.Play();
    }

    public static void StopAmbient() => _ambient?.Stop();

    private static float ParseGain(string s, float dflt)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

    // ── 声音组播放 ──

    /// <summary>播声音组(如 "interface/select/resource/sel_tree.xml")。
    /// channel: "ui" / "action"(决定增益链)。</summary>
    public static void PlayGroup(string groupPath, string channel = "action")
    {
        if (_dataRoot == null) return;
        var def = LoadGroup(groupPath);
        if (def == null || def.Files.Length == 0) return;

        string file = def.Files[_rng.Next(def.Files.Length)];
        var stream = LoadStream(ResolveAudio(file));
        if (stream == null) return;

        var player = NextPlayer();
        player.Stream = stream;
        float gain = def.Gain * _masterGain * (channel == "ui" ? _uiGain : _actionGain);
        player.VolumeDb = Mathf.LinearToDb(Mathf.Max(gain, 0.0001f));
        player.PitchScale = def.PitchLower + (float)_rng.NextDouble() * (def.PitchUpper - def.PitchLower);
        player.Play();
    }

    /// <summary>注册 3D 世界宿主(Main 场景;菜单/无宿主时位置音退化为 2D 池)。
    /// 重进场景时旧播放器随场景销毁,检测失效引用重建(与 2D 池同款)。</summary>
    public static void Init3D(Node3D worldHost)
    {
        _worldHost = worldHost;
        if (_pool3D.Count > 0 && !GodotObject.IsInstanceValid(_pool3D[0]))
            _pool3D.Clear();
        if (_pool3D.Count == 0)
            for (int i = 0; i < Pool3DSize; i++)
                // 原版距离衰减(CSoundManager 反比衰减):UnitSize 内全量,
                // 外圈反比滚降,MaxDistance 外静音。MaxDistance 原版按听力范围,
                // 取 250m(大半个屏)。
                _pool3D.Add(new AudioStreamPlayer3D
                {
                    Bus = "Master",
                    UnitSize = 15f,
                    MaxDistance = 250f,
                    AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                });
        foreach (var p in _pool3D)
            if (p.GetParent() == null) worldHost.AddChild(p);
    }

    /// <summary>清 3D 宿主(离场场景;播放器随场景销毁,引用摘除防悬垂)。</summary>
    public static void Clear3D()
    {
        _pool3D.Clear();
        _worldHost = null;
    }

    /// <summary>位置化播声音组(原版世界事件音:pos 处发声,按与监听者(相机)
    /// 距离衰减)。无 3D 宿主(菜单/测试)→ 回落 2D 池。</summary>
    public static void PlayGroupAt(string groupPath, Vector3 worldPos, string channel = "action")
    {
        if (_dataRoot == null) return;
        if (_worldHost == null || _pool3D.Count == 0)
        {
            PlayGroup(groupPath, channel);
            return;
        }
        var def = LoadGroup(groupPath);
        if (def == null || def.Files.Length == 0) return;
        var stream = LoadStream(ResolveAudio(def.Files[_rng.Next(def.Files.Length)]));
        if (stream == null) return;

        var player = _pool3D[_pool3DNext];
        _pool3DNext = (_pool3DNext + 1) % _pool3D.Count;
        player.Stream = stream;
        player.Position = worldPos;
        float gain = def.Gain * _masterGain * _actionGain;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Max(gain, 0.0001f));
        player.PitchScale = def.PitchLower + (float)_rng.NextDouble() * (def.PitchUpper - def.PitchLower);
        player.Play();
    }

    /// <summary>位置化模板事件(世界事件专用;select/order_* 人声留在 2D——
    /// 原版选令语音是界面反馈不走空间衰减)。</summary>
    public static void PlayUnitEventAt(TemplateLoader? templates, string templateName,
        string eventName, Vector3 worldPos)
    {
        if (templates == null || _dataRoot == null) return;
        ParamNode node;
        try { node = templates.LoadTemplate(templateName); }
        catch (Exception) { return; }
        var sg = node.GetChild("Sound").GetChild("SoundGroups").GetChild(eventName);
        if (!sg.IsOk) return;
        string path = sg.ToString().Trim();
        if (path.Length == 0) return;
        string lang = ReadChild(node, "Identity", "Lang");
        string pheno = ReadChild(node, "Identity", "Phenotype");
        if (lang.Length == 0) lang = "global";
        if (pheno.Length == 0) pheno = "male";
        PlayGroupAt(path.Replace("{lang}", lang).Replace("{phenotype}", pheno), worldPos);
    }

    /// <summary>播实体的模板音效事件(select/order_walk/order_attack/order_gather/
    /// order_garrison/death/trained/attacked…)。读模板 Sound/SoundGroups/&lt;event&gt;,
    /// {lang}=Identity/Lang(civ mixin 继承),{phenotype}=Identity/Phenotype(缺省 male)。</summary>
    public static void PlayUnitEvent(TemplateLoader? templates, string templateName, string eventName)
    {
        if (templates == null || _dataRoot == null) return;
        ParamNode node;
        try { node = templates.LoadTemplate(templateName); }
        catch (Exception) { return; }

        var sg = node.GetChild("Sound").GetChild("SoundGroups").GetChild(eventName);
        if (!sg.IsOk) return;
        string path = sg.ToString().Trim();
        if (path.Length == 0) return;

        string lang = ReadChild(node, "Identity", "Lang");
        string pheno = ReadChild(node, "Identity", "Phenotype");
        if (lang.Length == 0) lang = "global";
        if (pheno.Length == 0) pheno = "male";
        path = path.Replace("{lang}", lang).Replace("{phenotype}", pheno);
        PlayGroup(path);
    }

    private static string ReadChild(ParamNode node, string parent, string child)
    {
        var c = node.GetChild(parent).GetChild(child);
        return c.IsOk ? c.ToString().Trim() : "";
    }

    /// <summary>UI 音效(uigain 链;组文件 interface/ui/&lt;name&gt;.xml)。</summary>
    public static void PlayUi(string groupName) => PlayGroup($"interface/ui/{groupName}.xml", "ui");

    // ── 音乐(对齐 music.js 曲目表)──

    private static readonly string[] MenuTracks =
    {
        "Honor_Bound.ogg", "An_old_Warhorse_goes_to_Pasture.ogg",
        "Calm_Before_the_Storm.ogg", "Juno_Protect_You.ogg",
    };
    private static readonly string[] PeaceTracks =
    {
        "Tale_of_Warriors.ogg", "Tavern_in_the_Mist.ogg", "The_Road_Ahead.ogg",
    };
    private static readonly string[] BattleTracks =
    {
        "Taiko_1.ogg", "Taiko_2.ogg",
    };
    public const string VictoryTrack = "You_are_Victorious!.ogg";
    public const string DefeatTrack = "Dried_Tears.ogg";

    private static List<string> _playlist = new();
    private static int _playlistIndex;
    private static string _musicMode = "";

    /// <summary>开始音乐播放列表(kind: "menu"/"peace"/"battle";原版 startPlayList shuffle+循环)。</summary>
    public static void StartPlaylist(string kind)
    {
        _musicMode = kind;
        var src = kind == "menu" ? MenuTracks : kind == "battle" ? BattleTracks : PeaceTracks;
        _playlist = new List<string>(src);
        for (int i = _playlist.Count - 1; i > 0; i--)   // Fisher-Yates(表现层随机)
        {
            int j = _rng.Next(i + 1);
            (_playlist[i], _playlist[j]) = (_playlist[j], _playlist[i]);
        }
        _playlistIndex = 0;
        if (_playlist.Count > 0)
            PlayInternal(_playlist[0]);
    }

    /// <summary>战斗状态切音乐(原版 music.js PEACE↔BATTLE;只在局内音乐模式下生效,
    /// 菜单/jingle 不被打扰)。原版 crossfade,本移植直切。</summary>
    public static void SetBattleMode(bool inBattle)
    {
        if (_musicMode != "peace" && _musicMode != "battle") return;
        string want = inBattle ? "battle" : "peace";
        if (want == _musicMode) return;
        StartPlaylist(want);
    }

    /// <summary>单曲 jingle(胜利/失败;打断并清空列表,播完不自动续)。</summary>
    public static void PlayJingle(string file)
    {
        _musicMode = "";
        _playlist.Clear();
        PlayInternal(file);
    }

    public static void StopMusic()
    {
        _playlist.Clear();
        _music?.Stop();
    }

    private static void PlayInternal(string file)
    {
        if (_dataRoot == null || _music == null) return;
        var stream = LoadStream(ResolveAudio("music/" + file));
        if (stream == null) return;
        _music.Stream = stream;
        _music.VolumeDb = Mathf.LinearToDb(Mathf.Max(_masterGain * _musicGain, 0.0001f));
        _music.Play();
    }

    /// <summary>原版 VFS 挂载序:mods/public 优先,mods/mod 回落。输入为 audio/ 下的
    /// 相对路径(如 "interface/ui/ui_button_click.xml")。</summary>
    private static string ResolveAudio(string relUnderAudio)
    {
        string rel = relUnderAudio.Replace('/', Path.DirectorySeparatorChar);
        string pub = Path.Combine(_dataRoot!, "audio", rel);
        if (File.Exists(pub)) return pub;
        return Path.Combine(_dataRoot!, "..", "mod", "audio", rel);
    }

    // ── 内部 ──

    private static AudioStreamPlayer NextPlayer()
    {
        var p = _pool[_poolNext];
        _poolNext = (_poolNext + 1) % _pool.Count;
        return p;
    }

    private static SoundGroupDef? LoadGroup(string groupPath)
    {
        if (_groupCache.TryGetValue(groupPath, out var cached)) return cached;
        SoundGroupDef? def = null;
        string full = ResolveAudio(groupPath);
        try
        {
            if (File.Exists(full))
            {
                var doc = XDocument.Load(full);
                var rootEl = doc.Root;
                if (rootEl != null)
                {
                    string prefix = rootEl.Element("Path")?.Value.Trim() ?? "";
                    // 原版 SoundGroup 的 <Path> 自带 "audio/" 前缀且多数无尾斜杠
                    // (如 "audio/interface/select/building");我们的 ResolveAudio
                    // 会再拼 audio/ 根——统一去前缀、补斜杠,否则整条动作音静默。
                    if (prefix.StartsWith("audio/", StringComparison.Ordinal))
                        prefix = prefix[6..];
                    if (prefix.Length > 0 && !prefix.EndsWith('/'))
                        prefix += "/";
                    var files = new List<string>();
                    foreach (var s in rootEl.Elements("Sound"))
                        files.Add(prefix + s.Value.Trim());
                    def = new SoundGroupDef
                    {
                        Files = files.ToArray(),
                        Gain = ParseFloatChild(rootEl, "Gain", 1f),
                        PitchLower = ParseFloatChild(rootEl, "PitchLower", 1f),
                        PitchUpper = ParseFloatChild(rootEl, "PitchUpper", 1f),
                    };
                    // 原版 RandPitch=1 时以 [2-Upper, Upper] 对称近似(Upper 是单边上界)。
                    if (def.PitchUpper != 1f && def.PitchLower == 1f)
                        def.PitchLower = 2f - def.PitchUpper;
                }
            }
        }
        catch (Exception) { def = null; }
        _groupCache[groupPath] = def;
        return def;
    }

    private static float ParseFloatChild(XElement root, string name, float dflt)
        => float.TryParse(root.Element(name)?.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

    private static AudioStream? LoadStream(string absPath)
    {
        if (_streamCache.TryGetValue(absPath, out var cached)) return cached;
        AudioStream? stream = null;
        try
        {
            if (File.Exists(absPath))
                stream = AudioStreamOggVorbis.LoadFromFile(absPath);
        }
        catch (Exception) { stream = null; }
        _streamCache[absPath] = stream;
        return stream;
    }
}
