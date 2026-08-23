shader_type spatial;
render_mode cull_disabled, unshaded, blend_mix;

// 战争迷雾 + 领土边界 overlay(盖在烘焙地形上的一层透明 MIX)。
// 雾变暗 = 朝黑 alpha 混合:mix(dst, 黑, a) = dst×(1−a),与原 shader 内乘 bright 等价;
// 领土 = 玩家色 alpha 边界(对齐 C++ TerritoryTexture 的叠加语义:它是独立后期层,
// 不参与地形光照,所以放 overlay 反而比原来混在 albedo 里更忠实)。
// 网格与地形同一份(调用方复用 MeshInstance3D.Mesh,抬升 3cm 防 z-fighting);
// 本层不投/不收阴影(雾按 LOS 网格而非几何阴影,领土同理),unshaded 不需要法线。

uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform sampler2D territory_texture : filter_nearest, repeat_disable;
uniform vec4 player_colors[17];
uniform float territory_cells = 64.0;
uniform float world_size = 256.0;

void fragment() {
	vec2 world_xz = UV / 0.125;

	// 雾:unexplored → 纯黑(对齐 C++ 完全不可见),explored(0.5) → 50%,visible → 0。
	float fog = texture(fog_texture, world_xz / world_size).r;
	float bright = mix(0.0, 1.0, smoothstep(0.0, 1.0, fog));
	float a_fog = 1.0 - bright;

	// 领土:4m cell 网格,R = owner id,G = blink 标志;4 邻不同主即边界。
	vec3 tcol = vec3(0.0);
	float a_terr = 0.0;
	{
		// R = owner id,G = blink,B = 边界微光(CPU 烘焙:C++ TerritoryTexture
		// GenerateBitmap 语义——种子 192 + 32/格四向衰减,边界格留缝);
		// 细描边线由 TerritoryBorders 网格补(C++ TerritoryBoundary)。
		ivec2 tc = clamp(ivec2(floor(world_xz * 0.25)), ivec2(0), ivec2(int(territory_cells) - 1));
		vec4 terr = texelFetch(territory_texture, tc, 0);
		int towner = int(terr.r * 255.0 + 0.5);
		float glow = terr.b;
		if (towner > 0 && glow > 0.001) {
			float blink = terr.g > 0.5 ? 0.55 + 0.45 * sin(TIME * 4.0) : 1.0;
			tcol = player_colors[towner].rgb;
			a_terr = glow * 0.55 * blink * bright;
		}
	}

	// 顺序合成(先雾后领土)的单 pass 精确分解:
	//   out = mix(mix(dst, 黑, a_fog), tcol, a_terr) = dst×(1−A) + tcol×(a_terr/A)×A
	//   A = a_fog + a_terr×(1−a_fog)
	// 雾色为黑不贡献颜色,故 ALBEDO = tcol×(a_terr/A)、ALPHA = A。
	// (直接 ALPHA=max + ALBEDO=tcol 会让雾区领土格以领土色吃满雾 alpha 变成实心色带。)
	float a_total = a_fog + a_terr * (1.0 - a_fog);
	if (a_total < 0.003) discard;
	ALBEDO = tcol * (a_terr / a_total);
	ALPHA = a_total;
}
