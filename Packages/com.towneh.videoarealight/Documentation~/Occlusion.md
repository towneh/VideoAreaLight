# Occlusion in VideoAreaLight

How to stop screen reflections landing on surfaces they shouldn't — adjacent rooms, behind walls, under mezzanines.

## Two tools, in priority order

VideoAreaLight ships two complementary occlusion tools. They multiply together at runtime, so you can use either alone or both together.

1. **Zone Mask** — analytic OBB primitive defining the **outer** boundary of where the screen's lighting and reflections are allowed to land. Cheap (a few math ops at runtime). Use this whenever your venue has a recognisable room boundary.
2. **Probe Volume** — baked 3D visibility texture refining occlusion **inside** the zone, capturing static geometry an OBB can't represent (pillars, mezzanines, dance-floor steps). One texture fetch per active volume at runtime.

Reach for the Zone Mask first — it covers most cases. Add Probe Volumes only when specific intra-room geometry still leaks despite the zone.

Both tools gate diffuse and specular together. A surface outside the Zone Mask gets no reflection of the screen at all, which is what makes the package's occlusion reflection-correct rather than just diffuse-correct.

## Zone Mask

A `BoxCollider` referenced by the broadcaster. Fragments inside the box receive the screen's lighting; fragments outside get nothing.

### Authoring

The broadcaster's inspector shows a **Create Zone Mask** button when no zone is assigned. Click it: a `VAL_ZoneMask` GameObject (a tagged BoxCollider) is created beside the broadcaster, and the reference is wired up automatically.

You can also create one independently from `GameObject > Video Area Light > Zone Mask` and drag it into the broadcaster's `Zone Volume` field.

### Sizing

Resize the BoxCollider so it encompasses your venue with a small margin. The collider's transform rotation and scale are honoured, so you can tilt the box to fit angled rooms. The wireframe gizmo updates live in the scene view as you resize.

### Soft edges

`Zone Feather` on the broadcaster softens the boundary. `0` is a hard cutoff at the wall; values around `0.1` give a subtle fade that hides the seam without leaking far.

## Probe Volume

A `VideoAreaLightProbeVolume` component. Bakes a 3D visibility texture by firing rays from each voxel toward sample points on the screen rectangle and storing the fraction unblocked. At runtime, the value is sampled per fragment and multiplied into the contribution.

### Authoring

The broadcaster's inspector has an **Add Probe Volume** button. You can also create one from `GameObject > Video Area Light > Probe Volume`.

Position the GameObject so its box covers the area you want refined occlusion in. Resize via the `Size` field — the GameObject's rotation and scale apply on top, so the box can be tilted.

### Cascading volumes

Up to four volumes can run at once. Each contributes multiplicatively where its box covers a fragment; outside its bounds, that volume has no effect.

This lets you mix resolutions efficiently:

- One coarse volume covering the whole venue (cheap memory).
- Smaller fine volumes around problem geometry like steps, narrow corridors, or under mezzanines (high resolution exactly where it matters).

If more than four volumes exist in a scene, the top four by `Priority` win. Use a higher priority on fine-detail volumes to ensure they don't get evicted.

### Baking

- **Bake This Volume** button on the volume's inspector — fast, single-volume re-bake.
- **Tools > VideoAreaLight > Bake Visibility** menu — bakes every volume in the scene with per-volume progress.
- Baking uses Unity's Jobs system for parallel raycasts; typical bakes for room-scale volumes finish in a few seconds.

Each bake produces a `Texture3D` asset saved next to the active scene as `VAL_Visibility_<volume-name>.asset`. Re-bake when you move the screen, the volume, or any geometry the volume's rays would hit.

### Voxel size and quality

`Voxel Size` controls the resolution of the bake. `0.1m` (10cm) is a reasonable default for room-scale geometry. Drop to `0.05m` (5cm) around fine geometry like steps or thin partitions where a coarser bake might blur visibility across the geometry.

`Samples per Voxel` controls how many rays each voxel fires at the screen during baking. `16` is a good default — more samples give smoother penumbras at the cost of longer bakes.

`Occluder Mask` controls which physics layers count as light-blockers during baking. Walls, floors, and props you want to cast shadows should be included; the screen mesh's own collider (if any) should be excluded.

### Encoding (Scalar or Quadrant)

Each volume picks one of two encodings via the `Encoding` field on the inspector.

`Scalar` (default) stores one visibility value per voxel — 1 byte each, the original format.

`Quadrant` stores visibility separately for each of the screen's four UV quadrants — 4 bytes per voxel, 4× the texture size. At runtime the shader picks the right quadrant per fragment: orthogonal projection of `worldPos` onto the screen for diffuse, the MRP UV for specular. Two things this unlocks:

- **Directional shadows on screen-facing walls.** With Scalar, a partial occluder between the screen and a wall produces a uniformly dimmed wall (the average of blocked and unblocked rays). Quadrant reconstructs the shadow direction from which screen quadrants the wall actually sees.
- **Per-UV occlusion in floor reflections.** Quadrant darkens only the parts of the reflected screen that are physically blocked, instead of dimming the whole reflection uniformly.

Don't enable everywhere. The recommended pattern is **Scalar for venue-wide coarse volumes** and **Quadrant only for fine volumes around problem geometry** (steps, mezzanines, screen-facing walls) where the directional accuracy is worth 4× the storage. The Demo sample mixes both: Scalar for `VAL_Probe_Coarse` and `VAL_Probe_Corridor`; Quadrant for `VAL_Probe_Fine` and `VAL_Probe_BoothFloor`.

## Recipe: a fine-detail Probe Volume around a step

For a dance-floor step or any small piece of awkward geometry inside a venue:

1. Create the volume via the inspector or GameObject menu.
2. Position the GameObject at the step's centroid — roughly halfway between the upper and lower floor surfaces, centred along the step's width.
3. Orient the GameObject so its local axes align with the step (the long axis runs along the step's length).
4. Size the box to cover the step itself plus about 1 metre of floor on each side, plus a 20-30 cm margin.
5. Set `Voxel Size` to `0.05m` for fine resolution at the step's scale.
6. Set `Priority` to `1` (or higher than your venue-wide volume) so it's guaranteed not to be evicted.
7. Click **Bake This Volume**.

If a hard ring appears at the volume boundary, expand the box a few voxel widths in that direction and re-bake. The wireframe gizmo (orange when unbaked, green when baked) makes it easy to see what region you're covering.

## Recipe: cast static shadows from props on the dance floor

Useful for fixed venue features that should block the screen's light — a DJ booth, equipment cases, decorative pillars, sculpture, anything you don't expect to move at runtime. The Probe Volume bake captures these as shadows automatically; no separate tooling required.

How it works: the visibility baker fires raycasts from each voxel toward sample points on the screen rectangle. Any collider on a layer included in the volume's `Occluder Mask` blocks those rays, so the voxels behind the collider receive a lower visibility value. At runtime that translates to a darker patch in the screen's contribution behind the prop — a static shadow.

To set it up:

1. Make sure each prop has a collider that matches its silhouette. Box-shaped props can use Unity's default BoxCollider; irregular shapes (sculpture, truss work, LED fixtures) need a MeshCollider on the mesh.
2. Confirm the prop's collider is on a layer included in the Probe Volume's `Occluder Mask`. If you're using the default mask, every layer is included, so a freshly-imported prop usually just works.
3. Confirm the Probe Volume covers the area where the shadow should appear (the floor behind the prop, plus any walls the prop should also shadow).
4. Click **Bake This Volume**.

Tradeoffs and tips:

- **The shadow is frozen at bake time.** If the prop moves, the shadow stays where the prop *used to be* until you re-bake. Re-bake any time you reposition props, the screen, or other occluders.
- **Shadow edge sharpness scales with voxel size.** A 10 cm voxel produces noticeably softer shadow edges than 5 cm. For a venue-wide volume, 10 cm is fine for soft "presence" shadows from large props; drop a smaller fine volume (0.05 m) over precision-critical areas like the front of the stage if you want crisper shadow lines.
- **Don't include the screen mesh's collider in the bake.** If your video screen mesh has a collider on a layer the bake reads, the bake will think the screen blocks itself and produce broken visibility. Either remove the screen's collider or exclude its layer from `Occluder Mask`.
- **Many props compose for free.** Multiple static occluders inside a Probe Volume's bounds all cast their own shadows in the same bake. There's no per-object configuration; it all comes out of the same raycast pass.

## Memory and bake-time guide

Memory cost scales with voxel count: `(size.x / voxelSize) × (size.y / voxelSize) × (size.z / voxelSize)` bytes for the R8 texture, before texture-importer compression.

| Volume size | Voxel size | Voxels | Asset size |
|---|---|---|---|
| 4 × 4 × 3 m | 0.1 m | 48,000 | ~50 KB |
| 4 × 4 × 3 m | 0.05 m | 384,000 | ~380 KB |
| 10 × 10 × 4 m | 0.1 m | 400,000 | ~400 KB |
| 30 × 15 × 40 m | 0.1 m | 18 M | ~18 MB |
| 30 × 15 × 40 m | 0.05 m | 144 M | ~144 MB |

Bake time scales linearly with voxel count and ray count. The cascading model exists so you don't need to go to fine resolution across a whole venue: keep the venue-wide volume coarse and add small fine volumes only around problem geometry. A 10 × 1.25 × 12.5 m volume at 5 cm bakes in a few seconds and weighs around 1 MB.

## Other approaches considered

A few alternatives were evaluated and not adopted; recording the gist for context.

- **Per-material UV-painted mask.** A scalar mask painted per material in surface UV space and sampled at the receiver's UV. Rejected because surface-space masks can't gate reflections of off-surface space — a glossy floor in an adjacent room with no mask painted would still reflect the screen. The Zone Mask + Probe Volume combination handles this correctly because both work in world space, where the reflection's geometry actually lives.

For ideas that may land in future versions, see [`FutureIdeas.md`](FutureIdeas.md).
