using System;
using System.Collections.Generic;
using System.Text;

namespace ZeroAD.Godot.Modio;

// minisign(.minisig)验签——原版 source/ps/ModIo.cpp ParseSignature(:820-890)与
// VerifyDownloadedFile 签名段(:594-610)的等价端口(libsodium → 本目录 Ed25519/Blake2b)。
//
// .minisig(minisign -SHm 输出)四行格式(原版 :825-826):
//   untrusted comment: ...
//   base64(SigStruct 74B = "ED" + keynum[8] + sig[64])   ← sig = Ed25519(BLAKE2b-512(file))
//   trusted comment: ...
//   base64(global_sig 64B = Ed25519(sig[64] ‖ trusted_comment 文本))
// 公钥(default.cfg [modio] public_key)= base64(PKStruct 42B = "Ed" + keynum[8] + pk[32])。
// 多个 minisigs 的选择逻辑照原版:逐个尝试,keynum 与公钥不符 → 试下一个(continue);
// 其余任何失败(格式/非 "ED" 算法/全局签名无效)→ 整体失败,不再试后续签名。
internal static class MinisignVerifier
{
    public const int PublicKeyBytes = 42;     // "Ed" + keynum[8] + pk[32]
    public const int FileSigStructBytes = 74; // "ED" + keynum[8] + sig[64]
    public const int SignatureBytes = 64;

    /// <summary>解码后的 PKStruct(原版 ModIo.h:40 / m_pk)。</summary>
    public readonly struct PublicKey
    {
        public readonly byte[] KeyNum; // 8B,与 SigStruct.keynum 比对选签名
        public readonly byte[] Pk;     // 32B Ed25519 公钥
        public PublicKey(byte[] keyNum, byte[] pk) { KeyNum = keyNum; Pk = pk; }
    }

    /// <summary>base64 PKStruct → PublicKey;要求恰好 42B 且 alg == "Ed"
    ///(原版 ModIo ctor :150-156 的 sodium_base642bin + bin_len 检查)。</summary>
    public static bool TryDecodePublicKey(string b64, out PublicKey pk)
    {
        pk = default;
        if (!TryDecode(b64, PublicKeyBytes, out byte[]? raw)) return false;
        if (raw[0] != (byte)'E' || raw[1] != (byte)'d') return false;
        pk = new PublicKey(raw[2..10], raw[10..42]);
        return true;
    }

    /// <summary>原版 ParseSignature:在 minisigs 中找 keynum 匹配公钥且全局签名有效者,
    /// 成功返回其 64B 文件签名(对 BLAKE2b-512(文件) 的 Ed25519 签名)。
    /// 错误文案与原版 FAIL() 逐条一致。</summary>
    public static bool TryParseSignature(IReadOnlyList<string> minisigs, PublicKey pk,
        out byte[]? fileSig, out string error)
    {
        fileSig = null;
        foreach (string sigText in minisigs)
        {
            string[] lines = sigText.Split('\n');
            if (lines.Length < 4) { error = "Invalid (too short) sig."; return false; }
            const string untrustedPrefix = "untrusted comment: ";
            const string trustedPrefix = "trusted comment: ";
            if (!lines[0].StartsWith(untrustedPrefix, StringComparison.Ordinal))
            { error = "Malformed untrusted comment."; return false; }
            if (!lines[2].StartsWith(trustedPrefix, StringComparison.Ordinal))
            { error = "Malformed trusted comment."; return false; }
            if (!TryDecode(lines[1], FileSigStructBytes, out byte[]? sigRaw))
            { error = "Failed to decode base64 sig."; return false; }
            if (!sigRaw.AsSpan(2, 8).SequenceEqual(pk.KeyNum))
                continue; // keynum 不符 → 试下一个签名
            if (sigRaw[0] != (byte)'E' || sigRaw[1] != (byte)'D')
            { error = "Only hashed minisign signatures are supported."; return false; }
            if (!TryDecode(lines[3], SignatureBytes, out byte[]? globalSig))
            { error = "Failed to decode base64 global_sig."; return false; }
            // 全局签名覆盖 sig[64] ‖ trusted_comment 文本(原版 :866-875)。
            byte[] trustedComment = Encoding.UTF8.GetBytes(lines[2].Substring(trustedPrefix.Length));
            byte[] sigAndComment = new byte[SignatureBytes + trustedComment.Length];
            sigRaw.AsSpan(10, SignatureBytes).CopyTo(sigAndComment);
            trustedComment.CopyTo(sigAndComment, SignatureBytes);
            if (!Ed25519.Verify(globalSig, pk.Pk, sigAndComment))
            { error = "Failed to verify global signature."; return false; }
            fileSig = sigRaw[10..74];
            error = "";
            return true;
        }
        error = "Invalid signature.";
        return false;
    }

    /// <summary>文件签名验证(原版 VerifyDownloadedFile :598-610):
    /// Ed25519_verify(sig, BLAKE2b-512(文件内容), pk)。</summary>
    public static bool VerifyFileHash(byte[] fileSig, PublicKey pk, byte[] blake2b512Digest) =>
        fileSig.Length == SignatureBytes && blake2b512Digest.Length == 64
        && Ed25519.Verify(fileSig, pk.Pk, blake2b512Digest);

    private static bool TryDecode(string b64, int expectedBytes, out byte[] raw)
    {
        try { raw = Convert.FromBase64String(b64); }
        catch (FormatException) { raw = Array.Empty<byte>(); return false; }
        return raw.Length == expectedBytes;
    }

    /// <summary>代码内自检(项目无 Godot 侧 C# 测试基建,以此替代单测):
    /// RFC 8032/RFC 7693 已知答案 + 原版 source/ps/tests/test_ModIo.h 测试向量。
    /// 任一步失败说明实现被破坏,调用方必须拒绝一切验签。</summary>
    public static bool SelfTest(out string error)
    {
        error = "";
        // 1) Ed25519:RFC 8032 §7.1 TEST 1(空消息)。
        byte[] pk1 = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] sig1 = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        if (!Ed25519.Verify(sig1, pk1, ReadOnlySpan<byte>.Empty))
        { error = "Ed25519: RFC 8032 vector rejected"; return false; }
        if (Ed25519.Verify(sig1, pk1, new byte[] { 0x01 }))
        { error = "Ed25519: tampered message accepted"; return false; }

        // 2) BLAKE2b-512 已知答案(RFC 7693 Appendix A:空串与 "abc")。
        if (Convert.ToHexString(Blake2b.Hash512(ReadOnlySpan<byte>.Empty)) !=
            "786A02F742015903C6C6FD852552D272912F4740E15847618A86E217F71F5419" +
            "D25E1031AFEE585313896444934EB04B903A685B1448B755D56F701AFE9BE2CE")
        { error = "BLAKE2b: empty-input digest mismatch"; return false; }
        if (Convert.ToHexString(Blake2b.Hash512("abc"u8.ToArray())) !=
            "BA80A53F981C4D0D6A2797B69F12F6E94C212F14685AC4B74B12BB6FDBFFA2D1" +
            "7D87C5392AAB792DC252D5DE4533CC9518D38AA8DBF1925AB92386EDD4009923")
        { error = "BLAKE2b: \"abc\" digest mismatch"; return false; }

        // 3) 原版 test_ModIo.h 测试向量:测试公钥 + 合法 minisig(全局签名必须通过)。
        if (!TryDecodePublicKey("RWTA6VIoth2Q1PFLsRILr3G7NB+mwwO8BSGoXs63X6TQgNGM4cE8Pvd6", out var testPk))
        { error = "upstream test public key rejected"; return false; }
        const string validSig =
            "untrusted comment: \n" +
            "RUTA6VIoth2Q1HUg5bwwbCUZPcqbQ/reLXqxiaWARH5PNcwxX5vBv/mLPLgdxGsIrOyK90763+rCVTmjeYx5BDz8C0CIbGZTNQs=\n" +
            "trusted comment: timestamp:1517285433\tfile:tm.zip\n" +
            "THwNMhK4Ogj6XA4305p1K9/ouP/DrxPcDFrPaiu+Ke6/WGlHIzBZHvmHWUedvsK6dzL31Gk8YNzscKWnZqWNCw==";
        if (!TryParseSignature(new[] { validSig }, testPk, out byte[]? fileSig, out error) || fileSig == null)
        { error = "upstream valid minisig rejected: " + error; return false; }

        // 4) 同向量篡改全局签名首字符(T→A,原版 test_signature_parsing 负例)→ 必须拒绝。
        const string tamperedSig =
            "untrusted comment: \n" +
            "RUTA6VIoth2Q1HUg5bwwbCUZPcqbQ/reLXqxiaWARH5PNcwxX5vBv/mLPLgdxGsIrOyK90763+rCVTmjeYx5BDz8C0CIbGZTNQs=\n" +
            "trusted comment: timestamp:1517285433\tfile:tm.zip\n" +
            "AHwNMhK4Ogj6XA4305p1K9/ouP/DrxPcDFrPaiu+Ke6/WGlHIzBZHvmHWUedvsK6dzL31Gk8YNzscKWnZqWNCw==";
        if (TryParseSignature(new[] { tamperedSig }, testPk, out _, out _))
        { error = "tampered global signature accepted"; return false; }

        // 5) keynum 不匹配的签名(原版负例,RUTA5V…)→ 试完后 "Invalid signature."。
        const string wrongKeySig =
            "untrusted comment: \n" +
            "RUTA5VIoth2Q1HUg5bwwbCUZPcqbQ/reLXqxiaWARH5PNcwxX5vBv/mLPLgdxGsIrOyK90763+rCVTmjeYx5BDz8C0CIbGZTNQs=\n" +
            "trusted comment: \n";
        if (TryParseSignature(new[] { wrongKeySig }, testPk, out _, out _))
        { error = "wrong-keynum signature accepted"; return false; }
        return true;
    }
}
