using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 领土影响力(对齐原版 TerritoryInfluence —— 原版是模板数据,由 C++ TerritoryManager
/// 消费)。纯数据组件:<see cref="Radius"/>(米)/<see cref="Weight"/>(默认 1)/
/// <see cref="Root"/>(默认 false;root = 领土锚点,决定区域连通性)。
/// 模板无 &lt;TerritoryInfluence&gt; 节点即不装配(大多数单位无影响力)。
/// </summary>
[Component("TerritoryInfluence", "TerritoryInfluence")]
public sealed class TerritoryInfluenceComponent : ComponentBase, IComponentMessageHandler
{
    public Maths.Fixed Radius;
    public int Weight = 1;
    public bool Root;

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("radius", Radius);
        s.NumberI32("weight", Weight);
        s.Bool("root", Root);
    }

    public override void Deserialize(IDeserializer d)
    {
        Radius = d.NumberFixed("radius");
        Weight = d.NumberI32("weight");
        Root = d.Bool("root");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// 领土衰减(逐行对齐原版 TerritoryDecay.js):建筑处于其 &lt;Territory&gt; 列表允许的
/// 领土类型(neutral/enemy)或不连通飞地时进入 decaying,由 <see cref="CapturableComponent"/>
/// 按 <see cref="DecayRate"/>(占领点/秒)抽干 CP 并翻面。DecayRate=Infinity →
/// <see cref="TerritoryOwnership"/> 模式:实体归属直接跟随脚下领土(本数据无实例,
/// 字段与分支照原版保留)。原版事件驱动(OnTerritoriesChanged/OnPositionChanged/
/// OnDiplomacyChanged/OnOwnershipChanged);本移植由 SimBridge 每回合调
/// <see cref="Refresh"/>,回合边界取值与原版一致。
/// </summary>
[Component("TerritoryDecay", "TerritoryDecay")]
public sealed class TerritoryDecayComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>占领点/秒(模板值;TerritoryOwnership 模式下为 0)。</summary>
    public Maths.Fixed DecayRate;
    /// <summary>空格分隔的 decay 领土 token(neutral / enemy)。</summary>
    public string Territory = "";
    /// <summary>true = 归属跟随领土(模板 DecayRate=Infinity;本数据无实例)。</summary>
    public bool TerritoryOwnership;
    public bool Decaying;
    /// <summary>decaying 时有效:本区域边界外侧每玩家的(连通)相邻 cell 数,下标 0=gaia。</summary>
    public int[] ConnectedNeighbours = new int[LosGrid.MaxPlayers + 1];

    private static bool Has(string tokens, string token)
    {
        foreach (var t in tokens.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            if (t == token) return true;
        return false;
    }

    private static bool IsMutualAlly(ComponentManager cm, int self, int other) =>
        self == other || cm.Players.GetMutualAllies(self).Contains(other);

    /// <summary>逐行对齐 TerritoryDecay.js IsConnected;顺带维护 connectedNeighbours 与
    /// blink 覆盖(原版同函数的副作用)。</summary>
    public bool IsConnected(ComponentManager cm, TerritoryManager tm)
    {
        ArrayClear(ConnectedNeighbours);

        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos == null) return false;

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return true;   // 无主实体不会 decay
        int playerId = own.PlayerId;

        DiplomacyComponent? dip = null;
        if (playerId > 0)
        {
            var pe = cm.Players.GetPlayerEntityId(playerId);
            if (pe.HasValue) dip = cm.QueryInterface<DiplomacyComponent>(pe.Value);
            if (dip == null) return true;                   // 无外交组件 → 不 decay(原版同)
        }

        var x = pos.Position.X; var z = pos.Position.Z;
        int tileOwner = tm.GetOwner(x, z);
        if (tileOwner == 0)
        {
            ConnectedNeighbours[0] = 1;
            return playerId == 0 || !Has(Territory, "neutral");
        }

        bool tileConnected = tm.IsConnected(x, z);
        if (tileConnected && !IsMutualAlly(cm, playerId, tileOwner))
        {
            ConnectedNeighbours[tileOwner] = 1;
            return !Has(Territory, "enemy");
        }
        if (tileConnected) return true;

        // 未连通且非自家领土:向 gaia 衰(原版 #4749 特例)。
        if (playerId != tileOwner)
        {
            ConnectedNeighbours[0] = 1;
            return false;
        }

        // 自家飞地:边界邻着互盟的连通领土 → 不 decay 且灭 blink;否则 decay + blink。
        ConnectedNeighbours = tm.GetNeighbours(x, z, true);
        for (int i = 1; i <= LosGrid.MaxPlayers; i++)
            if (ConnectedNeighbours[i] > 0 && IsMutualAlly(cm, playerId, i))
            {
                tm.SetTerritoryBlinking(x, z, false);
                return true;
            }
        tm.SetTerritoryBlinking(x, z, true);
        return false;
    }

    /// <summary>对齐 GetDecayRate(原版经 ApplyValueModificationsToEntity;修正值管线对
    /// TerritoryDecay 路径暂无数据驱动实例,直接返回模板值——记为已知分叉)。</summary>
    public Maths.Fixed GetDecayRate() => DecayRate;

    /// <summary>对齐 UpdateDecayState:decaying = !IsConnected() && DecayRate > 0。</summary>
    public void UpdateDecayState(ComponentManager cm, TerritoryManager tm)
    {
        Decaying = !IsConnected(cm, tm) && DecayRate > Maths.Fixed.Zero;
    }

    /// <summary>TerritoryOwnership 模式(对齐 UpdateOwner):归属跟随脚下领土。</summary>
    public void UpdateOwner(ComponentManager cm, TerritoryManager tm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (own == null || pos == null) return;
        int tileOwner = tm.GetOwner(pos.Position.X, pos.Position.Z);
        if (tileOwner != own.PlayerId)
        {
            int from = own.PlayerId;
            own.PlayerId = tileOwner;
            cm.NotifyOwnerChanged(Entity, from, tileOwner);
        }
    }

    /// <summary>每回合刷新(替代原版事件订阅)。</summary>
    public void Refresh(ComponentManager cm, TerritoryManager tm)
    {
        if (TerritoryOwnership) UpdateOwner(cm, tm);
        else UpdateDecayState(cm, tm);
    }

    private static void ArrayClear(int[] a) { for (int i = 0; i < a.Length; i++) a[i] = 0; }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("rate", DecayRate);
        s.StringASCII("terr", Territory);
        s.Bool("town", TerritoryOwnership);
        s.Bool("decay", Decaying);
        for (int i = 0; i < ConnectedNeighbours.Length; i++)
            s.NumberI32("cn", ConnectedNeighbours[i]);
    }

    public override void Deserialize(IDeserializer d)
    {
        DecayRate = d.NumberFixed("rate");
        Territory = d.StringASCII("terr");
        TerritoryOwnership = d.Bool("town");
        Decaying = d.Bool("decay");
        for (int i = 0; i < ConnectedNeighbours.Length; i++)
            ConnectedNeighbours[i] = d.NumberI32("cn");
    }

    public void HandleMessage(IMessage message) { }
}
