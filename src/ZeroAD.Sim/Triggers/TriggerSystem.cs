using System;
using System.Collections.Generic;
using System.Globalization;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Triggers
{
    /// <summary>触发器条件(数据驱动;一个触发器的全部条件为 AND 关系)。
    /// 内置类型:
    ///   TimeElapsed          Seconds=浮点秒(触发器启用期间累计)
    ///   PlayerDefeated       PlayerId
    ///   PlayerWon            PlayerId
    ///   AreaContainsEntities X,Z,Radius(米),MinCount;可选 PlayerId(缺省任意非 gaia)、Class
    ///   EntityCountAtMost    PlayerId,Count;可选 Class(该玩家场上实体数 ≤ Count 时成立)</summary>
    public sealed class TriggerCondition
    {
        public string Type = "";
        public Dictionary<string, string> Params = new(StringComparer.Ordinal);
    }

    /// <summary>触发器动作。内置类型:
    ///   ShowMessage     Text(经 ITriggerSink 送往表现层)
    ///   SpawnEntities   Template,PlayerId,X,Z;可选 Count(默认 1),Spread(默认 0,半径米)
    ///   VictoryPlayer   PlayerId(判胜)
    ///   DefeatPlayer    PlayerId(判负)
    ///   EnableTrigger   Name(启用另一触发器)
    ///   DisableTrigger  Name(禁用另一触发器;可自指实现 Once)</summary>
    public sealed class TriggerAction
    {
        public string Type = "";
        public Dictionary<string, string> Params = new(StringComparer.Ordinal);
    }

    public sealed class TriggerDefinition
    {
        public string Name = "";
        public bool Enabled = true;
        /// <summary>true = 触发一次后自动禁用(原版一次性触发器语义)。</summary>
        public bool Once;
        public List<TriggerCondition> Conditions = new();
        public List<TriggerAction> Actions = new();
        /// <summary>TimeElapsed/Interval 条件用:启用期间累计秒数(存档保持,
        /// 读档后间隔不重置——原版 OnInterval 的 SetInterval 状态骑缝)。</summary>
        internal Maths.Fixed Elapsed;

        /// <summary>序列化(存档骑缝;原版 Trigger 组件的 triggerData.enabled +
        /// 定时器状态)。条件/动作表为静态定义不序列化,Enabled/Elapsed 为动态。</summary>
        public void Serialize(ZeroAD.Sim.Serialization.ISerializer s)
        {
            s.StringASCII("name", Name);
            s.Bool("enabled", Enabled);
            s.NumberFixed("elapsed", Elapsed);
        }

        public void Deserialize(ZeroAD.Sim.Serialization.IDeserializer d)
        {
            Name = d.StringASCII("name");
            Enabled = d.Bool("enabled");
            Elapsed = d.NumberFixed("elapsed");
        }
    }

    /// <summary>表现层/生成层挂钩——sim 内核不直接解析模板生成实体(模板解析在 SimBridge),
    /// 也不直接显示消息。由 SimBridge 实现注入。</summary>
    public interface ITriggerSink
    {
        void ShowMessage(string text);
        /// <summary>在 (x,z) 附近生成 count 个 template 实体,属主 playerId(0=gaia)。
        /// 返回生成的实体 id(地图脚本要给它们下命令;顺序 = 生成序,确定性)。</summary>
        IReadOnlyList<EntityId> SpawnEntities(string template, int playerId, float x, float z, int count, float spread);
    }

    /// <summary>地图脚本行为(maps/random/*_triggers.js 的 C# 移植接口)。
    /// OnInit 在地图加载完成时调一次(原版 OnInitGame);Tick 每 sim 回合由
    /// TriggerSystem.Tick 驱动(原版 DoAfterDelay/DoRepeatedly 用脚本内自排程实现,
    /// RNG 一律走 cm.RNG 保锁步一致)。dt 为定点秒(内核禁 float)。</summary>
    public interface IMapScriptBehavior
    {
        void OnInit(ComponentManager cm);
        void Tick(ComponentManager cm, Maths.Fixed dt);
    }

    /// <summary>触发器系统(原版 Trigger.js 的 C# 数据驱动移植框架)。
    /// 原版由地图 JS 脚本向 Trigger 组件注册 事件→动作;C# 内核无法执行地图 JS,
    /// 改为数据驱动:条件/动作内置实现,地图(或教程/战役)以 TriggerDefinition 表达。
    /// 由 ComponentManager.TickVictory 按回合驱动。确定性:全部条件为世界状态轮询,
    /// 无 RNG;动作为同步执行,顺序 = 注册顺序。
    ///
    /// 原版事件模型:Trigger 组件订阅 sim 消息(OnGlobalConstructionFinished/
    /// OnGlobalOwnershipChanged/OnGlobalDiplomacyChanged/OnTreasureCollected…)
    /// 经 CallEvent 投递到注册的触发器。本移植同名事件钩子由 SimBridge 在对应
    /// sim 事件点直调,OnInterval 由每回合 Elapsed 轮询推进。</summary>
    public sealed class TriggerSystem
    {
        private readonly List<TriggerDefinition> _triggers = new();

        // 事件触发器注册表(原版 this.triggers[event][name]):事件名 → 触发器列表。
        // CallEvent 时按事件名取全部启用中的触发器投递。
        private readonly Dictionary<string, List<TriggerDefinition>> _eventTriggers =
            new(StringComparer.Ordinal);

        /// <summary>可选效果出口(消息/生成)。null 时 ShowMessage/SpawnEntities 静默跳过。</summary>
        public ITriggerSink? Sink;

        /// <summary>地图脚本(当前图的 _triggers.js 移植件;null = 该图无脚本)。</summary>
        public IMapScriptBehavior? MapScript;

        // 触发点注册表(ref → 实体;模板带 <TriggerPoint><Reference> 的实体装配时自动入库,
        // 原版 Trigger.RegisterTriggerPoint)。坐标经 PositionComponent 取——存实体而非坐标,
        // 是因为实体同时是 OnRange 主动查询的 source。
        private readonly Dictionary<string, List<EntityId>> _triggerPoints = new(StringComparer.Ordinal);

        // OnRange 主动查询注册表(tag → 触发器名;原版 TriggerPoint.RegisterRangeTrigger 的
        // currentCollections/triggers 两表)。注册序 = 回合末派发序(确定性)。
        private readonly List<(int Tag, string Name)> _rangeTriggers = new();

        /// <summary>接 sim 事件总线(原版 Trigger 组件订阅 sim 消息的等价):
        /// OwnershipChanged/StructureBuilt/TrainingFinished/ResearchFinished 经
        /// CallEvent 投递到事件触发器。由 ComponentManager 装配时调一次。</summary>
        public void Attach(ComponentManager cm)
        {
            cm.OwnerChanged += (entity, from, to) =>
                CallEvent(cm, "OnOwnershipChanged", new { entity, from, to });
            cm.Events.StructureBuilt += e =>
                CallEvent(cm, "OnStructureBuilt", e);
            cm.Events.TrainingFinished += e =>
                CallEvent(cm, "OnTrainingFinished", e);
            cm.Events.ResearchFinished += e =>
                CallEvent(cm, "OnResearchFinished", e);
            cm.Events.TreasureCollected += e =>
                CallEvent(cm, "OnTreasureCollected", e);
            // 原版 Trigger.eventNames 其余项全量接线:
            cm.Events.PlayerCommand += e =>
                CallEvent(cm, "OnPlayerCommand", e);
            cm.Events.PlayerDefeated += e =>
                CallEvent(cm, "OnPlayerDefeated", e);
            cm.Events.PlayerWon += e =>
                CallEvent(cm, "OnPlayerWon", e);
            cm.Events.DiplomacyChanged += e =>
                CallEvent(cm, "OnDiplomacyChanged", e);
            cm.Events.ConstructionStarted += e =>
                CallEvent(cm, "OnConstructionStarted", e);
            cm.Events.TrainingQueued += e =>
                CallEvent(cm, "OnTrainingQueued", e);
            cm.Events.ResearchQueued += e =>
                CallEvent(cm, "OnResearchQueued", e);
            cm.Events.EntityRenamed += e =>
                CallEvent(cm, "OnEntityRenamed", e);
            // 原版 OnAttackDetected 数据源 = AttackDetection 报警。
            cm.Events.PlayerAttackedAlert += e =>
                CallEvent(cm, "OnAttackDetected", e);
            // OnCinemaPathEnded/OnCinemaQueueEnded:表现层 CinemaManager 经
            // SimBridge 转调 CallEvent(过场播放完成时);OnRange:主动范围查询——
            // AddRangeTrigger 建查询,Tick 回合末按 added/removed 增量派发
            // (原版 RangeManager PerformActiveQueries → MT_RangeUpdate →
            // TriggerPoint.OnRangeUpdate → CallTrigger("OnRange"))。
        }

        public IReadOnlyList<TriggerDefinition> Triggers => _triggers;

        /// <summary>注册触发点(原版 Trigger.RegisterTriggerPoint:ref → 实体)。
        /// 由 EntityAssembler 在装配带 &lt;TriggerPoint&gt;&lt;Reference&gt; 模板的实体时调用。</summary>
        public void RegisterTriggerPoint(string reference, EntityId entity)
        {
            if (!_triggerPoints.TryGetValue(reference, out var list))
                _triggerPoints[reference] = list = new List<EntityId>();
            if (!list.Contains(entity)) list.Add(entity);
        }

        /// <summary>移除触发点(原版 Trigger.RemoveRegisteredTriggerPoint;
        /// ComponentManager.DestroyEntity 在销毁 TriggerPointComponent 实体时调用)。</summary>
        public void UnregisterTriggerPoint(string reference, EntityId entity)
        {
            if (!_triggerPoints.TryGetValue(reference, out var list)) return;
            list.Remove(entity);
            if (list.Count == 0) _triggerPoints.Remove(reference);
        }

        /// <summary>取触发点实体(原版 GetTriggerPoints 的实体形态;无该 ref → 空表)。
        /// 注册序 = 生成序(确定性)。</summary>
        public IReadOnlyList<EntityId> GetTriggerPointEntities(string reference) =>
            _triggerPoints.TryGetValue(reference, out var list) ? list
                : (IReadOnlyList<EntityId>)Array.Empty<EntityId>();

        /// <summary>取触发点坐标(经各实体的 PositionComponent;不在世界的跳过)。
        /// 无该 ref → 空表。</summary>
        public List<Maths.FixedVector2D> GetTriggerPoints(ComponentManager cm, string reference)
        {
            var result = new List<Maths.FixedVector2D>();
            foreach (var ent in GetTriggerPointEntities(reference))
            {
                var pos = cm.QueryInterface<PositionComponent>(ent);
                if (pos == null || !pos.InWorld) continue;
                result.Add(new Maths.FixedVector2D(pos.Position.X, pos.Position.Z));
            }
            return result;
        }

        public void Add(TriggerDefinition trigger) => _triggers.Add(trigger);

        /// <summary>注册事件触发器(原版 Trigger.RegisterTrigger:event → 动作。
        /// EventName 为原版事件名(OnTreasureCollected/OnStructureBuilt/
        /// OnDiplomacyChanged/OnInterval/OnOwnershipChanged/OnInitGame/…)。</summary>
        public void AddEventTrigger(string eventName, TriggerDefinition trigger)
        {
            if (!_eventTriggers.TryGetValue(eventName, out var list))
                _eventTriggers[eventName] = list = new List<TriggerDefinition>();
            list.Add(trigger);
        }

        public void Clear()
        {
            _triggers.Clear();
            _eventTriggers.Clear();
            _triggerPoints.Clear();
            _rangeTriggers.Clear();
        }

        /// <summary>事件总线(原版 Trigger.CallEvent):按事件名投递到全部启用
        /// 触发器;OnInitGame 在地图加载完成时投递一次(原版 OnGlobalInitGame)。</summary>
        public void CallEvent(ComponentManager cm, string eventName, object? data = null)
        {
            if (!_eventTriggers.TryGetValue(eventName, out var list)) return;
            foreach (var t in list)
            {
                if (!t.Enabled) continue;
                foreach (var action in t.Actions)
                    Execute(cm, action);
                if (t.Once) t.Enabled = false;
            }
        }

        /// <summary>具名派发(原版 Trigger.CallTrigger(event, name, data)):只投递到
        /// 该事件下 Name 匹配的启用触发器。OnRange 增量用此通道。</summary>
        public void CallTrigger(ComponentManager cm, string eventName, string triggerName, object? data = null)
        {
            if (!_eventTriggers.TryGetValue(eventName, out var list)) return;
            foreach (var t in list)
            {
                if (t.Name != triggerName || !t.Enabled) continue;
                foreach (var action in t.Actions)
                    Execute(cm, action);
                if (t.Once) t.Enabled = false;
            }
        }

        /// <summary>注册持续范围触发器(原版 TriggerPoint.RegisterRangeTrigger):
        /// 以 source 实体为圆心建主动查询;回合末出现 added/removed 增量时
        /// CallTrigger("OnRange", name, RangeUpdateData)。返回查询 tag。
        /// maxRange &lt; 0 = 不限。players 空 = 任意属主。</summary>
        public int AddRangeTrigger(ComponentManager cm, EntityId source, string triggerName,
            Maths.Fixed minRange, Maths.Fixed maxRange, IReadOnlyList<int>? players = null,
            string requiredClass = "", bool enabled = true)
        {
            var range = SimSystem.Range
                ?? throw new InvalidOperationException("AddRangeTrigger requires a RangeManager");
            int tag = range.CreateActiveQuery(source, minRange, maxRange, players, requiredClass, enabled);
            _rangeTriggers.Add((tag, triggerName));
            return tag;
        }

        public void EnableRangeTrigger(int tag) => SimSystem.Range?.EnableActiveQuery(tag);
        public void DisableRangeTrigger(int tag) => SimSystem.Range?.DisableActiveQuery(tag);

        public TriggerDefinition? Find(string name)
        {
            foreach (var t in _triggers)
                if (t.Name == name) return t;
            return null;
        }

        /// <summary>序列化(存档骑缝;原版 Trigger 组件的 triggerData.enabled +
        /// 定时器状态 + triggerPoints 表):条件/动作为静态定义不序列化。
        /// v20 起附触发点注册表(ref → 实体 id 列表,ref 字典序、id 升序,写序固定);
        /// 读档后坐标经各实体 PositionComponent 复原。与 Deserialize 写序逐位一致。</summary>
        public void Serialize(ZeroAD.Sim.Serialization.ISerializer s)
        {
            s.NumberI32("count", _triggers.Count);
            foreach (var t in _triggers)
                t.Serialize(s);
            // v20 尾段:触发点注册表。
            var refs = new List<string>(_triggerPoints.Keys);
            refs.Sort(StringComparer.Ordinal);
            s.NumberI32("pointRefs", refs.Count);
            foreach (var r in refs)
            {
                s.StringASCII("ref", r);
                var ids = new List<EntityId>(_triggerPoints[r]);
                ids.Sort((a, b) => a.Value.CompareTo(b.Value));
                s.NumberI32("n", ids.Count);
                foreach (var id in ids)
                    s.NumberU32("ent", id.Value);
            }
        }

        /// <summary>反序列化(与 Serialize 写序逐位一致)。条件/动作静态定义
        /// 由地图脚本重注(OnInitGame 时 Add 的触发器先 Reset 再 Fill)。
        /// v19 及更早的档无触发点尾段 → 注册表置空(v20 前触发点本就不进档)。</summary>
        public void Deserialize(ZeroAD.Sim.Serialization.IDeserializer d)
        {
            int count = d.NumberI32("count");
            _triggers.Clear();
            for (int i = 0; i < count; i++)
            {
                var t = new TriggerDefinition();
                t.Deserialize(d);
                _triggers.Add(t);
            }
            _triggerPoints.Clear();
            if (Serialization.SaveFormat.LoadedVersion >= 20)
            {
                int refCount = d.NumberI32("pointRefs");
                for (int i = 0; i < refCount; i++)
                {
                    string r = d.StringASCII("ref");
                    int n = d.NumberI32("n");
                    var list = new List<EntityId>(n);
                    for (int j = 0; j < n; j++)
                        list.Add(new EntityId(d.NumberU32("ent")));
                    _triggerPoints[r] = list;
                }
            }
        }

        /// <summary>地图加载完成后投递 OnInitGame(原版 OnGlobalInitGame 广播:
        /// 地图脚本/战役注册的 OnInitGame 事件触发器在此点火)。由 SimBridge 在
        /// 地图脚本安装后(rmgen 路径)与场景实体生成完成后(scenario 路径)各调一次。</summary>
        public void NotifyInitGame(ComponentManager cm) =>
            CallEvent(cm, "OnInitGame", null);

        /// <summary>读档完成后投递(原版 OnDeserialized:Trigger 组件反序列化后
        /// 广播,触发器脚本重建瞬态)。由存档加载路径(SaveGameManager/SimBridge)
        /// 在 Deserialize 后调用。</summary>
        public void NotifyDeserialized(ComponentManager cm) =>
            CallEvent(cm, "OnDeserialized", null);

        /// <summary>OnRange 增量载荷(原版 TriggerPoint.OnRangeUpdate 的 r 对象:
        /// {added, removed, currentCollection})。</summary>
        public sealed class RangeUpdateData
        {
            public IReadOnlyList<EntityId> Added = Array.Empty<EntityId>();
            public IReadOnlyList<EntityId> Removed = Array.Empty<EntityId>();
            public IReadOnlyList<EntityId> CurrentCollection = Array.Empty<EntityId>();
        }

        /// <summary>每回合推进。dt 为本回合秒数(定点,0.1)。返回本回合触发次数(测试观察用)。
        /// OnInterval 事件在此投递(原版 SetInterval 语义:Interval 秒一到即投递);
        /// 回合末统一派发 OnRange 增量(原版 RangeManager 回合更新的时点)。</summary>
        public int Tick(ComponentManager cm, Maths.Fixed dt)
        {
            MapScript?.Tick(cm, dt);
            int fired = 0;
            // 按索引遍历:动作可启用/禁用触发器(含自禁用),不修改集合本身。
            for (int i = 0; i < _triggers.Count; i++)
            {
                var t = _triggers[i];
                if (!t.Enabled) continue;
                t.Elapsed += dt;

                bool all = true;
                foreach (var cond in t.Conditions)
                {
                    if (!Evaluate(cm, t, cond)) { all = false; break; }
                }
                if (!all) continue;

                fired++;
                foreach (var action in t.Actions)
                    Execute(cm, action);
                if (t.Once) t.Enabled = false;
            }
            // OnInterval 定时器事件(原版 SetInterval 的轮询等价):
            // Interval 秒一到即投递(参数挂触发器 Conditions 首个
            // "Interval" 参数的 Seconds,与原版的 interval 数据语义一致)。
            foreach (var t in _triggers)
            {
                if (!t.Enabled) continue;
                var interval = Maths.Fixed.Zero;
                foreach (var cond in t.Conditions)
                    if (cond.Type == "Interval")
                    {
                        interval = GetFixed(cond, "Seconds", Maths.Fixed.Zero);
                        break;
                    }
                if (interval <= Maths.Fixed.Zero) continue;
                if (t.Elapsed >= interval)
                {
                    t.Elapsed = Maths.Fixed.Zero;
                    CallEvent(cm, "OnInterval", null);
                    fired++;
                }
            }
            // 回合末:OnRange 主动查询增量(原版 PerformActiveQueries →
            // TriggerPoint.OnRangeUpdate → CallTrigger("OnRange", name, r))。
            fired += DispatchRangeUpdates(cm);
            return fired;
        }

        /// <summary>回合末派发全部启用中的 OnRange 主动查询增量(注册序)。
        /// 返回派发的触发器次数。</summary>
        private int DispatchRangeUpdates(ComponentManager cm)
        {
            if (_rangeTriggers.Count == 0) return 0;
            var range = SimSystem.Range;
            if (range == null) return 0;
            int fired = 0;
            foreach (var update in range.UpdateActiveQueries())
            {
                foreach (var (tag, name) in _rangeTriggers)
                {
                    if (tag != update.Tag) continue;
                    CallTrigger(cm, "OnRange", name, new RangeUpdateData
                    {
                        Added = update.Added,
                        Removed = update.Removed,
                        CurrentCollection = update.Current,
                    });
                    fired++;
                }
            }
            return fired;
        }

        private static bool Evaluate(ComponentManager cm, TriggerDefinition owner, TriggerCondition cond)
        {
            switch (cond.Type)
            {
                case "TimeElapsed":
                    return owner.Elapsed >= GetFixed(cond, "Seconds", Maths.Fixed.Zero);
                case "PlayerDefeated":
                {
                    var p = cm.Players.GetPlayerEntity(GetInt(cond, "PlayerId", -1));
                    return p != null && p.IsDefeated();
                }
                case "PlayerWon":
                {
                    var p = cm.Players.GetPlayerEntity(GetInt(cond, "PlayerId", -1));
                    return p != null && p.HasWon();
                }
                case "AreaContainsEntities":
                    return CountInArea(cm, cond) >= GetInt(cond, "MinCount", 1);
                case "EntityCountAtMost":
                    return CountByPlayer(cm, cond) <= GetInt(cond, "Count", 0);
                default:
                    return false;   // 未知条件类型不成立(保守,不触发)
            }
        }

        private void Execute(ComponentManager cm, TriggerAction action)
        {
            switch (action.Type)
            {
                case "ShowMessage":
                    Sink?.ShowMessage(GetStr(action, "Text", ""));
                    break;
                case "SpawnEntities":
                    Sink?.SpawnEntities(
                        GetStr(action, "Template", ""),
                        GetInt(action, "PlayerId", 0),
                        GetFloat(action, "X", 0f), GetFloat(action, "Z", 0f),
                        GetInt(action, "Count", 1), GetFloat(action, "Spread", 0f));
                    break;
                case "VictoryPlayer":
                {
                    int pid = GetInt(action, "PlayerId", -1);
                    var p = cm.Players.GetPlayerEntity(pid);
                    if (p != null && p.SetWon())
                        cm.Events.RaisePlayerWon(new Events.PlayerWonEvent { PlayerId = pid });
                    break;
                }
                case "DefeatPlayer":
                {
                    int pid = GetInt(action, "PlayerId", -1);
                    var p = cm.Players.GetPlayerEntity(pid);
                    if (p != null && p.SetDefeated())
                        cm.Events.RaisePlayerDefeated(new Events.PlayerDefeatedEvent
                        {
                            PlayerId = pid,
                            Reason = "Defeated by scenario trigger."
                        });
                    break;
                }
                case "EnableTrigger":
                {
                    var t = Find(GetStr(action, "Name", ""));
                    if (t != null) t.Enabled = true;
                    break;
                }
                case "DisableTrigger":
                {
                    var t = Find(GetStr(action, "Name", ""));
                    if (t != null) t.Enabled = false;
                    break;
                }
            }
        }

        private static int CountInArea(ComponentManager cm, TriggerCondition cond)
        {
            var range = SimSystem.Range;
            if (range == null) return 0;
            float x = GetFloat(cond, "X", 0f), z = GetFloat(cond, "Z", 0f);
            float radius = GetFloat(cond, "Radius", 0f);
            int playerId = GetInt(cond, "PlayerId", -1);    // -1 = 任意非 gaia
            string cls = GetStr(cond, "Class", "");
            float r2 = radius * radius;
            int count = 0;
            foreach (var ent in range.GetNonGaiaEntities())
            {
                var pos = cm.QueryInterface<PositionComponent>(ent);
                if (pos == null || !pos.InWorld) continue;
                float dx = pos.Position.X.ToFloat() - x, dz = pos.Position.Z.ToFloat() - z;
                if (dx * dx + dz * dz > r2) continue;
                if (playerId >= 0)
                {
                    var own = cm.QueryInterface<OwnershipComponent>(ent);
                    if (own == null || own.PlayerId != playerId) continue;
                }
                if (cls.Length > 0)
                {
                    var id = cm.QueryInterface<IdentityComponent>(ent);
                    if (id == null || !id.HasClass(cls)) continue;
                }
                count++;
            }
            return count;
        }

        private static int CountByPlayer(ComponentManager cm, TriggerCondition cond)
        {
            var range = SimSystem.Range;
            if (range == null) return 0;
            int playerId = GetInt(cond, "PlayerId", -1);
            string cls = GetStr(cond, "Class", "");
            int count = 0;
            foreach (var ent in range.GetEntitiesByPlayer(playerId))
            {
                if (cls.Length > 0)
                {
                    var id = cm.QueryInterface<IdentityComponent>(ent);
                    if (id == null || !id.HasClass(cls)) continue;
                }
                count++;
            }
            return count;
        }

        private static string GetStr(TriggerCondition c, string key, string fallback) =>
            c.Params.TryGetValue(key, out var v) ? v : fallback;
        private static string GetStr(TriggerAction a, string key, string fallback) =>
            a.Params.TryGetValue(key, out var v) ? v : fallback;
        private static int GetInt(TriggerCondition c, string key, int fallback) =>
            c.Params.TryGetValue(key, out var v) &&
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        private static int GetInt(TriggerAction a, string key, int fallback) =>
            a.Params.TryGetValue(key, out var v) &&
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        private static Maths.Fixed GetFixed(TriggerCondition c, string key, Maths.Fixed fallback) =>
            c.Params.TryGetValue(key, out var v) &&
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? Maths.Fixed.FromFloat(f) : fallback;
        private static float GetFloat(TriggerCondition c, string key, float fallback) =>
            c.Params.TryGetValue(key, out var v) &&
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
        private static float GetFloat(TriggerAction a, string key, float fallback) =>
            a.Params.TryGetValue(key, out var v) &&
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
    }
}
