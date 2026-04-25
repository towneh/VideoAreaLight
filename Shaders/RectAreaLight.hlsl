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
    float alpha    = max(roughness * roughness, 1e-4);
    float alphaP   = saturate(alpha + halfDiag / max(2.0 * dist, 1e-5));

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

    // Karis energy compensation for the lobe widening
    float normFactor = alpha / max(alphaP, 1e-5);
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
        float2 cookieUV = mul(_VAL_CookieWorldToUV, float4(mrpWS, 1.0)).xy;
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
}

#endif // VAL_RECT_AREA_LIGHT_INCLUDED
