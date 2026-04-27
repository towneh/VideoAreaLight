# VideoAreaLight — Poiyomi Pro integration guide

This guide covers the Poiyomi Pro Modular Shader integration that ships with the VideoAreaLight package. It's relevant **only** if you use [Poiyomi Pro](https://www.poiyomi.com/) and want existing Poiyomi-shaded materials (avatars, props) to receive contribution from a `VideoAreaLightSource` broadcaster — same effect as the dedicated `VideoAreaLight/Lit` shader, but on materials you don't want to convert.

## Prerequisites

- VideoAreaLight installed (this package).
- **Poiyomi Pro** installed in the project, at any path. Modular Shader v8a or later. The integration works with both Free and Pro shader variants because `ModuleCollectionPro` nests `ModuleCollectionFree`.
- A `VideoAreaLightSource` component live in the scene driving a video render texture. The same broadcaster drives both Poiyomi materials and `VideoAreaLight/Lit` materials — globals are global, you don't need a second component.

## Two-step user flow

The integration is split into two actions so you only pay the cost of regenerating shaders for materials you actually want lit by the screen.

### Step 1 — Install (one-time per project)

`Tools → VideoAreaLight → Install Poiyomi Module`

Fast (~1 second). What it does:

- Copies the template collection (`VRLTC_VideoAreaLight.poiTemplateCollection`) from the package's `Samples~/PoiyomiIntegration/` into `[Poiyomi]/Editor/Poi_FeatureModules/VideoAreaLight/`.
- Builds a `ShaderModule` ScriptableObject (`VRLM_VideoAreaLight.asset`) in-memory via reflection and saves it next to the template.
- Registers the module in Poiyomi's `ModuleCollectionPro` so the shader compiler picks it up.

It does **not** regenerate any Poiyomi shaders yet. Existing Poiyomi materials in your project won't show the **Video Area Light** section in their inspector until you run step 2.

The installer is idempotent — re-running it is safe and won't create duplicate entries.

### Step 2 — Apply (per material, on demand)

Two equivalent entry points:

- Right-click a single material in the inspector → **VideoAreaLight - Apply to Material's Shader**.
- Select one or more materials in the Project window → right-click → **VideoAreaLight - Apply to Selected Materials' Shader(s)**.

A confirmation dialog shows the **actual blast radius**:

```
Selected materials use 1 Poiyomi shader(s). The selected SHADERS will be
regenerated, which updates the Properties block visible to every material
bound to them — not just the ones you selected:

• .poiyomi/Poiyomi Pro URP
    selected: 1, 47 other materials in this project also use it

Materials that don't have the Video Area Light toggle enabled (default OFF)
pay nothing at runtime; the section just appears in their inspector.

Continue?
```

Click **Apply** to proceed; click **Cancel** to back out without changes.

What it does internally:

- Walks the unique set of Poiyomi `ModularShader` assets the selected materials use.
- Calls `ShaderGenerator.GenerateShader(...)` (Poiyomi's own regen API) on each. This is the same thing Poiyomi's per-shader "Regenerate Shader" inspector button does.
- Triggers Unity's shader variant recompile for those shaders only.

### Step 3 — Toggle per material

Open any Poiyomi material whose shader was regenerated. Scroll to the **Video Area Light** section.

- **Video Area Light Enabled** — master toggle. Default OFF. Materials with the toggle off pay nothing at runtime; the section just appears in their inspector.
- **Sample Video Cookie for Specular** — when on, the highlight reflects the actual video content. When off, only the average broadcast colour is used.
- **Diffuse Multiplier** — scales the diffuse contribution.
- **Specular Multiplier** — scales the specular highlight.

## The mental model — *shaders* are scoped, *materials* are toggled

This is the only part that confuses people on first contact:

- Poiyomi materials reference Poiyomi shaders. Shaders are shared resources; many materials bind to one shader.
- "Apply to Material's Shader" regenerates the shader, not the material. So **every material in your project bound to that shader gains the Video Area Light section**, not just the one you right-clicked.
- The per-material decision is the toggle. A material that hasn't enabled the toggle pays nothing at runtime — the section is just present in its inspector.

If you want VAL on some Poiyomi materials but not others, just leave the toggle off on the ones you don't want. There's no need to use a different shader.

## Uninstall

`Tools → VideoAreaLight → Uninstall Poiyomi Module`

Removes the module from `ModuleCollectionPro`, deletes the asset and template files, and tries to remove the `Editor/Poi_FeatureModules/VideoAreaLight/` folder if empty.

After uninstall, materials whose shaders were regenerated while the module was installed **still contain the Video Area Light hooks** in the locked/generated text. To regenerate them clean, re-Apply to those materials' shaders — the section will simply disappear from their inspector.

## Locking and re-applying

Thry's shader optimizer ("Lock In") creates a per-material copy of the shader at `<material-folder>/OptimizedShaders/<material-name>/`. Locking captures whatever's currently in the modular shader — including the VAL hooks if you've Applied — and bakes it.

Workflow:

1. Apply to your materials' shaders.
2. Toggle Video Area Light Enabled and configure multipliers per material.
3. Optionally lock the material via Thry. The locked copy will include the VAL code with the toggle baked into its current state.

If you change a multiplier or the cookie toggle on an already-locked material, Thry handles that the same way it handles any property change on locked materials.

If we update the VAL template (because of a bug fix, etc.), you'll need to re-Apply to pull in the new template, then re-lock if relevant.

## Troubleshooting

<details>
<summary><strong>The "Video Area Light" section doesn't appear on Poiyomi materials</strong></summary>

You haven't run **Apply** yet, or you Applied to a different shader than the one this material uses. Check the material's shader name (e.g. `.poiyomi/Poiyomi Pro URP`) and Apply against a material using that shader. Or, run Install if you skipped that step.
</details>

<details>
<summary><strong>Console spams "Failed to create material drawer Helpbox with arguments '1, 2'"</strong></summary>

Not from VAL — it's a bundled-Thry / bundled-template version mismatch in Poiyomi itself: many Poiyomi feature modules declare `[Helpbox(X, Y)]` with two args, but the bundled `HelpboxDrawer` only accepts 0 or 1. Helpbox text falls back to a plain unstyled label. Symptom only, no functional impact.
</details>

<details>
<summary><strong>No contribution at runtime even though the toggle is on</strong></summary>

1. Make sure a `VideoAreaLightSource` component is active in the scene driving a video texture.
2. Check that `_VAL_Valid` is being set — if the broadcaster isn't running (component disabled / GameObject inactive / video texture unassigned), `_VAL_Valid = 0` and VAL returns zero contribution.
3. Confirm the same scene also lights a `VideoAreaLight/Lit`-shaded object correctly. If that doesn't work either, the broadcaster is the issue, not the integration.
</details>

<details>
<summary><strong>Avatar bundle (.bee) build seems to omit the section</strong></summary>

Apply must run **before** the bundle build, so the bundled shader contains the regenerated text. If you Apply after, rebuild the bundle. If the avatar's materials are locked (Thry optimizer), the bundle uses the locked copies — make sure they were locked after the most recent Apply.
</details>

<details>
<summary><strong>The menu items appear even though I don't have Poiyomi installed</strong></summary>

They shouldn't — the package hides them automatically when Poiyomi isn't installed. If they still appear after Unity reloads, please open an issue with your Unity version.
</details>

## How the integration works (under the hood)

Two artifacts:

- **`VRLTC_VideoAreaLight.poiTemplateCollection`** — a Poiyomi modular-shader template collection. The `#T#`-delimited sections inject:
  - Properties → Poiyomi keyword `LIGHTING_PROPERTIES`
  - `#pragma shader_feature_local POI_VIDEOAREALIGHT` → `SHADER_KEYWORDS`
  - Material-property variable declarations → `BASE_PROPERTY_VARIABLES_EXPOSED`
  - `#include "Packages/com.towneh.videoarealight/Runtime/Shaders/RectAreaLight.hlsl"` → `FRAGMENT_BASE_FUNCTIONS`
  - The actual `VAL_EvaluateAreaLight` call → `FRAGMENT_BASE_LIGHTING`
- **`VRLM_VideoAreaLight.asset`** — a Poiyomi `ShaderModule` ScriptableObject that binds those template sections to the keywords above.

Both files live at `[Poiyomi]/Editor/Poi_FeatureModules/VideoAreaLight/` after install. The module is registered in Poiyomi's `ModuleCollectionPro` (which itself nests `ModuleCollectionFree`, so both shader families pick it up).

The installer is reflection-only against `Poiyomi.ModularShaderSystem.*`, so the VAL package compiles cleanly with or without Poiyomi present in the project.

## Reporting issues

If you hit something this guide doesn't cover, please open an issue at <https://github.com/towneh/VideoAreaLight/issues> with:

- VAL package version
- Poiyomi version
- Unity version
- Whether the material is locked
- Console output around the symptom
- Whether the dedicated `VideoAreaLight/Lit` shader works correctly with the same broadcaster (this isolates the integration from the underlying area-light math)
