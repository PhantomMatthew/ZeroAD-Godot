using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim.Tutorial;

/// <summary>经济演练教程(原版 maps/tutorials/starting_economy_walkthrough.js,482 行)的
/// C# 移植。26 个目标逐条对应原版 tutorialGoals;事件条件(OnPlayerCommand gather
/// fruit/tree/meat、construct storehouse/farmstead/field/barracks、set/unset-rallypoint、
/// TrainingQueued 批量校验、ResearchQueued/Finished、OwnershipChanged 房屋计数)
/// 全部走 TutorialGoal 的内置钩子。
/// 与 introductory 的差异:本关状态机用 context.State 字典承载(houseGoal/femaleCount/
/// stone/metal/trainingDone)。警告文案走 Engine.WarningMessage(原版 WarningMessage)。</summary>
public static class EconomyWalkthroughTutorial
{
    public const string MapName = "starting_economy_walkthrough";

    public static TutorialEngine Create(ComponentManager sim, SimEventBus events)
    {
        var engine = new TutorialEngine(BuildGoals());
        engine.Init(sim, events);
        return engine;
    }

    // ── 状态辅助(context.State 字典承载)──
    private static bool Flag(TutorialGoalContext ctx, string key) =>
        ctx.State.TryGetValue(key, out var v) && v is true;
    private static void SetFlag(TutorialGoalContext ctx, string key, bool v = true) =>
        ctx.State[key] = v;
    private static HashSet<EntityId> HouseGoal(TutorialGoalContext ctx)
    {
        if (!ctx.State.TryGetValue("houseGoal", out var v) || v is not HashSet<EntityId> set)
            ctx.State["houseGoal"] = set = new HashSet<EntityId>();
        return set;
    }
    private static int Counter(TutorialGoalContext ctx, string key) =>
        ctx.State.TryGetValue(key, out var v) && v is int i ? i : 0;

    private static string? ResourceSpecific(TutorialGoalContext ctx, EntityId target) =>
        ctx.Sim.QueryInterface<ResourceSupply>(target)?.SpecificType;
    private static string? ResourceGeneric(TutorialGoalContext ctx, EntityId target) =>
        ctx.Sim.QueryInterface<ResourceSupply>(target)?.GenericType;
    private static bool IsClass(TutorialGoalContext ctx, EntityId ent, string cls) =>
        ctx.Sim.QueryInterface<IdentityComponent>(ent)?.HasClass(cls) ?? false;
    private static bool HasDealtWithTech(TutorialGoalContext ctx, string tech) =>
        Triggers.TriggerHelper.HasDealtWithTech(ctx.Sim, ctx.PlayerId, tech);

    public static List<TutorialGoal> BuildGoals() => new()
    {
        // 0) 欢迎+操作说明(无完成条件——玩家点"继续"翻页;Delay=-1 由面板 Ready 钮驱动)
        new()
        {
            Instructions =
            {
                "This tutorial will teach the basics of developing your economy. You start with a Civic Center and a couple units in Village Phase; your goal is to develop and expand, evolving to Town Phase and City Phase.\n",
                "Toggle fullscreen with Alt+Enter; zoom with the mouse wheel; move the camera with the arrow keys.",
            },
        },
        // 1) 选中市政中心
        new() { Instructions = { "To start off, select your building, the Civic Center, by clicking on it. A selection ring in your player color will be displayed." } },
        // 2) 生产面板说明
        new()
        {
            Instructions =
            {
                "With the Civic Center selected, the production panel appears at the lower right. Available actions are unmasked; gray = not unlocked; red = insufficient resources. Hover icons for tooltips.\n",
                "The top row trains units; the bottom rows research technologies. The II icon advances to Town Phase — it requires more structures plus food and wood.",
            },
        },
        // 3) 单位类型说明
        new()
        {
            Instructions =
            {
                "Two main starting unit types: Civilians (pure economy) and Citizen Soldiers (workers that can fight). Civilians and Infantry gather all land resources; Cavalry can only gather meat from animals.\n",
            },
        },
        // 4) 左右键说明
        new() { Instructions = { "Rule of thumb: left-click selects; right-click with a selection issues an order (gather, build, fight…).\n" } },
        // 5) 采集果子(berr於 southeast)→ OnPlayerCommand gather fruit
        new()
        {
            Instructions =
            {
                "Food and wood matter most early. Civilians gather vegetables fastest. Select all your Civilians (drag a rectangle / click then Shift+click / double-click for same-type) and right-click the berries southeast of the Civic Center.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "gather" && msg.Target.HasValue
                    && ResourceSpecific(ctx, msg.Target.Value) == "fruit")
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 6) 步兵伐木 → gather tree
        new()
        {
            Instructions = { "Now gather wood with your Infantry Citizen Soldiers: select them and right-click the nearest tree." },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "gather" && msg.Target.HasValue
                    && ResourceSpecific(ctx, msg.Target.Value) == "tree")
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 7) 骑兵猎鸡 → gather meat
        new()
        {
            Instructions = { "Cavalry Citizen Soldiers are good hunters. Select your Cavalry and order it to slaughter the chickens around your Civic Center." },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "gather" && msg.Target.HasValue
                    && ResourceSpecific(ctx, msg.Target.Value) == "meat")
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 8) 设集结点到树 → set-rallypoint command=gather resourceType.specific==tree
        new()
        {
            Instructions =
            {
                "Set a rally point so newly trained units go straight to work: select the Civic Center and right-click a tree south of it.\n",
                "Rally points show as a small flag at the end of the blue line.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                bool ok = msg.Type == "set-rallypoint"
                    && msg.Data.TryGetValue("command", out var cmd) && cmd as string == "gather"
                    && msg.Data.TryGetValue("resourceType", out var res) && res as string == "tree";
                if (!ok)
                {
                    ctx.Engine!.WarningMessage("Select the Civic Center, then right-click a tree (cursor turns into a wood icon).");
                    return;
                }
                ctx.Engine!.AdvanceGoal();
            },
        },
        // 9) 批量训练 5 步兵 → TrainingQueued spearman count>1(否则警告+提示)
        new()
        {
            Instructions =
            {
                "Train more units: with the Civic Center selected, hold the batch-train key and click the second unit icon (Hoplites) to train five at once — cheaper than five single clicks.\n",
            },
            OnTrainingQueued = (ctx, msg) =>
            {
                if (!msg.UnitTemplate.Contains("infantry_spearman") || msg.Count == 1)
                {
                    ctx.Engine!.WarningMessage(msg.Count == 1
                        ? "Hold the batch hotkey while clicking to train several units."
                        : "The second icon trains the Hoplites.");
                    return;
                }
                ctx.Engine!.AdvanceGoal();
            },
        },
        // 10) 等训完 + 资源栏说明 → OnTrainingFinished
        new()
        {
            Instructions =
            {
                "Wait for the units to be trained.\n",
                "Watch the top-left resource counters: resources only count once workers deposit them at the Civic Center or another dropsite — minimize the walk distance for efficiency.",
            },
            OnTrainingFinished = (ctx, msg) => ctx.Engine!.AdvanceGoal(),
        },
        // 11) 建 Storehouse → construct structures/{civ}/storehouse
        new()
        {
            Instructions =
            {
                "New units gather wood automatically, but the walk back is long. Build a Storehouse (wood/stone/metal dropsite) near the trees: select your five Citizen Soldiers, click the Storehouse icon, and place it by the trees.\n",
                "Invalid (obstructed) spots show the building preview in red.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "construct"
                    && msg.Data.TryGetValue("template", out var t)
                    && t is string ts && ts.Contains("/storehouse"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 12) 等建成(接受木头的投放站)→ OnStructureBuilt dropsite wood
        new()
        {
            Instructions = { "The selected Citizens start constructing automatically once the foundation is placed." },
            OnStructureBuilt = (ctx, msg) =>
            {
                var dropsite = ctx.Sim.QueryInterface<ResourceDropsite>(msg.Building);
                if (dropsite != null && dropsite.Accepts(ResourceType.Wood))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 13) 取消集结点 → unset-rallypoint
        new()
        {
            Instructions =
            {
                "Builders now gather wood automatically. We have enough woodcutters — remove the rally point: right-click on the selected Civic Center (the flag icon shows crossed out).",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "unset-rallypoint")
                    ctx.Engine!.AdvanceGoal();
            },
            OnTrainingFinished = (ctx, msg) => SetFlag(ctx, "trainingDone"),
        },
        // 14) 批量训 5 平民 → TrainingQueued support_civilian count>1
        new()
        {
            Instructions = { "Train Civilians for more food: select the Civic Center, hold the batch key, and click the Civilian icon to train five." },
            Init = ctx => SetFlag(ctx, "trainingDone", false),
            OnTrainingQueued = (ctx, msg) =>
            {
                if (!msg.UnitTemplate.Contains("support_civilian") || msg.Count == 1)
                {
                    ctx.Engine!.WarningMessage(msg.Count == 1
                        ? "Hold the batch hotkey and click to train several units."
                        : "The first icon trains the Civilians.");
                    return;
                }
                ctx.Engine!.AdvanceGoal();
            },
        },
        // 15) 等训完 + 人口说明(IsDone trainingDone;训完也翻页)
        new()
        {
            Instructions =
            {
                "The units will be ready soon.\n",
                "Watch the population counter (fifth item top-left): current population (including units in training) vs limit, which your structures set.",
            },
            IsDone = ctx => Flag(ctx, "trainingDone"),
            OnTrainingFinished = (ctx, msg) => ctx.Engine!.AdvanceGoal(),
        },
        // 16) 建房说明(无事件——翻页)
        new()
        {
            Instructions =
            {
                "Near the population cap you must raise it with new structures — the House is cheapest.\n",
                "Let's build several Houses in a row.",
            },
        },
        // 17) 建两栋房(队列指令说明)→ houseCount>1(IsDone)
        new()
        {
            Instructions =
            {
                "Select two newly-trained Civilians and build Houses east of the Civic Center: click the House icon, then hold the queue key while clicking several spots — units work through queued orders in order. Press Escape to drop the House cursor.\n",
                "Reminder: click the first Civilian, then hold the add key and click the second.",
            },
            Init = ctx => { HouseGoal(ctx).Clear(); ctx.State["houseCount"] = 0; },
            IsDone = ctx => Counter(ctx, "houseCount") > 1,
            OnOwnershipChanged = (ctx, msg) =>
            {
                // 原版:地基归属玩家 → 入目标集并计数;易主/销毁(进度<1)→ 回退。
                if (msg.From >= 0 && HouseGoal(ctx).Contains(msg.Entity))
                {
                    HouseGoal(ctx).Remove(msg.Entity);
                    var f = ctx.Sim.QueryInterface<FoundationComponent>(msg.Entity);
                    if (f != null && f.Progress < 1f)
                        ctx.State["houseCount"] = Counter(ctx, "houseCount") - 1;
                }
                else if (msg.From < 0 && msg.To == ctx.PlayerId
                    && ctx.Sim.QueryInterface<FoundationComponent>(msg.Entity) != null
                    && IsClass(ctx, msg.Entity, "House"))
                {
                    HouseGoal(ctx).Add(msg.Entity);
                    ctx.State["houseCount"] = Counter(ctx, "houseCount") + 1;
                    if (HouseGoal(ctx).Count > 1) ctx.Engine!.AdvanceGoal();
                }
            },
        },
        // 18) 农田说明(原版 delay=-1 手动翻页)
        new()
        {
            Instructions =
            {
                "Berries are finite — Fields give unlimited food but gather slower.\n",
                "First build a Farmstead (a food dropsite) to minimize the walk.",
            },
            OnOwnershipChanged = (ctx, msg) =>
            {
                if (HouseGoal(ctx).Contains(msg.Entity))
                    HouseGoal(ctx).Remove(msg.Entity);
            },
        },
        // 19) 建 Farmstead → construct farmstead
        new()
        {
            Instructions =
            {
                "Select the three remaining idle Civilians and build a Farmstead in the open area west of the Civic Center.\n",
                "Leave room for Fields around it. Goats to the west can improve food income if hunted.\n",
                "Tip: hold the idle-only key while drag-selecting to pick just the idle units.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "construct" && msg.Data.TryGetValue("template", out var t)
                    && t is string ts && ts.Contains("/farmstead"))
                    ctx.Engine!.AdvanceGoal();
            },
            OnOwnershipChanged = (ctx, msg) =>
            {
                if (HouseGoal(ctx).Contains(msg.Entity))
                    HouseGoal(ctx).Remove(msg.Entity);
            },
        },
        // 20) 等两栋房建完(houseGoal 空)
        new()
        {
            Instructions =
            {
                "Finished Farmstead builders will seek the goats automatically.\n",
                "House builders idle once done — wait for them to finish both Houses.",
            },
            IsDone = ctx => HouseGoal(ctx).Count == 0,
            OnOwnershipChanged = (ctx, msg) =>
            {
                if (HouseGoal(ctx).Contains(msg.Entity))
                    HouseGoal(ctx).Remove(msg.Entity);
                if (HouseGoal(ctx).Count == 0) ctx.Engine!.AdvanceGoal();
            },
        },
        // 21) 建 Field → construct field
        new()
        {
            Instructions = { "With both Houses up, select your two Civilians and build a Field as close as possible to the Farmstead (it accepts all food)." },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "construct" && msg.Data.TryGetValue("template", out var t)
                    && t is string ts && ts.Contains("/field"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 22) 骑兵出猎骆驼 → gather meat
        new()
        {
            Instructions =
            {
                "Field builders will gather it automatically once done.\n",
                "The cavalry should be done with the chickens — take it south past the Civic Center to the lake and right-click a camel to hunt.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "gather" && msg.Target.HasValue
                    && ResourceSpecific(ctx, msg.Target.Value) == "meat")
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 23) 集结点到田(build Field 地基 或 gather grain)
        new()
        {
            Instructions = { "Up to five Workers per Field. Set the Civic Center rally point on the Field: new Workers will help build it, then gather it." },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type != "set-rallypoint") return;
                msg.Data.TryGetValue("command", out var cmdObj);
                string? cmd = cmdObj as string;
                if (cmd == "build")
                {
                    // 原版校验 target 是 Field 类地基——我们没存 target 类,简化认 build。
                    ctx.Engine!.AdvanceGoal();
                    return;
                }
                if (cmd == "gather"
                    && msg.Data.TryGetValue("resourceType", out var res) && res as string == "grain")
                {
                    ctx.Engine!.AdvanceGoal();
                    return;
                }
                ctx.Engine!.WarningMessage("Select the Civic Center and right-click on the Field.");
            },
        },
        // 24) 单点三次训平民 → TrainingQueued support_civilian count==1 ×3
        new()
        {
            Instructions = { "Click the Civilian icon three times to train three more farmers." },
            Init = ctx => ctx.State["femaleCount"] = 0,
            OnTrainingQueued = (ctx, msg) =>
            {
                if (!msg.UnitTemplate.Contains("support_civilian") || msg.Count != 1)
                {
                    ctx.Engine!.WarningMessage(msg.Count != 1
                        ? "Click without the batch key to train a single unit."
                        : "Click on the Civilian icon.");
                    return;
                }
                int n = Counter(ctx, "femaleCount") + 1;
                ctx.State["femaleCount"] = n;
                if (n == 3) ctx.Engine!.AdvanceGoal();
            },
        },
        // 25) 研究采集科技 → ResearchQueued 于 Farmstead(或 IsDone 已研)
        new()
        {
            Instructions =
            {
                "Gather rates improve with technologies. Select the Farmstead: its production panel lists researchable technologies — hover for costs/effects and pick one.",
            },
            IsDone = ctx => HasDealtWithTech(ctx, "gather_wicker_baskets")
                || HasDealtWithTech(ctx, "gather_farming_plows"),
            OnResearchQueued = (ctx, msg) =>
            {
                if (IsClass(ctx, msg.ResearcherEntity, "Farmstead"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 26) 建 Barracks → construct barracks
        new()
        {
            Instructions =
            {
                "Prepare for Town Phase: hover its icon in the Civic Center panel to see what's missing.\n",
                "Resources suffice but a structure is missing — and defense never hurts. Select four soldiers and build a Barracks near the Civic Center.",
            },
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "construct" && msg.Data.TryGetValue("template", out var t)
                    && t is string ts && ts.Contains("/barracks"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 27) 等建成 → StructureBuilt Barracks
        new()
        {
            Instructions = { "While the Barracks builds, rally two more builders onto its foundation (right-click it; hammer cursor) and train two more Hoplites." },
            OnStructureBuilt = (ctx, msg) =>
            {
                if (IsClass(ctx, msg.Building, "Barracks"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 28) 研究 Town Phase → ResearchQueued 于 CivilCentre(或 IsDone 已研)
        new()
        {
            Instructions =
            {
                "You can now research Town Phase: select the Civic Center and click the technology icon.\n",
                "If the icon shows red, wait for your workers to gather the missing resources.",
            },
            IsDone = ctx => HasDealtWithTech(ctx, "phase_town_athen")
                || HasDealtWithTech(ctx, "phase_town_generic"),
            OnResearchQueued = (ctx, msg) =>
            {
                if (IsClass(ctx, msg.ResearcherEntity, "CivilCentre"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 29) 石/金属采集说明(翻页)
        new()
        {
            Instructions =
            {
                "Later phases need stone and metal for bigger structures and better soldiers.\n",
                "While researching, send half your idle Citizen Soldiers to the stone quarry and half to the metal mine west of the Civic Center.",
            },
        },
        // 30) 队列指令(先回送再采)+ 双资源 + Town 完成 → 全齐翻页
        new()
        {
            Instructions =
            {
                "Soldiers carrying wood would lose it when switching resources — queue orders: hold the queue key, right-click the Civic Center to deposit, then right-click the quarry.\n",
                "Repeat with the remaining soldiers and the metal mine.",
            },
            Init = ctx => { SetFlag(ctx, "stone", false); SetFlag(ctx, "metal", false); },
            IsDone = ctx => Flag(ctx, "stone") && Flag(ctx, "metal")
                && (HasDealtWithTech(ctx, "phase_town_athen")
                    || HasDealtWithTech(ctx, "phase_town_generic")),
            OnPlayerCommand = (ctx, msg) =>
            {
                if (msg.Type == "gather" && msg.Target.HasValue)
                {
                    if (ResourceGeneric(ctx, msg.Target.Value) == "stone") SetFlag(ctx, "stone");
                    else if (ResourceGeneric(ctx, msg.Target.Value) == "metal") SetFlag(ctx, "metal");
                }
                if (Flag(ctx, "stone") && Flag(ctx, "metal")
                    && (HasDealtWithTech(ctx, "phase_town_athen")
                        || HasDealtWithTech(ctx, "phase_town_generic")))
                    ctx.Engine!.AdvanceGoal();
            },
            OnResearchFinished = (ctx, msg) =>
            {
                if (Flag(ctx, "stone") && Flag(ctx, "metal"))
                    ctx.Engine!.AdvanceGoal();
            },
        },
        // 31) 完结语
        new()
        {
            Instructions = { "This is the end of the walkthrough. You now know the basics of setting up your economy." },
        },
    };
}
