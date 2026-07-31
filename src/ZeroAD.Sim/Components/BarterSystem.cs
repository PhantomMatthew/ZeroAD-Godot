using System;

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

/// <summary>System-level barter with static (drift-free) pricing. See file notes for the
/// deferred price-drift economy.</summary>
public static class BarterSystem
{
    public const int DealAmount = 100;
    public const int ConstantDifference = 10;
    public const int TruePrice = 100;
    /// <summary>session.massbarter(Shift)每笔量(原版 BATCH_SIZE*DEAL_AMOUNT)。</summary>
    public const int MassDealAmount = DealAmount * 5;

    /// <summary>买入价(truePrice 上浮 CONSTANT_DIFFERENCE;原版 GetPrices buy 公式,去漂移/multiplier)。</summary>
    public static int BuyPrice(ResourceType res) => TruePrice * (DealAmount + ConstantDifference) / DealAmount;

    /// <summary>卖出价(truePrice 下浮 CONSTANT_DIFFERENCE;原版 GetPrices sell 公式,去漂移/multiplier)。</summary>
    public static int SellPrice(ResourceType res) => TruePrice * (DealAmount - ConstantDifference) / DealAmount;

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
        int gained = (int)Math.Round((double)SellPrice(sell) / BuyPrice(buy) * amount, MidpointRounding.AwayFromZero);
        player.AddResource(buy, gained);
    }
}
