shader_type spatial;
render_mode unshaded, cull_disabled;

// Magma and slime show their texture and nothing through it. So unlike water they draw with what's
// solid, and whatever is transparent, like goo dripping into them, is sorted against them by depth.

// The liquid's animation frames.
uniform sampler2DArray frames : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float frame_count = 1.0;
uniform float frames_per_second = 15.0;
// Lit by the sky's ambient.
uniform vec3 texture_light : source_color = vec3(1.0);
// Scrolls the texture, in repeats per second.
uniform vec2 flow = vec2(0.0);

void fragment() {
	float frame = mod(floor(TIME * frames_per_second), max(frame_count, 1.0));
	ALBEDO = texture(frames, vec3(UV + flow * TIME, frame)).rgb * texture_light;
}
