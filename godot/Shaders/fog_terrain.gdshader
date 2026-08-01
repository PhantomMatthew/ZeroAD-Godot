shader_type spatial;
render_mode cull_disabled;

// Terrain with fog-of-war darkening. The fog texture is the per-player LOS grid
// (R8: 0 unexplored, 0.5 explored, 1.0 visible, binomial-blurred) uploaded by
// FogWorldRenderer; world XZ is recovered from the terrain UV (both terrain
// builders emit UV = world * 0.125).
uniform sampler2D albedo_texture : source_color, repeat_enable;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform sampler2D territory_texture : filter_nearest, repeat_disable;
uniform vec4 player_colors[17];
uniform float territory_cells = 64.0;
uniform float world_size = 256.0;

void fragment() {
	// 镜像根下背面光栅化导致 NORMAL 取反(见 terrain_splat.gdshader 同注释),翻回。
	if (!FRONT_FACING) NORMAL = -NORMAL;

	vec3 base = texture(albedo_texture, UV).rgb * albedo_color.rgb;
	vec2 world_xz = UV / 0.125;
	float fog = texture(fog_texture, world_xz / world_size).r;
	// unexplored → near black, explored → ~55%, visible → full (0 A.D.-style).
	float bright = mix(0.12, 1.0, smoothstep(0.0, 1.0, fog));
	ALBEDO = base * bright;

	// Territory overlay(与 terrain_splat.gdshader 同逻辑):R=owner id,G=blink。
	{
		ivec2 tc = clamp(ivec2(floor(world_xz * 0.25)), ivec2(0), ivec2(int(territory_cells) - 1));
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
			float strength = border ? 0.55 : 0.12;
			ALBEDO = mix(ALBEDO, player_colors[towner].rgb, strength * blink);
		}
	}
}
