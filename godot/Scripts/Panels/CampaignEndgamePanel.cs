using Godot;

namespace ZeroAD.Godot;

/// <summary>战役通关页(原版 campaigns/default_menu/endgame/:终局瞬态页)。
/// 原版行为:run 全部关卡完成时打开本页(markLevelComplete 已在胜利时回写)。
/// 内容:战役名 + 完成统计(完成/总数)+ 回战役菜单。</summary>
public sealed partial class CampaignEndgamePanel : ModalPanelBase
{
    private readonly Campaigns.CampaignRun _run;
    private readonly System.Action _backToMenu;

    public CampaignEndgamePanel(Campaigns.CampaignRun run, System.Action backToMenu)
    {
        _run = run;
        _backToMenu = backToMenu;
    }

    public override void _Ready()
    {
        Layer = 64;
        var (content, _) = BuildShell(_run.Template?.Name ?? "Campaign", 460);

        int total = _run.Template?.Levels.Count ?? 0;
        int done = _run.CompletedLevels.Count;
        var stats = new Label
        {
            Text = $"Campaign complete!\n\nLevels completed: {done} / {total}",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 80),
        };
        content.AddChild(stats);

        AddButton(content, "Back to Campaign Menu", () =>
        {
            _backToMenu();
            Close();
            QueueFree();
        });
    }
}
