using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
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
        /// <summary>脚本级状态槽(原版教程 JS 的 this.xxx 自由字段;经济演练教程的
        /// houseGoal/femaleCount/stone/metal/trainingDone 等走这里)。</summary>
        public Dictionary<string, object> State = new();
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
        /// <summary>0=自动(无事件绑定→Ready 按钮);&gt;0=计时器秒数到自动翻页;
        /// &lt;0=强制 Ready 按钮(上游 delay:-1,带内务绑定但手动翻页的目标用)。</summary>
        public float Delay;
    }

    public sealed class TutorialEngine
    {
        private readonly List<TutorialGoal> _goals;
        private readonly TutorialGoalContext _ctx = new();
        private int _index;
        private bool _waitingReady;
        private bool _leaveOnReady;
        // goal Delay 计时器(原版 goal Delay 语义:Delay 秒到即进下一目标,
        // 此前退化为 Ready 按钮——无 Tick 驱动,定时器根本不跑)。
        private float _delayElapsed;
        private bool _delayPending;

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

        /// <summary>goal Delay 计时器推进(SimBridge 每回合调):Delay 秒到即
        /// 进下一目标(原版 goal Delay 语义;无 Delay 的事件驱动目标不受影响)。</summary>
        public void Tick(float dt)
        {
            if (!_delayPending) return;
            _delayElapsed += dt;
            if (_index - 1 >= 0 && _index - 1 < _goals.Count
                && _delayElapsed >= _goals[_index - 1].Delay)
            {
                _delayPending = false;
                _delayElapsed = 0;
                NextGoal();
            }
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
                // 计时器驱动(原版 goal Delay):Delay 秒到自动进下一目标,
                // 不再显示 Ready 按钮(此前退化为按钮——无 Tick 驱动)。
                _delayPending = true;
                _delayElapsed = 0;
                _waitingReady = false;
            }
            else if (goal.Delay < 0)
            {
                // 上游 delay:-1 语义——强制 Ready 按钮,即使目标带有事件绑定
                // (如经济关"农田说明"页带房屋追踪内务绑定,但靠手动翻页)。
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

        // 数据驱动目标的达成复查:绑定处理器跑完后若仍是同一目标,按 IsDone
        // (RequireFlags/RequireTechs* 装配)复查一次——复合条件(双旗/旗+科技)靠
        // 这里在任何相关事件后收口,不要求每类事件都有显式绑定。

        private void OnPlayerCommand(PlayerCommandEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnPlayerCommand?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnTrainingQueued(TrainingQueuedEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnTrainingQueued?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnTrainingFinished(TrainingFinishedEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnTrainingFinished?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnStructureBuilt(StructureBuiltEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnStructureBuilt?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnResearchQueued(ResearchQueuedEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnResearchQueued?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnResearchFinished(ResearchFinishedEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnResearchFinished?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void OnOwnershipChanged(OwnershipChangedEvent msg)
        {
            var goal = CurrentGoal;
            goal?.OnOwnershipChanged?.Invoke(_ctx, msg);
            Recheck(goal);
        }

        private void Recheck(TutorialGoal? goal)
        {
            // 处理器可能已翻页(直接 Advance)——只对仍为当前目标的做复查,防连跳。
            if (goal != null && ReferenceEquals(goal, CurrentGoal))
                AdvanceIfDone(goal);
        }

        public void NotifyReadyFromCommand()
        {
            if (_waitingReady)
                NextGoal();
        }

        public bool ShouldQuitOnReady => _leaveOnReady && _waitingReady;

        public string GetFullText() => string.Join("\n", _messageHistory);
    }
}
