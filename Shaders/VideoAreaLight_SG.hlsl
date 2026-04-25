#ifndef VAL_VIDEO_AREA_LIGHT_SG_INCLUDED
#define VAL_VIDEO_AREA_LIGHT_SG_INCLUDED

// Shader Graph Custom Function entry point for the Video Area Light.
// Use this file from a Custom Function node:
//   - Type:           File
//   - Source:         this file (VideoAreaLight_SG.hlsl)
//   - Name:           VideoAreaLight_float (or VideoAreaLight_half)
//
// Wire the inputs (in this order):
//   - WorldPos     (Vector3)  - Position node, World space
//   - WorldNormal  (Vector3)  - Normal Vector node, World space
//   - WorldView    (Vector3)  - View Direction node, World space
//   - Roughness    (Float)    - 1 - Smoothness, or your roughness map
//   - BaseColor    (Vector3)  - albedo
//   - Metallic     (Float)    - 0..1
//   - UseCookie    (Float)    - 0 = solid colour fill, 1 = sample video cookie
//
// Outputs:
//   - Diffuse      (Vector3)  - add to your shader's emission or diffuse term
//   - Specular     (Vector3)  - add to your shader's emission or specular term
//
// Both outputs are already in linear colour space and integrate the area
// light's contribution; they are independent of, and additive to, your
// existing URP/Lit lighting from punctual lights.

#include "RectAreaLight.hlsl"

void VideoAreaLight_float(
    float3 WorldPos,
    float3 WorldNormal,
    float3 WorldView,
    float  Roughness,
    float3 BaseColor,
    float  Metallic,
    float  UseCookie,
    out float3 Diffuse,
    out float3 Specular)
{
    bool cookie = UseCookie > 0.5;
    VAL_EvaluateAreaLight(
        WorldPos, WorldNormal, WorldView,
        Roughness, BaseColor, Metallic,
        cookie, Diffuse, Specular);
}

void VideoAreaLight_half(
    float3 WorldPos,
    float3 WorldNormal,
    float3 WorldView,
    float  Roughness,
    float3 BaseColor,
    float  Metallic,
    float  UseCookie,
    out float3 Diffuse,
    out float3 Specular)
{
    VideoAreaLight_float(
        WorldPos, WorldNormal, WorldView,
        Roughness, BaseColor, Metallic,
        UseCookie, Diffuse, Specular);
}

#endif // VAL_VIDEO_AREA_LIGHT_SG_INCLUDED
