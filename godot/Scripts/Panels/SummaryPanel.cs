using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot;

/// <summary>结算统计页(全屏,6 标签页)。镜像原版 gui/summary/summary.xml 的布局与皮肤:
/// ModernFade 全屏渐暗 + ModernWindow 窗口皮肤 + 标题行(结果/地图名)+ 标签按钮排
/// + 内容帧 + 右下红色 Continue(ModernButtonRed → 回主菜单)。
/// 由 GameOverOverlay 的"查看统计"按钮打开:接收 MatchSummary 数据,展示每玩家的计数器。
///
/// 6 个标签页:Score / Structures / Units / Resources / Market / Misc。
/// 每个 tab 是一个 Tree(多列),每行一个玩家。</summary>
public sealed partial class SummaryPanel : CanvasLayer
{
    private readonly MatchSummary _summary;
    private readonly int _localPlayerId;
    private readonly List<Control> _pages = new();
    private readonly List<Button> _tabButtons = new();

    public SummaryPanel(MatchSummary summary, int localPlayerId = 1)
    {
        _summary = summary;
        _localPlayerId = localPlayerId;
        Layer = 55;  // 在 HUD 之上、GameOverOverlay(50) 之上
    }

    public override void _Ready()
    {
        // ModernFade:全屏渐暗(原版 fadeImage;无独立贴图,深色等价)。
        var fade = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(fade);

        // ModernWindow:全屏窗口皮肤(原版 summaryWindow style=ModernWindow)。
        var window = new Panel
        {
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 16, OffsetTop = 16, OffsetRight = -16, OffsetBottom = -16,
        };
        UITheme.ApplyModernDialog(window);
        AddChild(window);

        var outer = new MarginContainer
        {
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
        };
        outer.AddThemeConstantOverride("margin_left", 24);
        outer.AddThemeConstantOverride("margin_top", 18);
        outer.AddThemeConstantOverride("margin_right", 24);
        outer.AddThemeConstantOverride("margin_bottom", 18);
        window.AddChild(outer);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        outer.AddChild(vbox);

        // 标题(原版 summaryWindowTitle)。
        var title = new Label
        {
            Text = "对局统计",
            Theme = UITheme.GetTheme(),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        // 头部行(原版 y26..92):左=胜负结果,中=地图名,右=占位(时长数据暂无)。
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(header);
        var local = _summary.Players.FirstOrDefault(p => p.PlayerId == _localPlayerId);
        bool won = local?.State == "Won" || _summary.WinnerPlayerId == _localPlayerId;
        var resultLabel = new Label
        {
            Text = won ? "Victory!" : "Defeat",
            Theme = UITheme.GetTheme(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        resultLabel.AddThemeFontSizeOverride("font_size", 18);
        resultLabel.AddThemeColorOverride("font_color",
            won ? new Color(0.20f, 0.78f, 0.30f) : new Color(0.85f, 0.22f, 0.18f));
        header.AddChild(resultLabel);
        string mapName = System.IO.Path.GetFileNameWithoutExtension(_summary.MapPath).Replace('_', ' ');
        var mapLabel = new Label
        {
            Text = mapName,
            Theme = UITheme.GetTheme(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        mapLabel.AddThemeFontSizeOverride("font_size", 18);
        header.AddChild(mapLabel);
        var rightPad = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddChild(rightPad);

        // 金色分隔条(原版 tabDividerLeft/Right = ModernTabHorizontalSpacer)。
        var sepTex = UITheme.TryLoad("res://assets/ui/modern/gold-separator.png");
        if (sepTex != null)
        {
            var sep = new TextureRect
            {
                Texture = sepTex,
                CustomMinimumSize = new Vector2(0, 4),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Tile,
            };
            vbox.AddChild(sep);
        }

        // 标签按钮排(原版 tab_buttons.xml 一排石头按钮;替代默认 TabContainer 皮肤)。
        var tabRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        tabRow.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(tabRow);
        var group = new ButtonGroup();
        foreach (var (tabTitle, _) in TabDefs)
        {
            var btn = new Button
            {
                Text = tabTitle,
                Theme = UITheme.GetTheme(),
                ToggleMode = true,
                ButtonGroup = group,
                CustomMinimumSize = new Vector2(110, 28),
            };
            StoneButtonStyle.Apply(btn, StoneButtonStyle.FindBinariesDir());
            int idx = _tabButtons.Count;
            btn.Pressed += () => SelectTab(idx);
            tabRow.AddChild(btn);
            _tabButtons.Add(btn);
        }

        // 内容帧(原版 generalPanel = ModernTabHorizontalFrame;深色帧近似)。
        var frame = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        frame.AddThemeStyleboxOverride("panel", UITheme.MakeModernDarkBox());
        vbox.AddChild(frame);

        foreach (var (tabTitle, builder) in TabDefs)
        {
            var tree = builder();
            tree.Visible = false;
            frame.AddChild(tree);
            _pages.Add(tree);
        }
        SelectTab(0);

        // 底部行:右侧红色 Continue(原版 continueButton style=ModernButtonRed,
        // 100%-200..-20 × 100%-48..-20 → 180×28;回主菜单)。
        var bottomRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End,
        };
        vbox.AddChild(bottomRow);
        var continueBtn = new Button
        {
            Text = "Continue",
            Theme = UITheme.GetRedButtonTheme(),
            CustomMinimumSize = new Vector2(180, 28),
        };
        continueBtn.Pressed += OnContinue;
        bottomRow.AddChild(continueBtn);
    }

    public void Open() => Visible = true;
    public void Close() => QueueFree();

    private void SelectTab(int idx)
    {
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == idx;
        for (int i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].ButtonPressed = i == idx;
    }

    /// <summary>Continue(原版 continueButton):离开本局 → 回主菜单。</summary>
    private void OnContinue() =>
        GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");

    // ── 标签页定义(标题 + 构建器)──

    private IEnumerable<(string title, System.Func<Tree> build)> TabDefs
    {
        get
        {
            yield return ("Score", BuildScoreTree);
            yield return ("Structures", BuildStructuresTree);
            yield return ("Units", BuildUnitsTree);
            yield return ("Resources", BuildResourcesTree);
            yield return ("Market", BuildMarketTree);
            yield return ("Misc", BuildMiscTree);
        }
    }

    // ── 通用 Tree 构建辅助 ──

    private static Tree MakeTree(string[] columns)
    {
        var tree = new Tree
        {
            Columns = columns.Length,
            ColumnTitlesVisible = true,
            HideRoot = true,
            Theme = UITheme.GetTheme(),
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

    // ── 标签页 1: Score ──

    private Tree BuildScoreTree()
    {
        var tree = MakeTree(new[] { "玩家", "总分", "经济", "军事", "探索", "状态" });
        foreach (var p in _summary.Players)
            AddPlayerRow(tree, $"玩家 {p.PlayerId} ({p.Civ})",
                p.Score.total, p.Score.economy, p.Score.military, p.Score.exploration, p.State);
        return tree;
    }

    // ── 标签页 2: Structures ──

    private Tree BuildStructuresTree()
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
        return tree;
    }

    // ── 标签页 3: Units ──

    private Tree BuildUnitsTree()
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
        return tree;
    }

    // ── 标签页 4: Resources ──

    private Tree BuildResourcesTree()
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
        return tree;
    }

    // ── 标签页 5: Market ──

    private Tree BuildMarketTree()
    {
        var tree = MakeTree(new[] { "玩家", "贸易收入", "售出", "购入", "战利品" });
        foreach (var p in _summary.Players)
        {
            var s = p.Stats;
            AddPlayerRow(tree, $"玩家 {p.PlayerId}",
                s.TradeIncome, SumExceptTotal(s.ResourcesSold), SumExceptTotal(s.ResourcesBought), s.LootCollected);
        }
        return tree;
    }

    // ── 标签页 6: Miscellaneous ──

    private Tree BuildMiscTree()
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
        return tree;
    }

    // ── 辅助 ──

    private static int SumOrZero(Dictionary<string, int> dict)
        => dict.TryGetValue("total", out var v) ? v : dict.Values.Sum();

    private static int SumExceptTotal(Dictionary<string, int> dict)
        => dict.Where(kvp => kvp.Key != "total").Sum(kvp => kvp.Value);

    private static int GetOrZero(Dictionary<string, int> dict, string key)
        => dict.TryGetValue(key, out var v) ? v : 0;
}
