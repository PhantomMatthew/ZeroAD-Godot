shader_type sky;

// 全景兜底(只有 front 贴图时)。必须全方向采样:
// 原版立方体贴图侧壁覆盖地平线以下,地图边缘是天空色。
// 此前 EYEDIR.y<0 输出黑,RTS 相机看图缘时半屏虚空。

uniform sampler2D panorama : source_color, filter_linear_mipmap, repeat_enable;

void sky() {
	COLOR = texture(panorama, SKY_COORDS).rgb;
}
