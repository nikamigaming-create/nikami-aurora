shader_type spatial;
render_mode unshaded, depth_draw_opaque, cull_back;

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
uniform int dao_point_count = 0;
uniform vec4 dao_point_position_0 = vec4(0.0);
uniform vec4 dao_point_position_1 = vec4(0.0);
uniform vec4 dao_point_position_2 = vec4(0.0);
uniform vec4 dao_point_color_0 = vec4(0.0);
uniform vec4 dao_point_color_1 = vec4(0.0);
uniform vec4 dao_point_color_2 = vec4(0.0);

uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// Character.mat treats this RGBA texture as data. ArmourSkinTint reserves
// alpha for exposed skin while RGB remains the three equipment tint zones.
uniform sampler2D texture_tint_mask : filter_linear_mipmap, repeat_enable;
uniform vec3 skin_diffuse = vec3(1.0);
uniform float skin_opacity : hint_range(0.0, 2.0) = 1.0;
uniform bool use_normal_texture = true;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float normal_strength : hint_range(0.0, 16.0) = 1.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);

varying vec3 dao_vertex_probe_lighting;
varying vec3 dao_world_position;
varying vec3 dao_world_normal;
varying vec3 dao_world_tangent;
varying vec3 dao_world_binormal;

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

vec3 dao_point_diffuse(vec4 light_position, vec4 light_color, vec3 normal) {
	vec3 delta = light_position.xyz - dao_world_position;
	float distance_to_light = length(delta);
	if (light_position.w <= 0.0 || distance_to_light >= light_position.w) {
		return vec3(0.0);
	}
	vec3 light_direction = delta / max(distance_to_light, 0.000001);
	float ndotl = max(dot(normal, light_direction), 0.0);
	if (ndotl <= 0.0) {
		return vec3(0.0);
	}
	float radial = clamp(distance_to_light / light_position.w, 0.0, 1.0);
	float attenuation = cos(radial * 1.5707963267948966);
	attenuation *= attenuation;
	return light_color.rgb * attenuation * ndotl;
}

void vertex() {
	UV = UV * uv1_scale.xy + uv1_offset.xy;
	dao_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	dao_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	dao_world_tangent = normalize((MODEL_MATRIX * vec4(TANGENT, 0.0)).xyz);
	dao_world_binormal = normalize((MODEL_MATRIX * vec4(BINORMAL, 0.0)).xyz);
	// Char1.vsh writes SH irradiance plus the character-light constant to
	// COLOR1 before Ch1ArmTnt.psh adds its point-light diffuse.
	dao_vertex_probe_lighting = dao_probe_irradiance(dao_world_normal) + dao_character_light;
}

void fragment() {
	vec4 base = texture(texture_albedo, UV) * albedo;
	vec4 tint_mask = texture(texture_tint_mask, UV);
	base.rgb *= mix(vec3(1.0), skin_diffuse, clamp(tint_mask.a * skin_opacity, 0.0, 1.0));

	vec3 sampled_normal = use_normal_texture
		? texture(texture_normal, UV).rgb
		: vec3(0.5, 0.5, 1.0);
	vec3 tangent_normal = normalize(sampled_normal * 2.0 - 1.0);
	vec3 world_normal = normalize(mat3(dao_world_tangent, dao_world_binormal, dao_world_normal) * tangent_normal);
	vec3 authored_lighting = dao_vertex_probe_lighting;
	if (dao_point_count > 0) authored_lighting += dao_point_diffuse(dao_point_position_0, dao_point_color_0, world_normal);
	if (dao_point_count > 1) authored_lighting += dao_point_diffuse(dao_point_position_1, dao_point_color_1, world_normal);
	if (dao_point_count > 2) authored_lighting += dao_point_diffuse(dao_point_position_2, dao_point_color_2, world_normal);

	ALBEDO = base.rgb * max(authored_lighting, vec3(0.0));
	ROUGHNESS = roughness;
	SPECULAR = specular;
	METALLIC = metallic;
	NORMAL_MAP = sampled_normal;
	NORMAL_MAP_DEPTH = normal_strength;
}
