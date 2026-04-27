#ifndef VAL_RECT_AREA_LIGHT_INCLUDED
#define VAL_RECT_AREA_LIGHT_INCLUDED

// Rectangular area light approximation for a single emitter.
//
// Specular: Most Representative Point (MRP) trick popularised by
//   Brian Karis - "Real Shading in Unreal Engine 4" - SIGGRAPH 2013.
//   Closest point on the rectangle to the reflection ray, then GGX
//   with a roughness widened by the area's angular size.
//
// Diffuse: analytic cosine-weighted irradiance from a planar polygon.
//   The edge-sum form has been published since the 1980s (Baum/Lambert
//   form factor) and is independent of LTC.
//
// No precomputed lookup tables required. The whole thing is roughly
// 60-80 ALU per pixel including the cookie sample, no texture LUT
// fetches. Shipped under whatever license the surrounding project uses.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

#define VAL_PI 3.14159265359

TEXTURE2D(_VAL_CookieTex);  SAMPLER(sampler_VAL_CookieTex);

float4   _VAL_Positions[4];      // BL, BR, TR, TL in world space (CCW from +normal side)
float4   _VAL_Color;
float    _VAL_Intensity;
float4   _VAL_Normal;            // outward normal of the emitter (unit)
float    _VAL_TwoSided;
float    _VAL_Valid;
float4x4 _VAL_CookieWorldToUV;   // unused by MRP for sampling but kept for API parity

// Optional zone mask. When _VAL_ZoneEnabled is set, fragments outside the
// broadcaster's zone collider receive no contribution (lets walls block the
// light from leaking into adjacent rooms). _VAL_WorldToZone maps world
// space into the zone's local frame, recentered on the box's pivot, so the
// test reduces to abs(localPos) < halfExtents.
float    _VAL_ZoneEnabled;
float4x4 _VAL_WorldToZone;
float4   _VAL_ZoneHalfExtents;
float    _VAL_ZoneFeather;

// Optional baked visibility volumes. Up to 4 cascaded volumes can be active
// at once; each volume owns one slot and contributes multiplicatively.
// Fragments outside a slot's UVW bounds are unaffected by that slot
// (multiplied by 1) — this lets authors mix one coarse venue-wide volume
// with smaller fine volumes targeting problem areas, instead of paying
// fine-resolution memory across the whole venue. Each voxel stores the
// fraction of the screen rectangle visible from that world position.
//
// _VAL_VisCount tells the shader how many slots are populated (0 → none);
// slots above that number cost nothing because their `if` block is skipped.
TEXTURE3D(_VAL_VisTex0);  SAMPLER(sampler_VAL_VisTex0);
TEXTURE3D(_VAL_VisTex1);  SAMPLER(sampler_VAL_VisTex1);
TEXTURE3D(_VAL_VisTex2);  SAMPLER(sampler_VAL_VisTex2);
TEXTURE3D(_VAL_VisTex3);  SAMPLER(sampler_VAL_VisTex3);
int      _VAL_VisCount;
float4x4 _VAL_WorldToVisUVW0;
float4x4 _VAL_WorldToVisUVW1;
float4x4 _VAL_WorldToVisUVW2;
float4x4 _VAL_WorldToVisUVW3;

// Per-slot encoding mode. 0 = Scalar (R channel = visibility fraction),
// 1 = Quadrant (RGBA channels = per-screen-quadrant visibility, bilerped
// at runtime). Each volume sets its slot's mode independently, so a
// scene can mix scalar (cheap, default) and quadrant (4× memory,
// directionally accurate) volumes.
float    _VAL_VisMode0;
float    _VAL_VisMode1;
float    _VAL_VisMode2;
float    _VAL_VisMode3;

// ---------------------------------------------------------------------------
// Diffuse - polygon irradiance
// ---------------------------------------------------------------------------

float VAL_IntegrateEdge(float3 v1, float3 v2)
{
    float cosTheta = clamp(dot(v1, v2), -0.9999, 0.9999);
    float theta = acos(cosTheta);
    return cross(v1, v2).z * (theta / sin(theta));
}

void VAL_ClipQuadToHorizon(inout float3 L[5], out int n)
{
    int config = 0;
    if (L[0].z > 0.0) config += 1;
    if (L[1].z > 0.0) config += 2;
    if (L[2].z > 0.0) config += 4;
    if (L[3].z > 0.0) config += 8;

    n = 0;

    if      (config == 0)                                         {                                                                                                                                                            }
    else if (config == 1)  { n = 3; L[1] = -L[1].z * L[0] + L[0].z * L[1];  L[2] = -L[3].z * L[0] + L[0].z * L[3];                                                                                                              }
    else if (config == 2)  { n = 3; L[0] = -L[0].z * L[1] + L[1].z * L[0];  L[2] = -L[2].z * L[1] + L[1].z * L[2];                                                                                                              }
    else if (config == 3)  { n = 4; L[2] = -L[2].z * L[1] + L[1].z * L[2];  L[3] = -L[3].z * L[0] + L[0].z * L[3];                                                                                                              }
    else if (config == 4)  { n = 3; L[0] = -L[3].z * L[2] + L[2].z * L[3];  L[1] = -L[1].z * L[2] + L[2].z * L[1];  L[2] = L[2];                                                                                                }
    else if (config == 5)  { n = 0;                                                                                                                                                                                             }
    else if (config == 6)  { n = 4; L[0] = -L[0].z * L[1] + L[1].z * L[0];  L[3] = -L[3].z * L[2] + L[2].z * L[3];                                                                                                              }
    else if (config == 7)  { n = 5; L[4] = -L[3].z * L[0] + L[0].z * L[3];  L[3] = -L[3].z * L[2] + L[2].z * L[3];                                                                                                              }
    else if (config == 8)  { n = 3; L[0] = -L[0].z * L[3] + L[3].z * L[0];  L[1] = -L[2].z * L[3] + L[3].z * L[2];  L[2] = L[3];                                                                                                }
    else if (config == 9)  { n = 4; L[1] = -L[1].z * L[0] + L[0].z * L[1];  L[2] = -L[2].z * L[3] + L[3].z * L[2];                                                                                                              }
    else if (config == 10) { n = 0;                                                                                                                                                                                             }
    else if (config == 11) { n = 5; L[4] = L[3]; L[3] = -L[2].z * L[3] + L[3].z * L[2];  L[2] = -L[2].z * L[1] + L[1].z * L[2];                                                                                                 }
    else if (config == 12) { n = 4; L[1] = -L[1].z * L[2] + L[2].z * L[1];  L[0] = -L[0].z * L[3] + L[3].z * L[0];                                                                                                              }
    else if (config == 13) { n = 5; L[4] = L[3]; L[3] = L[2]; L[2] = -L[1].z * L[2] + L[2].z * L[1]; L[1] = -L[1].z * L[0] + L[0].z * L[1];                                                                                     }
    else if (config == 14) { n = 5; L[4] = -L[0].z * L[3] + L[3].z * L[0];  L[0] = -L[0].z * L[1] + L[1].z * L[0];                                                                                                              }
    else                   { n = 4;                                                                                                                                                                                             }

    if (n == 3) L[3] = L[0];
    if (n == 4) L[4] = L[0];
}

float VAL_DiffuseFormFactor(
    float3 N, float3 V, float3 P,
    float3 P0, float3 P1, float3 P2, float3 P3,
    bool twoSided)
{
    float3 T1 = normalize(V - N * dot(V, N));
    float3 T2 = cross(N, T1);
    float3x3 worldToTangent = float3x3(T1, T2, N);

    float3 L[5];
    L[0] = mul(worldToTangent, P0 - P);
    L[1] = mul(worldToTangent, P1 - P);
    L[2] = mul(worldToTangent, P2 - P);
    L[3] = mul(worldToTangent, P3 - P);
    L[4] = float3(0, 0, 0);

    int n;
    VAL_ClipQuadToHorizon(L, n);
    if (n == 0) return 0.0;

    L[0] = normalize(L[0]);
    L[1] = normalize(L[1]);
    L[2] = normalize(L[2]);
    L[3] = normalize(L[3]);
    L[4] = normalize(L[4]);

    float sum = 0.0;
    sum += VAL_IntegrateEdge(L[0], L[1]);
    sum += VAL_IntegrateEdge(L[1], L[2]);
    sum += VAL_IntegrateEdge(L[2], L[3]);
    if (n >= 4) sum += VAL_IntegrateEdge(L[3], L[4]);
    if (n == 5) sum += VAL_IntegrateEdge(L[4], L[0]);

    // The polygon-edge sign depends on the orientation of the corners relative
    // to the receiver, which can flip with the receiver pose. We take the
    // magnitude here and apply one-sidedness via an explicit facing test
    // against the polygon plane below, in the public entry point.
    return abs(sum) / (2.0 * VAL_PI);
}

// ---------------------------------------------------------------------------
// Per-quadrant visibility bilerp (Quadrant encoding mode)
// ---------------------------------------------------------------------------

// Quadrant-encoded visibility texture stores fractional visibility per
// screen quadrant: R = BL (u<0.5, v<0.5), G = BR (u>=0.5, v<0.5),
// B = TL (u<0.5, v>=0.5), A = TR (u>=0.5, v>=0.5). Bilerp gives a
// per-uv visibility for the rectangle's contribution at runtime.
float VAL_QuadrantVisibility(float4 quadrants, float2 uv)
{
    float2 t = saturate(uv);
    float bottomRow = lerp(quadrants.r, quadrants.g, t.x);
    float topRow    = lerp(quadrants.b, quadrants.a, t.x);
    return lerp(bottomRow, topRow, t.y);
}

// ---------------------------------------------------------------------------
// Specular - Most Representative Point on the rectangle
// ---------------------------------------------------------------------------

void VAL_FindMRP(
    float3 P, float3 R,
    float3 P0, float3 P1, float3 P2, float3 P3,
    float3 planeNormal,
    out float3 mrpWS, out float2 mrpUV, out bool valid, out float insideMask)
{
    // Rectangle local edges. Convention: P0 = BL, P1 = BR, P2 = TR, P3 = TL.
    float3 ex = P1 - P0; // u-axis (left -> right across the screen)
    float3 ey = P3 - P0; // v-axis (bottom -> top)

    // Ray-plane intersection. denom < 0 means R is heading toward the
    // light's front face (R points from receiver to plane in -planeNormal sense).
    float3 toPlane = P0 - P;
    float denom = dot(R, planeNormal);
    float t = dot(toPlane, planeNormal) / (denom + (denom == 0.0 ? 1e-5 : 0.0));

    valid = (t > 0.0);
    if (!valid)
    {
        // Ray goes away from the plane - return a degenerate result; the
        // caller will weight it by NoL which will end up zero anyway, but
        // we avoid NaNs by snapping to the rect centre.
        mrpWS = (P0 + P1 + P2 + P3) * 0.25;
        mrpUV = float2(0.5, 0.5);
        insideMask = 0.0;
        return;
    }

    float3 hitWS = P + R * t;
    float3 d = hitWS - P0;

    float lex2 = max(dot(ex, ex), 1e-8);
    float ley2 = max(dot(ey, ey), 1e-8);
    float uRaw = dot(d, ex) / lex2;
    float vRaw = dot(d, ey) / ley2;
    float u = saturate(uRaw);
    float v = saturate(vRaw);

    mrpWS = P0 + ex * u + ey * v;
    mrpUV = float2(u, v);

    // insideMask: 1 if the unclamped reflection-ray intersection landed
    // inside the rectangle [0,1]^2; 0 if it landed outside. A small
    // smoothstep falloff softens the edge so the highlight tapers off
    // cleanly instead of cutting hard. Without this mask, MRP's saturate
    // clamps the hit to the nearest edge and the cookie's edge texels
    // smear out across receivers whose reflection rays never actually
    // touched the screen.
    float du = max(0.0, max(-uRaw, uRaw - 1.0));
    float dv = max(0.0, max(-vRaw, vRaw - 1.0));
    float distOutside = sqrt(du * du + dv * dv);
    insideMask = 1.0 - smoothstep(0.0, 0.05, distOutside);
}

float3 VAL_SpecularContribution(
    float3 N, float3 V, float3 P,
    float roughness, float3 F0,
    float3 P0, float3 P1, float3 P2, float3 P3,
    float3 lightColor, bool twoSided,
    bool useCookie)
{
    float3 ex = P1 - P0;
    float3 ey = P3 - P0;
    float3 nrm = normalize(cross(ex, ey));

    float3 R = reflect(-V, N);

    // For a one-sided light, ignore reflection rays heading away from the
    // light's front face. dot(R, nrm) < 0 means R points toward the front.
    float facingTerm = -dot(R, nrm);
    if (!twoSided && facingTerm <= 0.0) return float3(0, 0, 0);

    float3 mrpWS;
    float2 mrpUV;
    bool   hit;
    float  insideMask;
    VAL_FindMRP(P, R, P0, P1, P2, P3, nrm, mrpWS, mrpUV, hit, insideMask);
    if (!hit) return float3(0, 0, 0);

    float3 Lv   = mrpWS - P;
    float  dist = length(Lv);
    float3 Ld   = Lv / max(dist, 1e-5);

    float NoL = saturate(dot(N, Ld));
    if (NoL <= 0.0) return float3(0, 0, 0);

    float3 H = normalize(Ld + V);
    float NoH = saturate(dot(N, H));
    float NoV = saturate(dot(N, V));
    float VoH = saturate(dot(V, H));

    // Karis: widen the GGX lobe by the rectangle's angular extent at the receiver.
    float lex   = sqrt(dot(ex, ex));
    float ley   = sqrt(dot(ey, ey));
    float halfDiag = 0.5 * sqrt(lex * lex + ley * ley);
    float angularSpread = halfDiag / max(2.0 * dist, 1e-5);
    float alpha    = max(roughness * roughness, 1e-4);
    // alphaP is the lobe width widened by the rectangle's angular extent
    // (Karis). The previous saturate(...) clamp at 1 introduced a hard
    // derivative discontinuity that, multiplied with the other BRDF
    // factors, manifested as a visible band on the floor along the
    // locus where the kick-in occurred. Replace with a smooth
    // tanh-based asymptotic cap: stays near-linear for typical values
    // (alpha + angularSpread <= ~1) and asymptotes smoothly to Mmax
    // for large values. No derivative discontinuities anywhere.
    //
    // Mmax sets how aggressively to cap the lobe width near the source.
    // Higher = sharper near-source but more original-style haze; lower =
    // softer transitions but more diffuse near-source reflection.
    const float Mmax = 1.0;
    float alphaP   = Mmax * tanh((alpha + angularSpread) / Mmax);

    // GGX D
    float a2 = alphaP * alphaP;
    float dnm = (NoH * a2 - NoH) * NoH + 1.0;
    float D = a2 / (VAL_PI * dnm * dnm);

    // Smith joint visibility (UE4-style direct-lighting approximation)
    float k  = (alphaP + 1.0); k = (k * k) * 0.125;
    float Gv = NoV / (NoV * (1.0 - k) + k);
    float Gl = NoL / (NoL * (1.0 - k) + k);
    float G  = Gv * Gl;

    // Schlick fresnel at the half-vector
    float3 F = F0 + (1.0 - F0) * pow(1.0 - VoH, 5.0);

    // Karis energy compensation for the lobe widening, with the
    // numerator floored at the source's angular size. A reflection of
    // an area source physically can't be sharper than the source's
    // solid angle, so as roughness drops below that floor we stop
    // pretending alpha keeps shrinking. Without this floor, (alpha /
    // alphaP)^2 collapses to zero on near-mirror receivers
    // (smoothness ~ 1) and the highlight disappears.
    float alphaForNorm = max(alpha, angularSpread);
    float normFactor = alphaForNorm / max(alphaP, 1e-5);
    normFactor *= normFactor;

    float3 brdf = D * G * F * normFactor / max(4.0 * NoV * NoL, 1e-5);

    float3 emit = lightColor;
    if (useCookie)
    {
        // Sample the cookie at the world-space hit point through the broadcaster's
        // worldToUV matrix instead of mrpUV. The matrix is built from
        // transform.worldToLocalMatrix and is unaffected by Flip Normal's corner
        // reordering, so the cookie sample stays in the same orientation as the
        // video appears on the screen mesh - no horizontal mirror artifact.
        // Clamp the cookie UV defensively: the proper fix for cookie-edge
        // bleeding is the texture's wrap mode (should be Clamp), but if a
        // user-supplied video texture has wrapMode = Repeat, bilinear at
        // UVs near 0/1 pulls colours from the opposite edge and the
        // reflection bleeds bright bands past its actual footprint. Worth
        // the one ALU.
        float2 cookieUV = saturate(mul(_VAL_CookieWorldToUV, float4(mrpWS, 1.0)).xy);
        float mip = roughness * 7.0;
        emit *= SAMPLE_TEXTURE2D_LOD(_VAL_CookieTex, sampler_VAL_CookieTex, cookieUV, mip).rgb;
    }

    // The specular response above already absorbed the 4*NoV*NoL denominator;
    // we still scale by NoL to weight the directional lighting. The
    // insideMask fades the highlight out where the reflection ray landed
    // outside the rectangle, preventing the cookie's edge texels from
    // smearing across the floor beyond the screen's actual reflection.
    return emit * brdf * NoL * insideMask;
}

// ---------------------------------------------------------------------------
// Public entry point. Outputs additive diffuse + specular contributions in
// linear colour space. Sum these into your shader's emission (or per-pixel
// lighting accumulator).
// ---------------------------------------------------------------------------

void VAL_EvaluateAreaLight(
    float3 worldPos,
    float3 worldNormal,
    float3 worldView,
    float  roughness,
    float3 baseColor,
    float  metallic,
    bool   useCookie,
    out float3 diffuseOut,
    out float3 specularOut)
{
    diffuseOut  = float3(0, 0, 0);
    specularOut = float3(0, 0, 0);

    if (_VAL_Valid < 0.5) return;

    float3 N = normalize(worldNormal);
    float3 V = normalize(worldView);

    float3 P0 = _VAL_Positions[0].xyz;
    float3 P1 = _VAL_Positions[1].xyz;
    float3 P2 = _VAL_Positions[2].xyz;
    float3 P3 = _VAL_Positions[3].xyz;

    // Zone OBB feather mask. Cheap analytic test; cuts entire venues out
    // before any expensive math runs.
    float zoneFalloff = 1.0;
    if (_VAL_ZoneEnabled > 0.5)
    {
        float3 zl = mul(_VAL_WorldToZone, float4(worldPos, 1.0)).xyz;
        float3 d = abs(zl) - _VAL_ZoneHalfExtents.xyz;
        float outside = length(max(d, 0.0));
        zoneFalloff = 1.0 - saturate(outside / max(_VAL_ZoneFeather, 1e-4));
        if (zoneFalloff <= 0.0) return;
    }

    // Cascaded visibility volumes (up to 4). Each slot independently
    // chooses Scalar or Quadrant encoding via _VAL_VisMode#:
    //   Scalar (0):    sample .r once, multiply both diffuse and specular
    //                  by the same scalar visibility (simplest, cheapest).
    //   Quadrant (1):  sample 4-channel quadrant texture, bilerp at the
    //                  fragment's relevant screen UV (orthogonal projection
    //                  of worldPos for diffuse, MRP UV for specular).
    //                  Produces correct directional shadows on surfaces
    //                  facing the source.
    // Slots multiply (cascading model). Slots whose UVW lies outside [0,1]
    // contribute 1 to both, so a fine slot can sit inside a coarse one
    // without double-occluding.
    float2 diffuseUV  = mul(_VAL_CookieWorldToUV, float4(worldPos, 1.0)).xy;
    float3 R_dir      = reflect(-V, N);
    float3 _val_mrpWS; float2 specularUV; bool _val_mrpValid; float _val_mrpInside;
    VAL_FindMRP(worldPos, R_dir, P0, P1, P2, P3, _VAL_Normal.xyz,
                _val_mrpWS, specularUV, _val_mrpValid, _val_mrpInside);

    float visDiffuse  = 1.0;
    float visSpecular = 1.0;
    #define VAL_SAMPLE_VIS_SLOT(SLOT) \
        [branch] if (_VAL_VisCount > SLOT) \
        { \
            float3 _val_uvw##SLOT = mul(_VAL_WorldToVisUVW##SLOT, float4(worldPos, 1.0)).xyz; \
            if (all(_val_uvw##SLOT >= 0.0) && all(_val_uvw##SLOT <= 1.0)) \
            { \
                float4 _val_t##SLOT = SAMPLE_TEXTURE3D(_VAL_VisTex##SLOT, sampler_VAL_VisTex##SLOT, _val_uvw##SLOT); \
                if (_VAL_VisMode##SLOT >= 0.5) \
                { \
                    visDiffuse  *= VAL_QuadrantVisibility(_val_t##SLOT, diffuseUV); \
                    visSpecular *= VAL_QuadrantVisibility(_val_t##SLOT, specularUV); \
                } \
                else \
                { \
                    float _val_s##SLOT = _val_t##SLOT.r; \
                    visDiffuse  *= _val_s##SLOT; \
                    visSpecular *= _val_s##SLOT; \
                } \
            } \
        }
    VAL_SAMPLE_VIS_SLOT(0)
    VAL_SAMPLE_VIS_SLOT(1)
    VAL_SAMPLE_VIS_SLOT(2)
    VAL_SAMPLE_VIS_SLOT(3)
    #undef VAL_SAMPLE_VIS_SLOT

    // Fade specular visibility at the MRP saturation boundary. Without
    // this, the bilerp at saturated UV (Quadrant) or the .r read at
    // saturated UV (Scalar) can produce an intensity kink along the
    // floor curve where the reflection ray clips the screen edge,
    // visible as a faint line bending with view direction.
    float visSpecularEffective = lerp(1.0, visSpecular, _val_mrpInside);

    bool twoSided = _VAL_TwoSided > 0.5;
    float3 lightColor = _VAL_Color.rgb * _VAL_Intensity;

    // Facing test: for a one-sided emitter, only receivers on the +normal
    // side of the polygon should receive contribution. The polygon-integral
    // magnitude itself is sign-agnostic (we use abs() inside it).
    float facing = dot(worldPos - P0, _VAL_Normal.xyz);
    float facingMask = twoSided ? 1.0 : (facing > 0.0 ? 1.0 : 0.0);

    // Diffuse: analytic polygon irradiance (cookie averages out, so we use
    // the broadcast average colour, which is already what _VAL_Color carries).
    float ffDiffuse = VAL_DiffuseFormFactor(N, V, worldPos, P0, P1, P2, P3, twoSided);
    diffuseOut = lightColor * ffDiffuse * baseColor * (1.0 - metallic) * facingMask;

    // Specular: Karis MRP with optional cookie at the MRP UV.
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);
    specularOut = VAL_SpecularContribution(
        N, V, worldPos, roughness, F0,
        P0, P1, P2, P3,
        lightColor, twoSided, useCookie) * facingMask;

    diffuseOut  *= zoneFalloff * visDiffuse;
    specularOut *= zoneFalloff * visSpecularEffective;
}

#endif // VAL_RECT_AREA_LIGHT_INCLUDED
