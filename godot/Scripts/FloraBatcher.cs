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
    /// (mesh, localTransform) 部件表,每部件建一个 MultiMeshInstance3D。</summary>
    private Part[][] GetVariants(string template)
    {
        if (_buckets.TryGetValue(template, out var cached)) return cached;

        string? actorPath = Actors.ActorLoader.ExtractActorFromTemplate(template);
        var variants = new List<Part[]>();
        if (actorPath != null)
        {
            for (int v = 0; v < VariantCount; v++)
            {
                // 种子随变体号走(ActorLoader 按种子选 variant);0.7 灰=无玩家色(gaia 原色)。
                var node = Actors.ActorLoader.Instance.Instantiate(actorPath, v * 7919 + 17, new Color(0.7f, 0.6f, 0.4f));
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

    /// <summary>拍平 actor 子树:每个 MeshInstance3D → 一个合批部件(记录根相对变换)。</summary>
    private void Flatten(Node node, Transform3D acc, List<Part> parts)
    {
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
            parts.Add(new Part { Node = mmi, Mm = mm, Local = acc * mi.Transform });
        }
        foreach (var child in node.GetChildren())
        {
            // 非 Node3D 子节点(少见)不改变累计变换,继续下钻。
            var childAcc = child is Node3D n3 ? acc * n3.Transform : acc;
            Flatten(child, childAcc, parts);
        }
    }
}
