shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled;

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

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float opacity_multiplier = 1.0;
uniform float alpha_threshold : hint_range(0.0, 1.0) = 0.0784313725490196;
uniform float mip_bias = -2.5;
uniform float roughness : hint_range(0.0, 1.0) = 1.0;
uniform float specular : hint_range(0.0, 1.0) = 0.0;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec4 diffuse = texture(albedo_texture, UV, mip_bias);
	ALBEDO = diffuse.rgb;
	ALPHA = diffuse.a * opacity_multiplier;
	ALPHA_SCISSOR_THRESHOLD = alpha_threshold;
	ROUGHNESS = roughness;
	SPECULAR = specular;
	METALLIC = metallic;
}
