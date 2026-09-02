using System.Linq;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Per-entity fog-of-war memory, ported from Fogging.js. Tracks, per player, whether the
/// entity has ever been seen (seen), and whether a mirage currently replaces it (miraged).
/// Masks are per-player bits (players 1..16) serialized as u32.
///
/// Lifecycle (mirrors Fogging.js):
/// - VISIBLE → mark seen, clear miraged (the mirage entity is kept hidden for reuse).
/// - FOGGED (when activated) → LoadMirage: spawn or refresh the frozen stand-in.
/// - Ownership to a real player → Activate; on first activation, load mirages for
///   players who already saw the entity but can't see it now (Fogging.js Activate).
/// - Ownership to none (death/capture) → hidden mirages are destroyed, fogged ones
///   are orphaned (they self-destruct when their tile is next scouted).
/// </summary>
[Component("Fogging", "Fogging")]
public sealed class FoggingComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>Activated once the entity has a real owner (mirrors Fogging.js OnOwnershipChanged).</summary>
    public bool Activated;
    public uint SeenMask;
    public uint MiragedMask;
    /// <summary>player → mirage entity standing in for this one.</summary>
    public EntityId?[] MirageOf = new EntityId?[LosGrid.MaxPlayers + 1];
    /// <summary>Parent template name, needed to build the mirage's visuals (Task 8).</summary>
    public string TemplateName = "";

    public bool WasSeen(int player) => (SeenMask & Bit(player)) != 0;
    public bool IsMiraged(int player) => (MiragedMask & Bit(player)) != 0;

    internal static uint Bit(int player) => 1u << (player - 1);

    /// <summary>Fogging.js Activate(): on first activation, load a mirage for every
    /// player who has seen this entity but currently cannot see it.</summary>
    public void Activate(ComponentManager cm, RangeManager rm)
    {
        if (Activated) return;
        Activated = true;
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            if (WasSeen(p) && rm.GetLosVisibility(Entity, p) != LosVisibility.Visible)
                LoadMirage(p, cm, rm);
    }

    /// <summary>Fogging.js OnVisibilityChanged: VISIBLE marks seen + clears miraged;
    /// FOGGED (activated only) loads the mirage.</summary>
    public void OnVisibilityChanged(int player, LosVisibility vis, ComponentManager cm, RangeManager rm)
    {
        if (player < 1 || player > LosGrid.MaxPlayers) return;
        if (vis == LosVisibility.Visible)
        {
            // 镜像退场 → 贸易商切回真市场(原版 mirage swap-back)。
            if ((MiragedMask & Bit(player)) != 0 && MirageOf[player] is { } mirageId)
                NotifyMirageUnloaded(player, cm, mirageId);
            MiragedMask &= ~Bit(player);
            SeenMask |= Bit(player);
        }
        else if (vis == LosVisibility.Fogged && Activated)
        {
            LoadMirage(player, cm, rm);
        }
    }

    /// <summary>Fogging.js LoadMirage: mark miraged, spawn the stand-in on first use
    /// or refresh the frozen data on reuse, then ask for a visibility re-eval of the
    /// parent (it must flip to HIDDEN now that a mirage replaces it).</summary>
    public void LoadMirage(int player, ComponentManager cm, RangeManager rm)
    {
        MiragedMask |= Bit(player);
        MirageOf[player] ??= EntityAssembler.SpawnMirage(cm, rm, Entity, player, TemplateName);
        EntityAssembler.RefreshMirageData(cm, Entity, MirageOf[player]!.Value);
        // 原版 Mirage.js 语义:市场入雾 → 该玩家路由含真市场的贸易商切到镜像
        // (Trader.SwitchMarket + UnitAI.SwitchMarketOrder 改订单目标)。
        var market = cm.QueryInterface<MarketComponent>(Entity);
        if (market != null)
            foreach (var e in cm.AllEntities)
            {
                var trader = cm.QueryInterface<TraderComponent>(e);
                if (trader == null) continue;
                var own = cm.QueryInterface<OwnershipComponent>(e);
                if (own?.PlayerId != player) continue;
                if (trader.HasMarket(Entity))
                    trader.SwitchMarket(cm, Entity, MirageOf[player]!.Value);
            }
        rm.RequestVisibilityUpdate(Entity);
    }

    /// <summary>镜像退场(重现/销毁):贸易商从镜像切回真市场。</summary>
    public void NotifyMirageUnloaded(int player, ComponentManager cm, EntityId mirageId)
    {
        var market = cm.QueryInterface<MarketComponent>(Entity);
        if (market == null) return;
        foreach (var e in cm.AllEntities)
        {
            var trader = cm.QueryInterface<TraderComponent>(e);
            if (trader == null) continue;
            var own = cm.QueryInterface<OwnershipComponent>(e);
            if (own?.PlayerId != player) continue;
            if (trader.HasMarket(mirageId))
                trader.SwitchMarket(cm, mirageId, Entity);
        }
    }

    /// <summary>Fogging.js OnOwnershipChanged: gaining a real owner activates fogging;
    /// losing the owner (death/capture) destroys hidden mirages and orphans fogged
    /// ones (they self-destruct via MirageComponent when next scouted).</summary>
    public void OnOwnershipChanged(int from, int to, ComponentManager cm, RangeManager rm)
    {
        if (to > 0)
            Activate(cm, rm);
        if (to != -1)
            return;
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
        {
            var mirageId = MirageOf[p];
            if (mirageId == null) continue;
            if (rm.GetLosVisibility(mirageId.Value, p) == LosVisibility.Hidden)
            {
                cm.DestroyEntity(mirageId.Value);
                MirageOf[p] = null;
                MiragedMask &= ~Bit(p);
            }
            else
            {
                var mirage = cm.QueryInterface<MirageComponent>(mirageId.Value);
                if (mirage != null)
                    mirage.Parent = default;
            }
        }
    }

    protected override void OnInit()
    {
        Activated = false;
        SeenMask = 0;
        MiragedMask = 0;
        MirageOf = new EntityId?[LosGrid.MaxPlayers + 1];
        TemplateName = "";
    }

    public override void Serialize(ISerializer s)
    {
        s.Bool("act", Activated);
        s.NumberU32("seen", SeenMask);
        s.NumberU32("mird", MiragedMask);
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            s.NumberU32("mid", MirageOf[p]?.Value ?? 0);
        s.StringASCII("tmpl", TemplateName);
    }

    public override void Deserialize(IDeserializer d)
    {
        Activated = d.Bool("act");
        SeenMask = d.NumberU32("seen");
        MiragedMask = d.NumberU32("mird");
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
        {
            uint v = d.NumberU32("mid");
            MirageOf[p] = v == 0 ? null : new EntityId(v);
        }
        TemplateName = d.StringASCII("tmpl");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Marks an entity as a mirage: a frozen stand-in for <see cref="Parent"/> in one player's
/// fog, ported from Mirage.js. Holds last-seen data for GUI queries (health bars, resource
/// amounts). The visibility interlock lives in RangeManager.ComputeLosVisibility (a mirage
/// is HIDDEN while its tile is visible, FOGGED otherwise, and only ever for its player).
/// </summary>
[Component("Mirage", "Mirage")]
public sealed class MirageComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>The real entity this mirage stands in for; default = orphaned
    /// (parent destroyed — the mirage self-destructs when its tile is next visible).</summary>
    public EntityId Parent;
    /// <summary>The one player this mirage is visible to.</summary>
    public int Player;

    // Last-seen data for GUI queries (health bars, resource amounts).
    public int FrozenHealthCurrent;
    public int FrozenHealthMax;
    public int FrozenResourceAmount = -1; // -1 = not a resource
    /// <summary>父是市场时的交易类型快照(land/naval;原版 mirage 带 Market 件,
    /// 迷雾中贸易商照常交易)。空 = 非市场。</summary>
    public string FrozenMarketTypes = "";
    public bool HasMarketType(string type) =>
        FrozenMarketTypes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .Contains(type);

    /// <summary>Mirage.js OnVisibilityChanged: going HIDDEN for our player means the
    /// real entity is back in sight — notify the swap-back, or self-destruct when orphaned.</summary>
    public void OnVisibilityChanged(int player, LosVisibility vis, ComponentManager cm)
    {
        if (player != Player || vis != LosVisibility.Hidden) return;
        if (Parent.Value == 0)
            cm.DestroyEntity(Entity);
        else
            cm.Events.RaiseMirageSwapBack(new Events.MirageSwapBackEvent
            {
                Mirage = Entity,
                Parent = Parent,
                Player = Player
            });
    }

    protected override void OnInit()
    {
        Parent = default;
        Player = 0;
        FrozenHealthCurrent = 0;
        FrozenHealthMax = 0;
        FrozenResourceAmount = -1;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("parent", Parent.Value);
        s.NumberI32("player", Player);
        s.NumberI32("fhc", FrozenHealthCurrent);
        s.NumberI32("fhm", FrozenHealthMax);
        s.NumberI32("fra", FrozenResourceAmount);
    }

    public override void Deserialize(IDeserializer d)
    {
        Parent = new EntityId(d.NumberU32("parent"));
        Player = d.NumberI32("player");
        FrozenHealthCurrent = d.NumberI32("fhc");
        FrozenHealthMax = d.NumberI32("fhm");
        FrozenResourceAmount = d.NumberI32("fra");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Fog-of-war display flags, ported from the original's Visibility component
/// (template_structure.xml / template_unit.xml: &lt;Visibility&gt;&lt;RetainInFog&gt;).
/// RetainInFog=true (structures, gaia) keeps the entity standing in explored fog;
/// false (units) hides it. The scripted-visibility part of the original component
/// (per-player overrides) is deliberately not ported.
/// </summary>
[Component("Visibility", "Visibility")]
public sealed class VisibilityComponent : ComponentBase, IComponentMessageHandler
{
    public bool RetainInFog;

    protected override void OnInit() => RetainInFog = false;

    public override void Serialize(ISerializer s) => s.Bool("retain", RetainInFog);

    public override void Deserialize(IDeserializer d) => RetainInFog = d.Bool("retain");

    public void HandleMessage(IMessage message) { }
}
