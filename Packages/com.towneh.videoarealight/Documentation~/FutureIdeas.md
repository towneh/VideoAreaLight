# Future ideas

Forward-looking ideas for VideoAreaLight that have been scoped but are not part of the current package. Captured here for reference; not promises of future work.

## Anisotropic visibility encoding

The current Probe Volume stores a single scalar visibility value per voxel and trilinear-filters between samples at runtime. This can blur visibility across geometry thinner than the voxel size — for example, a single thin partition may not fully block light if the voxels on either side bleed into each other.

Encoding visibility per axis (e.g., RGBA channels for X, Y, Z half-spaces, or a full six-channel cube) and blending by the direction from voxel centre to fragment would solve this without raising voxel resolution. Tradeoffs:

- 4× to 8× memory per voxel depending on encoding.
- Runtime path adds one or two extra Texture3D fetches.
- Bake-side cost is similar (the same rays are still cast; results are bucketed by direction).

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
