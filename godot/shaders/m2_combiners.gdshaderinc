// M2 batches with a second texture, moving textures or animated color. The formulas are the
// wiki's, on M2/Rendering. The client adds in gamma space, so this does too, which is why
// the samplers aren't source_color. See docs/formats.md.

uniform sampler2D tex0 : hint_default_white, filter_linear_mipmap_anisotropic, TEX0_WRAP;
uniform sampler2D tex1 : hint_default_white, filter_linear_mipmap_anisotropic, TEX1_WRAP;
uniform int combiner = 1;
uniform int uv_source0 = 0;
uniform int uv_source1 = 0;
uniform mat3 uv_matrix0 = mat3(1.0);
uniform mat3 uv_matrix1 = mat3(1.0);
uniform vec3 tint = vec3(1.0);
uniform float element_alpha = 1.0;
uniform float alpha_key = 0.878;

#include "res://shaders/wow_fog.gdshaderinc"

#ifdef INSTANCE_LIGHT
// The sky's light in gamma space, for the parts of a building that the outside reaches.
global uniform vec3 world_ambient;
global uniform vec3 world_sun;
global uniform vec3 world_to_sun;
#endif

#ifdef INDOOR_OCCLUSION
// Baked light already says how an interior looks, but only lit surfaces take ambient occlusion,
// which only dims their ambient light. So this ignores every other light, and divides by the
// ambient, for it to be multiplied back. What's left is the baked look, dimmed by occlusion.
global uniform vec3 indoor_ambient;

void light() {
}
#endif

#ifdef FLOOR_LIGHT
// A creature's light from the floor under it, in gamma space, and in alpha how far it's
// replaced the sky's. Emission isn't lit, so as this rises the sky's light fades out.
instance uniform vec4 floor_light = vec4(1.0, 1.0, 1.0, 0.0);
#endif

#ifdef BAKED_BONES
// See M2BoneBake. CUSTOM0 holds a vertex's 4 bones and CUSTOM1 their weights.
uniform sampler2D bone_texture : filter_nearest, repeat_disable;
uniform float bake_seconds = 1.0;
uniform int bake_frames = 1;

mat4 bone_matrix(int bone, int frame) {
	vec4 x = texelFetch(bone_texture, ivec2(bone * 3, frame), 0);
	vec4 y = texelFetch(bone_texture, ivec2(bone * 3 + 1, frame), 0);
	vec4 z = texelFetch(bone_texture, ivec2(bone * 3 + 2, frame), 0);
	return transpose(mat4(x, y, z, vec4(0.0, 0.0, 0.0, 1.0)));
}
#endif

#ifdef SHAPED_LIGHT
// How much light a doodad's far side keeps, by eye. Its light is MODD's color, from one direction,
// and this stands in for MOHD's ambient, which alone would leave those sides nearly black.
const float SHAPED_FILL = 0.45;

// Where this copy's light goes, in the world's axes. Empty for a WMO's own walls, whose light is baked.
varying vec3 light_going;
#endif

#if defined(BAKED_BONES) || defined(BILLBOARD) || defined(SHAPED_LIGHT)
void vertex() {
#ifdef SHAPED_LIGHT
	light_going = INSTANCE_CUSTOM.xyz;
#endif
	// A worn item has no bones of its own, but can still have parts that turn to the viewer.
	mat4 skin = mat4(1.0);
#ifdef BAKED_BONES
	// Each copy keeps its own phase, from where it stands, so a forest doesn't sway as one.
	float phase = fract(dot(MODEL_MATRIX[3].xz, vec2(0.0137, 0.0071)));
	float position = fract(TIME / bake_seconds + phase) * float(bake_frames);
	int frame_a = int(position) % bake_frames;
	int frame_b = (frame_a + 1) % bake_frames;
	float blend = fract(position);
	ivec4 bones = ivec4(CUSTOM0 * 255.0 + 0.5);
	skin = mat4(0.0);
	for (int i = 0; i < 4; i++) {
		if (CUSTOM1[i] > 0.0) {
			skin += CUSTOM1[i] * ((1.0 - blend) * bone_matrix(bones[i], frame_a) + blend * bone_matrix(bones[i], frame_b));
		}
	}
#endif

#ifdef BILLBOARD
	// CUSTOM2 is the pivot of the bone that turns, and its M2BillboardKind. Each model axis
	// goes on a view axis: X toward the viewer, Y (the model's up here) upward.
	if (CUSTOM2.w < 0.5) {
		VERTEX = (MODELVIEW_MATRIX * skin * vec4(VERTEX, 1.0)).xyz;
		NORMAL = normalize(mat3(MODELVIEW_MATRIX) * mat3(skin) * NORMAL);
	} else {
		vec3 anchor = (MODELVIEW_MATRIX * skin * vec4(CUSTOM2.xyz, 1.0)).xyz;
		vec3 toward = normalize(-anchor);
		mat3 axes = mat3(MODELVIEW_MATRIX);
		float model_scale = length(axes[0]);
		vec3 x = vec3(0.0, 0.0, 1.0);
		vec3 y = vec3(0.0, 1.0, 0.0);
		vec3 z = vec3(-1.0, 0.0, 0.0);
		if (CUSTOM2.w > 3.5) {
			y = normalize(axes[1]);
			x = normalize(toward - y * dot(toward, y));
			z = cross(x, y);
		} else if (CUSTOM2.w > 2.5) {
			z = normalize(axes[2]);
			x = normalize(toward - z * dot(toward, z));
			y = cross(z, x);
		} else if (CUSTOM2.w > 1.5) {
			x = normalize(axes[0]);
			z = normalize(cross(x, toward));
			y = cross(z, x);
		}

		vec3 bone_scale = vec3(length(skin[0].xyz), length(skin[1].xyz), length(skin[2].xyz));
		vec3 offset = (VERTEX - CUSTOM2.xyz) * bone_scale * model_scale;
		VERTEX = anchor + x * offset.x + y * offset.y + z * offset.z;
		NORMAL = normalize(x * NORMAL.x + y * NORMAL.y + z * NORMAL.z);
	}
#else
	VERTEX = (skin * vec4(VERTEX, 1.0)).xyz;
	NORMAL = normalize(mat3(skin) * NORMAL);
#endif
}
#endif

const int TWO_TEXTURES = 10;

vec3 to_linear(vec3 c) {
	c = clamp(c, vec3(0.0), vec3(1.0));
	return mix(c / 12.92, pow((c + vec3(0.055)) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec2 sphere_map(vec3 vertex, vec3 normal) {
	vec3 position = -normalize(vertex);
	vec3 reflected = position - normal * (2.0 * dot(position, normal));
	reflected.z += 1.0;
	return normalize(reflected).xy * 0.5 + vec2(0.5);
}

vec2 coords(int source, vec2 first, vec2 second, vec2 sphere, mat3 matrix) {
	vec2 uv = source == 2 ? sphere : (source == 1 ? second : first);
	return (matrix * vec3(uv, 1.0)).xy;
}

void fragment() {
	vec2 sphere = sphere_map(VERTEX, NORMAL);
	vec4 t0 = texture(tex0, coords(uv_source0, UV, UV2, sphere, uv_matrix0));
	vec4 t1 = texture(tex1, coords(uv_source1, UV, UV2, sphere, uv_matrix1));
	vec3 lit = tint;
#ifdef INSTANCE_LIGHT
	// Inside a building, each copy has its own baked light instead of the sky's. A WMO's vertex
	// alpha is how far the outside reaches it, as in acazuc's client, so near a door it follows
	// the time of day. Doodads have alpha 0.
	vec3 outside = world_ambient + world_sun * clamp(dot(NORMAL, normalize(mat3(VIEW_MATRIX) * world_to_sun)), 0.0, 1.0);
	lit *= mix(COLOR.rgb, min(outside, vec3(1.0)), COLOR.a);
#endif
#ifdef SHAPED_LIGHT
	if (dot(light_going, light_going) > 0.25) {
		vec3 toward_light = -normalize(mat3(VIEW_MATRIX) * light_going);
		lit *= mix(SHAPED_FILL, 1.0, clamp(dot(NORMAL, toward_light), 0.0, 1.0));
	}
#endif
	vec3 added = vec3(0.0);
	float a = element_alpha;

	// "lit" gets lighting, and what's added doesn't, like the shaders' input color and textures.
	switch (combiner) {
		case 0: lit *= t0.rgb; break;
		case 1: lit *= t0.rgb; a *= t0.a; break;
		case 2: lit = mix(lit, t0.rgb, a); break;
		case 3: added = t0.rgb; a += t0.a; break;
		case 4: lit *= t0.rgb * 2.0; a *= t0.a * 2.0; break;
		case 5: lit = mix(t0.rgb, lit, a); break;
		// Opaque first, then the second texture's combiner.
		case 10: lit *= t0.rgb * t1.rgb; break;
		case 11: lit *= t0.rgb * t1.rgb; a *= t1.a; break;
		case 13: lit *= t0.rgb; added = t1.rgb; a += t1.a; break;
		case 14: lit *= t0.rgb * t1.rgb * 2.0; a *= t1.a * 2.0; break;
		case 16: lit *= t0.rgb * t1.rgb * 2.0; break;
		case 17: lit *= t0.rgb; added = t1.rgb; break;
		// Mod first.
		case 20: lit *= t0.rgb * t1.rgb; a *= t0.a; break;
		case 21: lit *= t0.rgb * t1.rgb; a *= t0.a * t1.a; break;
		case 23: lit *= t0.rgb; added = t1.rgb; a = a * t0.a + t1.a; break;
		case 24: lit *= t0.rgb * t1.rgb * 2.0; a *= t0.a * t1.a * 2.0; break;
		case 26: lit *= t0.rgb * t1.rgb * 2.0; a *= t0.a; break;
		case 27: lit *= t0.rgb; added = t1.rgb; a *= t0.a; break;
		// The other two pairs the client has.
		case 41: added = t0.rgb * t1.rgb; lit *= t1.rgb; a = (a + t0.a) * t1.a; break;
		case 54: lit *= t0.rgb * t1.rgb * 4.0; a = t0.a * t1.a * 4.0; break;
		// Layers the client merges into one.
		case 100: lit *= t0.rgb * mix(t1.rgb * 2.0, vec3(1.0), t0.a); break;
		case 101: lit *= t0.rgb; added = t1.rgb * t1.a; break;
		case 102: lit *= t0.rgb; added = t1.rgb * t1.a * t0.a; break;
		default: lit *= t0.rgb; a *= t0.a; break;
	}

#ifdef ALPHA_KEY
	// 224 / 255 on this client, scaled so that fading doesn't eat into the shape.
	if (a < alpha_key * element_alpha) {
		discard;
	}
#endif

#ifdef INDOOR_OCCLUSION
	ALBEDO = to_linear(lit + added) / max(indoor_ambient, vec3(0.001));
#elif defined(UNSHADED)
	ALBEDO = to_linear(lit + added);
#elif defined(FLOOR_LIGHT)
	ALBEDO = to_linear(lit) * (1.0 - floor_light.a);
	// Darker facing down, so a body indoors keeps its shape. The amounts are by eye.
	float facing = dot(NORMAL, normalize(mat3(VIEW_MATRIX) * vec3(0.0, 1.0, 0.0)));
	EMISSION = to_linear(added) + to_linear(lit * floor_light.rgb * (0.9 + 0.1 * facing)) * floor_light.a;
#else
	ALBEDO = to_linear(lit);
	EMISSION = to_linear(added);
#endif

#if defined(GAMMA_ADD)
	// The client blends in gamma space and Godot in linear, where a faint glow comes out far
	// stronger. Over a dark background, these give what the client would.
	ALBEDO = to_linear((lit + added) * clamp(a, 0.0, 1.0));
	ALPHA = 1.0;
#elif defined(GAMMA_ALPHA)
	ALPHA = pow(clamp(a, 0.0, 1.0), 2.2);
#elif defined(BLENDED)
	ALPHA = clamp(a, 0.0, 1.0);
#endif

#if defined(FOG_TO_BLACK)
	ALBEDO *= 1.0 - fog_amount(VERTEX);
	EMISSION *= 1.0 - fog_amount(VERTEX);
#elif defined(FOG_TO_WHITE)
	ALBEDO = mix(ALBEDO, vec3(1.0), fog_amount(VERTEX));
#endif

	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
