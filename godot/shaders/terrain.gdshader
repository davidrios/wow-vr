shader_type spatial;
render_mode cull_back;

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

varying flat vec4 layer_indexes;

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

	vec3 color;
	if (alpha_is_weights) {
		float total = alpha.r + alpha.g + alpha.b;
		vec3 weights = alpha / max(total, 1.0);
		color = layer0 * max(1.0 - total, 0.0) + layer1 * weights.r + layer2 * weights.g + layer3 * weights.b;
	} else {
		color = mix(layer0, layer1, alpha.r);
		color = mix(color, layer2, alpha.g);
		color = mix(color, layer3, alpha.b);
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
