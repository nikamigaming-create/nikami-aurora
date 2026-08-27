shader_type canvas_item;

// Full-screen port of hslmatrix.psh. DAO supplies three affine RGB rows and
// then runs each channel through its own 1D curve. Gamma is the analytic
// fallback until a location's retail LUT strip is present.
uniform sampler2D screen_texture : hint_screen_texture, filter_linear;
uniform vec4 matrix_r = vec4(1.0, 0.0, 0.0, 0.0);
uniform vec4 matrix_g = vec4(0.0, 1.0, 0.0, 0.0);
uniform vec4 matrix_b = vec4(0.0, 0.0, 1.0, 0.0);
uniform vec3 curve_gamma = vec3(1.0);

void fragment() {
	vec4 source = vec4(texture(screen_texture, SCREEN_UV).rgb, 1.0);
	vec3 matrix_color = vec3(dot(source, matrix_r), dot(source, matrix_g), dot(source, matrix_b));
	COLOR = vec4(pow(max(matrix_color, vec3(0.0)), max(curve_gamma, vec3(0.01))), 1.0);
}
