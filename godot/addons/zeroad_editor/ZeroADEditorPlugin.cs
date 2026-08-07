using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Editor;

/// <summary>0 A.D. 地图编辑器插件（M10）。
/// 三个菜单项：
///   - Import 0 A.D. Map：读 PMP+XML → 完整预览场景（真实地形/水/天光/实体模型）
///   - Generate Random Map：调 rmgen → MapExport → 同上
///   - Export to 0 A.D. Map：从场景树收集实体 → 写 PMP+XML（部分实现）
/// 预览构建统一走 MapSceneBuilder(与运行时世界结构镜像);产物存
/// res://Scenes/Previews/&lt;Map&gt;Preview.scn(二进制,网格+烘焙纹理内嵌)并打开。
/// 无头冒烟:--zeroad-build-preview=&lt;mapRel&gt; 时直接构建+保存+打印统计后退出。</summary>
[Tool]
public partial class ZeroADEditorPlugin : EditorPlugin
{
    private const string PreviewArgPrefix = "--zeroad-build-preview=";

    public override void _EnterTree()
    {
        AddToolMenuItem("Import 0 A.D. Map", new Callable(this, MethodName.OnImportMap));
        AddToolMenuItem("Generate Random Map", new Callable(this, MethodName.OnGenerateMap));
        AddToolMenuItem("Export to 0 A.D. Map", new Callable(this, MethodName.OnExportMap));
        GD.Print("[ZeroAD Editor] Plugin loaded");

        // 无头功能冒烟钩子(--headless --editor 下 CI/agent 可直接验证预览构建)。
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (arg.StartsWith(PreviewArgPrefix, StringComparison.Ordinal))
            {
                string mapRel = arg[PreviewArgPrefix.Length..];
                RunHeadlessPreview(mapRel);   // async:等一帧后构建并退出
                break;
            }
        }
    }

    public override void _ExitTree()
    {
        RemoveToolMenuItem("Import 0 A.D. Map");
        RemoveToolMenuItem("Generate Random Map");
        RemoveToolMenuItem("Export to 0 A.D. Map");
    }

    // ── 无头预览构建(--zeroad-build-preview=maps/tutorials/introductory_tutorial)──

    private async void RunHeadlessPreview(string mapRel)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int rc = 1;
        try
        {
            var dataRoot = MapSceneBuilder.FindDataRoot();
            if (dataRoot == null)
            {
                GD.PrintErr("[ZeroAD Editor] headless: data root (binaries junction) not found");
            }
            else
            {
                var result = MapSceneBuilder.Build(dataRoot, mapRel);
                if (result != null && SavePreview(result, openInEditor: false))
                    rc = 0;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ZeroAD Editor] headless preview failed: {ex.GetType().Name}: {ex.Message}");
        }
        GD.Print($"[ZeroAD Editor] headless preview rc={rc}");
        GetTree().Quit();
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
        string? xmlPath = Path.ChangeExtension(pmpPath, ".xml");
        if (!File.Exists(xmlPath)) xmlPath = null;

        var result = MapSceneBuilder.BuildFromFiles(
            pmpPath, xmlPath, Path.GetFileNameWithoutExtension(pmpPath));
        SavePreview(result, openInEditor: true);
    }

    // ── Generate rmgen ──

    private void OnGenerateMap()
    {
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

        var rng = new ZeroAD.Sim.RmgenMath.RmgenRng(seed);
        var settings = new ZeroAD.Sim.Rmgen.Common.MapSettings
        {
            Size = size,
            Seed = seed,
            DataRoot = MapSceneBuilder.FindDataRoot(),
            PlayerData = new() { new() { Civ = "gaia" }, new() { Civ = "athen" }, new() { Civ = "spart" } },
        };

        var mapExport = ZeroAD.Sim.Rmgen.Maps.MapRegistry.Generate(mapName, rng, settings);
        if (mapExport == null)
        {
            GD.PrintErr($"[ZeroAD Editor] Unknown map type: {mapName}");
            return;
        }

        // MapExport → PmpMap 走共享适配器(PmpMap.FromExport;旧内联版漏赋
        // VerticesPerSide,TerrainRenderer 必抛 InvalidDataException)。
        var result = MapSceneBuilder.BuildFromExport(mapExport, mapName);
        SavePreview(result, openInEditor: true);
    }

    // ── 预览场景保存 ──

    /// <summary>打包存 res://Scenes/Previews/&lt;Map&gt;Preview.scn(二进制);可选立即打开。</summary>
    private static bool SavePreview(MapSceneBuilder.Result result, bool openInEditor)
    {
        string dirAbs = ProjectSettings.GlobalizePath("res://Scenes/Previews");
        DirAccess.MakeDirRecursiveAbsolute(dirAbs);
        string safeName = string.Concat(result.MapName.Replace('/', '_').Replace('\\', '_'), "Preview");
        string resPath = $"res://Scenes/Previews/{safeName}.scn";

        var packed = new PackedScene();
        var err = packed.Pack(result.Root);
        if (err == Error.Ok)
            err = ResourceSaver.Save(packed, resPath);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[ZeroAD Editor] save preview failed: {err} → {resPath}");
            return false;
        }

        GD.Print($"[ZeroAD Editor] preview saved: {resPath} " +
                 $"({result.EntityCount} entities, {result.ModelCount} models, {result.MapSizeMeters}m)");
        if (openInEditor)
            EditorInterface.Singleton.OpenSceneFromPath(resPath);
        return true;
    }

    // ── Export PMP+XML（部分实现;维持现状)──

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

        // 收集实体(遍历场景树,读 MapSceneBuilder 写入的 template/player metadata)。
        // 实体节点在 WorldMirror/Entities 下;镜像根负 scale,故取局部 Position 即 sim 坐标。
        var entities = new List<MapEntityData>();
        int uid = 150;
        var entityRoot = sceneRoot.GetNodeOrNull<Node3D>("WorldMirror/Entities");
        if (entityRoot != null)
        {
            foreach (Node child in entityRoot.GetChildren())
            {
                if (child is not Node3D node3d) continue;
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

        var data = new MapData { MapName = "Exported Map" };
        data.Entities.AddRange(entities);
        // TODO: 写 PMP(需要完整的 heightmap/tiles——从场景 metadata 读取)
        // PmpMapWriter.Save(pmpPath, data);

        string xmlPath = Path.ChangeExtension(pmpPath, ".xml");
        ScenarioXmlWriter.Save(xmlPath, data, entities);
        GD.Print($"[ZeroAD Editor] Export complete: {entities.Count} entities → {xmlPath}");
    }
}
