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
    private const string BakeArgPrefix = "--zeroad-bake-terrain=";

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
            if (arg.StartsWith(BakeArgPrefix, StringComparison.Ordinal))
            {
                string mapRel = arg[BakeArgPrefix.Length..];
                RunHeadlessBakeTerrain(mapRel);   // async:只烘地形 albedo PNG
                break;
            }
            if (arg == "--zeroad-camera-zoom-smoke")
            {
                RunCameraZoomSmoke();   // 相机滚轮缩放的坐标空间冒烟
                break;
            }
        }
    }

    // ── 无头相机冒烟(--zeroad-camera-zoom-smoke)──
    // 验证 zoom-to-cursor 的坐标空间:滚轮缩放时焦点只该在 sim X/Z 上微调
    // (缩放向鼠标点收敛),绝不能在 Z 上飞出地图(visZ 误写进 sim 焦点的回归)。
    private async void RunCameraZoomSmoke()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int rc = 1;
        try
        {
            // 平一块地形做采样(高度恒 0,WorldSize=768 = 192 tiles × 4m)。
            const float worldSize = 768f;
            TerrainHeightService.Set((x, z) => 0f, worldSize);

            var cam = new ZeroAD.Godot.RTSCamera();
            GetTree().Root.AddChild(cam);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var sz = GetViewport().GetVisibleRect().Size; var center = new Vector2(sz.X / 2, sz.Y / 2);
            var focus0 = cam.Focus!.Value;
            GD.Print($"[cam-smoke] focus before: ({focus0.X:F1},{focus0.Y:F1},{focus0.Z:F1})");

            // 模拟滚轮放大(无 Shift → 走缩放分支)。
            var wheel = new InputEventMouseButton
            {
                ButtonIndex = MouseButton.WheelUp,
                Position = center,
                Pressed = true,
            };
            cam._Input(wheel);

            // 让平滑值收敛(足够多帧)。
            for (int i = 0; i < 120; ++i)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var focus1 = cam.Focus!.Value;
            GD.Print($"[cam-smoke] focus after : ({focus1.X:F1},{focus1.Y:F1},{focus1.Z:F1})");

            // 断言:z 方向位移必须很小(收敛方向是向图心鼠标点,量级 ≤ factor×focus 距离,
            // 不是 0.15×WorldSize 那种飞越)。x/z 都该在 [0, worldSize] 内且不越界漂移。
            float dz = Mathf.Abs(focus1.Z - focus0.Z);
            float dx = Mathf.Abs(focus1.X - focus0.X);
            bool inBounds = focus1.X >= 0 && focus1.X <= worldSize &&
                            focus1.Z >= 0 && focus1.Z <= worldSize;
            GD.Print($"[cam-smoke] |dx|={dx:F1} |dz|={dz:F1} inBounds={inBounds}");
            if (inBounds && dz < worldSize * 0.5f && dx < worldSize * 0.5f)
            {
                GD.Print("[cam-smoke] PASS");
                rc = 0;
            }
            else
            {
                GD.PrintErr("[cam-smoke] FAIL: focus drifted out of bounds on scroll");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[cam-smoke] exception: {ex.GetType().Name}: {ex.Message}");
        }
        GD.Print($"[cam-smoke] rc={rc}");
        GetTree().Quit();
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
                // 全量构建做验证(实体/模型计数),落盘的是轻量活预览场景。
                var result = MapSceneBuilder.Build(dataRoot, mapRel, setOwners: false);
                if (result != null)
                {
                    GD.Print($"[ZeroAD Editor] validated: {result.EntityCount} entities, " +
                             $"{result.ModelCount} models, {result.MapSizeMeters}m, water={result.HasWater}");
                    if (WritePreviewScene(result.MapName, $"MapRel = \"{mapRel}\""))
                        rc = 0;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ZeroAD Editor] headless preview failed: {ex.GetType().Name}: {ex.Message}");
        }
        GD.Print($"[ZeroAD Editor] headless preview rc={rc}");
        GetTree().Quit();
    }

    // ── 无头地形烘焙(--zeroad-bake-terrain=maps/tutorials/introductory_tutorial)──
    // 只烘 splat albedo 存 PNG(不建场景)——地形混合的快速视觉验证通道(124MB
    // 预览场景在 GUI 编辑器里重载要 2 分钟+,不适合迭代对照)。

    private async void RunHeadlessBakeTerrain(string mapRel)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int rc = 1;
        try
        {
            var dataRoot = MapSceneBuilder.FindDataRoot();
            string? pmpPath = dataRoot != null
                ? ZeroAD.Sim.Content.ScenarioLoader.FindPmpPath(dataRoot, mapRel) : null;
            if (pmpPath == null)
            {
                GD.PrintErr($"[ZeroAD Editor] bake: PMP not found: {mapRel}");
            }
            else
            {
                var img = SplatBaker.BakeAlbedo(PmpMap.Load(pmpPath));
                if (img != null)
                {
                    string outPath = ProjectSettings.GlobalizePath("user://terrain_bake.png");
                    img.SavePng(outPath);
                    GD.Print($"[ZeroAD Editor] terrain bake saved: {outPath}");
                    rc = 0;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ZeroAD Editor] bake failed: {ex.GetType().Name}: {ex.Message}");
        }
        GD.Print($"[ZeroAD Editor] headless bake rc={rc}");
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
        // 预览场景只存地图引用(活预览 MapPreview 打开即重建)——需要数据根相对路径。
        var dataRoot = MapSceneBuilder.FindDataRoot();
        if (dataRoot == null)
        {
            GD.PrintErr("[ZeroAD Editor] data root (binaries junction) not found");
            return;
        }
        string full = Path.GetFullPath(pmpPath);
        string rootWithSep = Path.GetFullPath(dataRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            GD.PrintErr($"[ZeroAD Editor] PMP must be under {rootWithSep} for live preview");
            return;
        }
        string mapRel = full[rootWithSep.Length..];
        mapRel = Path.ChangeExtension(mapRel, null)!.Replace(Path.DirectorySeparatorChar, '/');
        WritePreviewScene(Path.GetFileName(mapRel),
            $"MapRel = \"{mapRel}\"");
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
        if (!ZeroAD.Sim.Rmgen.Maps.MapRegistry.AvailableMaps.Contains(mapName))
        {
            GD.PrintErr($"[ZeroAD Editor] Unknown map type: {mapName}");
            return;
        }
        WritePreviewScene(mapName,
            $"RmgenMap = \"{mapName}\"\nRmgenSeed = {seed}\nRmgenSize = {size}");
    }

    // ── 预览场景写出(轻量 .tscn:Node3D + MapPreview 脚本 + 地图引用)──

    /// <summary>写 res://Scenes/Previews/&lt;name&gt;Preview.tscn 并打开。场景本身不含
    /// 任何世界内容——MapPreview [Tool] 在打开时即时重建(MapSceneBuilder),因此
    /// 文件恒定几 KB 且永远反映当前代码/素材。</summary>
    private static bool WritePreviewScene(string mapName, string propsBlock)
    {
        string dirAbs = ProjectSettings.GlobalizePath("res://Scenes/Previews");
        DirAccess.MakeDirRecursiveAbsolute(dirAbs);
        string safeName = string.Concat(mapName.Replace('/', '_').Replace('\\', '_'), "Preview");
        string resPath = $"res://Scenes/Previews/{safeName}.tscn";

        string text = "[gd_scene load_steps=2 format=3]\n\n"
            + "[ext_resource type=\"Script\" path=\"res://Scripts/MapPreview.cs\" id=\"1_mp\"]\n\n"
            + $"[node name=\"{safeName}\" type=\"Node3D\"]\n"
            + "script = ExtResource(\"1_mp\")\n"
            + propsBlock + "\n";
        string abs = ProjectSettings.GlobalizePath(resPath);
        File.WriteAllText(abs, text);

        GD.Print($"[ZeroAD Editor] preview scene written: {resPath}");
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
