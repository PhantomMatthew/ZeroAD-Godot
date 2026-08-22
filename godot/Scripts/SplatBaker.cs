using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// 把 PMP 的逐 tile 贴图(STileDesc tex1 + priority)在 CPU 侧按 64m patch(对齐 C++
/// PatchSize=16 tiles)分块烘成地形 albedo,每 patch 一张独立贴图。
/// 动机:Compatibility 渲染器下自定义 spatial shader 完全收不到方向光阴影
/// (6 变体最小场景实证),而 splat 混合必须自定义 shader——故把混合结果烘焙,
/// 地形换 StandardMaterial3D(不透明,受影/光照由引擎标准管线来);雾/领土挪到
/// fog_territory_overlay。分块(而非全图一张)是为了让像素密度与地图面积无关地
/// 保持在目标 texel/m——全图一张贴图的边长有上限(GPU 贴图尺寸/内存),地图越大
/// 每米分到的像素越少;分块后每块独立烘焙,密度由 ComputePatchPixelSize 按显存
/// 预算统一给出,不因地图大小被稀释。
///
/// 混合算法逐位移植上游 renderer/PatchRData.cpp BuildBlends/AddBlend +
/// renderer/AlphaMapCalculator.cpp:
///   - tile 的贴图就是 tex1(tex2 是遗产字段,上游 MapReader 只读 Tex1Index);
///   - 每 tile 收集 3×3 邻域 (贴图,priority),按 priority 降序(同优先级按贴图名
///     降序,对齐 STileBlend::DecreasingPriority)排序、同贴图相邻合并(位掩码 OR),
///     从含自身位的条目起截断——剩下的就是"压在本 tile 底图之上的高优先级邻图";
///   - 覆盖层按升序(back-to-front)依次以标准 alpha 形状图(art/textures/terrain/
///     alphamaps/standard 的 14 张 64×64 灰度图)调制叠上,形状由 8 邻居位掩码经
///     CAlphaMapCalculator 的查找表 + 翻转/旋转旗标确定;
///   - 每层贴图按 terrain XML 的 <props size angle/> 做 UV 变换(上游
///     CTerrainTextureEntry::GenerateTextureMatrix;缺省 32m/45°)——地面颗粒粗细
///     与走向由此与 C++ 版一致(此前固定 4m 轴对齐平铺,观感细 8~25 倍)。
/// 每层贴图保留自身原生边长(实测语料 512/1024/2048 三档都有,均为 2 的幂;此前
/// 强制降到 512 会把原生更清晰的贴图平白丢 2~4 倍细节)。带 mipmap 链。
/// </summary>
public static class SplatBaker
{
    /// <summary>缺失贴图时的占位图边长;真实贴图改用各自的原生边长(见 TerrainInfo.Size)。</summary>
    private const int PlaceholderSize = 512;
    private const int ShapeSize = 64;

    /// <summary>激进档目标密度(64m patch × 32 texel/m = 2048px 理想边长)。</summary>
    private const float TargetTexelsPerMeter = 32f;
    /// <summary>全部 patch 贴图(基础 mip 层,RGB8)的显存安全上限;超过则整体等比降档,
    /// 防止 giant 地图(32 patch/边)把显存打爆。</summary>
    private const long TerrainVramBudgetBytes = 2L * 1024 * 1024 * 1024;
    private const int MinPatchPx = 512;
    private const int MaxPatchPx = 4096;

    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    // 标准 alpha 形状(atlas 索引序,对齐 TerrainTextureManager::LoadAlphaMap 的 fnames)。
    private static readonly string[] ShapeFiles =
    {
        "blendcircle", "blendlshape", "blendedge", "blendedgecorner",
        "blendedgetwocorners", "blendfourcorners", "blendtwooppositecorners",
        "blendlshapecorner", "blendtwocorners", "blendcorner", "blendtwoedges",
        "blendthreecorners", "blendushape", "blendbad",
    };

    // BlendOffsets(bit n → (dz,dx)):bit0=N(0,-1) bit1=NW bit2=W bit3=SW bit4=S
    // bit5=SE bit6=E bit7=NE bit8=self(逐位对齐 PatchRData.cpp)。
    private static readonly (int dz, int dx)[] BlendOffsets =
    {
        (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, 0),
    };

    private const uint FlagFlipV = 0x01;
    private const uint FlagFlipU = 0x02;
    private const uint FlagRot90 = 0x04;
    private const uint FlagRot180 = 0x08;
    private const uint FlagRot270 = 0x10;

    private static byte[][]? _shapes;   // 14 × 64×64 灰度;null=未加载;空数组=加载失败

    /// <summary>一次性准备好的烘焙上下文(逐层贴图/UV 矩阵/逐 tile 覆盖层),在同一张地图的
    /// 所有 patch 之间共享,避免每 patch 重复解码贴图或重算 BuildTileOverlays。仅供本项目
    /// 内(TerrainRenderer)使用,故 internal——公开面只需 BakeAlbedo/BakeAlbedoPatch/
    /// ComputePatchPixelSize。</summary>
    internal sealed class BakeContext
    {
        public required byte[][] Layers;
        public required int[] LayerSizes;
        public required (float m11, float m13, float m21, float m23)[] UvMat;
        public required Overlay[][] Overlays;
        public required ushort[] TileTex1;
        public required int TilesPerSide;
        public required float TileSize;
        public required float MapSizeMeters;
        public required int TexCount;
    }

    /// <summary>解码贴图层 + 解析 UV 矩阵 + 预计算逐 tile 覆盖层。PMP 无贴图数据时返回
    /// null(调用方走纯色/单张贴图回退,不烘焙、不分块)。</summary>
    internal static BakeContext? PrepareBakeContext(PmpMap map)
    {
        int texCount = map.TextureNames.Count;
        if (texCount == 0 || map.TileTex1.Length == 0)
        {
            ZeroAD.Sim.Diag.Warn("Terrain", "SplatBaker: no texture data in PMP; falling back to flat terrain");
            return null;
        }

        EnsureShapesLoaded();

        // 只解码真正被 tile 引用的图层(tex1;tex2 上游忽略,不参与)。
        // 每层同时解析 terrain XML 的 <props size angle/>(平铺尺寸米 + 旋转角度,
        // 缺省 32m/45°)——上游 GenerateTextureMatrix 的逐贴图 UV 变换:
        //   u = (cos a·x − sin a·z)/size,  v = (−sin a·x − cos a·z)/size。
        int t = map.TilesPerSide;
        var used = new bool[texCount];
        for (int i = 0; i < map.TileTex1.Length; i++)
            used[Math.Clamp(map.TileTex1[i], 0, texCount - 1)] = true;
        var layers = new byte[texCount][];
        var layerSizes = new int[texCount];
        var uvMat = new (float m11, float m13, float m21, float m23)[texCount];
        for (int i = 0; i < texCount; i++)
        {
            if (!used[i]) continue;
            var info = LoadTerrainInfo(map.TextureNames[i]);
            layers[i] = info.Pixels;
            layerSizes[i] = info.Size;
            float cos = MathF.Cos(info.AngleRad) / info.SizeMeters;
            float sin = MathF.Sin(info.AngleRad) / info.SizeMeters;
            uvMat[i] = (cos, -sin, -sin, -cos);
        }

        // 逐 tile 预计算覆盖层(BuildBlends 移植;烘焙不需要 draw-call 合批层)。
        var overlays = BuildTileOverlays(map, t, texCount);
        int withOv = 0, ovSum = 0;
        for (int i = 0; i < overlays.Length; i++)
        {
            if (overlays[i].Length == 0) continue;
            withOv++;
            ovSum += overlays[i].Length;
        }
        ZeroAD.Sim.Diag.Log("Terrain",
            $"SplatBaker: {withOv}/{overlays.Length} tiles have overlays (avg {ovSum / Math.Max(1, withOv):F1})");

        return new BakeContext
        {
            Layers = layers,
            LayerSizes = layerSizes,
            UvMat = uvMat,
            Overlays = overlays,
            TileTex1 = map.TileTex1,
            TilesPerSide = t,
            TileSize = PmpMap.TileSize,
            MapSizeMeters = map.MapSizeMeters,
            TexCount = texCount,
        };
    }

    /// <summary>给定 PatchesPerSide,按显存预算算出统一的每-patch 贴图边长(2 的幂)。
    /// 理想密度 = TargetTexelsPerMeter(64m patch × 32 = 2048px);若全部 patch 的基础层
    /// (RGB8,不计 mipmap 的 ~33% 额外开销)总字节数超过 TerrainVramBudgetBytes,按
    /// sqrt(预算/理想总量) 整体降档,再夹在 [MinPatchPx, MaxPatchPx] 之间。giant 地图
    /// (32 patch/边)会因此落到下限附近,但仍明显高于此前"全图一张、随地图增大被稀释"
    /// 的密度。</summary>
    internal static int ComputePatchPixelSize(int patchesPerSide)
    {
        float patchWorldSize = PmpMap.PatchSize * PmpMap.TileSize;
        int idealPx = NextPow2((int)MathF.Round(patchWorldSize * TargetTexelsPerMeter));

        int patchCount = Math.Max(1, patchesPerSide * patchesPerSide);
        long idealBytesPerPatch = (long)idealPx * idealPx * 3;
        long idealTotal = idealBytesPerPatch * patchCount;
        if (idealTotal > TerrainVramBudgetBytes)
        {
            float scale = MathF.Sqrt((float)((double)TerrainVramBudgetBytes / idealTotal));
            idealPx = PrevPow2(Math.Max(1, (int)(idealPx * scale)));
        }
        return Math.Clamp(idealPx, MinPatchPx, MaxPatchPx);
    }

    private static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    private static int PrevPow2(int v)
    {
        int p = 1;
        while (p * 2 <= v) p <<= 1;
        return p;
    }

    /// <summary>烘焙整张地形 albedo(单张巨图,不分块)。保留供小场景/工具/测试直接取
    /// 一整张预览图;生产地形渲染路径改走 <see cref="BakeAlbedoPatch"/> 分块。</summary>
    public static Image? BakeAlbedo(PmpMap map)
    {
        var ctx = PrepareBakeContext(map);
        if (ctx == null) return null;
        int px = 2048;
        while (px < ctx.TilesPerSide * 21 && px < 8192) px *= 2;
        return BakeRegion(ctx, 0f, 0f, ctx.MapSizeMeters, px, map.TextureNames.Count);
    }

    /// <summary>烘焙一个 64m×64m patch 的地形 albedo(含 mipmap)。(patchX,patchZ) 为 patch
    /// 网格坐标(0..PatchesPerSide-1)。</summary>
    internal static Image BakeAlbedoPatch(BakeContext ctx, int patchX, int patchZ, int pxSize)
    {
        float patchWorldSize = PmpMap.PatchSize * PmpMap.TileSize;
        float originX = patchX * patchWorldSize;
        float originZ = patchZ * patchWorldSize;
        return BakeRegion(ctx, originX, originZ, patchWorldSize, pxSize, ctx.TexCount);
    }

    /// <summary>核心采样循环(逐位移植上游 RenderBlends 的每像素混合):对 [origin, origin+size)
    /// 世界矩形按 pxSize×pxSize 均匀采样,tile 查表 + 覆盖层 alpha 叠加,与全图/分块共用。</summary>
    private static Image BakeRegion(
        BakeContext ctx, float originX, float originZ, float regionSizeMeters, int pxSize, int texCount)
    {
        var layers = ctx.Layers;
        var layerSizes = ctx.LayerSizes;
        var uvMat = ctx.UvMat;
        var overlays = ctx.Overlays;
        var tileTex1 = ctx.TileTex1;
        int t = ctx.TilesPerSide;
        float tileSize = ctx.TileSize;
        var outp = new byte[pxSize * pxSize * 3];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Parallel.For(0, pxSize, y =>
        {
            float wz = originZ + (y + 0.5f) / pxSize * regionSizeMeters;
            int tz = Math.Clamp((int)(wz / tileSize), 0, t - 1);
            float fv = wz / tileSize - tz;              // tile 内位置(0..1,+z 向)
            int rowBytes = y * pxSize * 3;
            var rgb = new byte[3];
            var ovRgb = new byte[3];   // 行内复用(每行单线程),勿提为共享静态
            for (int x = 0; x < pxSize; x++)
            {
                float wx = originX + (x + 0.5f) / pxSize * regionSizeMeters;
                int tx = Math.Clamp((int)(wx / tileSize), 0, t - 1);
                float fu = wx / tileSize - tx;

                int tileIdx = tz * t + tx;
                int baseTex = tileTex1[tileIdx];
                SampleLayer(layers[baseTex], layerSizes[baseTex], uvMat[baseTex], wx, wz, rgb);

                var ovs = overlays[tileIdx];
                for (int k = 0; k < ovs.Length; k++)
                {
                    ref readonly var ov = ref ovs[k];
                    float alpha = ov.Full ? 1f : SampleShape(ov, fu, fv);
                    // 形状图朝向一旦和 tile 边错开,交界 alpha 会落到 0,CC/河岸就剩 4m 硬块。
                    // 用邻居位几何衰减垫底,有 alphamap 时取两者较大值(C++ 溅开 + 保证硬边被抹掉)。
                    if (ov.Mask != 0)
                        alpha = MathF.Max(alpha, GeometricAlpha(ov.Mask, fu, fv));
                    if (alpha <= 0f) continue;
                    SampleLayer(layers[ov.Tex], layerSizes[ov.Tex], uvMat[ov.Tex], wx, wz, ovRgb);
                    float a = alpha > 1f ? 1f : alpha;
                    rgb[0] = (byte)(rgb[0] + (ovRgb[0] - rgb[0]) * a + 0.5f);
                    rgb[1] = (byte)(rgb[1] + (ovRgb[1] - rgb[1]) * a + 0.5f);
                    rgb[2] = (byte)(rgb[2] + (ovRgb[2] - rgb[2]) * a + 0.5f);
                }

                // 覆盖层依赖 priority 截断:低优先级贴图(常是河岸/CC 底图一侧)根本没有 overlay。
                // 再按 4 邻接不同 tex1 做一次与 priority 无关的边混合,楼梯两侧都会软掉。
                BlendCardinalNeighbor(layers, layerSizes, uvMat, tileTex1, t, tx, tz,
                    fu, fv, wx, wz, rgb, ovRgb);

                int o = rowBytes + x * 3;
                outp[o] = rgb[0]; outp[o + 1] = rgb[1]; outp[o + 2] = rgb[2];
            }
        });

        var img = Image.CreateFromData(pxSize, pxSize, false, Image.Format.Rgb8, outp);
        img.GenerateMipmaps();
        sw.Stop();
        ZeroAD.Sim.Diag.Log("Terrain",
            $"Terrain splat baked: {pxSize}x{pxSize} region ({regionSizeMeters:F0}m from {originX:F0},{originZ:F0}) " +
            $"from {texCount} textures in {sw.ElapsedMilliseconds}ms");
        return img;
    }

    // ── BuildBlends 移植(逐 tile 覆盖层)──

    /// <summary>单条覆盖层:贴图索引 + 预解析的形状角点 UV(Full=true → 全 tile 覆盖)。</summary>
    internal struct Overlay
    {
        public int Tex;
        public bool Full;
        public int Shape;
        public int Mask; // 邻居位,形状图失效时走几何衰减
        // 四个角点的形状 UV:0=(0,0) 1=(1,0) 2=(1,1) 3=(0,1)(tile 内坐标系)。
        public float U0, V0, U1, V1, U2, V2, U3, V3;
    }

    private struct BlendEntry
    {
        public int Tex;
        public int Priority;
        public int Mask;   // bit n = 邻居位;bit8 = 自身
    }

    private static Overlay[][] BuildTileOverlays(PmpMap map, int t, int texCount)
    {
        var result = new Overlay[t * t][];
        bool hasPriority = map.TilePriority.Length == map.TileTex1.Length;
        var names = map.TextureNames;

        for (int tz = 0; tz < t; tz++)
        {
            for (int tx = 0; tx < t; tx++)
            {
                // 3×3 邻域(含自身),越界邻居跳过(上游 GetTile null → continue)。
                var blends = new List<BlendEntry>(9);
                for (int n = 0; n < 9; n++)
                {
                    int nx = tx + BlendOffsets[n].dx;
                    int nz = tz + BlendOffsets[n].dz;
                    if (nx < 0 || nx >= t || nz < 0 || nz >= t) continue;
                    int ni = nz * t + nx;
                    blends.Add(new BlendEntry
                    {
                        Tex = Math.Clamp(map.TileTex1[ni], 0, texCount - 1),
                        Priority = hasPriority ? (int)map.TilePriority[ni] : 0,
                        Mask = 1 << n,
                    });
                }

                // 降序 priority;同优先级按贴图名降序(STileBlend::DecreasingPriority)。
                blends.Sort((a, b) =>
                {
                    if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
                    return string.CompareOrdinal(names[b.Tex], names[a.Tex]);
                });

                // 相邻同贴图合并(掩码 OR)。
                var merged = new List<BlendEntry>(blends.Count);
                foreach (var b in blends)
                {
                    if (merged.Count > 0 && merged[^1].Tex == b.Tex)
                        merged[^1] = new BlendEntry { Tex = b.Tex, Priority = merged[^1].Priority, Mask = merged[^1].Mask | b.Mask };
                    else
                        merged.Add(b);
                }

                // 从含自身位(bit8)的条目起截断——压在本 tile 之上的只有更高优先级的。
                int cut = merged.FindIndex(e => (e.Mask & (1 << 8)) != 0);
                if (cut >= 0) merged.RemoveRange(cut, merged.Count - cut);

                // 绘制顺序 = 升序(原栈 back-to-front):逆序遍历截断后的降序栈。
                var ovs = new List<Overlay>(merged.Count);
                for (int k = merged.Count - 1; k >= 0; k--)
                {
                    var ov = ResolveOverlay(merged[k].Tex, merged[k].Mask);
                    if (ov.HasValue) ovs.Add(ov.Value);
                }
                result[tz * t + tx] = ovs.ToArray();
            }
        }
        return result;
    }

    /// <summary>AddBlend 移植:8 邻居掩码 → 形状 + 翻转/旋转 → 角点 UV。
    /// count==8(全邻居同贴图)→ null(无需覆盖);其余经 Calculate 查表。</summary>
    private static Overlay? ResolveOverlay(int tex, int mask)
    {
        // shape8[m] = 位设置 ? 0 : 1(形状描述"缺席"的邻居)。
        var shape = new int[8];
        for (int m = 0; m < 8; m++)
            shape[m] = (mask & (1 << m)) != 0 ? 0 : 1;

        int alphamap = Calculate(shape, out uint flags);
        // C++ AddBlend: alphamap==-1 → 不画这条覆盖(全邻居同贴图,base 已够)。
        // 先前当成 Full=true 会把整 tile 盖成邻图,交界处变成 4m 色块。
        if (alphamap == -1)
            return null;

        float u0 = 0, u1 = 1, v0 = 0, v1 = 1;   // 独立 PNG,全幅
        if ((flags & FlagFlipU) != 0) (u0, u1) = (u1, u0);
        if ((flags & FlagFlipV) != 0) (v0, v1) = (v1, v0);
        int baseIdx = (flags & FlagRot90) != 0 ? 1
            : (flags & FlagRot180) != 0 ? 2
            : (flags & FlagRot270) != 0 ? 3 : 0;

        // AddBlend:vtx[(base+0)%4]=(u0,v0) … 分配到 tile 四角。
        var ov = new Overlay { Tex = tex, Shape = alphamap, Mask = mask };
        var corners = new (float u, float v)[4];
        corners[(baseIdx + 0) % 4] = (u0, v0);
        corners[(baseIdx + 1) % 4] = (u1, v0);
        corners[(baseIdx + 2) % 4] = (u1, v1);
        corners[(baseIdx + 3) % 4] = (u0, v1);
        ov.U0 = corners[0].u; ov.V0 = corners[0].v;
        ov.U1 = corners[1].u; ov.V1 = corners[1].v;
        ov.U2 = corners[2].u; ov.V2 = corners[2].v;
        ov.U3 = corners[3].u; ov.V3 = corners[3].v;
        return ov;
    }

    /// <summary>tile 内 (fu,fv) 处采样覆盖层 alpha:角点 UV 双线性 → 形状图双线性。</summary>
    private static float SampleShape(in Overlay ov, float fu, float fv)
    {
        var shape = _shapes;
        if (shape == null || shape.Length == 0) return 0f;
        float uTop = ov.U0 + (ov.U1 - ov.U0) * fu;
        float uBot = ov.U3 + (ov.U2 - ov.U3) * fu;
        float u = uTop + (uBot - uTop) * fv;
        float vTop = ov.V0 + (ov.V1 - ov.V0) * fu;
        float vBot = ov.V3 + (ov.V2 - ov.V3) * fu;
        float v = vTop + (vBot - vTop) * fv;

        var data = shape[ov.Shape];
        float sx = u * ShapeSize - 0.5f, sy = v * ShapeSize - 0.5f;
        int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
        float ax = sx - x0, ay = sy - y0;
        int x1 = x0 + 1, y1 = y0 + 1;
        x0 = Math.Clamp(x0, 0, ShapeSize - 1); x1 = Math.Clamp(x1, 0, ShapeSize - 1);
        y0 = Math.Clamp(y0, 0, ShapeSize - 1); y1 = Math.Clamp(y1, 0, ShapeSize - 1);
        float top = data[y0 * ShapeSize + x0] + (data[y0 * ShapeSize + x1] - data[y0 * ShapeSize + x0]) * ax;
        float bot = data[y1 * ShapeSize + x0] + (data[y1 * ShapeSize + x1] - data[y1 * ShapeSize + x0]) * ax;
        return (top + (bot - top) * ay) / 255f;
    }

    /// <summary>形状图在 binaries/ 下(工程外)。用 .NET 读字节再 LoadPngFromBuffer,避免
    /// Image.LoadFromFile 对工程外路径/灰度 PNG 把亮度放进 A 通道、RGB 全 0——Convert(L8)
    /// 只看 RGB,结果整张黑,SampleShape 恒为 0,tile 交界就变成 4m 硬块(CC/河岸那种楼梯)。
    /// 取出的数组保证是 ShapeSize² 的单通道亮度。</summary>
    private static void EnsureShapesLoaded()
    {
        if (_shapes != null) return;
        string dir = Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "..", "binaries", "data", "mods", "public", "art", "textures", "terrain", "alphamaps", "standard"));
        var shapes = new List<byte[]>();
        foreach (var name in ShapeFiles)
        {
            string path = Path.Combine(dir, name + ".png");
            if (!File.Exists(path))
            {
                if (_warned.Add("shapes"))
                    ZeroAD.Sim.Diag.Warn("Terrain", $"SplatBaker: alpha shape maps not found at {dir}; overlays disabled");
                _shapes = Array.Empty<byte[]>();
                return;
            }
            var img = new Image();
            byte[] png = File.ReadAllBytes(path);
            if (img.LoadPngFromBuffer(png) != Error.Ok)
            {
                if (_warned.Add("shapes"))
                    ZeroAD.Sim.Diag.Warn("Terrain", $"SplatBaker: failed to decode alpha shape '{name}.png'");
                _shapes = Array.Empty<byte[]>();
                return;
            }
            if (img.GetWidth() != ShapeSize || img.GetHeight() != ShapeSize)
                img.Resize(ShapeSize, ShapeSize, Image.Interpolation.Bilinear);
            shapes.Add(ExtractLuminance(img));
        }
        _shapes = shapes.ToArray();
        int sum = 0;
        var circle = _shapes[0];
        for (int i = 0; i < circle.Length; i++) sum += circle[i];
        float mean = sum / (float)circle.Length;
        ZeroAD.Sim.Diag.Log("Terrain",
            $"SplatBaker: loaded {shapes.Count} alpha shapes from {dir} (blendcircle mean={mean:F1})");
        // 解码后仍近黑 = 亮度通道没采到,当加载失败,BakeRegion 走几何衰减兜底。
        if (mean < 1f)
        {
            ZeroAD.Sim.Diag.Warn("Terrain", "SplatBaker: alpha shapes decoded black; using geometric edge falloff");
            _shapes = Array.Empty<byte[]>();
        }
    }

    /// <summary>形状图朝向偏差时的几何衰减:按邻居位从对应边/角做 smoothstep。
    /// bit0=-X bit2=-Z bit4=+X bit6=+Z,奇数位为对角。</summary>
    private static float GeometricAlpha(int mask, float fu, float fv)
    {
        float a = 0f;
        if ((mask & (1 << 0)) != 0) a = MathF.Max(a, 1f - fu);
        if ((mask & (1 << 1)) != 0) a = MathF.Max(a, (1f - fu) * (1f - fv));
        if ((mask & (1 << 2)) != 0) a = MathF.Max(a, 1f - fv);
        if ((mask & (1 << 3)) != 0) a = MathF.Max(a, fu * (1f - fv));
        if ((mask & (1 << 4)) != 0) a = MathF.Max(a, fu);
        if ((mask & (1 << 5)) != 0) a = MathF.Max(a, fu * fv);
        if ((mask & (1 << 6)) != 0) a = MathF.Max(a, fv);
        if ((mask & (1 << 7)) != 0) a = MathF.Max(a, (1f - fu) * fv);
        if (a <= 0f) return 0f;
        return a * a * (3f - 2f * a);
    }

    /// <summary>与 priority 无关:当前 tile 四邻若 tex1 不同,在靠那条边的半格内混入邻图。
    /// 覆盖层只把高优先级溅到低优先级上,河岸/CC 低优先级那一侧会留下硬直角;这里两侧都抹。</summary>
    private static void BlendCardinalNeighbor(
        byte[][] layers, int[] layerSizes, (float m11, float m13, float m21, float m23)[] uvMat,
        ushort[] tileTex1, int t, int tx, int tz, float fu, float fv, float wx, float wz,
        byte[] rgb, byte[] scratch)
    {
        int self = tileTex1[tz * t + tx];
        BlendOne(tx - 1, tz, 1f - fu);
        BlendOne(tx + 1, tz, fu);
        BlendOne(tx, tz - 1, 1f - fv);
        BlendOne(tx, tz + 1, fv);

        void BlendOne(int nx, int nz, float along)
        {
            if (along <= 0f || nx < 0 || nz < 0 || nx >= t || nz >= t) return;
            int ntex = tileTex1[nz * t + nx];
            if (ntex == self) return;
            // 只在靠邻边的半格内混合,避免整 tile 被邻图染透。
            float w = along * 2f - 1f;
            if (w <= 0f) return;
            w = w * w * (3f - 2f * w);
            SampleLayer(layers[ntex], layerSizes[ntex], uvMat[ntex], wx, wz, scratch);
            rgb[0] = (byte)(rgb[0] + (scratch[0] - rgb[0]) * w + 0.5f);
            rgb[1] = (byte)(rgb[1] + (scratch[1] - rgb[1]) * w + 0.5f);
            rgb[2] = (byte)(rgb[2] + (scratch[2] - rgb[2]) * w + 0.5f);
        }
    }

    /// <summary>把任意 Godot 图像格式收成 ShapeSize² 亮度。灰度 PNG 被扩成 RGBA 时亮度可能在
    /// A 而非 RGB——只 Convert(L8) 会得到全黑。两边均值谁更像"有内容的掩码"用谁。</summary>
    private static byte[] ExtractLuminance(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        byte[] src = img.GetData();
        int n = w * h;
        if (src.Length == n)
            return src;
        if (n == 0 || src.Length % n != 0)
        {
            img.Convert(Image.Format.L8);
            return img.GetData();
        }
        int stride = src.Length / n;
        var rgb = new byte[n];
        var alpha = new byte[n];
        long rgbSum = 0, aSum = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * stride;
            byte r = src[o];
            rgb[i] = r;
            rgbSum += r;
            if (stride >= 4)
            {
                alpha[i] = src[o + 3];
                aSum += src[o + 3];
            }
        }
        if (stride >= 4 && aSum > rgbSum * 4 && rgbSum < 8L * n)
            return alpha;
        return rgb;
    }

    // ── CAlphaMapCalculator 移植(查找表 + 匹配)──

    // BlendShape4/8 操作(逐位复制 renderer/BlendShapes.h 的索引置换)。
    private static void Rotate90(int[] s, int[] d)
    { d[0] = s[6]; d[1] = s[7]; d[2] = s[0]; d[3] = s[1]; d[4] = s[2]; d[5] = s[3]; d[6] = s[4]; d[7] = s[5]; }
    private static void Rotate180(int[] s, int[] d)
    { d[0] = s[4]; d[1] = s[5]; d[2] = s[6]; d[3] = s[7]; d[4] = s[0]; d[5] = s[1]; d[6] = s[2]; d[7] = s[3]; }
    private static void Rotate270(int[] s, int[] d)
    { d[0] = s[2]; d[1] = s[3]; d[2] = s[4]; d[3] = s[5]; d[4] = s[6]; d[5] = s[7]; d[6] = s[0]; d[7] = s[1]; }
    private static void FlipU(int[] s, int[] d)
    { d[0] = s[4]; d[1] = s[3]; d[2] = s[2]; d[3] = s[1]; d[4] = s[0]; d[5] = s[7]; d[6] = s[6]; d[7] = s[5]; }
    private static void FlipV(int[] s, int[] d)
    { d[0] = s[0]; d[1] = s[7]; d[2] = s[6]; d[3] = s[5]; d[4] = s[4]; d[5] = s[3]; d[6] = s[2]; d[7] = s[1]; }

    private static void Rotate90_4(int[] s, int[] d) { d[0] = s[3]; d[1] = s[0]; d[2] = s[1]; d[3] = s[2]; }
    private static void Rotate180_4(int[] s, int[] d) { d[0] = s[2]; d[1] = s[3]; d[2] = s[0]; d[3] = s[1]; }
    private static void Rotate270_4(int[] s, int[] d) { d[0] = s[1]; d[1] = s[2]; d[2] = s[3]; d[3] = s[0]; }
    private static void FlipU_4(int[] s, int[] d) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3]; }
    private static void FlipV_4(int[] s, int[] d) { d[0] = s[0]; d[1] = s[3]; d[2] = s[2]; d[3] = s[1]; }

    private static bool Eq(int[] a, int[] b, int len)
    {
        for (int i = 0; i < len; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // 表(shape → 形状图索引);shape8 位序 = BlendOffsets 位序(N,NW,W,SW,S,SE,E,NE)。
    private static readonly (int[] shape, int map)[] Blends1 = { (new[] { 1, 0, 0, 0 }, 12) };
    private static readonly (int[] shape, int map)[] Blends2 =
    {
        (new[] { 0, 1, 1, 0 }, 7), (new[] { 1, 0, 1, 0 }, 10),
    };
    private static readonly (int[] shape, int map)[] Blends2_8 =
    {
        (new[] { 1, 1, 0, 0, 0, 0, 0, 0 }, 12), (new[] { 1, 0, 0, 0, 0, 1, 0, 0 }, 12),
        (new[] { 0, 1, 0, 1, 0, 0, 0, 0 }, 0), (new[] { 0, 1, 0, 0, 0, 1, 0, 0 }, 0),
    };
    private static readonly (int[] shape, int map)[] Blends3 = { (new[] { 1, 1, 1, 0 }, 4) };
    private static readonly (int[] shape, int map)[] Blends3_8 =
    {
        (new[] { 1, 1, 0, 0, 1, 0, 0, 0 }, 10), (new[] { 1, 1, 0, 0, 0, 0, 0, 1 }, 12),
        (new[] { 1, 1, 1, 0, 0, 0, 0, 0 }, 1), (new[] { 0, 1, 1, 0, 1, 0, 0, 0 }, 7),
        (new[] { 0, 0, 1, 0, 1, 0, 1, 0 }, 4), (new[] { 1, 1, 0, 0, 0, 1, 0, 0 }, 12),
        (new[] { 1, 1, 0, 1, 0, 0, 0, 0 }, 12), (new[] { 0, 0, 1, 0, 1, 0, 0, 1 }, 7),
        (new[] { 1, 0, 0, 1, 0, 1, 0, 0 }, 12), (new[] { 0, 1, 0, 1, 0, 1, 0, 0 }, 0),
    };
    private static readonly (int[] shape, int map)[] Blends4_8 =
    {
        (new[] { 1, 1, 0, 0, 1, 0, 0, 1 }, 10), (new[] { 1, 1, 0, 1, 1, 0, 0, 0 }, 10),
        (new[] { 1, 1, 0, 0, 1, 1, 0, 0 }, 10), (new[] { 1, 1, 0, 1, 0, 0, 0, 1 }, 12),
        (new[] { 0, 1, 1, 0, 1, 1, 0, 0 }, 7), (new[] { 1, 1, 1, 1, 0, 0, 0, 0 }, 1),
        (new[] { 1, 1, 1, 0, 1, 0, 0, 0 }, 3), (new[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 7),
        (new[] { 1, 0, 1, 0, 1, 1, 0, 0 }, 4), (new[] { 1, 1, 1, 0, 0, 1, 0, 0 }, 1),
        (new[] { 1, 1, 0, 1, 0, 1, 0, 0 }, 12), (new[] { 0, 1, 0, 1, 0, 1, 0, 1 }, 0),
    };
    private static readonly (int[] shape, int map)[] Blends5_8 =
    {
        (new[] { 1, 1, 1, 1, 1, 0, 0, 0 }, 2), (new[] { 1, 1, 1, 1, 0, 0, 0, 1 }, 1),
        (new[] { 1, 1, 1, 0, 1, 0, 0, 1 }, 3), (new[] { 1, 1, 1, 0, 1, 0, 1, 0 }, 11),
        (new[] { 1, 1, 1, 0, 0, 1, 0, 1 }, 1), (new[] { 1, 1, 0, 1, 1, 1, 0, 0 }, 10),
        (new[] { 1, 1, 1, 0, 1, 1, 0, 0 }, 3), (new[] { 1, 0, 1, 0, 1, 1, 0, 1 }, 4),
        (new[] { 1, 1, 0, 1, 0, 1, 0, 1 }, 12), (new[] { 0, 1, 1, 0, 1, 1, 0, 1 }, 7),
    };
    private static readonly (int[] shape, int map)[] Blends6_8 =
    {
        (new[] { 1, 1, 1, 1, 1, 1, 0, 0 }, 2), (new[] { 1, 1, 1, 1, 1, 0, 1, 0 }, 8),
        (new[] { 1, 1, 1, 1, 0, 1, 0, 1 }, 1), (new[] { 1, 1, 1, 0, 1, 1, 1, 0 }, 6),
        (new[] { 1, 1, 1, 0, 1, 1, 0, 1 }, 3), (new[] { 1, 1, 0, 1, 1, 1, 0, 1 }, 10),
    };
    private static readonly (int[] shape, int map)[] Blends7_8 =
    {
        (new[] { 1, 1, 1, 1, 1, 1, 0, 1 }, 2), (new[] { 1, 1, 1, 1, 1, 1, 1, 0 }, 9),
    };

    private static bool MatchFlipped(int[] tmpl, int[] shape, bool eight, ref uint flags)
    {
        if (Eq(tmpl, shape, eight ? 8 : 4)) return true;
        var tst = new int[8];
        if (eight) FlipU(tmpl, tst); else FlipU_4(tmpl, tst);
        if (Eq(tst, shape, eight ? 8 : 4)) { flags |= FlagFlipU; return true; }
        if (eight) FlipV(tmpl, tst); else FlipV_4(tmpl, tst);
        if (Eq(tst, shape, eight ? 8 : 4)) { flags |= FlagFlipV; return true; }
        return false;
    }

    private static bool MatchShape(int[] tmpl, int[] shape, bool eight, ref uint flags)
    {
        if (MatchFlipped(tmpl, shape, eight, ref flags)) return true;
        var tst = new int[8];
        if (eight) Rotate90(tmpl, tst); else Rotate90_4(tmpl, tst);
        if (MatchFlipped(tst, shape, eight, ref flags))
        { flags |= flags != 0 ? FlagRot270 : FlagRot90; return true; }
        if (eight) Rotate180(tmpl, tst); else Rotate180_4(tmpl, tst);
        if (MatchFlipped(tst, shape, eight, ref flags)) { flags |= FlagRot180; return true; }
        if (eight) Rotate270(tmpl, tst); else Rotate270_4(tmpl, tst);
        if (MatchFlipped(tst, shape, eight, ref flags))
        { flags |= flags != 0 ? FlagRot90 : FlagRot270; return true; }
        return false;
    }

    private static int LookupBlend((int[] shape, int map)[] table, int[] shape, bool eight, ref uint flags)
    {
        foreach (var (tmpl, map) in table)
            if (MatchShape(tmpl, shape, eight, ref flags))
                return map;
        return 13;   // blendbad(不应到达;上游同款兜底)
    }

    /// <summary>Calculate 移植:8 邻居形状(1=缺席)→ 形状图索引 + 翻转/旋转旗标;
    /// -1 = 全邻居同贴图(无需形状图,整 tile 覆盖)。</summary>
    private static int Calculate(int[] shape, out uint flags)
    {
        flags = 0;
        int count = 0;
        for (int i = 0; i < 8; i++) count += shape[i];

        if (count == 0) return 0;          // blendcircle
        if (count == 8) return -1;         // 全覆盖

        if (count <= 4 && shape[1] == 0 && shape[3] == 0 && shape[5] == 0 && shape[7] == 0)
        {
            // 无对角 → 按 BlendShape4(N,W,S,E = shape[0,2,4,6])查 4 位表。
            var shape4 = new[] { shape[0], shape[2], shape[4], shape[6] };
            switch (count)
            {
                case 1: return LookupBlend(Blends1, shape4, false, ref flags);
                case 2: return LookupBlend(Blends2, shape4, false, ref flags);
                case 3: return LookupBlend(Blends3, shape4, false, ref flags);
                case 4: return 5;          // blendfourcorners
            }
        }

        return count switch
        {
            1 => 0,                        // 单对角邻居 → blendcircle
            2 => LookupBlend(Blends2_8, shape, true, ref flags),
            3 => LookupBlend(Blends3_8, shape, true, ref flags),
            4 => LookupBlend(Blends4_8, shape, true, ref flags),
            5 => LookupBlend(Blends5_8, shape, true, ref flags),
            6 => LookupBlend(Blends6_8, shape, true, ref flags),
            7 => LookupBlend(Blends7_8, shape, true, ref flags),
            _ => 13,
        };
    }

    /// <summary>按贴图自有 UV 矩阵采样一层(上游 m_TextureMatrix 语义):
    /// u = m11·wx + m13·wz,v = m21·wx + m23·wz,wrap 后双线性。size 是该层的原生边长
    /// (2 的幂,逐层各异——不再强制统一降到 512)。</summary>
    private static void SampleLayer(byte[] layer, int size, (float m11, float m13, float m21, float m23) m,
        float wx, float wz, byte[] outp)
    {
        float u = m.m11 * wx + m.m13 * wz;
        float v = m.m21 * wx + m.m23 * wz;
        u %= 1f; if (u < 0) u += 1f;
        v %= 1f; if (v < 0) v += 1f;
        Bilinear(layer, size, u * size, v * size, outp);
    }

    /// <summary>双线性采样 size² Rgba8 图层,写 RGB 三字节。采样点在 texel 中心系。</summary>
    private static void Bilinear(byte[] layer, int size, float u, float v, byte[] outp)
    {
        Bilinear(layer, size, u, v, outp, 0);
    }

    private static void Bilinear(byte[] layer, int size, float u, float v, byte[] outp, int o)
    {
        float fx = u - 0.5f, fy = v - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        int x1 = (x0 + 1) & (size - 1), y1 = (y0 + 1) & (size - 1);
        x0 &= size - 1; y0 &= size - 1;
        int i00 = (y0 * size + x0) * 4, i10 = (y0 * size + x1) * 4;
        int i01 = (y1 * size + x0) * 4, i11 = (y1 * size + x1) * 4;
        for (int c = 0; c < 3; c++)
        {
            float top = layer[i00 + c] + (layer[i10 + c] - layer[i00 + c]) * ax;
            float bot = layer[i01 + c] + (layer[i11 + c] - layer[i01 + c]) * ax;
            outp[o + c] = (byte)Math.Clamp(top + (bot - top) * ay + 0.5f, 0f, 255f);
        }
    }

    /// <summary>地形贴图 + 平铺属性(terrain XML 的 baseTex 与 <props size angle/>)。
    /// size 单位米(一次重复的世界跨度),angle 单位角度;缺省 32m/45°
    /// (上游 CTerrainProperties 构造缺省)。Size 是 Pixels 的原生边长(2 的幂)。</summary>
    private sealed class TerrainInfo
    {
        public byte[] Pixels = null!;
        public int Size;
        public float SizeMeters = 32f;
        public float AngleRad = MathF.PI / 4f;
    }

    private static readonly Dictionary<string, TerrainInfo> _infoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PMP 贴图名(如 "medit_rocks_grass")解析为图层+平铺属性。
    /// 名字=terrain XML basename。解析顺序(关键:types/ 下有 63 个跨 biome 同名
    /// basename——cliff_01/grass_01 等;早期管线把 types/<biome>/ 拍平成 terrain/
    /// 导致同名互相覆盖,山丘灰岩被换成努比亚红土):
    /// 1) XML baseTex 相对路径(types/temperate/cliff_01.png → terrain/types/… 结构化副本);
    /// 2) XML baseTex basename 平铺命中(DDS 转换物的旧平铺位置);
    /// 3) 无 XML → terrain/<PMP名>.png 直取;4) 缺失给中性草绿而非中止整张地形。</summary>
    private static TerrainInfo LoadTerrainInfo(string name)
    {
        if (_infoCache.TryGetValue(name, out var cached)) return cached;

        var info = new TerrainInfo();
        string texRoot = ProjectSettings.GlobalizePath("res://assets/textures/");
        string? pngPath = null;

        string? xmlPath = FindTerrainXml(name);
        if (xmlPath != null)
        {
            ParseTerrainXml(xmlPath, out string? baseTexPng, out float? size, out float? angleDeg,
                out string? baseTexRel);
            if (size is > 0) info.SizeMeters = size.Value;
            if (angleDeg.HasValue) info.AngleRad = angleDeg.Value * MathF.PI / 180f;
            // 1) 结构化副本(types/<biome>/<file>.png)
            if (baseTexRel != null)
            {
                string structured = Path.Combine(texRoot, "terrain",
                    baseTexRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(structured)) pngPath = structured;
            }
            // 2) 旧平铺位置(DDS 转换物尚未结构化前)
            if (pngPath == null && baseTexPng != null)
            {
                foreach (var candidate in Directory.EnumerateFiles(texRoot, baseTexPng, SearchOption.AllDirectories))
                {
                    pngPath = candidate;
                    break;
                }
            }
        }
        // 3) 无 XML(或 XML 解析失败)→ 按 PMP 名直取
        if (pngPath == null)
        {
            string direct = Path.Combine(texRoot, "terrain", name + ".png");
            if (File.Exists(direct)) pngPath = direct;
        }

        if (pngPath != null)
        {
            var img = Normalize(pngPath);
            info.Size = img.GetWidth();
            info.Pixels = img.GetData();
        }
        else
        {
            if (_warned.Add(name))
                ZeroAD.Sim.Diag.Warn("Terrain", $"SplatBaker: texture '{name}' not found; using placeholder");
            var fallback = Image.CreateEmpty(PlaceholderSize, PlaceholderSize, false, Image.Format.Rgba8);
            fallback.Fill(new Color(0.35f, 0.50f, 0.20f));
            info.Size = PlaceholderSize;
            info.Pixels = fallback.GetData();
        }

        _infoCache[name] = info;
        return info;
    }

    private static string? FindTerrainXml(string name)
    {
        string terrainsRoot = ProjectSettings.GlobalizePath("res://..")
            + "/binaries/data/mods/public/art/terrains";
        try
        {
            foreach (var xml in Directory.EnumerateFiles(terrainsRoot, name + ".xml", SearchOption.AllDirectories))
                return xml;
        }
        catch (Exception ex)
        {
            if (_warned.Add("xml:" + name))
                ZeroAD.Sim.Diag.Warn("Terrain", $"SplatBaker: terrain XML scan failed for '{name}': {ex.Message}");
        }
        return null;
    }

    /// <summary>读 terrain XML 的 baseTex(PNG 名 + types/ 相对路径)与 <props size angle/>。</summary>
    private static void ParseTerrainXml(string xmlPath, out string? baseTexPng,
        out float? size, out float? angleDeg, out string? baseTexRel)
    {
        baseTexPng = null; size = null; angleDeg = null; baseTexRel = null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(xmlPath);
            foreach (var tex in doc.Descendants("texture"))
            {
                if ((string?)tex.Attribute("name") != "baseTex") continue;
                string? file = (string?)tex.Attribute("file");
                if (!string.IsNullOrEmpty(file))
                {
                    baseTexRel = Path.GetDirectoryName(file)!.Length > 0
                        ? Path.GetDirectoryName(file)!.Replace('\\', '/') + "/"
                            + Path.GetFileNameWithoutExtension(file) + ".png"
                        : Path.GetFileNameWithoutExtension(file) + ".png";
                    baseTexPng = Path.GetFileNameWithoutExtension(file) + ".png";
                }
                break;
            }
            var props = doc.Root?.Element("props");
            if (props != null)
            {
                if (float.TryParse((string?)props.Attribute("size"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float sz))
                    size = sz;
                if (float.TryParse((string?)props.Attribute("angle"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float an))
                    angleDeg = an;
            }
        }
        catch (Exception ex)
        {
            if (_warned.Add("parse:" + xmlPath))
                ZeroAD.Sim.Diag.Warn("Terrain", $"SplatBaker: terrain XML parse failed '{xmlPath}': {ex.Message}");
        }
    }

    /// <summary>保留贴图原生边长(不再强制降到固定 512),只在语料不是"方形 + 2 的幂"
    /// 时兜底修正——SampleLayer/Bilinear 的 wrap 位掩码(&amp; (size-1))与正方形假设都
    /// 要求这一点;实测真实地形贴图全部已是 512/1024/2048 之一,兜底分支只处理误命中的
    /// 非方形/非 2 的幂杂项文件。</summary>
    private static Image Normalize(string pngPath)
    {
        var img = Image.LoadFromFile(pngPath);
        int w = img.GetWidth(), h = img.GetHeight();
        if (w != h || !IsPow2(w))
        {
            int size = Math.Clamp(NextPow2(Math.Min(w, h)), 64, 2048);
            img.Resize(size, size, Image.Interpolation.Bilinear);
        }
        if (img.GetFormat() != Image.Format.Rgba8)
            img.Convert(Image.Format.Rgba8);
        return img;
    }

    private static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;
}
