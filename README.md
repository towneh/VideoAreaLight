# VideoAreaLight

Real-time area-light reflections from a video screen, designed for Unity 6000.0+ (URP 17 & Forward+).

<img width="1218" height="800" alt="{74817138-83E9-41CC-9723-56A48F9B1788}" src="https://github.com/user-attachments/assets/4817eb48-6c7b-446b-98a8-05e282c8f9e2" />
<i>Above example running on Basis VR (Unity 6000.4.4f1) & NormalMap sourced from AreaLit Demo scene.</i>

## Overview

VideoAreaLight makes a video render texture behave like a real rectangular area light source in URP 17. Glossy receivers (dance floor, metal walls, polished panels) pick up:

- A rectangular specular highlight that follows the screen's pose as the camera moves.
- Area-shaped soft falloff (analytic polygon irradiance, not a spot cone).
- A blurred image of the video sampled inside the highlight, so the floor reflects what's playing on the screen, not just a colour.

Everything is computed per-pixel in real time — no precomputed lookup textures, no bundled third-party data. The package exists because URP 17 has no native realtime rectangular area lights of its own (Unity's built-in Rect/Disc lights are baked-only). 

The underlying maths is implemented from publicly-published graphics papers (Karis's Most Representative Point for specular, classical polygon irradiance for diffuse), so the entire package ships under a clean MIT licence with no attribution carry-over.

## Installation

Install via the Unity Package Manager:

1. Open **Window → Package Manager**.
2. Click **+** → **Install package from git URL**.
3. Paste:

   ```
   https://github.com/towneh/VideoAreaLight.git?path=Packages/com.towneh.videoarealight
   ```

To try the example scene, find **Video Area Light** in the Package Manager, expand **Samples**, and click **Import** next to *Example Scene*. Open the imported `Samples/Video Area Light/<version>/Example Scene/ExampleScene.unity` and hit Play.

## Quick start

1. **Drag** `Runtime/Prefab/VideoAreaLight.prefab` onto the GameObject whose quad mesh displays the video.
2. **Assign Video Texture** (your VideoPlayer's render texture) on the `VideoAreaLightSource` component.
3. **Verify the cyan gizmo** points INTO the room. If not, toggle **Flip Normal**.
4. **Switch your floor / wall materials** to the `VideoAreaLight/Lit` shader. The property block mirrors URP/Lit, so existing texture, colour, smoothness, metallic, normal, occlusion, and emission values carry over.
5. **Tick `Use Video Cookie`** on high-gloss surfaces (Smoothness > 0.5).

Standard URP/Lit features still work: shadows, lightmaps, depth prepass, SSAO, fog, decals, Forward+ punctual lighting. The area-light contribution is additive on top.

> [!IMPORTANT]
> Only **one** `VideoAreaLightSource` may be active per scene. The component pushes a fixed set of global shader uniforms; two active components fight each other. Symptoms include flickering or no contribution at all.

> [!TIP]
> Unity's default Quad mesh has its visible face on `-transform.forward`, so a Quad with rotation 0 typically needs **Flip Normal = ON**. If you're unsure, temporarily set **Two Sided = ON**: if contribution appears, find the right Flip Normal value and return Two Sided to OFF (one-sided is cheaper and physically correct for a video panel).

## Package contents

```
VideoAreaLight/
├── package.json
├── README.md
├── LICENSE.txt
├── CHANGELOG.md
├── Runtime/
│   ├── com.towneh.videoarealight.Runtime.asmdef
│   ├── Prefab/
│   │   └── VideoAreaLight.prefab       pre-configured broadcaster
│   ├── Scripts/
│   │   └── VideoAreaLightSource.cs     broadcaster MonoBehaviour
│   └── Shaders/
│       ├── RectAreaLight.hlsl          core math include
│       ├── VideoAreaLight_Lit.shader   drop-in URP/Lit-compatible shader
│       └── VideoAreaLight_SG.hlsl      Shader Graph custom-function entry point
├── Editor/
│   ├── com.towneh.videoarealight.Editor.asmdef
│   └── VideoAreaLightLitGUI.cs         custom material inspector
└── Samples~/
    └── ExampleScene/                   importable demo scene
```

## Shader Graph alternative

If you'd rather not use the drop-in shader, drop `Runtime/Shaders/VideoAreaLight_SG.hlsl` into a **Custom Function** node:

- **Type:** File
- **Source:** drag the `.hlsl` file in
- **Name:** `VideoAreaLight_float`

Inputs: `WorldPos`, `WorldNormal`, `WorldView` (all World space), `Roughness` (1 − Smoothness), `BaseColor`, `Metallic`, `UseCookie` (0/1). Add the `Diffuse` + `Specular` outputs to your master node's **Emission** input.

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

Per-pixel: ~60–90 ALU instructions. No LUT fetches. With cookie ON, one extra mip-level texture sample.

At 90 Hz, ~2k per eye, with one area light:

| Platform                  | Floor only        | Full receiving set        |
|---------------------------|-------------------|---------------------------|
| PCVR (RTX 30/40 class)    | `0.10 – 0.30 ms`  | `0.40 – 0.90 ms`          |
| Quest 3 standalone        | `0.40 – 1.00 ms`  | `1.20 – 2.50 ms` (tight)  |

Cost levers, in order of impact: number of materials using the shader → cookie ON/OFF → surface gloss (rough surfaces still pay the eval cost for nearly-invisible highlights — prefer to skip).

## Known limitations

- Highlight at **Smoothness > 0.95**: less stretched than a true LTC integration would produce. Invisible at Smoothness < 0.85.
- **Cookie sampling is point-sampled** at the MRP UV. Looks correct on glossy floors and metals; on a perfect mirror, a multi-tap or LTC-textured integral would be more accurate.
- **Single area light per scene.** Multiple screens would need namespaced globals or an array-and-loop in the shader.
- **URP-only.** Won't compile against BIRP without changes.
- With **Contribute GI = ON + baked Lighting Mode**, this realtime contribution adds on top of baked light — can over-bright. For the dance floor, consider Contribute GI = OFF.

## Troubleshooting

<details>
<summary><strong>No contribution visible at all</strong></summary>

1. Cyan gizmo points INTO the room. If not, toggle Flip Normal.
2. Only **one** `VideoAreaLightSource` is active in the scene.
3. Receiving material's Shader is `VideoAreaLight/Lit` (or its Shader Graph wires the Custom Function output into Emission).
4. Video Texture is assigned and the VideoPlayer is playing.
5. Max Intensity is non-trivial (try `50`).
6. As a sanity check, set Two Sided = ON. If contribution appears, orientation was the issue; find the right Flip Normal value, then return Two Sided to OFF.
</details>

<details>
<summary><strong>Highlight in the wrong place / mirrored / behind the screen</strong></summary>

Screen Axis is wrong (XY for Quad, XZ for Plane), or the quad's visible face is on the opposite side — toggle Flip Normal.
</details>

<details>
<summary><strong>Highlight is right shape but wrong colour</strong></summary>

With Use Cookie OFF you get the average colour — that's expected. Tick **Use Cookie** to sample the actual video.
</details>

<details>
<summary><strong>Cookie too sharp / too blurry</strong></summary>

Cookie mip is `roughness * 7`. Adjust the multiplier in `RectAreaLight.hlsl` (`VAL_SpecularContribution`, around `0.5..2`).
</details>

<details>
<summary><strong>Highlight pops as the camera moves</strong></summary>

MRP closest-point clamp can briefly snap when the reflection ray crosses a rectangle edge. Most visible at Smoothness > 0.9. Lower Smoothness slightly or raise Response Time.
</details>

<details>
<summary><strong>Performance dips on Quest</strong></summary>

Restrict the shader to the dance floor only, set Use Cookie OFF on everything else.
</details>

## License

MIT — see [LICENSE.txt](LICENSE.txt).
