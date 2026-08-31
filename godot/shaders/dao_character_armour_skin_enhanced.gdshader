shader_type spatial;
render_mode depth_draw_opaque, cull_back;

// Kept for the common material-lighting binder. Enhanced presentation uses
// Godot's shaded PBR path rather than multiplying these terms into ALBEDO.
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

void vertex() {
	UV = UV * uv1_scale.xy + uv1_offset.xy;
}

void fragment() {
	vec4 base = texture(texture_albedo, UV) * albedo;
	vec4 tint_mask = texture(texture_tint_mask, UV);
	base.rgb *= mix(vec3(1.0), skin_diffuse, clamp(tint_mask.a * skin_opacity, 0.0, 1.0));
	ALBEDO = base.rgb;
	ROUGHNESS = roughness;
	SPECULAR = specular;
	METALLIC = metallic;
	NORMAL_MAP = use_normal_texture ? texture(texture_normal, UV).rgb : vec3(0.5, 0.5, 1.0);
	NORMAL_MAP_DEPTH = normal_strength;
}
