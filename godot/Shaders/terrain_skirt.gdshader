shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;

// 图缘裙边(原版 PatchRData.BuildSides):TerrainRenderer.cpp 用 str_solid
// 效果直写 uniform 纯黑(0,0,0,1),无光照也无雾。StandardMaterial3D 在
// Godot 4 无法关雾,雾图会把裙边刷成雾色(地图边缘露灰白),故用此 shader。

void fragment() {
	ALBEDO = vec3(0.0);
}
