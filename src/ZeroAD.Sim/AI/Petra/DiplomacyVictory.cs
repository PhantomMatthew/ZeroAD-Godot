using System.Collections.Generic;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>外交管理器（原版 petra/diplomacyManager.js，589 行）。
/// 管理贡品、外交请求、背叛逻辑（最后一人站立模式）。
/// 骨架版——update 结构移植，贡品/背叛/外交请求标 TODO。</summary>
public sealed class DiplomacyManager
{
    private readonly PetraConfig _config;
    public double NextTributeUpdate;
    public bool WaitingToBetray;
    public double BetrayLapseTime;

    public DiplomacyManager(PetraConfig config, bool deserialized) => _config = config;

    /// <summary>主更新（原版 diplomacyManager.js:542-568）。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        CheckEvents(gameState, events);

        // TODO: 贡品逻辑（资源富余时给盟友）
        // TODO: 背叛逻辑（最后一人站立模式）
        // TODO: 外交请求（极少发，randBool(0.1)）
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.PlayerDefeated)
            {
                // 玩家被击败 → 更新外交状态
                // TODO: 评估是否应背叛盟友
            }
        }
    }
}

/// <summary>胜利管理器（原版 petra/victoryManager.js，771 行）。
/// 管理奇迹胜利条件、最终推进、消灭残敌。
/// 骨架版——update 结构移植，奇迹管理/最终推进标 TODO。</summary>
public sealed class VictoryManager
{
    private readonly PetraConfig _config;

    public VictoryManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 victoryManager.js:594-770）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        CheckEvents(gameState, events);

        // TODO: manageWonders（奇迹胜利条件：建奇迹 + 守住计时）
        // TODO: 最终推进（残敌清理）
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.PlayerDefeated)
            {
                // 玩家被击败 → 检查胜利条件
                // TODO: 评估是否接近胜利
            }
        }
    }
}
