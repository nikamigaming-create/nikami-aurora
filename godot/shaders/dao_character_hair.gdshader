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
varying vec3 dao_world_position;
varying vec3 dao_world_tangent;

uniform int dao_point_count = 0;
uniform vec4 dao_point_position_0 = vec4(0.0);
uniform vec4 dao_point_position_1 = vec4(0.0);
uniform vec4 dao_point_position_2 = vec4(0.0);
uniform vec4 dao_point_color_0 = vec4(0.0);
uniform vec4 dao_point_color_1 = vec4(0.0);
uniform vec4 dao_point_color_2 = vec4(0.0);

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
	dao_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	dao_world_tangent = normalize((MODEL_MATRIX * vec4(TANGENT, 0.0)).xyz);
}

render_mode unshaded, cull_disabled, depth_prepass_alpha;

// HairAlpha's mml_tPackedTexture is four linear data channels (tangent shift,
// tint weight, coverage, primary-specular mask). The installed sampler state
// never enables D3D9 sRGB decoding, so this must not be tagged source_color.
uniform sampler2D albedo_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tint_noise_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_mask : filter_linear_mipmap_anisotropic, repeat_enable;
// DAO's hair materials can still carry the ordinary glTF roughness/metallic
// maps. Keeping those channels avoids turning every non-packed hair surface
// into the same uniformly matte material after the custom strand pass.
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

vec3 dao_point_diffuse(vec4 light_position, vec4 light_color, vec3 normal) {
	vec3 delta = light_position.xyz - dao_world_position;
	float distance_to_light = length(delta);
	if (light_position.w <= 0.0 || distance_to_light >= light_position.w) {
		return vec3(0.0);
	}
	vec3 light_direction = delta / max(distance_to_light, 0.000001);
	float radial = clamp(distance_to_light / light_position.w, 0.0, 1.0);
	// Hair0.psh: sincos writes cosine to x, then squares it.
	float attenuation = cos(radial * 1.5707963267948966);
	attenuation *= attenuation;
	float wrapped_diffuse = max(dot(normal, light_direction) * 0.75 + 0.25, 0.0);
	return light_color.rgb * attenuation * wrapped_diffuse;
}

vec3 dao_character_lighting(vec3 normal) {
	vec3 lighting = dao_probe_irradiance(normal);
	if (dao_point_count > 0) lighting += dao_point_diffuse(dao_point_position_0, dao_point_color_0, normal);
	if (dao_point_count > 1) lighting += dao_point_diffuse(dao_point_position_1, dao_point_color_1, normal);
	if (dao_point_count > 2) lighting += dao_point_diffuse(dao_point_position_2, dao_point_color_2, normal);
	return max(lighting, vec3(0.0));
}

vec3 dao_hair_lobes(vec4 light_position, vec3 normal, vec3 shifted_tangent, vec3 view_direction) {
	vec3 delta = light_position.xyz - dao_world_position;
	float distance_to_light = length(delta);
	if (light_position.w <= 0.0 || distance_to_light >= light_position.w) {
		return vec3(0.0);
	}
	vec3 light_direction = delta / max(distance_to_light, 0.000001);
	float radial = clamp(distance_to_light / light_position.w, 0.0, 1.0);
	float attenuation = cos(radial * 1.5707963267948966);
	attenuation *= attenuation;
	float diffuse_lobe = max(dot(normal, light_direction) * 0.75 + 0.25, 0.0);
	vec3 half_direction = normalize(view_direction + light_direction);
	float tangent_dot_half = dot(shifted_tangent, half_direction);
	float strand_sine = sqrt(max(1.0 - tangent_dot_half * tangent_dot_half, 0.0));
	// Hair0.psh raises the primary lobe three additional times after applying
	// the authored exponent (x^2, x^4, x^8). Secondary uses its value directly.
	float primary_lobe = pow(strand_sine, primary_specular_power * 8.0);
	float secondary_lobe = pow(strand_sine, secondary_specular_power);
	return vec3(diffuse_lobe, primary_lobe, secondary_lobe) * attenuation;
}

void fragment() {
	vec4 base = texture(albedo_texture, UV);
	vec3 hair_diffuse;
	vec3 hair_specular;
	if (packed_bioware) {
		// Port of installed Hair0.psh.fxo instructions:
		//   noise * Diffuse1 + packed.g * Diffuse0 +
		//   (1 - packed.g) * Diffuse2. Packed B is strand coverage.
		float noise_value = texture(tint_noise_texture, UV * noise_tiling).r;
		hair_diffuse = noise_value * diffuse_tint_1 +
			base.g * diffuse_tint_0 + (1.0 - base.g) * diffuse_tint_2;
		hair_specular = noise_value * specular_tint_1 +
			base.g * specular_tint_0 + (1.0 - base.g) * specular_tint_2;
		ALPHA = base.b;
		float packed_x = base.r * 0.4 - 0.2;
		vec3 packed_normal = normalize(vec3(packed_x, 0.0, 1.0));
		NORMAL_MAP = packed_normal * 0.5 + 0.5;
	} else {
		hair_diffuse = base.rgb * hair_tint.rgb;
		hair_specular = vec3(1.0);
		ALPHA = base.a;
		NORMAL_MAP = texture(normal_texture, UV).xyz;
	}
	vec3 world_normal = normalize(dao_probe_world_normal);
	vec3 world_tangent = normalize(dao_world_tangent);
	vec3 shifted_tangent = normalize(world_tangent + world_normal * (base.r * 0.4 - 0.2));
	vec3 view_direction = normalize(CAMERA_POSITION_WORLD - dao_world_position);
	vec3 diffuse_lighting = dao_probe_irradiance(world_normal);
	vec3 primary_lighting = vec3(0.0);
	vec3 secondary_lighting = vec3(0.0);
	if (dao_point_count > 0) {
		vec3 lobes = dao_hair_lobes(dao_point_position_0, world_normal, shifted_tangent, view_direction);
		vec3 radiance = dao_point_color_0.rgb;
		diffuse_lighting += radiance * lobes.x;
		primary_lighting += radiance * lobes.y;
		secondary_lighting += radiance * lobes.z;
	}
	if (dao_point_count > 1) {
		vec3 lobes = dao_hair_lobes(dao_point_position_1, world_normal, shifted_tangent, view_direction);
		vec3 radiance = dao_point_color_1.rgb;
		diffuse_lighting += radiance * lobes.x;
		primary_lighting += radiance * lobes.y;
		secondary_lighting += radiance * lobes.z;
	}
	if (dao_point_count > 2) {
		vec3 lobes = dao_hair_lobes(dao_point_position_2, world_normal, shifted_tangent, view_direction);
		vec3 radiance = dao_point_color_2.rgb;
		diffuse_lighting += radiance * lobes.x;
		primary_lighting += radiance * lobes.y;
		secondary_lighting += radiance * lobes.z;
	}
	vec3 strand_specular = primary_lighting * primary_specular_mask +
		secondary_lighting * hair_specular;
	ALBEDO = hair_diffuse * max(diffuse_lighting, vec3(0.0)) + base.a * strand_specular;
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
