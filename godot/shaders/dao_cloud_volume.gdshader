shader_type fog;

uniform vec3 wind_direction = vec3(0.72, 0.0, -0.38);
uniform float wind_speed = 0.85;
uniform float density_scale : hint_range(0.0, 1.0) = 0.42;
uniform vec3 cloud_albedo : source_color = vec3(0.91, 0.92, 0.94);
uniform vec3 cloud_ambient : source_color = vec3(0.025, 0.032, 0.045);

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
	float result = noise3(p) * 0.56;
	result += noise3(p * 2.03 + vec3(11.7, -4.2, 8.1)) * 0.29;
	result += noise3(p * 4.11 + vec3(-3.4, 17.3, -6.8)) * 0.15;
	return result;
}

void fog() {
	vec3 drift = wind_direction * TIME * wind_speed;
	vec3 weather_position = (WORLD_POSITION + drift) * vec3(0.021, 0.037, 0.021);
	float weather = fbm3(weather_position);
	float erosion = noise3(weather_position * 5.3 + vec3(TIME * 0.012, 0.0, TIME * -0.009));
	float cloud = smoothstep(0.27, 0.60, weather - erosion * 0.055);
	float edge_fade = smoothstep(0.0, 0.025, -SDF);
	DENSITY = cloud * density_scale * edge_fade;
	ALBEDO = mix(vec3(0.70, 0.75, 0.82), cloud_albedo, cloud * 0.62 + 0.24);
	EMISSION = (cloud_ambient + vec3(0.055, 0.060, 0.068)) * (0.18 + cloud * 0.42);
}
