using Godot;
using System.Collections.Generic;

namespace ZeroAD.Godot;

/// <summary>
/// 阴影代理系统。世界视觉挂在 Scale.z=−1 的 _worldRoot 下(对齐 C++ 左手系画面,见 Main.cs),
/// 但 Godot 的阴影深度 pass 对全局负 scale 实例不投影,导致全场景零投影。本类为视觉子树
/// 生成"正规空间(det+1) + 顶点 z 预翻转网格"的投影代理。
/// 隐藏方式:兼容性渲染器(opengl3)下 CastShadow=ShadowsOnly 有 bug——实例仍在主 pass
/// 渲染成黑色/白色残影(最小场景实证),故改用**视觉层隔离**:代理全部挂第 2 层,
/// RTSCamera.CullMask 剔除第 2 层(相机不可见),方向光 shadow_caster_mask 默认全含
/// (不可见但仍写阴影贴图——最小场景实证:layer2 立方体不可见却给邻物投影)。
/// 数学:S=diag(1,1,−1),proxy.Global = visual.Global·S、代理子节点局部 = S·T·S、网格顶点预乘 S,
/// 三层 S 相消(S²=I)后代理世界顶点 ≡ 视觉世界顶点(手性逐顶点一致,非近似)。
/// 蒙皮网格的代理无骨架 → 以绑定姿势投影(站立剪影,2m 单位尺度下与动画影不可分辨)。
/// </summary>
public static class ShadowProxyManager
{
    /// <summary>代理所在视觉层(第 2 层);相机剔除、灯光投影掩码默认包含。</summary>
    public const uint ProxyLayer = 2;

    private static readonly Basis S = new(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f);
    private static readonly Dictionary<Mesh, Mesh> _mirrorCache = new();

    /// <summary>为 visualRoot 子树构建代理树(不入树、不同步;调用方负责 AddChild + SyncFrom)。
    /// visualRoot 自身是 MeshInstance3D 时(如地形)返回的代理根本身也是 MeshInstance3D。</summary>
    public static Node3D CreateProxyRoot(Node3D visualRoot)
    {
        Node3D root = visualRoot is MeshInstance3D rootMi && IsCasterCandidate(rootMi)
            ? CreateProxyMi(rootMi)
            : new Node3D();
        root.Name = visualRoot.Name + "_shadow";
        BuildChildren(visualRoot, root);
        return root;
    }

    /// <summary>每帧对齐:proxy.Global = visual.Global·S,并同步可见性(迷雾隐藏的单位不漏影)。</summary>
    public static void SyncFrom(Node3D proxyRoot, Node3D visualRoot)
    {
        var g = visualRoot.GlobalTransform;
        proxyRoot.GlobalTransform = new Transform3D(g.Basis * S, g.Origin);
        proxyRoot.Visible = visualRoot.Visible;
    }

    private static void BuildChildren(Node3D src, Node3D dst)
    {
        foreach (var child in src.GetChildren())
        {
            if (child is not Node3D n3) continue;
            Node3D proxy = n3 is MeshInstance3D mi && IsCasterCandidate(mi)
                ? CreateProxyMi(mi)
                : new Node3D();
            proxy.Transform = Conjugate(n3.Transform);
            dst.AddChild(proxy);
            BuildChildren(n3, proxy);
        }
    }

    private static MeshInstance3D CreateProxyMi(MeshInstance3D mi) => new()
    {
        Mesh = GetMirroredMesh(mi.Mesh, mi),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        Layers = ProxyLayer,
    };

    /// <summary>扁平网格(选择圈/雾面/领土面/水面,AABB 无厚度)与显式关投影的网格不做代理。</summary>
    private static bool IsCasterCandidate(MeshInstance3D mi) =>
        mi.Mesh != null
        && mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off
        && mi.Mesh.GetAabb().Size.Y >= 0.05f;

    /// <summary>S·T·S 共轭:原点 (x,y,z)→(x,y,−z),基向量 z 行/列取负(det 保持 +1)。</summary>
    private static Transform3D Conjugate(Transform3D t) => new(S * t.Basis * S, S * t.Origin);

    /// <summary>网格的 z 翻转烘焙副本(按源网格缓存,全会话共享):顶点/法线 z 取反、
    /// 切线 z 与副法线手性 w 取反、三角形绕序反转(保持正面朝向与深度 pass 剔除正确)。
    /// 材质取源实例的活动材质(深度 pass 需要 alpha-scissor 叶片的透明镂空,否则树影成实心板)。</summary>
    private static Mesh GetMirroredMesh(Mesh src, MeshInstance3D srcInstance)
    {
        if (_mirrorCache.TryGetValue(src, out var cached)) return cached;

        var baked = new ArrayMesh();
        for (int i = 0; i < src.GetSurfaceCount(); i++)
        {
            var arrays = src.SurfaceGetArrays(i);

            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int v = 0; v < verts.Length; v++) verts[v].Z = -verts[v].Z;
            arrays[(int)Mesh.ArrayType.Vertex] = verts;

            if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
            {
                var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                for (int v = 0; v < normals.Length; v++) normals[v].Z = -normals[v].Z;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
            }

            if (arrays[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
            {
                var tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                for (int v = 0; v + 3 < tangents.Length; v += 4)
                {
                    tangents[v + 2] = -tangents[v + 2];
                    tangents[v + 3] = -tangents[v + 3];
                }
                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            }

            // SurfaceGetPrimitiveType 只在 ArrayMesh 上;运行期网格(GLB 导入/程序化)都是
            // ArrayMesh,兜底按三角形处理(本项目无其他图元)。
            var prim = src is ArrayMesh am ? am.SurfaceGetPrimitiveType(i) : Mesh.PrimitiveType.Triangles;
            if (prim == Mesh.PrimitiveType.Triangles)
            {
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (indices.Length == 0)
                {
                    // 非索引三角形列表:补索引再逐三角形换序(渲染等价,绕序修正统一走索引路径)。
                    indices = new int[verts.Length];
                    for (int v = 0; v < indices.Length; v++) indices[v] = v;
                }
                for (int t = 0; t + 2 < indices.Length; t += 3)
                    (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
                arrays[(int)Mesh.ArrayType.Index] = indices;
            }

            baked.AddSurfaceFromArrays(prim, arrays);
            var mat = srcInstance.GetActiveMaterial(i);
            if (mat != null) baked.SurfaceSetMaterial(i, mat);
        }

        _mirrorCache[src] = baked;
        return baked;
    }
}
