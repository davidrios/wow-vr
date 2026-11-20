// A glow around the controllers' hands, without the world needing bloom: the hand again, a little
// larger, added over what's behind it and fading out toward the shell's edge.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_back, shadows_disabled, fog_disabled;

uniform vec4 glow_color : source_color = vec4(0.35, 0.7, 1.0, 1.0);
uniform float strength : hint_range(0.0, 2.0) = 0.6;

// How far the glow reaches past the skin, in meters at a world scale of 1.
uniform float reach = 0.006;

void vertex() {
	VERTEX += NORMAL * reach;
}

void fragment() {
	float facing = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	ALBEDO = glow_color.rgb * strength * facing * facing;
}
