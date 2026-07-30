using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim.Tutorial
{
    public sealed class TutorialGoalContext
    {
        public TutorialEngine? Engine;
        public ComponentManager Sim = null!;
        public SimEventBus Events = null!;
        public int PlayerId = 1;
        public int EnemyId = 2;
        public bool TrainingDone;
        public bool RallyPointSet;
        public bool TrainingStarted;
        public bool FarmStarted;
        public bool MarketStarted;
        public bool TempleStarted;
        public int RamCount;
        public List<EntityId> Attackers = new();
    }

    public sealed class TutorialGoal
    {
        public List<string> Instructions = new();
        public Action<TutorialGoalContext>? Init;
        public Func<TutorialGoalContext, bool>? IsDone;
        public Action<TutorialGoalContext, PlayerCommandEvent>? OnPlayerCommand;
        public Action<TutorialGoalContext, TrainingQueuedEvent>? OnTrainingQueued;
        public Action<TutorialGoalContext, TrainingFinishedEvent>? OnTrainingFinished;
        public Action<TutorialGoalContext, StructureBuiltEvent>? OnStructureBuilt;
        public Action<TutorialGoalContext, ResearchQueuedEvent>? OnResearchQueued;
        public Action<TutorialGoalContext, ResearchFinishedEvent>? OnResearchFinished;
        public Action<TutorialGoalContext, OwnershipChangedEvent>? OnOwnershipChanged;
        public float Delay;
    }

    public sealed class TutorialEngine
    {
        private readonly List<TutorialGoal> _goals;
        private readonly TutorialGoalContext _ctx = new();
        private int _index;
        private bool _waitingReady;
        private bool _leaveOnReady;

        public bool IsComplete { get; private set; }
        public bool IsActive => _index < _goals.Count && !IsComplete;
        public IReadOnlyList<string> MessageHistory { get; } = new List<string>();

        private readonly List<string> _messageHistory = new();

        public TutorialEngine(IEnumerable<TutorialGoal> goals)
        {
            _goals = goals.ToList();
        }

        public void Init(ComponentManager sim, SimEventBus events, int playerId = 1, int enemyId = 2)
        {
            _ctx.Engine = this;
            _ctx.Sim = sim;
            _ctx.Events = events;
            _ctx.PlayerId = playerId;
            _ctx.EnemyId = enemyId;
            _index = 0;

            events.PlayerCommand += OnPlayerCommand;
            events.TrainingQueued += OnTrainingQueued;
            events.TrainingFinished += OnTrainingFinished;
            events.StructureBuilt += OnStructureBuilt;
            events.ResearchQueued += OnResearchQueued;
            events.ResearchFinished += OnResearchFinished;
            events.OwnershipChanged += OnOwnershipChanged;

            NextGoal();
        }

        public void OnReadyPressed()
        {
            if (_waitingReady)
                NextGoal();
        }

        public void AdvanceGoal() => NextGoal();

        private void NextGoal(bool deserializing = false)
        {
            if (_index >= _goals.Count)
                return;

            var goal = _goals[_index];
            _waitingReady = false;
            _leaveOnReady = false;

            if (!deserializing && goal.Init != null)
                goal.Init(_ctx);

            bool goalAlreadyDone = goal.IsDone?.Invoke(_ctx) ?? false;
            bool needDelay = true;

            if (goal.OnPlayerCommand != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnTrainingQueued != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnTrainingFinished != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnStructureBuilt != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnResearchQueued != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnResearchFinished != null && !goalAlreadyDone) needDelay = false;
            if (goal.OnOwnershipChanged != null && !goalAlreadyDone) needDelay = false;

            if (goal.Delay > 0)
            {
                // Timer-based delay not implemented; show ready button instead.
                _waitingReady = true;
            }
            else if (needDelay)
            {
                _waitingReady = true;
            }

            bool isLast = _index + 1 == _goals.Count;
            GoalMessage(goal.Instructions, _waitingReady, isLast);
            _index++;
        }

        private void GoalMessage(List<string> instructions, bool readyButton, bool leave)
        {
            _waitingReady = readyButton;
            _leaveOnReady = leave;
            foreach (var line in instructions)
                _messageHistory.Add(line);

            _ctx.Events.RaiseTutorialMessage(new TutorialNotification
            {
                Instructions = instructions,
                ReadyButton = readyButton,
                Leave = leave
            });
        }

        public void WarningMessage(string warning)
        {
            _ctx.Events.RaiseTutorialMessage(new TutorialNotification
            {
                Warning = warning
            });
        }

        private void AdvanceIfDone(TutorialGoal goal)
        {
            if (goal.IsDone?.Invoke(_ctx) ?? false)
                NextGoal();
        }

        private TutorialGoal? CurrentGoal =>
            _index > 0 && _index <= _goals.Count ? _goals[_index - 1] : null;

        private void OnPlayerCommand(PlayerCommandEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnPlayerCommand?.Invoke(_ctx, msg);
        }

        private void OnTrainingQueued(TrainingQueuedEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnTrainingQueued == null) return;
            goal.OnTrainingQueued(_ctx, msg);
        }

        private void OnTrainingFinished(TrainingFinishedEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnTrainingFinished == null) return;
            goal.OnTrainingFinished(_ctx, msg);
        }

        private void OnStructureBuilt(StructureBuiltEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnStructureBuilt == null) return;
            goal.OnStructureBuilt(_ctx, msg);
        }

        private void OnResearchQueued(ResearchQueuedEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnResearchQueued == null) return;
            goal.OnResearchQueued(_ctx, msg);
        }

        private void OnResearchFinished(ResearchFinishedEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnResearchFinished == null) return;
            goal.OnResearchFinished(_ctx, msg);
        }

        private void OnOwnershipChanged(OwnershipChangedEvent msg)
        {
            var goal = CurrentGoal;
            if (goal?.OnOwnershipChanged == null) return;
            goal.OnOwnershipChanged(_ctx, msg);
        }

        public void NotifyReadyFromCommand()
        {
            if (_waitingReady)
                NextGoal();
        }

        public bool ShouldQuitOnReady => _leaveOnReady && _waitingReady;

        public string GetFullText() => string.Join("\n", _messageHistory);
    }

    public static class IntroductoryTutorial
    {
        public static TutorialEngine Create(ComponentManager sim, SimEventBus events)
        {
            var engine = new TutorialEngine(BuildGoals());
            engine.Init(sim, events);
            return engine;
        }

        public static List<TutorialGoal> BuildGoals() => new()
        {
            new() { Instructions = { "Welcome to the 0 A.D. tutorial." } },

            new()
            {
                Instructions = { "Left-click on a Civilian and then right-click on a berry bush to make that Civilian gather food. Civilians gather vegetables faster than other units." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "gather" && msg.Target.HasValue &&
                        GetResourceSpecific(ctx, msg.Target.Value) == "fruit")
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Select the Citizen Soldier, right-click on a tree near the Civic Center to begin gathering wood. Citizen Soldiers gather wood faster than Civilians." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "gather" && msg.Target.HasValue &&
                        GetResourceSpecific(ctx, msg.Target.Value) == "tree")
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Select the Civic Center building and hold Shift while clicking on the Hoplite icon once to begin training a batch of Hoplites." },
                OnTrainingQueued = (ctx, msg) =>
                {
                    if (msg.UnitTemplate != "units/spart/infantry_spearman_b" || msg.Count == 1)
                    {
                        ResetQueue(ctx, msg.TrainerEntity);
                        Warning(ctx, msg.Count == 1
                            ? "Do not forget to press the batch training hotkey while clicking to produce multiple units."
                            : "Click on the Hoplite icon.");
                        return;
                    }
                    Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Select the two idle Civilians and build a House nearby by selecting the House icon. Place the House by left-clicking on a piece of land." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "House"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "When they are ready, select the newly trained Hoplites and assign them to build a Storehouse beside some nearby trees. They will begin to gather wood when it's constructed." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Storehouse"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Train a batch of Skirmishers by holding Shift and clicking on the Skirmisher icon in the Civic Center." },
                Init = ctx => ctx.TrainingDone = false,
                OnTrainingQueued = (ctx, msg) =>
                {
                    if (msg.UnitTemplate != "units/spart/infantry_javelineer_b" || msg.Count == 1)
                    {
                        ResetQueue(ctx, msg.TrainerEntity);
                        Warning(ctx, msg.Count == 1
                            ? "Do not forget to press the batch training hotkey while clicking to produce multiple units."
                            : "Click on the Skirmisher icon.");
                        return;
                    }
                    Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Build a Farmstead in an open space beside the Civic Center using any idle builders." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Farmstead"))
                        Advance(ctx);
                },
                OnTrainingFinished = (ctx, _) => ctx.TrainingDone = true
            },

            new()
            {
                Instructions = { "Let's wait for the Farmstead to be built." },
                OnTrainingFinished = (ctx, _) => ctx.TrainingDone = true,
                OnStructureBuilt = (ctx, msg) =>
                {
                    if (EntityMatches(ctx, msg.Building, "Farmstead"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Once the Farmstead is constructed, its builders will automatically begin gathering food if there is any nearby. Select the builders and instead make them construct a Field beside the Farmstead." },
                Init = ctx => ctx.FarmStarted = false,
                IsDone = ctx => ctx.FarmStarted && ctx.TrainingDone,
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Field"))
                        ctx.FarmStarted = true;
                    if (ctx.FarmStarted && ctx.TrainingDone)
                        Advance(ctx);
                },
                OnTrainingFinished = (ctx, _) =>
                {
                    ctx.TrainingDone = true;
                    if (ctx.FarmStarted && ctx.TrainingDone)
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "The Field's builders will now automatically begin gathering food from the Field. Using the newly created group of skirmishers, get them to build another House nearby." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "House"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Train a batch of Hoplites at the Civic Center. Select the Civic Center and with it selected right-click on a tree nearby. Units from the Civic Center will now automatically gather wood." },
                Init = ctx => { ctx.RallyPointSet = false; ctx.TrainingStarted = false; },
                IsDone = ctx => ctx.RallyPointSet && ctx.TrainingStarted,
                OnTrainingQueued = (ctx, msg) =>
                {
                    if (msg.UnitTemplate != "units/spart/infantry_spearman_b" || msg.Count == 1)
                    {
                        ResetQueue(ctx, msg.TrainerEntity);
                        Warning(ctx, msg.Count == 1
                            ? "Do not forget to press the batch training hotkey while clicking to produce multiple units."
                            : "Click on the Hoplite icon.");
                        return;
                    }
                    ctx.TrainingStarted = true;
                    if (ctx.RallyPointSet && ctx.TrainingStarted)
                        Advance(ctx);
                },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type != "set-rallypoint") return;
                    if (!msg.Data.TryGetValue("command", out var cmd) || cmd?.ToString() != "gather") return;
                    if (!msg.Data.TryGetValue("specific", out var spec) || spec?.ToString() != "tree")
                    {
                        Warning(ctx, "Select the Civic Center, then hover the cursor over the tree and right-click when you see your cursor change into a wood icon.");
                        return;
                    }
                    ctx.RallyPointSet = true;
                    if (ctx.RallyPointSet && ctx.TrainingStarted)
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Build a Barracks nearby. Whenever your population limit is reached, build an extra House using any available builder units. This will be the fifth Village Phase structure that you have built, allowing you to advance to the Town Phase." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Barracks"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Select the Civic Center again and advance to Town Phase by clicking on the II icon (you have to wait for the barracks to be built first). This will allow Town Phase buildings to be constructed." },
                IsDone = ctx => HasDealtWithTech(ctx, "phase_town_generic"),
                OnResearchQueued = (ctx, msg) =>
                {
                    if (!string.IsNullOrEmpty(msg.TechnologyTemplate) &&
                        EntityMatches(ctx, msg.ResearcherEntity, "CivilCentre"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "While waiting for the phasing up, you may reassign your idle workers to gathering the resources you are short of." },
                IsDone = ctx => IsTechResearched(ctx, "phase_town_generic"),
                OnResearchFinished = (ctx, msg) =>
                {
                    if (msg.Tech == "phase_town_generic")
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Order the idle Skirmishers to build an outpost to the north east at the edge of your territory." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Outpost"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Start training a batch of Civilians in the Civic Center and set its rally point to the Field (right click on it)." },
                Init = ctx => { ctx.RallyPointSet = false; ctx.TrainingStarted = false; },
                IsDone = ctx => ctx.RallyPointSet && ctx.TrainingStarted,
                OnTrainingQueued = (ctx, msg) =>
                {
                    if (msg.UnitTemplate != "units/spart/support_civilian" || msg.Count == 1)
                    {
                        ResetQueue(ctx, msg.TrainerEntity);
                        Warning(ctx, msg.Count == 1
                            ? "Do not forget to press the batch training hotkey while clicking to produce multiple units."
                            : "Click on the Civilian icon.");
                        return;
                    }
                    ctx.TrainingStarted = true;
                    if (ctx.RallyPointSet && ctx.TrainingStarted)
                        Advance(ctx);
                },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type != "set-rallypoint") return;
                    if (!msg.Data.TryGetValue("command", out var cmd) || cmd?.ToString() != "gather") return;
                    if (!msg.Data.TryGetValue("specific", out var spec) || spec?.ToString() != "grain") return;
                    ctx.RallyPointSet = true;
                    if (ctx.RallyPointSet && ctx.TrainingStarted)
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Prepare for an attack by an enemy player. Train more soldiers using the Barracks, and get idle soldiers to build a Tower near your Outpost." },
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type == "repair" && msg.Target.HasValue &&
                        EntityMatches(ctx, msg.Target.Value, "Tower"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Build a Forge and research the Infantry Training technology (sword icon) to improve infantry hack attack." },
                OnResearchQueued = (ctx, msg) =>
                {
                    if (!string.IsNullOrEmpty(msg.TechnologyTemplate) &&
                        EntityMatches(ctx, msg.ResearcherEntity, "Forge"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "The enemy is coming. Train more soldiers to fight off the enemies." },
                OnResearchFinished = (ctx, msg) =>
                {
                    LaunchAttack(ctx);
                    Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Try to repel the attack." },
                OnOwnershipChanged = (ctx, msg) =>
                {
                    if (msg.To != -1) return;
                    if (IsAttackRepelled(ctx))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "The enemy attack has been thwarted. Now build a market and a temple while you assign new units to gather required resources." },
                Init = ctx => { ctx.MarketStarted = false; ctx.TempleStarted = false; },
                IsDone = ctx => ctx.MarketStarted && ctx.TempleStarted,
                OnPlayerCommand = (ctx, msg) =>
                {
                    if (msg.Type != "repair" || !msg.Target.HasValue) return;
                    ctx.MarketStarted = ctx.MarketStarted || EntityMatches(ctx, msg.Target.Value, "Market");
                    ctx.TempleStarted = ctx.TempleStarted || EntityMatches(ctx, msg.Target.Value, "Temple");
                    if (ctx.MarketStarted && ctx.TempleStarted)
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Once you meet the City Phase requirements, select your Civic Center and advance to City Phase." },
                IsDone = ctx => HasDealtWithTech(ctx, "phase_city_generic"),
                OnResearchQueued = (ctx, msg) =>
                {
                    if (!string.IsNullOrEmpty(msg.TechnologyTemplate) &&
                        EntityMatches(ctx, msg.ResearcherEntity, "CivilCentre"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "While waiting for the phase change, you may train more soldiers at the Barracks." },
                IsDone = ctx => IsTechResearched(ctx, "phase_city_generic"),
                OnResearchFinished = (ctx, msg) =>
                {
                    if (msg.Tech == "phase_city_generic")
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "Now that you are in City Phase, build the Arsenal nearby and then use it to construct 2 Battering Rams." },
                Init = ctx => ctx.RamCount = 0,
                IsDone = ctx => ctx.RamCount > 1,
                OnTrainingQueued = (ctx, msg) =>
                {
                    if (msg.UnitTemplate == "units/spart/siege_ram")
                        ctx.RamCount += msg.Count;
                    if (ctx.RamCount > 1)
                    {
                        RemoveChampions(ctx);
                        Advance(ctx);
                    }
                }
            },

            new()
            {
                Instructions =
                {
                    "Stop all your soldiers gathering resources and instead task small groups to find the enemy Civic Center on the map. Once the enemy's base has been spotted, send your Siege Engines and all remaining soldiers to destroy it.\n",
                    "Civilians should continue to gather resources."
                },
                OnOwnershipChanged = (ctx, msg) =>
                {
                    if (msg.From != ctx.EnemyId) return;
                    if (EntityMatches(ctx, msg.Entity, "CivilCentre"))
                        Advance(ctx);
                }
            },

            new()
            {
                Instructions = { "The enemy has been defeated. These tutorial tasks are now completed." },
                Init = ctx => ctx.Events.RaiseTutorialMessage(new TutorialNotification
                {
                    Instructions = { "Tutorial completed!" },
                    ReadyButton = true,
                    Leave = true
                })
            }
        };

        private static void Advance(TutorialGoalContext ctx) => ctx.Engine?.AdvanceGoal();

        private static void Warning(TutorialGoalContext ctx, string text) =>
            ctx.Engine?.WarningMessage(text);

        private static bool EntityMatches(TutorialGoalContext ctx, EntityId entity, string className)
        {
            var identity = ctx.Sim.QueryInterface<IdentityComponent>(entity);
            return identity != null && identity.MatchesClassList(className);
        }

        private static string? GetResourceSpecific(TutorialGoalContext ctx, EntityId entity)
        {
            var supply = ctx.Sim.QueryInterface<ResourceSupply>(entity);
            return supply?.SpecificType;
        }

        private static void ResetQueue(TutorialGoalContext ctx, EntityId trainer)
        {
            var queue = ctx.Sim.QueryInterface<ProductionQueue>(trainer);
            queue?.ResetQueue();
        }

        private static bool HasDealtWithTech(TutorialGoalContext ctx, string tech)
        {
            var techMgr = FindTechManager(ctx);
            if (techMgr == null) return false;
            return techMgr.IsResearched(tech);
        }

        private static bool IsTechResearched(TutorialGoalContext ctx, string tech)
        {
            var techMgr = FindTechManager(ctx);
            return techMgr != null && techMgr.IsResearched(tech);
        }

        private static TechnologyManager? FindTechManager(TutorialGoalContext ctx)
        {
            foreach (var eid in ctx.Sim.AllEntities)
            {
                var tm = ctx.Sim.QueryInterface<TechnologyManager>(eid);
                if (tm != null) return tm;
            }
            return null;
        }

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

            if (target.HasValue)
            {
                var pos = ctx.Sim.QueryInterface<PositionComponent>(target.Value);
                if (pos != null)
                {
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
    }
}
