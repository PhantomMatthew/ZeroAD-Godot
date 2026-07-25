shader_type spatial;
render_mode cull_disabled;

// Terrain with fog-of-war darkening. The fog texture is the per-player LOS grid
// (R8: 0 unexplored, 0.5 explored, 1.0 visible, binomial-blurred) uploaded by
// FogWorldRenderer; world XZ is recovered from the terrain UV (both terrain
// builders emit UV = world * 0.125).
uniform sampler2D albedo_texture : source_color, repeat_enable;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D fog_texture : filter_linear, repeat_disable;
uniform float world_size = 256.0;

void fragment() {
	vec3 base = texture(albedo_texture, UV).rgb * albedo_color.rgb;
	vec2 world_xz = UV / 0.125;
	float fog = texture(fog_texture, world_xz / world_size).r;
	// unexplored → near black, explored → ~55%, visible → full (0 A.D.-style).
	float bright = mix(0.12, 1.0, smoothstep(0.0, 1.0, fog));
	ALBEDO = base * bright;
}
