namespace ZeroAD.Sim.AI.Petra;

/// <summary>地图掩码常量(原版 petra/mapMask.js):InfoMap/领土图/前线图
/// 的位标志。Petra 建造选址/基地扩张/防御选址按位与过滤。</summary>
public static class MapMask
{
    /// <summary>地图外(原版 outside=1)。</summary>
    public const byte Outside = 1;
    /// <summary>地图边界(原版 border=2:内侧距边 ≤80m 的 cell)。</summary>
    public const byte Border = 2;
    /// <summary>完整边界 = Outside|Border(原版 fullBorder:建造选址 disfavor)。</summary>
    public const byte FullBorder = Outside | Border;
    /// <summary>窄前线(原版 narrowFrontier=4:我方领土与敌/中界线内侧)。</summary>
    public const byte NarrowFrontier = 4;
    /// <summary>宽前线(原版 largeFrontier=8:领土扩张的外推线,窄线外侧)。</summary>
    public const byte LargeFrontier = 8;
    /// <summary>完整前线 = Narrow|Large(原版 fullFrontier:防御塔选址过滤)。</summary>
    public const byte FullFrontier = NarrowFrontier | LargeFrontier;
}
