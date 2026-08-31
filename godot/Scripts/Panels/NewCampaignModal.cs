using Godot;
using ZeroAD.Godot.Campaigns;

namespace ZeroAD.Godot;

// NewCampaignModal — 新战役命名弹窗(原版 campaigns/new_modal/NewCampaignModal.js):
// 文本框预填模板名,Start 时创建 run 文件(template_时间戳_随机数 命名)、置当前 run、
// 关闭后由调用方进战役主菜单。Cancel 直接关闭。
public sealed partial class NewCampaignModal : ModalPanelBase
{
    private readonly CampaignTemplate _template;
    private readonly string? _dataRoot;
    private LineEdit _nameEdit = null!;
    private Button _startButton = null!;

    /// <summary>run 创建完成(已保存 + 已置当前)。</summary>
    public event System.Action<CampaignRun>? OnRunCreated;

    public NewCampaignModal(CampaignTemplate template, string? dataRoot)
    {
        _template = template;
        _dataRoot = dataRoot;
    }

    public override void _Ready()
    {
        Layer = 62;   // 叠在 CampaignsPanel(55)之上
        var (content, _) = BuildShell("New Campaign", 420);
        Layer = 62;

        content.AddChild(MakeLabel(
            string.Format(Localization.Tr("Starting a new campaign: {0}"), Localization.Tr(_template.Name)), 14));

        _nameEdit = new LineEdit
        {
            Text = Localization.Tr(_template.Name),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        UITheme.ApplyModernInput(_nameEdit);
        _nameEdit.TextChanged += text => _startButton.Disabled = text.Length == 0;
        content.AddChild(_nameEdit);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", () => { Close(); QueueFree(); });
        _startButton = AddButton(buttons, "Start", CreateAndStart);

        // 原版 focus 输入框。
        _nameEdit.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void CreateAndStart()
    {
        // 原版文件名:template.identifier + "_" + Date.now() + "_" + floor(random*100000)。
        string filename = $"{_template.Identifier}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{GD.RandRange(0, 99999)}";
        var run = new CampaignRun
        {
            Filename = filename,
            UserDescription = _nameEdit.Text,
            Template = _template,
        };
        run.Save().SetCurrent();
        Close();
        QueueFree();
        OnRunCreated?.Invoke(run);
    }
}
