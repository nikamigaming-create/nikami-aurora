shader_type fog;

uniform float density_scale : hint_range(0.0, 1.0) = 0.42;
uniform vec3 cloud_albedo : source_color = vec3(0.91, 0.92, 0.94);
uniform vec3 cloud_ambient : source_color = vec3(0.025, 0.032, 0.045);
uniform float cloud_base_height = 35.0;
uniform float cloud_layer_thickness = 70.0;
uniform float authored_cloud_sharpness = 0.5;
uniform float authored_cloud_depth = 0.0;
uniform float cloud_range_1 = 1.0;
uniform float cloud_range_2 = 1.0;

float hash31(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash31(i);
	float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash31(i + vec3(1.0));
	float near_plane = mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y);
	float far_plane = mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y);
	return mix(near_plane, far_plane, f.z);
}

float fbm3(vec3 p) {
	float result = noise3(p) * 0.50;
	result += noise3(p * 2.03 + vec3(11.7, -4.2, 8.1)) * 0.26;
	result += noise3(p * 4.11 + vec3(-3.4, 17.3, -6.8)) * 0.15;
	result += noise3(p * 8.17 + vec3(5.8, -9.1, 13.6)) * 0.09;
	return result;
}

void fog() {
	vec3 source_range = vec3(max(cloud_range_1, 0.0001),
		max(1.0 + abs(authored_cloud_depth) * 0.01, 0.0001),
		max(cloud_range_2, 0.0001));
	vec3 weather_position = WORLD_POSITION * vec3(0.021, 0.030, 0.021) / source_range;
	vec3 domain_warp = vec3(
		noise3(weather_position * 0.57 + vec3(17.2, -3.1, 5.7)),
		noise3(weather_position * 0.51 + vec3(-8.4, 11.9, 2.6)),
		noise3(weather_position * 0.63 + vec3(3.3, 7.8, -12.1))) - vec3(0.5);
	float macro_weather = fbm3(weather_position + domain_warp * 1.85);
	float billow = fbm3(weather_position * 2.4 + domain_warp * 0.9);
	float erosion = fbm3(weather_position * 6.7 + vec3(23.1, -14.7, 9.8));
	float sharpness = clamp(authored_cloud_sharpness, 0.0001, 1.0);
	float authored_shape = mix(macro_weather, macro_weather * 0.72 + billow * 0.28,
		clamp(abs(authored_cloud_depth) * 0.01, 0.0, 1.0));
	authored_shape -= (1.0 - erosion) * mix(0.16, 0.055, sharpness);
	float threshold = clamp(0.52 - density_scale * 1.35, 0.22, 0.54);
	float cloud = smoothstep(threshold, threshold + mix(0.22, 0.045, sharpness),
		authored_shape);
	float normalized_height = (WORLD_POSITION.y - cloud_base_height) / max(cloud_layer_thickness, 0.01);
	float base_profile = smoothstep(0.0, 0.11, normalized_height);
	float top_profile = 1.0 - smoothstep(0.64 + macro_weather * 0.13, 1.0, normalized_height);
	float height_profile = base_profile * top_profile;
	// AuthoredCloudVolume uses FogVolumeShape.World. A world volume has no
	// finite local boundary to feather, so SDF produced large interpolated
	// triangular wedges in the sky. Height and source noise define the layer.
	DENSITY = cloud * density_scale * height_profile;
	float interior = clamp(cloud * height_profile, 0.0, 1.0);
	ALBEDO = mix(cloud_albedo * 0.72, cloud_albedo, 0.38 + interior * 0.62);
	EMISSION = cloud_ambient * (0.08 + interior * 0.16);
}
