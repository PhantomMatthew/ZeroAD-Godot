using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>实体集合（原版 common-api/entitycollection.js）。
/// C# 用 lazy IEnumerable&lt;AIEntity&gt; + LINQ-style 方法，替代原版的增量维护 Map。
/// registerUpdates/registerGlobalUpdates 是 no-op（lazy 模式每 think 重建，不做 dyn-prop 索引）。
/// 批量命令（move/attackMove/gather）待 Phase 2 集成 NetCommand。</summary>
public sealed class EntityCollection
{
    private readonly IEnumerable<AIEntity> _entities;

    public EntityCollection(IEnumerable<AIEntity> entities) => _entities = entities;

    public int Length => _entities.Count();
    public bool HasEntities() => _entities.Any();
    public IEnumerable<AIEntity> Values() => _entities;
    public List<AIEntity> ToList() => _entities.ToList();
    public uint[] ToIdArray() => _entities.Select(e => e.Id).ToArray();

    /// <summary>过滤（原版 filter，返回新 EntityCollection）。</summary>
    public EntityCollection Filter(Func<AIEntity, bool> predicate)
        => new(_entities.Where(predicate));

    /// <summary>取距 targetPos 最近的 n 个实体（原版 filterNearest）。</summary>
    public EntityCollection FilterNearest(FixedVector2D targetPos, int n)
    {
        var sorted = _entities
            .Where(e => e.Position2D != default)
            .OrderBy(e => AIUtils3.SquareDistance(e.Position2D, targetPos));
        return new(sorted.Take(n));
    }

    /// <summary>按原始谓词过滤（不经 Entity 门面——当前所有数据经 Entity，等同 Filter）。</summary>
    public EntityCollection FilterRaw(Func<AIEntity, bool> callback)
        => new(_entities.Where(callback));

    public void ForEach(Action<AIEntity> action)
    {
        foreach (var e in _entities) action(e);
    }

    /// <summary>注册为持久集合（原版 registerUpdates）。lazy 模式下 no-op。</summary>
    public EntityCollection RegisterUpdates() => this;
    public EntityCollection RegisterGlobalUpdates() => this;

    /// <summary>冻结（原版 freeze——不再自动添加新实体）。lazy 模式下 no-op。</summary>
    public void Freeze() { /* lazy 模式下无增量维护，freeze 语义自动满足 */ }
    public void Defreeze() { }

    /// <summary>中心点(原版 getCentrePosition:成员位置算术平均;
    /// 进攻计划队形锚点/基地定位用)。空集 → Zero。</summary>
    public FixedVector2D GetCentrePosition()
    {
        float sx = 0, sz = 0;
        int n = 0;
        foreach (var e in _entities)
        {
            if (e.Position2D == default) continue;
            sx += e.Position2D.X.ToFloat();
            sz += e.Position2D.Y.ToFloat();
            n++;
        }
        if (n == 0) return FixedVector2D.Zero;
        return new FixedVector2D(
            Maths.Fixed.FromFloat(sx / n), Maths.Fixed.FromFloat(sz / n));
    }

    /// <summary>近似位置(原版 getApproximatePosition:抽样样条平均,
    /// 大集合的廉价质心估计——原版 attackPlan 的 this.position 同款)。</summary>
    public FixedVector2D GetApproximatePosition(int sample = 10)
    {
        var list = ToList();
        if (list.Count == 0) return FixedVector2D.Zero;
        int step = Math.Max(1, list.Count / Math.Max(1, sample));
        float sx = 0, sz = 0;
        int n = 0;
        for (int i = 0; i < list.Count; i += step)
        {
            if (list[i].Position2D == default) continue;
            sx += list[i].Position2D.X.ToFloat();
            sz += list[i].Position2D.Y.ToFloat();
            n++;
        }
        if (n == 0) return FixedVector2D.Zero;
        return new FixedVector2D(
            Maths.Fixed.FromFloat(sx / n), Maths.Fixed.FromFloat(sz / n));
    }

    /// <summary>实体是否在本集合(原版 hasEntId)。</summary>
    public bool HasEntId(uint id)
    {
        foreach (var e in _entities)
            if (e.Id == id) return true;
        return false;
    }
}
