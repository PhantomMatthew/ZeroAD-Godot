using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot;

/// <summary>结算统计页（全屏，6 标签页）。镜像原版 gui/summary/layout.js 的布局。
/// 由 GameOverOverlay 的"查看统计"按钮打开：接收 MatchSummary 数据，展示每玩家的计数器。
///
/// 6 个标签页（TabContainer）：Score / Structures / Units / Resources / Market / Misc。
/// 每个 tab 是一个 Tree（多列），每行一个玩家，末尾可选团队聚合行。</summary>
public sealed partial class SummaryPanel : CanvasLayer
{
    private readonly MatchSummary _summary;
    private TabContainer _tabs = null!;

    public SummaryPanel(MatchSummary summary)
    {
        _summary = summary;
        Layer = 55;  // 在 HUD 之上、GameOverOverlay(50) 之上
    }

    public override void _Ready()
    {
        // 全屏背景
        var bg = new ColorRect
        {
            Color = new Color(0.04f, 0.035f, 0.03f, 0.97f),
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(bg);

        var outer = new VBoxContainer
        {
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 20, OffsetTop = 20, OffsetRight = -20, OffsetBottom = -20,
        };
        AddChild(outer);

        // 标题
        var title = new Label { Text = "对局统计", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        outer.AddChild(title);

        // 标签容器
        _tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        outer.AddChild(_tabs);

        BuildScoreTab();
        BuildStructuresTab();
        BuildUnitsTab();
        BuildResourcesTab();
        BuildMarketTab();
        BuildMiscTab();

        // 底部关闭按钮
        var closeBtn = new Button
        {
            Text = "关闭",
            CustomMinimumSize = new Vector2(120, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        closeBtn.Pressed += Close;
        outer.AddChild(closeBtn);
    }

    public void Open() => Visible = true;
    public void Close() => QueueFree();

    // ── 通用 Tree 构建辅助 ──

    private static Tree MakeTree(string[] columns)
    {
        var tree = new Tree
        {
            Columns = columns.Length,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        for (int i = 0; i < columns.Length; i++)
            tree.SetColumnTitle(i, columns[i]);
        return tree;
    }

    private static void AddPlayerRow(Tree tree, string name, params object[] values)
    {
        var root = tree.GetRoot() ?? tree.CreateItem();
        var item = tree.CreateItem(root);
        item.SetText(0, name);
        for (int i = 0; i < values.Length && i + 1 < tree.Columns; i++)
            item.SetText(i + 1, values[i]?.ToString() ?? "0");
    }

    private Dictionary<int, List<PlayerSummary>> TeamGroups()
        => _summary.Players.GroupBy(p => p.Team).ToDictionary(g => g.Key, g => g.ToList());

    // ── 标签页 1: Score ──

    private void BuildScoreTab()
    {
        var tree = MakeTree(new[] { "玩家", "总分", "经济", "军事", "探索", "状态" });
        foreach (var p in _summary.Players)
            AddPlayerRow(tree, $"玩家 {p.PlayerId} ({p.Civ})",
                p.Score.total, p.Score.economy, p.Score.military, p.Score.exploration, p.State);
        _tabs.AddChild(WrapInTab("Score", tree));
    }

    // ── 标签页 2: Structures ──

    private void BuildStructuresTab()
    {
        var tree = MakeTree(new[] { "玩家", "建造", "损失", "摧毁敌方", "占领", "损失价值", "摧毁价值" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                SumOrZero(s.BuildingsConstructed), SumOrZero(s.BuildingsLost),
                SumOrZero(s.EnemyBuildingsDestroyed), SumOrZero(s.BuildingsCaptured),
                s.BuildingsLostValue, s.EnemyBuildingsDestroyedValue);
        }
        _tabs.AddChild(WrapInTab("Structures", tree));
    }

    // ── 标签页 3: Units ──

    private void BuildUnitsTab()
    {
        var tree = MakeTree(new[] { "玩家", "训练", "损失", "击杀", "占领", "损失价值", "击杀价值" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                SumOrZero(s.UnitsTrained), SumOrZero(s.UnitsLost),
                SumOrZero(s.EnemyUnitsKilled), SumOrZero(s.UnitsCaptured),
                s.UnitsLostValue, s.EnemyUnitsKilledValue);
        }
        _tabs.AddChild(WrapInTab("Units", tree));
    }

    // ── 标签页 4: Resources ──

    private void BuildResourcesTab()
    {
        var tree = MakeTree(new[] { "玩家", "采集总量", "木材", "食物", "石头", "金属", "花费", "贡品(送/收)" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                SumExceptTotal(s.ResourcesGathered),
                GetOrZero(s.ResourcesGathered, "wood"), GetOrZero(s.ResourcesGathered, "food"),
                GetOrZero(s.ResourcesGathered, "stone"), GetOrZero(s.ResourcesGathered, "metal"),
                SumExceptTotal(s.ResourcesUsed),
                $"{s.TributesSent}/{s.TributesReceived}");
        }
        _tabs.AddChild(WrapInTab("Resources", tree));
    }

    // ── 标签页 5: Market ──

    private void BuildMarketTab()
    {
        var tree = MakeTree(new[] { "玩家", "贸易收入", "售出", "购入", "战利品" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                s.TradeIncome, SumExceptTotal(s.ResourcesSold), SumExceptTotal(s.ResourcesBought), s.LootCollected);
        }
        _tabs.AddChild(WrapInTab("Market", tree));
    }

    // ── 标签页 6: Miscellaneous ──

    private void BuildMiscTab()
    {
        var tree = MakeTree(new[] { "玩家", "K/D 比", "地图探索%", "地图控制%", "峰值控制%", "宝藏" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            int killed = SumExceptTotal(s.EnemyUnitsKilled);
            int lost = SumExceptTotal(s.UnitsLost);
            double kd = lost > 0 ? (double)killed / lost : killed;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                $"{kd:F2}", $"{s.PercentMapExplored:F1}", $"{s.PercentMapControlled:F1}",
                $"{s.PeakPercentMapControlled:F1}", s.TreasuresCollected);
        }
        _tabs.AddChild(WrapInTab("Misc", tree));
    }

    // ── 辅助 ──

    private static Control WrapInTab(string title, Tree tree)
    {
        var vbox = new VBoxContainer { Name = title };
        vbox.AddChild(tree);
        return vbox;
    }

    private static int SumOrZero(Dictionary<string, int> dict)
        => dict.TryGetValue("total", out var v) ? v : dict.Values.Sum();

    private static int SumExceptTotal(Dictionary<string, int> dict)
        => dict.Where(kvp => kvp.Key != "total").Sum(kvp => kvp.Value);

    private static int GetOrZero(Dictionary<string, int> dict, string key)
        => dict.TryGetValue(key, out var v) ? v : 0;
}
