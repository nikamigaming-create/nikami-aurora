shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha;

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
uniform int dao_point_count = 0;
uniform vec4 dao_point_position_0 = vec4(0.0);
uniform vec4 dao_point_position_1 = vec4(0.0);
uniform vec4 dao_point_position_2 = vec4(0.0);
uniform vec4 dao_point_color_0 = vec4(0.0);
uniform vec4 dao_point_color_1 = vec4(0.0);
uniform vec4 dao_point_color_2 = vec4(0.0);

uniform sampler2D albedo_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tint_noise_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D roughness_texture : hint_roughness_g, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 hair_tint : source_color = vec4(1.0);
uniform vec3 diffuse_tint_0 = vec3(1.0);
uniform vec3 diffuse_tint_1 = vec3(1.0);
uniform vec3 diffuse_tint_2 = vec3(1.0);
uniform vec3 specular_tint_0 = vec3(1.0);
uniform vec3 specular_tint_1 = vec3(1.0);
uniform vec3 specular_tint_2 = vec3(1.0);
uniform float primary_specular_mask = 0.01;
uniform float primary_specular_power = 60.0;
uniform float secondary_specular_power = 62.0;
uniform float noise_tiling = 7.32;
uniform bool packed_bioware = false;
uniform bool has_specular_mask = false;
uniform bool has_roughness_map = false;
uniform bool has_metallic_map = false;
uniform float roughness : hint_range(0.1, 0.9) = 0.68;
uniform float metallic_value : hint_range(0.0, 1.0) = 0.0;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.34;

void fragment() {
	vec4 base = texture(albedo_texture, UV);
	vec3 hair_diffuse;
	if (packed_bioware) {
		float noise_value = texture(tint_noise_texture, UV * noise_tiling).r;
		hair_diffuse = noise_value * diffuse_tint_1 + base.g * diffuse_tint_0 + (1.0 - base.g) * diffuse_tint_2;
		ALPHA = base.b;
		float packed_x = base.r * 0.4 - 0.2;
		NORMAL_MAP = normalize(vec3(packed_x, 0.0, 1.0)) * 0.5 + 0.5;
	} else {
		hair_diffuse = base.rgb * hair_tint.rgb;
		ALPHA = base.a;
		NORMAL_MAP = texture(normal_texture, UV).xyz;
	}
	ALBEDO = hair_diffuse;
	ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
	NORMAL_MAP_DEPTH = 0.72;
	float authored_roughness = has_roughness_map ? texture(roughness_texture, UV).g : 1.0;
	float authored_metallic = has_metallic_map ? texture(metallic_texture, UV).b : 1.0;
	ROUGHNESS = clamp(roughness * authored_roughness, 0.02, 1.0);
	METALLIC = clamp(metallic_value * authored_metallic, 0.0, 1.0);
	SPECULAR = 0.12 * (has_specular_mask ? texture(specular_mask, UV).r : 1.0);
	ANISOTROPY = 0.32;
	ANISOTROPY_FLOW = vec2(0.0, 1.0);
}
