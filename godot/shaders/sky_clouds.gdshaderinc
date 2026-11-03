// Two frames of SkyClouds, fading from one to the next, in gamma space because that's where the client blends them.
uniform sampler2D clouds_from : filter_linear, repeat_disable, hint_default_transparent;
uniform sampler2D clouds_to : filter_linear, repeat_disable, hint_default_transparent;
uniform float clouds_fade = 0.0;

// Half the moon's width, as the tangent of its angle. Picked by eye.
const float MOON_SIZE = 0.05;

// Samples a texture on a quad that always faces the viewer, centered on a direction in the sky.
vec4 on_quad(sampler2D quad, vec3 eye, vec3 center, float size) {
	float ahead = dot(eye, center);
	if (ahead <= 0.0) {
		return vec4(0.0);
	}

	vec3 right = normalize(cross(vec3(0.0, 1.0, 0.001), center));
	vec3 up = cross(center, right);
	vec2 uv = vec2(dot(eye, right), dot(eye, up)) / ahead / size * 0.5 + vec2(0.5);
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) {
		return vec4(0.0);
	}

	return texture(quad, uv);
}

// The client's cloud dome is part of a sphere, 45 degrees from its top, whose center is below the
// eye so that its edge is on the horizon. Its rings are unevenly spaced and each is a step of the
// texture out from the middle. Returns the texture coordinate, and in z how much the rings show.
vec3 on_cloud_dome(vec3 eye) {
	const float EYE_ABOVE_CENTER = 0.70710678;
	float along = -EYE_ABOVE_CENTER * eye.y + sqrt(1.0 - EYE_ABOVE_CENTER * EYE_ABOVE_CENTER + EYE_ABOVE_CENTER * EYE_ABOVE_CENTER * eye.y * eye.y);
	float from_top = acos(clamp(EYE_ABOVE_CENTER + along * eye.y, -1.0, 1.0)) / PI;
	float ring = from_top / 0.025;
	if (from_top > 0.23) {
		ring = from_top > 0.245 ? 10.0 + (from_top - 0.245) / 0.005 : 9.0 + (from_top - 0.23) / 0.015;
	} else if (from_top > 0.205) {
		ring = 8.0 + (from_top - 0.205) / 0.025;
	} else if (from_top > 0.175) {
		ring = 7.0 + (from_top - 0.175) / 0.03;
	}

	float shows = ring < 8.0 ? 1.0 : (ring < 9.0 ? 1.0 - (ring - 8.0) * 0.5 : max(0.5 - (ring - 9.0) * 0.5, 0.0));
	vec2 flat_dir = normalize(eye.xz);
	return vec3(vec2(0.5) + flat_dir * ring / 11.0 * 0.5, shows);
}

// The clouds in a direction: their color in gamma space, and how much they cover.
vec4 clouds_toward(vec3 eye) {
	vec3 dome = on_cloud_dome(eye);
	vec4 clouds = mix(texture(clouds_from, dome.xy), texture(clouds_to, dome.xy), clouds_fade);
	return vec4(clouds.rgb, clouds.a * dome.z);
}
