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
        /// <summary>训练属主玩家号(原版 MT_TrainingQueued 的 playerid)。</summary>
        public int PlayerId;
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
        /// <summary>研究属主玩家号(原版 MT_ResearchQueued 的 playerid)。</summary>
        public int PlayerId;
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

    /// <summary>停战开始(MT_CeasefireStarted):全体非 gaia 玩家互置中立,倒计时期间禁攻击。
    /// RemainingSeconds 供表现层显示倒计时通知。</summary>
    public sealed class CeasefireStartedEvent
    {
        public float RemainingSeconds;
    }

    /// <summary>停战结束(MT_CeasefireEnded):外交立场恢复停战前快照,可以开打。</summary>
    public sealed class CeasefireEndedEvent
    {
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

    /// <summary>玩家受击警报(原版 MT_MinimapPing + AttackDetection 抑制后的报警;
    /// AttackDetection.js OnGlobalAttacked → AttackAlert)。表现层:小地图 ping + 警报音。</summary>
    public sealed class PlayerAttackedAlertEvent
    {
        public int PlayerId;
        public EntityId Target;
        public EntityId Attacker;
        public float X, Z;
        /// <summary>家畜目标(原版低优先级通知,可被普通警报覆盖)。</summary>
        public bool TargetIsDomesticAnimal;
    }

    /// <summary>聊天消息（本地展示 sink）。Kind=Message 来自玩家输入（SP 本地回显或 MP ReceiveChat 转发），
    /// Kind=System 来自游戏事件（PlayerDefeated 等，无 sender）。MP 传输走 MultiplayerController RPC，
    /// 不进锁步（匹配原版 NMT_CHAT：直接 multicast，不经模拟/turn manager）。</summary>
    public sealed class ChatMessageEvent
    {
        public enum KindType { Message, System }
        public KindType Kind;
        public int SenderPlayerId;   // Message 时是发送者；System 时为 -1
        public string Text = "";
        public string Addressee = "";  // "all"/"allies"/"enemies"；空=all（简化版默认 all）
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

    /// <summary>宝藏结算(原版 Trigger.OnTreasureCollected 广播):收集者/宝物/属主。
    /// 地图脚本的"收集 X 个宝物判胜/解锁"经 TriggerSystem.CallEvent 驱动。</summary>
    public sealed class TreasureCollectedEvent
    {
        public EntityId Collector;
        public EntityId Treasure;
        public int PlayerId;
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

    /// <summary>建造开始(地基放下;原版 MT_ConstructionStarted)。触发器
    /// OnConstructionStarted 的数据源。</summary>
    public sealed class ConstructionStartedEvent
    {
        public EntityId Foundation;
        public string Template = "";
        public int OwnerPlayerId;
    }

    /// <summary>实体换名(原版 MT_EntityRenamed:晋升/变身旧号→新号)。触发器
    /// OnEntityRenamed 的数据源;Guard/RallyPoint 的目标改指也挂它。</summary>
    public sealed class EntityRenamedEvent
    {
        public EntityId OldEntity;
        public EntityId NewEntity;
    }

    /// <summary>攻击请求(原版 chat attack-request 的 sim 内等价;盟友请 AI 攻某敌)。</summary>
    public sealed class AttackRequestedEvent
    {
        public int SourcePlayer;
        public int TargetPlayer;
    }
    /// <summary>攻击请求答复(原版 attackAnswer;accepted = 兵力够立即推)。</summary>
    public sealed class AttackAnsweredEvent
    {
        public int SourcePlayer;
        public int TargetPlayer;
        public bool Accepted;
    }

    /// <summary>贡品请求(原版 chat.requestTribute 的 sim 内等价:AI 间同进程,
    /// 事件即达且锁步确定)。</summary>
    public sealed class TributeRequestedEvent
    {
        public int FromPlayer;
        public int ToPlayer;
        public string ResourceType = "";
    }

    /// <summary>外交立场变化(原版 MT_DiplomacyChanged 全局消息;DiplomacyComponent
    /// SetStance 后广播)。驻军/炮塔/护卫据以即时逐出非互盟单位。</summary>
    public sealed class DiplomacyChangedEvent
    {
        public int Player;
        public int OtherPlayer;
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
        public event Action<DiplomacyChangedEvent>? DiplomacyChanged;
        public event Action<TributeRequestedEvent>? TributeRequested;
        public event Action<AttackRequestedEvent>? AttackRequested;
        public event Action<AttackAnsweredEvent>? AttackAnswered;
        public event Action<ConstructionStartedEvent>? ConstructionStarted;
        public event Action<EntityRenamedEvent>? EntityRenamed;
        public event Action<AttackLaunchedEvent>? AttackLaunched;
        public event Action<ChatMessageEvent>? ChatMessage;
        public event Action<PlayerDefeatedEvent>? PlayerDefeated;
        public event Action<PlayerWonEvent>? PlayerWon;
        public event Action<CeasefireStartedEvent>? CeasefireStarted;
        public event Action<CeasefireEndedEvent>? CeasefireEnded;
        public event Action<GameEndedEvent>? GameEnded;
        public event Action<VisibilityChangedEvent>? VisibilityChanged;
        public event Action<MirageSwapBackEvent>? MirageSwapBack;
        public event Action<ResourceGatheredEvent>? ResourceGathered;
        public event Action<TreasureCollectedEvent>? TreasureCollected;
        public event Action<ResourceSpentEvent>? ResourceSpent;
        public event Action<EntityKilledEvent>? EntityKilled;
        public event Action<TradeIncomeEvent>? TradeIncome;
        public event Action<TributeEvent>? Tribute;
        public event Action<PlayerAttackedAlertEvent>? PlayerAttackedAlert;

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
        public void RaiseDiplomacyChanged(DiplomacyChangedEvent e) => DiplomacyChanged?.Invoke(e);
        public void RaiseTributeRequested(TributeRequestedEvent e) => TributeRequested?.Invoke(e);
        public void RaiseAttackRequested(AttackRequestedEvent e) => AttackRequested?.Invoke(e);
        public void RaiseAttackAnswered(AttackAnsweredEvent e) => AttackAnswered?.Invoke(e);
        public void RaiseConstructionStarted(ConstructionStartedEvent e) => ConstructionStarted?.Invoke(e);
        public void RaiseEntityRenamed(EntityRenamedEvent e) => EntityRenamed?.Invoke(e);
        public void RaiseAttackLaunched(AttackLaunchedEvent e) => AttackLaunched?.Invoke(e);
        public void RaisePlayerAttackedAlert(PlayerAttackedAlertEvent e) => PlayerAttackedAlert?.Invoke(e);
        public void RaiseChatMessage(ChatMessageEvent e) => ChatMessage?.Invoke(e);
        public void RaisePlayerDefeated(PlayerDefeatedEvent e) => PlayerDefeated?.Invoke(e);
        public void RaisePlayerWon(PlayerWonEvent e) => PlayerWon?.Invoke(e);
        public void RaiseCeasefireStarted(CeasefireStartedEvent e) => CeasefireStarted?.Invoke(e);
        public void RaiseCeasefireEnded(CeasefireEndedEvent e) => CeasefireEnded?.Invoke(e);
        public void RaiseGameEnded(GameEndedEvent e) => GameEnded?.Invoke(e);
        public void RaiseVisibilityChanged(VisibilityChangedEvent e) => VisibilityChanged?.Invoke(e);
        public void RaiseMirageSwapBack(MirageSwapBackEvent e) => MirageSwapBack?.Invoke(e);
        public void RaiseResourceGathered(ResourceGatheredEvent e) => ResourceGathered?.Invoke(e);
        public void RaiseTreasureCollected(TreasureCollectedEvent e) => TreasureCollected?.Invoke(e);
        public void RaiseResourceSpent(ResourceSpentEvent e) => ResourceSpent?.Invoke(e);
        public void RaiseEntityKilled(EntityKilledEvent e) => EntityKilled?.Invoke(e);
        public void RaiseTradeIncome(TradeIncomeEvent e) => TradeIncome?.Invoke(e);
        public void RaiseTribute(TributeEvent e) => Tribute?.Invoke(e);
    }
}
