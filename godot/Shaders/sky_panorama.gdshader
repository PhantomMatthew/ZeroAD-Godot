shader_type sky;

// 天空盒全景(原版 SkySet 5 面贴图)。原版 C++ SkyBox 无底面,地平线以下
// 漏清屏黑(图外虚空为黑);PanoramaSkyMaterial 会把贴图下半部裹到地平线
// 以下,拖到地图边缘露出亮色,故此处 EYEDIR.y < 0 强制输出黑。
// 地平线以上用 SKY_COORDS 采样,与 PanoramaSkyMaterial 的内建映射一致。

uniform sampler2D panorama : source_color, filter_linear_mipmap, repeat_enable;

void sky() {
	if (EYEDIR.y < 0.0) {
		COLOR = vec3(0.0);
	} else {
		COLOR = texture(panorama, SKY_COORDS).rgb;
	}
}
