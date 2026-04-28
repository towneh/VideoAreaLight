# VideoAreaLight

Real-time area-light reflections from a video screen, designed for Unity 6000.0+ (URP 17 & Forward+).

<img width="1218" height="800" alt="{74817138-83E9-41CC-9723-56A48F9B1788}" src="https://github.com/user-attachments/assets/4817eb48-6c7b-446b-98a8-05e282c8f9e2" />
<i>Above example running on Basis VR (Unity 6000.4.4f1) & NormalMap sourced from AreaLit Demo scene.</i>

## Overview

VideoAreaLight makes a video screen light up your scene like a real rectangular light source. Glossy surfaces — dance floors, metal walls, polished panels — pick up:

- A bright rectangular highlight that follows the screen's shape as the camera moves.
- Soft area-shaped falloff, not a spot cone.
- A blurred reflection of the video sampled inside the highlight, so the floor reflects what's playing on the screen rather than just a flat colour.

Everything runs in real time per-pixel. No precomputed textures or bundled third-party data, so the whole package is MIT-licensed and free to ship with your project.

## Installation

Install via the Unity Package Manager:

1. Open **Window → Package Manager**.
2. Click **+** → **Install package from git URL**.
3. Paste:

   ```
   https://github.com/towneh/VideoAreaLight.git?path=Packages/com.towneh.videoarealight
   ```

To try the demo scene, find **Video Area Light** in the Package Manager, expand **Samples**, and click **Import** next to *Demo*. Then run **Tools > VideoAreaLight > Build Demo Scene** — this generates the scene, materials, render texture, placeholder image, and auto-bakes its probe volumes. Open the generated `Demo.unity` and hit Play.

## Quick start

1. **Add a `VideoAreaLightSource` component** to the GameObject that holds your video screen mesh.
2. **Assign your Video Texture** (the render texture your VideoPlayer writes to).
3. **Check the cyan gizmo** points into the room. If not, toggle **Flip Normal**.
4. **Switch your floor and wall materials** to the `VideoAreaLight/Lit` shader (or use the Poiyomi or Shader Graph integrations). Existing texture, colour, smoothness, metallic, normal, occlusion, and emission values carry over from URP/Lit.
5. **Tick `Use Video Cookie`** on high-gloss surfaces (Smoothness > 0.5).
6. **Add a Zone Mask.** The broadcaster's inspector shows a **Create Zone Mask** button when none is assigned. Click it, then resize the box to encompass your room. Without one, screen reflections land on every surface in the world.

Standard URP/Lit features still work: shadows, lightmaps, fog, decals, Forward+ punctual lighting. The video light is added on top.

> [!IMPORTANT]
> Only **one** `VideoAreaLightSource` may be active per scene. Two active components will fight each other and you'll see flickering or no light at all.

> [!TIP]
> Unity's default Quad mesh has its visible face on the opposite side of its transform, so for a Quad you usually want **Flip Normal = ON**. If you're unsure, temporarily set **Two Sided = ON**: if the light appears, you've got the right setup — find the correct Flip Normal value and switch Two Sided back off.

## Occlusion: keeping reflections where they belong

In a venue with multiple rooms, the screen's reflections will land on every surface in the world by default — including a glossy bar floor in the lobby or a metal wall behind a partition. VideoAreaLight ships two tools to bound that.

### Zone Mask (start here)

Defines the outer boundary of where the screen's lighting and reflections are allowed to land. Cheap and effective — handles most cases on its own.

- Click **Create Zone Mask** in the broadcaster's inspector. It drops a `VAL_ZoneMask` prefab as a sibling of the broadcaster and links it up automatically.
- Or right-click in the Hierarchy → **Video Area Light → Zone Mask** to create one independently.
- Resize the BoxCollider to encompass the venue with a small margin. Rotation and scale of the box are honoured, so you can tilt it to fit angled rooms.
- Adjust **Zone Feather** on the broadcaster for a softer edge at the boundary.

### Probe Volume (only if needed)

Captures occlusion from awkward indoor geometry — dance-floor steps, mezzanine undersides, pillars, equipment cases — that a Zone Mask alone can't handle. Generates a small baked 3D visibility texture. Static props inside a volume's bounds also cast shadows on the surfaces below them automatically; no per-object setup needed.

- Click **Add Probe Volume** in the broadcaster's inspector, or right-click Hierarchy → **Video Area Light → Probe Volume**.
- Up to four can run at once. Mix one large coarse volume for the whole venue with smaller fine-detail volumes around the spots that need them.
- Click **Bake This Volume** in the volume's inspector. Re-bake when the screen, the volume, or any geometry moves. **Tools → VideoAreaLight → Bake Visibility** bakes every volume in the scene at once.
- Each volume saves a Texture3D asset next to the active scene.

For deeper guidance — sizing recipes, voxel-size and encoding tradeoffs, memory tables — see [`Documentation~/Occlusion.md`](Packages/com.towneh.videoarealight/Documentation~/Occlusion.md).

## Package contents

```
VideoAreaLight/
├── package.json
├── README.md
├── LICENSE.txt
├── CHANGELOG.md
├── Runtime/
│   ├── com.towneh.videoarealight.Runtime.asmdef
│   ├── Prefabs/
│   │   ├── VAL_ZoneMask.prefab            BoxCollider preset for analytic occlusion
│   │   └── VAL_ProbeVolume.prefab         empty probe volume for baked occlusion
│   ├── Scripts/
│   │   ├── VideoAreaLightSource.cs        broadcaster MonoBehaviour
│   │   └── VideoAreaLightProbeVolume.cs   cascading visibility-volume host
│   └── Shaders/
│       ├── RectAreaLight.hlsl             core math include
│       ├── VideoAreaLight_Lit.shader      drop-in URP/Lit-compatible shader
│       └── VideoAreaLight_SG.hlsl         Shader Graph custom-function entry point
├── Editor/
│   ├── com.towneh.videoarealight.Editor.asmdef
│   ├── VideoAreaLightSourceEditor.cs      broadcaster inspector + empty-state nudges
│   ├── VideoAreaLightProbeVolumeEditor.cs probe volume inspector + bake button
│   ├── VideoAreaLightProbeVolumeBaker.cs  visibility baker (RaycastCommand jobs)
│   ├── VideoAreaLightLitGUI.cs            custom material inspector
│   ├── VideoAreaLightMenu.cs              GameObject > Video Area Light menu items
│   ├── PoiyomiModuleInstaller.cs          Tools menu: install/uninstall Poiyomi module
│   └── IntegrationMenuVisibility.cs       auto-hides shader-integration menus when host shader missing
├── Documentation~/
│   ├── PoiyomiIntegration.md              guide for the Poiyomi Pro integration
│   ├── Occlusion.md                       occlusion guide and recipes
│   └── FutureIdeas.md                     forward-looking ideas not yet shipped
└── Samples~/
    ├── Demo/                              importable demo scene (built via menu command)
    └── PoiyomiIntegration/                Poiyomi Pro Modular Shader template
```

## Poiyomi Pro integration

VideoAreaLight ships a Modular Shader module for Poiyomi Pro so existing Poiyomi-shaded materials can receive the area light without switching shader. Two-step setup:

1. **Tools → VideoAreaLight → Install Poiyomi Module** (one-time, fast).
2. Right-click each Poiyomi material → **VideoAreaLight - Apply to Material's Shader** (regenerates only those shaders).

Then toggle **Video Area Light Enabled** on the materials you want lit. The same `VideoAreaLightSource` broadcaster drives both Poiyomi and `VideoAreaLight/Lit` materials — globals are global; you don't need a second component.

**Full guide:** [`Packages/com.towneh.videoarealight/Documentation~/PoiyomiIntegration.md`](Packages/com.towneh.videoarealight/Documentation~/PoiyomiIntegration.md) — covers the install flow, shader regeneration, Thry lock interaction, uninstall, and troubleshooting.

## Shader Graph alternative

If you'd rather not use the drop-in shader, drop `Runtime/Shaders/VideoAreaLight_SG.hlsl` into a **Custom Function** node:

- **Type:** File
- **Source:** drag the `.hlsl` file in
- **Name:** `VideoAreaLight_float`

Inputs: `WorldPos`, `WorldNormal`, `WorldView` (all World space), `Roughness` (1 − Smoothness), `BaseColor`, `Metallic`, `UseCookie` (0/1). Add the `Diffuse` + `Specular` outputs to your master node's **Emission** input.

## Basis VR (Cilbox)

`VideoAreaLightSource` is auto-marked `[Cilboxable]` when `com.cnlohr.cilbox` is present in the project, so the broadcaster runs inside Basis's prop sandbox without further setup. To opt out — Cilbox installed, but you don't want VAL surfaced to the prop script system — add `VAL_DISABLE_CILBOX` under **Project Settings → Player → Scripting Define Symbols**.

## Tuning

Three places shape the look: the broadcaster, the receiving material, and the post-process volume.

### `VideoAreaLightSource` component

| Property         | Recommended | Notes |
|------------------|-------------|-------|
| Max Intensity    | `50`        | Biggest lever. 30–100 sensible; >100 fine with HDR + bloom. |
| Min Intensity    | `0`         | In production. `~4` only as a debug floor. |
| Intensity Curve  | `1.0`       | Linear. `1.5` darkens midtones for punch; `0.5` lifts midtones. |
| Saturation Boost | `1.4`       | Up to `2.0` for vivid colour even on desaturated frames. |
| Response Time    | `0.15 s`    | TV-like. `0.05` raves, `0.4` cinematic. |
| Sample Rate      | `15 Hz`     | Plenty; raising it just costs more readbacks. |

### Receiving material

| Property         | Recommended       | Notes |
|------------------|-------------------|-------|
| Smoothness       | `0.6+`            | <0.5 reads like a spot light. `0.7–0.8` wet club floor; `0.8–0.9` polished metal. |
| Metallic         | `0 – 0.3`         | `0` plain, `0.1–0.3` wet/lacquered, `0.5+` for actual metal (intensifies the cookie). |
| Use Video Cookie | ON for high-gloss | Samples the video inside the highlight. OFF on rough surfaces. |
| Base Color       | mid-grey or lifted| Pure black absorbs all the light. |

### Post-process volume

| Property         | Recommended       | Notes |
|------------------|-------------------|-------|
| Bloom Threshold  | `0.5 – 0.7`       | Lower = more highlight blooms. Below `0.4` turns the whole frame to soup. |
| Bloom Intensity  | up to `0.8`       | Past `1.2` reads as overdone. |
| Tonemapping      | Neutral or ACES   | "None" hard-clips at white. |

### Quick-win combo

Club video wall on a glossy floor: `Max Intensity 50`, `Intensity Curve 1.0`, floor `Smoothness 0.7`, `Metallic 0.15`, `Use Cookie ON`, `Bloom Threshold 0.6`.

## Performance

Cheap per-pixel: a few dozen math ops and (if Use Cookie is on) one texture sample. No lookup tables, no precomputed data.

Indicative GPU time at 90 Hz, ~2k per eye, with one screen:

| Platform                  | Floor only        | Full receiving set        |
|---------------------------|-------------------|---------------------------|
| PCVR (RTX 30/40 class)    | `0.10 – 0.30 ms`  | `0.40 – 0.90 ms`          |
| Quest 3 standalone        | `0.40 – 1.00 ms`  | `1.20 – 2.50 ms` (tight)  |

The biggest cost lever is how many materials use the shader. Then Use Cookie on/off, then how glossy the surface is — even rough surfaces pay the same eval cost despite producing a near-invisible highlight, so it's better to keep the shader on the surfaces that actually benefit from it.

## Known limitations

- **Highlight at Smoothness > 0.95** is slightly less elongated than a perfect mirror would show, and barely visible below Smoothness 0.85.
- **One screen per scene.** Multiple `VideoAreaLightSource` components fight each other.
- **URP only.** The Built-in Render Pipeline isn't supported.
- If a receiver has **Contribute GI** on with a baked lighting mode, the realtime contribution stacks on top of any baked light from this screen — that surface can end up too bright. Disable Contribute GI on the receiver if it looks over-lit.

## Troubleshooting

<details>
<summary><strong>No light visible at all</strong></summary>

1. The cyan gizmo points into the room (toggle **Flip Normal** if not).
2. Only one `VideoAreaLightSource` is active in the scene.
3. Receiving materials use the `VideoAreaLight/Lit` shader (or their Shader Graph wires the Custom Function output into Emission).
4. Video Texture is assigned and the VideoPlayer is playing.
5. Max Intensity is high enough (try `50`).
6. Sanity check: set **Two Sided** to ON. If light appears, the screen is just facing the wrong way — pick the correct Flip Normal value, then turn Two Sided back off.
</details>

<details>
<summary><strong>Highlight is in the wrong place, mirrored, or behind the screen</strong></summary>

Screen Axis is wrong (XY for a Quad, XZ for a Plane), or the screen is facing the wrong way — toggle Flip Normal.
</details>

<details>
<summary><strong>Highlight is the right shape but the wrong colour</strong></summary>

With Use Cookie off you get the screen's average colour — that's expected. Tick **Use Cookie** to sample the actual video.
</details>

<details>
<summary><strong>Highlight pops or snaps as the camera moves</strong></summary>

Most visible at Smoothness > 0.9. Lower Smoothness slightly or raise Response Time.
</details>

<details>
<summary><strong>Performance dips on Quest</strong></summary>

Use the `VideoAreaLight/Lit` shader only on the dance floor and a few key glossy surfaces; keep Use Cookie off on the rest.
</details>

<details>
<summary><strong>Floor reflection shows coloured bands past the screen's edges</strong></summary>

Caused by the cookie texture's Wrap Mode being set to Repeat. With Repeat, bilinear sampling near UVs 0/1 reads from the opposite edge and bleeds those colours past the reflection's actual footprint.

Set the Wrap Mode to `Clamp` on the render texture (or any source texture) you're feeding to the broadcaster's `Video Texture`. The Demo builder force-sets this on its placeholder; user-supplied textures need it set manually in the Inspector.
</details>

## License

MIT — see [LICENSE.txt](LICENSE.txt).
