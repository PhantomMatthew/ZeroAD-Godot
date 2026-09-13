using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// VisionSharing — port of the original's VisionSharing.js (bribe/spy vision sharing +
/// garrison vision sharing). One component per entity whose template carries a
/// &lt;VisionSharing&gt; element (template_unit/template_structure included, so garrison
/// vision sharing applies broadly; only traders/merchant ships are Bribable).
///
/// Shared vision set (mirrors the upstream `shared` Set): the owner (player &gt; 0),
/// plus the owners (&gt; 0) of foreign garrisoned passengers (GarrisonHolder), plus spy
/// players. Diffs against the previous set are pushed to the <see cref="RangeManager"/>
/// via <see cref="RangeManager.SetExtraSeers"/> so the entity's vision circle is counted
/// in those players' LOS grids (the turn-based equivalent of upstream MT_VisionSharingChanged;
/// upstream recomputes on OnGarrisonedUnitsChanged/OnOwnershipChanged messages, we recompute
/// once per sim turn from <see cref="TickAll"/> — 10 Hz granularity, deterministic).
///
/// Spy lifecycle (AddSpy): guards mirror upstream — bribable template, not self-owned,
/// requester's TechnologyManager meets the "special/spy" template requirements
/// (unlock_spies), then the bribe cost is charged atomically (IncurBribeCost). Duration
/// comes from the special/spy template (VisionSharing/Duration through the modifiers
/// pipeline for the requester), scaled × 60/max(30, bribed entity's effective vision
/// range); stored as integer countdown ticks (10 ticks/s; -1 = permanent when the
/// template gives no duration). No float/double persists in sim state — only the
/// duration computation uses float, same precedent as BarterMultiplier.
/// </summary>
[Component("VisionSharing", "VisionSharing")]
public sealed class VisionSharingComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>一条间谍登记(原版 spies Map 的 value 为玩家号;到期计时是本移植的
    /// 回合制等价——原版用 Timer.SetTimeout(duration*1000) 回调 RemoveSpy)。</summary>
    public sealed class SpyEntry
    {
        public int Player;
        /// <summary>剩余回合数(10 回合/秒);-1 = 永久(模板无 Duration)。</summary>
        public int RemainingTicks;
    }

    /// <summary>模板 VisionSharing/Bribable(挂载/补挂时从模板写入;序列化随档——
    /// v22+ 冷加载不再重取模板,见 RegisterForLos 的补挂注释)。</summary>
    public bool Bribable;

    /// <summary>间谍表:间谍 id → 登记(原版 this.spies,Map;间谍 id 即 ++spyId)。</summary>
    public readonly Dictionary<int, SpyEntry> Spies = new();
    private int _nextSpyId;

    /// <summary>当前共享玩家位集(bit p-1 = 玩家 p,1..16;含属主位——对齐原版 shared Set)。</summary>
    public uint SharedMask;

    /// <summary>组件已激活(原版 this.activated):首个间谍或首个异主驻军乘员时置位,
    /// 一旦置位不再回退(原版同)。未激活时 ShareVisionWith 退化为"属主即共享"。</summary>
    public bool Activated;

    private static uint PlayerBit(int player) =>
        player >= 1 && player <= LosGrid.MaxPlayers ? 1u << (player - 1) : 0u;

    /// <summary>该实体是否与 <paramref name="player"/> 共享视野(原版 ShareVisionWith:
    /// 激活后查共享集,未激活退化为属主判定;互盟不算——原版同,盟友共享走另一条路)。</summary>
    public bool ShareVisionWith(ComponentManager cm, int player)
    {
        if (Activated)
            return (SharedMask & PlayerBit(player)) != 0;
        return (cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1) == player;
    }

    /// <summary>原版 Activate:激活门槛 = 有真实属主(玩家号 &gt; 0)。幂等。</summary>
    private void Activate(ComponentManager cm)
    {
        if (Activated) return;
        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (owner <= 0) return;
        Activated = true;
        SharedMask = PlayerBit(owner);
    }

    /// <summary>
    /// 重算共享集合并把增量推给 RangeManager(原版 CheckVisionSharings +
    /// MT_VisionSharingChanged 的 add/remove 广播)。集合构成:属主(&gt;0)∪ 异主驻军
    /// 乘员属主 ∪ 间谍玩家(后两者排除属主);属主越界(-1 无属主)时全空(原版 owner&lt;0
    /// 时 shared 为空集)。推给 RangeManager 的是"属主之外的额外见证者"位集——属主与
    /// 互盟的视野圈由 RangeManager 自身的 OwnerAllyMask 维护,两边在 SeerMask 求并,
    /// 间谍与盟友身份重叠时不会重复计数也不会误摘。
    /// </summary>
    public void CheckVisionSharings(ComponentManager cm, RangeManager rm)
    {
        uint desired = 0;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        int owner = own?.PlayerId ?? -1;
        if (owner >= 0)
        {
            if (owner > 0)
                desired |= PlayerBit(owner);

            // 驻军乘员带来的共享(原版 OnGarrisonedUnitsChanged → CheckVisionSharings)。
            var garrison = cm.QueryInterface<GarrisonHolderComponent>(Entity);
            if (garrison != null)
            {
                foreach (var ent in garrison.Entities)
                {
                    int entOwner = cm.QueryInterface<OwnershipComponent>(ent)?.PlayerId ?? -1;
                    if (entOwner > 0 && entOwner != owner)
                    {
                        desired |= PlayerBit(entOwner);
                        // 原版:异主乘员触发懒激活。
                        Activate(cm);
                    }
                }
            }

            // 间谍带来的共享。
            foreach (var spy in Spies.Values)
                if (spy.Player > 0 && spy.Player != owner)
                    desired |= PlayerBit(spy.Player);
        }

        if (!Activated)
            return;

        SharedMask = desired;
        // 额外见证者 = 共享集去掉属主位(属主圈 RangeManager 自理)。
        rm.SetExtraSeers(Entity, desired & ~PlayerBit(owner));
    }

    /// <summary>
    /// 贿赂本实体(原版 AddSpy)。守卫顺序与原版一致:可贿赂 → 非己方/玩家合法 →
    /// 请求者科技门(CanProduce("special/spy") → 模板 Identity/Requirements/Techs)→
    /// 贿赂扣费(须成功)。时长:special/spy 模板的 VisionSharing/Duration 经请求者的
    /// 修正值管线,再 × 60/max(30, 本实体有效视野)缩放;模板无 Duration → 永久。
    /// 返回间谍 id(0 = 拒绝)。
    /// </summary>
    public int AddSpy(ComponentManager cm, RangeManager rm, int player)
    {
        if (!Bribable) return 0;
        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (owner == player || player <= 0) return 0;

        var playerEntity = cm.GetPlayerEntityId(player);
        if (!playerEntity.HasValue) return 0;
        var techMgr = cm.QueryInterface<TechnologyManager>(playerEntity.Value);
        if (techMgr == null) return 0;

        TemplateStats? spyStats = null;
        try { spyStats = cm.Templates?.ExtractStats("special/spy"); } catch { }
        if (spyStats == null) return 0;
        // 原版 TechnologyManager.CanProduce("special/spy"):模板 Identity/Requirements/Techs
        // 全满足才可产(此处 = unlock_spies;RequirementsHelper 的 Techs 分支语义)。
        if (!techMgr.MeetsRequirements(spyStats.RequiredTechs)) return 0;
        if (!IncurBribeCost(cm, player, owner, failedBribe: false)) return 0;

        // 时长(原版:未显式给时取 special/spy 的 VisionSharing/Duration,经
        // ApplyValueModificationsToTemplate 后按被贿赂单位视野缩放;都无 → 永久)。
        int remainingTicks = -1;
        if (spyStats.VisionSharingDuration > 0f)
        {
            float duration = cm.Modifiers.ApplyTemplate("VisionSharing/Duration",
                spyStats.VisionSharingDuration, spyStats.GetClassList(), playerEntity.Value);
            var vis = cm.QueryInterface<VisionComponent>(Entity);
            if (vis != null)
                duration *= 60f / Math.Max(30f,
                    ValueModificationApplier.EffectiveVisionRange(cm, Entity, vis).ToFloat());
            // 回合制倒计时(固定 10 回合/秒;原版 duration*1000ms 的 SetTimeout)。
            remainingTicks = Math.Max(1, (int)Math.Ceiling(duration * 10f));
        }

        int id = ++_nextSpyId;
        Spies[id] = new SpyEntry { Player = player, RemainingTicks = remainingTicks };
        Activate(cm);
        CheckVisionSharings(cm, rm);

        // 成功贿赂计数(原版 IncreaseSuccessfulBribesCounter;直查请求者的统计组件)。
        cm.QueryInterface<StatisticsTrackerComponent>(playerEntity.Value)
            ?.IncreaseSuccessfulBribesCounter();
        return id;
    }

    /// <summary>移除间谍(原版 RemoveSpy;到期/手动)。</summary>
    public void RemoveSpy(ComponentManager cm, RangeManager rm, int id)
    {
        Spies.Remove(id);
        CheckVisionSharings(cm, rm);
    }

    /// <summary>每回合推进(回合制替代原版 Timer):倒计时到期的间谍移除;
    /// 已激活实体顺带重算共享(吸收驻军/易主变化);未激活但舱内有乘员的实体
    /// 也需一次 CheckVisionSharings 来完成懒激活(原版靠 OnGarrisonedUnitsChanged
    /// 消息驱动,此处回合粒度轮询等价)。</summary>
    public void Tick(ComponentManager cm, RangeManager rm)
    {
        if (Spies.Count > 0)
        {
            List<int>? expired = null;
            foreach (var (id, entry) in Spies)
            {
                if (entry.RemainingTicks < 0) continue;
                entry.RemainingTicks--;
                if (entry.RemainingTicks <= 0)
                    (expired ??= new List<int>()).Add(id);
            }
            if (expired != null)
            {
                expired.Sort();   // 定序移除(确定性)
                foreach (var id in expired)
                    Spies.Remove(id);
            }
        }
        if (Activated
            || (cm.QueryInterface<GarrisonHolderComponent>(Entity)?.Entities.Count ?? 0) > 0)
            CheckVisionSharings(cm, rm);
    }

    /// <summary>每回合驱动全部 VisionSharing 组件(SimBridge 在 LOS 重算前调用,
    /// 使到期/驻军变化当回合即反映到 LOS 网格)。迭代 cm.AllEntities 的插入序——
    /// 跨端确定。跳过条件:未激活 + 无间谍 + 无驻军乘员(三者皆无 = 共享集不可能
    /// 变化;驻军乘员是懒激活的唯一发现途径——原版靠 OnGarrisonedUnitsChanged 消息
    /// 即时重算,此处以回合粒度轮询等价)。</summary>
    public static void TickAll(ComponentManager cm, RangeManager rm)
    {
        foreach (var ent in cm.AllEntities)
        {
            var vs = cm.QueryInterface<VisionSharingComponent>(ent);
            if (vs == null) continue;
            if (!vs.Activated && vs.Spies.Count == 0
                && (cm.QueryInterface<GarrisonHolderComponent>(ent)?.Entities.Count ?? 0) == 0)
                continue;
            vs.Tick(cm, rm);
        }
    }

    /// <summary>
    /// 贿赂扣费(原版 Commands.js IncurBribeCost):乘数 = 被贿赂方玩家的
    /// SpyCostMultiplier(查询时合成,含 spy_counter 等科技修正);失败贿赂再乘
    /// special/spy 模板的 FailureCostRatio。每资源 floor(乘数 × 修正后模板成本),
    /// 请求者 TrySubtractResources 语义——全有或全无(不够则分文不扣)。
    /// </summary>
    public static bool IncurBribeCost(ComponentManager cm, int requester, int bribedPlayer, bool failedBribe)
    {
        var bribed = cm.GetPlayerEntity(bribedPlayer);
        var requesterPc = cm.GetPlayerEntity(requester);
        if (bribed == null || requesterPc == null) return false;
        var requesterEntity = cm.GetPlayerEntityId(requester);
        if (!requesterEntity.HasValue) return false;

        TemplateStats? spyStats = null;
        try { spyStats = cm.Templates?.ExtractStats("special/spy"); } catch { }
        if (spyStats == null) return false;

        float multiplier = bribed.GetSpyCostMultiplier(cm);
        if (failedBribe)
            multiplier *= spyStats.VisionSharingFailureCostRatio;

        var classes = spyStats.GetClassList();
        int food = (int)Math.Floor(multiplier * cm.Modifiers.ApplyTemplate(
            "Cost/Resources/food", spyStats.FoodCost, classes, requesterEntity.Value));
        int wood = (int)Math.Floor(multiplier * cm.Modifiers.ApplyTemplate(
            "Cost/Resources/wood", spyStats.WoodCost, classes, requesterEntity.Value));
        int stone = (int)Math.Floor(multiplier * cm.Modifiers.ApplyTemplate(
            "Cost/Resources/stone", spyStats.StoneCost, classes, requesterEntity.Value));
        int metal = (int)Math.Floor(multiplier * cm.Modifiers.ApplyTemplate(
            "Cost/Resources/metal", spyStats.MetalCost, classes, requesterEntity.Value));

        if (!requesterPc.CanAfford(wood, food, stone, metal)) return false;
        requesterPc.Spend(wood, food, stone, metal);
        return true;
    }

    public override void Serialize(ISerializer s)
    {
        s.Bool("bribable", Bribable);
        s.Bool("activated", Activated);
        s.NumberU32("sharedMask", SharedMask);
        s.NumberI32("nextSpyId", _nextSpyId);
        s.NumberI32("spyCount", Spies.Count);
        foreach (var (id, entry) in Spies.OrderBy(kv => kv.Key))
        {
            s.NumberI32("spyId", id);
            s.NumberI32("spyPlayer", entry.Player);
            s.NumberI32("spyTicks", entry.RemainingTicks);
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        Bribable = d.Bool("bribable");
        Activated = d.Bool("activated");
        SharedMask = d.NumberU32("sharedMask");
        _nextSpyId = d.NumberI32("nextSpyId");
        Spies.Clear();
        int n = d.NumberI32("spyCount");
        for (int i = 0; i < n; i++)
        {
            int id = d.NumberI32("spyId");
            Spies[id] = new SpyEntry
            {
                Player = d.NumberI32("spyPlayer"),
                RemainingTicks = d.NumberI32("spyTicks"),
            };
        }
        // 注意:不在此触碰 RangeManager——反序列化在组件流中段,LOS 索引尚未重建;
        // 额外见证者位由 RangeManager.RefreshFromComponents 在 Repopulate 时回本。
    }

    public void HandleMessage(IMessage message) { }
}
