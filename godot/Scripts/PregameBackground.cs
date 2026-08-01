using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

// 主菜单视差背景(忠实端口 gui/pregame/backgrounds/background.js + BackgroundHandler.js):
// 启动时随机选一套(carthage/hellenes/kush/seleucid),每套 2-4 层全高图片,
// 每层以低频余弦水平漂移(视差);tiling 层以 2h×h 平铺并按 iw=2h 回绕,
// 非 tiling 层拉伸到 2h 宽居中(或 halign=right 时 w16=h*16/9 靠右)。
// 贴图自 binaries/.../art/textures/ui/pregame/backgrounds/ 直接加载(不导入 res://)。
public sealed partial class PregameBackground : Control
{
	private sealed class LayerDef
	{
		public required string File;
		public float K, F;       // offset = K*w16*cos(F*t) + Extra*w16
		public float Extra;
		public float K2, F2;     // kush1_3 的第二余弦项
		public bool Tiling;
		public bool HRight;      // halign="right"(kush1_4)
		public float ConstK;     // 非常量层为 float.NaN;kush1_4 = -0.1
	}

	// 每套 = background.js 的一个数组;贴图文件名来自 backgrounds/*.xml 的 sprite 定义。
	private static readonly LayerDef[][] Sets =
	{
		new LayerDef[] // carthage
		{
			new() { File = "carthage1_1.png", K = 0.02f, F = 0.05f, Tiling = true, ConstK = float.NaN },
			new() { File = "carthage1_2.png", K = 0.04f, F = 0.05f, Tiling = true, ConstK = float.NaN },
			new() { File = "carthage1_3.png", K = 0.10f, F = 0.05f, ConstK = float.NaN },
			new() { File = "carthage1_4.png", K = 0.18f, F = 0.05f, ConstK = float.NaN },
		},
		new LayerDef[] // hellenes
		{
			new() { File = "hellenes1-1.png", K = 0.02f, F = 0.05f, Tiling = true, ConstK = float.NaN },
			new() { File = "hellenes1-2.png", K = 0.12f, F = 0.05f, Extra = -0.1f, ConstK = float.NaN },
			new() { File = "hellenes1-3.png", K = 0.16f, F = 0.05f, Extra = 0.25f, ConstK = float.NaN },
		},
		new LayerDef[] // kush
		{
			new() { File = "kush1_1.png", K = 0.07f, F = 0.1f, Tiling = true, ConstK = float.NaN },
			new() { File = "kush1_2.png", K = 0.05f, F = 0.1f, Tiling = true, ConstK = float.NaN },
			new() { File = "kush1_3.png", K = 0.04f, F = 0.1f, K2 = 0.01f, F2 = 0.04f, Tiling = true, ConstK = float.NaN },
			new() { File = "kush1_4.png", HRight = true, ConstK = -0.1f },
		},
		new LayerDef[] // seleucid
		{
			new() { File = "seleucid1_1.png", K = 0.05f, F = 0.02f, Tiling = true, ConstK = float.NaN },
			new() { File = "seleucid1_2.png", K = 0.10f, F = 0.04f, Tiling = true, ConstK = float.NaN },
			new() { File = "seleucid1_3.png", K = 0.17f, F = 0.05f, Extra = 0.125f, ConstK = float.NaN },
		},
	};

	private readonly List<(LayerDef Def, Texture2D? Tex)> _layers = new();
	private ulong _initMs;

	public PregameBackground()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsPreset(LayoutPreset.FullRect);
	}

	/// <summary>随机选一套并加载贴图;binariesDir 缺失则保持空(调用方回退渐变)。</summary>
	public bool Init(string? binariesDir)
	{
		if (binariesDir == null) return false;
		string dir = Path.Combine(binariesDir,
			"data", "mods", "public", "art", "textures", "ui", "pregame", "backgrounds");
		if (!Directory.Exists(dir)) return false;

		var rng = new RandomNumberGenerator();
		rng.Randomize();
		var set = Sets[(int)(rng.Randi() % Sets.Length)];
		foreach (var def in set)
		{
			Texture2D? tex = null;
			var img = Image.LoadFromFile(Path.Combine(dir, def.File));
			if (img != null) tex = ImageTexture.CreateFromImage(img);
			_layers.Add((def, tex));
		}
		_initMs = Time.GetTicksMsec();
		return true;
	}

	public override void _Process(double delta)
	{
		if (_layers.Count > 0) QueueRedraw();
	}

	public override void _Draw()
	{
		float h = Size.Y, screenW = Size.X;
		if (h <= 0 || screenW <= 0) return;
		double t = (Time.GetTicksMsec() - _initMs) / 1000.0;
		float w16 = h * 16f / 9f; // JS: BackgroundLayer.prototype.AspectRatio = 16/9

		foreach (var (def, tex) in _layers)
		{
			if (tex == null) continue;
			float offset = float.IsNaN(def.ConstK)
				? (def.K * (float)Math.Cos(def.F * t)
					+ (def.K2 != 0f ? def.K2 * (float)Math.Cos(def.F2 * t) : 0f)
					+ def.Extra) * w16
				: def.ConstK * w16;

			if (def.Tiling)
			{
				// JS: iw=2h,left=offset%iw 归到 (-iw,0],对象右缘=屏右,2h×h 平铺。
				float iw = 2f * h;
				float left = offset % iw;
				if (left >= 0) left -= iw;
				var tileSize = new Vector2(iw, h);
				for (float x = left; x < screenW; x += iw)
					DrawTextureRect(tex, new Rect2(x, 0, tileSize), false);
			}
			else if (def.HRight)
			{
				float left = screenW - w16 + offset;
				DrawTextureRect(tex, new Rect2(left, 0, w16, h), false);
			}
			else
			{
				// JS: right = 屏中+offset,对象 = right±h(宽 2h,拉伸)。
				float cx = screenW / 2f + offset;
				DrawTextureRect(tex, new Rect2(cx - h, 0, 2f * h, h), false);
			}
		}
	}
}
