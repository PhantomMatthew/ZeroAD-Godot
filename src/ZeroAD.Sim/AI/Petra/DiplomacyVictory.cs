using System.Collections.Generic;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>外交管理器（原版 petra/diplomacyManager.js，589 行）。
/// 管理贡品、外交请求、背叛逻辑（最后一人站立模式）。
/// 本版:tributes 贡品闭环移植(每 30s:盈余 >200 且盟友 <20% → 送 0.3×差值;
/// 盟友濒危 → 直送 100),LMS 背叛(只剩两家盟友 → 反目);外交请求(chat.requestTribute)
/// 属聊天体系,不移植。</summary>
public sealed class DiplomacyManager
{
    private readonly PetraConfig _config;
    /// <summary>下次贡品回合(原版 nextTributeUpdate;回合 = 0.1s,300 回合 = 30s)。</summary>
    public uint NextTributeTurn = 900;   // 原版初始 90s
    public bool WaitingToBetray;
    /// <summary>背叛到的回合(原版 betrayLapseTime)。</summary>
    public uint BetrayAtTurn;

    public DiplomacyManager(PetraConfig config, bool deserialized = false) => _config = config;

    /// <summary>主更新（原版 diplomacyManager.js:542-568:tributes + 背叛检查）。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        CheckEvents(gameState, events);
        uint turn = gameState.Net?.CurrentTurn ?? 0;
        if (turn >= NextTributeTurn)
        {
            NextTributeTurn = turn + 300;   // 30s
            Tributes(gameState);
        }
        CheckBetrayal(gameState, turn);
    }

    /// <summary>贡品(原版 tributes):对每个未败盟友,donor(同盟共胜局或盟友实体
    /// 比我方少)时按盈余输送;两种口径:常规(>200 盈余、盟友 <20% → 0.3×我方-盟友)
    /// 与濒危救济(盟友人口 < min(30, 我方一半) 且我方 >500 且盟友 <100 → 直送 100)。</summary>
    private void Tributes(GameState gameState)
    {
        var cm = gameState.Cm;
        var us = cm.GetPlayerEntity(gameState.PlayerId);
        if (us == null) return;
        int ourPop = us.PopUsed;

        foreach (int allyId in gameState.GetAllies())
        {
            var allyPlayer = cm.GetPlayerEntity(allyId);
            if (allyPlayer == null || !allyPlayer.IsActive()) continue;
            bool donor = cm.EndGame.AlliedVictory
                || CountEntities(cm, allyId) < CountEntities(cm, gameState.PlayerId);
            if (!donor) continue;

            Send(gameState, allyId, ResourceType.Food, us.Food, allyPlayer.Food, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Wood, us.Wood, allyPlayer.Wood, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Stone, us.Stone, allyPlayer.Stone, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Metal, us.Metal, allyPlayer.Metal, allyPlayer.PopUsed, ourPop);
        }
    }

    private static void Send(GameState gameState, int allyId,
        ResourceType type, int ours, int theirs, int allyPop, int ourPop)
    {
        int amount = 0;
        if (ours > 200 && theirs < 0.2f * ours)
            amount = (int)(0.3f * ours) - theirs;                 // 常规输送
        else if (allyPop < System.Math.Min(30, 0.5f * ourPop) && ours > 500 && theirs < 100)
            amount = 100;                                          // 濒危救济
        if (amount <= 0) return;
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Tribute(
            (uint)gameState.PlayerId, allyId, type, amount));
    }

    private static int CountEntities(ComponentManager cm, int playerId)
        => SimSystem.Range?.GetEntitiesByPlayer(playerId).Count ?? 0;

    /// <summary>LMS 背叛(原版 betrayal 逻辑简化):LastManStanding(非同盟共胜)局,
    /// 全场只剩我们和盟友两家活跃 → 经过短暂延迟后反目(互设敌对),否则无法收官。</summary>
    private void CheckBetrayal(GameState gameState, uint turn)
    {
        var cm = gameState.Cm;
        if (cm.EndGame.AlliedVictory) return;
        var actives = new List<int>();
        foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
        {
            var p = cm.Players.GetPlayerEntity(pid);
            if (p != null && p.IsActive()) actives.Add(pid);
        }
        if (actives.Count == 2 && actives.Contains(gameState.PlayerId)
            && gameState.IsPlayerMutualAlly(actives[0] == gameState.PlayerId ? actives[1] : actives[0]))
        {
            if (!WaitingToBetray)
            {
                WaitingToBetray = true;
                BetrayAtTurn = turn + 100;   // 10s 缓冲(原版 lapse)
            }
            if (turn >= BetrayAtTurn)
            {
                int other = actives[0] == gameState.PlayerId ? actives[1] : actives[0];
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetStance(
                    (uint)gameState.PlayerId, other, DiplomacyComponent.Enemy));
                WaitingToBetray = false;
            }
        }
        else
        {
            WaitingToBetray = false;
        }
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.PlayerDefeated)
            {
                // 玩家被击败 → 背叛状态重估(下一拍 CheckBetrayal 自然处理)。
                WaitingToBetray = false;
            }
        }
    }
}

/// <summary>胜利管理器（原版 petra/victoryManager.js，771 行）。
/// 管理奇迹胜利条件、最终推进、消灭残敌。
/// 本版:每 10 回合(原版 playedTurn%10)按胜利条件驱动——wonder:无奇迹无在队
/// 即建(ConstructionPlan);regicide:英雄健康 → 站姿 aggressive(原版同款护主);
/// capture_the_relic 的袭击队、关键单位守卫/治疗者编排未移植(记录在案)。</summary>
public sealed class VictoryManager
{
    private readonly PetraConfig _config;

    /// <summary>胜利关键实体(原版 victoryManager.criticalEnts:奇迹/圣物/国王——
    /// 防御分派与进攻征收都绕开它们)。</summary>
    public readonly HashSet<uint> CriticalEnts = new();

    public VictoryManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 victoryManager.js:594-770）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        CheckEvents(gameState, events);

        uint turn = gameState.Net?.CurrentTurn ?? 0;
        if (turn % 10 != 0) return;

        var endGame = gameState.Cm.EndGame;
        if (!endGame.HasCondition("wonder") && !endGame.HasCondition("regicide")
            && !endGame.HasCondition("capture_the_relic"))
            return;

        // 奇迹胜利(原版 HQ.buildWonder):无奇迹且无在队计划 → 建。
        if (endGame.HasCondition("wonder")
            && !gameState.GetOwnEntitiesByClass("Wonder").HasEntities()
            && queues.GetQueue("wonder")?.HasQueuedUnits != true)
        {
            queues.AddPlan("wonder",
                new ConstructionPlan(gameState, "structures/{civ}/wonder"));
        }

        // 弑君护主(原版:英雄血量健康 → aggressive 站姿让其自由迎敌)。
        if (endGame.HasCondition("regicide")
            && endGame.RegicideHeroes.TryGetValue(gameState.PlayerId, out var hero))
        {
            var health = gameState.Cm.QueryInterface<HealthComponent>(hero);
            var ai = gameState.Cm.QueryInterface<UnitAIComponent>(hero);
            if (health != null && ai != null && ai.Stance != "aggressive"
                && (float)health.Current / health.Max > 0.8f)
            {
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetUnitStance(
                    (uint)gameState.PlayerId, hero.Value, "aggressive"));
            }
        }

        // capture_the_relic:袭击队/守卫编排未移植(需 transportPlan/编队指派)。
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.PlayerDefeated)
            {
                // 玩家被击败 → 胜利条件评估(当前由 EndGameManager 轮询兜底)。
            }
        }
    }
}
