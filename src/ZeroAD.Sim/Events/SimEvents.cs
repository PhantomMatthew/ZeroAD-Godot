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
        /// <summary>本次命中实际抽走的占领点(原版 MT_Attacked 的 capture 字段;
        /// 表现层占领条/进度反馈用)。</summary>
        public float CaptureDealt;
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

    /// <summary>攻击发射（PerformAttack 调用时）。表现层用于生成飞行投射物。
    /// 纯视觉信号——伤害已由 DelayedDamage 瞬间结算（delayTurns=0），投射物只是装饰
    /// （匹配原版 CCmpProjectileManager：just graphical effects, non-deterministic float）。
    /// IsRanged=true 时表现层生成抛物线箭矢；melee 时表现层只播命中特效（不飞投射物）。</summary>
    public sealed class AttackLaunchedEvent
    {
        public EntityId Attacker;
        public EntityId Target;
        public bool IsRanged;
    }

    /// <summary>Raised by RangeManager.UpdateVisibilityData when an entity's per-player
    /// visibility changes (HIDDEN/FOGGED/VISIBLE). Drives Fogging/Mirage bookkeeping in the
    /// kernel and entity show/hide on the presentation layer. Mirrors CMessageVisibilityChanged.</summary>
    public sealed class VisibilityChangedEvent
    {
        public int Player;
        public EntityId Entity;
        public Components.LosVisibility Old;
        public Components.LosVisibility New;
    }

    // ── 统计追踪事件（驱动 StatisticsTracker 的计数器）。镜像原版各组件里的 IncreaseXxxCounter 调用点。──

    /// <summary>资源采集入账（drop-off 时）。镜像 ResourceGatherer.js:286 的 IncreaseResourceGatheredCounter。</summary>
    public sealed class ResourceGatheredEvent
    {
        public int PlayerId;
        public Components.ResourceType Type;
        public int Amount;
        /// <summary>素食食物的二级类型（fruit/grain 等），仅 Type=Food 时有意义。用于 vegetarianFood 桶。</summary>
        public string? GenericType;
    }

    /// <summary>资源花费（建造/训练/科研扣费时）。镜像 Player.js:349 的 IncreaseResourceUsedCounter。</summary>
    public sealed class ResourceSpentEvent
    {
        public int PlayerId;
        public Components.ResourceType Type;
        public int Amount;
    }

    /// <summary>实体被击杀（生命值归零的命中）。携带 killer 用于归属。镜像 Health.js:221 的 KilledEntity/LostEntity。
    /// 这是 kill 归属的唯一信号——DelayedDamage 命中后检测 IsDead 并 raise。</summary>
    public sealed class EntityKilledEvent
    {
        public EntityId Victim;
        public EntityId Killer;
    }

    /// <summary>贸易收入入账。镜像 Trader.js:197 的 IncreaseTradeIncomeCounter。</summary>
    public sealed class TradeIncomeEvent
    {
        public int PlayerId;
        public int Amount;
    }

    /// <summary>贡品发送/接收。镜像 Player.js:686,689 的 IncreaseTributesSent/ReceivedCounter。</summary>
    public sealed class TributeEvent
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public Components.ResourceType Type;
        public int Amount;
    }

    /// <summary>Raised when a player loses sight of a position and back: the mirage that stood
    /// in for <see cref="Parent"/> went HIDDEN because the real entity is visible again.
    /// Mirrors MT_EntityRenamed { entity: mirage, newentity: parent } in the original — the
    /// presentation layer swaps selection/GUI from the mirage back to the real entity.</summary>
    public sealed class MirageSwapBackEvent
    {
        public EntityId Mirage;
        public EntityId Parent;
        public int Player;
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
        public event Action<AttackLaunchedEvent>? AttackLaunched;
        public event Action<PlayerDefeatedEvent>? PlayerDefeated;
        public event Action<PlayerWonEvent>? PlayerWon;
        public event Action<GameEndedEvent>? GameEnded;
        public event Action<VisibilityChangedEvent>? VisibilityChanged;
        public event Action<MirageSwapBackEvent>? MirageSwapBack;
        public event Action<ResourceGatheredEvent>? ResourceGathered;
        public event Action<ResourceSpentEvent>? ResourceSpent;
        public event Action<EntityKilledEvent>? EntityKilled;
        public event Action<TradeIncomeEvent>? TradeIncome;
        public event Action<TributeEvent>? Tribute;

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
        public void RaiseAttackLaunched(AttackLaunchedEvent e) => AttackLaunched?.Invoke(e);
        public void RaisePlayerDefeated(PlayerDefeatedEvent e) => PlayerDefeated?.Invoke(e);
        public void RaisePlayerWon(PlayerWonEvent e) => PlayerWon?.Invoke(e);
        public void RaiseGameEnded(GameEndedEvent e) => GameEnded?.Invoke(e);
        public void RaiseVisibilityChanged(VisibilityChangedEvent e) => VisibilityChanged?.Invoke(e);
        public void RaiseMirageSwapBack(MirageSwapBackEvent e) => MirageSwapBack?.Invoke(e);
        public void RaiseResourceGathered(ResourceGatheredEvent e) => ResourceGathered?.Invoke(e);
        public void RaiseResourceSpent(ResourceSpentEvent e) => ResourceSpent?.Invoke(e);
        public void RaiseEntityKilled(EntityKilledEvent e) => EntityKilled?.Invoke(e);
        public void RaiseTradeIncome(TradeIncomeEvent e) => TradeIncome?.Invoke(e);
        public void RaiseTribute(TributeEvent e) => Tribute?.Invoke(e);
    }
}
