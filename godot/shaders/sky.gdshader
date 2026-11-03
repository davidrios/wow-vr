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
uniform vec3 moon_direction = vec3(0.0, -1.0, 0.0);
// The client's own: textures\\SunCenter, SunGlare, moon and moonglare.
uniform sampler2D sun_texture : source_color, filter_linear, repeat_disable, hint_default_black;
uniform sampler2D sun_glare_texture : source_color, filter_linear, repeat_disable, hint_default_black;
uniform sampler2D moon_texture : source_color, filter_linear, repeat_disable, hint_default_black;
uniform sampler2D moon_glare_texture : source_color, filter_linear, repeat_disable, hint_default_black;
// 0 once a skybox model fully covers the sky, which hides the sun, moon and clouds.
uniform float made_sky = 1.0;

#include "res://shaders/sky_clouds.gdshaderinc"

// In degrees above the horizon, as in Noggit's sky dome.
const float MIDDLE_ANGLE = 30.0;
const float LOW_ANGLE = 15.0;
const float ABOVE_HORIZON_ANGLE = 5.0;

// Half the width of each quad, as the tangent of its angle. Picked by eye.
const float SUN_SIZE = 0.06;
const float SUN_GLARE_SIZE = 0.45;
const float MOON_GLARE_SIZE = 0.2;

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

	if (AT_CUBEMAP_PASS == false && made_sky > 0.5) {
		float to_sun = dot(EYEDIR, sun_direction);
		if (sun_direction.y > -0.1) {
			color += halo_color * 0.35 * pow(max(to_sun, 0.0), 64.0);
			vec4 glare = on_quad(sun_glare_texture, EYEDIR, sun_direction, SUN_GLARE_SIZE);
			color += glare.rgb * glare.a * sun_color;
			vec4 center = on_quad(sun_texture, EYEDIR, sun_direction, SUN_SIZE);
			color += center.rgb * center.a * 2.0;
		}

		if (moon_direction.y > -0.1) {
			vec4 glare = on_quad(moon_glare_texture, EYEDIR, moon_direction, MOON_GLARE_SIZE);
			color += glare.rgb * glare.a * 0.15;
			vec4 moon = on_quad(moon_texture, EYEDIR, moon_direction, MOON_SIZE);
			color = mix(color, moon.rgb, moon.a);
		}

		if (EYEDIR.y > 0.0) {
			vec4 clouds = clouds_toward(EYEDIR);
			color = pow(mix(pow(max(color, vec3(0.0)), vec3(1.0 / 2.2)), clouds.rgb, clouds.a), vec3(2.2));
		}
	}

	COLOR = color;
}
