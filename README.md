VideoAreaLight
=========

Real-time area-light reflections from a video screen, in URP 17 / Forward+


OVERVIEW
--------
VideoAreaLight makes a video render texture behave like a real
rectangular area light source in URP 17. Glossy receivers (dance floor,
metal walls, polished panels) pick up:

  - A rectangular specular highlight that follows the screen's pose as
    the camera moves.
  - Area-shaped soft falloff (analytic polygon irradiance, not a spot
    cone).
  - A blurred image of the video sampled inside the highlight, so the
    floor reflects what's playing on the screen, not just a colour.

Specular uses Karis's Most Representative Point trick (SIGGRAPH 2013)
with a roughness-widened GGX lobe. Diffuse uses an analytic cosine-
weighted polygon irradiance integral (a closed-form result from the
1980s). No precomputed lookup tables, no third-party data. URP 17
otherwise has no realtime rectangular area lights (Rect/Disc lights
are baked-only); this fills the gap.


TRY IT NOW
----------
Open ExampleScene/ExampleScene.unity and hit Play. A simple room with
a video playing on a back-wall screen demonstrates the full effect on
the floor and walls. Use it as a reference for material settings,
prefab placement, and the cyan-gizmo orientation rule.


HOW IT RELATES TO VideoColourBouncer
------------------------------------
Complementary, not competing.

  VideoColourBouncer  Drives spot lights from the video colour. Cheap.
                      Ambient room fill from rough off-axis surfaces.
                      Circular highlights, no cookie.

  VideoAreaLight      Drives one rectangular area light matched to the
                      screen. Per-pixel cost on opted-in materials.
                      Rectangular highlights with the video content
                      visible inside (cookie).

Run both. Bouncer fills the room, AreaLight handles direct reflections
on glossy surfaces.


WHAT'S IN THIS FOLDER
---------------------
  README.txt
  LICENSE.txt
  ExampleScene/
    ExampleScene.unity                      reference scene - see
                                            "TRY IT NOW" above.
  Prefab/
    VideoAreaLight.prefab                   pre-configured broadcaster.
                                            Drop onto the screen
                                            GameObject and assign
                                            Video Texture - done.
  Scripts/
    VideoAreaLightSource.cs                 broadcaster MonoBehaviour
  Shaders/
    RectAreaLight.hlsl                      core math include
    VideoAreaLight_Lit.shader               drop-in URP/Lit-compatible
                                            shader (recommended)
    VideoAreaLight_SG.hlsl                  Shader Graph custom-function
                                            entry point (alternative)


SETUP GUIDE
-----------

Step 1 - Add VideoAreaLightSource to your video screen

  Drag Prefab/VideoAreaLight.prefab onto the GameObject whose quad mesh
  displays the video. Then:

    1. Assign Video Texture (your VideoPlayer's render texture).
    2. Verify the cyan gizmo points INTO the room. If not, toggle
       Flip Normal. (Unity's default Quad mesh has its visible face on
       -transform.forward, so a Quad with rotation 0 typically needs
       Flip Normal = ON.)

  Default values in the prefab: Sample Rate 15-20, Response Time 0.15,
  Max Intensity 50, Min Intensity 0, Intensity Curve 1.0, Saturation
  Boost 1.4, Two Sided OFF, Local Half Size (0.5, 0.5).

  IMPORTANT - only ONE VideoAreaLightSource may be active per scene.
  The component pushes a fixed set of global shader uniforms; two
  active components fight each other. Symptoms include flickering or
  no contribution at all.

  Diagnostic for Flip Normal: temporarily set Two Sided = ON. If
  contribution appears, the one-sidedness was masking the issue. Find
  the right Flip Normal value, then return Two Sided to OFF (one-sided
  is cheaper and physically correct for a video panel).

Step 2 - Apply the receiving shader to your materials

  Select each material that should receive the area light (dance floor,
  glossy metal walls / pipes / panels). In the Inspector, change its
  Shader to:

      VideoAreaLight / Lit

  The property block mirrors URP/Lit, so all existing texture, colour,
  smoothness, metallic, normal, occlusion, and emission values carry
  over. Then:

    - Tick Use Video Cookie on high-gloss surfaces (Smoothness > 0.5);
      leave OFF on rough materials (cookie blurs to average colour
      anyway, no visible benefit).
    - Don't apply this shader to surfaces far from the screen or
      facing away - contribution will be near-zero but you still pay
      per-pixel cost.

  Standard URP/Lit features still work: shadows, lightmaps, depth
  prepass, SSAO, fog, decals, Forward+ punctual lighting. The
  area-light contribution is additive on top.

  Shader Graph alternative: if you'd rather not use the .shader, drop
  Shaders/VideoAreaLight_SG.hlsl into a Custom Function node (Type:
  File, Name: VideoAreaLight_float) and add the Diffuse + Specular
  outputs to your master node's Emission. Inputs needed: WorldPos,
  WorldNormal, WorldView (all World space), Roughness (1 - Smoothness),
  BaseColor, Metallic, UseCookie (0/1).


TUNING
------
Three places shape the look: the broadcaster, the receiving material,
and the post-process volume.

VideoAreaLightSource component:

  Max Intensity      Biggest lever. 30-100 is sensible for a
                     room-scale screen. Default 50. Higher is fine
                     with HDR + bloom.
  Min Intensity      0 in production. ~4 only as a debug floor.
  Intensity Curve    1.0 default (linear). 1.5 darkens midtones for
                     punch. 0.5 lifts midtones for ambient feel.
  Saturation Boost   1.4 default. Up to 2.0 for vivid colour even on
                     desaturated frames.
  Response Time      0.15 s TV-like. 0.05 raves, 0.4 cinematic.
  Sample Rate        15 Hz is plenty.

Receiving material (where the effect actually shows up):

  Smoothness         < 0.5 reads like the spot-light bouncer. 0.6+
                     for a visible rectangular highlight. 0.7-0.8
                     for a wet club floor; 0.8-0.9 for polished metal.
  Metallic           0 plain, 0.1-0.3 wet/lacquered, 0.5+ for actual
                     metal (intensifies the cookie).
  Use Video Cookie   ON for high-gloss to see video content in the
                     highlight. OFF for rough surfaces.
  Base Color         Avoid pure black - it absorbs all the light.
                     Lift dark albedos toward mid-grey.

Post-process volume (highest leverage after Smoothness):

  Bloom Threshold    0.5-0.7 makes more of the highlight bloom.
                     Below 0.4 turns the whole frame to soup.
  Bloom Intensity    0.8 is a comfortable ceiling. Past 1.2 reads as
                     overdone.
  Tonemapping        Neutral or ACES. None hard-clips at white.

Quick-win combo for a club video wall on a glossy floor:
  Max Intensity 50, Intensity Curve 1.0, Floor Smoothness 0.7,
  Metallic 0.15, Use Cookie ON, Bloom Threshold 0.6.


PERFORMANCE
-----------
Per-pixel: ~60-90 ALU instructions. No LUT fetches. With cookie ON,
one extra mip-level texture sample.

At 90 Hz, ~2k per eye, with one area light:

  PCVR (RTX 30/40 class):
    Floor only:           0.10 - 0.30 ms
    Full receiving set:   0.40 - 0.90 ms

  Quest 3 standalone:
    Floor only:           0.40 - 1.00 ms
    Full set + cookie:    1.20 - 2.50 ms - tight; favour cookie ON
                          on the floor only, OFF elsewhere.

Cost levers, in order of impact: number of materials using the
shader → cookie ON/OFF → surface gloss (rough surfaces still pay
the eval cost for nearly-invisible highlights, prefer to skip).

Combined with VideoColourBouncer on Quest:
  Bouncer alone:                        0.5 - 1.5 ms
  Bouncer + AreaLight on floor only:    0.9 - 2.0 ms
  Bouncer + AreaLight on full set:      1.7 - 3.5 ms


KNOWN LIMITATIONS
-----------------
  - Highlight at smoothness > 0.95: less stretched than a true LTC
    integration would produce. Invisible at smoothness < 0.85.
  - Cookie sampling is point-sampled at the MRP UV. Looks correct on
    glossy floors and metals; on a perfect mirror a multi-tap or
    LTC-textured integral would be more accurate.
  - Single area light per scene. Multiple screens would need
    namespaced globals or an array-and-loop in the shader.
  - URP-only. Won't compile against BIRP without changes.
  - With Contribute GI = ON + baked Lighting Mode, this realtime
    contribution adds on top of baked light - can over-bright. For
    the dance floor consider Contribute GI = OFF.


TROUBLESHOOTING
---------------
No contribution visible at all - run through this list:
  a. Cyan gizmo points INTO the room. If not, toggle Flip Normal.
  b. Only ONE VideoAreaLightSource is active in the scene.
  c. Receiving material's Shader is VideoAreaLight/Lit (or its Shader
     Graph wires the Custom Function output into Emission).
  d. Video Texture is assigned and the VideoPlayer is playing.
  e. Max Intensity is non-trivial (try 50).
  f. As a sanity check, set Two Sided = ON. If contribution appears,
     orientation was the issue; find the right Flip Normal value, then
     return Two Sided to OFF.

Highlight in the wrong place / mirrored / behind the screen:
  Screen Axis is wrong (XY for Quad, XZ for Plane), or the quad's
  visible face is on the opposite side - toggle Flip Normal.

Highlight is right shape but wrong colour:
  With Use Cookie OFF you get the average colour - that's expected.
  Tick Use Cookie to sample the actual video.

Cookie too sharp / too blurry:
  Cookie mip is roughness * 7. Adjust the multiplier in
  RectAreaLight.hlsl (VAL_SpecularContribution, around 0.5..2).

Highlight pops as the camera moves:
  MRP closest-point clamp can briefly snap when the reflection ray
  crosses a rectangle edge. Most visible at smoothness > 0.9. Lower
  smoothness slightly or raise Response Time.

Performance dips on Quest:
  Restrict the shader to the dance floor only, set Use Cookie OFF
  on everything else.
