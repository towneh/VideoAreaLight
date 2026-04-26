# Changelog

All notable changes to this package are documented here.

## [1.1.0] - 2026-04-26

### Added
- **Poiyomi Pro Modular Shader integration.** Existing Poiyomi-shaded materials can now receive `VideoAreaLightSource` contribution without switching shader.
- Two-step setup: `Tools > VideoAreaLight > Install Poiyomi Module` (one-time, fast), then right-click a material → `VideoAreaLight - Apply to Material's Shader`. A confirmation dialog shows the actual blast radius before regenerating. Multi-select supported via the Project window. Uninstall via the same Tools menu.
- Per-material toggle (default OFF) plus diffuse/specular multipliers and cookie checkbox.
- Integration menu items auto-hide in projects where Poiyomi isn't installed.
- `Documentation~/PoiyomiIntegration.md` with the full guide; `Samples~/PoiyomiIntegration/` for inspecting or forking the template.
- Package Manager `documentationUrl` / `changelogUrl` / `licensesUrl` metadata.

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
