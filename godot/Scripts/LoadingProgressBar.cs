using System.IO;
using Godot;

namespace ZeroAD.Godot;

// 加载页进度条(忠实端口 gui/loading/ProgressBar.xml + sprites.xml):
// 外框 3 片(background left 64px / middle 拉伸 / right 64px);内嵌条 56 5 456 100%-5(400×22),
// 填充 = progressbar_middle 的 1×20 竖条(real_texture_placement 0 6 1 26)横向拉伸到 value%;
// 左端两枚 16px 端帽静态装饰(左帽 -8..8 取源 16 6 32 26,右帽 8..24 取源 0 6 16 26);
// 百分比文本居中(LoadingBarText:sans-bold-stroke-14 白)。
// 贴图自 binaries art/textures/ui/loading/progressbar/ 直读;缺失时回退纯色条。
public sealed partial class LoadingProgressBar : Control
{
	private const float InsetX = 56f, InsetY = 5f;

	private Texture2D? _bgL, _bgM, _bgR, _fill, _capL, _capR;
	private float _fraction;
	private readonly Label _pct;

	public LoadingProgressBar()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		_pct = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_pct.SetAnchorsPreset(LayoutPreset.FullRect);
		_pct.AddThemeFontSizeOverride("font_size", 14);
		_pct.AddThemeColorOverride("font_color", Colors.White);
		_pct.AddThemeColorOverride("font_outline_color", Colors.Black);
		_pct.AddThemeConstantOverride("outline_size", 4);
		AddChild(_pct);
		SetProgress(0f);
	}

	/// <summary>加载 6 张贴图;关键贴图缺失返回 false(调用方决定回退)。</summary>
	public bool Init(string? binariesDir)
	{
		if (binariesDir == null) return false;
		string dir = Path.Combine(binariesDir,
			"data", "mods", "public", "art", "textures", "ui", "loading", "progressbar");
		_bgL = Load(dir, "progressbar_background_left.png");
		_bgM = Load(dir, "progressbar_background_middle.png");
		_bgR = Load(dir, "progressbar_background_right.png");
		_fill = Load(dir, "progressbar_middle.png");
		_capL = Load(dir, "progressbar_left.png");
		_capR = Load(dir, "progressbar_right.png");
		return _bgM != null && _fill != null;
	}

	private static Texture2D? Load(string dir, string file)
	{
		var img = Image.LoadFromFile(Path.Combine(dir, file));
		return img == null ? null : ImageTexture.CreateFromImage(img);
	}

	public void SetProgress(float fraction)
	{
		_fraction = Mathf.Clamp(fraction, 0f, 1f);
		_pct.Text = $"{Mathf.RoundToInt(_fraction * 100)}%";
		QueueRedraw();
	}

	public override void _Draw()
	{
		float w = Size.X, h = Size.Y;
		if (w <= 0 || h <= 0) return;

		if (_bgM == null || _fill == null)
		{
			// 回退:纯色底 + 金填充(贴图缺失时仍可读)。
			DrawRect(new Rect2(0, 0, w, h), new Color(0.12f, 0.11f, 0.09f));
			float fw = (w - InsetX * 2) * _fraction;
			if (fw > 0)
				DrawRect(new Rect2(InsetX, InsetY, fw, h - InsetY * 2), new Color(0.85f, 0.70f, 0.35f));
			return;
		}

		// 外框 3 片(左 64 / 中拉伸 / 右 64)。
		if (_bgL != null) DrawTextureRect(_bgL, new Rect2(0, 0, 64, h), false);
		DrawTextureRect(_bgM, new Rect2(64, 0, w - 128, h), false);
		if (_bgR != null) DrawTextureRect(_bgR, new Rect2(w - 64, 0, 64, h), false);

		// 填充:1×20 竖条横向拉伸至 value%。
		float barW = w - InsetX * 2, barH = h - InsetY * 2;
		float fillW = barW * _fraction;
		if (fillW > 0)
			DrawTextureRectRegion(_fill, new Rect2(InsetX, InsetY, fillW, barH), new Rect2(0, 6, 1, 20));

		// 左端端帽(静态):左帽 x -8..8,右帽 x 8..24。
		if (_capL != null)
			DrawTextureRectRegion(_capL, new Rect2(InsetX - 8, InsetY, 16, barH), new Rect2(16, 6, 16, 20));
		if (_capR != null)
			DrawTextureRectRegion(_capR, new Rect2(InsetX + 8, InsetY, 16, barH), new Rect2(0, 6, 16, 20));
	}
}
