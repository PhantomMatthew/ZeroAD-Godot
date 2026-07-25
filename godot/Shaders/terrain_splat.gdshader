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
uniform float world_size = 256.0;
uniform float tiles_per_side = 192.0;
// Terrain texture world repeat: 0.25 = one texture repeat per 4m tile.
uniform float tex_world_scale = 0.25;

void fragment() {
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
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
