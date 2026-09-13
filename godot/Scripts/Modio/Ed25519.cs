using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace ZeroAD.Godot.Modio;

// Ed25519 纯托管验签(.NET 8 无内置 Ed25519;mod.io minisig 只需 verify,不含 sign/keygen)。
// 语义等价移植自以下公域(public domain / CC0)参考实现:
// - 域运算:curve25519-donna-64 的 5×51-bit limb 表示与 fmul/fexpand/fcontract(A. Langley);
//   limb 乘积累加用 .NET 内置 UInt128。
// - 点解码(negate 变体)/编码与验签方程 [S]B = R + [h]A:ref10 的 ge_frombytes_negate_vartime /
//   ge_tobytes / ge_double_scalarmult_vartime(D. J. Bernstein 等);双标量乘改为无预计算表的
//   Shamir 交织法(验签为一次性操作且全是公开数据,不需要常数时间/查表加速)。
// - 指数链 Invert(x^(2^255-21))/Pow22523(x^(2^252-3))与 64 字节标量约减 ModL:TweetNaCl。
// - 消息哈希 SHA-512 用 .NET 内置 System.Security.Cryptography。
// 曲线常量(d、sqrtm1、基点)取 RFC 8032 §5.1 的规范十进制值,静态初始化时经 BigInteger
// 归约拆 limb,避免手工抄写 limb 常量出错。
internal static class Ed25519
{
    /// <summary>验签:sig(64B = R‖S)是否为 pk(32B)对 msg 的有效 Ed25519 签名。</summary>
    public static bool Verify(ReadOnlySpan<byte> sig, ReadOnlySpan<byte> pk, ReadOnlySpan<byte> msg)
    {
        if (sig.Length != 64 || pk.Length != 32) return false;
        // S 必须为规范标量(< L),libsodium crypto_sign_verify_detached 同款拒绝。
        if (!IsCanonicalScalar(sig.Slice(32, 32))) return false;
        // 解出 -A(ref10 negate 变体:使双标量乘直接给出 [S]B - [h]A)。
        if (!TryDecodePointNegate(pk, out Point aNeg)) return false;
        byte[] h = HashModL(sig.Slice(0, 32), pk, msg);              // h = SHA-512(R‖A‖M) mod L
        Point rPrime = DoubleScalarMul(sig.Slice(32, 32), h, aNeg);  // [S]B + [h](-A)
        return EncodePoint(rPrime).AsSpan().SequenceEqual(sig.Slice(0, 32));
    }

    // ── 标量域 mod L(L = 2^252 + 27742317777372353535851937790883648493)──

    private static readonly byte[] LBytes =
        { 0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58, 0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
          0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10 };

    private static bool IsCanonicalScalar(ReadOnlySpan<byte> s)
    {
        for (int i = 31; i >= 0; i--)
        {
            if (s[i] < LBytes[i]) return true;
            if (s[i] > LBytes[i]) return false;
        }
        return false; // s == L 同样拒绝
    }

    /// <summary>SHA-512(R‖A‖M) 的 64 字节摘要按 TweetNaCl modL 约减到 mod L。</summary>
    private static byte[] HashModL(ReadOnlySpan<byte> r, ReadOnlySpan<byte> pk, ReadOnlySpan<byte> msg)
    {
        byte[] input = new byte[64 + msg.Length];
        r.CopyTo(input);
        pk.CopyTo(input.AsSpan(32));
        msg.CopyTo(input.AsSpan(64));
        return ModL(SHA512.HashData(input));
    }

    private static byte[] ModL(byte[] digest)
    {
        long[] x = new long[64];
        for (int i = 0; i < 64; i++) x[i] = digest[i];
        for (int i = 63; i >= 32; i--)
        {
            long carry = 0;
            int j;
            for (j = i - 32; j < i - 12; j++)
            {
                x[j] += carry - 16 * x[i] * LBytes[j - (i - 32)];
                carry = (x[j] + 128) >> 8;
                x[j] -= carry << 8;
            }
            x[j] += carry;
            x[i] = 0;
        }
        long c = 0;
        for (int j = 0; j < 32; j++)
        {
            x[j] += c - (x[31] >> 4) * LBytes[j];
            c = x[j] >> 8;
            x[j] &= 255;
        }
        for (int j = 0; j < 32; j++) x[j] -= c * LBytes[j];
        byte[] r = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            x[i + 1] += x[i] >> 8;
            r[i] = (byte)(x[i] & 255);
        }
        return r;
    }

    // ── 群运算(扩展坐标,edwards25519:-x² + y² = 1 + d x² y²)──

    private static readonly Fe BaseX = Fe.FromDecimal("15112221349535400772501151409588531511454012693041857206046113283949847762202");
    private static readonly Fe BaseY = Fe.FromDecimal("46316835694926478169428394003475163141307993866256225615783033603165251855960");
    private static readonly Fe D = Fe.FromDecimal("37095705934669439343138083508754565189542113879843219016388785533085940283555");
    private static readonly Fe D2 = D + D;
    private static readonly Fe SqrtM1 = Fe.FromDecimal("19681161376707505956807079304988542015446066515923890162744021073123829784752");
    private static readonly Point BasePoint = new(BaseX, BaseY, Fe.One, BaseX * BaseY);

    /// <summary>Shamir 交织:r = [s]B + [h]aNeg(s、h 为 32B 小端标量,均 &lt; 2^255)。</summary>
    private static Point DoubleScalarMul(ReadOnlySpan<byte> s, ReadOnlySpan<byte> h, Point aNeg)
    {
        Point bPlusA = Point.Add(BasePoint, aNeg);
        Point r = Point.Identity;
        for (int i = 255; i >= 0; i--)
        {
            r = Point.Double(r);
            int idx = Bit(s, i) | (Bit(h, i) << 1);
            if (idx == 1) r = Point.Add(r, BasePoint);
            else if (idx == 2) r = Point.Add(r, aNeg);
            else if (idx == 3) r = Point.Add(r, bPlusA);
        }
        return r;
    }

    private static int Bit(ReadOnlySpan<byte> scalar, int i) => (scalar[i >> 3] >> (i & 7)) & 1;

    /// <summary>ref10 ge_frombytes_negate_vartime:解出 y、按 sign 位恢复 x 后取相反奇偶的 x(即 -A)。</summary>
    private static bool TryDecodePointNegate(ReadOnlySpan<byte> s, out Point p)
    {
        p = Point.Identity;
        byte[] buf = s.ToArray();
        int sign = buf[31] >> 7;
        buf[31] &= 0x7F;
        Fe y = Fe.FromBytesLittle(buf);
        Fe y2 = y.Square();
        Fe u = y2 - Fe.One;        // y²-1
        Fe v = D * y2 + Fe.One;    // d·y²+1
        Fe v3 = v.Square() * v;
        Fe x = v3.Square() * v * u; // u·v⁷
        x = x.Pow22523();
        x = x * v3 * u;            // u·v³·(u·v⁷)^((q-5)/8)
        Fe vxx = v * x.Square();
        Fe check = vxx - u;
        if (!check.IsZero())
        {
            if (!(vxx + u).IsZero()) return false;
            x *= SqrtM1;
        }
        if (x.IsNegative() == (sign != 0)) x = Fe.Zero - x;
        p = new Point(x, y, Fe.One, x * y);
        return true;
    }

    /// <summary>ref10 ge_tobytes:仿射化后输出 y 的 32B 小端,最高位存 x 的奇偶。</summary>
    private static byte[] EncodePoint(Point p)
    {
        Fe iz = p.Z.Invert();
        byte[] b = (p.Y * iz).ToBytesLittle();
        if ((p.X * iz).IsNegative()) b[31] |= 0x80;
        return b;
    }

    private readonly struct Point
    {
        public readonly Fe X, Y, Z, T; // 扩展坐标:x=X/Z, y=Y/Z, xy=T/Z
        public Point(Fe x, Fe y, Fe z, Fe t) { X = x; Y = y; Z = z; T = t; }
        public static readonly Point Identity = new(Fe.Zero, Fe.One, Fe.One, Fe.Zero);

        /// <summary>add-2008-hwcd(d 非平方 ⇒ 完备公式,任意输入无需特判)。</summary>
        public static Point Add(Point p, Point q)
        {
            Fe a = (p.Y - p.X) * (q.Y - q.X);
            Fe b = (p.Y + p.X) * (q.Y + q.X);
            Fe c = D2 * p.T * q.T;
            Fe d = p.Z * q.Z; d = d + d;
            Fe e = b - a, f = d - c, g = d + c, h = b + a;
            return new Point(e * f, g * h, f * g, e * h);
        }

        /// <summary>dbl-2008-hwcd。</summary>
        public static Point Double(Point p)
        {
            Fe a = p.X.Square();
            Fe b = p.Y.Square();
            Fe c = p.Z.Square(); c = c + c;
            Fe d = Fe.Zero - a;
            Fe e = (p.X + p.Y).Square() - a - b;
            Fe g = d + b, f = g - c, h = d - b;
            return new Point(e * f, g * h, f * g, e * h);
        }
    }

    // ── 域运算 mod p(p = 2^255-19),5×51-bit limb ──

    private readonly struct Fe
    {
        // 不变量:乘法输出经进位链 < 2^51+ε;加法输出 < 2^52;减法(4p 偏移)输出 < 2^54。
        // 乘法输入上界 2^54(链式加减的最坏情形):19·4·(2^54)² < 2^115,UInt128 无溢出。
        // 减法右操作数最大为加法输出(< 2^52+ε)< SubBias,不会下溢。
        public readonly ulong L0, L1, L2, L3, L4;
        private Fe(ulong l0, ulong l1, ulong l2, ulong l3, ulong l4) { L0 = l0; L1 = l1; L2 = l2; L3 = l3; L4 = l4; }

        private const int Shift = 51;
        private const ulong Mask = (1UL << Shift) - 1;
        private const ulong SubBias0 = 0x1FFFFFFFFFFFB4;  // 4·(2^51-19),limb0 偏移(合计 = 4p)
        private const ulong SubBias = 0x1FFFFFFFFFFFFC;   // 4·(2^51-1)

        public static readonly Fe Zero = new(0, 0, 0, 0, 0);
        public static readonly Fe One = new(1, 0, 0, 0, 0);

        private static readonly BigInteger P = (BigInteger.One << 255) - 19;

        /// <summary>由规范十进制常量构造(静态初始化一次性,BigInteger 归约后拆 limb)。</summary>
        public static Fe FromDecimal(string value)
        {
            BigInteger v = BigInteger.Parse(value, System.Globalization.CultureInfo.InvariantCulture) % P;
            BigInteger limbMask = (BigInteger.One << Shift) - 1;
            ulong l0 = (ulong)(v & limbMask); v >>= Shift;
            ulong l1 = (ulong)(v & limbMask); v >>= Shift;
            ulong l2 = (ulong)(v & limbMask); v >>= Shift;
            ulong l3 = (ulong)(v & limbMask); v >>= Shift;
            ulong l4 = (ulong)(v & limbMask);
            return new Fe(l0, l1, l2, l3, l4);
        }

        /// <summary>donna fexpand:32B 小端 → 5 limb(调用方已处理 sign 位;允许值 ≥ p)。</summary>
        public static Fe FromBytesLittle(ReadOnlySpan<byte> b)
        {
            ulong l0 = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(0, 8)) & Mask;
            ulong l1 = (BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(6, 8)) >> 3) & Mask;
            ulong l2 = (BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(12, 8)) >> 6) & Mask;
            ulong l3 = (BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(19, 8)) >> 1) & Mask;
            ulong l4 = (BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(24, 8)) >> 12) & Mask;
            return new Fe(l0, l1, l2, l3, l4);
        }

        /// <summary>donna fcontract:完全归约(条件减 p)后写 32B 小端。
        /// 打包布局:l0→bit0、l1→bit51、l2→bit102、l3→bit153、l4→bit204,按 8B 边界切四刀。</summary>
        public byte[] ToBytesLittle()
        {
            var (l0, l1, l2, l3, l4) = CanonicalLimbs();
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0, 8), l0 | (l1 << 51));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8, 8), (l1 >> 13) | (l2 << 38));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16, 8), (l2 >> 26) | (l3 << 25));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24, 8), (l3 >> 39) | (l4 << 12));
            return b;
        }

        public bool IsNegative() => (CanonicalLimbs().L0 & 1) != 0;

        public bool IsZero()
        {
            var (l0, l1, l2, l3, l4) = CanonicalLimbs();
            return (l0 | l1 | l2 | l3 | l4) == 0;
        }

        private (ulong L0, ulong L1, ulong L2, ulong L3, ulong L4) CanonicalLimbs()
        {
            ulong l0 = L0, l1 = L1, l2 = L2, l3 = L3, l4 = L4;
            l1 += l0 >> Shift; l0 &= Mask;
            l2 += l1 >> Shift; l1 &= Mask;
            l3 += l2 >> Shift; l2 &= Mask;
            l4 += l3 >> Shift; l3 &= Mask;
            l0 += 19 * (l4 >> Shift); l4 &= Mask;
            l1 += l0 >> Shift; l0 &= Mask;   // 再来一轮,l0 折叠后的进位传完
            l2 += l1 >> Shift; l1 &= Mask;
            l3 += l2 >> Shift; l2 &= Mask;
            l4 += l3 >> Shift; l3 &= Mask;
            l0 += 19 * (l4 >> Shift); l4 &= Mask;
            l1 += l0 >> Shift; l0 &= Mask;
            // 此时值 < 2^255+小量;条件减 p:q = 1 ⟺ 值 ≥ 2^255-19
            ulong q = (l0 + 19) >> Shift;
            q = (l1 + q) >> Shift;
            q = (l2 + q) >> Shift;
            q = (l3 + q) >> Shift;
            q = (l4 + q) >> Shift;
            l0 += 19 * q;                    // q=1 时 +19 使进位翻过 2^255,再截掉即 -p
            l1 += l0 >> Shift; l0 &= Mask;
            l2 += l1 >> Shift; l1 &= Mask;
            l3 += l2 >> Shift; l2 &= Mask;
            l4 += l3 >> Shift; l3 &= Mask;
            l4 &= Mask;
            return (l0, l1, l2, l3, l4);
        }

        public static Fe operator +(Fe a, Fe b) =>
            new(a.L0 + b.L0, a.L1 + b.L1, a.L2 + b.L2, a.L3 + b.L3, a.L4 + b.L4);

        public static Fe operator -(Fe a, Fe b) =>
            new(a.L0 + SubBias0 - b.L0, a.L1 + SubBias - b.L1, a.L2 + SubBias - b.L2,
                a.L3 + SubBias - b.L3, a.L4 + SubBias - b.L4);

        /// <summary>donna fmul:模多项式 x⁵-19(x=2^51)的 schoolbook 卷积 + 进位链。</summary>
        public static Fe operator *(Fe a, Fe b)
        {
            UInt128 t0 = (UInt128)a.L0 * b.L0 + 19 * ((UInt128)a.L1 * b.L4 + (UInt128)a.L2 * b.L3 + (UInt128)a.L3 * b.L2 + (UInt128)a.L4 * b.L1);
            UInt128 t1 = (UInt128)a.L0 * b.L1 + (UInt128)a.L1 * b.L0 + 19 * ((UInt128)a.L2 * b.L4 + (UInt128)a.L3 * b.L3 + (UInt128)a.L4 * b.L2);
            UInt128 t2 = (UInt128)a.L0 * b.L2 + (UInt128)a.L1 * b.L1 + (UInt128)a.L2 * b.L0 + 19 * ((UInt128)a.L3 * b.L4 + (UInt128)a.L4 * b.L3);
            UInt128 t3 = (UInt128)a.L0 * b.L3 + (UInt128)a.L1 * b.L2 + (UInt128)a.L2 * b.L1 + (UInt128)a.L3 * b.L0 + 19 * ((UInt128)a.L4 * b.L4);
            UInt128 t4 = (UInt128)a.L0 * b.L4 + (UInt128)a.L1 * b.L3 + (UInt128)a.L2 * b.L2 + (UInt128)a.L3 * b.L1 + (UInt128)a.L4 * b.L0;
            t1 += t0 >> Shift; ulong l0 = (ulong)t0 & Mask;
            t2 += t1 >> Shift; ulong l1 = (ulong)t1 & Mask;
            t3 += t2 >> Shift; ulong l2 = (ulong)t2 & Mask;
            t4 += t3 >> Shift; ulong l3 = (ulong)t3 & Mask;
            // 折叠 t4 进位(×19)回 t0:必须留在 UInt128——输入 limb 大时 t4>>51 可达 ~2^62,19 倍会溢出 ulong
            UInt128 f0 = l0 + 19 * (t4 >> Shift); ulong l4 = (ulong)t4 & Mask;
            l1 += (ulong)(f0 >> Shift); l0 = (ulong)f0 & Mask;
            return new Fe(l0, l1, l2, l3, l4);
        }

        public Fe Square() => this * this;

        /// <summary>TweetNaCl inv25519:x^(p-2) = x^(2^255-21)(费马小定理求逆)。</summary>
        public Fe Invert()
        {
            Fe r = this;
            for (int a = 253; a >= 0; a--)
            {
                r = r.Square();
                if (a != 2 && a != 4) r *= this;
            }
            return r;
        }

        /// <summary>TweetNaCl pow2523:x^(2^252-3)(点解码的候选平方根指数)。</summary>
        public Fe Pow22523()
        {
            Fe r = this;
            for (int a = 250; a >= 0; a--)
            {
                r = r.Square();
                if (a != 1) r *= this;
            }
            return r;
        }
    }
}
