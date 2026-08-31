shader_type spatial;
render_mode depth_draw_opaque, cull_disabled;

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
uniform vec4 dao_scatter_data = vec4(1.0, 0.2, 0.2, 0.2);

uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform bool use_roughness_texture = false;
uniform sampler2D texture_roughness : hint_roughness_g, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_texture = false;
uniform sampler2D texture_metallic : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_texture = true;
uniform sampler2D texture_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float normal_strength : hint_range(0.0, 16.0) = 1.0;

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
uniform sampler2D texture_emotions_mask0 : filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_emotions_mask1 : filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_emotions_normal : filter_linear_mipmap, repeat_enable;
uniform vec4 mml_fEmotionsMapWeight0 = vec4(0.0);
uniform vec4 mml_fEmotionsMapWeight1 = vec4(0.0);
uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);

void vertex() {
	UV = UV * uv1_scale.xy + uv1_offset.xy;
}

void fragment() {
	vec4 base = texture(texture_albedo, UV) * albedo;
	float authored_specular = specular;
	if (use_face_tint) {
		vec4 mask = texture(texture_tint_mask, UV);
		vec3 diffuse_weights = mask.rgb * tint_diffuse_opacity.rgb;
		float diffuse_sum = clamp(dot(diffuse_weights, vec3(1.0)), 0.0, 1.0);
		vec3 diffuse_zones = tint_diffuse_0 * diffuse_weights.r + tint_diffuse_1 * diffuse_weights.g + tint_diffuse_2 * diffuse_weights.b;
		base.rgb *= mix(vec3(1.0), tint_diffuse_3, mask.a);
		base.rgb = mix(base.rgb, diffuse_zones, diffuse_sum);
		vec3 specular_weights = mask.rgb * tint_specular_opacity.rgb;
		float specular_sum = clamp(dot(specular_weights, vec3(1.0)), 0.0, 1.0);
		vec3 specular_zones = tint_specular_0 * specular_weights.r + tint_specular_1 * specular_weights.g + tint_specular_2 * specular_weights.b;
		vec3 specular_rgb = vec3(specular) * mix(vec3(1.0), tint_specular_3, mask.a);
		specular_rgb = mix(specular_rgb, specular_zones, specular_sum);
		authored_specular = clamp(dot(specular_rgb, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
	}
	vec3 face_normal = use_normal_texture ? texture(texture_normal, UV).rgb : vec3(0.5, 0.5, 1.0);
	if (use_facefx_emotions) {
		float weight = dot(texture(texture_emotions_mask0, UV), mml_fEmotionsMapWeight0)
			+ dot(texture(texture_emotions_mask1, UV), mml_fEmotionsMapWeight1);
		face_normal.xy += (texture(texture_emotions_normal, UV).ag - vec2(0.5)) * weight;
	}
	ALBEDO = base.rgb;
	ROUGHNESS = roughness * (use_roughness_texture ? texture(texture_roughness, UV).g : 1.0);
	SPECULAR = authored_specular;
	METALLIC = metallic * (use_metallic_texture ? texture(texture_metallic, UV).r : 1.0);
	NORMAL_MAP = face_normal;
	NORMAL_MAP_DEPTH = normal_strength;
}
