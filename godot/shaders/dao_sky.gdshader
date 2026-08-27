shader_type sky;

// Godot port of DAO sky.vsh/sky.psh + clouds.psh. DAO forces its dome to the
// far plane in the vertex pass; a Godot sky material already has that contract.
uniform vec3 zenith_color = vec3(0.055, 0.135, 0.275);
uniform vec3 horizon_color = vec3(0.34, 0.44, 0.56);
uniform vec3 fog_color = vec3(0.16, 0.19, 0.16);
uniform vec3 sun_color = vec3(1.0, 0.76, 0.42);
uniform vec3 sun_direction = vec3(0.0, 0.7, 0.7);
uniform vec3 atmosphere_sun_color = vec3(0.74, 0.32, 0.14);
uniform float atmosphere_sun_intensity = 17.0;
uniform float turbidity = 1.0;
uniform float rayleigh_multiplier = 1.0;
uniform float mie_multiplier = 1.0;
uniform float phase_eccentricity = 0.85;
uniform float earth_reflectance = 1.0;
uniform float distance_multiplier = 1.0;
uniform float atmosphere_alpha = 1.0;
uniform float moon_scale = 1.0;
uniform float moon_alpha = 0.5;
uniform vec3 cloud_color = vec3(0.8);
uniform float authored_cloud_density = 0.5;
uniform float authored_cloud_sharpness = 0.5;
uniform float authored_cloud_depth = 0.0;
uniform float cloud_range_multiplier_1 = 2.0;
uniform float cloud_range_multiplier_2 = 2.0;
uniform float fog_cap = 1.0;
uniform float fog_intensity = 1.0;
uniform float fog_zenith = 1.0;
uniform sampler2D sun_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform bool use_sun_texture = false;
uniform float sun_texture_half_angle : hint_range(0.005, 0.1) = 0.028;
uniform vec3 rayleigh_coeff = vec3(0.30, 0.47, 0.82);
uniform vec3 mie_coeff = vec3(0.12, 0.10, 0.08);
uniform float mie_g = 0.76;
uniform float fog_floor : hint_range(0.0, 1.0) = 0.12;
uniform float cloud_coverage : hint_range(0.0, 1.0) = 0.58;
uniform float cloud_density : hint_range(0.0, 2.0) = 0.92;

float fast_atan(float x) {
	float t = x <= 1.0 ? x : 1.0 / x;
	float t2 = t * t;
	float p = t2 * 0.0208350997 - 0.0851330012;
	p = t2 * p + 0.180141002;
	p = t2 * p - 0.330299497;
	p = t2 * p + 0.999866009;
	float result = t * p;
	return x <= 1.0 ? result : 1.5707963268 - result;
}

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float noise2(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash21(i), hash21(i + vec2(1.0, 0.0)), f.x),
		mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0)), f.x), f.y);
}

float fbm(vec2 p) {
	float value = 0.0;
	float amplitude = 0.52;
	mat2 octave = mat2(vec2(1.67, 1.13), vec2(-1.13, 1.67));
	for (int i = 0; i < 5; i++) {
		value += noise2(p) * amplitude;
		p = octave * p + vec2(7.1, -3.8);
		amplitude *= 0.5;
	}
	return value;
}

void sky() {
	vec3 view_dir = normalize(EYEDIR);
	vec3 to_sun = normalize(sun_direction);
	float elevation = view_dir.y;
	float cos_theta = dot(view_dir, to_sun);

	// Direct Godot port of Haven's proven SKY_PS Hoffman-Preetham path. Every
	// coefficient below comes from the installed ARL ATMO fields exported in the
	// havenarea manifest; no presentation tuning is applied here.
	float rayleigh_mult = max(rayleigh_multiplier, 0.0);
	float mie_mult = max(mie_multiplier, 0.0);
	float source_turbidity = max(turbidity, 1.0);
	float g = clamp(phase_eccentricity, -0.99, 0.99);
	vec3 beta_r = vec3(0.0058, 0.0135, 0.0331) * rayleigh_mult;
	float bm = 0.0040 * mie_mult * source_turbidity;
	vec3 beta_m = vec3(bm);
	vec3 beta_t = max(beta_r + beta_m, vec3(0.00001));
	float path_length = 8.0 * max(distance_multiplier, 0.0) / (max(elevation, 0.0) + 0.15);
	vec3 extinction = exp(-beta_t * path_length);
	float phase_r = 0.75 * (1.0 + cos_theta * cos_theta);
	float g2 = g * g;
	float phase_m = (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cos_theta, 0.0001), 1.5));
	vec3 esun = atmosphere_sun_color * atmosphere_sun_intensity;
	vec3 inscatter = (beta_r * phase_r + beta_m * phase_m) / beta_t * esun * (vec3(1.0) - extinction);
	vec3 physical_sky = (vec3(1.0) - exp(-inscatter * 0.35)) * max(atmosphere_alpha, 0.0);
	physical_sky += fog_color * max(earth_reflectance, 0.0) * max(1.0 - elevation, 0.0) * 0.015;

	if (use_sun_texture && cos_theta > 0.9) {
		vec3 sun_right = normalize(cross(abs(to_sun.y) > 0.98 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0), to_sun));
		vec3 sun_up = normalize(cross(to_sun, sun_right));
		vec2 sun_uv = vec2(dot(view_dir, sun_right), dot(view_dir, sun_up)) / (2.0 * sun_texture_half_angle) + vec2(0.5);
		float inside = step(0.0, sun_uv.x) * step(sun_uv.x, 1.0) * step(0.0, sun_uv.y) * step(sun_uv.y, 1.0);
		float authored_sun = texture(sun_texture, clamp(sun_uv, vec2(0.0), vec2(1.0))).r * inside;
		physical_sky += vec3(authored_sun);
	}
	float moon_cos = dot(view_dir, -to_sun);
	float moon_radius = clamp(0.004 * max(moon_scale, 0.0), 0.001, 0.12);
	float moon_disc = smoothstep(1.0 - moon_radius, 1.0 - moon_radius * 0.25, moon_cos);
	physical_sky += vec3(0.62, 0.68, 0.78) * moon_disc * max(moon_alpha, 0.0);

	if (authored_cloud_density > 0.01 && elevation > -0.05) {
		float t = 800.0 / max(elevation, 0.01);
		vec2 cloud_range = max(vec2(cloud_range_multiplier_1, cloud_range_multiplier_2), vec2(0.0001));
		vec2 cloud_uv = view_dir.xz * t * 0.0005 / cloud_range;
		cloud_uv.x += TIME * authored_cloud_depth * 0.0001;
		float cloud_noise = fbm(cloud_uv * 3.0);
		float cloud_shape = smoothstep(1.0 - authored_cloud_density,
			1.0 - authored_cloud_density * authored_cloud_sharpness, cloud_noise)
			* smoothstep(-0.05, 0.15, elevation);
		float cloud_light = clamp(to_sun.y * 0.5 + 0.5, 0.0, 1.0);
		vec3 lit_cloud = mix(cloud_color * 0.5, cloud_color + atmosphere_sun_color * 0.3, cloud_light);
		physical_sky = mix(physical_sky, lit_cloud, cloud_shape * 0.85);
	}
	if (elevation < 0.0) {
		physical_sky = mix(physical_sky, fog_color * 0.3, clamp(-elevation * 3.0, 0.0, 1.0));
	}
	float horizon_fog = clamp((fog_zenith - max(elevation, 0.0)) * max(fog_intensity, 0.0), 0.0, max(fog_cap, 0.0));
	physical_sky = mix(physical_sky, fog_color, horizon_fog);
	vec3 color = physical_sky;

	// Interleaved-gradient-style dither; kept tiny because output is HDR.
	float dither = (hash21(view_dir.xz * 8192.0 + fract(TIME) * 37.0) - 0.5) / 255.0;
	COLOR = max(color + vec3(dither), vec3(0.0));
}
