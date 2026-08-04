using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// 把 PMP 的逐 tile 贴图对(STileDesc tex1/tex2)在 CPU 侧烘成单张地形 albedo。
/// 动机:Compatibility 渲染器下自定义 spatial shader 完全收不到方向光阴影
/// (6 变体最小场景实证),而 splat 混合必须自定义 shader——故把混合结果烘焙,
/// 地形换 StandardMaterial3D(不透明,受影/光照由引擎标准管线来,与 C++
/// 固定管线 texel×(sun·N·L+ambient) 等价);雾/领土挪到 fog_territory_overlay。
///
/// 混合语义与原 terrain_splat.gdshader 逐位一致:weight 纹理在 tile 中心采样
/// (linear 于中心=texel 原值)→ 权重逐 tile 二值,blend tile 整格取 tex2,
/// 非 blend 整格取 tex1;贴图世界重复 0.25(每 4m tile 一次),双线性 + wrap。
/// 输出边长 = clamp(tiles×21 取 2 的幂, 2048, 8192)(192 tile 教程图 → 4096,
/// ≈5.3px/m);带 mipmap 链(远距离防闪烁)。
/// </summary>
public static class SplatBaker
{
	private const int LayerSize = 512;
	private const float TexWorldScale = 0.25f; // 与原 shader tex_world_scale 一致
	private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>烘焙整张地形 albedo(含 mipmap)。PMP 无贴图数据时返回 null(调用方走回退)。</summary>
	public static Image? BakeAlbedo(PmpMap map)
	{
		int texCount = map.TextureNames.Count;
		if (texCount == 0 || map.TileTex1.Length == 0)
		{
			GD.PushWarning("SplatBaker: no texture data in PMP; falling back to flat terrain");
			return null;
		}

		// 只解码真正被 tile 引用的图层(多数地图 < 全部声明)。
		// TileTex2 与 TileTex1 不等长(如 rmgen 适配器只填单层)时按"无 blend 层"处理。
		bool hasTex2 = map.TileTex2.Length == map.TileTex1.Length;
		var used = new bool[texCount];
		int t = map.TilesPerSide;
		for (int i = 0; i < map.TileTex1.Length; i++)
		{
			used[Math.Clamp(map.TileTex1[i], 0, texCount - 1)] = true;
			if (hasTex2 && map.TileTex2[i] != PmpMap.NoTexture)
				used[Math.Clamp(map.TileTex2[i], 0, texCount - 1)] = true;
		}
		var layers = new byte[texCount][];
		for (int i = 0; i < texCount; i++)
			if (used[i])
				layers[i] = LoadTerrainLayer(map.TextureNames[i]).GetData();

		// 目标分辨率:每 tile ≈21px(4096@192),取 2 的幂便于 mipmap。
		int px = 2048;
		while (px < t * 21 && px < 8192) px *= 2;

		// 每 tile 选定图层(权重二值:blend→tex2,否则 tex1)——逐 tile 一张选择表。
		var pick = new int[t * t];
		for (int i = 0; i < t * t; i++)
		{
			int a = Math.Clamp(map.TileTex1[i], 0, texCount - 1);
			bool blend = hasTex2 && map.TileTex2[i] != PmpMap.NoTexture;
			pick[i] = blend ? Math.Clamp(map.TileTex2[i], 0, texCount - 1) : a;
		}

		float mapSize = map.MapSizeMeters;
		float tileSize = PmpMap.TileSize;
		var outp = new byte[px * px * 3];
		var sw = System.Diagnostics.Stopwatch.StartNew();

		Parallel.For(0, px, y =>
		{
			float wz = (y + 0.5f) / px * mapSize;
			int tz = Math.Clamp((int)(wz / tileSize), 0, t - 1);
			float vz = wz * TexWorldScale % 1f; if (vz < 0) vz += 1f;
			int rowBytes = y * px * 3;
			for (int x = 0; x < px; x++)
			{
				float wx = (x + 0.5f) / px * mapSize;
				int tx = Math.Clamp((int)(wx / tileSize), 0, t - 1);
				byte[] layer = layers[pick[tz * t + tx]];
				float ux = wx * TexWorldScale % 1f; if (ux < 0) ux += 1f;
				Bilinear(layer, ux * LayerSize, vz * LayerSize, outp, rowBytes + x * 3);
			}
		});

		var img = Image.CreateFromData(px, px, false, Image.Format.Rgb8, outp);
		img.GenerateMipmaps();
		sw.Stop();
		GD.Print($"Terrain splat baked: {px}x{px} from {texCount} textures ({t}x{t} tiles) in {sw.ElapsedMilliseconds}ms");
		return img;
	}

	/// <summary>双线性采样 512² Rgba8 图层,写 RGB 三字节。采样点在 texel 中心系。</summary>
	private static void Bilinear(byte[] layer, float u, float v, byte[] outp, int o)
	{
		float fx = u - 0.5f, fy = v - 0.5f;
		int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
		float ax = fx - x0, ay = fy - y0;
		int x1 = (x0 + 1) & (LayerSize - 1), y1 = (y0 + 1) & (LayerSize - 1);
		x0 &= LayerSize - 1; y0 &= LayerSize - 1;
		int i00 = (y0 * LayerSize + x0) * 4, i10 = (y0 * LayerSize + x1) * 4;
		int i01 = (y1 * LayerSize + x0) * 4, i11 = (y1 * LayerSize + x1) * 4;
		for (int c = 0; c < 3; c++)
		{
			float top = layer[i00 + c] + (layer[i10 + c] - layer[i00 + c]) * ax;
			float bot = layer[i01 + c] + (layer[i11 + c] - layer[i01 + c]) * ax;
			outp[o + c] = (byte)Math.Clamp(top + (bot - top) * ay + 0.5f, 0f, 255f);
		}
	}

	/// <summary>PMP 贴图名(如 "medit_rocks_grass")解析为 512² Rgba8 图。
	/// 名字=terrain XML basename=其 baseTex 文件名,直取几乎必中;art/terrains XML
	/// 扫描兜底改名情况;缺失给中性草绿而非中止整张地形。</summary>
	private static Image LoadTerrainLayer(string name)
	{
		string texRoot = ProjectSettings.GlobalizePath("res://assets/textures/");
		string direct = Path.Combine(texRoot, "terrain", name + ".png");
		if (File.Exists(direct))
			return Normalize(direct);

		string? viaXml = ResolveViaTerrainXml(name, texRoot);
		if (viaXml != null)
			return Normalize(viaXml);

		if (_warned.Add(name))
			GD.PushWarning($"SplatBaker: texture '{name}' not found; using placeholder");
		var fallback = Image.CreateEmpty(LayerSize, LayerSize, false, Image.Format.Rgba8);
		fallback.Fill(new Color(0.35f, 0.50f, 0.20f));
		return fallback;
	}

	private static Image Normalize(string pngPath)
	{
		var img = Image.LoadFromFile(pngPath);
		if (img.GetWidth() != LayerSize || img.GetHeight() != LayerSize)
			img.Resize(LayerSize, LayerSize, Image.Interpolation.Bilinear);
		if (img.GetFormat() != Image.Format.Rgba8)
			img.Convert(Image.Format.Rgba8);
		return img;
	}

	/// <summary>扫 art/terrains/**/&lt;name&gt;.xml 的 baseTex,再在 assets/textures 下找同名 PNG。</summary>
	private static string? ResolveViaTerrainXml(string name, string texRoot)
	{
		string terrainsRoot = ProjectSettings.GlobalizePath("res://..")
			+ "/binaries/data/mods/public/art/terrains";
		try
		{
			foreach (var xml in Directory.EnumerateFiles(terrainsRoot, name + ".xml", SearchOption.AllDirectories))
			{
				var doc = System.Xml.Linq.XDocument.Load(xml);
				foreach (var tex in doc.Descendants("texture"))
				{
					if ((string?)tex.Attribute("name") != "baseTex") continue;
					string? file = (string?)tex.Attribute("file");
					if (string.IsNullOrEmpty(file)) continue;
					string pngName = Path.GetFileNameWithoutExtension(file) + ".png";
					foreach (var candidate in Directory.EnumerateFiles(texRoot, pngName, SearchOption.AllDirectories))
						return candidate;
				}
			}
		}
		catch (Exception ex)
		{
			if (_warned.Add("xml:" + name))
				GD.PushWarning($"SplatBaker: terrain XML scan failed for '{name}': {ex.Message}");
		}
		return null;
	}
}
