// Godot's fog turns a pixel toward the fog's color, which additive blending then adds to the scene
// and multiplying darkens it by, so a far glow shows as a bright square. Those use fog_disabled and
// this instead, to fade out in the fog. Matches SkyController's depth fog, with curve and density 1.
global uniform vec2 fog_range;

float fog_amount(vec3 view_position) {
	return smoothstep(fog_range.x, fog_range.y, length(view_position));
}
