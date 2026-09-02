shader_type spatial;
render_mode cull_disabled, blend_mix, depth_draw_opaque, specular_schlick_ggx, diffuse_burley;

// 水面(对齐 C++ TerrainRenderer fancy water 的关键语义,compatibility 可用的子集):
// 双水波法线图错帧流动(lake/ocean 序列帧,art/textures/animated/water/<type>/);
// fresnel 向天色过渡;岸线按场景深度差渐隐(hint_depth_texture);
// murkiness 越高越不透(深水色主导),waviness 决定波纹强度。
// 上游的屏幕反射/折射(compatibility 下 cost 高)未移植——v1 目标:水像水,不再是黑块。

uniform vec4 water_color : source_color = vec4(0.31, 0.25, 0.14, 1.0);
uniform vec4 water_tint : source_color = vec4(0.31, 0.25, 0.14, 1.0);
uniform float murkiness = 0.9;
uniform float waviness = 6.0;
uniform float wind_angle = 0.0;
uniform sampler2D normal_a : hint_normal, repeat_enable, filter_linear_mipmap;
uniform sampler2D normal_b : hint_normal, repeat_enable, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture;
// 画质档(原版 default.cfg:watereffects/waterrealdepth):
// waves_enabled=false → 平波纯底色(低机位兜底);depth_fade_enabled=false → 岸线不渐隐。
uniform bool waves_enabled = true;
uniform bool depth_fade_enabled = true;

void fragment() {
	vec2 wind = vec2(cos(wind_angle), sin(wind_angle));
	// 两层不同尺度的波反向流动(覆盖 UV 0..1 = mapSize*1.5;周期 ≈ 90 次/幅)。
	vec2 wuv = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz * 0.125;
	vec2 uv1 = wuv * 42.0 + wind * TIME * 0.028;
	vec2 uv2 = wuv * 59.0 - wind.yx * TIME * 0.021;
	vec3 n1 = texture(normal_a, uv1).rgb * 2.0 - 1.0;
	vec3 n2 = texture(normal_b, uv2).rgb * 2.0 - 1.0;
	vec3 wave = normalize(vec3(n1.xy + n2.xy, 2.0));   // 合并波扰动(切线空间)

	// 波纹强度:waviness 0..~12 → 0.15..1.2
	float wstr = clamp(waviness / 10.0, 0.15, 1.2);
	if (waves_enabled)
	{
		NORMAL_MAP = wave * 0.5 + 0.5;
		NORMAL_MAP_DEPTH = wstr;
	}
	else
	{
		// watereffects=false:纯底色平波(原版低画质等价——关法线扰动与波光)。
		NORMAL_MAP_DEPTH = 0.0;
	}

	// fresnel:视线越平越映天色
	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.5);
	vec3 sky = vec3(0.42, 0.58, 0.72);
	vec3 base = mix(water_color.rgb, sky, fres * 0.6);
	// 波纹高光微闪(波光)
	float sparkle = waves_enabled ? clamp(wave.x * wave.y * 6.0, 0.0, 1.0) * 0.10 * wstr : 0.0;
	base += sparkle;

	// 岸线渐隐:水面与场景的屏幕深度差 → 近岸透明
	float scene_depth = texture(depth_tex, SCREEN_UV).x;
	vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
	vec4 vpos = INV_PROJECTION_MATRIX * ndc;
	vpos.xyz /= vpos.w;
	float depth_diff = -(vpos.z + VERTEX.z);   // 场景深 − 水面深(米)
	float shore = depth_fade_enabled ? smoothstep(0.0, 2.2, depth_diff) : 1.0;

	ALBEDO = base;
	// murkiness 高 → 深水更不透明;岸线处最透 0.35
	ALPHA = mix(0.35, 0.55 + murkiness * 0.38, shore);
	ROUGHNESS = 0.22;
	SPECULAR = 0.65;
}
