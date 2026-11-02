shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

// The liquid's animation frames.
uniform sampler2DArray frames : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float frame_count = 1.0;
uniform float frames_per_second = 15.0;
// Magma and slime show their texture, lit by the sky's ambient. Water gets its look from the sky.
uniform bool textured = false;
uniform vec3 texture_light : source_color = vec3(1.0);
// Scrolls the texture, in repeats per second.
uniform vec2 flow = vec2(0.0);

// All set from the light tables as the time of day changes.
uniform vec3 shallow_color : source_color = vec3(0.1, 0.3, 0.3);
uniform vec3 deep_color : source_color = vec3(0.0, 0.1, 0.2);
uniform float shallow_alpha = 0.5;
uniform float deep_alpha = 1.0;
uniform vec3 sky_top : source_color = vec3(0.2, 0.4, 0.8);
uniform vec3 sky_horizon : source_color = vec3(0.7, 0.8, 0.9);
uniform vec3 sun_color : source_color = vec3(1.0);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);

// How much the texture's bumps tilt the surface.
const float RIPPLE_STRENGTH = 0.09;
const float SUN_SHARPNESS = 280.0;
// A surface seen straight on still reflects this much.
const float BASE_REFLECTION = 0.03;
// Even at a grazing angle it reflects no more than this, or distant water turns milky.
const float MAX_REFLECTION = 0.6;

varying vec3 world_position;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float frame = mod(floor(TIME * frames_per_second), max(frame_count, 1.0));
	vec2 uv = UV + flow * TIME;
	vec3 texel = texture(frames, vec3(uv, frame)).rgb;

	if (textured) {
		ALBEDO = texel * texture_light;
		ALPHA = 1.0;
	} else {
		// The frames are bump patterns, so their slope gives ripples.
		vec2 step_size = vec2(1.0 / 128.0);
		float height = texel.g;
		float east = texture(frames, vec3(uv + vec2(step_size.x, 0.0), frame)).g;
		float south = texture(frames, vec3(uv + vec2(0.0, step_size.y), frame)).g;
		vec3 normal = normalize(vec3((height - east) * RIPPLE_STRENGTH * 4.0, 1.0, (height - south) * RIPPLE_STRENGTH * 4.0));

		vec3 to_camera = normalize(CAMERA_POSITION_WORLD - world_position);
		// From underneath, the surface faces the other way.
		if (to_camera.y < 0.0) {
			normal.y = -normal.y;
		}

		float facing = clamp(dot(normal, to_camera), 0.0, 1.0);
		float reflection = BASE_REFLECTION + (MAX_REFLECTION - BASE_REFLECTION) * pow(1.0 - facing, 5.0);
		vec3 reflected = reflect(-to_camera, normal);
		vec3 sky = mix(sky_horizon, sky_top, sqrt(clamp(reflected.y, 0.0, 1.0)));

		float depth = UV2.x;
		// A little of the pattern shows, so still water doesn't look like flat paint.
		vec3 body = mix(shallow_color, deep_color, depth) * mix(0.85, 1.15, height);
		float glitter = pow(clamp(dot(reflected, sun_direction), 0.0, 1.0), SUN_SHARPNESS);

		ALBEDO = mix(body, sky, reflection) + sun_color * glitter;
		ALPHA = clamp(max(mix(shallow_alpha, deep_alpha, depth), reflection) + glitter, 0.0, 1.0);
	}
}
