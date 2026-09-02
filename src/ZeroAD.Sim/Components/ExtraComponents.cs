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

    protected override void OnInit() { }

    private int OwnerId(ComponentManager cm) =>
        cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;

    /// <summary>兼容面:首个集结点(旧单点读取处;空队列 = Zero)。</summary>
    public Maths.FixedVector2D Position
    {
        get
        {
            foreach (var kv in PerPlayer)
                if (kv.Value.Pos.Count > 0) return kv.Value.Pos[0];
            return new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero);
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
    /// (queued=true 链)。首命令命中本建筑且类型在 ignore 中 → 全链跳过
    /// (原版:卸载时不向自身再集结)。命令翻译对齐 RallyPointCommands.js:
    /// 目标死了的 gather → gather-near-position;attack → attack-walk;其余 → walk。</summary>
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
            // 首命令指向本建筑且在忽略列 → 跳过该点(原版 ignore 语义,逐点判)。
            if (data.Target == Entity.Value && ignore.Contains(command))
                continue;

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
                    ai.Repair(new EntityId(data.Target), queued: true);
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
                    ai.Trade(new EntityId(data.Target > 0 ? data.Target : data.Source), queued: true);
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
                e.Data.Add(new RallyPointData
                {
                    Command = d.StringASCII("cmd"),
                    Target = d.NumberU32("target"),
                    Source = d.NumberU32("source"),
                    ResourceType = d.StringASCII("res"),
                    AllowCapture = d.Bool("ac"),
                });
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
