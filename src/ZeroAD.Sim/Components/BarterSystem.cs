using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// BarterSystem — 系统级资源易物,移植自原版 simulation/components/Barter.js。
//
// 原版 Barter 是挂在 SYSTEM_ENTITY 上的单一全局组件(非每市场):一份 priceDifferences[]
// 驱动所有玩家的易物价,每笔交易推高 DIFFERENCE_PER_DEAL、每 5s 回落 DIFFERENCE_RESTORE,
// 再乘以玩家 BarterMultiplier(科技/光环可改)。本端口本轮落地**静态价**(priceDifferences 恒 0、
// 无漂移、无 per-player multiplier)——价漂移经济列为 backlog(与 ceasefire/spy/attack-request 同批)。
// 静态价足以支撑 Trade 面板的买/卖显示与 ExchangeResources 换算;日后补漂移只改本文件。
//
// 忠实常量(原版 Barter.prototype.*):
//   DEAL_AMOUNT = 100          每笔基准量(或 BATCH_SIZE*DEAL_AMOUNT = 500,session.massbarter)
//   CONSTANT_DIFFERENCE = 10   买/卖围绕 truePrice 的固定价差
//   truePrice = 100            4 资源均 100(resources/*.json)

/// <summary>System-level barter with price drift (Barter.js 全量:每笔推涨价差、
/// 每 5s 回落、truePrice ± CONSTANT_DIFFERENCE ± priceDifferences)。价差状态是全局
/// 单份(所有玩家共享);存档经 BarterStateComponent(挂系统实体)骑缝序列化。</summary>
public static class BarterSystem
{
    public const int DealAmount = 100;
    public const int ConstantDifference = 10;
    public const int TruePrice = 100;
    /// <summary>session.massbarter(Shift)每笔量(原版 BATCH_SIZE*DEAL_AMOUNT)。</summary>
    public const int MassDealAmount = DealAmount * 5;
    /// <summary>每笔基准量的价差推涨(原版 DIFFERENCE_PER_DEAL)。</summary>
    public const float DifferencePerDeal = 2f;
    /// <summary>每恢复周期的回落上限(原版 DIFFERENCE_RESTORE)。</summary>
    public const float DifferenceRestore = 0.5f;
    /// <summary>恢复周期毫秒(原版 RESTORE_TIMER_INTERVAL)。</summary>
    public const float RestoreIntervalMs = 5000f;

    // 全局价差(drift)状态:resource → 相对 truePrice 的偏移(正 = 更贵)。
    private static readonly Dictionary<ResourceType, float> _diff = new()
    {
        [ResourceType.Food] = 0f, [ResourceType.Wood] = 0f,
        [ResourceType.Stone] = 0f, [ResourceType.Metal] = 0f,
    };
    private static float _restoreElapsed;

    /// <summary>价差快照(BarterStateComponent 存档用)。</summary>
    public static IReadOnlyDictionary<ResourceType, float> PriceDifferences => _diff;

    /// <summary>重置价差(SimSystem.Init 新世界语义;防跨局/跨测试静态泄漏)。</summary>
    public static void Reset()
    {
        foreach (var res in _diff.Keys.ToArray()) _diff[res] = 0f;
        _restoreElapsed = 0f;
    }

    /// <summary>存档恢复(整表覆写)。</summary>
    public static void RestoreDifferences(IReadOnlyDictionary<ResourceType, float> snap, float elapsed)
    {
        foreach (var kv in snap) _diff[kv.Key] = kv.Value;
        _restoreElapsed = elapsed;
    }

    /// <summary>买入价(truePrice + 固定差 + 漂移,× 玩家乘数;原版 GetPrices buy 公式)。
    /// multiplier = 玩家模板/科技修正(Player/BarterMultiplier/Buy/{res};缺省 1)。</summary>
    public static int BuyPrice(ResourceType res, float multiplier = 1f)
        => (int)MathF.Round(TruePrice * (DealAmount + ConstantDifference
            + (int)MathF.Round(_diff.GetValueOrDefault(res))) * multiplier / DealAmount);

    /// <summary>卖出价(truePrice − 固定差 + 漂移,× 玩家乘数;原版 GetPrices sell 公式)。</summary>
    public static int SellPrice(ResourceType res, float multiplier = 1f)
        => (int)MathF.Round(TruePrice * (DealAmount - ConstantDifference
            + (int)MathF.Round(_diff.GetValueOrDefault(res))) * multiplier / DealAmount);

    /// <summary>每笔推涨(原版 ExchangeResources 尾部:sell 侧 +、buy 侧 −)。</summary>
    private static void ApplyDealDrift(ResourceType sell, ResourceType buy, int amount)
    {
        float per = DifferencePerDeal * amount / DealAmount;
        _diff[sell] = Math.Min(ConstantDifference, _diff[sell] + per);
        _diff[buy] = Math.Max(-ConstantDifference, _diff[buy] - per);
    }

    /// <summary>价差回落(原版 ProgressTimeout:每 5s 向 0 收敛,步长 ±RESTORE)。
    /// 由 SimBridge 每 tick 驱动(锁步时基)。</summary>
    public static void TickRestore(float dt)
    {
        _restoreElapsed += dt * 1000f;
        while (_restoreElapsed >= RestoreIntervalMs)
        {
            _restoreElapsed -= RestoreIntervalMs;
            foreach (var res in _diff.Keys.ToArray())
                _diff[res] -= Math.Clamp(_diff[res], -DifferenceRestore, DifferenceRestore);
        }
    }

    /// <summary>执行一笔易物(原版 Barter.js ExchangeResources)。校验:amount ∈ {100,500}、
    /// 玩家可易物(有市场)、买卖非同资源、源余额足;扣 sell、按 sellPrice/buyPrice 换算加 buy。
    /// 任一校验失败静默返回(对齐原版"非 100/500 直接丢")。</summary>
    public static void ExchangeResources(ComponentManager cm, PlayerComponent player, int playerId,
        ResourceType sell, ResourceType buy, int amount)
    {
        if (amount != DealAmount && amount != MassDealAmount) return;
        if (sell == buy) return;
        if (!player.CanBarter(cm, playerId)) return;
        if (!player.TrySpend(sell, amount)) return;
        // 原版:价格 × 玩家乘数(模板/科技),换算比例 = sell×mult.sell / buy×mult.buy。
        int gained = (int)Math.Round(
            (double)SellPrice(sell, player.GetBarterMultiplierSell(sell.ToString().ToLowerInvariant()))
            / BuyPrice(buy, player.GetBarterMultiplierBuy(buy.ToString().ToLowerInvariant()))
            * amount, MidpointRounding.AwayFromZero);
        player.AddResource(buy, gained);
        // 价漂移:卖出资源涨、买入资源跌(原版 ExchangeResources 尾部)。
        ApplyDealDrift(sell, buy, amount);
    }
}

/// <summary>价差状态的存档骑缝(挂系统实体):序列化 BarterSystem 全局漂移表,
/// 使存档/锁步哈希覆盖经济状态。</summary>
[Component("BarterState", "BarterState")]
public sealed class BarterStateComponent : ComponentBase
{
    public override void Serialize(ISerializer s)
    {
        var d = BarterSystem.PriceDifferences;
        s.NumberFixed("food", Maths.Fixed.FromFloat(d.GetValueOrDefault(ResourceType.Food)));
        s.NumberFixed("wood", Maths.Fixed.FromFloat(d.GetValueOrDefault(ResourceType.Wood)));
        s.NumberFixed("stone", Maths.Fixed.FromFloat(d.GetValueOrDefault(ResourceType.Stone)));
        s.NumberFixed("metal", Maths.Fixed.FromFloat(d.GetValueOrDefault(ResourceType.Metal)));
    }

    public override void Deserialize(IDeserializer d)
    {
        var snap = new Dictionary<ResourceType, float>
        {
            [ResourceType.Food] = d.NumberFixed("food").ToFloat(),
            [ResourceType.Wood] = d.NumberFixed("wood").ToFloat(),
            [ResourceType.Stone] = d.NumberFixed("stone").ToFloat(),
            [ResourceType.Metal] = d.NumberFixed("metal").ToFloat(),
        };
        BarterSystem.RestoreDifferences(snap, 0f);
    }
}
