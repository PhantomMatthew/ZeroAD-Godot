using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// GarrisonHolder 已迁至 Garrison.cs(MS5:GarrisonHolder.js 行为件,替换 P0 计数版死代码)。

// MarketComponent 已并入 Trader.cs(MS5:Trader.js/Market.js 行为 + 保留 P0 barter 字段)。

[Component("RallyPoint", "RallyPoint")]
public sealed class RallyPointComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>集结点指令数据(原版 RallyPoint data 项;gui/input.js getActionInfo 产物)。
    /// command ∈ walk/gather/gather-near-position/repair/build/garrison/occupy-turret/
    /// attack/attack-walk/patrol/collect-treasure/trade。</summary>
    public sealed class RallyPointData
    {
        public string Command = "walk";
        public uint Target;          // 0 = 无(纯位置点)
        public uint Source;          // trade 起点市场
        public string ResourceType = "";   // gather-near-position 的 resourceType(specific)
        public bool AllowCapture;
        /// <summary>attack-walk/patrol 的目标类别过滤(原版 data.targetClasses;
        /// 空 = 不过滤)。存档 v19 起序列化。</summary>
        public string? TargetClasses;
        public RallyPointData Clone() => (RallyPointData)MemberwiseClone();
    }

    /// <summary>单玩家集结队列(原版 perPlayer[player] = { pos: [], data: [] })。</summary>
    public sealed class RallyPointEntry
    {
        public readonly List<Maths.FixedVector2D> Pos = new();
        public readonly List<RallyPointData> Data = new();
    }

    /// <summary>每玩家集结队列(原版 perPlayer)。</summary>
    public readonly Dictionary<int, RallyPointEntry> PerPlayer = new();

    protected override void OnInit()
    {
        // 事件订阅(Guard 同款 SimSystem.Sim 懒接):易主清空 + 换名改指。
        var cm = SimSystem.Sim;
        if (cm == null || _subscribedCm != null) return;
        _subscribedCm = cm;
        cm.OwnerChanged += OnAnyOwnershipChanged;
        cm.Events.EntityRenamed += OnEntityRenamed;
    }

    protected override void OnDeinit()
    {
        if (_subscribedCm == null) return;
        _subscribedCm.OwnerChanged -= OnAnyOwnershipChanged;
        // EntityRenamed 不退订:原版 MT_EntityRenamed 在 DestroyEntity 之前广播
        // (Transform.js:180/208),本移植的 Promotion.Promote 先毁后广播——退订会让
        // "建筑自身换名迁移队列"永远收不到。留存代价是已毁建筑的空队列闭包(有界)。
        _subscribedCm = null;
    }

    private ComponentManager? _subscribedCm;

    /// <summary>原版 OnOwnershipChanged:易主清空集结队列;构造/析构(from/to ==
    /// INVALID_PLAYER = -1)豁免(RallyPoint.js:149-156)。</summary>
    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        if (entity != Entity) return;
        if (from == -1 || to == -1) return;
        OnOwnershipChanged();
    }

    /// <summary>原版 OnGlobalEntityRenamed(RallyPoint.js:122-147):全队列 Data 的
    /// Target/Source 命中旧号 → 改指新号;改名的是建筑自身且新实体带 RallyPoint →
    /// 整条队列逐点迁移到新实体。</summary>
    private void OnEntityRenamed(Events.EntityRenamedEvent e)
    {
        var cm = _subscribedCm ?? SimSystem.Sim;
        foreach (var entry in PerPlayer.Values)
            foreach (var d in entry.Data)
            {
                if (d.Target == e.OldEntity.Value) d.Target = e.NewEntity.Value;
                if (d.Source == e.OldEntity.Value) d.Source = e.NewEntity.Value;
            }

        if (e.OldEntity != Entity || cm == null) return;
        var newRally = cm.QueryInterface<RallyPointComponent>(e.NewEntity);
        if (newRally == null) return;
        foreach (var kv in PerPlayer)
            for (int i = 0; i < kv.Value.Pos.Count; i++)
            {
                newRally.AddPosition(kv.Value.Pos[i], kv.Key);
                if (i < kv.Value.Data.Count)
                    newRally.AddData(kv.Value.Data[i].Clone(), kv.Key);
            }
    }

    private int OwnerId(ComponentManager cm) =>
        cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;

    /// <summary>兼容面:首个集结点(旧单点读取处;空队列 = Zero)。
    /// 定序取键最小者(Dictionary 枚举序不定,多玩家键并存时旧实现读序不稳)。</summary>
    public Maths.FixedVector2D Position
    {
        get
        {
            var first = PerPlayer.OrderBy(kv => kv.Key)
                .FirstOrDefault(kv => kv.Value.Pos.Count > 0);
            return first.Value != null
                ? first.Value.Pos[0]
                : new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero);
        }
    }

    /// <summary>兼容写:设单点(清空该玩家队列后放一点)。旧 Set(pos) 语义。</summary>
    public void Set(Maths.FixedVector2D pos, int player = -1)
    {
        var e = EntryFor(player);
        e.Pos.Clear(); e.Data.Clear();
        e.Pos.Add(pos);
        e.Data.Add(new RallyPointData());
    }

    private RallyPointEntry EntryFor(int player)
    {
        if (!PerPlayer.TryGetValue(player, out var e))
            PerPlayer[player] = e = new RallyPointEntry();
        return e;
    }

    public void AddPosition(Maths.FixedVector2D pos, int player)
    {
        EntryFor(player).Pos.Add(pos);
        EntryFor(player).Data.Add(new RallyPointData());
    }

    public void AddData(RallyPointData data, int player)
    {
        var e = EntryFor(player);
        if (e.Data.Count < e.Pos.Count) e.Data.Add(data);
        else e.Data[^1] = data;
    }

    public bool HasPositions(int player) =>
        PerPlayer.TryGetValue(player, out var e) && e.Pos.Count > 0;

    /// <summary>任一玩家有集结点(生产出货门槛用;具体属主解析在 OrderToRallyPoint)。</summary>
    public bool HasAnyPositions => PerPlayer.Values.Any(e => e.Pos.Count > 0);

    /// <summary>原版 GetPositions:目标实体存活且在世界上 → 点位跟拍其当前位置
    /// (LOS 可见性检查略——我们的 mirage 体系另行处理)。</summary>
    public List<Maths.FixedVector2D> GetPositions(ComponentManager cm, int player)
    {
        var ret = new List<Maths.FixedVector2D>();
        if (!PerPlayer.TryGetValue(player, out var e)) return ret;
        for (int i = 0; i < e.Pos.Count; i++)
        {
            var pos = e.Pos[i];
            var data = i < e.Data.Count ? e.Data[i] : null;
            if (data?.Target > 0)
            {
                var target = new EntityId(data.Target);
                var hp = cm.QueryInterface<HealthComponent>(target);
                var tpos = cm.QueryInterface<PositionComponent>(target);
                bool alive = cm.QueryInterface<FormationComponent>(target) != null
                    || hp is { Current: > 0 };
                if (alive && tpos != null && tpos.InWorld)
                    pos = new Maths.FixedVector2D(tpos.Position.X, tpos.Position.Z);
            }
            ret.Add(pos);
        }
        return ret;
    }

    public List<RallyPointData> GetData(int player) =>
        PerPlayer.TryGetValue(player, out var e) ? e.Data : new List<RallyPointData>();

    public void Unset(int player) => PerPlayer.Remove(player);

    /// <summary>原版 OnOwnershipChanged:易主清空集结队列。</summary>
    public void OnOwnershipChanged() => PerPlayer.Clear();

    /// <summary>原版 OrderToRallyPoint:给出厂单位按集结队列下发排队订单
    /// (queued=true 链)。ignore 语义:命中本建筑且类型在忽略列的点逐点跳过
    /// (原版整链 return;WS1 裁定保留逐点跳过,不回退)。命令翻译对齐
    /// RallyPointCommands.js:目标死了的 gather → gather-near-position;
    /// attack → attack-walk;其余 → walk。末命令为 trade 且前导全 walk →
    /// 前导点折叠为航线 waypoints(原版 RallyPointCommands.js:145-167;
    /// 集结点显示仍按全点,折叠只作用于下发命令)。repair/build 末点
    /// autocontinue=true(原版 RallyPointCommands.js:57)。</summary>
    public void OrderToRallyPoint(ComponentManager cm, EntityId spawned, params string[] ignore)
    {
        var own = cm.QueryInterface<OwnershipComponent>(spawned);
        if (own == null) return;
        int player = own.PlayerId;
        // 回落链(原版默认 player=GetOwner()):单位属主 → 建筑属主 → 兼容键 -1
        // (旧 Set(pos) 无玩家语义时的存储键)。
        if (!HasPositions(player))
        {
            int buildingOwner = OwnerId(cm);
            if (HasPositions(buildingOwner)) player = buildingOwner;
            else if (HasPositions(-1)) player = -1;
            else return;
        }
        var ai = cm.QueryInterface<UnitAIComponent>(spawned);
        if (ai == null) return;

        var e = PerPlayer[player];
        var positions = GetPositions(cm, player);

        // 第一遍:逐点翻译成待发动作(原版 GetRallyPointCommands 的产物)。
        var actions = new List<(string Command, Maths.FixedVector2D Pos, RallyPointData Data)>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            var data = i < e.Data.Count ? e.Data[i] : new RallyPointData();
            string command = data.Command;
            if (data.Target > 0)
            {
                var tpos = cm.QueryInterface<PositionComponent>(new EntityId(data.Target));
                if (tpos == null || !tpos.InWorld)
                    command = command switch
                    {
                        "gather" => "gather-near-position",
                        "collect-treasure" => "collect-treasure",   // 近位版未实现,直走
                        "attack" => "attack-walk",
                        _ => "walk",
                    };
            }
            // 指向本建筑且在忽略列 → 跳过该点(原版 ignore 语义,逐点判)。
            if (data.Target == Entity.Value && ignore.Contains(command))
                continue;
            actions.Add((command, pos, data));
        }
        if (actions.Count == 0) return;

        // trade 航线折叠(原版 RallyPointCommands.js:145-167):末动作 trade 且前导
        // 全是 walk → 前导点变 waypoints 随 trade 单走(每程往返都经过),不再单独下发。
        List<Maths.FixedVector2D>? route = null;
        if (actions.Count > 1 && actions[^1].Command == "trade")
        {
            var waypoints = new List<Maths.FixedVector2D>(actions.Count - 1);
            bool allWalk = true;
            for (int i = 0; i < actions.Count - 1; i++)
            {
                if (actions[i].Command != "walk") { allWalk = false; break; }
                waypoints.Add(actions[i].Pos);
            }
            if (allWalk && waypoints.Count > 0)
            {
                route = waypoints;
                actions.RemoveRange(0, actions.Count - 1);
            }
        }

        for (int i = 0; i < actions.Count; i++)
        {
            var (command, pos, data) = actions[i];
            switch (command)
            {
                case "gather":
                    ai.Gather(new EntityId(data.Target), queued: true);
                    break;
                case "gather-near-position":
                    ai.GatherNearPosition(pos, queued: true);
                    break;
                case "repair":
                case "build":
                    // 末点 autocontinue(原版 autocontinue: i == rallyPos.length - 1):
                    // 修完/建完就近续建下一地基。
                    ai.Repair(new EntityId(data.Target), queued: true,
                        autocontinue: i == actions.Count - 1);
                    break;
                case "garrison":
                    ai.Garrison(new EntityId(data.Target), queued: true);
                    break;
                case "occupy-turret":
                    ai.OccupyTurret(new EntityId(data.Target), queued: true);
                    break;
                case "attack":
                    ai.Attack(new EntityId(data.Target), data.AllowCapture, queued: true);
                    break;
                case "attack-walk":
                    ai.WalkAndFight(pos, queued: true);
                    break;
                case "patrol":
                    ai.Patrol(pos, queued: true);
                    break;
                case "collect-treasure":
                    ai.CollectTreasure(new EntityId(data.Target), queued: true);
                    break;
                case "trade":
                    // 原版 setup-trade-route(target=第二市场, source=第一市场, route)。
                    if (data.Target > 0)
                        ai.SetupTradeRoute(cm, new EntityId(data.Target),
                            data.Source > 0 ? new EntityId(data.Source) : null,
                            i == actions.Count - 1 ? route : null, queued: true);
                    else
                        ai.Walk(pos, queued: true);
                    break;
                default:
                    ai.Walk(pos, queued: true);
                    break;
            }
        }
    }

    public override void Serialize(ISerializer s)
    {
        int n = PerPlayer.Count;
        s.NumberI32("players", n);
        foreach (var kv in PerPlayer.OrderBy(kv => kv.Key))
        {
            s.NumberI32("player", kv.Key);
            s.NumberI32("count", kv.Value.Pos.Count);
            for (int i = 0; i < kv.Value.Pos.Count; i++)
            {
                s.NumberFixed("x", kv.Value.Pos[i].X);
                s.NumberFixed("z", kv.Value.Pos[i].Y);
                var d = i < kv.Value.Data.Count ? kv.Value.Data[i] : new RallyPointData();
                s.StringASCII("cmd", d.Command);
                s.NumberU32("target", d.Target);
                s.NumberU32("source", d.Source);
                s.StringASCII("res", d.ResourceType);
                s.Bool("ac", d.AllowCapture);
                // 存档 v19 尾段:attack-walk/patrol 目标类别(空串 = 不过滤)。
                s.StringASCII("tcl", d.TargetClasses ?? "");
            }
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        PerPlayer.Clear();
        int n = d.NumberI32("players");
        for (int p = 0; p < n; p++)
        {
            int player = d.NumberI32("player");
            int count = d.NumberI32("count");
            var e = EntryFor(player);
            for (int i = 0; i < count; i++)
            {
                e.Pos.Add(new Maths.FixedVector2D(d.NumberFixed("x"), d.NumberFixed("z")));
                var data = new RallyPointData
                {
                    Command = d.StringASCII("cmd"),
                    Target = d.NumberU32("target"),
                    Source = d.NumberU32("source"),
                    ResourceType = d.StringASCII("res"),
                    AllowCapture = d.Bool("ac"),
                };
                // 存档 v19 尾段(更早的档没有,按 null/不过滤读;见 SaveFormat.LoadedVersion)。
                if (SaveFormat.LoadedVersion >= 19)
                {
                    string tcl = d.StringASCII("tcl");
                    data.TargetClasses = tcl.Length > 0 ? tcl : null;
                }
                e.Data.Add(data);
            }
        }
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Vision", "Vision")]
public sealed class VisionComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>Base vision range in meters (fixed-point — this feeds LOS tick math).
    /// Template value comes from &lt;Vision&gt;&lt;Range&gt;; techs adjust the effective
    /// range through the modifiers pipeline ("Vision/Range").</summary>
    public Maths.Fixed Range;

    protected override void OnInit() => Range = Maths.Fixed.FromInt(20);

    public override void Serialize(ISerializer s) =>
        s.NumberFixed("range", Range);

    public override void Deserialize(IDeserializer d) =>
        Range = d.NumberFixed("range");

    public void HandleMessage(IMessage message) { }
}

[Component("Promotion", "Promotion")]
public sealed class PromotionComponent : ComponentBase, IComponentMessageHandler
{
    public int XP;
    public int Level = 1;
    public int XpNext = 20;
    /// <summary>Promotion/Entity:晋升目标模板(空 = 无晋升链,如 elite 段/英雄)。</summary>
    public string PromoteTo = "";

    public void AddXP(ComponentManager cm, int amount)
    {
        XP += amount;
        // 原版 Promotion.js:XP ≥ RequiredXp 即 Promote(ChangeEntityTemplate 换模板,
        // 位置/朝向/属主保持,血量按比例折算,余量 XP 结转新段)。
        if (PromoteTo.Length > 0 && XP >= XpNext && cm != null)
        {
            Promote(cm, XP - XpNext);
            return;
        }
        while (XP >= XpNext && PromoteTo.Length == 0)
        {
            // 无晋升链(原版到顶):等级继续累计(供表现层军衔条)。
            XP -= XpNext;
            Level++;
            XpNext = (int)(XpNext * 1.5f);
        }
    }

    /// <summary>旧签名(无 cm):只累计不晋升,行为兼容。</summary>
    public void AddXP(int amount) => AddXP(null!, amount);

    /// <summary>换模板晋升(原版 ChangeEntityTemplate 语义):同位同向同主重建,
    /// 血量比例折算,余量 XP 结转。新实体的组件字段由装配器按新模板注入。</summary>
    private void Promote(ComponentManager cm, int carryXp)
    {
        var identity = cm.QueryInterface<IdentityComponent>(Entity);
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var health = cm.QueryInterface<HealthComponent>(Entity);
        if (identity == null || pos == null || owner == null) return;

        string target = PromoteTo.Replace("{civ}",
            cm.GetPlayerEntity(owner.PlayerId)?.Civ ?? "");
        if (target.Contains('{') || cm.Templates?.TemplateExists(target) != true) return;

        float x = pos.Position.X.ToFloat();
        float z = pos.Position.Z.ToFloat();
        var yaw = pos.Rotation.Y;
        float frac = health != null && health.Max > 0
            ? (float)health.Current / health.Max : 1f;
        cm.DestroyEntity(Entity);
        var promoted = cm.SpawnEntity(target, x, z, owner.PlayerId);
        // 原版 MT_EntityRenamed(晋升换号):触发器/护卫/集结点改指。
        cm.Events.RaiseEntityRenamed(new Events.EntityRenamedEvent
        { OldEntity = Entity, NewEntity = promoted });
        var newPos = cm.QueryInterface<PositionComponent>(promoted);
        if (newPos != null)
            newPos.Rotation = new Maths.FixedVector3D(pos.Rotation.X, yaw, pos.Rotation.Z);
        var newHealth = cm.QueryInterface<HealthComponent>(promoted);
        if (newHealth != null && frac < 1f)
            newHealth.Current = (int)MathF.Round(newHealth.Max * frac);
        var newPromotion = cm.QueryInterface<PromotionComponent>(promoted);
        if (newPromotion != null && carryXp > 0)
            newPromotion.XP = carryXp;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("xp", XP);
        s.NumberI32("lvl", Level);
        s.NumberI32("next", XpNext);
        s.StringASCII("to", PromoteTo);
    }

    public override void Deserialize(IDeserializer d)
    {
        XP = d.NumberI32("xp");
        Level = d.NumberI32("lvl");
        XpNext = d.NumberI32("next");
        PromoteTo = d.StringASCII("to");
    }

    public void HandleMessage(IMessage message) { }
}
