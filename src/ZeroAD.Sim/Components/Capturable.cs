using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 可占领(对齐原版 Capturable.js):每玩家一份占领点(下标 0=gaia,总和 = MaxCapturePoints),
/// 主人那份归零 → 翻面给 CP 最多的玩家。已移植:Capture/CanCapture(单位捕获入口,
/// 由 DelayedDamage 捕获通道调用)、Reduce 多敌均摊、TerritoryDecay 抽干、regen 恢复
/// (含驻军捕获强度×GarrisonRegenRate 加成、负 regen 衰向 gaia)、SetCapturePoints(克隆)。
/// <b>已移植</b>:<b>Capturable/* 科技修正</b>(RegenRate/GarrisonRegenRate use-site 惰性读;
/// CapturePoints 经 ValueModificationApplier.RescaleMaxCapturePoints 按比例缩放 CP 数组,
/// 镜像 Health:模板基值 BaseMaxCapturePoints 保幂等)。注:phase 科技是 GarrisonRegenRate
/// <b>add</b> 0.5/1.0(ship_capture_resistance 才是 CapturePoints ×1.4)。
/// <b>未移植</b>:StatisticsTracker 战报、Fogging.Activate 雾内隐藏、外部易主 CP 转移
/// (原版 OnOwnershipChanged 的 wololo 分支——我们的翻面在 RegisterCapturePointsChanged
/// 内闭环)、MT_CapturePointsChanged 消息(表现层自行轮询)。
/// 原版 1s 定时器;本移植由 SimBridge 每回合(0.1s)调 <see cref="TimerTick"/>,速率×dt 等价。
/// </summary>
[Component("Capturable", "Capturable")]
public sealed class CapturableComponent : ComponentBase, IComponentMessageHandler
{
    public const int MaxPlayers = LosGrid.MaxPlayers;

    public Fixed MaxCapturePoints;
    /// <summary>模板基值(修正值管线的输入,镜像 <see cref="HealthComponent.BaseMax"/>)。
    /// 0 = 未显式设置,回退用 <see cref="MaxCapturePoints"/>。Capturable/CapturePoints 科技
    /// 改变 max 时由 <see cref="ValueModificationApplier.RescaleMaxCapturePoints"/> 按比例缩放 CP 数组。</summary>
    public Fixed BaseMaxCapturePoints;
    /// <summary>修正值查询用的基值:BaseMaxCapturePoints &gt; 0 优先,否则 MaxCapturePoints
    /// (镜像 <see cref="HealthComponent.BaseMaxOrMax"/>)。</summary>
    public Fixed BaseMaxCapturePointsOrMax => BaseMaxCapturePoints > Fixed.Zero ? BaseMaxCapturePoints : MaxCapturePoints;
    public Fixed RegenRate;
    public Fixed GarrisonRegenRate;
    /// <summary>每玩家占领点(下标 0=gaia)。</summary>
    public Fixed[] CapturePoints = new Fixed[MaxPlayers + 1];

    /// <summary>装配后由 EntityAssembler 调一次(此时 OwnershipComponent 已挂):
    /// 首主 CP 拉满(原版在首个 OnOwnershipChanged 里做)。</summary>
    public void InitForOwner(int owner)
    {
        for (int i = 0; i < CapturePoints.Length; i++) CapturePoints[i] = Fixed.Zero;
        if (owner >= 0 && owner <= MaxPlayers) CapturePoints[owner] = MaxCapturePoints;
    }

    /// <summary>对齐原版 SetCapturePoints(克隆实体用):整体替换 CP 数组(拷贝语义)。
    /// 调用方保证总和与 max 一致。</summary>
    public void SetCapturePoints(Fixed[] points)
    {
        Array.Copy(points, CapturePoints, Math.Min(points.Length, CapturePoints.Length));
    }

    /// <summary>对齐原版 Capture:单位捕获入口(DelayedDamage 捕获通道调用)。
    /// captorOwner 无效或无可抽敌方 CP → 0;否则 Reduce 并返回实际抽中量。
    /// (captor 本体原版仅留给 loot TODO,逻辑不读。)</summary>
    public Fixed Capture(ComponentManager cm, Fixed amount, EntityId captor, int captorOwner)
    {
        if (captorOwner < 0 || !CanCapture(cm, captorOwner)) return Fixed.Zero;
        return Reduce(cm, amount, captorOwner);
    }

    /// <summary>对齐原版 CanCapture:playerId 视角下仍存在敌方 CP(才可(再)占领)。
    /// 敌方判定与 <see cref="Reduce"/> 完全同源(原版同用一个 diplomacy)。</summary>
    public bool CanCapture(ComponentManager cm, int playerId)
    {
        for (int i = 0; i <= MaxPlayers; i++)
            if (CapturePoints[i] > Fixed.Zero && IsEnemyForCapture(cm, playerId, i))
                return true;
        return false;
    }

    /// <summary>Capturable/RegenRate use-site 惰性读(同 Combat 的 Attack/Capture/Capture):
    /// 序列化的 RegenRate 为模板基值,科技加成恒新 + 幂等。</summary>
    private Fixed ApplyRegenRate(ComponentManager cm) =>
        Fixed.FromFloat(cm.Modifiers.Apply("Capturable/RegenRate", RegenRate.ToFloat(), Entity));

    /// <summary>对齐原版 GetRegenRate:base + Σ(驻军单位的 Capture 攻击强度 ×
    /// GarrisonRegenRate)。base 与 GarrisonRegenRate 均过修正值管线(use-site 惰性读,
    /// phase 科技 GarrisonRegenRate +0.5/+1.0 在此生效);强度亦过 Attack/Capture/Capture。
    /// 无 GarrisonHolder → 仅 base。</summary>
    public Fixed GetRegenRate(ComponentManager cm)
    {
        var regen = ApplyRegenRate(cm);
        var holder = cm.QueryInterface<GarrisonHolderComponent>(Entity);
        if (holder == null) return regen;
        var total = regen;
        Fixed garrisonMult = Fixed.FromFloat(
            cm.Modifiers.Apply("Capturable/GarrisonRegenRate", GarrisonRegenRate.ToFloat(), Entity));
        foreach (var e in holder.Entities)
        {
            var atk = cm.QueryInterface<AttackComponent>(e);
            if (atk == null || atk.CaptureStrength <= Fixed.Zero) continue;
            float strength = cm.Modifiers.Apply("Attack/Capture/Capture", atk.CaptureStrength.ToFloat(), e);
            total += Fixed.FromFloat(strength).Multiply(garrisonMult);
        }
        return total;
    }

    /// <summary>Reduce/CanCapture 共用的敌方判定(原版两侧同用 diplomacy.IsEnemy):
    /// gaia(0)恒为敌;自身非敌;无玩家实体/无外交组件 → 仅 gaia 算敌(保 P0 语义,
    /// 原版此外交缺失时 Reduce 直接返回 0,差异记录在案)。</summary>
    private static bool IsEnemyForCapture(ComponentManager cm, int playerId, int i)
    {
        if (i == 0) return true;
        if (i == playerId) return false;
        var pe = cm.Players.GetPlayerEntityId(playerId);
        if (!pe.HasValue) return false;
        var dip = cm.QueryInterface<DiplomacyComponent>(pe.Value);
        return dip != null && dip.IsEnemy(i);
    }

    /// <summary>对齐 TimerTick:decay 抽干(优先)→ regen 恢复;dt 为本回合秒数。</summary>
    public void TimerTick(ComponentManager cm, Fixed dt)
    {
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return;
        int owner = own.PlayerId;

        var decay = cm.QueryInterface<TerritoryDecayComponent>(Entity);
        if (decay != null && decay.Decaying)
        {
            // 原版:decay = min(rate, cp[owner]),从主人扣,按邻居占比分配(无邻居归 gaia)。
            Fixed drain = decay.GetDecayRate().Multiply(dt);
            if (drain > CapturePoints[owner]) drain = CapturePoints[owner];
            if (drain > Fixed.Zero)
            {
                CapturePoints[owner] -= drain;
                int total = 0;
                foreach (int n in decay.ConnectedNeighbours) total += n;
                if (total > 0)
                {
                    for (int p = 0; p <= MaxPlayers; p++)
                        if (decay.ConnectedNeighbours[p] > 0)
                            CapturePoints[p] += drain * decay.ConnectedNeighbours[p] / total;
                }
                else
                {
                    CapturePoints[0] += drain;
                }
                RegisterCapturePointsChanged(cm);
            }
        }

        // 对齐原版 TimerTick regen 段:GetRegenRate(含驻军加成);负 regen 衰向 gaia。
        Fixed regen = GetRegenRate(cm);
        if (regen < Fixed.Zero)
            Reduce(cm, (-regen).Multiply(dt), 0);
        else if (regen > Fixed.Zero)
            Reduce(cm, regen.Multiply(dt), owner);
    }

    /// <summary>对齐 Reduce:从 playerId 的所有敌方(含 gaia)均摊抽 amount,全数奖给
    /// playerId;返回实际抽取量。敌方判定见 <see cref="IsEnemyForCapture"/>。</summary>
    public Fixed Reduce(ComponentManager cm, Fixed amount, int playerId)
    {
        if (amount <= Fixed.Zero) return Fixed.Zero;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return Fixed.Zero;

        bool IsEnemyOf(int i) => IsEnemyForCapture(cm, playerId, i);

        // 原版均摊循环:每轮把剩余量平分给仍有 CP 的敌方,直至抽满或抽干。
        Fixed removed = Fixed.Zero;
        while (amount - removed > Fixed.FromFloat(0.0001f))
        {
            int enemies = 0;
            for (int i = 0; i <= MaxPlayers; i++)
                if (CapturePoints[i] > Fixed.Zero && IsEnemyOf(i)) enemies++;
            if (enemies == 0) break;
            Fixed share = (amount - removed) / Fixed.FromInt(enemies);
            // 定点截断保底:剩余 < 敌数时 share=0,全员"抽 0"会死循环(原版守卫是
            // float share>0.0001,无此问题)——尘埃量(<0.00024 CP)直接弃,远小于原版阈值。
            if (share <= Fixed.Zero) break;
            int survivors = 0;
            for (int i = 0; i <= MaxPlayers; i++)
            {
                if (CapturePoints[i] <= Fixed.Zero || !IsEnemyOf(i)) continue;
                if (CapturePoints[i] > share) { CapturePoints[i] -= share; removed += share; survivors++; }
                else { removed += CapturePoints[i]; CapturePoints[i] = Fixed.Zero; }
            }
            if (survivors == 0) break;
        }

        // 抽中量全给 playerId(原版:takenCapturePoints = max − 总和——同时自愈
        // decay 分配整除截断造成的总和漂移;超和不反扣)。返回值同原版=实际奖给量。
        Fixed sum = Fixed.Zero;
        for (int i = 0; i <= MaxPlayers; i++) sum += CapturePoints[i];
        Fixed award = MaxCapturePoints - sum;
        if (award < Fixed.Zero) award = Fixed.Zero;
        CapturePoints[playerId] += award;
        RegisterCapturePointsChanged(cm);
        return award;
    }

    /// <summary>对齐 RegisterCapturePointsChanged:主人 CP 归零 → 翻面给 CP 最多者
    /// (严格大于、下标升序 → 平手小编号/gaia 优先,与原版 reduce 同)。</summary>
    private void RegisterCapturePointsChanged(ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null) return;
        int owner = own.PlayerId;
        if (owner < 0 || CapturePoints[owner] > Fixed.Zero) return;

        int best = 0;
        for (int p = 1; p <= MaxPlayers; p++)
            if (CapturePoints[p] > CapturePoints[best]) best = p;
        own.PlayerId = best;
        cm.NotifyOwnerChanged(Entity, owner, best);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("max", MaxCapturePoints);
        s.NumberFixed("bmax", BaseMaxCapturePoints);
        s.NumberFixed("regen", RegenRate);
        s.NumberFixed("gregen", GarrisonRegenRate);
        for (int i = 0; i < CapturePoints.Length; i++)
            s.NumberFixed("cp", CapturePoints[i]);
    }

    public override void Deserialize(IDeserializer d)
    {
        MaxCapturePoints = d.NumberFixed("max");
        BaseMaxCapturePoints = d.NumberFixed("bmax");
        RegenRate = d.NumberFixed("regen");
        GarrisonRegenRate = d.NumberFixed("gregen");
        for (int i = 0; i < CapturePoints.Length; i++)
            CapturePoints[i] = d.NumberFixed("cp");
    }

    public void HandleMessage(IMessage message) { }
}
