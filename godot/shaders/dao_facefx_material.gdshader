shader_type spatial;
render_mode unshaded, depth_draw_opaque, cull_disabled;

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
// Face.mat g_vScatterData default. The installed female/elf face MAOs do not
// override it, so this is the exact constant consumed as Face0.psh c51.
uniform vec4 dao_scatter_data = vec4(1.0, 0.2, 0.2, 0.2);

varying vec3 dao_vertex_probe_lighting;
varying vec3 dao_world_position;
varying vec3 dao_world_normal;
varying vec3 dao_world_tangent;
varying vec3 dao_world_binormal;

// Preserve the PBR inputs imported from the installed game's face material.
// FaceFX contributes only the authored emotion-normal layer; it must not swap
// in a different skin-lighting model or change the actor's diffuse response.
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform bool use_roughness_texture = false;
uniform sampler2D texture_roughness : hint_roughness_g, filter_linear_mipmap, repeat_enable;
uniform bool use_metallic_texture = false;
uniform sampler2D texture_metallic : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_normal_texture = true;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float normal_strength : hint_range(0.0, 16.0) = 1.0;

// Installed Face0.psh tint-mask contract. R/G/B select lips, eyeshadow and
// blush. Alpha applies the skin multiplier. RGB zones are replacement blends;
// skin is multiplicative before those blends.
uniform bool use_face_tint = false;
uniform sampler2D texture_tint_mask : filter_linear_mipmap, repeat_enable;
uniform vec3 tint_diffuse_0 = vec3(1.0);
uniform vec3 tint_diffuse_1 = vec3(1.0);
uniform vec3 tint_diffuse_2 = vec3(1.0);
uniform vec3 tint_diffuse_3 = vec3(1.0);
uniform vec3 tint_specular_0 = vec3(1.0);
uniform vec3 tint_specular_1 = vec3(1.0);
uniform vec3 tint_specular_2 = vec3(1.0);
uniform vec3 tint_specular_3 = vec3(1.0);
uniform vec4 tint_diffuse_opacity = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 tint_specular_opacity = vec4(0.0, 0.0, 0.0, 1.0);

uniform bool use_facefx_emotions = true;
// Face0.psh reads the installed DXT5nm perturbation from alpha/green and the
// two four-channel masks as raw data, so none of these use normal-map hints.
uniform sampler2D texture_emotions_mask0 : filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_emotions_mask1 : filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_emotions_normal : filter_linear_mipmap, repeat_enable;
uniform vec4 mml_fEmotionsMapWeight0 = vec4(0.0);
uniform vec4 mml_fEmotionsMapWeight1 = vec4(0.0);

uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);

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
	float radial = clamp(distance_to_light / light_position.w, 0.0, 1.0);
	float attenuation = cos(radial * 1.5707963267948966);
	attenuation *= attenuation;
	float ndotl = dot(normal, light_direction);
	if (ndotl <= 0.0) {
		return vec3(0.0);
	}
	// Face0.psh lines 226..240: compare a shifted smoothstep to the ordinary
	// N.L smoothstep, tint that difference by g_vScatterData.rgb * 0.5, then
	// add it back to N.L before multiplying the point-light RGB.
	float shifted = clamp((ndotl + dao_scatter_data.w) / (1.0 + dao_scatter_data.w), 0.0, 1.0);
	float shifted_curve = shifted * shifted * (3.0 - 2.0 * shifted);
	float direct = clamp(ndotl, 0.0, 1.0);
	float direct_curve = direct * direct * (3.0 - 2.0 * direct);
	vec3 scatter = clamp((shifted_curve - direct_curve) * dao_scatter_data.rgb * 0.5,
		vec3(0.0), vec3(1.0));
	return light_color.rgb * attenuation * (vec3(ndotl) + scatter);
}

void vertex() {
	UV = UV * uv1_scale.xy + uv1_offset.xy;
	dao_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	dao_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	dao_world_tangent = normalize((MODEL_MATRIX * vec4(TANGENT, 0.0)).xyz);
	dao_world_binormal = normalize((MODEL_MATRIX * vec4(BINORMAL, 0.0)).xyz);
	// Face1.vsh writes this exact SH matrix evaluation plus g_vLightParams[4]
	// to COLOR1; Face0.psh adds its point diffuse before tint multiplication.
	dao_vertex_probe_lighting = dao_probe_irradiance(dao_world_normal) + dao_character_light;
}

void fragment() {
	vec4 base = texture(texture_albedo, UV) * albedo;
	float authored_specular = specular;
	if (use_face_tint) {
		vec4 tint_mask = texture(texture_tint_mask, UV);
		vec3 diffuse_weights = tint_mask.rgb * tint_diffuse_opacity.rgb;
		float diffuse_sum = clamp(dot(diffuse_weights, vec3(1.0)), 0.0, 1.0);
		vec3 diffuse_zones = tint_diffuse_0 * diffuse_weights.r
			+ tint_diffuse_1 * diffuse_weights.g
			+ tint_diffuse_2 * diffuse_weights.b;
		// Face0.psh uses mask alpha directly for the fourth skin zone.
		base.rgb *= mix(vec3(1.0), tint_diffuse_3, tint_mask.a);
		base.rgb = mix(base.rgb, diffuse_zones, diffuse_sum);

		vec3 specular_weights = tint_mask.rgb * tint_specular_opacity.rgb;
		float specular_sum = clamp(dot(specular_weights, vec3(1.0)), 0.0, 1.0);
		vec3 specular_zones = tint_specular_0 * specular_weights.r
			+ tint_specular_1 * specular_weights.g
			+ tint_specular_2 * specular_weights.b;
		vec3 specular_rgb = vec3(specular)
			* mix(vec3(1.0), tint_specular_3, tint_mask.a);
		specular_rgb = mix(specular_rgb, specular_zones, specular_sum);
		authored_specular = clamp(dot(specular_rgb, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
	}
	vec3 face_normal = use_normal_texture
		? texture(texture_normal, UV).rgb
		: vec3(0.5, 0.5, 1.0);
	if (use_facefx_emotions) {
		float emotion_weight = dot(texture(texture_emotions_mask0, UV), mml_fEmotionsMapWeight0)
			+ dot(texture(texture_emotions_mask1, UV), mml_fEmotionsMapWeight1);
		vec4 emotion_normal = texture(texture_emotions_normal, UV);
		face_normal.xy += (emotion_normal.ag - vec2(0.5)) * emotion_weight;
	}
	vec3 tangent_normal = normalize(face_normal * 2.0 - 1.0);
	vec3 world_normal = normalize(mat3(dao_world_tangent, dao_world_binormal, dao_world_normal) * tangent_normal);
	vec3 authored_lighting = dao_vertex_probe_lighting;
	if (dao_point_count > 0) authored_lighting += dao_point_diffuse(dao_point_position_0, dao_point_color_0, world_normal);
	if (dao_point_count > 1) authored_lighting += dao_point_diffuse(dao_point_position_1, dao_point_color_1, world_normal);
	if (dao_point_count > 2) authored_lighting += dao_point_diffuse(dao_point_position_2, dao_point_color_2, world_normal);
	ALBEDO = base.rgb * max(authored_lighting, vec3(0.0));
	ROUGHNESS = roughness * (use_roughness_texture ? texture(texture_roughness, UV).g : 1.0);
	SPECULAR = authored_specular;
	METALLIC = metallic * (use_metallic_texture ? texture(texture_metallic, UV).r : 1.0);
	NORMAL_MAP = face_normal;
	NORMAL_MAP_DEPTH = normal_strength;
}
