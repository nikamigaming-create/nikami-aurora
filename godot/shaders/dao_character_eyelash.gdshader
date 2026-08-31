shader_type spatial;

// Clean-room port of the installed retail eyelash path:
// PF_ULM_BASa.mao -> Eye.mat/Eyelash -> Eyelash0.psh + EyelashPnch.sta.
// The DX9 state blends and alpha-tests at the same time, keeps depth writes,
// and disables culling. Godot exposes the same contract through blend_mix,
// ALPHA_SCISSOR_THRESHOLD, depth_draw_always, and cull_disabled.
render_mode unshaded, blend_mix, depth_draw_always, cull_disabled;

uniform bool dao_probe_enabled = false;
uniform vec4 dao_probe_r0 = vec4(0.0);
uniform vec4 dao_probe_r1 = vec4(0.0);
uniform vec4 dao_probe_r2 = vec4(0.0);
uniform vec4 dao_probe_r3 = vec4(0.0);
uniform vec4 dao_probe_g0 = vec4(0.0);
uniform vec4 dao_probe_g1 = vec4(0.0);
uniform vec4 dao_probe_g2 = vec4(0.0);
uniform vec4 dao_probe_g3 = vec4(0.0);
uniform vec4 dao_probe_b0 = vec4(0.0);
uniform vec4 dao_probe_b1 = vec4(0.0);
uniform vec4 dao_probe_b2 = vec4(0.0);
uniform vec4 dao_probe_b3 = vec4(0.0);
uniform vec3 dao_character_light = vec3(0.0);

varying vec3 dao_vertex_lighting;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float opacity_multiplier = 1.0;
uniform float alpha_threshold : hint_range(0.0, 1.0) = 0.0784313725490196;
uniform float mip_bias = -2.5;

vec3 dao_probe_irradiance(vec3 godot_world) {
	if (!dao_probe_enabled) {
		return vec3(1.0);
	}
	vec3 haven_normal = vec3(godot_world.x, -godot_world.z, godot_world.y);
	vec4 n = vec4(normalize(haven_normal), 1.0);
	vec4 mr = vec4(dot(dao_probe_r0, n), dot(dao_probe_r1, n), dot(dao_probe_r2, n), dot(dao_probe_r3, n));
	vec4 mg = vec4(dot(dao_probe_g0, n), dot(dao_probe_g1, n), dot(dao_probe_g2, n), dot(dao_probe_g3, n));
	vec4 mb = vec4(dot(dao_probe_b0, n), dot(dao_probe_b1, n), dot(dao_probe_b2, n), dot(dao_probe_b3, n));
	return max(vec3(dot(n, mr), dot(n, mg), dot(n, mb)), vec3(0.0));
}

void vertex() {
	vec3 world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	// Eye0.vsh writes SH(normal) + g_vLightParams[4] to COLOR0. Keeping this
	// as a vertex result preserves the retail interpolation across the lashes.
	dao_vertex_lighting = dao_probe_irradiance(world_normal) + dao_character_light;
}

void fragment() {
	// Eyelash0.psh multiplies diffuse RGB by COLOR0 and leaves sampled alpha
	// under the independent material opacity multiplier.
	vec4 diffuse = texture(albedo_texture, UV, mip_bias);
	ALBEDO = diffuse.rgb * max(dao_vertex_lighting, vec3(0.0));
	ALPHA = diffuse.a * opacity_multiplier;
	ALPHA_SCISSOR_THRESHOLD = alpha_threshold;
}
