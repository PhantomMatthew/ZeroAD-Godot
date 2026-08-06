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
        /// <summary>TimeElapsed 条件用:启用期间累计秒数。</summary>
        internal float Elapsed;
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
    /// RNG 一律走 cm.RNG 保锁步一致)。</summary>
    public interface IMapScriptBehavior
    {
        void OnInit(ComponentManager cm);
        void Tick(ComponentManager cm, float dt);
    }

    /// <summary>触发器系统(原版 Trigger.js 的 C# 数据驱动移植框架)。
    /// 原版由地图 JS 脚本向 Trigger 组件注册 事件→动作;C# 内核无法执行地图 JS,
    /// 改为数据驱动:条件/动作内置实现,地图(或教程/战役)以 TriggerDefinition 表达。
    /// 由 ComponentManager.TickVictory 按回合驱动。确定性:全部条件为世界状态轮询,
    /// 无 RNG;动作为同步执行,顺序 = 注册顺序。</summary>
    public sealed class TriggerSystem
    {
        private readonly List<TriggerDefinition> _triggers = new();

        /// <summary>可选效果出口(消息/生成)。null 时 ShowMessage/SpawnEntities 静默跳过。</summary>
        public ITriggerSink? Sink;

        /// <summary>地图脚本(当前图的 _triggers.js 移植件;null = 该图无脚本)。</summary>
        public IMapScriptBehavior? MapScript;

        // 触发点注册表(ref → 世界坐标;rmgen 的 trigger/trigger_point_X 实体经此入库)。
        private readonly Dictionary<string, List<Maths.FixedVector2D>> _triggerPoints = new(StringComparer.Ordinal);

        public IReadOnlyList<TriggerDefinition> Triggers => _triggers;

        /// <summary>注册触发点(原版 Trigger.RegisterTriggerPoint 的坐标版——
        /// 我们只记位置不建实体)。</summary>
        public void RegisterTriggerPoint(string reference, Maths.FixedVector2D pos)
        {
            if (!_triggerPoints.TryGetValue(reference, out var list))
                _triggerPoints[reference] = list = new List<Maths.FixedVector2D>();
            list.Add(pos);
        }

        /// <summary>取触发点(原版 GetTriggerPoints;无该 ref → 空表)。</summary>
        public IReadOnlyList<Maths.FixedVector2D> GetTriggerPoints(string reference) =>
            _triggerPoints.TryGetValue(reference, out var list) ? list
                : (IReadOnlyList<Maths.FixedVector2D>)Array.Empty<Maths.FixedVector2D>();

        public void Add(TriggerDefinition trigger) => _triggers.Add(trigger);

        public void Clear() => _triggers.Clear();

        public TriggerDefinition? Find(string name)
        {
            foreach (var t in _triggers)
                if (t.Name == name) return t;
            return null;
        }

        /// <summary>每回合推进。dt 为本回合秒数(0.1)。返回本回合触发次数(测试观察用)。</summary>
        public int Tick(ComponentManager cm, float dt)
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
            return fired;
        }

        private static bool Evaluate(ComponentManager cm, TriggerDefinition owner, TriggerCondition cond)
        {
            switch (cond.Type)
            {
                case "TimeElapsed":
                    return owner.Elapsed >= GetFloat(cond, "Seconds", 0f);
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
        private static float GetFloat(TriggerCondition c, string key, float fallback) =>
            c.Params.TryGetValue(key, out var v) &&
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
        private static float GetFloat(TriggerAction a, string key, float fallback) =>
            a.Params.TryGetValue(key, out var v) &&
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
    }
}
