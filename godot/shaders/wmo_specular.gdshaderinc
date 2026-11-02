// The outside of a WMO whose material's shader has a highlight. The cases are acazuc's client's,
// and nothing from Blizzard confirms them. Indoor groups aren't lit by the sun, so they don't use this.
#include "res://shaders/wow_light.gdshaderinc"

const int MODE_SPECULAR = 1;
const int MODE_METAL = 2;
const int MODE_ENV_METAL = 5;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform int mode = 1;
uniform float specular_strength = 1.0;
uniform float metal_strength = 4.0;
uniform float specular_power = 10.0;

varying float texture_alpha;

void fragment() {
	vec4 color = texture(albedo_texture, UV);
	ALBEDO = color.rgb;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	texture_alpha = color.a;
#ifdef ALPHA_KEY
	ALPHA = color.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
#endif
}

void light() {
	DIFFUSE_LIGHT += wow_diffuse(NORMAL, LIGHT, VIEW) * ATTENUATION * LIGHT_COLOR;

	vec3 amount = vec3(specular_strength);
	if (mode == MODE_METAL) {
		amount = vec3(texture_alpha * metal_strength);
	} else if (mode == MODE_ENV_METAL) {
		amount = ALBEDO * specular_strength;
	}

	SPECULAR_LIGHT += amount * wow_highlight(NORMAL, LIGHT, VIEW, specular_power) * ATTENUATION * LIGHT_COLOR / PI;
}
