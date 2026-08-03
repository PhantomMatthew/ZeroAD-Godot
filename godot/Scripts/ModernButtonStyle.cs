using System.IO;
using Godot;

namespace ZeroAD.Godot;

// ModernButtonRed 按钮样式端口(mods/mod/gui/common/modern/styles.xml L162 + sprites.xml L338/384/431):
//   normal   = red-unselected-{left,center,right}-{top,center,bottom}.png 九宫格(8px 边)
//   hover    = 同基底 + effect add_color 60 42 42(逐像素加色,同 StoneButtonStyle 的 FancyGlow)
//   pressed  = 同 normal(原版 sprite_pressed="ModernButtonRed")
//   disabled = 同基底 + effect grayscale(去饱和)
// 实现:九件拼成一张 144×32 图(左中右 8/128/8 × 上中下 8/16/8),StyleBoxTexture 设 8px 纹理边距
// 后 Godot 九宫格拉伸语义与原版逐件 placement 完全一致。
// 字体 sans-bold-stroke-14 白(黑描边),disabled 文字 210 210 210 160——与 StoneButtonStyle 一致。
// 贴图自 binaries data/mods/mod/art/textures/ui/global/modern/button/ 直读;缺失时不动调用方原样式。
public static class ModernButtonStyle
{
	private const int Border = 8;           // 原版边件 8×8 / 8×16 / 128×8,九宫格边宽 8
	private const int MidH = 16;            // 中件(8×16 / 128×16)高
	private const string DirRel = "data/mods/mod/art/textures/ui/global/modern/button";

	private static StyleBoxTexture? _normal, _hover, _pressed, _disabled;
	private static bool _tried;

	public static void Apply(Button btn, string? binariesDir)
	{
		Ensure(binariesDir);
		if (_normal == null || _hover == null || _pressed == null || _disabled == null)
			return;

		btn.AddThemeStyleboxOverride("normal", _normal);
		btn.AddThemeStyleboxOverride("hover", _hover);
		btn.AddThemeStyleboxOverride("pressed", _pressed);
		btn.AddThemeStyleboxOverride("disabled", _disabled);
		// focus 框用 normal 贴图,避免默认焦点描边盖住贴图。
		btn.AddThemeStyleboxOverride("focus", _normal);

		btn.AddThemeFontSizeOverride("font_size", 14);
		btn.AddThemeColorOverride("font_color", Colors.White);
		btn.AddThemeColorOverride("font_hover_color", Colors.White);
		btn.AddThemeColorOverride("font_pressed_color", Colors.White);
		btn.AddThemeColorOverride("font_disabled_color", new Color(210f / 255f, 210f / 255f, 210f / 255f, 160f / 255f));
		btn.AddThemeColorOverride("font_outline_color", Colors.Black);
		btn.AddThemeConstantOverride("outline_size", 4);
	}

	private static void Ensure(string? binariesDir)
	{
		if (_tried) return;
		_tried = true;
		if (binariesDir == null) return;
		string dir = Path.Combine(binariesDir, DirRel.Replace('/', Path.DirectorySeparatorChar));

		var normal = Compose(dir, add: (0, 0, 0), grayscale: false);
		var hover = Compose(dir, add: (60, 42, 42), grayscale: false);
		var disabled = Compose(dir, add: (0, 0, 0), grayscale: true);
		if (normal == null || disabled == null || hover == null) return;

		_normal = Box(normal);
		_hover = Box(hover);
		_pressed = Box(normal);   // 原版 sprite_pressed = ModernButtonRed(即 normal)
		_disabled = Box(disabled);
	}

	private static StyleBoxTexture Box(Image img)
	{
		var box = new StyleBoxTexture { Texture = ImageTexture.CreateFromImage(img) };
		// 8px 纹理边距 → 九宫格拉伸;内容边距小补,避免文字贴边。
		box.TextureMarginLeft = Border;
		box.TextureMarginRight = Border;
		box.TextureMarginTop = Border;
		box.TextureMarginBottom = Border;
		box.SetContentMarginAll(4);
		return box;
	}

	/// <summary>九件拼 144×32 单图 + 可选加色/灰度效果(原版 effect add_color / grayscale)。</summary>
	private static Image? Compose(string dir, (int r, int g, int b) add, bool grayscale)
	{
		const int w = 144, h = Border + MidH + Border;   // 144×32
		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);

		// (纹理名, 源区 w×h, 目标位置)——目标尺寸与源同,九宫格拉伸交给 StyleBoxTexture。
		(string file, int sw, int sh, int dx, int dy)[] parts =
		{
			("red-unselected-left-top.png",        Border, Border, 0, 0),
			("red-unselected-center-top.png",      128,    Border, Border, 0),
			("red-unselected-right-top.png",       Border, Border, w - Border, 0),
			("red-unselected-left-center.png",     Border, MidH,   0, Border),
			("red-unselected-center-center.png",   128,    MidH,   Border, Border),
			("red-unselected-right-center.png",    Border, MidH,   w - Border, Border),
			("red-unselected-left-bottom.png",     Border, Border, 0, h - Border),
			("red-unselected-center-bottom.png",   128,    Border, Border, h - Border),
			("red-unselected-right-bottom.png",    Border, Border, w - Border, h - Border),
		};
		foreach (var (file, sw, sh, dx, dy) in parts)
		{
			var tex = Image.LoadFromFile(Path.Combine(dir, file));
			if (tex == null) return null;
			// 资产尺寸可能小于名义 128×16(旧资产),钳到实际尺寸防 BlitRect 越界。
			int rw = Mathf.Min(sw, tex.GetWidth());
			int rh = Mathf.Min(sh, tex.GetHeight());
			img.BlitRect(tex, new Rect2I(0, 0, rw, rh), new Vector2I(dx, dy));
		}

		if (add.r > 0 || grayscale)
		{
			var dr = add.r / 255f;
			var dg = add.g / 255f;
			var db = add.b / 255f;
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					var c = img.GetPixel(x, y);
					if (grayscale)
					{
						// 原版 effect grayscale:Rec.601 亮度三通道回填。
						float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
						c.R = c.G = c.B = lum;
					}
					else
					{
						c.R = Mathf.Min(c.R + dr, 1f);
						c.G = Mathf.Min(c.G + dg, 1f);
						c.B = Mathf.Min(c.B + db, 1f);
					}
					img.SetPixel(x, y, c);
				}
			}
		}
		return img;
	}
}
