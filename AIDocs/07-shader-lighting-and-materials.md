# Shader Lighting And Materials

This document covers direct lighting, shadow behavior, material scattering, and transparency/refraction in `Assets/Scripts/RayTracingCompute.compute`.

## Direct Lighting

Direct lighting comes from emissive sphere lights.

`GetLightHittingPoint()` computes direct lighting by drawing one or more lights per shading point and shading each with stochastic disk samples across the emissive sphere. Bounce 0 uses `max(1, _ShadowQuality + 1)` disk samples per shaded light, while later bounces use one sample per shaded light to reduce cost. The actual per-light shading work lives in `SampleSingleLight()`.

Each disk sample is weighted by `saturate(dot(directionToLight, hit.normal))` and samples whose direction is at or behind the surface (N·L <= 0) are skipped entirely, so back-facing light directions contribute nothing. Shadow rays are spawned from `hit.position` offset along the surface normal (`hit.normal * 0.001`), not along the light direction.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Light falloff uses a clamped inverse-square-style distance term scaled by light radius and `_LightFalloffScale`, although transparent shadow tinting remains approximate.

### Light Sampling Strategies

`_LightSamplingStrategy` (from `GameManager.lightSamplingStrategy`) selects which lights each hit shades. All three strategies are unbiased estimators of the same total direct light; they trade per-frame noise for cost.

- **AllLights (0)**: shades every light each hit. Most accurate per frame; cost scales linearly with light count. This is the most expensive strategy in many-light scenes.
- **UniformRandom (1)**: draws `_LightSampleCount` lights uniformly at random and applies a `lightCount / drawCount` Monte Carlo correction. Cheapest, but noisiest, because samples swing between near-black distant lights and bright nearby ones.
- **ImportanceSampled (2)**: draws `_LightSampleCount` lights with probability proportional to a cheap `luminance(emission) * falloff(distanceSquared, radius)` weight (`LightImportanceWeight()`), then divides each contribution by its selection probability. Far less noise per sample than UniformRandom because samples concentrate on bright/nearby lights, while distant lights keep a nonzero pick probability so the result stays unbiased. The weight uses squared distance directly (no `sqrt`) and mirrors `GetDirectLightFalloff()` math.

For the random/importance strategies, if `_LightSampleCount` would cover (nearly) every light anyway, `GetLightHittingPoint()` falls back to all-lights behavior (weight `1`, no `1/pdf` scaling) to avoid needless selection variance at the same cost.

`_MaxLightSamples` is a separate diagnostic cap: when positive, it clamps how many lights any strategy considers, which was used to confirm the per-hit light loop is the dominant cost in `Benchmark_ManyLights`.

ImportanceSampled only weights up to `MaxImportanceLights` (`128`) lights; lights beyond that are ignored for importance weighting. `GameManager` logs a one-time warning when the scene exceeds this count while ImportanceSampled is active.

### Light Sampling Structure And Compile-Time Constraint

`GetLightHittingPoint()` is deliberately written with a **single** inlined `SampleSingleLight()` call site inside one `[loop]`. A helper, `SelectLightForDraw()`, isolates the cheap per-strategy light selection and weighting, while the expensive, BVH-traversing `SampleSingleLight()` body is called once. Inlining `SampleSingleLight()` at multiple call sites (one per strategy branch) previously made the Metal/HLSL compiler duplicate the shadow BVH traversal loop many times, causing multi-minute shader compiles that hung Unity on "Importing Assets". Keep direct-light changes within this single-call-site shape. See `Tools > Ray Tracing > Precompile Compute Shader` for surfacing compile time/errors from edit mode.

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
3. Use the nearest internal triangle hit as the candidate exit face.
4. Run a bounded scene intersection query along the internal segment, ignoring the current transparent mesh. This uses the normal top-level/per-mesh BVH traversal and can find objects enclosed by the transparent mesh before the exit face.
5. If an interior object is found, continue tracing inside the transparent mesh so the next bounce shades that object.
6. If no interior object is found, refract from material back into air and continue the path from the exit point.

This gives visible prism-like behavior for simple closed meshes such as pyramids while still allowing enclosed objects, such as a pencil inside a water cylinder, to be hit before the transparent mesh exit. It is still approximate: it assumes a mostly closed/convex mesh, does not track a full nested-medium stack, and does not model distance-based absorption.

## Partial Shader Pieces

- `distanceThroughOpacity` is written for transparent/refraction calculations and transparent shadow logic, but is not part of a full absorption model.
