shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

varying vec2 flame_coord;
uniform float flame_half_extent = 0.357;
uniform float flame_height = 0.85;
uniform float flame_min_y = 0.0;

void vertex() {
	// The generated fallback contains two crossed 0.714 x 0.85 m planes but
	// no UVs. Reconstruct stable coordinates from their authored local bounds.
	flame_coord = vec2(max(abs(VERTEX.x), abs(VERTEX.z)) / max(flame_half_extent, 0.001),
		(VERTEX.y - flame_min_y) / max(flame_height, 0.001));
}

void fragment() {
	float y = clamp(flame_coord.y, 0.0, 1.0);
	float flicker = sin(TIME * 11.0 + y * 17.0) * 0.055 + sin(TIME * 7.0 - y * 9.0) * 0.035;
	float half_width = mix(0.92, 0.08, pow(y, 1.35)) + flicker;
	float edge = 1.0 - smoothstep(max(0.0, half_width - 0.18), half_width, flame_coord.x);
	float base_fade = smoothstep(0.0, 0.10, y);
	float tip_fade = 1.0 - smoothstep(0.82, 1.0, y);
	float alpha = edge * base_fade * tip_fade;
	if (alpha < 0.03) {
		discard;
	}
	vec3 hot = vec3(1.0, 0.78, 0.22);
	vec3 ember = vec3(1.0, 0.10, 0.008);
	ALBEDO = mix(hot, ember, clamp(y * 0.78 + flame_coord.x * 0.18, 0.0, 1.0));
	EMISSION = ALBEDO * mix(2.6, 1.1, y);
	ALPHA = alpha * 0.82;
}
