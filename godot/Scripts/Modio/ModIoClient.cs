using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace ZeroAD.Godot.Modio;

// ModIoClient — 原版 source/ps/ModIo.cpp 的 Godot 端口(mod.io v1 REST,global::Godot.HttpRequest)。
// 端点/凭据照抄 default.cfg [modio.v1]:baseurl https://g-5.modapi.io/v1,api_key,name_id=0ad。
// 流程:GetGameId(name_id 查游戏 id)→ ListMods(/games/{id}/mods)→ DownloadMod(binary_url,
// 进度轮询 GetDownloadedBytes/GetBodySize)→ md5 校验(filehash.md5)→ 落 user://mods/。
// 缺口:原版 minisigs(Ed25519)签名校验未移植——缺签名字段的 mod 标 invalid,
// 有签名但此处不验(见 PORTING-GAPS §8)。
public sealed partial class ModIoClient : Node
{
    /// <summary>一个线上 mod(对齐原版 m_ModData properties 展平)。</summary>
    public sealed record OnlineMod(
        string Name, string NameId, string Summary,
        string Version, long FileSize, string FileHashMd5,
        string BinaryUrl, IReadOnlyList<string> Dependencies,
        bool Invalid, string Error);

    public string BaseUrl = "https://g-5.modapi.io/v1";
    public string ApiKey = "23df258a71711ea6e4b50893acc1ba55";
    public string NameId = "0ad";

    private string _gameId = "";

    /// <summary>下载进度回调(0..1);由面板在 _Process 轮询 PollDownloadProgress 驱动。</summary>
    public double DownloadProgress { get; private set; }
    private global::Godot.HttpRequest? _activeDownload;

    /// <summary>modio.v1.baseurl/api_key/name_id 从 default.cfg 覆盖(UserConfig 已加载时)。</summary>
    public void ApplyConfig(Func<string, string?> getDefault)
    {
        BaseUrl = getDefault("modio.v1.baseurl") is { Length: > 0 } b ? b : BaseUrl;
        ApiKey = getDefault("modio.v1.api_key") is { Length: > 0 } k ? k : ApiKey;
        NameId = getDefault("modio.v1.name_id") is { Length: > 0 } n ? n : NameId;
    }

    /// <summary>GetGameId:/games?api_key=…&name_id=0ad → data[0].id。失败返回 false + error。</summary>
    public async Task<(bool Ok, string Error)> GetGameId()
    {
        var (ok, body, error) = await Get($"{BaseUrl}/games?api_key={ApiKey}&name_id={NameId}");
        if (!ok) return (false, error);
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                _gameId = el.GetProperty("id").GetInt32().ToString();
                return (true, "");
            }
            return (false, "no game with name_id " + NameId);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>ListMods:/games/{id}/mods?api_key=… → 全部线上 mod(字段缺失 → invalid,
    /// 对齐原版 INVALIDATE_DATA_AND_CONTINUE)。</summary>
    public async Task<(List<OnlineMod>? Mods, string Error)> ListMods()
    {
        if (_gameId.Length == 0)
        {
            var (ok, err) = await GetGameId();
            if (!ok) return (null, err);
        }
        var (ok2, body, error) = await Get($"{BaseUrl}/games/{_gameId}/mods?api_key={ApiKey}");
        if (!ok2) return (null, error);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var mods = new List<OnlineMod>();
            foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
                mods.Add(ParseMod(el));
            return (mods, "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>单个 mod 条目解析(原版 ParseModsResponse 同款字段链:
    /// name/name_id/summary + modfile{version,filesize,filehash.md5,download.binary_url,
    /// metadata_blob{dependencies,minisigs}})。minisigs 只验存在性(签名校验未移植)。</summary>
    private static OnlineMod ParseMod(JsonElement el)
    {
        string Invalid(string err) => err;
        try
        {
            string name = el.GetProperty("name").GetString() ?? "";
            string nameId = el.GetProperty("name_id").GetString() ?? "";
            string summary = el.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()! : "";
            var modFile = el.GetProperty("modfile");
            string version = modFile.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()! : "";
            long filesize = 0;
            if (modFile.TryGetProperty("filesize", out var fs))
                filesize = fs.ValueKind == JsonValueKind.Number ? fs.GetInt64()
                    : long.TryParse(fs.GetString(), out long p) ? p : 0;
            string md5 = modFile.TryGetProperty("filehash", out var fh) && fh.ValueKind == JsonValueKind.Object
                && fh.TryGetProperty("md5", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : "";
            string url = modFile.TryGetProperty("download", out var dl) && dl.ValueKind == JsonValueKind.Object
                && dl.TryGetProperty("binary_url", out var bu) && bu.ValueKind == JsonValueKind.String
                ? bu.GetString()! : "";
            var deps = new List<string>();
            bool hasSig = false;
            if (modFile.TryGetProperty("metadata_blob", out var mb) && mb.ValueKind == JsonValueKind.String)
                try
                {
                    using var meta = JsonDocument.Parse(mb.GetString()!);
                    if (meta.RootElement.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
                        foreach (var dep in d.EnumerateArray())
                            if (dep.ValueKind == JsonValueKind.String) deps.Add(dep.GetString()!);
                    if (meta.RootElement.TryGetProperty("minisigs", out var ms) && ms.ValueKind == JsonValueKind.Array
                        && ms.GetArrayLength() > 0)
                        hasSig = true;
                }
                catch { }
            // 原版:签名解析失败 → invalid。此处 minisigs 缺失 → invalid;存在但不验签(缺口)。
            bool invalid = name.Length == 0 || nameId.Length == 0 || url.Length == 0 || !hasSig;
            return new OnlineMod(name, nameId, summary, version, filesize, md5, url, deps,
                invalid, invalid ? Invalid("missing fields or signature") : "");
        }
        catch (Exception ex)
        {
            return new OnlineMod("", "", "", "", 0, "", "", Array.Empty<string>(), true, ex.Message);
        }
    }

    /// <summary>下载 mod zip 到 destPath(先 .temp 落盘,md5 校验通过才改名;
    /// 原版 verifyDownload 同款)。PollDownloadProgress 由调用方 _Process 驱动。</summary>
    public async Task<(bool Ok, string Error)> DownloadMod(OnlineMod mod, string destPath)
    {
        string tempPath = destPath + ".temp";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        CancelDownload();
        DownloadProgress = 0;
        _activeDownload = new global::Godot.HttpRequest { DownloadFile = tempPath, Timeout = 0 };
        AddChild(_activeDownload);
        var req = _activeDownload;
        Error err = req.Request(mod.BinaryUrl);
        if (err != Error.Ok)
        {
            Cleanup(req, tempPath);
            return (false, $"request failed: {err}");
        }
        var result = await ToSignal(req, global::Godot.HttpRequest.SignalName.RequestCompleted);
        long responseCode = result[1].AsInt64();
        if (result[0].AsInt32() != (int)global::Godot.HttpRequest.Result.Success || responseCode != 200)
        {
            Cleanup(req, tempPath);
            return (false, $"download failed (HTTP {responseCode})");
        }
        // md5 校验(原版 filecheck)。
        if (mod.FileHashMd5.Length > 0)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            await using var stream = File.OpenRead(tempPath);
            byte[] digest = await md5.ComputeHashAsync(stream);
            string hex = Convert.ToHexString(digest).ToLowerInvariant();
            if (hex != mod.FileHashMd5.ToLowerInvariant())
            {
                Cleanup(req, tempPath);
                return (false, $"file verification error: expected md5 {mod.FileHashMd5}, got {hex}");
            }
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);
        Cleanup(req, null);
        return (true, "");
    }

    /// <summary>轮询下载进度(面板 _Process 调用);无活动下载返回 null。</summary>
    public double? PollDownloadProgress()
    {
        if (_activeDownload == null) return null;
        int body = _activeDownload.GetBodySize();
        int got = _activeDownload.GetDownloadedBytes();
        if (body > 0) DownloadProgress = (double)got / body;
        return DownloadProgress;
    }

    public void CancelDownload()
    {
        if (_activeDownload != null)
        {
            _activeDownload.CancelRequest();
            _activeDownload.QueueFree();
            _activeDownload = null;
        }
    }

    private void Cleanup(global::Godot.HttpRequest req, string? tempPath)
    {
        if (ReferenceEquals(req, _activeDownload)) _activeDownload = null;
        req.QueueFree();
        try { if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }

    private async Task<(bool Ok, string Body, string Error)> Get(string url)
    {
        var req = new global::Godot.HttpRequest { Timeout = 30 };
        AddChild(req);
        Error err = req.Request(url);
        if (err != Error.Ok)
        {
            req.QueueFree();
            return (false, "", $"request failed: {err}");
        }
        var result = await ToSignal(req, global::Godot.HttpRequest.SignalName.RequestCompleted);
        req.QueueFree();
        if (result[0].AsInt32() != (int)global::Godot.HttpRequest.Result.Success)
            return (false, "", $"network error (result {result[0].AsInt32()})");
        long code = result[1].AsInt64();
        if (code != 200)
            return (false, "", $"HTTP {code}");
        return (true, result[3].AsByteArray().Length > 0
            ? System.Text.Encoding.UTF8.GetString(result[3].AsByteArray()) : "{}", "");
    }
}
