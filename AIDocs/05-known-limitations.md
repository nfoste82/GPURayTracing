# Known Limitations

This document captures current implementation limits and broad architectural direction. For the current renderer/sampling defects, T1 repair progress, and validation gates, see [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md). The focused T1 repair does not close the other findings or broader acceptance gates. For unrelated future work lists, see `09-roadmap-and-improvements.md`; for performance guidance, see `10-benchmarking-and-performance.md`.

## Known Limitations

- Spheres, emissive sphere lights, emissive mesh lights, registered triangle meshes, and one finite procedural water surface are ray traced. Hard directional illumination uses analytic delta records; soft directional illumination uses virtual sun-triangle proposals rather than intersectable sun geometry.
- Unity meshes are traced only when registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`; box colliders and the scene `Directional Light` are not used by the compute shader renderer.
- First-hit rays use a top-level BVH over spheres, emissive light spheres, and registered mesh AABBs once the scene has enough objects to amortize traversal overhead. Shadow rays use a separate top-level BVH over blocker objects only: regular spheres and meshes. Smaller scenes use flat object loops. Triangle meshes also use per-mesh AABB culling and per-mesh BVHs to skip most triangle tests. Current default BVH thresholds are conservative so benchmark scenes can opt into BVHs deliberately.
- `UpdateSpheres()` checks cached sphere/light data each rendered frame and uploads only changed data; geometry changes still require CPU transformation and buffer updates.
- Debug modes intend to show first-hit/path diagnostics, but later wavefront hits overwrite primary-hit storage and can mask path records. They lack legends/configurable ranges and are not yet reliable primary-hit validation; use configured BVH thresholds and the overlay alongside diagnostics.
- Shadow rays check regular spheres and triangles as blockers, but not light spheres.
- Direct light sampling supports three strategies (all lights, uniform random, importance-sampled), with local RIS for eligible primary opaque events and ordinary sampling elsewhere. The ordinary importance selector/PDF considers at most `MaxImportanceLights` (`128`) entries, so it does not cover all lights in larger scenes; this is not a triangle-count limit or a blanket description of every RIS proposal. The importance estimate ignores visibility and the surface normal. Strategy/PDF consistency and estimator defects remain under audit in document 27.
- `GetLightHittingPoint()` must keep a single inlined `SampleSingleLight()` call site. Duplicating that BVH-traversing body across multiple call sites caused the Metal/HLSL compiler to expand the shadow traversal loop many times, producing multi-minute shader compiles that hung Unity on "Importing Assets". `Tools > Ray Tracing > Precompile Compute Shader` exists to surface compile time and errors from edit mode instead of stalling on first Play.
- Refraction/transparency use Schlick Fresnel selection, Snell refraction, and distance-based RGB absorption. Sphere transmission advances through explicit boundary events with a bounded medium stack; mesh transmission still uses a bounded entry/exit shortcut rather than general volume traversal.
- Direct lighting accumulates additively. Sphere/point-like lights retain clamped artistic falloff; mesh emitters use physical one-sided area-light geometry. Transparent shadow tinting remains approximate rather than fully physically based.
- Opaque diffuse scattering uses cosine-weighted hemisphere sampling within the diffuse/specular mixture. Spatial A-trous denoising is implemented; temporal reconstruction remains experimental and incompletely validated.
- Mesh UVs, interpolated vertex normals, imported tangents, albedo maps, glTF-channel metallic/roughness maps, and tangent-space normal maps are supported; spheres also support albedo, normal, and parallax textures. Each channel's array uses its largest source dimensions, with smaller textures resampled and non-readable sources copied through a render texture. Texture contents mutated in place are not detected automatically.
- Mesh refraction assumes a mostly closed/convex mesh and uses nearest same-mesh triangle crossings to find exit faces, with bounded internal total internal reflection and absorption. An eight-entry medium stack exists, including shadow boundary tracking, but robust concave/non-manifold traversal, camera-inside-mesh initialization, and arbitrary overlapping media are not generally solved.
- Opaque shading has shared BRDF evaluation, diffuse/GGX mixture-PDF calculation, VNDF sampling, and finite-light/environment MIS. T1's narrow-lobe density defect, T2/local R5 MIS counts, T3 terminal continuation, and T4/T8 mesh-emitter/sun contracts have focused GPU coverage. Grazing-frequency integration, reused-reservoir correctness, and broader acceptance remain pending in document 27. Water and dielectric shortcuts are not equivalent to this opaque BRDF model.
- Each emissive mesh contributes one global light entry, with area-CDF triangle selection inside that entry. Dense meshes still incur geometry/storage and triangle-sampling costs, but do not consume one global importance slot per triangle.
- Procedural water is a transform-backed `Water` component, but only one active component is currently accepted by each `GameManager`; a second is disabled with an error. Position and scale control an axis-aligned closed volume with a ray-marched wavy top and flat side/bottom boundaries; rotation is not supported. Pathological/high-frequency top crossings can be missed. Direct-light segment-distance estimation remains approximate when both endpoints are outside but the segment crosses the volume. CPU autofocus tests the average top plane rather than the animated wave or flat side/bottom boundaries.
- Active wavefront stages have image/queue bounds checks; the retired `CSMain` out-of-bounds claim is not a current blocker. Per-path scatter-event budgets are enforced after multi-event helpers, with one terminal-only sky/emitter trace. Debug hit preservation remains an open wavefront audit finding.
- Mesh change tracking watches transforms and material values but not replacement or in-place mutation of `MeshFilter.sharedMesh`; runtime topology, vertex, or UV changes can leave uploaded triangles/BVHs stale.
- Single-frame mode overwrites global vSync, target-frame-rate, and time-scale settings and restores hard-coded values rather than preserving previous application settings.
- GPU BVH traversal uses a fixed 32-entry stack. Per-mesh median splits keep trees shallow, and top-level SAH depth is constrained by the CPU builder; preserving this capacity contract remains necessary when changing builders or traversal. See `06-shader-intersections-and-bvh.md`.
- Adaptive scheduling is integrated through fixed-8x8 allocation and layered wavefront generation/resolve, but allocation quality, accounting, and sampling validation remain incomplete. Experimental temporal/spatial RIS and path guiding also have active opt-in wavefront wrappers; their unresolved correctness and history issues are tracked in document 27, not pending migration plans.
- Scene-view sphere, light, water-bounds, and skybox previews are composition aids. They approximate the ray-traced result but are not guaranteed to match all compute shader shading, reflection, refraction, exposure, or sampling behavior exactly.

## Recently Completed

- Procedural finite water gained animated wave intersection, Snell/Fresnel reflection/refraction, direct highlights, and distance-based underwater absorption, plus dedicated benchmark scenes.
- Glass transmission gained Snell refraction, RGB distance absorption, explicit sphere boundaries, bounded medium state, and a mesh shortcut with bounded interior-object detection and total internal reflection.
- Transparent shadows track ordered boundaries and active-medium segment distances through spheres and closed meshes, with a thin-surface fallback for unpaired mesh boundaries and a bounded crossing count.
- Emissive registered meshes participate in direct-light sampling through one mesh-level entry and an area-weighted triangle CDF.
- Texture support uses per-channel `Texture2DArray` resources sized from source assets.
- Mesh materials gained continuous metallic response plus linear metallic/roughness maps and tangent-space normal maps through dedicated texture arrays.
- Opaque surfaces gained shared diffuse/GGX BRDF evaluation, mixture-PDF calculation, VNDF specular sampling, and MIS plumbing. The first T1 narrow-lobe density repair now has focused GPU coverage, not full estimator acceptance; other weighting defects remain unresolved. The imported Stanford Dragon benchmark exercises high-triangle smooth geometry.
- ACES filmic tone mapping and a `GameManager.exposure` control were added. Exposure-scaled tone mapping is applied after Catmull-Rom reconstruction in the final presentation pass, while debug render modes are written untone-mapped.
- Progressive final-color frame accumulation was added with a `GameManager.enableFrameAccumulation` toggle. It averages HDR radiance before tone mapping, resets when render/camera/scene/quality state changes, skips debug render modes, and continues refining while single-frame mode is active.
- Direct lighting gained selectable sampling strategies via `GameManager.lightSamplingStrategy`: all lights, uniform random pick, and importance-sampled pick, with a configurable `lightSampleCount` for the random/importance strategies. These trade per-frame variance for lower many-light cost; full proposal support and matching PDFs remain correctness requirements, not a claim that the current estimator passes the audit.
- Local direct-light RIS is integrated for eligible primary opaque events. Experimental temporal/spatial reuse is opt-in through `RayTracingWavefrontRis`, with runtime gates and a spatial prepass where enabled. These routes still require correctness and history validation. Local candidate-count changes intentionally preserve progressive color accumulation; reuse settings have separate invalidation requirements.
- `GetLightHittingPoint()` was refactored to a single inlined `SampleSingleLight()` call site with a cheap `SelectLightForDraw()` selection helper, fixing multi-minute shader compiles caused by inlining the BVH-traversing shading body at multiple strategy call sites.
- `RayTracingShaderPrecompiler` provides targeted active-asset/variant dispatch with per-kernel timings and surfaced compile messages. It no longer warms a legacy `CSMain` debug matrix; see `22-shader-compile-splitting-handoff.md`.
- Water and fog use dedicated wavefront wrappers, terrain uses `TERRAIN_ENABLED`, and camera-side caustics gathering uses a separate asset/composite pass to keep these compile costs isolated.
- A `maxLightSamples` diagnostic cap was added to clamp how many lights any strategy considers, which confirmed the per-hit light loop dominates cost in many-light scenes.
- `RayTracingBenchmarkOverlay` and `Tools > Ray Tracing > Generate Scenes` create generated performance and image-quality scenes for many spheres, shadow blockers, many lights, dense meshes, many mesh objects, glass, sparse scenes, and dynamic transforms.
- The `AccelerationStructures` debug render mode was added to visualize active general and shadow BVHs. This helped confirm that shadow BVH threshold values must exceed the blocker count to force flat shadow loops.
- `topLevelBvhMinObjectCount` and `shadowBvhMinObjectCount` were expanded to support high thresholds such as `1024`, making it easy to force BVH-on versus flat-loop comparisons at runtime.
- `UnregisterObject()` removes disabled/destroyed ray-traced objects from CPU sphere/light lists and marks GPU buffers for rebuild.
- `_outputTexture` is recreated when the runtime render size changes, and `renderTextureCamera.aspect` is updated to match the active render target.
- `RayMaterial`, `RayLight`, `SphereCollider`, and `Transform` references are cached at registration time instead of fetched every rendered frame.
- Debug render modes are available for final color, normals, albedo, emission, direct light, throughput, bounce count, and hit distance.
- Shader RNG uses semantic camera/bounce dimensions with Owen-scrambled Sobol coordinates through the configured dimension limit and a hash fallback for later dimensions. Dimension overlap/correlation findings remain open in document 27.
- `RayMaterial` supports `Diffuse`, `Metal`, and `Glass` material types.
- Direct light samples accumulate additively instead of using channel-wise max combining.
- Glass material paths use Schlick Fresnel weighting with Snell transmission helpers, explicit sphere boundary transitions, and approximate bounded mesh traversal.
- Soft shadows use stochastic area-light sampling instead of a dense grid, and shadow rays early-out for opaque blockers before the light distance.
- Diffuse bounce sampling uses cosine-weighted hemisphere sampling.
- Deeper paths use Russian roulette termination once throughput is low enough.
- Direct lighting uses clamped inverse-square-style falloff scaled by light radius.
- `GameManager.lightFalloffScale` exposes direct light falloff tuning to the inspector.
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
- `GameManager` can sync Unity's skybox preview from the ray tracer's skybox texture/tint settings.
- Editor pause now refocuses/repaints the Game view through an editor-only callback so the last presented compute render remains visible when the Unity toolbar Pause button is used.

## Architectural Direction

The active renderer is queue-driven: `WavefrontPathTracingManager` dispatches the staged wavefront kernels, with explicit radiance, throughput, medium, RNG, and PDF state. `TracePath()`/`CSMain` is retired. Future renderer work should preserve this separation rather than reintroducing one-off per-bounce color trees. Current repairs and their validation order belong to document 27.

For major features, prefer building toward a generic hit/material/shading abstraction:

- Intersections produce a `RayHit`.
- `RayHit` maps to material data.
- Shading consumes ray, hit, material, and lights.
- Path state remains explicit in `radiance` and `throughput`.
