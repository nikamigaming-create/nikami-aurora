shader_type spatial;
render_mode blend_mix, depth_prepass_alpha, cull_disabled;

// The original DAO mesh supplies the shoreline and elevation. Surface detail is
// generated in world space so a very large lake cannot expose stretched mesh UVs.
uniform vec4 deep_color : source_color = vec4(0.025, 0.105, 0.145, 0.91);
uniform vec4 shallow_color : source_color = vec4(0.12, 0.31, 0.32, 0.82);
uniform vec4 sky_tint : source_color = vec4(0.28, 0.43, 0.55, 1.0);
uniform sampler2D normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 vsh_params0 = vec4(0.0, 0.01, 0.1, 0.0);
uniform vec4 vsh_params1 = vec4(0.00866025, -0.005, 0.1, 0.0);
uniform vec4 vsh_params2 = vec4(-0.00866025, -0.005, 0.1, 0.0);
uniform vec4 psh_params0 = vec4(0.1, 0.1, 0.1, 0.0);
uniform vec4 psh_params1 = vec4(1.0, 1.0, 50.0, 0.39);
uniform vec4 water_fog = vec4(0.008255, 0.65, 0.0, 0.0);

varying vec3 world_position;

vec3 dao_normal(vec4 sample_value) {
	// The installed DXT5nm resource stores tangent X in alpha and Y in green.
	vec2 xy = sample_value.ag * 2.0 - 1.0;
	return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
}

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 world_uv = world_position.xz;
	vec3 n0 = dao_normal(texture(normal_map, world_uv * vsh_params0.z + TIME * vsh_params0.xy));
	vec3 n1 = dao_normal(texture(normal_map, world_uv * vsh_params1.z + TIME * vsh_params1.xy));
	vec3 n2 = dao_normal(texture(normal_map, world_uv * vsh_params2.z + TIME * vsh_params2.xy));
	vec3 tangent_normal = normalize(n0 + n1 + n2);
	vec3 generated_normal = normalize(vec3(tangent_normal.x, tangent_normal.z, tangent_normal.y));
	vec3 view_normal = normalize((VIEW_MATRIX * vec4(generated_normal, 0.0)).xyz);

	// water0.psh uses the dielectric Schlick constants 0.02/0.98.
	float fresnel = 0.02 + 0.98 * pow(1.0 - clamp(dot(view_normal, normalize(VIEW)), 0.0, 1.0), 5.0);
	float variation = clamp(tangent_normal.z * 0.5 + 0.5, 0.0, 1.0);
	vec3 base = mix(deep_color.rgb, shallow_color.rgb, variation);
	base = mix(base, sky_tint.rgb, fresnel * 0.48);

	ALBEDO = base;
	ROUGHNESS = mix(0.22, 0.08, fresnel);
	METALLIC = 0.0;
	SPECULAR = 0.72;
	NORMAL = view_normal;
	ALPHA = mix(deep_color.a, shallow_color.a, 0.42) + fresnel * 0.05;
}
