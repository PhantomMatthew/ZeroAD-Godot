using System;
using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;
using ZeroAD.Godot.Actors.Parsing;

public sealed class ActorComposer
{
    private const int MaxPropDepth = 5;

    private readonly HashSet<string> _warnedActors = new();
    private readonly HashSet<string> _warnedAttachpoints = new();

    public Node3D BuildStructural(ResolvedActorSpec spec, int depth = 0)
    {
        var root = new Node3D();
        root.SetMeta(LayerMeta.ActorPath, spec.ActorPath);
        if (!string.IsNullOrEmpty(spec.MeshGlbPath))
            root.SetMeta(LayerMeta.MeshGlbPath, spec.MeshGlbPath!);

        // 粒子系统(可移植!EnvironmentParticles 把 art/particles/*.xml 装成
        // GPUParticles3D):粒子 actor(prop 挂载的 dust/splash/fire 触发点)
        // 与 mesh actor 的 actor-local 火焰(burn.xml 等)统一装配。
        if (spec.Particles != null && string.IsNullOrEmpty(spec.MeshGlbPath))
        {
            AttachParticles(root, spec, null);
            return root;
        }

        // 贴花 actor(<decal/> 无 mesh):平躺 quad + baseTex(AlphaScissor)——
        // 替代原版的贴花渲染器,而不是喂兜底盒。
        if (spec.Decal != null && string.IsNullOrEmpty(spec.MeshGlbPath))
        {
            root.AddChild(MakeDecalQuad(spec));
            return root;
        }

        Node3D? instance = null;
        if (!string.IsNullOrEmpty(spec.MeshGlbPath))
            instance = LoadAndInstantiateGlb(spec.MeshGlbPath!);

        if (instance == null)
        {
            root.AddChild(MakeFallbackBox(Colors.White));
            string meshReason = string.IsNullOrEmpty(spec.MeshGlbPath)
                ? "no-mesh-resolved"
                : $"glb-load-failed:{spec.MeshGlbPath}";
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(spec.ActorPath, meshReason);
            WarnActorOnce(spec.ActorPath, $"BuildStructural: {meshReason}; using fallback box");
            return root;
        }

        root.AddChild(instance);

        // actor-local 火焰(原版 <particles file="burn.xml"/> 随模型):
        // 壁炉/灯火等结构火焰接根(原版 attachpoint=root)。
        AttachParticles(root, spec, null);

        // NOTE: animations are NOT loaded here. BuildStructural's result is packed into a
        // PackedScene by ComposedSceneCache and re-instantiated per spawn; PackedScene only
        // serializes Godot-native properties, so ManualAnimator's C# clip state would be
        // lost. Clips are attached to the final instance in ActorLoader.Instantiate instead.
        var skeleton = AttachpointResolver.FindSkeleton(instance);

        if (depth < MaxPropDepth)
        {
            // TEMP-DIAG: 雅典 CC 的 prop 挂载(7 个 root 装饰 prop 是否全解析/挂载)
            if (spec.ActorPath.Contains("athenians/civil_centre"))
                ZeroAD.Sim.Diag.Log("Actor", $"BuildStructural {spec.ActorPath}: spec.Props.Count={spec.Props.Count} attachpoints=[{string.Join(",", spec.Props.Select(p => p.Key + "→" + p.Value.ActorPath.Split('/').Last()))}]");
            foreach (var kv in spec.Props)
            {
                string attachpoint = kv.Key;
                var propSpec = kv.Value;

                var childSpec = ResolveChildSpec(propSpec);
                if (childSpec == null)
                {
                    if (spec.ActorPath.Contains("athenians/civil_centre"))
                        ZeroAD.Sim.Diag.Warn("Actor", $"  childSpec NULL for prop {propSpec.ActorPath}");
                    continue;
                }

                var childNode = BuildStructural(childSpec, depth + 1);
                AttachProp(root, instance, skeleton, attachpoint, childNode, spec.ActorPath);
            }
        }

        return root;
    }

    /// <summary>粒子装配:spec.Particles 是 <particles file="x.xml"/> 的文件名
    /// (art/particles 定义名);为空字符串(<particles/> 无 file)时用 actor 名。
    /// 失败静默(缺贴图/定义时该装饰缺席,不影响主体)。</summary>
    private void AttachParticles(Node3D root, ResolvedActorSpec spec, Vector3? offset)
    {
        string defName = spec.Particles ?? "";
        if (defName.Length == 0)
            defName = System.IO.Path.GetFileNameWithoutExtension(spec.ActorPath);
        else if (defName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            defName = defName[..^4];
        var particles = EnvironmentParticles.BuildByName(defName);
        if (particles == null) return;
        particles.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (offset.HasValue) particles.Position = offset.Value;
        root.AddChild(particles);
    }

    private void AttachProp(Node3D root, Node3D instance, Skeleton3D? skeleton, string attachpoint, Node3D childNode, string actorPath)
    {
        if (!AttachPropAt(root, skeleton, attachpoint, childNode))
            WarnAttachpointOnce(actorPath, attachpoint);
    }

    /// <summary>Attaches a prop at the given attachpoint: bone attachment when the
    /// skeleton has a matching bone, prop-point node otherwise, unit root as last
    /// resort. Tags the child with <see cref="LayerMeta.PropAttachpoint"/> so
    /// StatePropSwitcher can find base props later. Returns false when no real
    /// attachpoint matched (child attached to root).</summary>
    public static bool AttachPropAt(Node3D root, Skeleton3D? skeleton, string attachpoint, Node3D childNode)
    {
        childNode.SetMeta(LayerMeta.PropAttachpoint, attachpoint);

        if (skeleton != null)
        {
            int boneIdx = AttachpointResolver.FindBoneIndex(skeleton, attachpoint);
            if (boneIdx != -1)
            {
                // Static prop meshes carry a baked -90°X from the DAE→GLB
                // conversion (DAE prop-frame +Z becomes GLB +Y — bounds-verified).
                // Whether to undo it depends on the bone class: LEAF prop bones
                // (helmet, weapon_R/L, shield_arm, ammo...) kept the DAE joint
                // frame through conversion, so their props need +90°X; CHAIN
                // bones (head, neck, spine) were reoriented by Blender's
                // Y-along-child convention, which already cancels the bake —
                // compensating them double-rotates (-90°X put faces skyward,
                // +90°X planted them on the ground; raw/identity was correct).
                if (!attachpoint.Equals("head", StringComparison.OrdinalIgnoreCase))
                    childNode.Rotation = new Vector3(Mathf.Pi / 2f, 0f, 0f);
                var ba = new BoneAttachment3D();
                skeleton.AddChild(ba);
                ba.BoneIdx = boneIdx;
                ba.AddChild(childNode);
                return true;
            }
        }

        var attachNode = AttachpointResolver.FindNode(root, attachpoint);
        if (attachNode != null)
        {
            // Match the C++ engine: PMDConvert::AddStaticPropPoints decomposes each
            // prop-point's world transform into translation + rotation and DISCARDS the
            // scale (PropPoint stores only t and q, never scale). Zeroing only
            // attachNode.Scale is not enough when the point is nested under a
            // scaled mesh node (weap_javelin_shaft_b/c, weap_shaft_wood_*):
            // jav_blade then inherits [160,100,160] and becomes a ~75m spearhead.
            // GlobalTransform is unusable here — BuildStructural runs off-tree,
            // and Godot then treats GlobalTransform as local Transform.
            attachNode.Scale = Vector3.One;
            attachNode.AddChild(childNode);
            childNode.Scale = ReciprocalScale(ScaleRelativeTo(attachNode, root));
            return true;
        }

        root.AddChild(childNode);
        return false;
    }

    public Node3D Compose(ResolvedActorSpec spec, Color teamColor, int depth = 0)
    {
        var root = BuildStructural(spec, depth);
        InstanceCustomizer.Apply(root, spec, teamColor, entitySeed: 0);
        TryPlayIdle(root);
        return root;
    }

    public static void SetAnimationState(Node3D instance, string state)
    {
        if (string.IsNullOrEmpty(state)) return;
        // 子树里的每一个 animator 都播同名状态:坐骑播马的 idle/walk,骑手 prop
        // 播骑乘 idle/walk(C++ 对整个 entity 重跑 variation,各部件各取同名 clip)。
        foreach (var animator in FindAllAnimators(instance))
            if (animator.HasState(state))
                animator.Play(state);
        var switcher = StatePropSwitcher.Find(instance);
        switcher?.Apply(state);
    }

    /// <summary>递归收集子树内所有 ManualAnimator(坐骑 + 骑手等带动画的 prop)。</summary>
    internal static IEnumerable<ZeroAD.Godot.SkeletalAnim.ManualAnimator> FindAllAnimators(Node node)
    {
        if (node is ZeroAD.Godot.SkeletalAnim.ManualAnimator ma) yield return ma;
        foreach (var child in node.GetChildren())
            foreach (var found in FindAllAnimators(child))
                yield return found;
    }

    /// <summary>给带骨骼的 prop(骑兵骑手、战车乘员等)挂它们自己的动画集。
    /// BuildStructural 的产物被 PackedScene 缓存,C# clip 状态只能在实例化后挂;
    /// 主 spec 的动画由 Instantiate 直接挂,prop 的动画在这里按 (seed,attachpoint)
    /// 链式子种子重新解析 spec 后挂到 prop 子树——与 BuildStructural 用的
    /// HashCode.Combine(seed, attachpoint) 完全一致,变体选择因此与缓存场景一致。</summary>
    internal static void AttachPropAnimations(Node3D instance, int seed, int depth = 0)
    {
        if (depth > MaxPropDepth) return;
        foreach (var child in instance.GetChildren())
        {
            if (child is not Node3D n3) continue;
            if (n3.HasMeta(LayerMeta.PropAttachpoint) && n3.HasMeta(LayerMeta.ActorPath))
            {
                string attachpoint = (string)n3.GetMeta(LayerMeta.PropAttachpoint);
                int subSeed = HashCode.Combine(seed, attachpoint);
                var childSpec = ResolveChildSpec(new PropSpec((string)n3.GetMeta(LayerMeta.ActorPath), subSeed));
                if (childSpec != null && childSpec.Animations.Count > 0)
                    TryLoadExternalAnimations(n3, childSpec.Animations);
                AttachPropAnimations(n3, subSeed, depth + 1);
            }
            else
            {
                AttachPropAnimations(n3, seed, depth + 1);
            }
        }
    }

    private static readonly HashSet<string> _animWarned = new();

    // Parsed DAE clips keyed by resolved animation path — shared across every unit
    // (same DAE → same keyframes). Avoids re-parsing 200+ tracks per spawn.
    private static readonly Dictionary<string, ZeroAD.Godot.SkeletalAnim.AnimClip> _clipCache = new();

    public static void TryLoadExternalAnimations(Node3D baseInstance, IReadOnlyList<AnimRef> animations)
    {
        if (animations.Count == 0) return;

        var skeleton = AttachpointResolver.FindSkeleton(baseInstance);
        if (skeleton == null) return; // props (capes etc.) have no skeleton — skip.

        var animator = new ZeroAD.Godot.SkeletalAnim.ManualAnimator { Name = "ManualAnimator" };
        baseInstance.AddChild(animator);

        var clips = new Dictionary<string, ZeroAD.Godot.SkeletalAnim.AnimClip>(System.StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var anim in animations)
        {
            string stateName = anim.Name.ToLowerInvariant();
            // All same-name candidates are tried in actor order until one resolves —
            // a missing/stale conversion must not cost the whole animation state.
            if (added.Contains(stateName)) continue;

            var resolved = AssetPathResolver.Instance.ResolveAnimation(anim.File);
            if (!resolved.Found || resolved.Value == null) continue;

            var clip = LoadClip(resolved.Value);
            if (clip == null) continue;

            clips[stateName] = clip;
            added.Add(stateName);
        }

        animator.Init(skeleton, clips);
    }

    /// <summary>Loads + parses a DAE/GLB animation scene into an <see cref="ZeroAD.Godot.SkeletalAnim.AnimClip"/>,
    /// cached by resolved path.</summary>
    private static ZeroAD.Godot.SkeletalAnim.AnimClip? LoadClip(string resolvedPath)
    {
        if (_clipCache.TryGetValue(resolvedPath, out var cached)) return cached;

        var scene = ModelLibrary.LoadAnimationScene(resolvedPath);
        if (scene == null) { _clipCache[resolvedPath] = null!; return null; }

        var temp = scene.Instantiate();
        if (temp == null) { _clipCache[resolvedPath] = null!; return null; }

        ZeroAD.Godot.SkeletalAnim.AnimClip? result = null;
        try
        {
            var src = ModelLibrary.FindAnimationPlayer(temp);
            // The DAE's own skeleton carries the reference rest rotations used to
            // correct the Blender-converted mesh GLB's frame mismatch.
            var daeSkel = AttachpointResolver.FindSkeleton(temp);
            if (src != null)
            {
                // A GLB can carry several actions (e.g. gather_wood.dae has two); Blender's
                // gltf export often emits an empty placeholder as the first one. Pick the
                // animation with the most tracks rather than the first, so the real clip wins.
                Animation? best = null;
                int bestTracks = 0;
                foreach (var libNameVar in src.GetAnimationLibraryList())
                {
                    var lib = src.GetAnimationLibrary(libNameVar.ToString());
                    if (lib == null) continue;
                    foreach (var animNameVar in lib.GetAnimationList())
                    {
                        var a = lib.GetAnimation(animNameVar.ToString());
                        if (a != null && a.GetTrackCount() > bestTracks)
                        {
                            best = a;
                            bestTracks = a.GetTrackCount();
                        }
                    }
                }
                if (best != null)
                    result = ZeroAD.Godot.SkeletalAnim.AnimClipParser.Parse(best, daeSkel);
            }
        }
        finally
        {
            temp.QueueFree();
        }

        _clipCache[resolvedPath] = result!;
        return result;
    }

    public static void LoadAnimationClips(Node3D baseInstance, IEnumerable<string> animGlbRelPaths)
    {
        // Legacy entry point — delegate to the manual-animator pipeline.
        var refs = new List<AnimRef>();
        foreach (var p in animGlbRelPaths)
            if (!string.IsNullOrEmpty(p)) refs.Add(new AnimRef("clip", p, 1));
        if (refs.Count > 0) TryLoadExternalAnimations(baseInstance, refs);
    }

    internal static ResolvedActorSpec? ResolveChildSpec(PropSpec prop) =>
        SpecMerger.MergeFromActorPath(
            ActorLoader.ResolveActorAbsPath(prop.ActorPath),
            prop.SubSeed,
            AssetPathResolver.Instance);

    private static Node3D? LoadAndInstantiateGlb(string relGlbPath)
    {
        var scene = ModelLibrary.LoadGlb(relGlbPath);
        if (scene == null) return null;
        // Never rescale by unposed AABB. Sheep/chicken verts are ~3 cm after
        // Blender <unit> while skeleton rest + idle are already meters — ×100
        // made gaia fauna gigantic (the reverted SkinnedMeshUnitCompensator).
        // The inverse class (meter verts + inch/cm bones on waypoint/garrison/
        // target-marker props) is fixed at the source by
        // tools/fix_glb_skeleton_unit_space.py, wired into run_full_pipeline.sh.
        return scene.Instantiate<Node3D>();
    }
    /// not including, <paramref name="ancestor"/>. Identity when they are the
    /// same node or <paramref name="node"/> is not under <paramref name="ancestor"/>.</summary>
    internal static Vector3 ScaleRelativeTo(Node3D node, Node3D ancestor)
    {
        var s = Vector3.One;
        Node? n = node;
        while (n != null && n != ancestor)
        {
            if (n is Node3D n3)
                s *= n3.Scale;
            n = n.GetParent();
        }
        return n == ancestor ? s : Vector3.One;
    }

    internal static Vector3 ReciprocalScale(Vector3 s) => new(
        Mathf.Abs(s.X) < 1e-8f ? 1f : 1f / s.X,
        Mathf.Abs(s.Y) < 1e-8f ? 1f : 1f / s.Y,
        Mathf.Abs(s.Z) < 1e-8f ? 1f : 1f / s.Z);

    internal static void TryPlayIdle(Node3D instance)
    {
        bool any = false;
        foreach (var animator in FindAllAnimators(instance))
            if (animator.HasState("idle")) any = true;
        if (!any) return;
        SetAnimationState(instance, "idle");  // 同时触发 StatePropSwitcher
        foreach (var animator in FindAllAnimators(instance))
            animator.Advance((float)GD.Randf() * 2.0f);
    }

    public static MeshInstance3D MakeFallbackBox(Color color)
    {
        var mi = new MeshInstance3D();
        var box = new BoxMesh { Size = new Vector3(1.5f, 2f, 1.5f) };
        mi.Mesh = box;
        var mat = new StandardMaterial3D { AlbedoColor = color };
        mi.MaterialOverride = mat;
        mi.Position = new Vector3(0, 1f, 0);
        return mi;
    }

    private static readonly Dictionary<string, ImageTexture?> _decalTexCache = new();

    /// <summary>贴花 quad(原版 decal 渲染器的最小替代):平躺 QuadMesh(width×depth),
    /// baseTex AlphaScissor、双面、离地 5cm 防 z-fighting,angle/offset 按 XML 参数。</summary>
    private static MeshInstance3D MakeDecalQuad(ResolvedActorSpec spec)
    {
        var d = spec.Decal!;
        var mesh = new QuadMesh { Size = new Vector2(d.Width > 0 ? d.Width : 4f, d.Depth > 0 ? d.Depth : 4f) };
        var mi = new MeshInstance3D { Mesh = mesh };

        ImageTexture? tex = null;
        if (spec.Textures.TryGetValue("baseTex", out var rel))
        {
            if (!_decalTexCache.TryGetValue(rel, out tex))
            {
                string resPath = "res://assets/textures/" + rel.Replace('\\', '/');
                var img = AssetIO.LoadImageRes(resPath);
                tex = img != null ? ImageTexture.CreateFromImage(img) : null;
                _decalTexCache[rel] = tex;
            }
        }
        var mat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.4f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (tex != null) mat.AlbedoTexture = tex;
        else mat.AlbedoColor = new Color(0.4f, 0.35f, 0.25f);   // 贴图缺失时泥色,绝不白盒
        mi.MaterialOverride = mat;

        // QuadMesh 面朝 +Z;绕 X 旋转 -90° 平躺到地面(Y 朝上),angle 为偏航,offset 平移。
        mi.Rotation = new Vector3(-Mathf.Pi / 2f, d.Angle, 0f);
        mi.Position = new Vector3(d.OffsetX, 0.05f, d.OffsetZ);
        return mi;
    }

    private void WarnActorOnce(string actor, string message)
    {
        if (_warnedActors.Add(actor + message))
            ZeroAD.Sim.Diag.Warn("Actor", message);
    }

    private void WarnAttachpointOnce(string actor, string attachpoint)
    {
        if (_warnedAttachpoints.Add(actor + "|" + attachpoint))
            ZeroAD.Sim.Diag.Warn("Actor", $"ActorComposer: attachpoint '{attachpoint}' not found in '{actor}'");
    }
}
