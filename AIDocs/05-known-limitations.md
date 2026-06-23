# Known Limitations

This document captures current implementation limits and broad architectural direction. For future work lists, see `09-roadmap-and-improvements.md`. For performance hotspots and benchmark guidance, see `10-benchmarking-and-performance.md`.

## Known Limitations

- Spheres, emissive sphere lights, registered triangle meshes, and an implicit infinite ground plane are ray traced.
- Unity meshes are traced only when registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`; box colliders and the scene `Directional Light` are not used by the compute shader renderer.
- First-hit rays use a top-level BVH over spheres, emissive light spheres, and registered mesh AABBs once the scene has enough objects to amortize traversal overhead. Shadow rays use a separate top-level BVH over blocker objects only: regular spheres and meshes. Smaller scenes use flat object loops. Triangle meshes also use per-mesh AABB culling and per-mesh BVHs to skip most triangle tests. Current default BVH thresholds are conservative so benchmark scenes can opt into BVHs deliberately.
- `UpdateSpheres()` uploads all sphere/light data every rendered frame, even though component references are cached.
- Debug render modes are basic first-hit/path diagnostics and do not include legends or configurable visualization ranges. `AccelerationStructures` is available for checking whether the general top-level BVH and shadow BVH are active.
- Shadow rays check regular spheres and triangles as blockers, but not light spheres.
- Direct light sampling supports three strategies (all lights, uniform random, importance-sampled). The random and importance strategies are unbiased but noisier per frame; final-color frame accumulation can average that variance when the camera and scene are static, but there is still no spatial denoiser. ImportanceSampled only weights up to `MaxImportanceLights` (`128`) lights; lights beyond that count are ignored for importance weighting (a one-time warning is logged), and its importance weight ignores shadows and the surface normal, so it is an estimate rather than the true contribution.
- `GetLightHittingPoint()` must keep a single inlined `SampleSingleLight()` call site. Duplicating that BVH-traversing body across multiple call sites caused the Metal/HLSL compiler to expand the shadow traversal loop many times, producing multi-minute shader compiles that hung Unity on "Importing Assets". `Tools > Ray Tracing > Precompile Compute Shader` exists to surface compile time and errors from edit mode instead of stalling on first Play.
- Refraction/transparency use Fresnel material selection, but sphere and mesh transmitted paths are still approximate rather than a full physically accurate volume traversal.
- Direct lighting accumulates additively and uses clamped inverse-square-style falloff, but transparent shadow tinting is still approximate rather than fully physically based.
- Diffuse scattering uses cosine-weighted hemisphere sampling on later bounces. Final-color frame accumulation can reduce variance in static views, but there is still no spatial denoising.
- Mesh triangle normals are flat face normals; imported vertex normals, smoothing groups, UVs, and textures are not used.
- Mesh refraction assumes a mostly closed/convex mesh and uses the nearest same-mesh triangle as the exit face. It does not handle nested media, multi-hit internal reflections, or distance-based absorption.
- BVH traversal (per-mesh, top-level, and shadow, on both GPU and the CPU autofocus path) uses a fixed-size stack of `64`. If a node's children would overflow the stack, those children are silently dropped rather than handled, which could in theory miss intersections for pathologically deep trees. The current median-split builds make this unlikely, but it is not guarded.
- Scene-view sphere, light, ground, and skybox previews are composition aids. They approximate the ray-traced result but are not guaranteed to match all compute shader shading, reflection, refraction, exposure, or sampling behavior exactly.

## Recently Completed

- ACES filmic tone mapping and a `GameManager.exposure` control were added. Exposure-scaled tone mapping is applied to the final color in `CSMain`, while debug render modes are written untone-mapped.
- Progressive final-color frame accumulation was added with a `GameManager.enableFrameAccumulation` toggle. It averages HDR radiance before tone mapping, resets when render/camera/scene/quality state changes, skips debug render modes, and continues refining while single-frame mode is active.
- Direct lighting gained selectable sampling strategies via `GameManager.lightSamplingStrategy`: all lights (original behavior), uniform random pick, and importance-sampled pick, with a configurable `lightSampleCount` for the random/importance strategies. All three are unbiased estimators; the random/importance strategies trade per-frame noise for much lower cost in many-light scenes (e.g. `Benchmark_ManyLights`).
- `GetLightHittingPoint()` was refactored to a single inlined `SampleSingleLight()` call site with a cheap `SelectLightForDraw()` selection helper, fixing multi-minute shader compiles caused by inlining the BVH-traversing shading body at multiple strategy call sites.
- `RayTracingShaderPrecompiler` (`Tools > Ray Tracing > Precompile Compute Shader`) was added to force-compile and dispatch the compute shader from edit mode with timing and surfaced compile messages, so slow/failing kernels appear there instead of hanging Unity on first Play.
- A `maxLightSamples` diagnostic cap was added to clamp how many lights any strategy considers, which confirmed the per-hit light loop dominates cost in many-light scenes.
- `RayTracingBenchmarkOverlay` and `Tools > Ray Tracing > Generate Benchmark Scenes` were added to create benchmark scenes for many spheres, shadow blockers, many lights, dense meshes, many mesh objects, glass, sparse scenes, and dynamic transforms.
- The `AccelerationStructures` debug render mode was added to visualize active general and shadow BVHs. This helped confirm that shadow BVH threshold values must exceed the blocker count to force flat shadow loops.
- `topLevelBvhMinObjectCount` and `shadowBvhMinObjectCount` were expanded to support high thresholds such as `1024`, making it easy to force BVH-on versus flat-loop comparisons at runtime.
- `UnregisterObject()` removes disabled/destroyed ray-traced objects from CPU sphere/light lists and marks GPU buffers for rebuild.
- `_outputTexture` is recreated when the runtime render size changes, and `renderTextureCamera.aspect` is updated to match the active render target.
- `RayMaterial`, `RayLight`, `SphereCollider`, and `Transform` references are cached at registration time instead of fetched every rendered frame.
- Debug render modes are available for final color, normals, albedo, emission, direct light, throughput, bounce count, and hit distance.
- Shader RNG now uses a local hash-based per-pixel/per-sample `uint` state instead of mutable sine/hash float state.
- `RayMaterial` supports `Diffuse`, `Metal`, and `Glass` material types.
- Direct light samples accumulate additively instead of using channel-wise max combining.
- Glass material paths use Schlick Fresnel weighting with the existing approximate sphere refraction helper.
- Soft shadows use stochastic area-light sampling instead of a dense grid, and shadow rays early-out for opaque blockers before the light distance.
- Diffuse bounce sampling uses cosine-weighted hemisphere sampling.
- Deeper paths use Russian roulette termination once throughput is low enough.
- Direct lighting uses clamped inverse-square-style falloff scaled by light radius.
- `GameManager.lightFalloffScale` exposes direct light falloff tuning to the inspector.
- Ground smoothness affects the implicit ground plane's first continuation ray instead of always behaving like a mirror.
- Single-frame mode can be disabled from the inspector, `T`, or `Space` to resume real-time rendering, and it keeps blitting the last compute output in the Game view while dispatch is paused.
- Unused ambient/checkerboard shader parameters, unused shader helpers, and inactive mesh-buffer scaffolding were removed.
- Direct light sampling is skipped when path throughput is below `MinDirectLightThroughput`.
- Triangle mesh upload from Unity `MeshFilter` components was added for registered mesh objects.
- Triangle intersection was added to first-hit tracing, autofocus checks, and shadow blockers.
- `RayMeshPrimitive` and `GameObject > Ray Tracing` editor menu items were added for cube, pyramid, and dodecahedron mesh test objects.
- Mesh triangle uploads now rebuild only when registered mesh transforms or ray material values change.
- Mesh glass refraction now approximates entry and exit through closed triangle meshes using per-object `meshIndex` values.
- Triangle meshes now build and upload per-mesh AABBs and BVH nodes so first-hit, shadow, and mesh-refraction rays do not need to test every triangle.
- First-hit and shadow rays now traverse a top-level scene BVH so they can skip groups of spheres, lights, and meshes before object-specific tests.
- `RayObjectPreview` and additional `GameObject > Ray Tracing` menu items were added for visible ray-traced sphere/light composition in Scene view.
- `GameManager` now draws a depth-tested editor preview for the implicit ground plane and can sync Unity's skybox preview from the ray tracer's skybox texture/tint settings.
- Editor pause now refocuses/repaints the Game view through an editor-only callback so the last presented compute render remains visible when the Unity toolbar Pause button is used.

## Architectural Direction

The shader now has a cleaner iterative `TracePath()` loop with explicit radiance, throughput, albedo, and emission terms. Future renderer work should preserve that separation rather than reintroducing one-off per-bounce color trees.

For major features, prefer building toward a generic hit/material/shading abstraction:

- Intersections produce a `RayHit`.
- `RayHit` maps to material data.
- Shading consumes ray, hit, material, and lights.
- Path state remains explicit in `radiance` and `throughput`.
