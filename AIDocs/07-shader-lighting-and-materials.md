# Shader Lighting And Materials

This document covers direct lighting, shadow behavior, material scattering, and transparency/refraction in `Assets/Scripts/RayTracingCompute.compute`.

## Direct Lighting

Direct lighting comes from emissive sphere lights and emissive mesh-triangle lights.

`GetLightHittingPoint()` computes direct lighting by drawing one or more lights per shading point and shading each with stochastic samples across the light shape. Sphere lights use disk samples across the emissive sphere radius. Mesh lights are represented as one light per emissive triangle and use uniform barycentric samples across the triangle. Bounce 0 uses `max(1, _ShadowQuality + 1)` samples per shaded light, while later bounces use one sample per shaded light to reduce cost. The actual per-light shading work lives in `SampleSingleLight()`.

Each disk sample first checks `saturate(dot(directionToLight, hit.normal))`, and samples whose direction is at or behind the surface (N·L <= 0) are skipped entirely, so back-facing light directions contribute nothing. Lit samples are then evaluated by `GetDirectMaterialResponse()`: diffuse materials use albedo-weighted Lambert-style direct lighting, while metal/glass/water add a smoothness-controlled direct specular lobe with Schlick Fresnel. Water scales this direct specular response by opacity, with a small floor so fully clear water retains only faint Fresnel cues. Shadow rays are spawned from `hit.position` offset along the surface normal (`hit.normal * 0.001`), not along the light direction.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Light falloff uses a clamped inverse-square-style distance term scaled by light radius/area and `_LightFalloffScale`. Mesh-light samples are additionally weighted by the light triangle facing term, so back-facing triangle lights do not illuminate a shading point. Transparent shadow blockers attenuate direct light with accumulated RGB transmittance, so colored glass can filter light before it reaches the shaded point.

Direct-light segments that cross the procedural water volume are additionally attenuated by water absorption using an estimated underwater segment length. This affects underwater points lit from above the water, above-water points lit from underwater, and underwater-to-underwater lighting.

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

Transparent blockers multiply an accumulated shadow transmittance instead of replacing the result with the nearest transparent hit. `GetShadowTransmittance()` starts at white, multiplies in `GetTransparentShadowTransmittance()` for every transparent blocker found before the light, and returns black immediately for opaque blockers. This lets multiple glass layers compound energy loss and RGB filtering: a white light through blue glass then yellow glass is multiplied by both glass filters before contributing to direct lighting.

Sphere shadow blockers use `GetSphereDistanceThroughMedium()` to estimate the chord length through the sphere and apply distance-based absorption. Mesh shadow blockers currently treat every transparent triangle hit as a thin filter using `ThinTransparentSurfaceDistance`; this gives stacked colored mesh panes visible color/energy filtering without pairing mesh entry/exit faces inside the shadow query.

For shadow-BVH traversal details, see `06-shader-intersections-and-bvh.md`.

## Path Tracing Materials

`TracePath()` supports these material scattering paths:

- `Diffuse`: uses direct lighting and cosine-weighted hemisphere scattering on later bounces, attenuated by albedo. On bounce 0, smoothness blends the continuation ray between diffuse scattering and reflection, which allows the implicit ground plane's `_GroundSmoothness` to affect visible reflections.
- `Metal`: reflects around the surface normal, with smoothness controlling rough reflection direction randomization, attenuates by albedo, and receives direct specular highlights from sampled lights.
- `Glass`: uses Schlick Fresnel reflectance to randomly choose first-surface reflection versus transmission. Transmitted sphere and mesh paths use Snell refraction. Mesh glass supports bounded internal total internal reflection (TIR) while searching for an exit face. Transmitted glass paths are attenuated by distance-based RGB absorption. Glass and water also receive direct specular highlights from sampled lights.

The glass path is entered whenever `IsGlassMaterial(hit)` is true, which happens for `materialType == Glass` **or** for any hit with `opacity < 1.0`. A nominally `Diffuse` or `Metal` object with reduced opacity therefore scatters through the glass transmission/Fresnel path.

## Transparency And Refraction

Glass opacity controls how often the material transmits versus reflects. `GetTransmissionAmount(hit)` returns `1 - opacity`, and the glass path transmits with probability `(1 - opacity) * (1 - fresnelReflectance)`. Opacity `1.0` is therefore fully reflective/opaque, while opacity `0.99` only rarely transmits instead of collapsing every transmitted path to near-black.

Glass applies distance-based RGB absorption through `GetAbsorptionTransmittance()`. The shader treats `RayMaterial.Color`/sampled albedo as a per-channel filter color and raises it by `distanceThroughMedium * opacity`, with a small neutral absorption term (`GlassNeutralAbsorption`) so even nearly white glass loses some energy through distance/layers. This is Beer-Lambert-style behavior rather than a full spectral renderer, but it means stacked colored glass naturally compounds through path throughput and transparent shadows. Higher opacity means denser absorption, while lower opacity means a weaker color filter.

`GetGlassAbsorptionTransmittance()` uses `hit.distanceThroughOpacity` when available and falls back to `ThinTransparentSurfaceDistance` for thin/open transparent surfaces. Sphere glass writes this distance in `ApplySphereRefraction()`. Mesh glass writes it in `ApplyPlanarTransmission()` from the accumulated internal distance to the exit face, or from the distance to an interior hit when an object is found inside the transparent mesh before the exit face.

`RefractSnell()` implements Snell-law refraction and reports failure when the requested transition would exceed the critical angle. Glass-to-air failures are total internal reflection events and reflect the ray back into the current medium.

Transparent/glass sphere refraction is approximate. `ApplySphereRefraction()`:

1. Refracts from air into the sphere using `RefractSnell()`, or from sphere material back into air when the ray starts inside the sphere.
2. For entry hits, casts a bounded internal ray up to the current sphere exit while ignoring that current sphere.
3. If another scene object is hit before the exit, continues tracing inside the current sphere so interpenetrating transparent objects can be seen.
4. If no closer internal object is hit, estimates the exit point by finding a closest point across the sphere chord.
5. Computes the exit normal.
6. Refracts back out into air, or reflects on total internal reflection.

Glass material scattering uses opacity-scaled Schlick Fresnel reflectance to randomly choose first-surface reflection or transmission. Reflected glass paths blend from white toward material color as opacity increases, while transmitted paths are filtered by distance-based absorption. Internal TIR bounces consume from the same `_NumBounces` path budget as regular scene bounces, so a ray with only two bounces remaining can only spend two bounces on glass entry/exit/internal reflection work.

Water uses the same refraction/Fresnel helper, but its reflection probability is additionally scaled by opacity. At 0 opacity, water is still ray-intersected and refracts/transmits rays, but first-surface reflections and direct specular highlights are reduced to a small minimum so underwater objects remain visible. Water opacity controls the surface response; `_WaterAbsorptionStrength` controls distance-based volume absorption separately. Path segments that start underwater multiply throughput by exponential transmittance based on `_WaterColor`, `_WaterAbsorptionStrength`, and segment distance, so shallow bottom bounces remain brighter while deeper water becomes darker and more color-filtered.

Triangle mesh refraction uses `ApplyPlanarTransmission()` rather than the sphere helper:

1. Refract from air into the hit triangle using `RefractSnell()`.
2. Cast an internal ray against triangles with the same `meshIndex`.
3. Use the nearest internal triangle hit as the candidate exit face.
4. Run a bounded scene intersection query along the internal segment, ignoring the current transparent mesh. This uses the normal top-level/per-mesh BVH traversal and can find objects enclosed by the transparent mesh before the exit face.
5. If an interior object is found, continue tracing inside the transparent mesh so the next bounce shades that object.
6. If no interior object is found, try to refract from material back into air and continue the path from the exit point.
7. If the exit face exceeds the critical angle, reflect internally and repeat until the ray exits, hits an interior object, misses the closed mesh, or exhausts the remaining path-bounce budget.

This gives visible prism-like behavior for simple closed meshes such as pyramids while still allowing enclosed objects, such as a pencil inside a water cylinder, to be hit before the transparent mesh exit. It is still approximate: it assumes a mostly closed/convex mesh and does not track a full nested-medium stack.
