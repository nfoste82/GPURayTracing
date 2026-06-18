# Shader Lighting And Materials

This document covers direct lighting, shadow behavior, material scattering, and transparency/refraction in `Assets/Scripts/RayTracingCompute.compute`.

## Direct Lighting

Direct lighting comes from emissive sphere lights.

`GetLightHittingPoint()` computes direct lighting by taking stochastic disk samples across each emissive sphere light. Bounce 0 uses `max(1, _ShadowQuality + 1)` samples per light, while later bounces use one sample per light to reduce cost.

Each sample is weighted by `saturate(dot(directionToLight, hit.normal))` and samples whose direction is at or behind the surface (N·L <= 0) are skipped entirely, so back-facing light directions contribute nothing. Shadow rays are spawned from `hit.position` offset along the surface normal (`hit.normal * 0.001`), not along the light direction.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Light falloff uses a clamped inverse-square-style distance term scaled by light radius and `_LightFalloffScale`, although transparent shadow tinting remains approximate.

## Shadows

Shadow rays test blockers against regular spheres and mesh leaves, but not light leaves. Opaque blockers early-out immediately.

Transparent blockers can tint shadow light by using the blocking sphere color and opacity. The shader currently uses the nearest transparent hit before the light distance rather than accumulating transmittance through multiple transparent blockers.

For shadow-BVH traversal details, see `06-shader-intersections-and-bvh.md`.

## Path Tracing Materials

`TracePath()` supports these material scattering paths:

- `Diffuse`: uses direct lighting and cosine-weighted hemisphere scattering on later bounces, attenuated by albedo. On bounce 0, smoothness blends the continuation ray between diffuse scattering and reflection, which allows the implicit ground plane's `_GroundSmoothness` to affect visible reflections.
- `Metal`: reflects around the surface normal, with smoothness controlling rough reflection direction randomization, and attenuates by albedo.
- `Glass`: uses Schlick Fresnel reflectance to weight approximate sphere refraction for spheres. For mesh triangles, it uses approximate closed-mesh entry/exit refraction.

The glass path is entered whenever `IsGlassMaterial(hit)` is true, which happens for `materialType == Glass` **or** for any hit with `opacity < 1.0`. A nominally `Diffuse` or `Metal` object with reduced opacity therefore scatters through the glass transmission/Fresnel path.

## Transparency And Refraction

The amount of light transmitted through a transparent surface comes from `GetTransmissionAmount(hit) = clamp((1 - opacity) * 1.333, 0, 1)`. Because of the `1.333` multiplier, transmission saturates to fully transparent once `opacity` drops to about `0.25` rather than scaling linearly with opacity, which surprises users tuning the `Opacity` slider.

The `Refract()` helper is **not** a Snell's-law refraction. It is a custom linear approximation: it computes `(targetRefraction - sourceRefraction) / MaxRefractionDiff * MaxReflect`, scales that by `dot(normal, direction)`, and blends it into the incoming direction before renormalizing. No layer of the renderer implements true Snell's law per interface; both the sphere and mesh transmission paths build on this approximation.

Transparent/glass sphere refraction is approximate. `ApplySphereRefraction()`:

1. Refracts from air into the sphere using `Refract()`, or from sphere material back into air when the ray starts inside the sphere.
2. Estimates the exit point by finding a closest point across the sphere chord.
3. Computes the exit normal.
4. Refracts back out into air.

Glass material scattering uses Schlick Fresnel reflectance to weight transmission, but the transmitted ray still uses the project's approximate `Refract()` helper rather than a full Snell-law volume traversal. This avoids the high variance of randomly choosing reflection or transmission per sample.

Triangle mesh refraction uses `ApplyPlanarTransmission()` rather than the sphere helper:

1. Refract from air into the hit triangle using the project `Refract()` helper.
2. Cast an internal ray against triangles with the same `meshIndex`.
3. Use the nearest internal triangle hit as the exit face.
4. Refract from material back into air.
5. Continue the path from the exit point.

This gives visible prism-like behavior for simple closed meshes such as pyramids. It is still approximate: it assumes a mostly closed/convex mesh, does not handle nested media, and does not model distance-based absorption.

## Partial Shader Pieces

- `distanceThroughOpacity` is written for transparent/refraction calculations and transparent shadow logic, but is not part of a full absorption model.
