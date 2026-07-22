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

    /// <summary>
    /// Raised by <see cref="ComponentManager.SpawnEntity"/> after a sim entity has been fully
    /// assembled. The presentation layer subscribes to build Godot visuals. Pure sim state
    /// (no Godot dependency) — mirrors how TrainingFinished/StructureBuilt already work.
    /// </summary>
    public sealed class EntityCreatedEvent
    {
        public EntityId Entity;
        public string TemplateName = "";
        public int OwnerPlayerId = -1;
    }

    public sealed class TutorialNotification
    {
        public List<string> Instructions = new();
        public string? Warning;
        public bool ReadyButton;
        public bool Leave;
    }

    /// <summary>Raised by DelayedDamage when a hit settles on a target. Lets the presentation
    /// layer play impact feedback (blood puffs, screen shake, hit sound).</summary>
    public sealed class AttackLandedEvent
    {
        public EntityId Target;
        public EntityId Attacker;
        public int DamageDealt;
    }

    /// <summary>Raised when a player is eliminated (lost all units + buildings in conquest).
    /// Ported from the MT_PlayerDefeated message. Drives the game-over UI.</summary>
    public sealed class PlayerDefeatedEvent
    {
        public int PlayerId;
        public string Reason = "";
    }

    /// <summary>Raised when a player wins (last one standing). Ported from MT_PlayerWon.</summary>
    public sealed class PlayerWonEvent
    {
        public int PlayerId;
    }

    /// <summary>Raised once when the match ends — the sole surviving player has won.
    /// The presentation layer shows the victory/defeat overlay on this.</summary>
    public sealed class GameEndedEvent
    {
        public int WinnerPlayerId;
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
        public event Action<EntityCreatedEvent>? EntityCreated;
        public event Action<AttackLandedEvent>? AttackLanded;
        public event Action<PlayerDefeatedEvent>? PlayerDefeated;
        public event Action<PlayerWonEvent>? PlayerWon;
        public event Action<GameEndedEvent>? GameEnded;

        public void RaisePlayerCommand(PlayerCommandEvent e) => PlayerCommand?.Invoke(e);
        public void RaiseTrainingQueued(TrainingQueuedEvent e) => TrainingQueued?.Invoke(e);
        public void RaiseTrainingFinished(TrainingFinishedEvent e) => TrainingFinished?.Invoke(e);
        public void RaiseStructureBuilt(StructureBuiltEvent e) => StructureBuilt?.Invoke(e);
        public void RaiseResearchQueued(ResearchQueuedEvent e) => ResearchQueued?.Invoke(e);
        public void RaiseResearchFinished(ResearchFinishedEvent e) => ResearchFinished?.Invoke(e);
        public void RaiseOwnershipChanged(OwnershipChangedEvent e) => OwnershipChanged?.Invoke(e);
        public void RaiseTutorialMessage(TutorialNotification n) => TutorialMessage?.Invoke(n);
        public void RaiseEntityCreated(EntityCreatedEvent e) => EntityCreated?.Invoke(e);
        public void RaiseAttackLanded(AttackLandedEvent e) => AttackLanded?.Invoke(e);
        public void RaisePlayerDefeated(PlayerDefeatedEvent e) => PlayerDefeated?.Invoke(e);
        public void RaisePlayerWon(PlayerWonEvent e) => PlayerWon?.Invoke(e);
        public void RaiseGameEnded(GameEndedEvent e) => GameEnded?.Invoke(e);
    }
}
