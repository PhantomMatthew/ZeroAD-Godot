using System.IO;
using Godot;

namespace ZeroAD.Godot;

// StoneButtonFancy 按钮样式端口(common/styles.xml L74 + common/sprites.xml):
//   normal   = button_stone_unselected.png(0 0 256 28 拉伸铺满)
//   hover    = button_stone_selected.png 基底 + 左右各 32×28 trim 合成(FancyOver)
//   pressed  = hover 合成 + add_color 60 42 42(FancyGlow,逐像素加色)
//   disabled = unselected + add_color 42 42 42
// 字体 sans-bold-stroke-14 白(黑描边),disabled 文字 210 210 210 160。
// 贴图自 binaries art/textures/ui/global/button/ 直读;缺失时不动调用方原样式(回退)。
public static class StoneButtonStyle
{
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
		string dir = Path.Combine(binariesDir,
			"data", "mods", "public", "art", "textures", "ui", "global", "button");

		var normal = Compose(dir, "button_stone_unselected.png", trims: false, add: (0, 0, 0));
		var hover = Compose(dir, "button_stone_selected.png", trims: true, add: (0, 0, 0));
		var pressed = Compose(dir, "button_stone_selected.png", trims: true, add: (60, 42, 42));
		var disabled = Compose(dir, "button_stone_unselected.png", trims: false, add: (42, 42, 42));
		if (normal == null || hover == null || pressed == null || disabled == null) return;

		_normal = Box(normal);
		_hover = Box(hover);
		_pressed = Box(pressed);
		_disabled = Box(disabled);
	}

	private static StyleBoxTexture Box(Image img)
	{
		var box = new StyleBoxTexture { Texture = ImageTexture.CreateFromImage(img) };
		box.SetContentMarginAll(2);
		return box;
	}

	/// <summary>基底(0 0 256 28)+ 可选左右 trim 合成 + 可选逐像素加色(add_color 效果)。</summary>
	private static Image? Compose(string dir, string baseFile, bool trims, (int r, int g, int b) add)
	{
		var img = Image.LoadFromFile(Path.Combine(dir, baseFile));
		if (img == null) return null;
		if (img.GetWidth() != 256 || img.GetHeight() != 28)
			img = img.GetRegion(new Rect2I(0, 0, 256, 28));
		else
			img = img.Duplicate() as Image;
		if (img == null) return null;

		if (trims)
		{
			var left = Image.LoadFromFile(Path.Combine(dir, "button_stone_selected_left_trim.png"));
			var right = Image.LoadFromFile(Path.Combine(dir, "button_stone_selected_right_trim.png"));
			if (left != null)
				img.BlitRect(Region32(left), new Rect2I(0, 0, 32, 28), new Vector2I(0, 0));
			if (right != null)
				img.BlitRect(Region32(right), new Rect2I(0, 0, 32, 28), new Vector2I(256 - 32, 0));
		}

		if (add.r > 0)
		{
			var dr = add.r / 255f;
			var dg = add.g / 255f;
			var db = add.b / 255f;
			for (int y = 0; y < img.GetHeight(); y++)
			{
				for (int x = 0; x < img.GetWidth(); x++)
				{
					var c = img.GetPixel(x, y);
					c.R = Mathf.Min(c.R + dr, 1f);
					c.G = Mathf.Min(c.G + dg, 1f);
					c.B = Mathf.Min(c.B + db, 1f);
					img.SetPixel(x, y, c);
				}
			}
		}
		return img;
	}

	private static Image Region32(Image img) =>
		img.GetWidth() != 32 || img.GetHeight() != 28 ? img.GetRegion(new Rect2I(0, 0, 32, 28)) : img;
}
