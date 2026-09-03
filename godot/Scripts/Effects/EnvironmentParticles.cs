using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>环境粒子系统(PORTING-GAPS §7 渲染五件套之首):原版
/// art/particles/*.xml 的 GPUParticles3D 映射。原版 XML schema:
/// emissionrate/lifetime(uniform)/velocity.x/y/z/size/color.r/g/b。
/// 此处解析 XML → 程序化装配 GPUParticles3D(天空云/建筑烟尘/水面溅花/
/// 风动尘沙)。地图/表现层注册按位置触发。</summary>
public sealed partial class EnvironmentParticles : Node
{
    /// <summary>XML 粒子定义的解析结果。</summary>
    public sealed class ParticleDef
    {
        public string TexturePath = "";
        public string Blend = "mix";            // add / mix
        public float EmissionRate = 10f;
        public float LifetimeMin = 1f, LifetimeMax = 2f;
        public float AngleMin, AngleMax;
        public float VelXMin, VelXMax, VelYMin, VelYMax, VelZMin, VelZMax;
        public float VelAngleMin, VelAngleMax;
        public float SizeMin = 0.5f, SizeMax = 1f;
        public float ColorR = 1f, ColorG = 1f, ColorB = 1f;
        public float Alpha = 1f;
        /// <summary>发射体积偏移(原版 uniform position.x/y/z——雨雪的关键:
        /// ±50m 水平盒 + y 75–90 高空落点)。全 0 = 未声明(球心发射)。</summary>
        public float PosXMin, PosXMax, PosYMin, PosYMax, PosZMin, PosZMax;
        public bool HasPositionBox => PosXMin != 0 || PosXMax != 0 || PosZMin != 0 || PosZMax != 0;
    }

    private static readonly Dictionary<string, ParticleDef> _cache = new();
    private static string? _particlesDir;

    /// <summary>粒子 XML 目录(原版 art/particles;走 binaries junction,
    /// 与资产管线同路径解析)。</summary>
    public static string? FindParticlesDir()
    {
        if (_particlesDir != null) return _particlesDir;
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "particles"));
            if (System.IO.Directory.Exists(p))
            {
                _particlesDir = p;
                return p;
            }
        }
        return null;
    }

    /// <summary>按名加载粒子定义(art/particles/{name}.xml,缓存)。</summary>
    public static ParticleDef? LoadDef(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        string? dir = FindParticlesDir();
        if (dir == null) return null;
        string path = System.IO.Path.Combine(dir, name + ".xml");
        if (!System.IO.File.Exists(path)) return null;

        var def = new ParticleDef();
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(path);
            var root = doc.DocumentElement;
            if (root == null) return null;
            foreach (System.Xml.XmlNode node in root.ChildNodes)
            {
                if (node is not System.Xml.XmlElement el) continue;
                string nameAttr = el.GetAttribute("name");
                string value = el.GetAttribute("value");
                string min = el.GetAttribute("min");
                string max = el.GetAttribute("max");
                if (el.Name == "texture")
                    def.TexturePath = el.InnerText.Trim();
                else if (el.Name == "blend")
                    def.Blend = el.GetAttribute("mode");
                else if (el.Name == "constant")
                {
                    switch (nameAttr)
                    {
                        case "emissionrate": def.EmissionRate = F(value); break;
                        case "color.r": def.ColorR = F(value); break;
                        case "color.g": def.ColorG = F(value); break;
                        case "color.b": def.ColorB = F(value); break;
                    }
                }
                else if (el.Name == "uniform")
                {
                    switch (nameAttr)
                    {
                        case "lifetime": def.LifetimeMin = F(min); def.LifetimeMax = F(max); break;
                        case "angle": def.AngleMin = F(min); def.AngleMax = F(max); break;
                        case "velocity.x": def.VelXMin = F(min); def.VelXMax = F(max); break;
                        case "velocity.y": def.VelYMin = F(min); def.VelYMax = F(max); break;
                        case "velocity.z": def.VelZMin = F(min); def.VelZMax = F(max); break;
                        case "velocity.angle": def.VelAngleMin = F(min); def.VelAngleMax = F(max); break;
                        case "size": def.SizeMin = F(min); def.SizeMax = F(max); break;
                        case "position.x": def.PosXMin = F(min); def.PosXMax = F(max); break;
                        case "position.y": def.PosYMin = F(min); def.PosYMax = F(max); break;
                        case "position.z": def.PosZMin = F(min); def.PosZMax = F(max); break;
                    }
                }
            }
        }
        catch { return null; }
        _cache[name] = def;
        return def;
    }

    private static float F(string s) => float.TryParse(s,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

    /// <summary>按定义装配一个 GPUParticles3D。原版 XML → Godot 字段映射:
    /// emissionrate → amount/tick、uniform lifetime/velocity/size → random ranges、
    /// blend add → blend_mode ADD、texture → draw pass material。</summary>
    public static GpuParticles3D? Build(ParticleDef def, int amount = 32)
    {
        var particles = new GpuParticles3D
        {
            Amount = amount,
            Lifetime = def.LifetimeMax,
            Emitting = true,
            OneShot = false,
            Preprocess = def.LifetimeMax,
            SpeedScale = 1f,
            FixedFps = 30,
        };

        // 方向:原版速度向量直接给定(velocity.x/y/z 范围);Y 负值(雨雪下落)→
        // Godot Direction=(0,-1,0) + 正值速率。正向(烟尘上升)同理取绝对值。
        float velYMid = (def.VelYMin + def.VelYMax) * 0.5f;
        var direction = velYMid < 0f ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
        var process = new ParticleProcessMaterial
        {
            Direction = direction,
            InitialVelocityMin = Mathf.Abs(def.VelYMin),
            InitialVelocityMax = Mathf.Abs(def.VelYMax),
            Spread = def.AngleMax * 60f,             // radians → degrees 近似
            Gravity = new Vector3(0, 0, 0),
            ScaleMin = def.SizeMin,
            ScaleMax = def.SizeMax,
        };
        // 发射体积(原版 position.* uniform → 盒式):发射器偏移 = 盒中心。
        if (def.HasPositionBox)
        {
            process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            process.EmissionBoxExtents = new Vector3(
                Mathf.Max(0.5f, (def.PosXMax - def.PosXMin) * 0.5f),
                Mathf.Max(0.5f, (def.PosYMax - def.PosYMin) * 0.5f),
                Mathf.Max(0.5f, (def.PosZMax - def.PosZMin) * 0.5f));
        }
        else
        {
            process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
            process.EmissionSphereRadius = 1f;
        }
        if (def.Blend == "add")
            process.RenderPriority = 0;
        particles.ProcessMaterial = process;

        // 材质:贴图 + blend。
        if (def.TexturePath.Length > 0)
        {
            string? texDir = FindParticlesDir();
            if (texDir != null)
            {
                string texPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(texDir, "..", "..", "..",
                        def.TexturePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(texPath))
                {
                    var img = Image.LoadFromFile(texPath);
                    if (img != null)
                    {
                        var mat = new StandardMaterial3D
                        {
                            AlbedoTexture = ImageTexture.CreateFromImage(img),
                            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                            VertexColorUseAsAlbedo = true,
                        };
                        if (def.Blend == "add")
                            mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                        particles.DrawPass1 = new QuadMesh
                        {
                            Size = new Vector2(1f, 1f),
                            Material = mat,
                        };
                    }
                }
            }
        }
        return particles;
    }

    /// <summary>按名直接装配(常用入口:cloud/smoke_volcano/water_splash/...)。</summary>
    public static GpuParticles3D? BuildByName(string name, int amount = 32)
    {
        var def = LoadDef(name);
        return def == null ? null : Build(def, amount);
    }

    /// <summary>粒子 actor 装配(原版 art/actors/particle/{name}.xml → 取其
    /// &lt;particles file&gt; 引用的粒子定义;天气/氛围装饰物入口:雨/雪/尘暴/云)。
    /// 数量按 发射率×最长寿命 估算(并发上限),封顶 1024。</summary>
    public static GpuParticles3D? BuildForActor(string actorName)
    {
        string? dir = FindParticlesDir();
        if (dir == null) return null;
        string actorPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            dir, "..", "..", "actors", "particle", actorName + ".xml"));
        if (!System.IO.File.Exists(actorPath)) return null;
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(actorPath);
            var particlesEl = doc.DocumentElement?.SelectSingleNode("//particles")
                as System.Xml.XmlElement;
            string? file = particlesEl?.GetAttribute("file");
            if (string.IsNullOrEmpty(file)) return null;
            var def = LoadDef(System.IO.Path.GetFileNameWithoutExtension(file));
            if (def == null) return null;
            int amount = Mathf.Clamp(
                (int)(def.EmissionRate * def.LifetimeMax), 32, 1024);
            var particles = Build(def, amount);
            // 发射盒垂直偏移(原版 position.y 范围——雨雪从高空落下)。
            if (particles != null && def.HasPositionBox)
                particles.Position = new Vector3(0, (def.PosYMin + def.PosYMax) * 0.5f, 0);
            return particles;
        }
        catch { return null; }
    }
}
