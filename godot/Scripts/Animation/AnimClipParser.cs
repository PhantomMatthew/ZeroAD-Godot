using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.SkeletalAnim;

/// <summary>
/// Extracts an <see cref="AnimClip"/> from a Godot <see cref="Animation"/>.
/// Collada-imported animations store one clip named "default" with tracks of
/// the form <c>Biped/Skeleton3D:&lt;bone_name&gt;</c> (ROTATION_3D / POSITION_3D /
/// SCALE_3D). We strip the skeleton prefix and index keyframes by bone name so
/// <see cref="ManualAnimator"/> can interpolate per-bone each frame.
/// </summary>
public static class AnimClipParser
{
    /// <summary>Parses the first (only) animation of the source player into a clip.
    /// If <paramref name="daeSkeleton"/> is provided, also records per-bone rest
    /// rotations so ManualAnimator can correct the Blender-vs-Collada frame
    /// mismatch on the mesh GLB skeleton.</summary>
    public static AnimClip Parse(global::Godot.Animation src, Skeleton3D? daeSkeleton = null)
    {
        var clip = new AnimClip { Length = (float)src.Length };

        if (daeSkeleton != null)
        {
            for (int i = 0; i < daeSkeleton.GetBoneCount(); i++)
                clip.RestRotations[daeSkeleton.GetBoneName(i)] =
                    daeSkeleton.GetBoneRest(i).Basis.GetRotationQuaternion();
        }

        for (int i = 0; i < src.GetTrackCount(); i++)
        {
            string pathStr = src.TrackGetPath(i).ToString();
            int colon = pathStr.LastIndexOf(':');
            if (colon < 0) continue;
            string bone = pathStr[(colon + 1)..];
            // Tolerate both the raw Collada format ("arm_L") and any pre-rewritten
            // bone_pose format ("bone_pose/arm_L").
            const string prefix = "bone_pose/";
            if (bone.StartsWith(prefix, System.StringComparison.Ordinal))
                bone = bone[prefix.Length..];
            if (bone.Length == 0) continue;

            var type = src.TrackGetType(i);
            int count = src.TrackGetKeyCount(i);
            switch (type)
            {
                case global::Godot.Animation.TrackType.Rotation3D:
                {
                    if (!clip.Rotations.TryGetValue(bone, out var list))
                        clip.Rotations[bone] = list = new List<RotKey>(count);
                    for (int k = 0; k < count; k++)
                        list.Add(new RotKey((float)src.TrackGetKeyTime(i, k),
                            (Quaternion)src.TrackGetKeyValue(i, k)));
                    break;
                }
                case global::Godot.Animation.TrackType.Position3D:
                {
                    if (!clip.Positions.TryGetValue(bone, out var list))
                        clip.Positions[bone] = list = new List<VecKey>(count);
                    for (int k = 0; k < count; k++)
                        list.Add(new VecKey((float)src.TrackGetKeyTime(i, k),
                            (Vector3)src.TrackGetKeyValue(i, k)));
                    break;
                }
                case global::Godot.Animation.TrackType.Scale3D:
                {
                    if (!clip.Scales.TryGetValue(bone, out var list))
                        clip.Scales[bone] = list = new List<VecKey>(count);
                    for (int k = 0; k < count; k++)
                        list.Add(new VecKey((float)src.TrackGetKeyTime(i, k),
                            (Vector3)src.TrackGetKeyValue(i, k)));
                    break;
                }
            }
        }
        return clip;
    }
}
