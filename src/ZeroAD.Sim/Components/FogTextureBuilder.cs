using System;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Builds the fog-of-war texture payload (R8: 0 unexplored, 128 explored, 255 visible)
/// from a <see cref="LosGrid"/>, plus a separable 7-tap binomial blur (1,6,15,20,15,6,1)/64
/// that softens fog edges. Godot-free byte buffers (kernel-testable); the presentation
/// layer (minimap, world fog shader) uploads them to an L8 image. Buffers are reused
/// across calls — the returned array is valid until the next Build call.
/// </summary>
public sealed class FogTextureBuilder
{
    // Binomial coefficients sum to 64 — exact power-of-two division, no rounding drift.
    private static readonly int[] Kernel = { 1, 6, 15, 20, 15, 6, 1 };

    private byte[] _base = Array.Empty<byte>();
    private byte[] _scratch = Array.Empty<byte>();
    private int _n;

    /// <summary>Unblurred R8 fill from the grid. Row-major [j*n+i].</summary>
    public byte[] BuildBase(LosGrid los, int player)
    {
        EnsureSize(los.VerticesPerSide);
        for (int j = 0; j < _n; j++)
            for (int i = 0; i < _n; i++)
                _base[j * _n + i] = los.IsVisible(player, i, j) ? (byte)255
                    : los.IsExplored(player, i, j) ? (byte)128
                    : (byte)0;
        return _base;
    }

    /// <summary>Base fill + horizontal/vertical binomial passes (edges clamped).
    /// Output size is los.VerticesPerSide.</summary>
    public byte[] BuildBlurred(LosGrid los, int player)
    {
        BuildBase(los, player);
        BlurPass(_base, _scratch, horizontal: true);
        BlurPass(_scratch, _base, horizontal: false);
        return _base;
    }

    private void EnsureSize(int n)
    {
        if (n == _n) return;
        _n = n;
        _base = new byte[n * n];
        _scratch = new byte[n * n];
    }

    private void BlurPass(byte[] src, byte[] dst, bool horizontal)
    {
        int n = _n;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int sum = 0;
                for (int k = -3; k <= 3; k++)
                {
                    int ci = horizontal ? Math.Clamp(i + k, 0, n - 1) : i;
                    int cj = horizontal ? j : Math.Clamp(j + k, 0, n - 1);
                    sum += src[cj * n + ci] * Kernel[k + 3];
                }
                dst[j * n + i] = (byte)(sum >> 6);
            }
    }
}
