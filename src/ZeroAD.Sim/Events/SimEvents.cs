using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Events
{
    public sealed class PlayerCommandEvent
    {
        public string Type = "";
        public EntityId? Target;
        public Dictionary<string, object> Data = new();
    }

    public sealed class TrainingQueuedEvent
    {
        public EntityId TrainerEntity;
        public string UnitTemplate = "";
        public int Count = 1;
    }

    public sealed class TrainingFinishedEvent
    {
        public EntityId TrainerEntity;
        public string UnitTemplate = "";
    }

    public sealed class StructureBuiltEvent
    {
        public EntityId Building;
        public string TemplateName = "";
    }

    public sealed class ResearchQueuedEvent
    {
        public EntityId ResearcherEntity;
        public string TechnologyTemplate = "";
    }

    public sealed class ResearchFinishedEvent
    {
        public EntityId ResearcherEntity;
        public string Tech = "";
    }

    public sealed class OwnershipChangedEvent
    {
        public EntityId Entity;
        public int From;
        public int To;
    }

    public sealed class TutorialNotification
    {
        public List<string> Instructions = new();
        public string? Warning;
        public bool ReadyButton;
        public bool Leave;
    }

    public sealed class SimEventBus
    {
        public event Action<PlayerCommandEvent>? PlayerCommand;
        public event Action<TrainingQueuedEvent>? TrainingQueued;
        public event Action<TrainingFinishedEvent>? TrainingFinished;
        public event Action<StructureBuiltEvent>? StructureBuilt;
        public event Action<ResearchQueuedEvent>? ResearchQueued;
        public event Action<ResearchFinishedEvent>? ResearchFinished;
        public event Action<OwnershipChangedEvent>? OwnershipChanged;
        public event Action<TutorialNotification>? TutorialMessage;

        public void RaisePlayerCommand(PlayerCommandEvent e) => PlayerCommand?.Invoke(e);
        public void RaiseTrainingQueued(TrainingQueuedEvent e) => TrainingQueued?.Invoke(e);
        public void RaiseTrainingFinished(TrainingFinishedEvent e) => TrainingFinished?.Invoke(e);
        public void RaiseStructureBuilt(StructureBuiltEvent e) => StructureBuilt?.Invoke(e);
        public void RaiseResearchQueued(ResearchQueuedEvent e) => ResearchQueued?.Invoke(e);
        public void RaiseResearchFinished(ResearchFinishedEvent e) => ResearchFinished?.Invoke(e);
        public void RaiseOwnershipChanged(OwnershipChangedEvent e) => OwnershipChanged?.Invoke(e);
        public void RaiseTutorialMessage(TutorialNotification n) => TutorialMessage?.Invoke(n);
    }
}
