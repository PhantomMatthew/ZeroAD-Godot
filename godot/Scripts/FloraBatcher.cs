using System.Collections.Generic;
using Godot;
using ZeroAD.Sim;

namespace ZeroAD.Godot;

/// <summary>静态 gaia 资源(树/石/矿/遗迹)的 MultiMesh 合批渲染。
/// 动机:此前每株植物/岩石是完整 Node3D 子树(根 + 2-3 个 MeshInstance3D +
/// 等量阴影代理),mainland 约 700 株 → 场景 5000+ 节点、拉远视角 draw calls 上千。
/// 合批后:每个 (模板×变体) 桶的每个网格部件 = 1 个 MultiMeshInstance3D(1 次 draw call
/// /surface),实体本身只留一个无网格锚点(选择圈/诊断仍按 EntityNodes 工作)。
/// 移除/雾隐 = 实例变换写零缩放(原地隐藏,槽位进空闲链表复用);不重建 MultiMesh。
/// 不适用:动物(有 Health,要血条/动画)、mirage(雾中变暗按节点处理)、建筑/单位。</summary>
public sealed class FloraBatcher
{
    /// <summary>每模板变体数(种子驱动 actor 变体,保树种多样性)。</summary>
    private const int VariantCount = 3;
    /// <summary>MultiMesh 容量增长步长(避免逐实例重分配)。</summary>
    private const int GrowChunk = 64;

    private sealed class Part
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mm = null!;
        public Transform3D Local;          // 部件相对 actor 根的局部变换
        public readonly Stack<int> FreeSlots = new();
        public int Top;                    // 已用槽位上界(不含空闲)
    }

    private sealed class EntitySlots
    {
        public Part[] Parts = System.Array.Empty<Part>();
        public int[] Slots = System.Array.Empty<int>();
        public Transform3D Base;           // 实体世界变换(恢复可见时用)
        public bool Visible = true;
        public string Template = "";
        public int Variant;                // 选中的变体索引(id % VariantCount)——诊断用
    }

    private readonly Node3D _root;
    private readonly Dictionary<string, Part[][]> _buckets = new();  // template → variants → parts
    private readonly Dictionary<uint, EntitySlots> _entities = new();

    public FloraBatcher(Node3D parent)
    {
        _root = new Node3D { Name = "FloraBatch" };
        parent.AddChild(_root);
    }

    public bool Contains(EntityId id) => _entities.ContainsKey(id.Value);

    /// <summary>诊断统计:逐模板返回 (实体数, 当前可见数)——ZEROAD_FLORA_DUMP 用。</summary>
    public System.Collections.Generic.IEnumerable<(string Template, int Total, int Visible)> Stats()
    {
        foreach (var g in _entities.Values.GroupBy(e => e.Template))
            yield return (g.Key, g.Count(), g.Count(e => e.Visible));
    }

        /// <summary>诊断采样:逐模板前 count 个实体的 Base 平移(世界坐标)——查变换异常。</summary>
    public System.Collections.Generic.IEnumerable<string> SampleBases(int count)
    {
        foreach (var g in _entities.Values.GroupBy(e => e.Template))
        {
            int i = 0;
            foreach (var e in g)
            {
                if (i++ >= count) break;
                yield return $"{g.Key}[{i}] pos={e.Base.Origin}";
            }
        }
    }

    /// <summary>诊断:逐实体(变体, 实时变换)对照——dev 诊断。只输出矩形内的实体。</summary>
    public System.Collections.Generic.IEnumerable<string> SampleVariantsInRect(
        float minX, float minZ, float maxX, float maxZ)
    {
        foreach (var e in _entities.Values)
        {
            var o = e.Base.Origin;
            if (o.X < minX || o.X > maxX || o.Z < minZ || o.Z > maxZ) continue;
            var t = e.Parts[0].Mm.GetInstanceTransform(e.Slots[0]);
            yield return $"{e.Template} v{e.Variant} id_slot={e.Slots[0]} base=({o.X:F0},{o.Y:F1},{o.Z:F0}) " +
                         $"live=({t.Origin.X:F0},{t.Origin.Y:F1},{t.Origin.Z:F0}) scaleX={t.Basis.Scale.X:F2}";
        }
    }

    /// <summary>诊断:逐模板统计 MultiMesh 实时实例状态(非零缩放=真渲染,零缩放=被隐)。</summary>
    public System.Collections.Generic.IEnumerable<string> ReportLive()
    {
        foreach (var g in _entities.Values.GroupBy(e => e.Template))
        {
            int live = 0, zero = 0, movedOff = 0;
            foreach (var e in g)
            {
                var t = e.Parts[0].Mm.GetInstanceTransform(e.Slots[0]);
                float sx = t.Basis.Scale.X;
                if (sx < 0.01f) zero++;
                else if (t.Origin != e.Base.Origin) movedOff++;
                else live++;
            }
            yield return $"{g.Key}: live={live} zeroScaled={zero} movedOff={movedOff}";
        }
    }

    /// <summary>诊断:逐部件(变体×网格)的容量/网格顶点数/活实例数——查空网格桶。</summary>
    public System.Collections.Generic.IEnumerable<string> ReportParts()
    {
        foreach (var bucket in _buckets)
            for (int v = 0; v < bucket.Value.Length; v++)
                for (int p = 0; p < bucket.Value[v].Length; p++)
                {
                    var part = bucket.Value[v][p];
                    int verts = 0;
                    var arr = part.Node.Multimesh.Mesh?.SurfaceGetArrays(0);
                    if (arr != null && arr.Count > 0)
                        verts = arr[0].AsVector3Array().Length;
                    int live = 0;
                    for (int i = 0; i < part.Top; i++)
                        if (part.Mm.GetInstanceTransform(i).Basis.Scale.X > 0.01f) live++;
                    yield return $"{bucket.Key} v{v} p{p}: verts={verts} cap={part.Mm.InstanceCount} used={part.Top} live={live} aabb={part.Mm.GetAabb()}";
                }
    }

    /// <summary>把实体并入合批;成功返回 true。actor 无网格(兜底盒模型)时返回 false,
    /// 调用方走旧的逐节点路径。</summary>
    public bool Add(EntityId id, string template, Vector3 pos, float yaw)
    {
        var variants = GetVariants(template);
        if (variants.Length == 0) return false;

        // 确定性选变体(实体 id 哈希;同种树不再全员同形,也无逐实体种子开销)。
        var parts = variants[id.Value % VariantCount];
        var slots = new EntitySlots
        {
            Parts = parts,
            Slots = new int[parts.Length],
            Base = new Transform3D(new Basis(Vector3.Up, yaw), pos),
            Template = template,
            Variant = (int)(id.Value % VariantCount),
        };
        for (int i = 0; i < parts.Length; i++)
        {
            int slot = AllocSlot(parts[i]);
            parts[i].Mm.SetInstanceTransform(slot, slots.Base * parts[i].Local);
            slots.Slots[i] = slot;
        }
        _entities[id.Value] = slots;
        return true;
    }

    /// <summary>隐藏/恢复(雾隐与销毁共用):零缩放写实例,视觉消失但槽位保留。</summary>
    public void SetVisible(EntityId id, bool visible)
    {
        if (!_entities.TryGetValue(id.Value, out var es) || es.Visible == visible) return;
        es.Visible = visible;
        for (int i = 0; i < es.Parts.Length; i++)
            es.Parts[i].Mm.SetInstanceTransform(es.Slots[i],
                visible ? es.Base * es.Parts[i].Local : Transform3D.Identity.Scaled(Vector3.Zero));
    }

    /// <summary>实体销毁:零缩放 + 槽位回收(下棵同种树复用)。</summary>
    public void Remove(EntityId id)
    {
        if (!_entities.Remove(id.Value, out var es)) return;
        for (int i = 0; i < es.Parts.Length; i++)
        {
            es.Parts[i].Mm.SetInstanceTransform(es.Slots[i], Transform3D.Identity.Scaled(Vector3.Zero));
            es.Parts[i].FreeSlots.Push(es.Slots[i]);
        }
    }

    /// <summary>存档重建/换图:全部清空(MultiMesh 节点释放,桶表重置)。</summary>
    public void Clear()
    {
        foreach (var child in _root.GetChildren()) child.QueueFree();
        _buckets.Clear();
        _entities.Clear();
    }

    // ── 内部 ──

    private int AllocSlot(Part part)
    {
        if (part.FreeSlots.Count > 0) return part.FreeSlots.Pop();
        int slot = part.Top++;
        if (slot >= part.Mm.InstanceCount)
        {
            // 扩容量并零初始化新增槽位(未写变换的实例会以恒等变换渲染在原点!)。
            int oldCap = part.Mm.InstanceCount;
            part.Mm.InstanceCount = slot + GrowChunk;
            for (int i = oldCap; i < part.Mm.InstanceCount; i++)
                part.Mm.SetInstanceTransform(i, Transform3D.Identity.Scaled(Vector3.Zero));
        }
        return slot;
    }

        /// <summary>取模板的变体桶:actor 实例化 VariantCount 次(不同种子),拍平成
        /// (mesh, localTransform) 部件表,每部件建一个 MultiMeshInstance3D。
        /// 已知问题:部分 actor 在大种子下变体选择的 mesh/贴图组渲染失败(Gold Oasis
        /// 棕榈 c/d 桶),逐节点路径(种子 0)却稳定可用——合批退化为全部桶共享种子 0
        /// 网格(多样性暂时让位给可用性,直到变体选择根因查清)。</summary>
    private Part[][] GetVariants(string template)
    {
        if (_buckets.TryGetValue(template, out var cached)) return cached;

        string? actorPath = Actors.ActorLoader.ExtractActorFromTemplate(template);
        var variants = new List<Part[]>();
        if (actorPath != null)
        {
            for (int v = 0; v < VariantCount; v++)
            {
                // 种子固定 0:同非合批路径(ModelLibrary/逐节点渲染验证可用);
                // 0.7 灰=无玩家色(gaia 原色)。
                var node = Actors.ActorLoader.Instance.Instantiate(actorPath, 0, new Color(0.7f, 0.6f, 0.4f));
                if (node == null) continue;
                var parts = new List<Part>();
                Flatten(node, Transform3D.Identity, parts);
                node.Free();   // 未入树,直接释放
                if (parts.Count > 0) variants.Add(parts.ToArray());
            }
        }
        var result = variants.Count > 0 ? variants.ToArray() : System.Array.Empty<Part[]>();
        _buckets[template] = result;
        return result;
    }

    /// <summary>拍平 actor 子树:每个 MeshInstance3D → 一个合批部件(记录根相对变换)。
    /// <paramref name="acc"/> 是父节点累计变换,不含 <paramref name="node"/> 自己。
    /// 旧写法先在父循环里把 child.Transform 乘进 acc,到 MeshInstance3D 再乘一次
    /// mi.Transform:GLB 里带 100× 的网格节点(角豆树冠、花岗岩)会被平方成 10000×,
    /// 随机图市政厅就会被几百米的树和石头挡住。Scenario 走逐节点路径,所以 Tutorial 上看不出来。</summary>
    private void Flatten(Node node, Transform3D acc, List<Part> parts)
    {
        Transform3D world = node is Node3D n3 ? acc * n3.Transform : acc;
        if (node is MeshInstance3D mi && mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0)
        {
            var mm = new MultiMesh
            {
                Mesh = mi.Mesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = 0,
            };
            var mmi = new MultiMeshInstance3D { Multimesh = mm };
            // 材质:actor 组合器可能写了材质覆盖(gaia 一般无,照抄保真)。
            if (mi.MaterialOverride != null) mmi.MaterialOverride = mi.MaterialOverride;
            _root.AddChild(mmi);
            parts.Add(new Part { Node = mmi, Mm = mm, Local = world });
        }
        foreach (var child in node.GetChildren())
            Flatten(child, world, parts);
    }
}
