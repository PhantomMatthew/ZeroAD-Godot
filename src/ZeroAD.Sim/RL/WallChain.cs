using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.RL;

/// <summary>Headless copy of <c>WallPlacer.Compute</c> (Walls.js GetWallPlacement)
/// so RL Build of a wallset can emit a piece chain without Godot.</summary>
public static class WallChain
{
    public readonly record struct Piece(string Template, float X, float Z, float Angle);

    public static List<Piece> Compute(
        string tower, string wallLong, string wallMedium, string wallShort,
        float towerWidth, float longLen, float mediumLen, float shortLen,
        float minOverlap, float maxOverlap,
        float startX, float startZ, float endX, float endZ)
    {
        var result = new List<Piece>();
        var candidates = new[]
        {
            (wallLong, longLen),
            (wallMedium, mediumLen),
            (wallShort, shortLen),
        };
        float dx = endX - startX, dz = endZ - startZ;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len <= towerWidth) return result;

        var segments = new List<(string tmpl, float len)>();
        var placement = GetWallSegmentsRec(len, candidates, minOverlap, maxOverlap,
            towerWidth, 0f, segments);
        if (placement == null) return result;

        var segs = placement.Value.Segments;
        float r = placement.Value.R;
        float spacing = r / (2f * segs.Count);
        float dirX = dx / len, dirZ = dz / len;
        float angle = -MathF.Atan2(dz, dx);

        float progress = 0f;
        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            result.Add(new Piece(
                seg.tmpl,
                startX + (progress + spacing + seg.len / 2f) * dirX,
                startZ + (progress + spacing + seg.len / 2f) * dirZ,
                angle));
            if (i < segs.Count - 1)
            {
                result.Add(new Piece(
                    tower,
                    startX + (progress + seg.len + 2f * spacing) * dirX,
                    startZ + (progress + seg.len + 2f * spacing) * dirZ,
                    angle));
            }
            progress += seg.len + 2f * spacing;
        }
        return result;
    }

    private static (List<(string tmpl, float len)> Segments, float R)? GetWallSegmentsRec(
        float d, (string tmpl, float len)[] candidates, float minOverlap, float maxOverlap,
        float t, float distSoFar, List<(string tmpl, float len)> segments)
    {
        foreach (var cand in candidates)
        {
            segments.Add(cand);
            float newDist = distSoFar + cand.len;
            float r = d - newDist;
            float rLower = (1f - 2f * maxOverlap) * segments.Count * t;
            float rUpper = (1f - 2f * minOverlap) * segments.Count * t;
            if (r < rLower)
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (r > rUpper)
            {
                var rec = GetWallSegmentsRec(d, candidates, minOverlap, maxOverlap, t, newDist, segments);
                if (rec == null)
                {
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                return rec;
            }
            return (segments, r);
        }
        return null;
    }
}
