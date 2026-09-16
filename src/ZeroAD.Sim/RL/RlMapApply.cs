using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Rmgen;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.Rmgen.Maps;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.RL;

/// <summary>Headless PMP / rmgen intake for <see cref="RlEnvironment"/>.</summary>
public static class RlMapApply
{
    public static readonly string[] PlayableCivs =
    {
        "athen", "spart", "mace", "rome", "cart", "pers", "gaul", "iber", "brit",
        "maur", "ptol", "sele", "kush"
    };

    public static readonly object RmgenGate = new();

    public static void PickCivs(uint seed, out string agent, out string opp)
    {
        int n = PlayableCivs.Length;
        int a = (int)(seed % (uint)n);
        int b = (int)((seed / (uint)n) % (uint)n);
        if (b == a) b = (a + 1) % n;
        agent = PlayableCivs[a];
        opp = PlayableCivs[b];
    }

    public static bool IsRmgenName(string mapName) =>
        mapName.Length > 0 && MapRegistry.MapType(mapName) != null;

    public static bool TryLoadPmp(string modsPublic, string mapName, out PmpTerrain pmp,
        out ScenarioData? scenario)
    {
        pmp = null!;
        scenario = null;
        string? pmpPath = ScenarioLoader.FindPmpPath(modsPublic, mapName);
        if (pmpPath == null || !File.Exists(pmpPath)) return false;
        pmp = PmpTerrain.Load(pmpPath);
        string? xml = ScenarioLoader.FindScenarioPath(modsPublic, mapName);
        if (xml != null) scenario = ScenarioLoader.Load(xml);
        return true;
    }

    public static MapExport? GenerateRmgen(string mapName, uint seed, int size,
        string agentCiv, string oppCiv, string? dataRoot)
    {
        var settings = new MapSettings
        {
            Size = Math.Max(64, size),
            Seed = seed,
            CircularMap = true,
            DataRoot = dataRoot
        };
        settings.PlayerData.Add(new PlayerData { Civ = "gaia" });
        settings.PlayerData.Add(new PlayerData { Civ = agentCiv, Team = 0 });
        settings.PlayerData.Add(new PlayerData { Civ = oppCiv, Team = 1 });
        lock (RmgenGate)
            return MapRegistry.Generate(mapName, new RmgenRng(seed), settings);
    }

    public static void ApplyHeightGrid(ComponentManager cm, int tiles, float tileSize,
        Func<int, int, float> heightAt, float waterMeters, bool circular,
        string? modsPublic)
    {
        var terrain = new TerrainComponent();
        terrain.Configure(tiles, tileSize);
        terrain.SetWaterLevel(Fixed.FromFloat(waterMeters));
        var grid = new TerrainClass[tiles, tiles];
        var heights = new Fixed[tiles + 1, tiles + 1];
        for (int tz = 0; tz <= tiles; tz++)
            for (int tx = 0; tx <= tiles; tx++)
                heights[tx, tz] = Fixed.FromFloat(heightAt(tx, tz));
        for (int tz = 0; tz < tiles; tz++)
        {
            for (int tx = 0; tx < tiles; tx++)
            {
                float h00 = heightAt(tx, tz);
                float h10 = heightAt(tx + 1, tz);
                float h01 = heightAt(tx, tz + 1);
                float h11 = heightAt(tx + 1, tz + 1);
                float mid = (h00 + h10 + h01 + h11) * 0.25f;
                if (mid <= waterMeters)
                {
                    grid[tx, tz] = TerrainClass.Water;
                    continue;
                }
                float hi = Math.Max(Math.Max(h00, h10), Math.Max(h01, h11));
                float lo = Math.Min(Math.Min(h00, h10), Math.Min(h01, h11));
                float slope = (hi - lo) / tileSize;
                grid[tx, tz] = slope > 1.0f ? TerrainClass.Impassable : TerrainClass.Land;
            }
        }
        terrain.SetPassabilityGrid(grid);
        terrain.SetHeightGrid(heights);
        SimSystem.SetTerrainComponent(terrain);

        int worldM = (int)(tiles * tileSize);
        var f0 = Fixed.Zero;
        var f1 = Fixed.FromInt(worldM);
        var obs = new ObstructionManager(tiles, tileSize);
        obs.SetPassabilityCircular(circular);
        obs.SetBounds(f0, f0, f1, f1);
        SimSystem.SetObstructionManager(obs);
        if (cm.Range != null)
        {
            cm.Range.LosCircular = circular;
            cm.Range.SetBounds(f1);
        }
        cm.Territory?.SetBounds(worldM);

        var pf = new PathfinderComponent(cm);
        string? modsParent = modsPublic != null ? Directory.GetParent(modsPublic)?.FullName : null;
        pf.SetPassabilityConfig(modsParent);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
    }

    public static void ApplyPmp(ComponentManager cm, PmpTerrain pmp, float waterMeters,
        string? modsPublic)
    {
        ApplyHeightGrid(cm, pmp.TilesPerSide, PmpTerrain.TileSize,
            pmp.GetHeight, waterMeters, circular: true, modsPublic);
    }

    public static void ApplyRmgen(ComponentManager cm, MapExport export, string? modsPublic)
    {
        int tiles = export.Size;
        int verts = tiles + 1;
        float Height(int x, int z)
        {
            if (x < 0) x = 0;
            if (z < 0) z = 0;
            if (x >= verts) x = verts - 1;
            if (z >= verts) z = verts - 1;
            int idx = z * verts + x;
            if (idx < 0 || idx >= export.Height.Length) return 0f;
            return export.Height[idx] * PmpTerrain.HeightScale;
        }
        ApplyHeightGrid(cm, tiles, PmpTerrain.TileSize, Height, (float)export.SeaLevel,
            circular: true, modsPublic);
    }

    public static void SpawnRmgenEntities(ComponentManager cm, TemplateLoader templates,
        MapExport export)
    {
        foreach (var ent in export.Entities)
        {
            if (ent.TemplateName.StartsWith("actor|", StringComparison.Ordinal)) continue;
            if (!templates.TemplateExists(ent.TemplateName)) continue;
            float x = (float)ent.Position.X * PmpTerrain.TileSize;
            float z = (float)ent.Position.Y * PmpTerrain.TileSize;
            int owner = ent.PlayerID > 0 ? ent.PlayerID : -1;
            cm.SpawnEntity(ent.TemplateName, x, z, owner);
        }
    }

    public static void SpawnScenario(ComponentManager cm, TemplateLoader templates,
        ScenarioData data, string agentCiv, string oppCiv, string? civsRoot)
    {
        var replacer = new SkirmishReplacer(templates, civsRoot);
        replacer.Apply(data.Entities, pid => pid == 1 ? agentCiv : pid == 2 ? oppCiv : pid <= 0 ? "gaia" : null);
        foreach (var e in data.Entities)
        {
            if (e.IsActor || e.Template.Length == 0) continue;
            if (e.Template.StartsWith("actor|", StringComparison.Ordinal)) continue;
            if (!templates.TemplateExists(e.Template)) continue;
            int owner = e.Player > 0 ? e.Player : -1;
            cm.SpawnEntity(e.Template, e.X, e.Z, owner);
        }
    }
}
