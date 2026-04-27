# Future ideas

Forward-looking ideas for VideoAreaLight that have been scoped but are not part of the current package. Captured here for reference; not promises of future work.

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
