# Shader Lighting And Materials

This document covers direct lighting, shadow behavior, material scattering, and transparency/refraction in `Assets/Scripts/RayTracingCompute.compute`.

## Direct Lighting

Direct lighting comes from emissive sphere lights and emissive mesh-triangle lights.

`GetLightHittingPoint()` computes direct lighting by drawing one or more lights per shading point and shading each with stochastic samples across the light shape. Sphere lights use disk samples across the emissive sphere radius. Mesh lights are represented as one light per emissive triangle and use uniform barycentric samples across the triangle. Bounce 0 uses `max(1, _ShadowQuality + 1)` samples per shaded light, while later bounces use one sample per shaded light to reduce cost. The actual per-light shading work lives in `SampleSingleLight()`.

Each disk sample first checks `saturate(dot(directionToLight, hit.normal))`, and samples whose direction is at or behind the surface (N·L <= 0) are skipped entirely, so back-facing light directions contribute nothing. Lit samples are evaluated by the shared `EvaluateMaterialBrdf()`: diffuse materials use energy-reduced Lambert reflection and all reflective materials use a GGX microfacet lobe with Schlick Fresnel and Smith masking-shadowing. Water scales its dielectric Fresnel base reflectance by opacity, with a small floor so fully clear water retains faint Fresnel cues; unlike tinted glass, water albedo is reserved for transmission/absorption and does not tint the direct-reflection F0. Shadow rays are spawned from `hit.position` offset along the surface normal (`hit.normal * 0.001`), not along the light direction.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Light falloff uses a clamped inverse-square-style distance term scaled by light radius/area and `_LightFalloffScale`. Mesh-light samples are additionally weighted by the light triangle facing term, so back-facing triangle lights do not illuminate a shading point. Transparent shadow blockers attenuate direct light with accumulated RGB transmittance, so colored glass can filter light before it reaches the shaded point.

Direct-light segments that cross the procedural water volume are additionally attenuated by water absorption using an estimated underwater segment length. This affects underwater points lit from above the water, above-water points lit from underwater, and underwater-to-underwater lighting.

Explicit triangle-light samples and opaque BRDF continuation samples are combined with power-heuristic multiple importance sampling. Triangle area samples are converted to solid-angle PDFs, include the active light-selection probability, and account for the number of samples taken by each technique. Emissive triangle hits reached by an opaque BRDF sample receive the complementary weight. Sphere lights are excluded because their historical falloff-scaled direct-light contribution is not the same estimator as emissive-hit radiance; weighting them as complementary techniques creates dark centers in reflected sphere lights. Glass/water transmission and zero-radius light fallbacks retain their established behavior.

### Light Sampling Strategies

`_LightSamplingStrategy` (from `GameManager.lightSamplingStrategy`) selects which lights each hit shades. The strategies trade per-frame noise for cost. Uniform and importance sampling are unbiased only over the set of lights they can select; the current importance cap described below can omit lights from that set.

- **AllLights (0)**: shades every light each hit. Most accurate per frame; cost scales linearly with light count. This is the most expensive strategy in many-light scenes.
- **UniformRandom (1)**: draws `_LightSampleCount` lights uniformly at random and applies a `lightCount / drawCount` Monte Carlo correction. Cheapest, but noisiest, because samples swing between near-black distant lights and bright nearby ones.
- **ImportanceSampled (2)**: draws `_LightSampleCount` lights with probability proportional to a cheap `luminance(emission) * falloff(distanceSquared, radius)` weight (`LightImportanceWeight()`), then divides each contribution by its selection probability. Far less noise per sample than UniformRandom because samples concentrate on bright/nearby lights. Within the capped considered set, distant lights keep a nonzero pick probability. The weight uses squared distance directly (no `sqrt`) and mirrors `GetDirectLightFalloff()` math.

For the random/importance strategies, if `_LightSampleCount` would cover (nearly) every light anyway, `GetLightHittingPoint()` falls back to all-lights behavior (weight `1`, no `1/pdf` scaling) to avoid needless selection variance at the same cost.

`_MaxLightSamples` is a separate diagnostic cap: when positive, it clamps how many lights any strategy considers, which was used to confirm the per-hit light loop is the dominant cost in `Benchmark_ManyLights`.

ImportanceSampled only weights up to `MaxImportanceLights` (`128`) lights; lights beyond that are ignored for importance weighting. `GameManager` logs a one-time warning when the scene exceeds this count while ImportanceSampled is active. Because omitted lights have zero selection probability, this mode is biased relative to the full scene when the cap is exceeded. Emissive mesh triangles each count as a light entry, so a tessellated mesh light can reach the limit quickly.

### Light Sampling Structure And Compile-Time Constraint

`GetLightHittingPoint()` is deliberately written with a **single** inlined `SampleSingleLight()` call site inside one `[loop]`. A helper, `SelectLightForDraw()`, isolates the cheap per-strategy light selection and weighting, while the expensive, BVH-traversing `SampleSingleLight()` body is called once. Inlining `SampleSingleLight()` at multiple call sites (one per strategy branch) previously made the Metal/HLSL compiler duplicate the shadow BVH traversal loop many times, causing multi-minute shader compiles that hung Unity on "Importing Assets". Keep direct-light changes within this single-call-site shape. See `Tools > Ray Tracing > Precompile Compute Shader` for surfacing compile time/errors from edit mode.

## Shadows

Shadow rays test blockers against regular spheres and mesh leaves, but not light leaves. Opaque blockers early-out immediately.

Transparent blockers multiply an accumulated shadow transmittance instead of replacing the result with the nearest transparent hit. `GetShadowTransmittance()` starts at white, processes boundaries in distance order, attenuates each segment from the active medium, and returns black immediately for opaque blockers. This lets multiple glass layers compound energy loss and RGB filtering: a white light through blue glass then yellow glass is multiplied by both glass filters before contributing to direct lighting.

Closed sphere and mesh shadow blockers use ordered entry/exit boundaries and apply distance-based absorption over the actual segment inside each active medium. Properly nested blockers reuse the production medium identity rules. Open meshes, or mesh entries without a valid paired exit before the light, retain the explicit `ThinTransparentSurfaceDistance` fallback. Transparent traversal stops when transmittance is negligible and has a fixed crossing limit; opaque-only scenes retain their cheaper boolean occlusion path.

For shadow-BVH traversal details, see `06-shader-intersections-and-bvh.md`.

## Path Tracing Materials

`TracePath()` supports these material scattering paths:

- `Diffuse`: uses a mixture of cosine-weighted Lambert sampling and GGX reflection. Direct lighting and continuation rays use the same BRDF, roughness mapping, and mixture PDF.
- `Metal`: uses GGX reflection sampling with albedo as the Fresnel base reflectance. Direct highlights and continuation reflections therefore share their lobe shape and PDF.
- `Glass`: uses Schlick Fresnel reflectance to randomly choose first-surface reflection versus transmission. Transmitted sphere and mesh paths use Snell refraction. Mesh glass supports bounded internal total internal reflection (TIR) while searching for an exit face. Transmitted glass paths are attenuated by distance-based RGB absorption. Glass and water also receive direct specular highlights from sampled lights.

`TracePath()` carries a fixed-capacity medium stack. Camera rays initialize the stack with containing water and translucent spheres, with containing spheres ordered outermost to innermost. Transmitted water, sphere-glass, and mesh-glass paths update that state; reflection and TIR do not. Absorption is evaluated for each traveled segment from the active medium. Path-selection Fresnel and the sphere, mesh, and water transmission helpers use the current medium IOR as the source and the entered medium or revealed parent IOR as the target. Exiting an interpenetrating sphere removes that sphere by object identity even when another overlapping sphere is currently active; the transition remains in the active overlap medium instead of producing an unmatched exit and stale absorption state.

Mesh hits retain both a shading normal and the triangle's geometric normal. `RayMaterial.InterpolateNormals` barycentrically interpolates imported vertex normals for direct lighting, BRDF sampling, and opaque reflection. Ray offsets, transparent boundaries, and mesh refraction continue to use the geometric normal so smooth shading does not change the actual polygonal volume.

Opaque continuation throughput uses `brdf * abs(N dot L) / pdf`. The common roughness conversion is `roughness = 1 - smoothness`, `alpha = roughness^2`, with a small roughness floor to keep mirror-like GGX evaluation finite. Glass/water transmission and Fresnel branch selection retain their medium-stack-specific path; their direct reflection uses the shared GGX evaluator.

Each uploaded emissive triangle stores the matching `_Lights` index, and emissive sphere hits already use their light-buffer index. This identity lets a BRDF-sampled emissive hit reconstruct the same light-selection and shape PDF used by next-event estimation.

The glass path is entered whenever `IsGlassMaterial(hit)` is true, which happens for `materialType == Glass` **or** for any hit with `opacity < 1.0`. A nominally `Diffuse` or `Metal` object with reduced opacity therefore scatters through the glass transmission/Fresnel path.

## Transparency And Refraction

Glass opacity controls how often the material transmits versus reflects. `GetTransmissionAmount(hit)` returns `1 - opacity`, and the glass path transmits with probability `(1 - opacity) * (1 - fresnelReflectance)`. Opacity `1.0` is therefore fully reflective/opaque, while opacity `0.99` only rarely transmits instead of collapsing every transmitted path to near-black.

Glass applies distance-based RGB absorption through the active medium's per-segment transmittance. The shader treats `RayMaterial.Color`/sampled albedo as a per-channel filter color and raises it by `distanceThroughMedium * opacity`, with a small neutral absorption term (`GlassNeutralAbsorption`) so even nearly white glass loses some energy through distance/layers. This is Beer-Lambert-style behavior rather than a full spectral renderer, but it means stacked colored glass naturally compounds through path throughput and transparent shadows. Higher opacity means denser absorption, while lower opacity means a weaker color filter.

Sphere and closed-mesh helpers report only internal distance they consume while crossing a volume within one scattering operation. Paths that remain inside carry the medium stack into the next bounce, where regular segment attenuation applies. Open meshes that have no paired exit retain the explicit `ThinTransparentSurfaceDistance` fallback.

`RefractSnell()` implements Snell-law refraction and reports failure when the requested transition would exceed the critical angle. Glass-to-air failures are total internal reflection events and reflect the ray back into the current medium.

Transparent/glass sphere refraction is approximate. `ApplySphereRefraction()`:

1. Refracts from the current stack medium into the sphere using `RefractSnell()`, or from the sphere into the revealed parent medium on an exit hit.
2. For entry hits, casts a bounded internal ray up to the current sphere exit while ignoring that current sphere.
3. If another scene object is hit before the exit, continues tracing inside the current sphere so interpenetrating transparent objects can be seen.
4. If no closer internal object is hit, estimates the exit point by finding a closest point across the sphere chord.
5. Computes the exit normal.
6. Refracts back into the source/parent medium, or reflects on total internal reflection.

Glass material scattering uses opacity-scaled Schlick Fresnel reflectance to randomly choose first-surface reflection or transmission. Reflected glass paths blend from white toward material color as opacity increases, while transmitted paths are filtered by distance-based absorption. Internal TIR bounces consume from the same `_NumBounces` path budget as regular scene bounces, so a ray with only two bounces remaining can only spend two bounces on glass entry/exit/internal reflection work.

Water uses the same refraction/Fresnel helper, but its reflection probability is additionally scaled by opacity. At 0 opacity, water is still ray-intersected and refracts/transmits rays, but first-surface reflections and direct specular highlights are reduced to a small minimum so underwater objects remain visible. Water opacity controls the surface response; `_WaterAbsorptionStrength` controls distance-based volume absorption separately. Path segments that start underwater multiply throughput by exponential transmittance based on `_WaterColor`, `_WaterAbsorptionStrength`, and segment distance, so shallow bottom bounces remain brighter while deeper water becomes darker and more color-filtered.

Triangle mesh refraction uses `ApplyPlanarTransmission()` rather than the sphere helper:

1. Refract from the current stack medium into the hit triangle using `RefractSnell()`; an already-active matching mesh boundary instead refracts directly into its parent medium.
2. Cast an internal ray against triangles with the same `meshIndex`.
3. Use the nearest internal triangle hit as the candidate exit face.
4. Run a bounded scene intersection query along the internal segment, ignoring the current transparent mesh. This uses the normal top-level/per-mesh BVH traversal and can find objects enclosed by the transparent mesh before the exit face.
5. If an interior object is found, continue tracing inside the transparent mesh so the next bounce shades that object.
6. If no interior object is found, try to refract from the material back into the source/parent medium and continue the path from the exit point.
7. If the exit face exceeds the critical angle, reflect internally and repeat until the ray exits, hits an interior object, misses the closed mesh, or exhausts the remaining path-bounce budget.

This gives visible prism-like behavior for simple closed meshes such as pyramids while still allowing enclosed objects, such as a pencil inside a water cylinder, to be hit before the transparent mesh exit. It is still approximate: it assumes a mostly closed/convex mesh and supports properly nested volumes rather than arbitrary interpenetrating medium ordering.
