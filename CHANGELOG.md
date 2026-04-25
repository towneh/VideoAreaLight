# Changelog

All notable changes to this package are documented here.

## [1.0.0] - 2026-04-25

### Added
- Initial release.
- `VideoAreaLightSource` broadcaster MonoBehaviour. Samples the video render texture's average colour via async GPU readback and broadcasts the screen pose, average colour, intensity, and cookie matrix as global shader uniforms.
- `VideoAreaLight/Lit` drop-in shader, mirrors URP/Lit's property block so material conversion is painless.
- `RectAreaLight.hlsl` core math include - Karis MRP specular with a roughness-widened GGX lobe; analytic cosine-weighted polygon irradiance for diffuse; cookie sampling at the world-space hit point with edge-falloff masking.
- Custom material editor (`VideoAreaLightLitGUI`) that wraps URP/Lit's polished inspector and adds a "Use Video Cookie" toggle.
- Shader Graph alternative entry point (`VideoAreaLight_SG.hlsl`) for users who prefer Shader Graph over the drop-in shader.
- Pre-configured prefab at `Prefab/VideoAreaLight.prefab` with sensible defaults.
- `ExampleScene` sample demonstrating the effect on glossy walls and a wet-club-style floor.
- MIT license.
