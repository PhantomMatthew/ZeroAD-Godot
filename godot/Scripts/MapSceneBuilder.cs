using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>地图预览场景构建器(编辑器 WYSIWYG / 无头冒烟共用):从 PMP+场景 XML
/// (或 rmgen MapExport)构建完整静态 3D 场景——地形、水面、天光、真实实体模型,
/// 镜像运行时 Main.SetupTerrain/SetupRmgenTerrain 的世界结构(地形顶点预翻转挂根,
/// 水与实体挂 Scale.z=-1 的 WorldMirror,子节点局部坐标=sim 坐标)。
/// 纯静态 + System.IO,不依赖 SimBridge/ComponentManager,编辑器内直接可跑。
/// 产物由调用方 PackedScene.Pack 存盘(所有子孙节点的 Owner 在此统一设置)。</summary>
public static class MapSceneBuilder
{
    public sealed class Result
    {
        public Node3D Root = null!;
        public string MapName = "";
        public float MapSizeMeters;
        public int EntityCount;      // 摆进场景的实体总数
        public int ModelCount;       // 其中真实 GLB 模型数(其余为占位体)
        public bool HasWater;
    }

    /// <summary>binaries/data/mods/public 数据根(junction;与 Main.FindDataRoot 同候选)。</summary>
    public static string? FindDataRoot()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        var candidates = new[]
        {
            System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public")),
        };
        foreach (var p in candidates)
            if (System.IO.Directory.Exists(p)) return p;
        return null;
    }

    /// <summary>PMP+XML 路径:mapRel 如 "maps/tutorials/introductory_tutorial"(不含扩展名)。
    /// setOwners=false 用于编辑器内活预览(生成物不进 Pack/存档)。</summary>
    public static Result? Build(string dataRoot, string mapRel, bool setOwners = true)
    {
        string? pmpPath = ScenarioLoader.FindPmpPath(dataRoot, mapRel);
        if (pmpPath == null)
        {
            GD.PrintErr($"[MapSceneBuilder] PMP not found: {mapRel}");
            return null;
        }
        return BuildFromFiles(pmpPath, ScenarioLoader.FindScenarioPath(dataRoot, mapRel),
            System.IO.Path.GetFileName(mapRel), setOwners);
    }

    /// <summary>任意文件路径入口(编辑器 FileDialog 导入用):PMP 必给,XML 可空
    /// (无 XML → 无实体、默认天光、无水面)。</summary>
    public static Result BuildFromFiles(string pmpPath, string? xmlPath, string mapName,
        bool setOwners = true)
    {
        var pmp = PmpMap.Load(pmpPath);
        var entities = new List<ScenarioEntityDef>();
        if (xmlPath != null && System.IO.File.Exists(xmlPath))
            entities.AddRange(ScenarioLoader.Load(xmlPath).Entities);
        return BuildCore(pmp, xmlPath, entities, mapName, setOwners);
    }

    /// <summary>rmgen 路径:MapExport → 预览场景(无场景 XML;天光用默认环境,
    /// 水面按 export.SeaLevel 近似)。</summary>
    public static Result BuildFromExport(ZeroAD.Sim.Rmgen.MapExport export, string mapName,
        bool setOwners = true)
    {
        var pmp = PmpMap.FromExport(export);
        var entities = new List<ScenarioEntityDef>();
        foreach (var ent in export.Entities)
        {
            entities.Add(new ScenarioEntityDef
            {
                Template = ent.TemplateName,
                Player = ent.PlayerID,
                X = (float)ent.Position.X,
                Z = (float)ent.Position.Y,
                OrientationY = (float)ent.Orientation,
            });
        }
        return BuildCore(pmp, xmlPath: null, entities, mapName, setOwners);
    }

    private static Result BuildCore(PmpMap pmp, string? xmlPath, List<ScenarioEntityDef> entities,
        string mapName, bool setOwners)
    {
        // 实体 Y 贴地的数据源(ModelLibrary 内部采样;未设置时退化 y=0)。
        TerrainHeightService.Set(pmp.GetHeightWorld, pmp.MapSizeMeters);

        string rootName = string.Concat(mapName.Replace('/', '_').Replace('\\', '_'), "Preview");
        var root = new Node3D { Name = rootName };

        // 地形(顶点已预翻转为世界坐标,挂根;含 CreateTrimeshCollision 的 StaticBody,
        // 编辑器里可直接射线点选)。
        var terrain = TerrainRenderer.CreateFromHeightmap(pmp);
        terrain.Name = "Terrain";
        root.AddChild(terrain);

        // 视觉镜像根(与运行时 Main._worldRoot 同约定)。
        var mirror = new Node3D
        {
            Name = "WorldMirror",
            Scale = new Vector3(1f, 1f, -1f),
            Position = new Vector3(0f, 0f, pmp.MapSizeMeters),
        };
        root.AddChild(mirror);

        // 天光:先按运行时同款底(显式 Color 背景,兼容性/Forward+ 都出目标蓝),
        // 再用地图 XML 的 Environment(太阳方向/环境光/雾色)覆盖。
        var light = new DirectionalLight3D
        {
            Name = "Sun",
            Rotation = new Vector3(-0.7f, 0.5f, 0f),
            LightEnergy = 1.2f,
            ShadowEnabled = true,
        };
        root.AddChild(light);
        var sky = new WorldEnvironment { Name = "Sky" };
        var env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.45f, 0.65f, 0.9f),
            FogEnabled = true,
            FogLightColor = new Color(0.5f, 0.7f, 0.95f),
            FogDensity = 0.001f,
        };
        sky.Environment = env;
        root.AddChild(sky);
        var mapEnv = xmlPath != null ? MapEnvironment.LoadFromXml(xmlPath) : null;
        (mapEnv ?? MapEnvironment.Default).Apply(light, env);

        // 水面(运行时挂 WorldMirror 下)。
        bool hasWater = false;
        if (xmlPath != null)
        {
            var water = WaterRenderer.LoadWaterFromXml(xmlPath);
            if (water != null)
            {
                var waterMesh = WaterRenderer.CreateWaterPlane(
                    water.Value.height, water.Value.color, pmp.MapSizeMeters);
                waterMesh.Name = "Water";
                mirror.AddChild(waterMesh);
                hasWater = true;
            }
        }

        // 实体(真实 GLB 模型 + 队色;缺模型回退占位体)。跳过 special/ 系统实体。
        var entityRoot = new Node3D { Name = "Entities" };
        mirror.AddChild(entityRoot);
        int modelCount = 0, placed = 0;
        foreach (var def in entities)
        {
            if (def.Template.Length == 0 || def.Template.StartsWith("special/", System.StringComparison.Ordinal))
                continue;
            var color = SimBridge.GetPlayerColor(def.Player);
            Node3D? node = null;
            try { node = ModelLibrary.InstantiateForTemplate(def.Template, def.X, def.Z, color); }
            catch (System.Exception ex)
            {
                GD.PushWarning($"[MapSceneBuilder] instantiate failed '{def.Template}': {ex.Message}");
            }
            if (node != null)
            {
                modelCount++;
            }
            else
            {
                node = MakeFallback(def.Template, def.X, def.Z, color);
                if (node == null) continue;
            }
            node.Name = $"{def.Template.Replace('/', '_')}_{def.Uid}";
            node.Rotation = new Vector3(0f, def.OrientationY, 0f);
            node.SetMeta("template", def.Template);
            node.SetMeta("player", def.Player);
            entityRoot.AddChild(node);
            placed++;
        }

        // PackedScene.Pack 只收 Owner 链上的节点——打包存档路径才把全部子孙挂到根名下;
        // 编辑器活预览(MapPreview [Tool] 即时重建)不设 Owner,生成物不进档。
        if (setOwners)
            SetOwnerRecursive(root, root);

        GD.Print($"[MapSceneBuilder] {mapName}: {placed} entities ({modelCount} real models), " +
                 $"{pmp.MapSizeMeters}m, water={hasWater}");
        return new Result
        {
            Root = root,
            MapName = mapName,
            MapSizeMeters = pmp.MapSizeMeters,
            EntityCount = placed,
            ModelCount = modelCount,
            HasWater = hasWater,
        };
    }

    /// <summary>缺模型回退(无 sim 上下文,按模板名推断类别;对齐 SimBridge.CreateVisualFor
    /// 的启发式)。</summary>
    private static Node3D? MakeFallback(string template, float x, float z, Color color)
    {
        Node3D node;
        if (template.Contains("tree", System.StringComparison.OrdinalIgnoreCase) ||
            template.Contains("flora", System.StringComparison.OrdinalIgnoreCase))
            node = EntityMeshFactory.CreateTree();
        else if (template.StartsWith("structures/", System.StringComparison.Ordinal))
            node = EntityMeshFactory.CreateBuilding(color, template);
        else if (template.StartsWith("units/", System.StringComparison.Ordinal))
            node = EntityMeshFactory.CreateSoldier(color);
        else
        {
            // gaia 岩石/装饰等:中性小方块。
            node = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(1f, 1f, 1f) },
                Position = new Vector3(x, TerrainHeightService.Sample(x, z) + 0.5f, z),
            };
            return node;
        }
        node.Position = new Vector3(x, TerrainHeightService.Sample(x, z), z);
        return node;
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwnerRecursive(child, owner);
        }
    }
}
