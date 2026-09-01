using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>高度图加载（逐字移植 source/graphics/MapIO.cpp ParseHeightmapImage +
    /// rmgen/library.js 的 convertHeightmap1Dto2D/extractHeightmap）。
    /// 内置最小 PNG 解码器（8 位灰度、非隔行——hellas.png 即此格式），
    /// 内核不依赖 Godot 图像 API，保持跨平台确定性。</summary>
    public static class HeightmapLoader
    {
        private const int PatchSize = 16;   // source/graphics/Patch.h PATCH_SIZE

        /// <summary>Engine.LoadHeightmapImage：PNG → (tileSize+1)² 的 u16 数组。
        /// tileSize = min(w,h) 截到 PATCH_SIZE 倍数；8→16 位 ×256；行垂直翻转
        /// （heightmap[(tileSize - y) * (tileSize + 1) + x]），最右/最下顶点重复末像素。</summary>
        public static ushort[] LoadHeightmapImage(string path)
        {
            var (pixels, width, height) = DecodeGray8Png(File.ReadAllBytes(path));

            int tileSize = Math.Min(width, height);
            tileSize -= tileSize % PatchSize;

            var heightmap = new ushort[(tileSize + 1) * (tileSize + 1)];
            for (int y = 0; y < tileSize + 1; ++y)
                for (int x = 0; x < tileSize + 1; ++x)
                {
                    int offset = Math.Min(y, tileSize - 1) * width + Math.Min(x, tileSize - 1);
                    heightmap[(tileSize - y) * (tileSize + 1) + x] = (ushort)(256 * pixels[offset]);
                }
            return heightmap;
        }

        /// <summary>convertHeightmap1Dto2D——行主序一维 → [x][y] 二维（Float32Array 语义）。</summary>
        public static float[][] ConvertHeightmap1Dto2D(ushort[] heightmap)
        {
            int hmSize = (int)Math.Sqrt(heightmap.Length);
            var result = new float[hmSize][];
            for (int x = 0; x < hmSize; ++x)
            {
                result[x] = new float[hmSize];
                for (int y = 0; y < hmSize; ++y)
                    result[x][y] = heightmap[y * hmSize + x];
            }
            return result;
        }

        /// <summary>extractHeightmap——取 topLeft 起 size×size 的子区域。</summary>
        public static float[][] ExtractHeightmap(float[][] heightmap, RmgenVector2D topLeft, int size)
        {
            var result = new float[size][];
            for (int x = 0; x < size; ++x)
            {
                result[x] = new float[size];
                for (int y = 0; y < size; ++y)
                    result[x][y] = heightmap[x + (int)topLeft.X][y + (int)topLeft.Y];
            }
            return result;
        }

        // ── 最小 PNG 解码（IHDR/IDAT/IEND；colortype 0、bitdepth 8、非隔行）──

        private static (byte[] pixels, int width, int height) DecodeGray8Png(byte[] data)
        {
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50)
                throw new InvalidDataException("not a PNG");

            int width = 0, height = 0, bitDepth = 8;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                int length = ReadBE32(data, pos);
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == "IHDR")
                {
                    width = ReadBE32(data, pos + 8);
                    height = ReadBE32(data, pos + 12);
                    bitDepth = data[pos + 16];
                    int colorType = data[pos + 17];
                    int interlace = data[pos + 20];
                    // elephantine.png 是 1 位灰度（上游生成时阈值化过）；其余为 8 位。
                    if (bitDepth != 1 && bitDepth != 8 || colorType != 0 || interlace != 0)
                        throw new InvalidDataException(
                            $"unsupported PNG: bitDepth={bitDepth} colorType={colorType} interlace={interlace}");
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, pos + 8, length);
                }
                else if (type == "IEND")
                {
                    break;
                }
                pos += 12 + length;   // length + type + data + crc
            }

            // zlib 流（ZLibStream 处理 2 字节头 + adler32 尾）
            byte[] raw;
            using (var zs = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                zs.CopyTo(ms);
                raw = ms.ToArray();
            }

            // 逐行解滤波。8 位灰度：每行 = 1 滤波字节 + width 像素字节；
            // 1 位灰度：每行 = 1 滤波字节 + ceil(width/8) 压缩字节（解滤波按压缩字节算，
            // bpp=1——PNG 规范对 1 位图的滤波单位是 1 字节），解完再逐位展开成 0/255。
            if (bitDepth == 1)
                return DecodeBitPng(raw, width, height);

            var pixels = new byte[width * height];
            int stride = width + 1;
            for (int y = 0; y < height; ++y)
            {
                int rowStart = y * stride;
                int filter = raw[rowStart];
                for (int x = 0; x < width; ++x)
                {
                    int cur = raw[rowStart + 1 + x];
                    int left = x > 0 ? pixels[y * width + x - 1] : 0;
                    int up = y > 0 ? pixels[(y - 1) * width + x] : 0;
                    int upLeft = x > 0 && y > 0 ? pixels[(y - 1) * width + x - 1] : 0;
                    int val = filter switch
                    {
                        0 => cur,
                        1 => cur + left,
                        2 => cur + up,
                        3 => cur + (left + up) / 2,
                        4 => cur + Paeth(left, up, upLeft),
                        _ => throw new InvalidDataException("bad PNG filter " + filter),
                    };
                    pixels[y * width + x] = (byte)(val & 0xFF);
                }
            }
            return (pixels, width, height);
        }

        /// <summary>1 位灰度 PNG：按压缩字节解滤波，然后逐位展开（0/1 → 0/255）。</summary>
        private static (byte[] pixels, int width, int height) DecodeBitPng(byte[] raw,
            int width, int height)
        {
            int packedWidth = (width + 7) / 8;
            int stride = packedWidth + 1;
            var packed = new byte[packedWidth * height];
            for (int y = 0; y < height; ++y)
            {
                int rowStart = y * stride;
                int filter = raw[rowStart];
                for (int x = 0; x < packedWidth; ++x)
                {
                    int cur = raw[rowStart + 1 + x];
                    int left = x > 0 ? packed[y * packedWidth + x - 1] : 0;
                    int up = y > 0 ? packed[(y - 1) * packedWidth + x] : 0;
                    int upLeft = x > 0 && y > 0 ? packed[(y - 1) * packedWidth + x - 1] : 0;
                    int val = filter switch
                    {
                        0 => cur,
                        1 => cur + left,
                        2 => cur + up,
                        3 => cur + (left + up) / 2,
                        4 => cur + Paeth(left, up, upLeft),
                        _ => throw new InvalidDataException("bad PNG filter " + filter),
                    };
                    packed[y * packedWidth + x] = (byte)(val & 0xFF);
                }
            }

            var pixels = new byte[width * height];
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                    pixels[y * width + x] =
                        (byte)((packed[y * packedWidth + x / 8] >> (7 - x % 8) & 1) * 255);
            return (pixels, width, height);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static int ReadBE32(byte[] d, int o)
            => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    }
}
