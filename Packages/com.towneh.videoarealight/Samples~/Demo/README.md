# VideoAreaLight Demo

A compact club-style venue that demonstrates the package's full feature
set — area-light reflections on glossy surfaces plus the 1.2.0 occlusion
stack:

- **Main room** — a screen on the N wall lights a DJ booth on a glossy floor.
- **L-shaped corridor** — exits the S wall and turns 90° toward a spawn alcove.
- **Spawn alcove** — visibly unaffected by the screen, lit by a separate fill light.

## First-time setup

1. Import this sample via Package Manager (you already did, since you're reading this).
2. Run `Tools > VideoAreaLight > Build Demo Scene`. The builder generates
   the scene (`Demo.unity`), materials (in `Materials/`), a render texture
   and SMPTE-bars placeholder image (in `Textures/`), and a preconfigured
   `VideoPlayer` on `VAL_Source`. It also auto-bakes both probe volumes
   at the end so the scene is play-ready.
3. Open `Demo.unity` and hit Play. The screen shows colour bars, the room
   is lit by their average, the booth casts a baked shadow, and the
   corridor + spawn alcove are dark (zone mask). Walk through to see each
   occlusion mechanism in action.

The builder is idempotent — re-run the menu command any time you want a
fresh scene; it overwrites the existing scene + materials and re-bakes.

If you tweak any geometry by hand and want to re-bake without rebuilding
everything else, run `Tools > VideoAreaLight > Bake Visibility` directly.

### Playing your own video

The placeholder is for first-look only. To run a real clip, do **all
three** swaps (otherwise the screen visual and the area light fall out
of sync):

1. **VideoPlayer.Video Clip** — drop your clip on the `VideoPlayer`
   component of `VAL_Source`. (The component is preconfigured to render
   to `VAL_Screen_RT.renderTexture`.)
2. **VAL_Source → Video Texture** — change from `VAL_ScreenPlaceholder`
   to the `VAL_Screen_RT` render texture. This drives the area
   light's colour from your video.
3. **VAL_Screen material → Base Map** — change from `VAL_ScreenPlaceholder`
   to the `VAL_Screen_RT` render texture. This makes the visible
   screen show your video.

If only #1 is done, the screen stays as colour bars and the lighting
stays as the placeholder average. If #2 is missed, the screen shows the
video but the lighting tracks the placeholder. If #3 is missed, the
lighting follows the video but the screen visual is frozen on the bars.

**Texture wrap mode caveat:** any texture you assign to `VAL_Source →
Video Texture` (whether a render texture, image asset, or video
texture) should have its **Wrap Mode set to `Clamp`** in the Inspector.
The default for image imports is `Repeat`, which causes bilinear
sampling at UVs near the texture's edges to pull in colours from the
opposite side; in the floor's specular reflection, that bleeds visible
bands of unrelated colour past the screen's actual footprint. The
generated `VAL_Screen_RT` render texture is already Clamp-by-default;
the generated `VAL_ScreenPlaceholder` PNG is also force-set to Clamp
by the builder. User-supplied assets need to be set manually.

## What each toggle demonstrates

Open the scene, enter Play mode, then in the Inspector:

| Toggle this off                                    | What you should see                                                                                  |
|----------------------------------------------------|-------------------------------------------------------------------------------------------------------|
| `VAL_Probe_Coarse` GameObject (disable)            | The dramatic toggle. Corridor walls and the spawn alcove start receiving the screen's analytic contribution even though they have no line of sight to it. The corridor floor reflects the screen straight through the 90° turn. **(Cascading volumes demo — coarse slot, doing the venue-wide heavy lifting.)** |
| `VAL_Probe_Fine` GameObject (disable)              | The cast shadow on the floor behind the DJ booth disappears, and the speaker shadows on the south wall lose their sharp edges (fall back to coarse 20cm sampling). The coarse and corridor probes still occlude their regions. **(Cascading volumes demo — fine slot.)** |
| `VAL_Probe_Corridor` GameObject (disable)          | The shadow of the booth's silhouette on the corridor's back wall (visible through the doorway) loses its tight edges and goes back to coarse 10cm sampling. Demonstrates the cascading model's intent: drop a small fine-resolution probe on a specific problem area instead of going fine across the whole venue. **(Cascading volumes demo — middle slot.)** |
| `VAL_Probe_BoothFloor` GameObject (disable)        | The hard shadow boundary right where the DJ booth meets the floor goes from crisp (1cm voxels) to stepped (2.5cm fine-probe sampling). The most visually prominent demo of why ultra-fine targeted probes matter for close-camera shots. **(Cascading volumes demo — top slot.)** |
| `VAL_Source` → **Zone Volume** field (clear it)    | Subtle change in this demo. The zone is sized to the whole venue (main + corridor + spawn), so almost every fragment is inside it either way. Zone mask matters most when there's geometry OUTSIDE your defined venue — neighbouring rooms, exterior surfaces — that you want to keep cheaply zeroed without baking. Probe volumes do the intra-venue heavy lifting. **(Zone mask demo — primary use is venue boundary, not intra-venue cutoff.)** |
| All three together (zone + both probes)            | All occlusion is off. Light leaks everywhere — straight through walls, around the booth, into the corridor and spawn through their corners. This is the "before 1.2.0" baseline. |

## Scene anatomy

```
Demo
├── Architecture
│   ├── MainRoom        (8 × 5 × 3.5m, screen on N wall, glossy floor)
│   ├── Corridor        (L-shape: south leg + east leg, glossy floor continues)
│   └── Spawn           (3 × 3 × 3m, matte floor — different room aesthetic)
├── Props
│   └── DJBooth         (Lectern + 2 speaker stacks, the cast-shadow hero)
├── Lighting
│   ├── VAL_Source      (Quad MeshRenderer + VideoAreaLightSource on ONE GameObject — see Implementation gotchas)
│   ├── VAL_ZoneMask    (BoxCollider sized to the venue's bounding rectangle — main + corridor + spawn)
│   ├── VAL_Probe_Coarse (covers main + corridor + spawn at 10cm voxels — venue-wide backstop, Scalar encoding)
│   ├── VAL_Probe_Corridor (corridor A near-doorway region at 5cm voxels, priority 5, Scalar encoding)
│   ├── VAL_Probe_Fine  (around DJ booth + south wall up to ceiling at 2.5cm voxels, priority 10, Quadrant encoding)
│   ├── VAL_Probe_BoothFloor (tight box around the booth-floor interface at 1cm voxels, priority 15, Quadrant encoding)
│   └── SpawnFill       (warm Spot, range 4m, contained to spawn)
└── Camera              (just inside main-room doorway, looking N)
```

## Implementation gotchas

These are the non-obvious rules to follow when adapting this scene's setup
to your own venue. The builder script encodes them; this section spells
them out so you can apply them by hand.

### 1. The screen Quad's MeshRenderer and `VideoAreaLightSource` must share one GameObject.

The broadcaster computes the screen's world-space corners using its **own**
`transform.TransformPoint(localHalfSize)`. If you put the broadcaster on
a parent and the visible Quad on a child, the broadcaster reads the
parent's scale (likely 1×1) while the Quad renders at the child's scale.
The lit area silently desyncs from the visible screen — symptoms:

- Reflections look like they're coming from a tiny 1m square, not the
  full screen.
- Diffuse falloff is much faster than expected for the screen's apparent
  size.
- Scaling the screen up at runtime has no effect on the lighting.

Fix: parent the Quad mesh directly under (e.g.) your `Lighting` group and
add `VideoAreaLightSource` to that same GameObject.

### 2. Surfaces that should receive the screen's light must use `VideoAreaLight/Lit`.

Plain URP/Lit doesn't read the `_VAL_*` shader globals — surfaces stay
lit only by ambient. The package ships a drop-in URP/Lit-compatible
shader at `VideoAreaLight/Lit`; the demo's wall/floor/booth materials all
use it. (Shader Graph and Poiyomi integrations have their own hookups —
see the package root README.)

### 3. Walls and floors need colliders for the probe bake.

The visibility baker uses `Physics.Raycast`. Visual meshes without
colliders are invisible to the bake and won't occlude. Unity Cube
primitives include a BoxCollider by default; if you build with custom
meshes, add a MeshCollider before baking.

### 4. Re-bake whenever any occluder, the screen, or a probe volume moves.

Bakes are static snapshots. Nothing detects staleness for you. After any
geometry change, run `Tools > VideoAreaLight > Bake Visibility` (all
volumes) or use a volume's "Bake This Volume" inspector button (one).

### 5. Encoding mode is per-volume; mix to taste.

Each probe volume's `Encoding` field is independent. The Demo uses both
modes deliberately:

- **Scalar** (`VAL_Probe_Coarse`, `VAL_Probe_Corridor`) — 1 byte/voxel.
  The package's drop-in default. Treats every direction equally and
  produces shadows whose *position* tracks geometry but whose *shape*
  can over-extend (a wall in scalar shadow often reaches taller than
  the actual occluder, because scalar visibility can't tell which part
  of the screen is occluded).
- **Quadrant** (`VAL_Probe_Fine`, `VAL_Probe_BoothFloor`) — 4 bytes/voxel.
  Stores per-screen-quadrant visibility, bilerped at runtime against
  the fragment's relevant screen UV. Produces correct directional
  shadows on surfaces facing the source — the speaker shadows on the
  south wall behind the booth track the actual speaker height instead
  of stretching toward the ceiling. Costs 4× memory and benefits from
  higher `samplesPerVoxel` (the demo uses 64 for Quadrant, 16 for Scalar).

The mix-and-match pattern is the recommended one: Scalar where coverage
is venue-wide, Quadrant where directional accuracy reads strongest
(close-up shots on walls or floors with directional shadow shapes).

## Bake notes

- Both probe volumes need to be re-baked any time you move the booth, the
  screen, or any wall. The bake is a static snapshot.
- The fine volume sits at `priority = 10` to guarantee it claims a slot
  even if you add more volumes later — fine detail is what's expensive
  to lose.
- Default voxel sizes (10cm coarse, 5cm corridor, 2.5cm fine, 1cm
  booth-floor) total ~10MB of `.asset` data — substantial, but the demo
  prioritises visual quality over distribution size. Bake time on the
  Jobs raycaster runs ~2–3 minutes total. Don't bump `samplesPerVoxel`
  as a quality knob; it scales bake time linearly without improving
  spatial sharpness — voxel size is the right knob.
- This demo uses all 4 probe slots (the runtime cap). Add or remove any
  one and the rest still work; the priority field decides which 4 win
  if a 5th is ever added.

## Why a builder script instead of a hand-authored scene?

The scene's geometry is dense enough (architecture cubes + props + lighting
rig) that hand-editing the YAML to tweak a dimension is tedious and
error-prone. The builder is the source of truth: open
`Editor/DemoSceneBuilder.cs` to see exactly how every transform, component
value, and material is configured, and re-run the menu command to
regenerate after edits.
