shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Drawn over the whole view while the eyes are under liquid: a slow wobble, a tint, drifting
// murk in the water and caustics on what's near. Fog does the rest, from SkyController.
//
// The murk and caustics are anchored in the world, not the screen: a pattern stuck to the view
// moves with the head, and has no depth, which is uncomfortable in a headset. The wobble is kept
// small and slow for the same reason.

uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D scene_depth : hint_depth_texture, repeat_disable, filter_nearest;
uniform vec3 tint = vec3(0.6, 0.8, 0.9);
uniform vec3 haze = vec3(0.3, 0.5, 0.55);
uniform float strength = 0.004;

const float MURK_STRENGTH = 0.005;
const float NEAR_HAZE = 0.025;
const float CAUSTIC_STRENGTH = 0.025;
const float CAUSTIC_RANGE = 22.0;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

// Soft blobs from 0 to 1, without needing a texture.
float cloud(vec3 p, float time) {
	float n = sin(p.x * 1.7 + time * 0.31 + sin(p.y * 2.3 + time * 0.13))
		+ sin(p.y * 1.9 - time * 0.23 + sin(p.z * 2.1))
		+ sin(p.z * 1.6 + time * 0.27 + sin(p.x * 2.6));
	return n / 6.0 + 0.5;
}

// Thin bright lines where two drifting patterns cross.
float caustic(vec2 p, float time) {
	float a = sin(p.x * 1.3 + sin(p.y * 1.1 + time * 0.5) * 1.5 + time * 0.3);
	float b = sin(p.y * 1.4 + sin(p.x * 0.9 - time * 0.4) * 1.5 - time * 0.2);
	return pow(1.0 - abs(a + b) * 0.5, 6.0);
}

void fragment() {
	vec2 wobble = vec2(
		sin(SCREEN_UV.y * 14.0 + TIME * 0.9) + sin(SCREEN_UV.y * 31.0 - TIME * 0.6),
		sin(SCREEN_UV.x * 17.0 - TIME * 0.7) + sin(SCREEN_UV.x * 27.0 + TIME * 0.5));
	vec3 color = textureLod(screen, SCREEN_UV + wobble * strength, 0.0).rgb * tint;

	// Where this pixel's ray goes, for this eye.
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, texture(scene_depth, SCREEN_UV).r, 1.0);
	vec3 hit_view = view.xyz / max(abs(view.w), 0.00001);
	vec3 camera = INV_VIEW_MATRIX[3].xyz;
	vec3 hit = (INV_VIEW_MATRIX * vec4(hit_view, 1.0)).xyz;
	float reach = length(hit - camera);
	vec3 ray = (hit - camera) / max(reach, 0.001);

	// Caustics on surfaces near the viewer that face up.
	vec3 normal = normalize(cross(dFdx(hit), dFdy(hit)));
	float up = smoothstep(0.3, 0.8, abs(normal.y));
	float near = 1.0 - smoothstep(CAUSTIC_RANGE * 0.4, CAUSTIC_RANGE, reach);
	color += haze * caustic(hit.xz, TIME) * up * near * CAUSTIC_STRENGTH;

	// Murk at a few fixed distances, only those in front of what's seen. Together they read as
	// a volume, and each has real depth in a headset.
	float murk = 0.0;
	murk += (reach > 1.2 ? 1.0 : 0.0) * smoothstep(0.45, 0.8, cloud(camera + ray * 1.2, TIME)) * 0.5;
	murk += (reach > 2.5 ? 1.0 : 0.0) * smoothstep(0.45, 0.8, cloud((camera + ray * 2.5) * 0.7, TIME)) * 0.35;
	murk += (reach > 5.0 ? 1.0 : 0.0) * smoothstep(0.4, 0.8, cloud((camera + ray * 5.0) * 0.4, TIME)) * 0.3;

	// Even what's right at the eye is seen through water.
	color = mix(color, haze, clamp(NEAR_HAZE + murk * MURK_STRENGTH, 0.0, 1.0));

	ALBEDO = color;
}
