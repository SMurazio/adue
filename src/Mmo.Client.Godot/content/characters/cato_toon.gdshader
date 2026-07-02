shader_type spatial;
// ambient_light_disabled: Godot's renderer otherwise ADDS the environment ambient on top of our banding, which
// washes the fur pale and lifts the dark rim until the outline disappears. Blender's Toon BSDF has no such ambient,
// so disabling it here lets the cel bands + Fresnel rim fully control the look (matching the Blender reference).
render_mode specular_disabled, ambient_light_disabled;

// Cato toon material, ported from the Blender node graph:
//   Toon BSDF (cel-banded albedo)  +  Fresnel -> Color Ramp -> Mix,
// but the Fresnel drives a DARK rim colour (a soft outline) instead of an emissive glow.
// Everything is a uniform so it's tweakable LIVE in Godot's shader editor (viewport updates as you drag).

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;

// --- Cel banding (the Toon BSDF) --------------------------------------------------------------
// N·L below band_threshold reads the shadow tone (lifted to shadow_floor so it never goes black);
// above it reads full-lit. The narrow smoothstep keeps the band edge crisp but anti-aliased.
uniform float band_threshold : hint_range(0.0, 1.0) = 0.4;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.6;

// --- Fresnel dark rim (the outline look) ------------------------------------------------------
// Darkest at the grazing silhouette, fading toward the camera-facing centre. rim_power = edge
// falloff (higher = thinner, tighter rim); rim_strength = how far the edge tints toward rim_color.
uniform vec3 rim_color : source_color = vec3(0.10, 0.07, 0.05);
uniform float rim_power : hint_range(0.5, 8.0) = 4.0;
uniform float rim_strength : hint_range(0.0, 1.0) = 0.85;

void fragment() {
	vec3 tex = texture(albedo_tex, UV).rgb;
	// Fresnel: 0 where the surface faces the camera, ->1 at the grazing edge. NORMAL and VIEW are
	// both view-space in the fragment stage, so dot(NORMAL, VIEW) is the facing ratio.
	float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), rim_power);
	ALBEDO = mix(tex, rim_color, fresnel * rim_strength);
}

void light() {
	float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
	float lit = smoothstep(band_threshold - 0.03, band_threshold + 0.03, ndotl);
	float band = mix(shadow_floor, 1.0, lit);
	DIFFUSE_LIGHT += ALBEDO * band * ATTENUATION;
}
