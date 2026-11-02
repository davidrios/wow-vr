shader_type sky;

// Colors from the light tables, from straight up down to the horizon.
uniform vec3 top_color : source_color;
uniform vec3 middle_color : source_color;
uniform vec3 low_color : source_color;
uniform vec3 above_horizon_color : source_color;
uniform vec3 horizon_color : source_color;
// Below the horizon, as WoW's sky dome does.
uniform vec3 fog_color : source_color;

uniform vec3 sun_color : source_color;
uniform vec3 halo_color : source_color;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
// 0 by day, 1 when the stars are fully out.
uniform float star_brightness = 0.0;

// In degrees above the horizon, as in Noggit's sky dome.
const float MIDDLE_ANGLE = 30.0;
const float LOW_ANGLE = 15.0;
const float ABOVE_HORIZON_ANGLE = 5.0;

const float SUN_SIZE = 0.9994;
const float SUN_EDGE = 0.9990;
const float STAR_CELLS = 220.0;

float random(vec3 cell) {
	return fract(sin(dot(cell, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

vec3 sky_gradient(float angle) {
	if (angle >= MIDDLE_ANGLE) {
		return mix(middle_color, top_color, (angle - MIDDLE_ANGLE) / (90.0 - MIDDLE_ANGLE));
	}
	if (angle >= LOW_ANGLE) {
		return mix(low_color, middle_color, (angle - LOW_ANGLE) / (MIDDLE_ANGLE - LOW_ANGLE));
	}
	if (angle >= ABOVE_HORIZON_ANGLE) {
		return mix(above_horizon_color, low_color, (angle - ABOVE_HORIZON_ANGLE) / (LOW_ANGLE - ABOVE_HORIZON_ANGLE));
	}
	if (angle >= 0.0) {
		return mix(horizon_color, above_horizon_color, angle / ABOVE_HORIZON_ANGLE);
	}
	return mix(horizon_color, fog_color, clamp(-angle / 10.0, 0.0, 1.0));
}

void sky() {
	float angle = degrees(asin(clamp(EYEDIR.y, -1.0, 1.0)));
	vec3 color = sky_gradient(angle);

	if (AT_CUBEMAP_PASS == false) {
		float to_sun = dot(EYEDIR, sun_direction);
		if (sun_direction.y > -0.1) {
			color += halo_color * 0.35 * pow(max(to_sun, 0.0), 64.0);
			color = mix(color, sun_color * 2.0, smoothstep(SUN_EDGE, SUN_SIZE, to_sun));
		}

		if (star_brightness > 0.0 && EYEDIR.y > 0.0) {
			vec3 cell = floor(EYEDIR * STAR_CELLS);
			float star = step(0.9975, random(cell));
			color += vec3(star * random(cell + 1.0) * star_brightness);
		}
	}

	COLOR = color;
}
