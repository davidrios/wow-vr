shader_type spatial;

// The loading screen's fog: over the world, which the void hides, like the rest of the screen.
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled, blend_mix, depth_draw_never, depth_test_disabled;

#include "res://shaders/void_fog.gdshaderinc"
