using Godot;

namespace ZeroAD.Godot;

/// <summary>地图活预览([Tool]):.tscn 里只存地图引用,编辑器打开时即时重建完整
/// 3D 场景(地形/水/天光/实体模型,走 MapSceneBuilder)。生成物挂在 "Generated"
/// 子节点下、不设 Owner —— 永不进档,因此预览场景文件恒定几 KB,且永远反映
/// 当前代码/素材(替代早期把 1864 个模型内嵌进 .scn 的做法:124MB、编辑器
/// 加载卡死、SplatBaker 改动后还要重烘)。
/// 编辑器里改 MapRel/Rmgen* 后选中节点点"Rebuild"按钮可手动重建。</summary>
[Tool]
public partial class MapPreview : Node3D
{
    /// <summary>PMP 地图相对路径(数据根下,不含扩展名;如 maps/tutorials/introductory_tutorial)。</summary>
    [Export] public string MapRel = "";

    /// <summary>rmgen 地图名(非空则忽略 MapRel 走生成,如 "mainland")。</summary>
    [Export] public string RmgenMap = "";
    [Export] public uint RmgenSeed = 42;
    [Export] public int RmgenSize = 192;

    [ExportToolButton("Rebuild")]
    public Callable RebuildButton => Callable.From(Rebuild);

    public override void _Ready()
    {
        // 编辑器打开/场景运行时各建一次。生成物不进档(Owner 不设)。
        CallDeferred(nameof(Rebuild));
    }

    public void Rebuild()
    {
        var old = GetNodeOrNull<Node3D>("Generated");
        old?.QueueFree();

        MapSceneBuilder.Result? result = null;
        if (RmgenMap.Length > 0)
        {
            var rng = new ZeroAD.Sim.RmgenMath.RmgenRng(RmgenSeed);
            var settings = new ZeroAD.Sim.Rmgen.Common.MapSettings
            {
                Size = RmgenSize,
                Seed = RmgenSeed,
                DataRoot = MapSceneBuilder.FindDataRoot(),
                PlayerData = new() { new() { Civ = "gaia" }, new() { Civ = "athen" }, new() { Civ = "spart" } },
            };
            var export = ZeroAD.Sim.Rmgen.Maps.MapRegistry.Generate(RmgenMap, rng, settings);
            if (export != null)
                result = MapSceneBuilder.BuildFromExport(export, RmgenMap, setOwners: false);
        }
        else if (MapRel.Length > 0)
        {
            var dataRoot = MapSceneBuilder.FindDataRoot();
            if (dataRoot != null)
                result = MapSceneBuilder.Build(dataRoot, MapRel, setOwners: false);
        }

        if (result == null)
        {
            ZeroAD.Sim.Diag.Err("MapPreview", $"build failed (MapRel='{MapRel}' RmgenMap='{RmgenMap}')");
            return;
        }
        result.Root.Name = "Generated";
        AddChild(result.Root);
        ZeroAD.Sim.Diag.Log("MapPreview", $"built '{result.MapName}': {result.EntityCount} entities " +
                 $"({result.ModelCount} models), {result.MapSizeMeters}m, water={result.HasWater}");
    }
}
