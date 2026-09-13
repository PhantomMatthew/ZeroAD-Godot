shader_type sky;

// 原版 SkyManager cubemap:6 面都采样(底面复用 top 贴图)。
// 不要把 EYEDIR.y<0 涂黑——C++ 侧壁一直延伸到地平线以下,地图边缘看到的是天空不是虚空。

uniform samplerCube source_panorama : filter_linear, source_color, hint_default_black;

void sky() {
	COLOR = texture(source_panorama, EYEDIR).rgb;
}
