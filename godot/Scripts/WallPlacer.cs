using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>城墙拼链算法(逐行移植原版 simulation/helpers/Walls.js:GetWallPlacement +
/// GetWallSegmentsRec)。输入墙组部件模板与各段链长,输出首尾带塔楼的部件序列
/// (模板/位置/朝向)。纯表现层数学——最终每个部件作为独立 Build 命令下发(含显式
/// 坐标),锁步各端结果一致,不进内核。</summary>
public static class WallPlacer
{
    /// <summary>墙组数据(来自模板 WallSet 段 + 各部件 WallPiece/Length)。</summary>
    public sealed record WallSetData(
        string Tower, string Gate, string Long, string Medium, string Short,
        float TowerWidth, float LongLen, float MediumLen, float ShortLen,
        float MinOverlap, float MaxOverlap);

    public readonly record struct Piece(string Template, float X, float Z, float Angle);

    /// <summary>计算 start→end 的墙件序列(首尾塔楼 + 段间塔楼;段长优选 long)。
    /// 距离过近(放不下两端塔)或无解时返回空。</summary>
    public static List<Piece> Compute(WallSetData set, Vector2 start, Vector2 end)
    {
        var result = new List<Piece>();
        // 段候选(长→短;原版偏好长段——长段可被门替换)。
        var candidates = new[]
        {
            (set.Long, set.LongLen),
            (set.Medium, set.MediumLen),
            (set.Short, set.ShortLen),
        };
        float towerWidth = set.TowerWidth;

        float dx = end.X - start.X, dz = end.Y - start.Y;
        float len = Mathf.Sqrt(dx * dx + dz * dz);
        // 至少容得下首尾两座塔并排。
        if (len <= towerWidth) return result;

        var placement = GetWallSegmentsRec(len, candidates, set.MinOverlap, set.MaxOverlap,
            towerWidth, 0, new List<(string tmpl, float len)>());
        if (placement == null) return result;

        var segments = placement.Value.Segments;
        float r = placement.Value.R;
        // r = 去除塔楼后的剩余距离;均匀摊成段间距(可负=搭进塔楼)。
        float spacing = r / (2f * segments.Count);

        float dirX = dx / len, dirZ = dz / len;
        float angle = -Mathf.Atan2(dz, dx);

        float progress = 0f;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            result.Add(new Piece(
                seg.tmpl,
                start.X + (progress + spacing + seg.len / 2f) * dirX,
                start.Y + (progress + spacing + seg.len / 2f) * dirZ,
                angle));

            if (i < segments.Count - 1)
            {
                result.Add(new Piece(
                    set.Tower,
                    start.X + (progress + seg.len + 2f * spacing) * dirX,
                    start.Y + (progress + seg.len + 2f * spacing) * dirZ,
                    angle));
            }
            progress += seg.len + 2f * spacing;
        }
        return result;
    }

    /// <summary>原版 GetWallSegmentsRec:递归选段使总长与目标距离的差 r 落在
    /// [(1-2*maxOverlap)·N·t, (1-2*minOverlap)·N·t] 区间(塔楼吸收余量)。</summary>
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
                // 超长了:退掉这段,试下一档。
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
