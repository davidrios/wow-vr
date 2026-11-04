shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

// Water is painted to go with WoW's hand-painted world, and kept comfortable in a headset: only
// a low, slow swell for moving geometry, only depth read from the screen, for foam, and soft shapes that fade out before they can
// shimmer. All its positions come from UV, so it doesn't jump when the world re-centers.

// The liquid's animation frames.
uniform sampler2DArray frames : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float frame_count = 1.0;
uniform float frames_per_second = 15.0;
// Magma and slime show their texture, lit by the sky's ambient. Water is painted.
uniform bool textured = false;
uniform vec3 texture_light : source_color = vec3(1.0);
// Scrolls the texture, in repeats per second.
uniform vec2 flow = vec2(0.0);

// Soft seamless shapes, whose mipmaps fade to flat grey.
uniform sampler2D paint_noise : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float paint_scale = 1.0;

// All set from the light tables as the time of day changes.
uniform vec3 shallow_color : source_color = vec3(0.1, 0.3, 0.3);
uniform vec3 deep_color : source_color = vec3(0.0, 0.1, 0.2);
uniform float shallow_alpha = 0.5;
uniform float deep_alpha = 1.0;
uniform vec3 sky_top : source_color = vec3(0.2, 0.4, 0.8);
uniform vec3 sky_horizon : source_color = vec3(0.7, 0.8, 0.9);
uniform vec3 sun_color : source_color = vec3(1.0);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform float road_strength = 1.0;

// A long, low swell, in yards. It's slow and gentle because small quick movement of what
// surrounds the viewer is what's uncomfortable in a headset. It stays seamless between tiles for
// the same reason as the ripples, and dies away at the waterline so the shoreline stays still.
// Most lakes' depth values are low, around 0.15, so it has to finish fading well before that.
//
// This water isn't lit, so height alone can't be seen except at its edges. The swell also tilts
// what the surface reflects, by more than its real slope, which is what shows it moving.
const float SWELL_HEIGHT = 0.2;
const float SWELL_FADE_DEPTH = 0.08;
const float SWELL_TILT = 9.0;
// A texture repeat is 4 cells, of 25 / 6 yards each. Slopes are measured over one cell.
const float SWELL_SLOPE_UV = 0.25;
const float SWELL_SLOPE_YARDS = 4.1667;
const float SWELL_SCALE = 0.125;
const vec2 SWELL_DRIFT_A = vec2(0.0035, 0.0025);
const vec2 SWELL_DRIFT_B = vec2(-0.0025, 0.0035);

const float MAX_REFLECTION = 0.206;
const float BLOTCH_STRENGTH = 0.28;
const float FRAME_STRENGTH = 0.10;
const float RIPPLE_LINE_WIDTH = 0.045;
// Kept almost imperceptible, at the user's request.
const float RIPPLE_STRENGTH = 0.02;
// Ripple lines fade out over this range of texture repeats per pixel, before they can alias.
const float RIPPLE_FADE_START = 0.02;
const float RIPPLE_FADE_END = 0.09;
const float ROAD_SHARPNESS = 70.0;
const float ROAD_BRIGHTNESS = 0.5;
const float SHORE_WIDTH = 0.09;
const float SHORE_WOBBLE = 0.12;
const float SHORE_STRENGTH = 0.1125;

// Foam where something meets the water, in yards of depth below it, so it hugs a boat's hull.
const float FOAM_WIDTH = 0.18;
const float FOAM_WOBBLE = 0.2;
const float FOAM_STRENGTH = 0.25;
const float FOAM_FADE_START = 50.0;
const float FOAM_FADE_END = 110.0;

uniform sampler2D scene_depth : hint_depth_texture, repeat_disable, filter_nearest;

varying vec2 swell_slope;

float swell_at(vec2 at, float time) {
	vec2 uv = at * paint_scale * SWELL_SCALE;
	float a = textureLod(paint_noise, uv + time * SWELL_DRIFT_A, 0.0).r;
	float b = textureLod(paint_noise, vec2(uv.x + uv.y, uv.x - uv.y) + time * SWELL_DRIFT_B, 0.0).r;
	return (a + b - 1.0) * 2.0 * SWELL_HEIGHT;
}

void vertex() {
	if (!textured) {
		float fade = smoothstep(0.0, SWELL_FADE_DEPTH, UV2.x);
		float here = swell_at(UV, TIME);
		swell_slope = vec2(
			swell_at(UV + vec2(SWELL_SLOPE_UV, 0.0), TIME) - here,
			swell_at(UV + vec2(0.0, SWELL_SLOPE_UV), TIME) - here) / SWELL_SLOPE_YARDS * fade;
		VERTEX.y += here * fade;
	}
}

void fragment() {
	if (textured) {
		float frame = mod(floor(TIME * frames_per_second), max(frame_count, 1.0));
		ALBEDO = texture(frames, vec3(UV + flow * TIME, frame)).rgb * texture_light;
		ALPHA = 1.0;
	} else {
		vec2 uv = UV * paint_scale;
		float time = TIME;

		// Big slow patches of lighter and darker color.
		float blotch = texture(paint_noise, uv * 0.25 + time * vec2(0.006, 0.004)).r;

		// Two layers drifting across each other. Their whole-number mixes of U and V keep them
		// seamless between tiles. Where they cross middle grey, a thin wavy line shows.
		float a = texture(paint_noise, uv + time * vec2(0.022, 0.012)).r;
		float b = texture(paint_noise, vec2(uv.x + uv.y, uv.x - uv.y) - time * vec2(0.016, 0.026)).r;
		float wave = a + b - 1.0;
		float pixel = max(fwidth(uv.x), fwidth(uv.y));
		float width = max(RIPPLE_LINE_WIDTH, fwidth(wave));
		float lines = 1.0 - smoothstep(0.0, width, abs(wave));
		lines *= 1.0 - smoothstep(RIPPLE_FADE_START, RIPPLE_FADE_END, pixel);

		// Only some patches have lines, or they join into loops that look like a contour map.
		lines *= smoothstep(0.48, 0.68, blotch);

		// The client's own frames, faded into each other so they don't flicker at 15 per second.
		float frame_time = TIME * frames_per_second;
		float frame = mod(floor(frame_time), max(frame_count, 1.0));
		float next = mod(frame + 1.0, max(frame_count, 1.0));
		float detail = mix(
			texture(frames, vec3(UV, frame)).g,
			texture(frames, vec3(UV, next)).g,
			fract(frame_time));

		// VIEW is the same for both eyes, so this works it out per eye, which puts highlights at
		// the right depth in a headset.
		vec3 to_camera = normalize((INV_VIEW_MATRIX * vec4(EYE_OFFSET - VERTEX, 0.0)).xyz);
		vec3 surface_normal = normalize(vec3(-swell_slope.x * SWELL_TILT, 1.0, -swell_slope.y * SWELL_TILT));
		float facing = abs(dot(to_camera, surface_normal));
		float reflection = MAX_REFLECTION * pow(1.0 - facing, 3.0);
		vec3 reflected = reflect(-to_camera, surface_normal);
		vec3 sky = mix(sky_horizon, sky_top, sqrt(clamp(reflected.y, 0.0, 1.0)));

		float depth = UV2.x;
		vec3 body = mix(shallow_color, deep_color, smoothstep(0.0, 1.0, depth));
		body *= 1.0 + (blotch - 0.5) * BLOTCH_STRENGTH * 2.0 + (detail - 0.75) * FRAME_STRENGTH;
		vec3 color = mix(body, sky, reflection);

		// A soft pale band that wanders along the shore.
		float shore = 1.0 - smoothstep(0.0, SHORE_WIDTH, depth + (a - 0.5) * SHORE_WOBBLE);
		color = mix(color, sky_horizon, shore * SHORE_STRENGTH);

		// How far below the water's surface whatever's behind this pixel is. Per eye, as both
		// the depth and the matrices are.
		vec4 scene = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, texture(scene_depth, SCREEN_UV).r, 1.0);
		float scene_y = (INV_VIEW_MATRIX * vec4(scene.xyz / scene.w, 1.0)).y;
		float water_y = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).y;
		float gap = water_y - scene_y + (a - 0.5) * FOAM_WOBBLE;
		float foam = 1.0 - smoothstep(0.0, FOAM_WIDTH, gap);
		foam *= smoothstep(0.3, 0.55, b) * FOAM_STRENGTH;
		foam *= 1.0 - smoothstep(FOAM_FADE_START, FOAM_FADE_END, -VERTEX.z);
		color = mix(color, mix(sky_horizon, vec3(1.0), 0.5), foam);

		// Ripple lines catch the sky's light.
		color = mix(color, sky_horizon, lines * RIPPLE_STRENGTH);

		// The sun's road: a broad glow, broken up by the same shapes rather than by sparkles.
		float road = pow(clamp(dot(reflected, sun_direction), 0.0, 1.0), ROAD_SHARPNESS);
		float broken = smoothstep(0.42, 0.58, a * 0.5 + b * 0.5 + road * 0.3);
		float glow = road * (0.35 + 0.65 * broken) * road_strength * ROAD_BRIGHTNESS;
		color += sun_color * glow;

		ALBEDO = color;
		float alpha = mix(shallow_alpha, deep_alpha, depth);
		alpha = max(alpha, max(reflection, lines * RIPPLE_STRENGTH));
		alpha = max(alpha, shore * SHORE_STRENGTH);
		alpha = max(alpha, foam);
		ALPHA = clamp(alpha + glow, 0.0, 1.0);
	}
}
