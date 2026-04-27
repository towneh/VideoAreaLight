# Future ideas

Forward-looking ideas for VideoAreaLight that have been scoped but are not part of the current package. Captured here for reference; not promises of future work.

## Anisotropic visibility encoding

The current Probe Volume stores a single scalar visibility value per voxel and trilinear-filters between samples at runtime. The single scalar can't represent *which part* of the screen is occluded — only the fraction. This produces two characteristic artifacts:

- **Speaker/prop shadows over-extend on walls.** A receiving wall fragment whose voxel sees, say, 70% of the screen visible reads the same dimming whether the occluded 30% is at screen-top or screen-bottom. The shadow on the wall therefore extends as tall as the bake's voxel-grid permits, regardless of where on the screen the occlusion actually is, often reaching well past the occluder's actual height.
- **Specular reflection on glossy floors can't track which point of the screen is occluded for the reflected ray.** The reflection direction lands at a specific screen UV; if that UV happens to be in the blocked region, the reflection should be dim, but scalar visibility just averages.

Two encodings have been attempted experimentally on feature branches; neither merged into main. Both are preserved with their step-by-step commit history (format-plumb → math change → tuning) so future work can reuse pieces.

### Attempt 1 — SH L1 directional encoding (`feature/anisotropic-visibility`)

4-channel RGBA32 storage: DC term in R, three L1 directional coefficients in GBA (packed from [-1, 1] to [0, 1]). Baker accumulates SH coefficients from the existing rays. Shader reconstructs at the surface normal (diffuse) and reflection direction (specular) via `saturate(DC + k · dot(SH₁, d))`.

**Headline win** in early testing: speaker shadows correctly tracked the screen's vertical extent — the directional info exists and the bake captures it.

**Headline loss** at full evaluation: SH L1 is band-limited. Reconstruction at the *dominant* ray direction saturates regardless of how much of the source is occluded — and for the typical receiver geometry (a wall facing the screen), the surface normal *is* the dominant ray direction. `DC + 2·dot(SH₁, +Z)` for a wall with N = +Z and rays mostly going in +Z gives a saturated 1 even when DC is well below 1. The shadow on a screen-facing wall therefore becomes nearly invisible despite the directional info existing in the bake — the reconstruction can't extract it where it matters most.

Other observations: per-voxel SH₁ direction noise produced visible voxel-grid banding even at 64 samples per voxel (variance scales as 1/√N for SH coefficients; lower factor reconstruction reduces banding but also reduces the directional differentiation). 4× memory cost on top.

**Verdict:** structurally limited. L2 (9 coefficients) might fix the dominant-direction saturation but at ~12× memory and proportional bake time.

### Attempt 2 — Per-screen-quadrant encoding (`feature/quadrant-visibility`)

4-channel RGBA32 storage: visibility from the voxel to each of four screen UV quadrants (R = BL, G = BR, B = TL, A = TR). Baker bins each ray by its target screen UV quadrant. Shader bilinear-reconstructs at the fragment's actual relevant screen UV — the orthogonal projection of `worldPos` onto the screen plane for diffuse, and the existing MRP UV for specular.

The conceptual difference vs SH/cube encodings: this *isn't trying to represent visibility on the full sphere of directions*. The problem is visibility of a known rectangular emitter, which is a 2D function over screen UV. Quadrant encoding matches the actual problem geometry rather than approximating a 3D function and hoping the relevant region gets enough resolution.

**Headline win:** speaker shadows track the correct height *and* the lectern shadow tracks its own (different) height in the same scene — they read as visually distinct heights on the wall. No SH-style saturation problem because the encoding is direct, not a band-limited approximation. No visible voxel-grid banding at 64 samples (unlike SH, where SH₁ direction noise amplified into the reconstruction).

**Headline cost:** 4× per-voxel storage. The demo's bake assets grow from ~10 MB (scalar) to ~40 MB (quadrant), past the rough 17 MB threshold flagged elsewhere as "unacceptable for UGC distribution."

**Caveats:**
- Source-shape-specific. Only valid for a rectangular emitter. Disc lights, polyhedral lights, or other shapes would need a different encoding. The package is rectangular by design so this isn't a real practical limit, but it's not a general-purpose visibility format.
- Resolution is screen-quadrant-coarse. A booth occluding a small region inside a single quadrant gets averaged with the rest of that quadrant. If finer detail is needed, escalate to 8 cells (2 RGBA textures = 8× scalar) or 16 cells (4 RGBA textures = 16× scalar).
- Sub-quadrant sample variance: with N total rays and 4 quadrants, ~N/4 rays per bucket. At 16 total (the legacy scalar default), per-quadrant variance is high. The branch defaults to 64 samples for this reason.

**Verdict:** correct for the package's actual problem, gated on memory cost. Worth merging when memory budgets relax, or for use cases where ~40 MB on a small demo is acceptable.

### Comparison summary

For the demo scene's "speaker shadow on the south wall behind the booth" view:

| Approach | Wall shadow shape | Voxel banding | Memory | Verdict |
|---|---|---|---|---|
| Scalar (`main`) | Correct *position*, wrong *height* (over-extends near ceiling) | None | 1× | Shipped |
| SH L1 (`feature/anisotropic-visibility`) | Nearly absent — saturation kills the directional reconstruction at the wall's normal | Visible at 64 samples | 4× | Structurally limited; not merged |
| Quadrant (`feature/quadrant-visibility`) | Correct height, distinct speaker vs lectern shadows, soft physical penumbra | None | 4× | Best result; gated on memory cost |

The shipped scalar visibility produces the dramatic "shadow goes to the ceiling" result that's recognisable in a club setting and arguably reads as stylised, but it isn't physically right. Quadrant produces the physically-right result but at memory cost the package can't currently afford for distribution.

### Other directions, not yet tried

- **6-face cube encoding** — full directional info, 6 channels = 2 RGBA textures = 8× memory. Similar memory class to quadrant but more general (not source-shape-specific). Worth a future attempt if the package needs to support non-rectangular emitters.
- **Sparse encoding** — most voxels in the demo have uniform visibility (fully visible or fully blocked). Only voxels near occluders carry varying values. A sparse representation could store directional info only where it matters, dramatically reducing memory in practice. Authoring/runtime cost is substantial; only worth it if the average-case memory becomes the limiting factor.
- **Per-axis half-spaces (3-channel)** — original `FutureIdeas.md` framing referenced "RGBA channels for X, Y, Z half-spaces." Same family as cube encoding but coarser. Likely to have its own band-limit issues for narrow-cone visibility queries, similar to SH L1.

## Diffuse-from-lightmap mode

Replace the analytic polygon-irradiance diffuse on static surfaces with a baked Texture2D, modulated at runtime by the screen's current average colour. Specular stays analytic and still goes through the existing volumes.

This would capture soft shadows and one-bounce colour bleed off walls — quality the analytic diffuse path can't model. Tradeoffs:

- Static-only (dynamic objects keep using the analytic diffuse).
- Bake produces a lightmap-shaped Texture2D per receiver atlas.
- Temporal information in the diffuse is lost: a flashing-blue screen and a steady-blue screen produce the same bake. Acceptable for video-wall content where the receiver perception of room lighting is already a low-frequency average.
- Re-bake required when the screen, occluders, or the receiver geometry move.

## Auto-sized voxels

Volume `voxelSize` is currently set by hand. An auto-sizing mode that derives voxel size from the volume's bounds (small volumes get fine voxels, large volumes get coarse ones) would make the cascading workflow more turnkey. Single property change in the baker.

## More than four cascading volumes

The shader currently has four slots. Expanding to eight or sixteen is mechanical (more globals, more `if` blocks in the shader, larger slot table in the component) but increases per-fragment cost. Worth considering only if multi-room venues with many independent occlusion regions become a common authoring pattern.
