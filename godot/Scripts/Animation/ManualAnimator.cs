using System.Collections.Generic;
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
    private readonly Dictionary<string, AnimClip> _clips = new();
    private readonly Dictionary<string, int> _boneIdx = new();
    // Per-bone frame correction: mesh_rest * dae_rest⁻¹. Converts a DAE animation
    // quaternion (Collada bone frame) into the mesh GLB's bone frame (Blender-
    // converted, axes differ — typically a ~90° X rotation). Computed once at Init.
    private readonly Dictionary<string, Quaternion> _corrections = new();

    private string _current = "";
    private float _elapsed;

    public bool HasState(string state) => _clips.ContainsKey(state);

    /// <summary>One-line diagnostic summary: current state + clip count + bone count.</summary>
    public string Summary => $"state={_current} clips={_clips.Count} bones={_skeleton?.GetBoneCount() ?? -1}";

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

        // Animations are Blender-converted GLBs (same pipeline as the mesh GLBs),
        // so their bone frame matches the mesh skeleton exactly — no runtime
        // correction needed. The _corrections map is identity when source and mesh
        // share rest poses (verified per-bone at load).
        _elapsed = clip.Length > 0f
            ? (_elapsed + (float)delta) % clip.Length
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
