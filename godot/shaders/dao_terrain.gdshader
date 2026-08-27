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

render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D palette : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D palette_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D mask_v : filter_nearest, repeat_disable;
uniform sampler2D mask_a : filter_linear, repeat_disable;
uniform sampler2D mask_a2 : filter_linear, repeat_disable;
uniform vec4 pal_dim = vec4(0.5, 0.25, 2.0, 4.0);
uniform vec4 pal_param = vec4(0.0625, 0.03125, 0.375, 0.1875);
uniform vec4 uv_scales0 = vec4(24.0);
uniform vec4 uv_scales1 = vec4(24.0);

int material_index(float encoded) {
    return clamp(int(floor(encoded * 7.5 + 0.5)), 0, 7);
}

float material_weight(int index, vec4 low_weights, vec4 high_weights) {
    return index < 4 ? low_weights[index] : high_weights[index - 4];
}

float material_scale(int index) {
    return index < 4 ? uv_scales0[index] : uv_scales1[index - 4];
}

vec2 palette_uv(float encoded_index, float scale, vec2 base_uv) {
    float major = floor(encoded_index * pal_dim.z);
    float minor = floor(fract(encoded_index * pal_dim.z) * pal_dim.w);
    vec2 cell_offset = vec2(major * pal_dim.x + pal_param.x, minor * pal_dim.y + pal_param.y);
    return fract(scale * base_uv) * pal_param.zw + cell_offset;
}

void fragment() {
    // Haven's glTF writer already performs the V conversion used by DAO's
    // renderer. Flipping the decoded DDS masks again mirrors every patch.
    vec2 base_uv = UV;
    vec3 encoded = texture(mask_v, base_uv).rgb;
    vec4 low_weights = texture(mask_a, base_uv);
    vec4 high_weights = texture(mask_a2, base_uv);
    int indices[3] = {material_index(encoded.r), material_index(encoded.g), material_index(encoded.b)};
    float weights[3] = {
        material_weight(indices[0], low_weights, high_weights),
        material_weight(indices[1], low_weights, high_weights),
        material_weight(indices[2], low_weights, high_weights)
    };
    float weight_sum = max(weights[0] + weights[1] + weights[2], 0.0001);
    vec4 albedo = vec4(0.0);
    vec2 normal_xy = vec2(0.0);
    for (int channel = 0; channel < 3; channel++) {
        float weight = weights[channel] / weight_sum;
        vec2 atlas_uv = palette_uv(encoded[channel], material_scale(indices[channel]), base_uv);
        vec4 diffuse_sample = texture(palette, atlas_uv);
        vec4 normal_sample = texture(palette_normal, atlas_uv);
        albedo += diffuse_sample * weight;
        // DAO terrain palettes store tangent X in alpha and tangent Y in green.
        normal_xy += vec2(normal_sample.a, normal_sample.g) * weight;
    }
    vec2 signed_xy = normal_xy * 2.0 - 1.0;
    float normal_z = sqrt(max(1.0 - dot(signed_xy, signed_xy), 0.0));
	ALBEDO = albedo.rgb;
	EMISSION = ALBEDO * dao_probe_irradiance(dao_probe_world_normal);
    NORMAL_MAP = vec3(signed_xy, normal_z) * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = 1.0;
    METALLIC = 0.0;
    ROUGHNESS = mix(0.92, 0.38, clamp(albedo.a, 0.0, 1.0));
}
