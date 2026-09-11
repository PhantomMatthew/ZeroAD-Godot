using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim.Tutorial
{
    /// <summary>教程关卡数据(godot/data/tutorials/&lt;map&gt;.json)。原版目标表在教程地图
    /// JS(tutorialGoals)里;C# 无法执行地图 JS,改为 JSON 数据驱动——目标表以本记录
    /// 族描述,由 <see cref="TutorialLevelLoader.Assemble"/> 用内置绑定装配成 TutorialGoal
    /// (同 TriggerSystem 的数据驱动条件/动作模式)。</summary>
    public sealed class TutorialLevelData
    {
        public string Map = "";
        public List<GoalData> Goals = new();
    }

    /// <summary>单目标描述(原版 tutorialGoals 元素的数据形式)。</summary>
    public sealed class GoalData
    {
        public List<string> Instructions = new();
        /// <summary>0=自动(无绑定→Ready 按钮);&gt;0=计时器秒数;&lt;0=强制 Ready 按钮
        /// (上游 delay:-1,用于带内务绑定但靠手动翻页的目标)。</summary>
        public float Delay;
        public InitData? Init;
        /// <summary>达成条件——装配成 IsDone,由引擎 Recheck 在任何相关事件后收口。
        /// 缺省 = 无复合条件,绑定命中即翻页。</summary>
        public RequireData? Require;
        public List<BindingData> On = new();
        /// <summary>目标完成翻页时执行的命名动作(launchAttack/removeChampions)。</summary>
        public List<string> Actions = new();
    }

    public sealed class InitData
    {
        public List<string> ClearFlags = new();
        /// <summary>进入目标即弹出一条完成通知(原版结尾目标的 "Tutorial completed!")。</summary>
        public string? CompletionNotice;
    }

    public sealed class RequireData
    {
        /// <summary>全部旗为真(ctx.State 布尔槽)。</summary>
        public List<string> Flags = new();
        /// <summary>任一科技已排队或研发(TriggerHelper.HasDealtWithTech 语义)。</summary>
        public List<string> TechsDealtAny = new();
        /// <summary>任一科技已研发完成。</summary>
        public List<string> TechsResearchedAny = new();
        public string? CounterKey;
        public int CounterMin;
        /// <summary>房屋地基追踪集为空(经济关"等两栋房建完")。</summary>
        public bool HouseSetEmpty;
        /// <summary>LaunchAttack 记录的进攻方全灭(原版 IsAttackRepelled)。</summary>
        public bool AttackersRepelled;
    }

    /// <summary>事件绑定。Event 必填,其余按事件类型取用:
    /// 公共:SetFlag(命中置旗)/Warn(失配警告)/WarnAlways(连 Command 不匹配也警告);
    /// playerCommand:Command/ResourceSpecific/ResourceGeneric/TargetClass/TemplateContains/
    ///   RallyCommand/RallyResource(匹配 Data["specific"] 或 Data["resourceType"]);
    /// trainingQueued:Template|TemplateContains/MinCount/ExactCount/Increment/
    ///   ResetQueueOnMismatch/WarnOnCount/WarnOnTemplate;
    /// structureBuilt:Class|DropsiteAccepts;researchQueued:ResearcherClass;
    /// researchFinished:Tech(缺省=任意);ownershipChanged:From/To("enemy"/"gaia"/"self"/数字)/
    ///   TargetClass/TrackHouses(房屋地基追踪内务,不推进)。
    /// 带 SetFlag/Increment 的绑定是纯副作用,不直接翻页;无副作用的绑定在无 Require 时
    /// 命中即翻页。</summary>
    public sealed class BindingData
    {
        public string Event = "";
        public string? SetFlag;
        public string? Warn;
        public bool WarnAlways;
        public string? Command;
        public string? ResourceSpecific;
        public string? ResourceGeneric;
        public string? TargetClass;
        public string? TemplateContains;
        public string? RallyCommand;
        public string? RallyResource;
        public string? Template;
        public int? MinCount;
        public int? ExactCount;
        public string? Increment;
        public bool ResetQueueOnMismatch;
        public string? WarnOnCount;
        public string? WarnOnTemplate;
        public string? Class;
        public string? DropsiteAccepts;
        public string? ResearcherClass;
        public string? Tech;
        public string? From;
        public string? To;
        public bool TrackHouses;
    }

    /// <summary>JSON → 数据记录 → TutorialGoal 装配。加载只在教程开局执行一次,
    /// 不进 tick 路径(确定性无关)。未知 Event/Action 抛 InvalidDataException——
    /// 数据错误要响亮,不静默跳过。</summary>
    public static class TutorialLevelLoader
    {
        private const string HouseGoalKey = "houseGoal";
        private const string HouseCountKey = "houseCount";

        public static TutorialLevelData Load(string path) => Parse(File.ReadAllText(path));

        public static TutorialLevelData Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var level = new TutorialLevelData
            {
                Map = root.TryGetProperty("Map", out var m) ? m.GetString() ?? "" : ""
            };
            foreach (var g in root.GetProperty("Goals").EnumerateArray())
                level.Goals.Add(ParseGoal(g));
            return level;
        }

        private static GoalData ParseGoal(JsonElement g)
        {
            var goal = new GoalData
            {
                Instructions = StrList(g, "Instructions"),
                Delay = g.TryGetProperty("Delay", out var d) ? d.GetSingle() : 0f,
                Actions = StrList(g, "Actions")
            };
            if (g.TryGetProperty("Init", out var init))
            {
                goal.Init = new InitData
                {
                    ClearFlags = StrList(init, "ClearFlags"),
                    CompletionNotice = init.TryGetProperty("CompletionNotice", out var cn)
                        ? cn.GetString() : null
                };
            }
            if (g.TryGetProperty("Require", out var req))
            {
                goal.Require = new RequireData
                {
                    Flags = StrList(req, "Flags"),
                    TechsDealtAny = StrList(req, "TechsDealtAny"),
                    TechsResearchedAny = StrList(req, "TechsResearchedAny"),
                    CounterKey = req.TryGetProperty("Counter", out var ctr) && ctr.TryGetProperty("Key", out var ck)
                        ? ck.GetString() : null,
                    CounterMin = req.TryGetProperty("Counter", out var ctr2) && ctr2.TryGetProperty("Min", out var cm)
                        ? cm.GetInt32() : 0,
                    HouseSetEmpty = req.TryGetProperty("HouseSetEmpty", out var hse) && hse.GetBoolean(),
                    AttackersRepelled = req.TryGetProperty("AttackersRepelled", out var ar) && ar.GetBoolean()
                };
            }
            if (g.TryGetProperty("On", out var on) && on.ValueKind == JsonValueKind.Array)
                foreach (var b in on.EnumerateArray())
                    goal.On.Add(ParseBinding(b));
            return goal;
        }

        private static BindingData ParseBinding(JsonElement b)
        {
            return new BindingData
            {
                Event = Str(b, "Event") ?? throw new InvalidDataException("教程绑定缺 Event"),
                SetFlag = Str(b, "SetFlag"),
                Warn = Str(b, "Warn"),
                WarnAlways = Bool(b, "WarnAlways"),
                Command = Str(b, "Command"),
                ResourceSpecific = Str(b, "ResourceSpecific"),
                ResourceGeneric = Str(b, "ResourceGeneric"),
                TargetClass = Str(b, "TargetClass"),
                TemplateContains = Str(b, "TemplateContains"),
                RallyCommand = Str(b, "RallyCommand"),
                RallyResource = Str(b, "RallyResource"),
                Template = Str(b, "Template"),
                MinCount = b.TryGetProperty("MinCount", out var mc) ? mc.GetInt32() : null,
                ExactCount = b.TryGetProperty("ExactCount", out var ec) ? ec.GetInt32() : null,
                Increment = Str(b, "Increment"),
                ResetQueueOnMismatch = Bool(b, "ResetQueueOnMismatch"),
                WarnOnCount = Str(b, "WarnOnCount"),
                WarnOnTemplate = Str(b, "WarnOnTemplate"),
                Class = Str(b, "Class"),
                DropsiteAccepts = Str(b, "DropsiteAccepts"),
                ResearcherClass = Str(b, "ResearcherClass"),
                Tech = Str(b, "Tech"),
                From = Str(b, "From"),
                To = Str(b, "To"),
                TrackHouses = Bool(b, "TrackHouses")
            };
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static List<string> StrList(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();

        // ── 装配 ──

        public static List<TutorialGoal> Assemble(TutorialLevelData level) =>
            level.Goals.Select(AssembleGoal).ToList();

        public static TutorialEngine CreateEngine(ComponentManager sim, SimEventBus events, string path)
        {
            var engine = new TutorialEngine(Assemble(Load(path)));
            engine.Init(sim, events);
            return engine;
        }

        private static TutorialGoal AssembleGoal(GoalData spec)
        {
            foreach (var b in spec.On)
                ValidateEvent(b.Event);
            foreach (var a in spec.Actions)
                ValidateAction(a);

            var goal = new TutorialGoal { Instructions = spec.Instructions, Delay = spec.Delay };
            bool trackHouses = spec.On.Any(b => b.TrackHouses);

            if (spec.Init != null || spec.Require?.CounterKey != null || trackHouses)
            {
                goal.Init = ctx =>
                {
                    if (spec.Init != null)
                        foreach (var f in spec.Init.ClearFlags)
                            ctx.State[f] = false;
                    if (spec.Require?.CounterKey != null)
                        ctx.State[spec.Require.CounterKey] = 0;
                    if (trackHouses)
                    {
                        HouseGoal(ctx).Clear();
                        ctx.State[HouseCountKey] = 0;
                    }
                    if (spec.Init?.CompletionNotice != null)
                        ctx.Events.RaiseTutorialMessage(new TutorialNotification
                        {
                            Instructions = { spec.Init.CompletionNotice },
                            ReadyButton = true,
                            Leave = true
                        });
                };
            }

            if (spec.Require != null)
                goal.IsDone = BuildIsDone(spec);

            // 完成动作:有 Require 的目标包在 IsDone 首次为真时触发;无 Require 的
            // 在绑定命中翻页前触发。fired 闭包防 Recheck 重复触发。
            bool actionsFired = false;
            void RunActionsOnce(TutorialGoalContext ctx)
            {
                if (actionsFired || spec.Actions.Count == 0) return;
                actionsFired = true;
                foreach (var a in spec.Actions)
                    RunAction(ctx, a);
            }
            if (spec.Require != null && spec.Actions.Count > 0)
            {
                var inner = goal.IsDone!;
                goal.IsDone = ctx =>
                {
                    if (!inner(ctx)) return false;
                    RunActionsOnce(ctx);
                    return true;
                };
            }

            // 绑定命中:副作用(SetFlag/Increment)后,纯条件绑定在无 Require 时直接翻页。
            // 返回 true = 已翻页,调用方停止评估后续绑定(防一次事件连跳两页)。
            bool OnMatch(TutorialGoalContext ctx, BindingData b)
            {
                bool sideEffect = b.SetFlag != null || b.Increment != null;
                if (b.SetFlag != null) ctx.State[b.SetFlag] = true;
                if (b.Increment != null) ctx.State[b.Increment] = Counter(ctx, b.Increment) + 1;
                if (sideEffect || spec.Require != null) return false;
                RunActionsOnce(ctx);
                ctx.Engine?.AdvanceGoal();
                return true;
            }

            var byEvent = spec.On.GroupBy(b => b.Event)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            if (byEvent.TryGetValue("playerCommand", out var pc))
                goal.OnPlayerCommand = (ctx, msg) =>
                {
                    foreach (var b in pc)
                        if (HandlePlayerCommand(ctx, b, msg, OnMatch)) break;
                };
            if (byEvent.TryGetValue("trainingQueued", out var tq))
                goal.OnTrainingQueued = (ctx, msg) =>
                {
                    foreach (var b in tq)
                        if (HandleTrainingQueued(ctx, b, msg, OnMatch)) break;
                };
            if (byEvent.TryGetValue("trainingFinished", out var tf))
                goal.OnTrainingFinished = (ctx, msg) =>
                {
                    foreach (var b in tf)
                        if (OnMatch(ctx, b)) break;
                };
            if (byEvent.TryGetValue("structureBuilt", out var sb))
                goal.OnStructureBuilt = (ctx, msg) =>
                {
                    foreach (var b in sb)
                        if (MatchesStructureBuilt(ctx, b, msg) && OnMatch(ctx, b)) break;
                };
            if (byEvent.TryGetValue("researchQueued", out var rq))
                goal.OnResearchQueued = (ctx, msg) =>
                {
                    foreach (var b in rq)
                        if (MatchesResearchQueued(ctx, b, msg) && OnMatch(ctx, b)) break;
                };
            if (byEvent.TryGetValue("researchFinished", out var rf))
                goal.OnResearchFinished = (ctx, msg) =>
                {
                    foreach (var b in rf)
                        if ((b.Tech == null || msg.Tech == b.Tech) && OnMatch(ctx, b)) break;
                };
            if (byEvent.TryGetValue("ownershipChanged", out var oc))
                goal.OnOwnershipChanged = (ctx, msg) =>
                {
                    foreach (var b in oc)
                    {
                        if (b.TrackHouses) { TrackHouse(ctx, msg); continue; }
                        if (MatchesOwnershipChanged(ctx, b, msg) && OnMatch(ctx, b)) break;
                    }
                };
            return goal;
        }

        private static Func<TutorialGoalContext, bool> BuildIsDone(GoalData spec)
        {
            var r = spec.Require!;
            return ctx =>
            {
                foreach (var f in r.Flags)
                    if (!Flag(ctx, f)) return false;
                if (r.TechsDealtAny.Count > 0 && !r.TechsDealtAny.Any(t =>
                        Triggers.TriggerHelper.HasDealtWithTech(ctx.Sim, ctx.PlayerId, t)))
                    return false;
                if (r.TechsResearchedAny.Count > 0 && !r.TechsResearchedAny.Any(t => IsTechResearched(ctx, t)))
                    return false;
                if (r.CounterKey != null && Counter(ctx, r.CounterKey) < r.CounterMin)
                    return false;
                if (r.HouseSetEmpty && HouseGoal(ctx).Count != 0)
                    return false;
                if (r.AttackersRepelled && !IsAttackRepelled(ctx))
                    return false;
                return true;
            };
        }

        // ── 绑定分派 ──

        private static bool HandlePlayerCommand(TutorialGoalContext ctx, BindingData b,
            PlayerCommandEvent msg, Func<TutorialGoalContext, BindingData, bool> onMatch)
        {
            if (MatchesPlayerCommand(ctx, b, msg)) return onMatch(ctx, b);
            if (b.Warn == null) return false;
            // 警告范围:Command 匹配,且(WarnAlways 或 rally 指令匹配)——原版 introductory
            // 集结点只在 command==gather 但资源不对时警告;econ 关任何误操作都警告。
            bool inScope = (b.Command == null || msg.Type == b.Command)
                && (b.WarnAlways || b.RallyCommand == null
                    || (msg.Data.TryGetValue("command", out var c) && c as string == b.RallyCommand));
            if (inScope) Warning(ctx, b.Warn);
            return false;
        }

        private static bool MatchesPlayerCommand(TutorialGoalContext ctx, BindingData b, PlayerCommandEvent msg)
        {
            if (b.Command != null && msg.Type != b.Command) return false;
            if (b.ResourceSpecific != null && !(msg.Target.HasValue
                    && ResourceSpecific(ctx, msg.Target.Value) == b.ResourceSpecific)) return false;
            if (b.ResourceGeneric != null && !(msg.Target.HasValue
                    && ResourceGeneric(ctx, msg.Target.Value) == b.ResourceGeneric)) return false;
            if (b.TargetClass != null && !(msg.Target.HasValue
                    && EntityMatches(ctx, msg.Target.Value, b.TargetClass))) return false;
            if (b.TemplateContains != null && !(msg.Data.TryGetValue("template", out var t)
                    && t is string ts && ts.Contains(b.TemplateContains, StringComparison.Ordinal))) return false;
            if (b.RallyCommand != null && !(msg.Data.TryGetValue("command", out var c)
                    && c as string == b.RallyCommand)) return false;
            if (b.RallyResource != null)
            {
                msg.Data.TryGetValue("specific", out var s1);
                msg.Data.TryGetValue("resourceType", out var s2);
                if ((s1 as string ?? s2 as string) != b.RallyResource) return false;
            }
            return true;
        }

        private static bool HandleTrainingQueued(TutorialGoalContext ctx, BindingData b,
            TrainingQueuedEvent msg, Func<TutorialGoalContext, BindingData, bool> onMatch)
        {
            bool templateOk = b.Template != null
                ? msg.UnitTemplate == b.Template
                : b.TemplateContains == null || msg.UnitTemplate.Contains(b.TemplateContains, StringComparison.Ordinal);
            bool countOk = (!b.MinCount.HasValue || msg.Count >= b.MinCount.Value)
                && (!b.ExactCount.HasValue || msg.Count == b.ExactCount.Value);
            if (templateOk && countOk) return onMatch(ctx, b);

            string? warn = !countOk ? b.WarnOnCount ?? b.Warn : b.WarnOnTemplate ?? b.Warn;
            if (warn != null)
            {
                if (b.ResetQueueOnMismatch) ResetQueue(ctx, msg.TrainerEntity);
                Warning(ctx, warn);
            }
            return false;
        }

        private static bool MatchesStructureBuilt(TutorialGoalContext ctx, BindingData b, StructureBuiltEvent msg)
        {
            if (b.Class != null && !EntityMatches(ctx, msg.Building, b.Class)) return false;
            if (b.DropsiteAccepts != null)
            {
                var ds = ctx.Sim.QueryInterface<ResourceDropsite>(msg.Building);
                if (ds == null || !ds.Accepts(Enum.Parse<ResourceType>(b.DropsiteAccepts, true)))
                    return false;
            }
            return b.Class != null || b.DropsiteAccepts != null;
        }

        private static bool MatchesResearchQueued(TutorialGoalContext ctx, BindingData b, ResearchQueuedEvent msg)
        {
            if (string.IsNullOrEmpty(msg.TechnologyTemplate)) return false;
            return b.ResearcherClass == null || EntityMatches(ctx, msg.ResearcherEntity, b.ResearcherClass);
        }

        private static bool MatchesOwnershipChanged(TutorialGoalContext ctx, BindingData b, OwnershipChangedEvent msg)
        {
            if (b.From != null && msg.From != ParsePlayerRef(ctx, b.From)) return false;
            if (b.To != null && msg.To != ParsePlayerRef(ctx, b.To)) return false;
            if (b.TargetClass != null && !EntityMatches(ctx, msg.Entity, b.TargetClass)) return false;
            return b.From != null || b.To != null || b.TargetClass != null;
        }

        /// <summary>"enemy"→敌方玩家号,"self"→本玩家,"gaia"→-1,其余按数字解析。</summary>
        private static int ParsePlayerRef(TutorialGoalContext ctx, string s) => s switch
        {
            "enemy" => ctx.EnemyId,
            "self" => ctx.PlayerId,
            "gaia" => -1,
            _ => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
        };

        /// <summary>房屋地基追踪(原版经济关 houseGoal/houseCount):地基归属玩家→入集计数;
        /// 易主/销毁时出集,进度&lt;1 回退计数。内务绑定,自身不推进目标。</summary>
        private static void TrackHouse(TutorialGoalContext ctx, OwnershipChangedEvent msg)
        {
            if (msg.From >= 0 && HouseGoal(ctx).Contains(msg.Entity))
            {
                HouseGoal(ctx).Remove(msg.Entity);
                var f = ctx.Sim.QueryInterface<FoundationComponent>(msg.Entity);
                if (f != null && f.Progress < 1f)
                    ctx.State[HouseCountKey] = Counter(ctx, HouseCountKey) - 1;
            }
            else if (msg.From < 0 && msg.To == ctx.PlayerId
                && ctx.Sim.QueryInterface<FoundationComponent>(msg.Entity) != null
                && IsClass(ctx, msg.Entity, "House"))
            {
                HouseGoal(ctx).Add(msg.Entity);
                ctx.State[HouseCountKey] = Counter(ctx, HouseCountKey) + 1;
            }
        }

        // ── 命名动作 ──

        private static void RunAction(TutorialGoalContext ctx, string action)
        {
            switch (action)
            {
                case "launchAttack": LaunchAttack(ctx); break;
                case "removeChampions": RemoveChampions(ctx); break;
                default: throw new InvalidDataException($"未知教程动作: {action}");
            }
        }

        /// <summary>敌方 CitizenSoldier 全体进攻玩家的 Tower(优先)/CivilCentre;
        /// 进攻者记入 ctx.Attackers 供 AttackersRepelled 复查(原版 launchAttack)。</summary>
        private static void LaunchAttack(TutorialGoalContext ctx)
        {
            EntityId? target = null;
            foreach (var eid in ctx.Sim.AllEntities)
            {
                var identity = ctx.Sim.QueryInterface<IdentityComponent>(eid);
                var owner = ctx.Sim.QueryInterface<OwnershipComponent>(eid);
                if (identity == null || owner == null || owner.PlayerId != ctx.PlayerId) continue;
                if (identity.MatchesClassList("Tower") || identity.MatchesClassList("CivilCentre"))
                {
                    target = eid;
                    if (identity.MatchesClassList("Tower")) break;
                }
            }

            ctx.Attackers.Clear();
            foreach (var eid in ctx.Sim.AllEntities)
            {
                var identity = ctx.Sim.QueryInterface<IdentityComponent>(eid);
                var owner = ctx.Sim.QueryInterface<OwnershipComponent>(eid);
                var attack = ctx.Sim.QueryInterface<AttackComponent>(eid);
                if (identity == null || owner == null || attack == null) continue;
                if (owner.PlayerId == ctx.EnemyId && identity.HasClass("CitizenSoldier"))
                    ctx.Attackers.Add(eid);
            }

            if (!target.HasValue) return;
            var pos = ctx.Sim.QueryInterface<PositionComponent>(target.Value);
            if (pos == null) return;
            foreach (var attacker in ctx.Attackers)
            {
                var atk = ctx.Sim.QueryInterface<AttackComponent>(attacker);
                if (atk != null)
                {
                    atk.AttackTarget(ctx.Sim, target.Value);
                    continue;
                }
                var motion = ctx.Sim.QueryInterface<UnitMotion>(attacker);
                motion?.MoveToPoint(new Maths.FixedVector2D(pos.Position.X, pos.Position.Z));
            }
        }

        /// <summary>敌方 Champion 只留 6 个,其余斩杀(原版 ram 目标完成的连带脚本)。</summary>
        private static void RemoveChampions(TutorialGoalContext ctx)
        {
            int keep = 6;
            foreach (var eid in ctx.Sim.AllEntities)
            {
                var identity = ctx.Sim.QueryInterface<IdentityComponent>(eid);
                var owner = ctx.Sim.QueryInterface<OwnershipComponent>(eid);
                if (identity == null || owner == null || owner.PlayerId != ctx.EnemyId) continue;
                if (!identity.HasClass("Champion")) continue;
                var health = ctx.Sim.QueryInterface<HealthComponent>(eid);
                if (health == null)
                    ctx.Sim.DestroyEntity(eid);
                else if (--keep < 0)
                    health.Current = 0;
            }
        }

        private static bool IsAttackRepelled(TutorialGoalContext ctx)
        {
            foreach (var eid in ctx.Attackers)
            {
                var health = ctx.Sim.QueryInterface<HealthComponent>(eid);
                if (health != null && !health.IsDead)
                    return false;
            }
            return ctx.Attackers.Count > 0;
        }

        // ── 状态与组件辅助 ──

        private static void ValidateEvent(string ev)
        {
            switch (ev)
            {
                case "playerCommand":
                case "trainingQueued":
                case "trainingFinished":
                case "structureBuilt":
                case "researchQueued":
                case "researchFinished":
                case "ownershipChanged":
                    break;
                default:
                    throw new InvalidDataException($"未知教程事件类型: {ev}");
            }
        }

        private static void ValidateAction(string action)
        {
            if (action != "launchAttack" && action != "removeChampions")
                throw new InvalidDataException($"未知教程动作: {action}");
        }

        private static bool Flag(TutorialGoalContext ctx, string key) =>
            ctx.State.TryGetValue(key, out var v) && v is true;

        private static int Counter(TutorialGoalContext ctx, string key) =>
            ctx.State.TryGetValue(key, out var v) && v is int i ? i : 0;

        private static HashSet<EntityId> HouseGoal(TutorialGoalContext ctx)
        {
            if (!ctx.State.TryGetValue(HouseGoalKey, out var v) || v is not HashSet<EntityId> set)
                ctx.State[HouseGoalKey] = set = new HashSet<EntityId>();
            return set;
        }

        private static void Warning(TutorialGoalContext ctx, string text) =>
            ctx.Engine?.WarningMessage(text);

        private static bool EntityMatches(TutorialGoalContext ctx, EntityId entity, string className)
        {
            var identity = ctx.Sim.QueryInterface<IdentityComponent>(entity);
            return identity != null && identity.MatchesClassList(className);
        }

        private static bool IsClass(TutorialGoalContext ctx, EntityId ent, string cls) =>
            ctx.Sim.QueryInterface<IdentityComponent>(ent)?.HasClass(cls) ?? false;

        private static string? ResourceSpecific(TutorialGoalContext ctx, EntityId target) =>
            ctx.Sim.QueryInterface<ResourceSupply>(target)?.SpecificType;

        private static string? ResourceGeneric(TutorialGoalContext ctx, EntityId target) =>
            ctx.Sim.QueryInterface<ResourceSupply>(target)?.GenericType;

        private static void ResetQueue(TutorialGoalContext ctx, EntityId trainer) =>
            ctx.Sim.QueryInterface<ProductionQueue>(trainer)?.ResetQueue();

        private static bool IsTechResearched(TutorialGoalContext ctx, string tech)
        {
            var pEnt = ctx.Sim.Players.GetPlayerEntityId(ctx.PlayerId);
            var tm = pEnt.HasValue ? ctx.Sim.QueryInterface<TechnologyManager>(pEnt.Value) : null;
            return tm != null && tm.IsResearched(tech);
        }
    }
}
