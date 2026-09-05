using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

/// <summary>pathfinder.xml 通行类注册表(原版 CCmpPathfinder 的 m_PassabilityClasses,
/// 由 simulation/data/pathfinder.xml 驱动)。9 类:default/large/ship/ship-small(单位寻路)、
/// building-land/building-shore(建筑放置)、unrestricted/default-terrain-only/
/// ship-terrain-only(AI 选址/领土用,不印障碍)。
///
/// 数据驱动:数据根存在时读 XML(mod 可覆盖);缺失回退内建默认(与上游 XML 逐值一致)。
/// 注意:类索引即位号(0..8)——注册顺序决定 NavcellData 位,勿随意重排(存档/navcell
/// 位序稳定依赖);XML 顺序即上游顺序。</summary>
public sealed class PathfinderConfig
{
    public readonly IReadOnlyList<PassabilityClassDef> Classes;
    private readonly Dictionary<string, PassabilityClassDef> _byName = new(StringComparer.Ordinal);

    /// <summary>同回合同步即答的路径请求预算(原版 pathfinder.xml MaxSameTurnMoves;
    /// PathfinderComponent.MaxSameTurnPaths 由 SetPassabilityConfig 注入此值)。</summary>
    public int MaxSameTurnMoves = 20;

    /// <summary>推挤参数块(原版 pathfinder.xml 的 Pushing)。仅解析存档备用:
    /// UnitSeparation 用的是 v1 调参常数(见该处字段注释,与 XML 值有意分歧),
    /// 不从 XML 重灌,避免双源漂移。</summary>
    public PushingSettings Pushing { get; private set; } = new();

    private PathfinderConfig(IReadOnlyList<PassabilityClassDef> classes)
    {
        Classes = classes;
        foreach (var c in classes) _byName[c.Name] = c;
    }

    public PassabilityClassDef? ByName(string name) =>
        _byName.TryGetValue(name, out var c) ? c : null;

    /// <summary>原版 getPassabilityClassMask:名 → 位掩码;未知名 → default(0)。</summary>
    public PassClass MaskOf(string name) =>
        _byName.TryGetValue(name, out var c) ? c.Mask : Classes[0].Mask;

    /// <summary>单位寻路类(有净空要求的 Obstructions=Pathfinding 类)——
    /// 分层/长程寻路只对这些类建连通性(建筑/AI 类不参与寻路查询)。</summary>
    public IEnumerable<PassabilityClassDef> UnitPathClasses()
    {
        foreach (var c in Classes)
            if (c.Obstructions == ObstructionKind.Pathfinding)
                yield return c;
    }

    /// <summary>从数据根加载(pathfinder.xml 在 mods/&lt;mod&gt;/simulation/data/)。
    /// 遍历顺序 = 上游 VFS 覆盖语义:public 后挂载、优先命中(可覆盖 mod 同名文件);
    /// 上游目前只在 mods/mod 提供该文件,顺序调整对现内容零行为差。
    /// 缺失/解析失败 → 内建默认。返回(配置, 是否来自 XML)。</summary>
    public static PathfinderConfig Load(string? dataModsDir = null)
    {
        if (dataModsDir != null)
        {
            foreach (var mod in new[] { "public", "mod" })
            {
                string path = Path.Combine(dataModsDir, mod, "simulation", "data", "pathfinder.xml");
                if (File.Exists(path) && TryParse(path, out var cfg))
                    return cfg;
            }
        }
        return Default();
    }

    /// <summary>内建默认:与上游 pathfinder.xml 逐值一致(上游改动时同步)。</summary>
    public static PathfinderConfig Default()
    {
        var classes = new List<PassabilityClassDef>
        {
            new() { Name = "default", Obstructions = ObstructionKind.Pathfinding,
                MaxWaterDepth = Fixed.FromInt(2), MaxTerrainSlope = Fixed.FromInt(1),
                Clearance = Fixed.FromFraction(4, 5) },
            new() { Name = "large", Obstructions = ObstructionKind.Pathfinding,
                MaxWaterDepth = Fixed.FromInt(2), MaxTerrainSlope = Fixed.FromInt(1),
                Clearance = Fixed.FromInt(3) },
            new() { Name = "ship", Obstructions = ObstructionKind.Pathfinding,
                MinWaterDepth = Fixed.FromInt(1), Clearance = Fixed.FromInt(10) },
            new() { Name = "ship-small", Obstructions = ObstructionKind.Pathfinding,
                MinWaterDepth = Fixed.FromInt(1), Clearance = Fixed.FromInt(3) },
            new() { Name = "building-land", Obstructions = ObstructionKind.Foundation,
                MaxWaterDepth = Fixed.Zero, MinShoreDistance = Fixed.FromInt(4),
                MaxTerrainSlope = Fixed.FromInt(1) },
            new() { Name = "building-shore", Obstructions = ObstructionKind.Foundation,
                MaxShoreDistance = Fixed.FromInt(8),
                MaxTerrainSlope = Fixed.FromFraction(5, 4) },
            new() { Name = "unrestricted", Obstructions = ObstructionKind.None },
            new() { Name = "default-terrain-only", Obstructions = ObstructionKind.None,
                MaxWaterDepth = Fixed.FromInt(2), MaxTerrainSlope = Fixed.FromInt(1) },
            new() { Name = "ship-terrain-only", Obstructions = ObstructionKind.None,
                MinWaterDepth = Fixed.FromInt(1) },
        };
        for (int i = 0; i < classes.Count; i++)
            classes[i].Mask = PathfindingCore.PassClassMaskFromIndex(i);
        return new PathfinderConfig(classes);
    }

    /// <summary>XML 解析(原版 PathfinderPassability 字段同名)。类位号 = 文档序;
    /// 根级 MaxSameTurnMoves/Pushing 块一并解析(缺省保留字段初值 = 上游 XML 值)。</summary>
    private static bool TryParse(string path, out PathfinderConfig cfg)
    {
        cfg = Default();
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            var parent = root?.Element("PassabilityClasses");
            if (parent == null) return false;

            int? maxSameTurnMoves = null;
            if (int.TryParse(root!.Element("MaxSameTurnMoves")?.Value, out int maxMoves))
                maxSameTurnMoves = maxMoves;
            var pushing = ParsePushing(root.Element("Pushing"));

            var classes = new List<PassabilityClassDef>();
            foreach (var el in parent.Elements())
            {
                var def = new PassabilityClassDef { Name = el.Name.LocalName };
                string obstructions = el.Element("Obstructions")?.Value ?? "none";
                def.Obstructions = obstructions switch
                {
                    "pathfinding" => ObstructionKind.Pathfinding,
                    "foundation" => ObstructionKind.Foundation,
                    _ => ObstructionKind.None,
                };
                if (decimal.TryParse(el.Element("MaxWaterDepth")?.Value, out decimal maxWd))
                    def.MaxWaterDepth = Fixed.FromFloat((float)maxWd);
                if (decimal.TryParse(el.Element("MinWaterDepth")?.Value, out decimal minWd))
                    def.MinWaterDepth = Fixed.FromFloat((float)minWd);
                if (decimal.TryParse(el.Element("MaxTerrainSlope")?.Value, out decimal slope))
                    def.MaxTerrainSlope = Fixed.FromFloat((float)slope);
                if (decimal.TryParse(el.Element("Clearance")?.Value, out decimal clearance))
                    def.Clearance = Fixed.FromFloat((float)clearance);
                if (decimal.TryParse(el.Element("MinShoreDistance")?.Value, out decimal minShore))
                    def.MinShoreDistance = Fixed.FromFloat((float)minShore);
                if (decimal.TryParse(el.Element("MaxShoreDistance")?.Value, out decimal maxShore))
                    def.MaxShoreDistance = Fixed.FromFloat((float)maxShore);
                classes.Add(def);
            }
            if (classes.Count == 0 || classes[0].Name != "default") return false;
            for (int i = 0; i < classes.Count; i++)
                classes[i].Mask = PathfindingCore.PassClassMaskFromIndex(i);
            cfg = new PathfinderConfig(classes);
            if (maxSameTurnMoves.HasValue) cfg.MaxSameTurnMoves = maxSameTurnMoves.Value;
            if (pushing != null) cfg.Pushing = pushing;
            return true;
        }
        catch (Exception ex)
        {
            Diag.Warn("Pathfinder", $"pathfinder.xml parse failed ({path}): {ex.Message} — defaults in use");
            return false;
        }
    }

    /// <summary>Pushing 子块解析;任字段缺失保留 PushingSettings 初值(上游 XML 逐值)。
    /// 块整体缺失 → null(调用侧保留默认实例)。</summary>
    private static PushingSettings? ParsePushing(XElement? el)
    {
        if (el == null) return null;
        var p = new PushingSettings();
        if (decimal.TryParse(el.Element("Radius")?.Value, out decimal radius))
            p.Radius = Fixed.FromFloat((float)radius);
        if (decimal.TryParse(el.Element("StaticExtension")?.Value, out decimal staticExt))
            p.StaticExtension = Fixed.FromFloat((float)staticExt);
        if (decimal.TryParse(el.Element("MovingExtension")?.Value, out decimal movingExt))
            p.MovingExtension = Fixed.FromFloat((float)movingExt);
        if (decimal.TryParse(el.Element("StaticSpread")?.Value, out decimal staticSpread))
            p.StaticSpread = Fixed.FromFloat((float)staticSpread);
        if (decimal.TryParse(el.Element("MovingSpread")?.Value, out decimal movingSpread))
            p.MovingSpread = Fixed.FromFloat((float)movingSpread);
        if (decimal.TryParse(el.Element("MinimalForce")?.Value, out decimal minForce))
            p.MinimalForce = Fixed.FromFloat((float)minForce);
        if (decimal.TryParse(el.Element("PressureStrength")?.Value, out decimal strength))
            p.PressureStrength = Fixed.FromFloat((float)strength);
        if (decimal.TryParse(el.Element("PressureDecay")?.Value, out decimal decay))
            p.PressureDecay = Fixed.FromFloat((float)decay);
        return p;
    }
}

/// <summary>原版 pathfinder.xml 的 Pushing 块:推挤半径/动静扩展/散布/最小力/压力强度
/// 与衰减。初值与上游 XML 逐值一致(单位:米/无量纲系数)。</summary>
public sealed class PushingSettings
{
    public Fixed Radius = Fixed.FromFloat(1.4f);
    public Fixed StaticExtension = Fixed.FromFloat(0.5f);
    public Fixed MovingExtension = Fixed.FromFloat(4.0f);
    public Fixed StaticSpread = Fixed.FromFloat(0.9f);
    public Fixed MovingSpread = Fixed.FromFloat(0.4f);
    public Fixed MinimalForce = Fixed.FromFloat(0.2f);
    public Fixed PressureStrength = Fixed.FromFloat(0.5f);
    public Fixed PressureDecay = Fixed.FromFloat(0.6f);
}
