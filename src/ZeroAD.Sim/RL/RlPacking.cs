using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.RL;

/// <summary>CatalogId packing for Tribute / Barter / trading-goods / control groups.</summary>
public static class RlPacking
{
    public static readonly int[] TributeAmounts = { 100, 500, 1000 };

    public static ResourceType ResourceOf(int catalogId)
    {
        int v = catalogId % 4;
        if (v < 0) v += 4;
        return (ResourceType)v;
    }

    public static int TributeAmount(int catalogId)
    {
        int idx = (catalogId >> 2) & 3;
        if (idx >= TributeAmounts.Length) idx = 0;
        return TributeAmounts[idx];
    }

    public static void BarterPair(int catalogId, out ResourceType sell, out ResourceType buy, out int amount)
    {
        sell = ResourceOf(catalogId);
        buy = ResourceOf(catalogId >> 2);
        if (buy == sell) buy = (ResourceType)(((int)sell + 1) % 4);
        amount = ((catalogId >> 4) & 1) != 0 ? 500 : 100;
    }

    public static void TradingGoods(int catalogId, out int wood, out int food, out int stone, out int metal)
    {
        int w = catalogId & 3;
        int f = (catalogId >> 2) & 3;
        int s = (catalogId >> 4) & 3;
        int m = (catalogId >> 6) & 3;
        int sum = w + f + s + m;
        if (sum <= 0)
        {
            wood = 100;
            food = stone = metal = 0;
            return;
        }
        wood = 100 * w / sum;
        food = 100 * f / sum;
        stone = 100 * s / sum;
        metal = 100 - wood - food - stone;
        if (metal < 0)
        {
            wood += metal;
            metal = 0;
        }
    }

    public const int FormationShapeCount = 4;
    public const int ControlGroupCount = 10;
    public const int ControlAssignBase = 10;
    public const int ControlRecallBase = 20;

    public static bool IsControlAssign(int catalogId, out int group)
    {
        group = catalogId - ControlAssignBase;
        return group >= 0 && group < ControlGroupCount;
    }

    public static bool IsControlRecall(int catalogId, out int group)
    {
        group = catalogId - ControlRecallBase;
        return group >= 0 && group < ControlGroupCount;
    }
}
