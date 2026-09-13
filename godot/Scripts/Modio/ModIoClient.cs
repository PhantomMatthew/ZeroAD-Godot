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
// 进度轮询 GetDownloadedBytes/GetBodySize)→ md5 校验(filehash.md5)→ minisign 验签
//(原版 VerifyDownloadedFile:Ed25519(BLAKE2b-512(file)))→ 落 user://mods/。
// 验签端口:列表期 MinisignVerifier.TryParseSignature(原版 ParseSignature:keynum 匹配 +
// 全局签名 Ed25519(sig‖trusted_comment)),失败标 invalid;下载后 VerifyFileHash,
// 失败与 md5 失配同路径(failed_filecheck 等价)。公钥来自 default.cfg [modio] public_key。
public sealed partial class ModIoClient : Node
{
    /// <summary>一个线上 mod(对齐原版 m_ModData properties 展平)。
    /// FileSignature = minisig 里验过的 64B 文件签名(原版 SigStruct.sig),null = 无有效签名。</summary>
    public sealed record OnlineMod(
        string Name, string NameId, string Summary,
        string Version, long FileSize, string FileHashMd5,
        string BinaryUrl, IReadOnlyList<string> Dependencies,
        byte[]? FileSignature, bool Invalid, string Error);

    public string BaseUrl = "https://g-5.modapi.io/v1";
    /// <summary>mod.io 应用级只读公钥(上游 0 A.D. 同款公开集成键)。
    /// 默认值不放源码——从 options.json 的 "modio.apikey" 注入(镜像上游
    /// binaries/data/config/default.cfg 的 modio.v1.api_key);缺失时面板报
    /// "未配置" 而非静默失效。ApplyConfig 仍可经 default.cfg 覆盖。</summary>
    public string ApiKey = "";
    public string NameId = "0ad";
    /// <summary>minisign 验签公钥(default.cfg [modio] public_key,base64 PKStruct
    /// 42B = "Ed" + keynum[8] + pk[32];原版 ModIo ctor 同款来源)。缺失/无法解析时
    /// 一切 mod 标 invalid(原版 ENSURE "mod.io will be unusable" 的温和等价)。</summary>
    public string PublicKey = "";

    private string _gameId = "";
    private MinisignVerifier.PublicKey? _pk;
    private bool _keyDecoded;

    /// <summary>下载进度回调(0..1);由面板在 _Process 轮询 PollDownloadProgress 驱动。</summary>
    public double DownloadProgress { get; private set; }
    private global::Godot.HttpRequest? _activeDownload;

    /// <summary>modio.v1.baseurl/api_key/name_id、modio.public_key 从 default.cfg 覆盖
    /// (UserConfig 已加载时)。</summary>
    public void ApplyConfig(Func<string, string?> getDefault)
    {
        BaseUrl = getDefault("modio.v1.baseurl") is { Length: > 0 } b ? b : BaseUrl;
        // 优先 default.cfg;回落 options.json 的 modio.apikey(应用级公钥的存放位)。
        ApiKey = getDefault("modio.v1.api_key") is { Length: > 0 } k ? k
            : getDefault("modio.apikey") is { Length: > 0 } k2 ? k2 : ApiKey;
        NameId = getDefault("modio.v1.name_id") is { Length: > 0 } n ? n : NameId;
        PublicKey = getDefault("modio.public_key") is { Length: > 0 } p ? p : PublicKey;
    }

    /// <summary>解码后的验签公钥(懒解析,PublicKey 可能被 ApplyConfig 或直接赋值)。
    /// 首次使用时跑一次 MinisignVerifier.SelfTest;自检失败/公钥缺失/公钥畸形 → null,
    /// 此后一切签名视为无效(原版 ENSURE 不可用的温和等价),并 PushError 留痕。</summary>
    private MinisignVerifier.PublicKey? VerifierKey
    {
        get
        {
            if (_keyDecoded) return _pk;
            _keyDecoded = true;
            if (!MinisignVerifier.SelfTest(out var selfErr))
                GD.PushError($"mod.io 验签自检失败:{selfErr}——拒绝一切 minisig 验签");
            else if (PublicKey.Length == 0)
                GD.PushError("mod.io public_key 未配置(default.cfg [modio] public_key)——mod 验签不可用");
            else if (MinisignVerifier.TryDecodePublicKey(PublicKey, out var pk)) _pk = pk;
            else GD.PushError("mod.io public_key 无法解析(base64 PKStruct 42B)");
            return _pk;
        }
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
    /// metadata_blob{dependencies,minisigs}})。minisigs 按原版 ParseSignature 实验:
    /// keynum 匹配公钥 + 全局签名 Ed25519 通过才取到文件签名,否则标 invalid
    /// (原版 INVALIDATE_DATA_AND_CONTINUE;多个 minisigs 的选择逻辑见 MinisignVerifier)。</summary>
    private OnlineMod ParseMod(JsonElement el)
    {
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
            var minisigs = new List<string>();
            if (modFile.TryGetProperty("metadata_blob", out var mb) && mb.ValueKind == JsonValueKind.String)
                try
                {
                    using var meta = JsonDocument.Parse(mb.GetString()!);
                    if (meta.RootElement.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
                        foreach (var dep in d.EnumerateArray())
                            if (dep.ValueKind == JsonValueKind.String) deps.Add(dep.GetString()!);
                    if (meta.RootElement.TryGetProperty("minisigs", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var sig in ms.EnumerateArray())
                            if (sig.ValueKind == JsonValueKind.String) minisigs.Add(sig.GetString()!);
                }
                catch { }
            // 原版:ParseSignature 失败 → invalid(标出但留在列表)。
            byte[]? fileSig = null;
            string sigError;
            if (minisigs.Count == 0) sigError = "missing minisigs";
            else if (VerifierKey is not { } pk) sigError = "no usable public key";
            else if (MinisignVerifier.TryParseSignature(minisigs, pk, out fileSig, out sigError)) { }
            bool missingFields = name.Length == 0 || nameId.Length == 0 || url.Length == 0;
            bool invalid = missingFields || fileSig == null;
            return new OnlineMod(name, nameId, summary, version, filesize, md5, url, deps, fileSig,
                invalid, invalid ? (missingFields ? "missing fields" : sigError) : "");
        }
        catch (Exception ex)
        {
            return new OnlineMod("", "", "", "", 0, "", "", Array.Empty<string>(), null, true, ex.Message);
        }
    }

    /// <summary>下载 mod zip 到 destPath(先 .temp 落盘,md5 + minisign 验签通过才改名;
    /// 原版 VerifyDownloadedFile :557-613 同款,验签失败与 md5 失配同路径——
    /// 面板按 failed_filecheck 弹 Verification Error 仅 Abort)。PollDownloadProgress 由调用方 _Process 驱动。</summary>
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
        // minisign 验签(原版 :594-610):Ed25519(文件签名, BLAKE2b-512(文件内容), 公钥)。
        // FileSignature 为 null 的 mod 列表期已标 invalid、面板禁下载;此处再挡一层。
        byte[] fileHash;
        await using (var sigStream = File.OpenRead(tempPath))
            fileHash = await Blake2b.Hash512Async(sigStream);
        if (mod.FileSignature == null || VerifierKey is not { } pk
            || !MinisignVerifier.VerifyFileHash(mod.FileSignature, pk, fileHash))
        {
            Cleanup(req, tempPath);
            return (false, "file verification error: failed to verify minisign signature");
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
