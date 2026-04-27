# Changelog

All notable changes to this package are documented here.

## [1.2.0] - 2026-04-27

### Added
- **Zone Mask occlusion.** Reference a BoxCollider on the broadcaster to bound where the screen's reflections can land. Cheap analytic test in the shader; the recommended primary tool for keeping reflections out of adjacent rooms.
- **Probe Volume occlusion.** New `VideoAreaLightProbeVolume` component bakes a 3D visibility texture for intra-room geometry a Zone Mask can't represent. Up to 4 cascade per scene, contributing multiplicatively where their bounds cover a fragment.
- **Static prop shadows.** Any collider inside a Probe Volume's bounds casts a baked shadow on receiving surfaces — no per-object setup.
- Visibility baker — `Tools > VideoAreaLight > Bake Visibility` (all volumes) and a "Bake This Volume" button on each volume's inspector. Uses Unity's Jobs system (`RaycastCommand.ScheduleBatch`) for parallel raycasts.
- Custom inspectors for `VideoAreaLightSource` and `VideoAreaLightProbeVolume`. The broadcaster shows an empty-state prompt with a "Create Zone Mask" button and a scene-wide listing of probe volumes with an "Add Probe Volume" button.
- `GameObject > Video Area Light > Zone Mask` and `> Probe Volume` menu entries.
- Authoring prefabs: `Runtime/Prefabs/VAL_ZoneMask.prefab` and `Runtime/Prefabs/VAL_ProbeVolume.prefab`.
- `Documentation~/Occlusion.md` — usage guide for both occlusion tools, including sizing recipes and a memory/voxel reference.
- `Documentation~/FutureIdeas.md` — forward-looking ideas not yet shipped.

### Changed
- Inspector tooltips, headers, and help text refreshed across both components.

### Removed
- `Runtime/Prefab/VideoAreaLight.prefab` (broadcaster prefab). Add the `VideoAreaLightSource` component directly to your screen mesh GameObject; the new prefabs at `Runtime/Prefabs/` cover the optional Zone Mask and Probe Volume.

## [1.1.0] - 2026-04-26

### Added
- **Poiyomi Pro Modular Shader integration.** Existing Poiyomi materials can receive light from a VideoAreaLight screen without switching shader.
- Two-step setup via the Tools menu: install the module once, then apply it to chosen materials. Confirmation dialog before each shader regeneration; multi-select supported via the Project window. Uninstall through the same menu.
- Per-material enable toggle (default off), diffuse/specular multipliers, and cookie checkbox.
- Integration menu items auto-hide when Poiyomi isn't installed.
- `Documentation~/PoiyomiIntegration.md` full guide; `Samples~/PoiyomiIntegration/` template for inspection or forking.
- Package Manager `documentationUrl`, `changelogUrl`, `licensesUrl` metadata.

## [1.0.0] - 2026-04-25

### Added
- Initial release.
- `VideoAreaLightSource` broadcaster — reads the video render texture's average colour and drives the per-pixel area-light effect via global shader uniforms.
- `VideoAreaLight/Lit` drop-in shader mirroring URP/Lit's property block, so material conversion is painless.
- Core lighting maths in `RectAreaLight.hlsl` — Karis MRP specular with a roughness-widened GGX lobe and analytic polygon irradiance for diffuse. No precomputed lookup textures or third-party data.
- Custom material inspector adds a Use Video Cookie toggle on top of URP/Lit's standard fields.
- Shader Graph entry point (`VideoAreaLight_SG.hlsl`) as an alternative to the drop-in shader.
- Pre-configured `Prefab/VideoAreaLight.prefab` with sensible defaults.
- `ExampleScene` sample demonstrating the effect on glossy walls and a wet-club-style floor.
- MIT license.
