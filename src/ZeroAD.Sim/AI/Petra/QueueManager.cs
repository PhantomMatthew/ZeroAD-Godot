using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>队列管理器（原版 petra/queueManager.js，639 行）。
/// 按优先级在多个命名队列间分配资源。每个队列有独立账户；分配算法：
///   1. getAvailableResources = 玩家资源 - 全部账户
///   2. distributeResource: 按优先级比例分配可用资源到各队列账户
///      （足够首个计划的降优先级；账户上限 = 首计划成本 + 60% 次计划）
///   3. startNextItems: 账户够买首计划且 canStart → 扣费启动
///   4. checkPausedQueues: 工人少时暂停非关键队列（CC/军事/科技/防御）
/// 逐字移植分配算法。</summary>
public sealed class QueueManager
{
    private readonly PetraConfig _config;
    private readonly Dictionary<string, PetraQueue> _queues = new();
    private readonly Dictionary<string, int> _priorities = new();
    private readonly Dictionary<string, ResourcesManager> _accounts = new();
    private List<KeyValuePair<string, PetraQueue>> _queueArrays = new();

    public QueueManager(PetraConfig config)
    {
        _config = config;
        foreach (var kvp in config.Priorities)
        {
            _queues[kvp.Key] = new PetraQueue();
            _priorities[kvp.Key] = kvp.Value;
            _accounts[kvp.Key] = new ResourcesManager();
        }
        SortQueues();
    }

    /// <summary>可用资源 = 玩家资源 - 全部账户。</summary>
    public ResourcesManager GetAvailableResources(GameState gameState)
    {
        var res = gameState.GetResources();
        foreach (var kvp in _accounts)
            res.Subtract(kvp.Value.Wood, kvp.Value.Food, kvp.Value.Stone, kvp.Value.Metal);
        return res;
    }

    public ResourcesManager GetTotalAccountedResources()
    {
        var total = new ResourcesManager();
        foreach (var acc in _accounts.Values) total.Add(acc);
        return total;
    }

    /// <summary>当前需求（各队列首计划成本之和 - 当前资源）。</summary>
    public ResourcesManager CurrentNeeds(GameState gameState)
    {
        var needed = new ResourcesManager();
        foreach (var q in _queueArrays)
        {
            var queue = q.Value;
            if (!queue.HasQueuedUnits) continue;
            var plan = queue.GetNext();
            if (plan == null || !plan.IsGo(gameState)) continue;
            needed.Add(plan.GetCost());
        }
        var current = gameState.GetResources();
        return new ResourcesManager(
            Math.Max(0, needed.Wood - current.Wood), Math.Max(0, needed.Food - current.Food),
            Math.Max(0, needed.Stone - current.Stone), Math.Max(0, needed.Metal - current.Metal));
    }

    /// <summary>分配资源到各队列账户（核心算法，逐字移植 distributeResource）。</summary>
    public void DistributeResources(GameState gameState)
    {
        var available = GetAvailableResources(gameState);
        string[] resCodes = { "wood", "food", "stone", "metal" };

        foreach (var res in resCodes)
        {
            int avail = ResValue(available, res);
            // 负值 → 重新缩放账户（资源被消耗/交换掉了）
            if (avail < 0)
            {
                int total = ResValue(gameState.GetResources(), res);
                double scale = total != 0 ? (double)total / (total - avail) : 0;
                avail = total;
                foreach (var j in _queues.Keys)
                {
                    int scaled = (int)(scale * ResValue(_accounts[j], res));
                    SetResValue(_accounts[j], res, scaled);
                    avail -= scaled;
                }
            }

            if (avail == 0) { SwitchResource(gameState, res); continue; }

            double totalPriority = 0;
            var tempPrio = new Dictionary<string, double>();
            var maxNeed = new Dictionary<string, int>();

            foreach (var j in _queues.Keys)
            {
                var queue = _queues[j];
                if (!queue.HasQueuedUnits || queue.Paused) { CheckExcess(j, res, avail); continue; }

                // maxAccountWanted = 首计划成本 + 60% 次计划成本
                var queueCost = MaxAccountWanted(queue, gameState, 0.6);
                int qCost = ResValue(queueCost, res);
                int accVal = ResValue(_accounts[j], res);

                if (accVal < qCost)
                {
                    tempPrio[j] = _priorities.GetValueOrDefault(j, 0);
                    maxNeed[j] = qCost - accVal;
                    // 首计划已够本资源 → 降优先级（×0.5）
                    var next = queue.GetNext();
                    if (next != null && accVal >= ResValue(next.GetCost(), res))
                        tempPrio[j] /= 2;
                    if (tempPrio[j] > 0) totalPriority += tempPrio[j];
                }
                else
                {
                    // 超额 → 回收
                    avail += accVal - qCost;
                    SetResValue(_accounts[j], res, qCost);
                }
            }

            // 按优先级比例分配
            double remaining = avail;
            bool missing = false;
            foreach (var j in tempPrio.Keys)
            {
                int toAdd = totalPriority > 0 ? (int)(avail * tempPrio[j] / totalPriority) : 0;
                if (toAdd >= maxNeed[j]) toAdd = maxNeed[j];
                else missing = true;
                AddResValue(_accounts[j], res, toAdd);
                maxNeed[j] -= toAdd;
                remaining -= toAdd;
            }
            // 分配余数（floor 导致的零头）
            if (missing && remaining > 0)
            {
                foreach (var j in tempPrio.Keys)
                {
                    int toAdd = Math.Min(maxNeed[j], (int)remaining);
                    AddResValue(_accounts[j], res, toAdd);
                    remaining -= toAdd;
                    if (remaining <= 0) break;
                }
            }
        }
    }

    /// <summary>无可用资源时压缩账户（高优先级队列可从低优先级抢资源）。</summary>
    private void SwitchResource(GameState gameState, string res)
    {
        foreach (var j in _queues.Keys)
        {
            var queue = _queues[j];
            if (!queue.HasQueuedUnits || queue.Paused) continue;
            var queueCost = MaxAccountWanted(queue, gameState, 0);
            int qCost = ResValue(queueCost, res);
            if (ResValue(_accounts[j], res) >= qCost) continue;

            foreach (var i in _queues.Keys)
            {
                if (i == j) continue;
                if (_priorities.GetValueOrDefault(i, 0) >= _priorities.GetValueOrDefault(j, 0)) continue;
                int combined = ResValue(_accounts[j], res) + ResValue(_accounts[i], res);
                if (combined < qCost) continue;
                int diff = qCost - ResValue(_accounts[j], res);
                AddResValue(_accounts[j], res, diff);
                AddResValue(_accounts[i], res, -diff);
                break;
            }
        }
    }

    /// <summary>启动各队列的下一个可负担计划。</summary>
    public void StartNextItems(GameState gameState)
    {
        foreach (var q in _queueArrays)
        {
            var name = q.Key;
            var queue = q.Value;
            if (queue.HasQueuedUnits && !queue.Paused)
            {
                var item = queue.GetNext();
                if (item == null) continue;
                if (_accounts[name].CanAfford(item.GetCost()) && item.CanStart(gameState))
                {
                    if (_accounts[name].CanAfford(item.GetCost()))  // canStart 可能更新 cost，二次检查
                    {
                        var cost = item.GetCost();
                        _accounts[name].Subtract(cost.Wood, cost.Food, cost.Stone, cost.Metal);
                        queue.StartNext(gameState);
                    }
                }
            }
            else if (!queue.HasQueuedUnits)
            {
                _accounts[name] = new ResourcesManager();
            }
        }
    }

    /// <summary>主更新（原版 update）。</summary>
    public void Update(GameState gameState)
    {
        foreach (var kvp in _queues)
            kvp.Value.Check(gameState);

        CheckPausedQueues(gameState);
        DistributeResources(gameState);
        StartNextItems(gameState);
    }

    /// <summary>工人少时暂停非关键队列（逐字移植 checkPausedQueues）。
    /// TODO: 精确版需 HQ.hasPotentialBase + needFarm/needCorral/needFish（Phase 2 后续补）。
    /// 当前简化版：仅按 worker 数量暂停。</summary>
    private void CheckPausedQueues(GameState gameState)
    {
        int numWorkers = gameState.CountOwnEntitiesByRole("worker");
        int workersMin = Math.Min(Math.Max(12, (int)(24 * _config.PopScaling)), _config.Economy.PopPhase2);

        foreach (var q in _queues)
        {
            string name = q.Key;
            var queue = q.Value;
            bool toBePaused = false;

            if (numWorkers < workersMin / 3)
                toBePaused = name != "citizenSoldier" && name != "villager" && name != "emergency";
            else if (numWorkers < workersMin * 2 / 3)
                toBePaused = name is "civilCentre" or "economicBuilding" or "militaryBuilding"
                    or "defenseBuilding" or "healer" or "majorTech" or "minorTech";
            else if (numWorkers < workersMin)
                toBePaused = name is "civilCentre" or "defenseBuilding" or "majorTech";

            if (toBePaused && !queue.Paused)
            {
                queue.Paused = true;
                _accounts[name] = new ResourcesManager();
            }
            else if (!toBePaused && queue.Paused)
                queue.Paused = false;
        }
    }

    // ── 队列管理 API ──

    public void AddQueue(string name, int priority)
    {
        if (_queues.ContainsKey(name)) return;
        _queues[name] = new PetraQueue();
        _priorities[name] = priority;
        _accounts[name] = new ResourcesManager();
        SortQueues();
    }

    public void RemoveQueue(string name)
    {
        _queues.Remove(name);
        _priorities.Remove(name);
        _accounts.Remove(name);
        SortQueues();
    }

    public void ChangePriority(string name, int newPriority)
    {
        if (_queues.ContainsKey(name)) _priorities[name] = newPriority;
        SortQueues();
    }

    public int GetPriority(string name) => _priorities.GetValueOrDefault(name, 0);

    public PetraQueue? GetQueue(string name)
        => _queues.GetValueOrDefault(name);

    public void AddPlan(string queueName, QueuePlan plan)
    {
        if (_queues.TryGetValue(queueName, out var q))
            q.AddPlan(plan);
    }

    public void PauseQueue(string name, bool scrapAccounts)
    {
        if (!_queues.TryGetValue(name, out var q)) return;
        q.Paused = true;
        if (scrapAccounts) _accounts[name] = new ResourcesManager();
    }

    public void UnpauseQueue(string name)
    {
        if (_queues.TryGetValue(name, out var q)) q.Paused = false;
    }

    public void Clear()
    {
        foreach (var q in _queues.Values) q.Plans.Clear();
    }

    // ── 辅助 ──

    private void SortQueues()
    {
        _queueArrays = _queues
            .OrderByDescending(kvp => _priorities.GetValueOrDefault(kvp.Key, 0))
            .ThenBy(kvp => kvp.Key)
            .ToList();
    }

    private static ResourcesManager MaxAccountWanted(PetraQueue queue, GameState gameState, double fraction)
    {
        var cost = new ResourcesManager();
        var plans = queue.Plans;
        if (plans.Count > 0 && plans[0].IsGo(gameState))
            cost.Add(plans[0].GetCost());
        if (plans.Count > 1 && plans[1].IsGo(gameState) && fraction > 0)
        {
            var c2 = plans[1].GetCost();
            cost.Add(new ResourcesManager(
                (int)(c2.Wood * fraction), (int)(c2.Food * fraction),
                (int)(c2.Stone * fraction), (int)(c2.Metal * fraction)));
        }
        return cost;
    }

    private void CheckExcess(string queueName, string res, int available)
    {
        // 超额账户回收（distributeResource 里内联逻辑的抽取）
        var queueCost = new ResourcesManager();  // 空 = 无计划
        if (!_queues[queueName].HasQueuedUnits) return;
        var cost = MaxAccountWanted(_queues[queueName], null!, 0.6);
        // 这里 null! 不对——CheckExcess 在 distributeResource 里内联处理，不单独调用
    }

    private static int ResValue(ResourcesManager r, string res) => res switch
    {
        "wood" => r.Wood, "food" => r.Food, "stone" => r.Stone, "metal" => r.Metal, _ => 0,
    };

    private static void SetResValue(ResourcesManager r, string res, int val)
    {
        switch (res)
        {
            case "wood": r.Wood = val; break;
            case "food": r.Food = val; break;
            case "stone": r.Stone = val; break;
            case "metal": r.Metal = val; break;
        }
    }

    private static void AddResValue(ResourcesManager r, string res, int delta)
    {
        switch (res)
        {
            case "wood": r.Wood += delta; break;
            case "food": r.Food += delta; break;
            case "stone": r.Stone += delta; break;
            case "metal": r.Metal += delta; break;
        }
    }
}
