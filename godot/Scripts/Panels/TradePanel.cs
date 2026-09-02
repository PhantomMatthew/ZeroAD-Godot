using System;
using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// Trade 面板(对齐 session/trade/TradeDialog.xml + Barter.js + Trader.js)。
// 上半 — 易物(Barter):选 Sell 资源 + 3 个 Buy 钮(=其余资源);点击执行 CommandBarter,
//   普通 100、Shift 500;钮上回显估算换得量 round(SellPrice/BuyPrice*amount)。无市场时整段禁用并提示。
//   价取静态 BarterSystem(去价漂移,漂移延后)。
// 下半 — 贸易品比例(TradingGoods):4 资源各一 % + ↑↓(±5),保持和恒 100(增一资源则减最大他项,
//   减一则加最大他项);改→CommandSetTradingGoods;+「均分 25%」复位钮。
// 底部 — trader 状态(Gui.GetTraderNumber:陆地/船只 在商数)。面板不暂停 sim。
public sealed partial class TradePanel : ModalPanelBase
{
    private readonly SimBridge _sim;
    private OptionButton _sellSel = null!;
    private HBoxContainer _buyRow = null!;
    private Label _barterStatus = null!;
    private readonly Dictionary<ResourceType, Label> _pctLabels = new();
    private Label _status = null!;
    private Dictionary<ResourceType, int> _goods = new();

    public TradePanel(SimBridge sim) => _sim = sim;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Trade", minWidth: 620);
        _status = status;

        // ── 易物段 ──
        content.AddChild(SectionLabel("Barter"));
        _barterStatus = MakeLabel("", 13);
        content.AddChild(_barterStatus);

        var sellRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sellRow.AddThemeConstantOverride("separation", 8);
        sellRow.AddChild(MakeLabel("Sell:", 14));
        _sellSel = new OptionButton { Theme = UITheme.GetTheme() };
        foreach (var t in AllResources)
            _sellSel.AddItem(ResourceName(t));
        _sellSel.Selected = 0;
        _sellSel.ItemSelected += _ => RebuildBuyButtons();
        sellRow.AddChild(_sellSel);
        sellRow.AddChild(MakeLabel("→ Buy:", 14));
        _buyRow = new HBoxContainer();
        _buyRow.AddThemeConstantOverride("separation", 6);
        sellRow.AddChild(_buyRow);
        content.AddChild(sellRow);

        content.AddChild(Hr());

        // ── 贸易品比例段 ──
        content.AddChild(SectionLabel("Trading Goods (land traders gather these)"));
        var goodsGrid = new GridContainer { Columns = 4 };
        goodsGrid.AddThemeConstantOverride("h_separation", 16);
        foreach (var t in AllResources)
        {
            var cell = new VBoxContainer();
            cell.AddThemeConstantOverride("separation", 2);
            cell.AddChild(MakeResourceTag(t));
            var pct = MakeLabel("25%", 16);
            _pctLabels[t] = pct;
            cell.AddChild(pct);
            var arrows = new HBoxContainer();
            arrows.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            arrows.AddThemeConstantOverride("separation", 4);
            var down = new Button { Text = "▼", Theme = UITheme.GetTheme(), CustomMinimumSize = new Vector2(36, 26) };
            var up = new Button { Text = "▲", Theme = UITheme.GetTheme(), CustomMinimumSize = new Vector2(36, 26) };
            ResourceType captured = t;
            down.Pressed += () => Adjust(captured, -5);
            up.Pressed += () => Adjust(captured, +5);
            arrows.AddChild(down);
            arrows.AddChild(up);
            cell.AddChild(arrows);
            goodsGrid.AddChild(cell);
        }
        content.AddChild(goodsGrid);

        var even = new Button
        {
            Text = "Distribute Evenly (25%)",
            Theme = UITheme.GetTheme(),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(200, 28),
        };
        even.Pressed += () =>
        {
            _goods = new Dictionary<ResourceType, int>
            {
                [ResourceType.Food] = 25, [ResourceType.Wood] = 25,
                [ResourceType.Stone] = 25, [ResourceType.Metal] = 25,
            };
            CommitTradingGoods();
        };
        content.AddChild(even);

        content.AddChild(Hr());

        AddButton(content, "Close", Close, minWidth: 160);
    }

    /// <summary>价格漂移 UI(原版 TradeDialog 的漂移价回显):面板开着时 1s 重算
    /// 报价(BarterSystem 漂移随成交变动;此前开页一次静态,漂移不可见)。</summary>
    public override void _Process(double delta)
    {
        if (!Visible) return;
        _driftAccum += (float)delta;
        if (_driftAccum < 1f) return;
        _driftAccum = 0;
        RebuildBuyButtons();
    }
    private float _driftAccum;

    protected override void OnOpen()
    {
        // 拉取当前贸易品比例(内核 PlayerComponent.GetTradingGoods 经 Gui 转发)。
        _goods = _sim.Gui.GetTradingGoods((int)_sim.LocalPlayerId);
        RefreshPctLabels();
        RebuildBuyButtons();
    }

    private void RebuildBuyButtons()
    {
        foreach (var n in _buyRow.GetChildren())
            ((Node)n).QueueFree();

        // 可易物性 + 价签估算全走 GuiInterface 桥(此前绕桥:PlayerComponent.CanBarter +
        // BarterSystem 静态直读——收敛点)。
        int localId = (int)_sim.LocalPlayerId;
        ResourceType sell = AllResources[(int)_sellSel.Selected];
        var anyQuote = _sim.Gui.GetBarterQuote(localId, sell,
            sell == ResourceType.Food ? ResourceType.Wood : ResourceType.Food);
        _barterStatus.Text = anyQuote.CanBarter ? "" : "No Markets Available — build a Market to barter.";

        foreach (var buy in AllResources)
        {
            if (buy == sell) continue;
            var quote = _sim.Gui.GetBarterQuote(localId, sell, buy);
            var btn = new Button
            {
                Text = $"{ResourceName(buy)}  (+{quote.Gain100}/{quote.Gain500})",
                Theme = UITheme.GetTheme(),
                Disabled = !quote.CanBarter,
                CustomMinimumSize = new Vector2(120, 28),
                TooltipText = "Click = 100, Shift = 500",
            };
            btn.AddThemeColorOverride("font_color", ResourceColor(buy));
            ResourceType capturedBuy = buy;
            btn.Pressed += () =>
            {
                int amount = global::Godot.Input.IsPhysicalKeyPressed(Key.Shift) ? 500 : 100;
                _sim.CommandBarter(sell, capturedBuy, amount);
            };
            _buyRow.AddChild(btn);
        }
    }

    // 增/减某资源 5%,保持和=100:增则减最大他项,减则加最大他项。
    private void Adjust(ResourceType t, int delta)
    {
        EnsureGoods();
        int cur = _goods[t];
        if (delta > 0)
        {
            var biggest = BiggestOther(t);
            if (_goods[biggest] < delta) return; // 他项不足,不动
            _goods[t] = cur + delta;
            _goods[biggest] -= delta;
        }
        else if (cur >= -delta)
        {
            _goods[t] = cur + delta; // delta 负
            var biggest = BiggestOther(t);
            _goods[biggest] -= delta; // 加回
        }
        CommitTradingGoods();
    }

    private ResourceType BiggestOther(ResourceType exclude)
    {
        ResourceType best = exclude;
        int bestVal = -1;
        foreach (var t in AllResources)
        {
            if (t == exclude) continue;
            if (_goods[t] > bestVal) { bestVal = _goods[t]; best = t; }
        }
        return best;
    }

    private void EnsureGoods()
    {
        if (_goods.Count == 0)
            _goods = _sim.Gui.GetTradingGoods((int)_sim.LocalPlayerId);
    }

    private void CommitTradingGoods()
    {
        RefreshPctLabels();
        _sim.CommandSetTradingGoods(_goods[ResourceType.Wood], _goods[ResourceType.Food],
                                    _goods[ResourceType.Stone], _goods[ResourceType.Metal]);
    }

    private void RefreshPctLabels()
    {
        foreach (var t in AllResources)
        {
            if (_pctLabels.TryGetValue(t, out var lbl) && _goods.TryGetValue(t, out int v))
                lbl.Text = $"{v}%";
        }
        var tn = _sim.Gui.GetTraderNumber((int)_sim.LocalPlayerId);
        _status.Text = $"Traders: {tn.LandTrading}/{tn.LandTotal} land trading"
                       + (tn.ShipTotal > 0 ? $"   ·   {tn.ShipTrading}/{tn.ShipTotal} ship trading" : "");
    }

    private static Label SectionLabel(string text)
    {
        var l = MakeLabel(text, 16);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        l.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
        return l;
    }

    private static HSeparator Hr() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
}
