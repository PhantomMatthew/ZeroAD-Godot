using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace ZeroAD.Godot.Modio;

// BLAKE2b-512 流式哈希(RFC 7693;.NET 8 无内置 BLAKE2,Godot HashingContext 亦无)。
// 与 libsodium crypto_generichash(outlen=64, 无 key) 逐字节一致——原版 ModIo.cpp
// VerifyDownloadedFile(:598-606)在下载中增量算的 hash_state、以及 minisign -H 的
// 预哈希,都是这个构造。
internal sealed class Blake2b
{
    private const int BlockBytes = 128;

    private static readonly ulong[] IV =
    {
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    };

    private static readonly byte[,] Sigma =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
    };

    private readonly ulong[] _h = new ulong[8];
    private readonly byte[] _buf = new byte[BlockBytes];
    private int _bufLen;
    private ulong _t0, _t1; // 已压缩字节计数(128-bit,低位溢出进高位)

    public Blake2b()
    {
        Array.Copy(IV, _h, 8);
        _h[0] ^= 0x01010000 ^ 64u; // 参数块:digest=64B,key=0,fanout=1,depth=1
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int fill = Math.Min(BlockBytes - _bufLen, data.Length);
            data.Slice(0, fill).CopyTo(_buf.AsSpan(_bufLen));
            _bufLen += fill;
            data = data.Slice(fill);
            // 满块仅在后面还有数据时才压缩——最后一块必须留给 Digest 打 last 标志
            if (_bufLen == BlockBytes && data.Length > 0)
            {
                AddCounter(BlockBytes);
                Compress(_buf, false);
                _bufLen = 0;
            }
        }
    }

    public byte[] Digest()
    {
        AddCounter((ulong)_bufLen);
        Array.Clear(_buf, _bufLen, BlockBytes - _bufLen);
        Compress(_buf, true);
        byte[] output = new byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(i * 8, 8), _h[i]);
        return output;
    }

    public static byte[] Hash512(ReadOnlySpan<byte> data)
    {
        var b = new Blake2b();
        b.Update(data);
        return b.Digest();
    }

    /// <summary>流式哈希整个流(下载文件的验签预哈希)。</summary>
    public static async Task<byte[]> Hash512Async(Stream stream)
    {
        var b = new Blake2b();
        byte[] chunk = new byte[65536];
        int n;
        while ((n = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            b.Update(chunk.AsSpan(0, n));
        return b.Digest();
    }

    private void AddCounter(ulong bytes)
    {
        _t0 += bytes;
        if (_t0 < bytes) _t1++;
    }

    private void Compress(byte[] block, bool isLast)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(i * 8, 8));
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 8; i++) { v[i] = _h[i]; v[i + 8] = IV[i]; }
        v[12] ^= _t0;
        v[13] ^= _t1;
        if (isLast) v[14] = ~v[14];
        for (int r = 0; r < 12; r++)
        {
            int s = r % 10;
            G(v, 0, 4, 8, 12, m[Sigma[s, 0]], m[Sigma[s, 1]]);
            G(v, 1, 5, 9, 13, m[Sigma[s, 2]], m[Sigma[s, 3]]);
            G(v, 2, 6, 10, 14, m[Sigma[s, 4]], m[Sigma[s, 5]]);
            G(v, 3, 7, 11, 15, m[Sigma[s, 6]], m[Sigma[s, 7]]);
            G(v, 0, 5, 10, 15, m[Sigma[s, 8]], m[Sigma[s, 9]]);
            G(v, 1, 6, 11, 12, m[Sigma[s, 10]], m[Sigma[s, 11]]);
            G(v, 2, 7, 8, 13, m[Sigma[s, 12]], m[Sigma[s, 13]]);
            G(v, 3, 4, 9, 14, m[Sigma[s, 14]], m[Sigma[s, 15]]);
        }
        for (int i = 0; i < 8; i++) _h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];           v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];           v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
