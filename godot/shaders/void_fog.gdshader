shader_type spatial;

// The login stage's fog: behind the stage and the character, so it tests depth.
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled, blend_mix, depth_draw_never;

#include "res://shaders/void_fog.gdshaderinc"
