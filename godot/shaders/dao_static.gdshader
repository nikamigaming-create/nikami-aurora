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

// Haven's converted creature meshes can carry the opposite winding on their
// head/body partitions.  Back-face culling makes an otherwise loaded head
// disappear or look hollow depending on the camera angle.  Static scene and
// creature materials therefore need the same two-sided rule as skin/hair.
render_mode diffuse_burley, specular_schlick_ggx, depth_draw_opaque, cull_disabled;

// Port contract for static1.vsh/static1.psh. Godot supplies the world/view
// transforms, cascaded shadows and fog; this pass preserves DAO decal, normal,
// tint and specular-mask semantics while using Burley + GGX lighting.
uniform sampler2D decal : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D roughness_map : hint_roughness_g, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform bool has_decal = false;
uniform bool has_normal = false;
uniform bool has_specular_mask = false;
uniform bool has_roughness_map = false;
uniform bool has_metallic_map = false;
uniform float roughness_value : hint_range(0.02, 1.0) = 0.72;
uniform float specular_level : hint_range(0.0, 1.0) = 0.46;
uniform float metallic_value : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	vec4 base = has_decal ? texture(decal, UV) : vec4(1.0);
	ALBEDO = base.rgb * tint.rgb;
	EMISSION = ALBEDO * dao_probe_irradiance(dao_probe_world_normal);
	if (has_normal) {
		NORMAL_MAP = texture(normal_map, UV).rgb;
		NORMAL_MAP_DEPTH = 1.0;
	}
	float mask = has_specular_mask ? texture(specular_mask, UV).r : 1.0;
	float authored_roughness = has_roughness_map ? texture(roughness_map, UV).g : 1.0;
	float authored_metallic = has_metallic_map ? texture(metallic_map, UV).b : 1.0;
	METALLIC = clamp(metallic_value * authored_metallic, 0.0, 1.0);
	ROUGHNESS = mix(1.0, roughness_value * authored_roughness, mask);
	SPECULAR = specular_level * mask;
}
