using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Editor;

/// <summary>0 A.D. 地图编辑器插件（M10）。
/// 转换管线模式：PMP+XML / rmgen → Godot 场景 → 编辑 → 导出。
/// 三个菜单项：
///   - Import 0 A.D. Map：读 PMP+XML → 构建 Node3D 场景
///   - Generate Random Map：调 rmgen → MapExport → 构建 Node3D 场景
///   - Export to 0 A.D. Map：从场景树收集实体 → 写 PMP+XML</summary>
[Tool]
public partial class ZeroADEditorPlugin : EditorPlugin
{
    private const string AddonName = "zeroad_editor";

    public override void _EnterTree()
    {
        AddToolMenuItem("Import 0 A.D. Map", new Callable(this, MethodName.OnImportMap));
        AddToolMenuItem("Generate Random Map", new Callable(this, MethodName.OnGenerateMap));
        AddToolMenuItem("Export to 0 A.D. Map", new Callable(this, MethodName.OnExportMap));
        GD.Print("[ZeroAD Editor] Plugin loaded");
    }

    public override void _ExitTree()
    {
        RemoveToolMenuItem("Import 0 A.D. Map");
        RemoveToolMenuItem("Generate Random Map");
        RemoveToolMenuItem("Export to 0 A.D. Map");
    }

    // ── Import PMP+XML ──

    private void OnImportMap()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.pmp ;0 A.D. Terrain" },
            Title = "Select 0 A.D. PMP file",
        };
        dialog.FileSelected += (selectedPath) => DoImport(selectedPath);
        EditorInterface.Singleton.GetBaseControl().AddChild(dialog);
        dialog.PopupCentered(new Vector2I(600, 400));
    }

    private void DoImport(string pmpPath)
    {
        GD.Print($"[ZeroAD Editor] Importing {pmpPath}");

        // 读 PMP
        var pmpMap = PmpMap.Load(pmpPath);
        if (pmpMap == null)
        {
            GD.PrintErr("[ZeroAD Editor] Failed to load PMP");
            return;
        }

        // 构建 MapData
        var mapData = PmpToMapData(pmpMap);

        // 尝试读同目录 XML
        string xmlPath = Path.ChangeExtension(pmpPath, ".xml");
        if (File.Exists(xmlPath))
        {
            GD.Print($"[ZeroAD Editor] Found scenario XML: {xmlPath}");
            // TODO: 用 ScenarioMapLoader 读实体（需 ComponentManager 上下文——编辑器内简化版）
        }

        BuildScene(mapData, pmpMap);
        GD.Print("[ZeroAD Editor] Import complete");
    }

    // ── Generate rmgen ──

    private void OnGenerateMap()
    {
        // 简化版：弹一个对话框输入地图类型 + 种子 + 大小
        var confirm = new AcceptDialog
        {
            Title = "Generate Random Map",
            DialogText = "Generating 'mainland' map with seed 42, size 192.\n(Full UI with map/seed picker coming later.)",
        };
        confirm.Confirmed += () => DoGenerate("mainland", 42, 192);
        EditorInterface.Singleton.GetBaseControl().AddChild(confirm);
        confirm.PopupCentered(new Vector2I(400, 200));
    }

    private void DoGenerate(string mapName, uint seed, int size)
    {
        GD.Print($"[ZeroAD Editor] Generating {mapName} (seed={seed}, size={size})");

        // 调 rmgen
        var rng = new ZeroAD.Sim.RmgenMath.RmgenRng(seed);
        var settings = new ZeroAD.Sim.Rmgen.Common.MapSettings
        {
            Size = size,
            Seed = seed,
            PlayerData = new() { new() { Civ = "gaia" }, new() { Civ = "athen" }, new() { Civ = "spart" } },
        };

        var mapExport = ZeroAD.Sim.Rmgen.Maps.MapRegistry.Generate(mapName, rng, settings);
        if (mapExport == null)
        {
            GD.PrintErr($"[ZeroAD Editor] Unknown map type: {mapName}");
            return;
        }

        // MapExport → PmpMap 适配
        var pmpMap = MapExportToPmpMap(mapExport, size);
        var mapData = MapExportToMapData(mapExport);

        BuildScene(mapData, pmpMap);
        GD.Print($"[ZeroAD Editor] Generated {mapName}: {mapExport.Entities.Count} entities, size={size}");
    }

    // ── Export PMP+XML ──

    private void OnExportMap()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
        {
            GD.PrintErr("[ZeroAD Editor] No scene open to export");
            return;
        }

        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.pmp ;0 A.D. Terrain" },
            Title = "Save 0 A.D. PMP file",
        };
        dialog.FileSelected += (selectedPath) => DoExport(selectedPath, root);
        EditorInterface.Singleton.GetBaseControl().AddChild(dialog);
        dialog.PopupCentered(new Vector2I(600, 400));
    }

    private void DoExport(string pmpPath, Node sceneRoot)
    {
        GD.Print($"[ZeroAD Editor] Exporting to {pmpPath}");

        // 从场景树收集 MapData（存在根节点的 metadata）
        var mapData = sceneRoot.GetMeta("map_data_path", "").AsString;
        // 简化版：从场景结构重建 MapData
        // TODO: 完整版从场景节点 metadata 读取
        GD.Print("[ZeroAD Editor] Export: collect entities from scene tree...");

        // 收集实体（遍历 Node3D 子节点，读 metadata）
        var entities = new List<MapEntityData>();
        int uid = 150;
        foreach (Node child in sceneRoot.GetChildren())
        {
            if (child is Node3D node3d)
            {
                var ent = new MapEntityData
                {
                    Uid = uid++,
                    Template = node3d.GetMeta("template", "").AsString(),
                    PlayerID = (int)node3d.GetMeta("player", 0).AsInt64(),
                    X = node3d.Position.X,
                    Y = 0,  // 高度由地形采样
                    Z = node3d.Position.Z,
                    Angle = node3d.Rotation.Y,
                };
                if (ent.Template.Length > 0) entities.Add(ent);
            }
        }

        // 写 PMP（如果有 mapData）
        var data = new MapData { MapName = "Exported Map" };
        data.Entities.AddRange(entities);
        // TODO: 写 PMP（需要完整的 heightmap/tiles——从场景 metadata 读取）
        // PmpMapWriter.Save(pmpPath, data);

        // 写 XML
        string xmlPath = Path.ChangeExtension(pmpPath, ".xml");
        ScenarioXmlWriter.Save(xmlPath, data, entities);
        GD.Print($"[ZeroAD Editor] Export complete: {entities.Count} entities → {xmlPath}");
    }

    // ── 场景构建（核心——从 PmpMap 构建 Godot Node3D 场景）──

    private void BuildScene(MapData mapData, PmpMap pmpMap)
    {
        // 创建场景根
        var root = new Node3D { Name = "MapRoot" };

        // 地形 mesh
        var terrain = TerrainRenderer.CreateFromHeightmap(pmpMap);
        terrain.Name = "Terrain";
        root.AddChild(terrain);
        terrain.Owner = root;

        // 实体节点（占位 mesh）
        foreach (var ent in mapData.Entities)
        {
            var node = new MeshInstance3D { Name = ent.Template.Replace('/', '_') };
            node.Position = new Vector3(ent.X, ent.Y, ent.Z);
            node.Rotation = new Vector3(0, ent.Angle, 0);
            node.SetMeta("template", ent.Template);
            node.SetMeta("player", ent.PlayerID);
            // 简单占位 mesh（完整版用 EntityMeshFactory）
            node.Mesh = new BoxMesh { Size = new Vector3(1, 2, 1) };
            root.AddChild(node);
            node.Owner = root;
        }

        // 设为编辑场景
        var packed = new PackedScene();
        packed.Pack(root);
        var tmpPath = ProjectSettings.GlobalizePath("res://tmp_map_import.tscn");
        ResourceSaver.Save(packed, tmpPath);
        EditorInterface.Singleton.OpenSceneFromPath(tmpPath);
        EditorInterface.Singleton.MarkSceneAsUnsaved();
    }

    // ── 适配器 ──

    private static MapData PmpToMapData(PmpMap pmp)
    {
        int tilesPerSide = pmp.PatchesPerSide * 16;
        int vertsPerSide = pmp.VerticesPerSide;
        var data = new MapData { PatchesPerSide = pmp.PatchesPerSide };

        // heightmap: PmpMap 用一维 ushort[]（row-major: index = z * vertsPerSide + x）
        data.Heightmap = pmp.Heightmap;

        // textures
        data.TextureNames = pmp.TextureNames.ToArray();
        data.TileTextureIndex = pmp.TileTex1;
        data.TilePriority = pmp.TilePriority;

        return data;
    }

    private static MapData MapExportToMapData(ZeroAD.Sim.Rmgen.MapExport export)
    {
        int tiles = export.Size;
        int pps = tiles / 16;
        var data = new MapData { PatchesPerSide = pps, MapName = "Generated Map" };

        data.Heightmap = export.Height;
        data.TextureNames = export.TextureNames.ToArray();
        data.TileTextureIndex = export.TileIndex;
        data.TilePriority = System.Array.ConvertAll(export.TilePriority, v => (uint)v);

        foreach (var ent in export.Entities)
        {
            data.Entities.Add(new MapEntityData
            {
                Template = ent.TemplateName,
                PlayerID = ent.PlayerID,
                X = (float)ent.Position.X,
                Y = 0,
                Z = (float)ent.Position.Y,
                Angle = (float)ent.Orientation,
            });
        }
        return data;
    }

    private static PmpMap MapExportToPmpMap(ZeroAD.Sim.Rmgen.MapExport export, int size)
    {
        int pps = size / 16;

        // PmpMap 用一维数组（row-major）
        return new PmpMap
        {
            Version = 7,
            PatchesPerSide = pps,
            Heightmap = export.Height,
            TextureNames = new List<string>(export.TextureNames),
            TileTex1 = export.TileIndex,
        };
    }
}
