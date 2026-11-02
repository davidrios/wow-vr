// Shared by shaders with their own light(), which replaces Godot's for the diffuse part too.

// Godot's own Burley diffuse at roughness 1, so these match the materials beside them.
vec3 wow_diffuse(vec3 normal, vec3 light, vec3 view) {
	vec3 half_way = normalize(view + light);
	float n_dot_l = clamp(dot(normal, light), 0.0, 1.0);
	float l_dot_h = clamp(dot(light, half_way), 0.0, 1.0);
	float to_90 = 2.0 * l_dot_h * l_dot_h - 0.5;
	float from_view = 1.0 + to_90 * pow(1.0 - clamp(dot(normal, view), 0.0, 1.0), 5.0);
	float from_light = 1.0 + to_90 * pow(1.0 - n_dot_l, 5.0);
	return vec3(from_view * from_light * n_dot_l / PI);
}

// Phong, as in acazuc's client. Nothing from Blizzard confirms the power of 10.
float wow_highlight(vec3 normal, vec3 light, vec3 view, float power) {
	return pow(clamp(dot(view, reflect(-light, normal)), 0.0, 1.0), power);
}
