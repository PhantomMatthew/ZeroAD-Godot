using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot.SkeletalAnim;

/// <summary>
/// Drives a <see cref="Skeleton3D"/> from parsed <see cref="AnimClip"/>s, bypassing
/// AnimationPlayer. Godot 4.7's AnimationMixer does not apply Collada-imported bone
/// values correctly, so this interpolates keyframes manually and calls
/// SetBonePoseRotation/Position/Scale directly each frame.
///
/// Attach as a child of the visual node that owns the skeleton. Clips are shared
/// (parsed once per DAE, cached) and referenced by state name ("idle", "walk",
/// "gather_tree", ...). On state change all bones are reset to rest so stale poses
/// from the previous clip don't linger.
/// </summary>
public sealed partial class ManualAnimator : Node
{
    private Skeleton3D? _skeleton;
    // 大小写不敏感:加载侧(ActorComposer.TryLoadExternalAnimations)把 clip key 强制
    // 小写,但请求侧 ResolveAnimationState 可能返回原版 XML 的大小写("Build"/"Walk"/
    // "Idle"——variants/biped/*.xml 的 name 属性本就大写)。OrdinalIgnoreCase 让两侧
    // 无论哪种写法都能匹配,根治"建造工永远 idle"这类大小写契约分裂。
    private readonly Dictionary<string, AnimClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _boneIdx = new();
    // Per-bone frame correction: mesh_rest * dae_rest⁻¹. Converts a DAE animation
    // quaternion (Collada bone frame) into the mesh GLB's bone frame (Blender-
    // converted, axes differ — typically a ~90° X rotation). Computed once at Init.
    private readonly Dictionary<string, Quaternion> _corrections = new();

    private string _current = "";
    private float _elapsed;
    private float _accum;   // 限频累计(见 _Process)

    /// <summary>每秒由 SimBridge 聚合打印的动画段耗时(TEMP-PROF)。</summary>
    public static double FrameCostMs;

    public bool HasState(string state) => _clips.ContainsKey(state);

    /// <summary>One-line diagnostic summary: current state + clip count + bone count.</summary>
    public string Summary => $"state={_current} clips={_clips.Count} bones={_skeleton?.GetBoneCount() ?? -1}";

    /// <summary>Comma-separated list of loaded clip state names (debug dump only).</summary>
    public string StatesCsv => string.Join(",", _clips.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

    public void Init(Skeleton3D skeleton, IReadOnlyDictionary<string, AnimClip> clips)
    {
        _skeleton = skeleton;
        foreach (var kv in clips)
            _clips[kv.Key] = kv.Value;
        ComputeCorrections();
    }

    /// <summary>For each bone, derives the rotation that maps the DAE skeleton's bone
    /// frame to the mesh GLB's bone frame, using the first clip that carries rest
    /// data for that bone. All biped clips share the same rest pose, so one pass
    /// covers every state.</summary>
    private void ComputeCorrections()
    {
        if (_skeleton == null) return;
        foreach (var clip in _clips.Values)
        {
            foreach (var kv in clip.RestRotations)
            {
                if (_corrections.ContainsKey(kv.Key)) continue;
                int idx = _skeleton.FindBone(kv.Key);
                if (idx < 0) continue;
                var meshRest = _skeleton.GetBoneRest(idx).Basis.GetRotationQuaternion();
                _corrections[kv.Key] = meshRest * kv.Value.Inverse();
            }
        }
    }

    public void Play(string state)
    {
        if (state == _current) return;
        _current = state;
        _elapsed = 0f;
        ResetToRest();
    }

    /// <summary>Offsets playback time without changing state — used to desync
    /// otherwise-identical idle loops so a group doesn't animate in lockstep.</summary>
    public void Advance(float seconds)
    {
        _elapsed += seconds;
        if (_skeleton != null && _clips.TryGetValue(_current, out var clip) && clip.Length > 0f)
            _elapsed %= clip.Length;
    }

    public override void _Process(double delta)
    {
        if (_skeleton == null || !_clips.TryGetValue(_current, out var clip)) return;

        // 限频 ~30Hz:骨插值 + ForceUpdateAllBoneTransforms(整骨架矩阵 + CPU 换肤)
        // 是大地图上的每帧大头。动画在 10Hz tick 下 30Hz 已足够平滑(人眼无感),
        // 却把这块成本砍掉一半以上(Corinthian 7400 实体时帧率从 4 翻倍级提升)。
        _accum += (float)delta;
        if (_accum < 1f / 30f) return;
        float step = _accum;
        _accum = 0f;
        var _sw = System.Diagnostics.Stopwatch.GetTimestamp();

        // Animations are Blender-converted GLBs (same pipeline as the mesh GLBs),
        // so their bone frame matches the mesh skeleton exactly — no runtime
        // correction needed. The _corrections map is identity when source and mesh
        // share rest poses (verified per-bone at load).
        _elapsed = clip.Length > 0f
            ? (_elapsed + step) % clip.Length
            : 0f;

        foreach (var kv in clip.Rotations)
        {
            int idx = BoneIdx(kv.Key);
            if (idx >= 0)
            {
                var q = InterpRot(kv.Value, _elapsed);
                // Per-bone frame correction (identity when source + mesh share rest poses,
                // which holds now that animations are Blender-converted GLBs matching the
                // mesh GLBs). Kept as a safety net for any future mixed-source assets.
                if (_corrections.TryGetValue(kv.Key, out var c))
                    q = c * q;
                _skeleton.SetBonePoseRotation(idx, q);
            }
        }
        foreach (var kv in clip.Positions)
        {
            int idx = BoneIdx(kv.Key);
            if (idx >= 0)
                _skeleton.SetBonePosePosition(idx, InterpVec(kv.Value, _elapsed));
        }
        foreach (var kv in clip.Scales)
        {
            int idx = BoneIdx(kv.Key);
            if (idx >= 0)
                _skeleton.SetBonePoseScale(idx, InterpVec(kv.Value, _elapsed));
        }

        // Godot 4.7 Skeleton3D does not auto-propagate SetBonePose* calls to the
        // skinned mesh within the same frame — the mesh keeps rendering the last
        // processed pose. Force the skeleton to recompute global transforms + skin
        // so the new bone poses are visible THIS frame. Without this, units stand
        // in their rest/idle pose even while the animator advances walk/run cycles.
        _skeleton.ForceUpdateAllBoneTransforms();
        FrameCostMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _sw) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private int BoneIdx(string name)
    {
        if (_boneIdx.TryGetValue(name, out var idx)) return idx;
        int found = _skeleton!.FindBone(name);
        _boneIdx[name] = found;
        return found;
    }

    private void ResetToRest()
    {
        if (_skeleton == null) return;
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            var rest = _skeleton.GetBoneRest(i);
            _skeleton.SetBonePosePosition(i, rest.Origin);
            _skeleton.SetBonePoseRotation(i, rest.Basis.GetRotationQuaternion());
            _skeleton.SetBonePoseScale(i, rest.Basis.Scale);
        }
    }

    private static Quaternion InterpRot(List<RotKey> keys, float t)
    {
        int n = keys.Count;
        if (n == 0) return Quaternion.Identity;
        if (t <= keys[0].Time) return keys[0].Value;
        if (t >= keys[n - 1].Time) return keys[n - 1].Value;
        for (int i = 0; i < n - 1; i++)
        {
            if (t >= keys[i].Time && t <= keys[i + 1].Time)
            {
                float span = keys[i + 1].Time - keys[i].Time;
                float f = span > 0f ? (t - keys[i].Time) / span : 0f;
                return keys[i].Value.Slerp(keys[i + 1].Value, f);
            }
        }
        return keys[n - 1].Value;
    }

    private static Vector3 InterpVec(List<VecKey> keys, float t)
    {
        int n = keys.Count;
        if (n == 0) return Vector3.Zero;
        if (t <= keys[0].Time) return keys[0].Value;
        if (t >= keys[n - 1].Time) return keys[n - 1].Value;
        for (int i = 0; i < n - 1; i++)
        {
            if (t >= keys[i].Time && t <= keys[i + 1].Time)
            {
                float span = keys[i + 1].Time - keys[i].Time;
                float f = span > 0f ? (t - keys[i].Time) / span : 0f;
                return keys[i].Value.Lerp(keys[i + 1].Value, f);
            }
        }
        return keys[n - 1].Value;
    }
}
