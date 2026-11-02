// An M2 particle: a quad that faces the viewer, showing a tile of its texture.

#include "res://shaders/wow_fog.gdshaderinc"

uniform sampler2D tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_disable;
uniform int rows = 1;
uniform int columns = 1;

// Which tile shows over a particle's life. With no keys, it keeps a random one.
uniform int cell_count = 0;
uniform vec4 cell_times = vec4(0.0);
uniform vec4 cell_values = vec4(0.0);

// How much of a cycle is the particle's life. See ParticleLibrary.
uniform float life_share = 1.0;

float cell_at(float life) {
	for (int i = 1; i < cell_count; i++) {
		if (life <= cell_times[i]) {
			float span = cell_times[i] - cell_times[i - 1];
			float along = span > 0.0 ? (life - cell_times[i - 1]) / span : 1.0;
			return mix(cell_values[i - 1], cell_values[i], along);
		}
	}

	return cell_values[max(cell_count - 1, 0)];
}

vec3 to_linear(vec3 c) {
	c = clamp(c, vec3(0.0), vec3(1.0));
	return mix(c / 12.92, pow((c + vec3(0.055)) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	// Each eye's view with the main camera's turn, so both eyes see the quad at one angle.
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(MAIN_CAM_INV_VIEW_MATRIX[0], MAIN_CAM_INV_VIEW_MATRIX[1], MAIN_CAM_INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0));
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);

	int tiles = rows * columns;
	float chosen = cell_count > 0 ? cell_at(INSTANCE_CUSTOM.y / life_share) : INSTANCE_CUSTOM.z * float(tiles);
	int tile = clamp(int(chosen), 0, tiles - 1);
	UV = (UV + vec2(float(tile % columns), float(tile / columns))) / vec2(float(columns), float(rows));
}

void fragment() {
	vec4 t = texture(tex, UV);
	float a = t.a * COLOR.a;
#ifdef ALPHA_KEY
	if (a < 0.5) {
		discard;
	}
	ALBEDO = to_linear(t.rgb * COLOR.rgb);
#else
	// The client adds in gamma space, where a flame's dozens of faint quads soon reach full
	// brightness. Fully converting each one, as a lone glow wants, leaves them too dim to do
	// that, and this power is between the two. Picked by eye.
	ALBEDO = pow(clamp(t.rgb * COLOR.rgb * a, vec3(0.0), vec3(1.0)), vec3(1.4)) * (1.0 - fog_amount(VERTEX));
#endif
}
