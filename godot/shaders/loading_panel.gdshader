shader_type spatial;

// Shows the panel's 2D page, with rounded corners and a thin edge of light.
render_mode unshaded, depth_test_disabled, cull_back, shadows_disabled, fog_disabled, blend_mix;

uniform sampler2D page : source_color, filter_linear;
uniform float fade = 1.0;
uniform vec2 size = vec2(1.0);
uniform float corner = 0.05;
uniform vec3 edge_color : source_color = vec3(0.55, 0.68, 0.95);
uniform float edge_width = 0.004;

void fragment() {
	vec2 point = (UV - 0.5) * size;
	vec2 inside = abs(point) - size * 0.5 + corner;
	float distance_out = length(max(inside, vec2(0.0))) + min(max(inside.x, inside.y), 0.0) - corner;
	float pixel = fwidth(distance_out);
	float shape = 1.0 - smoothstep(-pixel, 0.0, distance_out);
	float edge = smoothstep(-edge_width - pixel, -edge_width, distance_out);

	ALBEDO = mix(texture(page, UV).rgb, edge_color, edge * 0.6);
	ALPHA = shape * fade;
}
