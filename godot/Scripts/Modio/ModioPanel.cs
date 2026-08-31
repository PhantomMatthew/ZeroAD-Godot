using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using global::Godot;
using ZeroAD.Godot.Dialogs;

namespace ZeroAD.Godot.Modio;

// ModioPanel — 原版 gui/mod/gui/modio/modio.js 的端口(mod.io 在线 mod 浏览/下载页):
// 列表(name/name_id/version/filesize/dependencies 列,过滤+兼容过滤+排序),
// 描述框,Download → 进度对话框(进度条 + 已传/总量/百分比 + 已用/剩余/均速),
// 失败弹 Abort/Retry(failed_gameid/failed_listing/failed_downloading/failed_filecheck 四态)。
// 进页前先过 mod.io Disclaimer 条款门(modmodio.js downloadModsButton 同款流程)。
// 下载成功 → 解 zip 到 user://mods/{name_id}/(原版 ModInstaller 等价)。
public sealed partial class ModioPanel : ModalPanelBase
{
    private ModIoClient _client = null!;
    private Tree _tree = null!;
    private LineEdit _filter = null!;
    private CheckBox _compatFilter = null!;
    private Label _desc = null!;
    private Label _error = null!;
    private Button _downloadButton = null!;
    private Button _refreshButton = null!;

    private List<ModIoClient.OnlineMod> _mods = new();
    private int _selected = -1;
    private int _sortColumn = 0;
    private bool _sortAsc = true;

    // 进度对话框控件(原版 downloadDialog)
    private PanelContainer? _progressDialog;
    private Label _progressTitle = null!;
    private Label _progressCaption = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressText = null!;
    private Label _progressStatus = null!;
    private bool _downloading;
    private double _downloadStartMsec;

    /// <summary>下载完成(解包后)回调——ModmodPanel 用以刷新(installedMods)。</summary>
    public event System.Action<string>? OnModDownloaded;

    public override void _Ready()
    {
        var (content, _) = BuildShell("Download Mods", 900);
        _client = new ModIoClient();
        _client.ApplyConfig(k => UserConfig.GetDefault(k));
        AddChild(_client);

        // 过滤行(原版 modFilter + compatibilityFilter)。
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 8);
        _filter = new LineEdit
        {
            PlaceholderText = Localization.Tr("Filter"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        UITheme.ApplyModernInput(_filter);
        _filter.TextChanged += _ => Populate();
        filterRow.AddChild(_filter);
        _compatFilter = new CheckBox
        {
            Text = Localization.Tr("Filter compatible mods"),
            ButtonPressed = true,
        };
        UITheme.ApplyCheckboxIcons(_compatFilter);
        _compatFilter.Toggled += _ => Populate();
        filterRow.AddChild(_compatFilter);
        content.AddChild(filterRow);

        // 列表(原版 modsAvailableList 五列)。
        _tree = new Tree
        {
            Columns = 5,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(840, 340),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _tree.SetColumnTitle(0, "Name");
        _tree.SetColumnTitle(1, "Folder");
        _tree.SetColumnTitle(2, "Version");
        _tree.SetColumnTitle(3, "Size");
        _tree.SetColumnTitle(4, "Dependencies");
        _tree.ItemSelected += OnSelected;
        _tree.ColumnTitleClicked += (col, _) =>
        {
            int c = (int)col;
            if (_sortColumn == c) _sortAsc = !_sortAsc;
            else { _sortColumn = c; _sortAsc = true; }
            Populate();
        };
        content.AddChild(_tree);

        _desc = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _desc.AddThemeFontSizeOverride("font_size", 13);
        content.AddChild(_desc);
        _error = new Label { Text = "" };
        _error.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f));
        _error.AddThemeFontSizeOverride("font_size", 13);
        content.AddChild(_error);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Back", () => { Close(); QueueFree(); });
        _refreshButton = AddButton(buttons, "Refresh", () => _ = UpdateModList(), disabled: true);
        _downloadButton = AddButton(buttons, "Download", () => _ = DownloadSelected(), disabled: true);

        BuildProgressDialog();

        // 进页即初始化(原版 init:progressDialog "Initializing mod.io interface."
        // + ModIoStartGetGameId)。条款门已在 Open() 前由调用方/TermsManager 把关。
        _ = UpdateModList();
    }

    // ── 列表 ──

    private void Populate()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        string ft = _filter.Text.ToLowerInvariant();
        IEnumerable<ModIoClient.OnlineMod> shown = _mods;
        if (_compatFilter.ButtonPressed)
            shown = shown.Where(m => !m.Invalid);
        if (ft.Length > 0)
            shown = shown.Where(m =>
                m.Name.ToLowerInvariant().Contains(ft) ||
                m.NameId.ToLowerInvariant().Contains(ft) ||
                m.Summary.ToLowerInvariant().Contains(ft));
        shown = _sortColumn switch
        {
            1 => Sort(shown, m => m.NameId),
            2 => Sort(shown, m => m.Version),
            3 => _sortAsc ? shown.OrderBy(m => m.FileSize) : shown.OrderByDescending(m => m.FileSize),
            4 => Sort(shown, m => string.Join(" ", m.Dependencies)),
            _ => Sort(shown, m => m.Name),
        };
        int idx = 0;
        foreach (var m in shown)
        {
            var item = _tree.CreateItem(root);
            item.SetText(0, m.Name);
            item.SetText(1, m.NameId);
            item.SetText(2, m.Version);
            item.SetText(3, FilesizeToString(m.FileSize));
            item.SetText(4, string.Join(" ", m.Dependencies));
            if (m.Invalid)
                for (int c = 0; c < 5; c++)
                    item.SetCustomColor(c, new Color(0.9f, 0.4f, 0.4f));   // compatibilityColor 红
            item.SetMetadata(0, _mods.IndexOf(m));
            if (_mods.IndexOf(m) == _selected)
                _tree.SetSelected(item, 0);
            idx++;
        }
    }

    private IEnumerable<ModIoClient.OnlineMod> Sort(IEnumerable<ModIoClient.OnlineMod> src,
        System.Func<ModIoClient.OnlineMod, string> key) =>
        _sortAsc ? src.OrderBy(key) : src.OrderByDescending(key);

    private void OnSelected()
    {
        _selected = _tree.GetSelected()?.GetMetadata(0).AsInt32() ?? -1;
        bool has = _selected >= 0 && _selected < _mods.Count;
        var mod = has ? _mods[_selected] : null;
        _downloadButton.Disabled = !has || mod!.Invalid;
        _desc.Text = has && !mod!.Invalid ? mod.Summary : "";
        _error.Text = has && mod!.Invalid
            ? string.Format(Localization.Tr("Invalid mod: {0}"), mod.Error) : "";
    }

    // ── 网络流程(原版 g_ModIOState 状态机的 async 等价)──

    private async System.Threading.Tasks.Task UpdateModList()
    {
        _refreshButton.Disabled = true;
        _mods.Clear();
        Populate();
        ShowProgress(Localization.Tr("Updating"),
            Localization.Tr("Fetching and updating list of available mods."), showBar: false);

        var (mods, error) = await _client.ListMods();
        HideProgress();
        if (mods == null)
        {
            // failed_listing:Abort(0)/Retry(1)。
            GameMsgBox.Show(this, 500, 250,
                $"Mod List could not be retrieved.\n\n{error}",
                Localization.Tr("Fetch Error"),
                new[] { Localization.Tr("Abort"), Localization.Tr("Retry") },
                idx => { if (idx == 1) _ = UpdateModList(); });
            return;
        }
        _mods = mods;
        _selected = -1;
        _desc.Text = "";
        _error.Text = "";
        _refreshButton.Disabled = false;
        Populate();
    }

    private async System.Threading.Tasks.Task DownloadSelected()
    {
        if (_selected < 0 || _selected >= _mods.Count || _mods[_selected].Invalid) return;
        var mod = _mods[_selected];
        _downloadButton.Disabled = true;
        string destPath = Path.Combine(
            ProjectSettings.GlobalizePath("user://mods/"), mod.NameId + ".zip");
        _downloading = true;
        _downloadStartMsec = Time.GetTicksMsec();
        ShowProgress(Localization.Tr("Downloading"),
            string.Format(Localization.Tr("Downloading “{0}”"), mod.Name), showBar: true);

        var (ok, error) = await _client.DownloadMod(mod, destPath);
        _downloading = false;
        HideProgress();
        _downloadButton.Disabled = false;
        if (!ok)
        {
            // md5 失配走 failed_filecheck(仅 Abort);网络失败走 failed_downloading(Abort/Retry)。
            bool checksum = error.Contains("verification");
            GameMsgBox.Show(this, 500, 250, error,
                Localization.Tr(checksum ? "Verification Error" : "Download Error"),
                checksum ? new[] { Localization.Tr("Abort") }
                         : new[] { Localization.Tr("Abort"), Localization.Tr("Retry") },
                idx => { if (!checksum && idx == 1) _ = DownloadSelected(); });
            return;
        }
        // 解包到 user://mods/{name_id}/(原版 ModInstaller.Install 等价;zip 根含 mod.json)。
        string extractDir = Path.Combine(ProjectSettings.GlobalizePath("user://mods/"), mod.NameId);
        string? extractError = ExtractZip(destPath, extractDir);
        try { File.Delete(destPath); } catch { }
        if (extractError != null)
        {
            GameMsgBox.Show(this, 500, 250, extractError, Localization.Tr("Install Error"));
            return;
        }
        OnModDownloaded?.Invoke(mod.Name);
    }

    /// <summary>zip → 目录(ZIPReader 等价物:系统 ZipArchive)。</summary>
    private static string? ExtractZip(string zipPath, string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string dest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!dest.StartsWith(Path.GetFullPath(destDir))) continue;   // zip-slip 防护
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return null;
        }
        catch (System.Exception ex) { return $"extract failed: {ex.Message}"; }
    }

    // ── 进度对话框(原版 downloadDialog)──

    private void BuildProgressDialog()
    {
        _progressDialog = new PanelContainer
        {
            Visible = false,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            CustomMinimumSize = new Vector2(460, 0),
        };
        _progressDialog.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.04f, 0.98f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            ContentMarginTop = 14, ContentMarginBottom = 14, ContentMarginLeft = 16, ContentMarginRight = 16,
        });
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        _progressDialog.AddChild(v);
        _progressTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _progressTitle.AddThemeFontSizeOverride("font_size", 16);
        v.AddChild(_progressTitle);
        _progressCaption = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _progressCaption.AddThemeFontSizeOverride("font_size", 14);
        v.AddChild(_progressCaption);
        _progressBar = new ProgressBar { MinValue = 0, MaxValue = 100, CustomMinimumSize = new Vector2(0, 20) };
        v.AddChild(_progressBar);
        _progressText = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _progressText.AddThemeFontSizeOverride("font_size", 13);
        v.AddChild(_progressText);
        _progressStatus = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _progressStatus.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(_progressStatus);
        var cancelRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        AddButton(cancelRow, "Cancel Download", () => { _client.CancelDownload(); HideProgress(); });
        v.AddChild(cancelRow);
        AddChild(_progressDialog);
    }

    private void ShowProgress(string title, string caption, bool showBar)
    {
        _progressTitle.Text = title;
        _progressCaption.Text = caption;
        _progressBar.Visible = showBar;
        _progressText.Visible = showBar;
        _progressStatus.Visible = showBar;
        _progressBar.Value = 0;
        _progressText.Text = "";
        _progressStatus.Text = "";
        _progressDialog!.Visible = true;
    }

    private void HideProgress() => _progressDialog!.Visible = false;

    public override void _Process(double delta)
    {
        if (!_downloading) return;
        double? progress = _client.PollDownloadProgress();
        if (progress == null) return;
        UpdateProgressBar(progress.Value);
    }

    /// <summary>updateProgressBar 移植:百分比 + 已传/总量 + 已用/剩余/均速
    /// (剩余与均速都朴素假设连接稳定——原版注释同款)。</summary>
    private void UpdateProgressBar(double progress)
    {
        int percent = Mathf.CeilToInt((float)(progress * 100));
        _progressBar.Value = percent;
        long total = _selected >= 0 && _selected < _mods.Count ? _mods[_selected].FileSize : 0;
        double transferred = progress * total;
        _progressText.Text = $"{FilesizeToString((long)transferred)} / {FilesizeToString(total)} ({percent}%)";

        double elapsedMs = Time.GetTicksMsec() - _downloadStartMsec;
        double remainingMs = percent > 0 ? (100 - percent) * elapsedMs / percent : 0;
        double avg = elapsedMs > 0 ? transferred / (elapsedMs / 1000) : 0;
        _progressStatus.Text =
            $"Time Elapsed: {TimeToString(elapsedMs)}\n" +
            $"Estimated Time Remaining: {(remainingMs > 0 ? TimeToString(remainingMs) : "∞")}\n" +
            $"Average Speed: {FilesizeToString((long)avg)}/s";
    }

    // ── 格式化(原版 filesize.js/timeToString)──

    private static readonly string[] SizeUnits = { "B", "KiB", "MiB", "GiB" };

    private static string FilesizeToString(long bytes)
    {
        double v = bytes;
        int unit = 0;
        while (v >= 1024 && unit < SizeUnits.Length - 1) { v /= 1024; unit++; }
        return $"{v:0.#} {SizeUnits[unit]}";
    }

    private static string TimeToString(double ms)
    {
        int s = (int)(ms / 1000);
        return $"{s / 60}:{s % 60:00}";
    }
}
