using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Pack — port of Pack.js. Siege engines (and similar) pack into a mobile form and unpack into
// a firing form. Packing accumulates ElapsedTime; at PackTime the Packed flag flips and the
// entity transforms into the Pack/Entity template: a NEW entity spawned at the same position
// with the same owner and preserved health fraction, old entity destroyed — mirrors the
// engine's ChangeEntityTemplate.
//
// Original timer = 250 ms interval adding (interval + lateness) per fire, i.e. real elapsed
// time; the port accumulates dt directly — identical totals, deterministic.
// MT_PackFinished is replaced by Tick's return value (UnitAI polls in PACKING/UNPACKING).

[Component("Pack", "Pack")]
public sealed class PackComponent : ComponentBase, IComponentMessageHandler
{
    public bool Packed;              // true = packed/transport form; false = unpacked/active
    public bool Packing;             // true while a pack/unpack is in progress (this.packing)
    public float PackTime = 5f;      // template Pack/Time — seconds to pack/unpack
    public float ElapsedTime;        // this.elapsedTime — progress 0..PackTime
    public string PackEntity = "";   // template Pack/Entity — template to transform into

    /// <summary>Runtime only: the entity this one transformed into on the last completed
    /// pack/unpack (null when no transform happened — e.g. empty PackEntity). Informational
    /// for the presentation layer; NOT serialized (transient pointer, not sim state).</summary>
    public EntityId? LastTransformedTo;

    public bool IsPacked => Packed;
    public bool IsPacking => Packing;
    public bool CanPack() => !Packing && !Packed;
    public bool CanUnpack() => !Packing && Packed;

    /// <summary>GUI 进度 0..1(原版 GetProgress)。</summary>
    public float GetProgress() => PackTime > 0f ? Math.Min(ElapsedTime / PackTime, 1f) : 1f;

    public void Pack()
    {
        if (!CanPack())
            return;
        Packing = true;
    }

    public void Unpack()
    {
        if (!CanUnpack())
            return;
        Packing = true;
    }

    /// <summary>Port of Pack.js CancelPack: abort in-progress pack, reset progress, keep form.</summary>
    public void CancelPack()
    {
        if (!Packing)
            return;
        Packing = false;
        ElapsedTime = 0f;
    }

    /// <summary>Advance packing progress. Returns true on completion (the MT_PackFinished
    /// equivalent): Packed flipped, progress reset, entity transformed into PackEntity.</summary>
    public bool Tick(float dt, ComponentManager cm)
    {
        if (!Packing)
            return false;
        ElapsedTime += dt;
        if (ElapsedTime < PackTime)
            return false;

        Packing = false;
        Packed = !Packed;
        ElapsedTime = 0f;
        Transform(cm);
        return true;
    }

    /// <summary>ChangeEntityTemplate 移植:同位置/同主/血量比例换新模板实体,销毁旧实体。
    /// PackEntity 为空(或位置缺失)时仅翻转标志——无内容包的内核测试可运行。</summary>
    private void Transform(ComponentManager cm)
    {
        LastTransformedTo = null;
        if (string.IsNullOrEmpty(PackEntity))
            return;
        var posComp = cm.QueryInterface<PositionComponent>(Entity);
        if (posComp == null)
            return;
        var pos = posComp.Position;
        var rot = posComp.Rotation;
        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        float healthFrac = cm.QueryInterface<HealthComponent>(Entity)?.HealthFraction ?? 1f;

        var newEnt = cm.SpawnEntity(PackEntity, pos.X.ToFloat(), pos.Z.ToFloat(), owner);
        var newPos = cm.QueryInterface<PositionComponent>(newEnt);
        if (newPos != null)
            newPos.Rotation = rot;
        var newHealth = cm.QueryInterface<HealthComponent>(newEnt);
        if (newHealth != null)
            newHealth.Current = Math.Max(1, (int)(newHealth.Max * healthFrac));

        LastTransformedTo = newEnt;
        cm.DestroyEntity(Entity);
    }

    public override void Serialize(ISerializer s)
    {
        s.Bool("packed", Packed);
        s.Bool("packing", Packing);
        s.NumberFixed("time", Maths.Fixed.FromFloat(PackTime));
        s.NumberFixed("elapsed", Maths.Fixed.FromFloat(ElapsedTime));
        s.StringASCII("entity", PackEntity);
    }

    public override void Deserialize(IDeserializer d)
    {
        Packed = d.Bool("packed");
        Packing = d.Bool("packing");
        PackTime = d.NumberFixed("time").ToFloat();
        ElapsedTime = d.NumberFixed("elapsed").ToFloat();
        PackEntity = d.StringASCII("entity");
    }

    public void HandleMessage(IMessage message) { }
}
