shader_type spatial;
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

varying vec3 dao_probe_world_normal;

vec3 dao_probe_irradiance(vec3 godot_world) {
	if (!dao_probe_enabled) {
		return vec3(0.0);
	}
	godot_world = normalize(godot_world);
	vec3 haven_normal = vec3(godot_world.x, -godot_world.z, godot_world.y);
	vec4 n = vec4(haven_normal, 1.0);
	vec4 mr = vec4(dot(dao_probe_r0, n), dot(dao_probe_r1, n), dot(dao_probe_r2, n), dot(dao_probe_r3, n));
	vec4 mg = vec4(dot(dao_probe_g0, n), dot(dao_probe_g1, n), dot(dao_probe_g2, n), dot(dao_probe_g3, n));
	vec4 mb = vec4(dot(dao_probe_b0, n), dot(dao_probe_b1, n), dot(dao_probe_b2, n), dot(dao_probe_b3, n));
	return max(vec3(dot(n, mr), dot(n, mg), dot(n, mb)), vec3(0.0));
}

void vertex() {
	dao_probe_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

render_mode blend_mix, depth_prepass_alpha, cull_back, diffuse_burley, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D texture_roughness : hint_roughness_g, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D texture_metallic : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D texture_tint_mask : filter_linear_mipmap, repeat_enable;
uniform vec3 tint_zone1 : source_color = vec3(1.0);
uniform vec3 tint_zone2 : source_color = vec3(1.0);
uniform vec3 tint_zone3 : source_color = vec3(1.0);
uniform vec3 tint_zone4 : source_color = vec3(1.0);
uniform vec4 tint_opacity = vec4(1.0);
uniform bool has_roughness_map = false;
uniform bool has_metallic_map = false;
uniform float roughness : hint_range(0.0, 1.0) = 0.58;
uniform float specular : hint_range(0.0, 1.0) = 0.42;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec4 base = texture(texture_albedo, UV) * albedo;
	vec4 mask = texture(texture_tint_mask, UV);
	float w1 = mask.r * tint_opacity.r;
	float w2 = mask.g * tint_opacity.g;
	float w3 = mask.b * tint_opacity.b;
	float w4 = mask.a * tint_opacity.a;
	float w_sum = clamp(w1 + w2 + w3, 0.0, 1.0);
	vec3 zone_sum = tint_zone1 * w1 + tint_zone2 * w2 + tint_zone3 * w3;
	vec3 zone4_mult = mix(vec3(1.0), tint_zone4, w4);
	base.rgb = (base.rgb * zone4_mult) * (1.0 - w_sum) + zone_sum;
	ALBEDO = base.rgb;
	EMISSION = ALBEDO * dao_probe_irradiance(dao_probe_world_normal);
	ALPHA = base.a;
	ALPHA_SCISSOR_THRESHOLD = 0.12;
	NORMAL_MAP = texture(texture_normal, UV).rgb;
	NORMAL_MAP_DEPTH = 1.0;
	float authored_roughness = has_roughness_map ? texture(texture_roughness, UV).g : 1.0;
	float authored_metallic = has_metallic_map ? texture(texture_metallic, UV).b : 1.0;
	ROUGHNESS = clamp(roughness * authored_roughness, 0.02, 1.0);
	METALLIC = clamp(metallic * authored_metallic, 0.0, 1.0);
	SPECULAR = specular;
}
