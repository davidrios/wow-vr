shader_type spatial;
render_mode cull_back;

#include "res://shaders/wow_light.gdshaderinc"

// All of a tile's textures, in MTEX order.
uniform sampler2DArray layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// RGB holds the alpha of layers 1, 2 and 3. One 64x64 cell per chunk.
uniform sampler2D alpha_atlas : filter_linear, repeat_disable;
// Big alpha maps are weights that sum to 1, so their layer order means nothing.
// Older maps paint each layer over the ones below.
uniform bool alpha_is_weights = false;
// Debug view: alternate chunks show only their vertex colors.
uniform bool checkerboard = false;
// Debug view: swaps red and blue in vertex colors, to compare byte orders.
uniform bool swap_vertex_colors = false;

// WoW repeats a terrain texture 8 times across a chunk.
const float TEXTURE_REPEATS = 8.0;

// How shiny each layer is, from the alpha of its _s file. Black for a layer without one.
uniform sampler2DArray specular_masks : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float specular_strength = 2.0;
uniform float specular_power = 10.0;

varying flat vec4 layer_indexes;
varying float specular_mask;

void vertex() {
	layer_indexes = round(CUSTOM0 * 255.0);
}

void fragment() {
	vec2 uv = UV * TEXTURE_REPEATS;
	vec3 alpha = texture(alpha_atlas, UV2).rgb;

	vec3 layer0 = texture(layers, vec3(uv, layer_indexes.x)).rgb;
	vec3 layer1 = texture(layers, vec3(uv, layer_indexes.y)).rgb;
	vec3 layer2 = texture(layers, vec3(uv, layer_indexes.z)).rgb;
	vec3 layer3 = texture(layers, vec3(uv, layer_indexes.w)).rgb;

	float mask0 = texture(specular_masks, vec3(uv, layer_indexes.x)).r;
	float mask1 = texture(specular_masks, vec3(uv, layer_indexes.y)).r;
	float mask2 = texture(specular_masks, vec3(uv, layer_indexes.z)).r;
	float mask3 = texture(specular_masks, vec3(uv, layer_indexes.w)).r;

	vec3 color;
	if (alpha_is_weights) {
		float total = alpha.r + alpha.g + alpha.b;
		vec3 weights = alpha / max(total, 1.0);
		float rest = max(1.0 - total, 0.0);
		color = layer0 * rest + layer1 * weights.r + layer2 * weights.g + layer3 * weights.b;
		specular_mask = mask0 * rest + mask1 * weights.r + mask2 * weights.g + mask3 * weights.b;
	} else {
		color = mix(layer0, layer1, alpha.r);
		color = mix(color, layer2, alpha.g);
		color = mix(color, layer3, alpha.b);
		specular_mask = mix(mix(mix(mask0, mask1, alpha.r), mask2, alpha.g), mask3, alpha.b);
	}

	// Vertex colors tint the texture in gamma space, and 127 is neutral.
	vec3 tint = pow((swap_vertex_colors ? COLOR.bgr : COLOR.rgb) * 2.0, vec3(2.2));
	ivec2 chunk = ivec2(floor(UV2 * 16.0));
	if (checkerboard && (chunk.x + chunk.y) % 2 == 1) {
		color = vec3(0.5);
	}

	ALBEDO = color * tint;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

void light() {
	DIFFUSE_LIGHT += wow_diffuse(NORMAL, LIGHT, VIEW) * ATTENUATION * LIGHT_COLOR;

	// Tinted by the texture, as in acazuc's client.
	float highlight = wow_highlight(NORMAL, LIGHT, VIEW, specular_power);
	SPECULAR_LIGHT += ALBEDO * specular_mask * highlight * specular_strength * ATTENUATION * LIGHT_COLOR / PI;
}
