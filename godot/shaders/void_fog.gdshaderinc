// A thick fog around the head, rising from below to just over the horizon, for the voids behind the
// login and loading screens: where a sky has no ground, the fog is it. Its colors are a zone's horizon
// and fog colors from the client's light tables.

uniform float fade = 1.0;
uniform vec3 horizon_color : source_color = vec3(0.5, 0.33, 0.26);
uniform vec3 fog_color : source_color = vec3(0.16, 0.0, 0.12);

// Where the fog thins out above the horizon and is thickest below it, as heights on the unit sphere.
uniform float top = 0.22;
uniform float solid = -0.08;

varying vec3 direction;

void vertex() {
	direction = VERTEX;
}

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}

void fragment() {
	vec3 dir = normalize(direction);
	float around = atan(dir.z, dir.x);

	// Slow wisps along the horizon, so the edge isn't a clean line.
	vec2 uv = vec2(around * 3.0 + TIME * 0.015, dir.y * 9.0);
	float wisps = noise(uv) * 0.6 + noise(uv * 2.3 + vec2(TIME * 0.02, 0.0)) * 0.4;
	float edge = top + (wisps - 0.5) * 0.12;
	float thickness = smoothstep(edge, solid, dir.y);

	vec3 color = mix(horizon_color, fog_color, smoothstep(0.08, -0.45, dir.y));
	ALBEDO = color;
	ALPHA = thickness * fade;
}
