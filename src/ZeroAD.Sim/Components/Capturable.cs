using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 可占领(对齐原版 Capturable.js 的 decay/regen 闭环):每玩家一份占领点(下标 0=gaia,
/// 总和 ≤ MaxCapturePoints),主人那份归零 → 翻面给 CP 最多的玩家。
/// 本移植范围 = TerritoryDecay 下游:TimerTick 的 decay 抽干(按 ConnectedNeighbours
/// 比例分给邻主,无邻居归 gaia)+ regen 恢复(Reduce 均摊到敌方)。<b>未移植</b>:
/// 单位捕获 Attack"Capture" 特效(Capture()/CanCapture()/驻军加成)、StatisticsTracker
/// 战报、Fogging.Activate 雾内隐藏、MT_CapturePointsChanged 消息(表现层自行轮询)。
/// 原版 1s 定时器;本移植由 SimBridge 每回合(0.1s)调 <see cref="TimerTick"/>,速率×dt 等价。
/// </summary>
[Component("Capturable", "Capturable")]
public sealed class CapturableComponent : ComponentBase, IComponentMessageHandler
{
    public const int MaxPlayers = LosGrid.MaxPlayers;

    public Fixed MaxCapturePoints;
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

        Fixed regen = RegenRate;
        if (regen > Fixed.Zero)
            Reduce(cm, regen.Multiply(dt), owner);
    }

    /// <summary>对齐 Reduce:从 playerId 的所有敌方(含 gaia)均摊抽 amount,全数奖给
    /// playerId;返回实际抽取量。敌方判定:DiplomacyComponent(gaia 恒为敌,对齐原版)。</summary>
    public Fixed Reduce(ComponentManager cm, Fixed amount, int playerId)
    {
        if (amount <= Fixed.Zero) return Fixed.Zero;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return Fixed.Zero;

        DiplomacyComponent? dip = null;
        var pe = cm.Players.GetPlayerEntityId(playerId);
        if (pe.HasValue) dip = cm.QueryInterface<DiplomacyComponent>(pe.Value);
        bool IsEnemyOf(int i) => i == 0 || (i != playerId && dip != null && dip.IsEnemy(i));

        // 原版均摊循环:每轮把剩余量平分给仍有 CP 的敌方,直至抽满或抽干。
        Fixed removed = Fixed.Zero;
        while (amount - removed > Fixed.FromFloat(0.0001f))
        {
            int enemies = 0;
            for (int i = 0; i <= MaxPlayers; i++)
                if (CapturePoints[i] > Fixed.Zero && IsEnemyOf(i)) enemies++;
            if (enemies == 0) break;
            Fixed share = (amount - removed) / Fixed.FromInt(enemies);
            int survivors = 0;
            for (int i = 0; i <= MaxPlayers; i++)
            {
                if (CapturePoints[i] <= Fixed.Zero || !IsEnemyOf(i)) continue;
                if (CapturePoints[i] > share) { CapturePoints[i] -= share; removed += share; survivors++; }
                else { removed += CapturePoints[i]; CapturePoints[i] = Fixed.Zero; }
            }
            if (survivors == 0) break;
        }

        // 抽出量全给 playerId(原版:takenCapturePoints = max − 总和,加给 playerID)。
        CapturePoints[playerId] += removed;
        RegisterCapturePointsChanged(cm);
        return removed;
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
        s.NumberFixed("regen", RegenRate);
        s.NumberFixed("gregen", GarrisonRegenRate);
        for (int i = 0; i < CapturePoints.Length; i++)
            s.NumberFixed("cp", CapturePoints[i]);
    }

    public override void Deserialize(IDeserializer d)
    {
        MaxCapturePoints = d.NumberFixed("max");
        RegenRate = d.NumberFixed("regen");
        GarrisonRegenRate = d.NumberFixed("gregen");
        for (int i = 0; i < CapturePoints.Length; i++)
            CapturePoints[i] = d.NumberFixed("cp");
    }

    public void HandleMessage(IMessage message) { }
}
