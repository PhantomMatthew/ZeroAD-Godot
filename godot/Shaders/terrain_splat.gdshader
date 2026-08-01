shader_type spatial;
render_mode cull_disabled;

// Splat-textured terrain with fog-of-war darkening.
// Per tile the PMP stores a texture pair (STileDesc tex1/tex2); we mix the pair
// by the weight map. Indices are fetched per tile (texelFetch — never filtered),
// the weight is bilinear-sampled at tile centres so blends span ~1 tile, matching
// the original's per-patch blend gradients. Fog logic is identical to
// fog_terrain.gdshader: world XZ recovered from UV (terrain UV = world * 0.125).

uniform sampler2DArray albedo_array : source_color, repeat_enable;
uniform sampler2D idx_a : filter_nearest, repeat_disable;
uniform sampler2D idx_b : filter_nearest, repeat_disable;
uniform sampler2D weight_b : filter_linear, repeat_disable;
uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform sampler2D territory_texture : filter_nearest, repeat_disable;
uniform vec4 player_colors[17];
uniform float territory_cells = 64.0;
uniform float world_size = 256.0;
uniform float tiles_per_side = 192.0;
// Terrain texture world repeat: 0.25 = one texture repeat per 4m tile.
uniform float tex_world_scale = 0.25;

void fragment() {
	// 世界视觉挂在 Scale.z=−1 的镜像根下:绕序翻转使地形片元以背面光栅化,
	// 内建 NORMAL 随之取反(指 −Y)→ 太阳 N·L=0,地形只剩环境光(画面发暗发平的根因)。
	// 标准材质会自动按朝向修正,自定义 shader 必须自己翻回(最小场景法线可视化实证)。
	if (!FRONT_FACING) NORMAL = -NORMAL;

	vec2 world_xz = UV / 0.125;
	vec2 tile_f = world_xz * 0.25; // / TileSize(4m)
	ivec2 tile = ivec2(floor(tile_f));
	tile = clamp(tile, ivec2(0), ivec2(int(tiles_per_side) - 1));

	float ia = texelFetch(idx_a, tile, 0).r * 255.0;
	float ib = texelFetch(idx_b, tile, 0).r * 255.0;
	float w = texture(weight_b, (vec2(tile) + 0.5) / tiles_per_side).r;

	vec2 tuv = world_xz * tex_world_scale;
	vec3 a = texture(albedo_array, vec3(tuv, ia)).rgb;
	vec3 b = texture(albedo_array, vec3(tuv, ib)).rgb;
	vec3 base = mix(a, b, w);

	float fog = texture(fog_texture, world_xz / world_size).r;
	// unexplored → near black, explored → ~55%, visible → full (0 A.D.-style).
	float bright = mix(0.12, 1.0, smoothstep(0.0, 1.0, fog));
	ALBEDO = base * bright;

	// Territory overlay: 4m cell grid, R = owner id, G = blink flag. Border where a
	// 4-neighbour has a different owner; unconnected (blink) regions pulse with TIME.
	{
		ivec2 tc = clamp(ivec2(floor(tile_f)), ivec2(0), ivec2(int(territory_cells) - 1));
		vec4 terr = texelFetch(territory_texture, tc, 0);
		int towner = int(terr.r * 255.0 + 0.5);
		if (towner > 0) {
			ivec2 tmax = ivec2(int(territory_cells) - 1);
			bool border =
				int(texelFetch(territory_texture, clamp(tc + ivec2(1, 0), ivec2(0), tmax), 0).r * 255.0 + 0.5) != towner ||
				int(texelFetch(territory_texture, clamp(tc - ivec2(1, 0), ivec2(0), tmax), 0).r * 255.0 + 0.5) != towner ||
				int(texelFetch(territory_texture, clamp(tc + ivec2(0, 1), ivec2(0), tmax), 0).r * 255.0 + 0.5) != towner ||
				int(texelFetch(territory_texture, clamp(tc - ivec2(0, 1), ivec2(0), tmax), 0).r * 255.0 + 0.5) != towner;
			float blink = terr.g > 0.5 ? 0.55 + 0.45 * sin(TIME * 4.0) : 1.0;
			// 对齐 C++ TerritoryTexture:边界线 alpha 0.75 短衰减,内部无平铺染色
			// (原来的 0.12 内部染色让整片领地蒙上玩家色灰蓝罩,画面发暗)。
			float strength = border ? 0.55 : 0.0;
			ALBEDO = mix(ALBEDO, player_colors[towner].rgb, strength * blink);
		}
	}

	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
