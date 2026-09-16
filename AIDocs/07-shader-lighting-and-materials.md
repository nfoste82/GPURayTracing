# Shader Lighting And Materials

This document covers the shared lighting/material helpers in `Assets/Scripts/RayTracingShared.hlsl`, used by the active `Assets/Resources/RayTracingWavefront.compute` queue stages and feature wrappers. `27-renderer-sampling-audit-and-repair-plan.md` is the authoritative sampling repair plan; integration and historical probe coverage below do not establish general estimator correctness.

## Direct Lighting

Direct lighting comes from emissive sphere lights and emissive mesh-triangle lights.

When `GameManager.enableEnvironmentLighting` is enabled, a readable equirectangular `Texture2D` skybox is also sampled as a direct environment light. The CPU builds a luminance-times-`sin(theta)` two-level CDF in `EnvironmentImportanceSampling`; the shader samples a row then a texel, jitters within that texel, converts its discrete mass to a solid-angle PDF, and traces an infinite-distance shadow ray. `environmentHighlightThreshold`, `environmentHighlightSoftKnee`, and `environmentHighlightIntensity` optionally remap only HDRI luminance above a threshold, letting artists strengthen a bright key source without raising ambient fill. The CDF uses the same remapped luminance, and the shader applies it to direct lighting, sky misses, and reflections. Environment samples use power-heuristic MIS against opaque BRDF continuation samples; a continuation ray that misses to the sky receives the complementary weight. The environment distribution is capped by `environmentImportanceWidth`/`environmentImportanceHeight` (default `512x256`) to bound memory and rebuild cost. If the texture is missing, unreadable, or not a `Texture2D`, direct environment lighting is disabled with a warning while sky background/reflections remain available.

`RayDirectionalLight` uses transform forward as the direction illumination travels. Production upload represents it with two virtual `SunTriangle` emitters at a fixed distance for direct sampling and photon transport, not with the analytic directional shader branch. The analytic branch's cone/delta sampling and infinite shadow-query convention therefore do not describe the current production sun. Virtual triangles are not intersectable scene emitters, yet currently receive triangle MIS; zero angular radius produces zero area and an invalid RIS target. These are unresolved findings in document 27.

The positive-area virtual-sun direct strength is set to one half per triangle. Caustic photon power compensates for virtual distance squared. Neither that convention nor the fixed-distance geometry establishes a physically infinite, uniformly cone-sampled sun.

`CSWavefrontTraceShadows` calls `GetLightHittingPoint()` for direct lighting. Sphere lights use disk samples across the emissive sphere radius. Each mesh light is one global emitter: it selects a triangle with a world-space-area-weighted CDF, then uses a uniform barycentric sample across that triangle. Ordinary direct lighting also processes configured environment draws through this loop. Eligible primary opaque `ImportanceSampled` events instead use the standard local RIS reservoir over finite-light/environment candidates and route only the selected candidate through production visibility. Ineligible events and later bounces retain ordinary direct sampling. Finite-light reservoir `proposalPdf` stores branch and global-emitter selection probability, not a full shape density; triangle selection is compensated separately, with shape-to-solid-angle conversion used for MIS. See `23-initial-ris-direct-lighting-plan.md` for that measure convention. The actual per-light shading work lives in `SampleSingleLight()`. Shadow-ray offsets select the geometric-normal side containing the sampled light direction, which is necessary when shading an inner glass boundary.

Each disk sample first checks `saturate(dot(directionToLight, hit.normal))`, and samples whose direction is at or behind the surface (N·L <= 0) are skipped entirely, so back-facing light directions contribute nothing. Lit samples are evaluated by the shared `EvaluateMaterialBrdf()`: diffuse materials use energy-reduced Lambert reflection and all reflective materials use a GGX microfacet lobe with Schlick Fresnel and Smith masking-shadowing. Water keeps its physical dielectric Fresnel response independent of opacity; unlike tinted glass, water albedo is reserved for volume absorption and does not tint direct or environment reflection. Shadow rays are spawned from `hit.position` on the geometric-normal side containing the sampled light direction.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Sphere-light falloff uses the historical clamped inverse-square-style distance term scaled by light radius and `_LightFalloffScale`. Valid triangle samples use area and emitter facing divided by a clamped, scaled squared-distance term. A zero triangle shape PDF currently falls into the sphere/point fallback, so back-face rejection is not reliable. The triangle falloff convention also differs from emissive-hit radiance. Transparent blockers attenuate direct light with RGB transmittance.

The retired adaptive guide-priority fields remain for deserialization only; they do not promote groups in the current Welford scheduler. Active allocation controls and their defects are described in document 27.

Direct-light segments that cross the procedural water volume are additionally attenuated by water absorption using an estimated underwater segment length. This affects underwater points lit from above the water, above-water points lit from underwater, and underwater-to-underwater lighting.

Explicit triangle/environment samples and opaque BRDF continuation samples use power-heuristic weights. Ordinary environment weights now include the same sample count on both sides. Local RIS retains a one-proposal partition including finite/environment branch probability and receiver-dependent emitter selection, regardless of candidate or ordinary light/shadow/environment counts. Continuation records the attempted technique even when the reservoir is empty, without an ordinary all-lights fallback. Local near-delta opaque emitter hits also receive the complementary weight for their finite-width GGX proposal. Classify and guide-training weights share this policy. After the last allowed scatter, a terminal-only trace evaluates sky or mesh-emitter continuation with those same weights; non-emissive hits retire. Focused T2/local R5 and T3/T7 GPU coverage passes. Reused source PDFs/support and triangle falloff versus hit-radiance differences remain unresolved. Sphere lights remain excluded because their historical direct-light model differs from emissive-hit radiance; pairing them creates dark reflection centers. Glass/water and zero-radius fallbacks retain established behavior. See document 27 for acceptance boundaries.

### Light Sampling Strategies

`_LightSamplingStrategy` (from `GameManager.lightSamplingStrategy`) selects which lights each hit shades. The strategies trade sampling cost and variance; their selection corrections do not resolve the estimator/PDF defects above. The current importance cap described below can omit lights entirely.

- **AllLights (0)**: shades every considered light each hit, avoiding global-emitter selection variance; cost scales linearly with light count. It remains an ordinary-estimator diagnostic reference, not a correctness oracle.
- **UniformRandom (1)**: draws `_LightSampleCount` lights uniformly at random and applies a `lightCount / drawCount` Monte Carlo correction. Cheapest, but noisiest, because samples swing between near-black distant lights and bright nearby ones.
- **ImportanceSampled (2)**: eligible primary opaque events run local RIS with `_InitialRisCandidateCount` (1-16, default 4); count 1 is still RIS. The ordinary fallback draws `_LightSampleCount` lights using a cheap `luminance(emission) * falloff(distanceSquared, radius)` weight (`LightImportanceWeight()`) and selection-probability compensation. This can reduce selection variance by concentrating on bright/nearby lights. Within the capped considered set, distant lights keep a nonzero pick probability. The weight uses squared distance directly (no `sqrt`) and mirrors `GetDirectLightFalloff()` math.

The direct-light selection helper returns the selected light's PDF alongside its Monte Carlo weight, so `GetLightHittingPoint()` does not rescan the importance list after selecting a light. Complementary BRDF-hit MIS still reconstructs its PDF independently because it begins with an emissive hit rather than a direct-light selection.

For ordinary random/importance sampling, if `_LightSampleCount` would cover (nearly) every light anyway, `GetLightHittingPoint()` falls back to all-lights behavior (weight `1`, no `1/pdf` scaling) to avoid needless selection variance at the same cost. This is not the eligible primary local-RIS policy.

`_MaxLightSamples` is a separate diagnostic cap: when positive, it clamps how many lights any strategy considers, which was used to confirm the per-hit light loop is the dominant cost in `Benchmark_ManyLights`.

ImportanceSampled only weights up to `MaxImportanceLights` (`128`) global emitters; lights beyond that are ignored for importance weighting. `GameManager` logs a one-time warning when the scene exceeds this count while ImportanceSampled is active. Because omitted lights have zero selection probability, this mode is biased relative to the full scene when the cap is exceeded. An emissive mesh consumes one global entry regardless of triangle count.

### Planned Light BVH

A future portable light BVH will replace the capped O(light-count) importance-selection scan when emitter count warrants it. It will store conservative emitter-group bounds, aggregate flux, and directional bounds, then sample branches according to a receiver-dependent contribution estimate. The final emitter PDF must include every hierarchy branch probability, followed by the existing mesh-triangle CDF and light-shape PDF, before conversion to the solid-angle measure used by MIS. The hierarchy must preserve nonzero probability for every eligible light; it cannot silently recreate the current cap-induced bias.

The current all-lights and uniform strategies remain useful correctness/performance references. The selection implementation must also preserve the single inlined `SampleSingleLight()` call site described below. See `09-roadmap-and-improvements.md` for implementation stages, tradeoffs, and validation criteria.

### Light Sampling Structure And Compile-Time Constraint

`GetLightHittingPoint()` is deliberately written with a **single** inlined `SampleSingleLight()` call site inside one `[loop]`. A helper, `SelectLightForDraw()`, isolates the cheap per-strategy light selection and weighting, while the expensive, BVH-traversing `SampleSingleLight()` body is called once. Finite emitters and environment samples both flow through that call site, so `GetShadowTransmittance()` has only one production direct-light call site. Inlining the sampling or visibility body at multiple call sites previously made the Metal/HLSL compiler duplicate the shadow BVH traversal loop many times, causing multi-minute shader compiles that hung Unity on "Importing Assets". Keep direct-light changes within this single-call-site shape. See `Tools > Ray Tracing > Precompile Compute Shader` for surfacing compile time/errors from edit mode.

## Shadows

Shadow rays test blockers against regular spheres and mesh leaves, but not light leaves. Opaque blockers early-out immediately.

Transparent blockers multiply an accumulated shadow transmittance instead of replacing the result with the nearest transparent hit. `GetShadowTransmittance()` starts at white, processes boundaries in distance order, attenuates each segment from the active medium, and returns black immediately for opaque blockers. This lets multiple glass layers compound energy loss and RGB filtering: a white light through blue glass then yellow glass is multiplied by both glass filters before contributing to direct lighting.

Closed sphere and mesh shadow blockers use ordered entry/exit boundaries and apply distance-based absorption over the actual segment inside each active medium. Properly nested blockers reuse the production medium identity rules. Open meshes, or mesh entries without a valid paired exit before the light, retain the explicit `ThinTransparentSurfaceDistance` fallback. Transparent traversal stops when transmittance is negligible and has a fixed crossing limit; opaque-only scenes retain their cheaper boolean occlusion path.

For shadow-BVH traversal details, see `06-shader-intersections-and-bvh.md`.

## Path Tracing Materials

`CSWavefrontScatter` calls the shared `CreateScatteredRay()` helpers for these material scattering paths:

- `Diffuse`: uses a mixture of cosine-weighted Lambert sampling and GGX visible-normal reflection sampling. Direct lighting and continuation rays share BRDF evaluation, roughness mapping, and PDF code, including the T1 narrow-lobe density repair with the focused coverage limits below.
- `Metal`: uses GGX visible-normal reflection sampling with albedo as the Fresnel base reflectance. Conditioning the microfacet distribution on the current view direction avoids many invalid grazing-angle reflections produced by raw NDF sampling, but shared evaluation alone does not prove the reported PDF matches the sampler.
- `Glass`: uses `RayMaterial.Specular` as a minimum reflection chance, then Schlick Fresnel to raise reflection toward one at grazing angles. `RayMaterial.Transmission` independently controls the remaining transmission chance. Sphere and mesh paths sample GGX boundary normals from smoothness for both reflection and Snell transmission, so rough glass broadens refractions and caustics into a frosted appearance. Mesh glass supports bounded internal total internal reflection (TIR) while searching for an exit face. Transmitted glass paths are attenuated by distance-based RGB absorption. Glass and water also receive direct specular highlights from sampled lights.

`WavefrontPathState` carries a fixed-capacity medium stack. Camera rays initialize the stack with containing water and translucent spheres, with containing spheres ordered outermost to innermost. Transmitted water, sphere-glass, and mesh-glass paths update that state; reflection and TIR do not. Absorption is evaluated for each traveled segment from the active medium. Path-selection Fresnel and the sphere, mesh, and water transmission helpers use the current medium IOR as the source and the entered medium or revealed parent IOR as the target. Exiting an interpenetrating sphere removes that sphere by object identity even when another overlapping sphere is currently active; the transition remains in the active overlap medium instead of producing an unmatched exit and stale absorption state.

Mesh hits retain both a shading/optical normal and the triangle's geometric normal. `RayMaterial.InterpolateNormals` barycentrically interpolates imported vertex normals for direct lighting, BRDF sampling, reflection, mesh Fresnel, and entry/exit refraction. Ray offsets, transparent boundary classification, and volume traversal continue to use the geometric normal so smooth shading does not change the actual polygonal volume.

Opaque continuation throughput uses `brdf * abs(N dot L) / pdf`. The common roughness conversion is `roughness = 1 - smoothness`, `alpha = roughness^2`, with a small roughness floor to keep mirror-like GGX evaluation finite. Glass/water transmission and Fresnel branch selection retain their medium-stack-specific path; their direct reflection uses the shared GGX evaluator.

Opaque GGX continuation samples the Heitz visible-normal distribution and reports a reflected-direction PDF through the shared BRDF evaluator. The first T1 repair replaces `GgxDistribution()`'s denominator floor with stable cross-product `sin^2(theta)` evaluation of GGX D, preserving narrow-lobe density. Focused GPU checks compare BRDF and evaluated/sampled PDFs against double-precision references at normal and grazing views, plus normal-incidence cone and null-event frequencies. Grazing sampling-frequency integration, white-furnace/raw-HDR mean acceptance, and relevant wrapper compiles remain pending; other estimator and MIS defects are not repaired by T1. See document 27 for results and validation limits. Rough glass and water boundary normals intentionally retain their separate raw-GGX/rejection path; replacing dielectric transmission sampling requires an eta-dependent BTDF PDF and is not implied by the opaque reflection sampler. This repair does not change triangle-light or glass appearance policy.

Mesh opaque materials can blend continuously between dielectric and metal with `RayMaterial.Metallic`. The glTF-style metallic/roughness texture multiplies scalar metallic and roughness using blue and green channels respectively. Tangent-space normal maps modify the optical/shading normal used by direct light, GGX sampling, reflection, and mesh refraction; geometric triangle normals continue to control boundary classification and ray offsets.

### Planned Ray Material Presets

Optional data-only `RayMaterialPreset` assets are planned as an editor authoring convenience. Applying one will copy documented scalar and texture values into an ordinary `RayMaterial`; shader code and GPU material layout remain unchanged. Manual edits can intentionally diverge from the source preset, and preset changes must follow the existing material/texture dirty path and accumulation invalidation without rebuilding geometry or BVHs. See `09-roadmap-and-improvements.md` for the override policy and test requirements.

Each uploaded emissive triangle stores the matching `_Lights` index, and emissive sphere hits already use their light-buffer index. This identity supports reconstruction of competing light-selection and shape PDFs, subject to the current MIS mismatches above.

`WavefrontPathState` retains the previous surface position, material PDF, and direct-light/RIS/near-delta/soft-shadow flags for continuation MIS. It does not preserve a full previous `RayHit`; `_WavefrontHits` is overwritten by each intersection stage.

The glass path is entered whenever `IsGlassMaterial(hit)` is true, which happens for `materialType == Glass` **or** for any hit with `opacity < 1.0`. A nominally `Diffuse` or `Metal` object with reduced opacity therefore scatters through the glass transmission/Fresnel path.

## Transparency And Refraction

Glass reflection uses `lerp(Specular, 1, SchlickFresnel)`, while `Transmission` controls how many non-reflected paths refract. Opacity does not add an opaque reflective portion, so smooth glass with a typical IOR of `1.5` and `Specular` `0` reflects about 4% at normal incidence and increasingly toward grazing angles. Opacity remains the density control for distance-based absorption and transparent shadows.

Glass applies distance-based RGB absorption through the active medium's per-segment transmittance. The shader treats `RayMaterial.Color`/sampled albedo as a per-channel filter color and raises it by `distanceThroughMedium * opacity`, with a small neutral absorption term (`GlassNeutralAbsorption`) so even nearly white glass loses some energy through distance/layers. This is Beer-Lambert-style behavior rather than a full spectral renderer, but it means stacked colored glass naturally compounds through path throughput and transparent shadows. Higher opacity means denser absorption, while lower opacity means a weaker color filter.

Sphere entry and exit are separate path events with regular per-segment attenuation. Closed-mesh helpers report internal distance they consume within one scattering operation; paths that remain inside carry the medium stack into the next bounce. Open meshes that have no paired exit retain the explicit `ThinTransparentSurfaceDistance` fallback.

`RefractSnell()` implements Snell-law refraction and reports failure when the requested transition would exceed the critical angle. Glass-to-air failures are total internal reflection events and reflect the ray back into the current medium.

Transparent/glass sphere refraction uses explicit path events at both boundaries. `ApplySphereRefraction()`:

1. Refracts from the current stack medium into the sphere using `RefractSnell()`, or from the sphere into the revealed parent medium on an exit hit.
2. For entry hits, starts the next path segment just inside the sphere and pushes the sphere medium.
3. Normal scene traversal finds the nearest enclosed object or the sphere's rear boundary.
4. Sphere intersections retain an outward geometric normal but face the shading normal against the incident ray, allowing direct light and Fresnel reflection on inner boundaries.
5. The rear boundary is shaded and independently selects Fresnel reflection or transmission. Reflection and total internal reflection preserve the sphere medium; transmission removes it.

Glass material scattering uses the independent `Specular` minimum plus Schlick Fresnel reflectance to randomly choose first-surface reflection, and `Transmission` to select among the remaining paths independently of opacity. Smoothness uses the shared `roughness = 1 - smoothness`, `alpha = roughness^2` mapping to sample a GGX microfacet normal independently at each crossed boundary; smoothness `1` retains exact Snell directions, while lower values blur both reflected and transmitted paths. Caustic photons use the same boundary-normal sampling, so frosted glass broadens the photon pattern rather than retaining a sharp caustic. Reflected glass paths remain untinted while transmitted paths are filtered by distance-based absorption. Internal TIR contributes to `bouncesConsumed`; wavefront classification now enforces the resulting per-path depth before any later direct-light or scatter event.

Water uses the same physical refraction/Fresnel helper regardless of opacity, so grazing views retain strong sky reflections even when opacity is zero. Opacity adds an opaque reflective portion to the boundary: transmission is selected with probability `(1 - opacity) * (1 - Fresnel)`, and all other paths reflect. Neither branch receives an extra surface tint or attenuation; `_WaterColor` and `_WaterAbsorptionStrength` control distance-based volume absorption separately. Path segments that start underwater multiply throughput by exponential transmittance based on those settings and segment distance, so shallow bottom bounces remain brighter while deeper water becomes darker and more color-filtered. A small neutral extinction floor prevents near-white water channels from becoming lossless, which would otherwise make deep water converge to its tint rather than darken.

Triangle mesh refraction uses `ApplyPlanarTransmission()` rather than the sphere helper:

1. Refract from the current stack medium into the hit triangle using `RefractSnell()`; an already-active matching mesh boundary instead refracts directly into its parent medium.
2. Cast an internal ray against triangles with the same `meshIndex`.
3. Use the nearest internal triangle hit as the candidate exit face.
4. Run a bounded scene intersection query along the internal segment, ignoring the current transparent mesh. This uses the normal top-level/per-mesh BVH traversal and can find objects enclosed by the transparent mesh before the exit face.
5. If an interior object is found, continue tracing inside the transparent mesh so the next bounce shades that object.
6. If no interior object is found, try to refract from the material back into the source/parent medium and continue the path from the exit point.
7. If the exit face exceeds the critical angle, reflect internally and repeat until the ray exits, hits an interior object, misses the closed mesh, or exhausts the remaining path-bounce budget.

This gives visible prism-like behavior for simple closed meshes such as pyramids while still allowing enclosed objects, such as a pencil inside a water cylinder, to be hit before the transparent mesh exit. It is still approximate: it assumes a mostly closed/convex mesh and supports properly nested volumes rather than arbitrary interpenetrating medium ordering.
