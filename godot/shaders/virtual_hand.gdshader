// The controllers' hands, shown while the player has no body: see-through and lit from within,
// faint where they face the eyes and bright at their edges. Unshaded, so night and caves don't dim them.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_always, cull_back, shadows_disabled, fog_disabled;

uniform vec4 core_color : source_color = vec4(0.3, 0.65, 1.0, 0.18);
uniform vec4 edge_color : source_color = vec4(0.8, 0.95, 1.0, 0.95);
uniform float edge_sharpness : hint_range(0.5, 8.0) = 2.2;

void fragment() {
	float edge = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), edge_sharpness);
	vec4 color = mix(core_color, edge_color, edge);
	ALBEDO = color.rgb;
	ALPHA = color.a;
}
