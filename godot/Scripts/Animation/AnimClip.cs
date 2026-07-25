using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.SkeletalAnim;

/// <summary>
/// A parsed animation clip: per-bone keyframe streams extracted from a Godot
/// <see cref="Animation"/> resource. Godot 4.7's AnimationMixer fails to apply
/// bone values from Collada-imported clips (track path + value compatibility
/// issue), so we bypass AnimationPlayer and drive <see cref="Skeleton3D"/>
/// bones directly from this structure.
/// </summary>
public sealed class AnimClip
{
    public float Length;
    public readonly Dictionary<string, List<RotKey>> Rotations = new();
    public readonly Dictionary<string, List<VecKey>> Positions = new();
    public readonly Dictionary<string, List<VecKey>> Scales = new();

    /// <summary>Per-bone REST rotations from the DAE's own skeleton. The mesh GLBs
    /// are Blender-converted (bone axes differ from the raw Collada the animations
    /// come from), so ManualAnimator computes a per-bone correction
    /// <c>mesh_rest * dae_rest⁻¹</c> from these to align the two frames.</summary>
    public readonly Dictionary<string, Quaternion> RestRotations = new();
}

public readonly record struct RotKey(float Time, Quaternion Value);
public readonly record struct VecKey(float Time, Vector3 Value);
