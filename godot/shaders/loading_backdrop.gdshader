shader_type spatial;

// The void behind the loading panel: a dark sky with slow stars, around the head, over everything.
render_mode unshaded, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled, blend_mix;

uniform float fade = 1.0;
uniform vec3 top_color : source_color = vec3(0.012, 0.016, 0.045);
uniform vec3 horizon_color : source_color = vec3(0.05, 0.075, 0.17);
uniform vec3 bottom_color : source_color = vec3(0.004, 0.005, 0.014);
uniform vec3 star_color : source_color = vec3(0.8, 0.88, 1.0);
uniform float star_cells = 42.0;

varying vec3 direction;

void vertex() {
	direction = VERTEX;
}

vec3 hash(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.xxy + p.yxx) * p.zyx);
}

void fragment() {
	vec3 dir = normalize(direction);
	float up = dir.y;
	vec3 color = mix(horizon_color, top_color, smoothstep(0.0, 0.7, up));
	color = mix(color, bottom_color, smoothstep(0.0, -0.45, up));

	vec3 scaled = dir * star_cells;
	vec3 cell = floor(scaled);
	vec3 random = hash(cell);
	// One cell in six has a star, away from the cell's edge so none is cut in half.
	if (random.z > 0.83) {
		vec3 center = cell + 0.5 + (hash(cell + 7.0) - 0.5) * 0.5;
		float size = mix(0.035, 0.11, random.x * random.x);
		float star = smoothstep(size, 0.0, distance(scaled, center));
		float twinkle = 0.65 + 0.35 * sin(TIME * mix(0.4, 1.3, random.y) + random.x * 40.0);
		color += star_color * star * twinkle * smoothstep(-0.5, 0.05, up);
	}

	ALBEDO = color;
	ALPHA = fade;
}
