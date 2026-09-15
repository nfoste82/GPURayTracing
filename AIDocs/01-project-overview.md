# Project Overview

This is a Unity real-time GPU ray/path tracing project. The scene runs inside Unity, but image generation is performed by the queue-driven compute shader in `Assets/Resources/RayTracingWavefront.compute`.

The renderer currently ray traces spheres, emissive sphere and mesh lights, registered triangle meshes, and one optional finite procedural water surface. Unity scene meshes, walls, and colliders that are not registered as ray-traced objects still exist mostly for scene organization and physics; they are not traced by the compute shader.

## Key Files

- `Assets/Scripts/GameManager.cs`: Main Unity-side controller. Owns render texture creation, compute shader dispatch, quality settings, camera controls, autofocus, object buffers, shader parameter uploads, and optional Unity skybox preview sync. It does not implement `OnRenderImage()` itself; it exposes `RenderImage(src, dest)`, which is called by `RayTracingCameraRenderer`.
- `Assets/Scripts/RayTracingCameraRenderer.cs`: Camera component whose `OnRenderImage()` delegates to `GameManager.RenderImage()`. It runs on whatever camera holds this component (typically the same camera wired into `GameManager.renderTextureCamera`, but that link is only inspector wiring, not enforced in code).
- `Assets/Resources/RayTracingWavefront.compute`: Main GPU renderer. Generates camera rays, runs staged intersection, lighting, shadow, and scatter queues, and writes the final pixel color.
- `Assets/Scripts/RayTracingBenchmarkOverlay.cs`: Runtime benchmark overlay for frame timing, geometry counts, BVH status, and quality settings.
- `Assets/Scripts/BenchmarkOrbitMover.cs`: Simple deterministic movement helper for dynamic benchmark scenes.
- `Assets/Scripts/RayTracingObject.cs`: Registers and unregisters ray-traced scene objects with the nearest parent `GameManager`.
- `Assets/Scripts/RayMaterial.cs`: Per-object render material data: material type, color, optional mesh albedo texture, smoothness, opacity, and refraction index.
- `Assets/Scripts/RayMeshPrimitive.cs`: Procedural mesh primitive helper for ray-traced cube, pyramid, and dodecahedron test objects.
- `Assets/Scripts/RayObjectPreview.cs`: Editor/runtime helper that adds rasterized sphere previews and optional Unity point-light previews for ray-traced sphere and light objects.
- `Assets/Scripts/RayLight.cs`: Per-light emission color and HDR intensity.
- `Assets/Scripts/Water.cs`: Transform-backed finite water volume. Position sets the average wavy-top center, scale sets X/Z footprint and Y depth, and the component owns water material/wave settings. One active component is currently supported per `GameManager`.
- `Assets/Scripts/ColorExtensions.cs`: Converts `Color32` to normalized `Vector3` values for GPU upload.
- `Assets/Editor/RayMeshPrimitiveMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for creating ray-traced mesh primitive test objects in the hierarchy.
- `Assets/Editor/RaySceneObjectMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for ray-traced spheres and light spheres.
- `Assets/Editor/RayTracingSceneGenerator.cs`: Adds `Tools > Ray Tracing > Generate Scenes` for creating focused performance and image-quality scenes.
- `Assets/Editor/RayTracingShaderPrecompiler.cs`: Adds `Tools > Ray Tracing > Precompile Compute Shader`. It warms one split compute asset at a time and records asset/kernel/variant timing, so slow or failing kernels show up there instead of stalling Unity on first Play. Unity compiles compute kernels lazily on first `Dispatch`; see `22-shader-compile-splitting-handoff.md` before running the intentionally expensive all-assets command.
- `Assets/Scenes/Root.unity`: Main scene with the camera, game manager, ray-traced spheres, light spheres, physics objects, and visual scene geometry.
- `Assets/Scenes/Generated/*.unity`: Generated scenes for stressing specific renderer paths and validating image quality.

## Runtime Feature Set

- Real-time compute-shader rendering driven through `RayTracingCameraRenderer.OnRenderImage()`, which calls `GameManager.RenderImage()`.
- Dynamic sphere transforms and physics-driven sphere movement.
- Registered triangle mesh objects through `RayTracingObject` + `RayMaterial` + `MeshFilter`.
- Mesh UV/albedo, metallic-roughness, normal, and parallax texture sampling through per-channel texture arrays; spheres also support albedo, normal, and parallax textures.
- Editor-created ray-traced cube, pyramid, and dodecahedron primitives that remain visible in Scene view but hide their rasterized `MeshRenderer` in Play mode by default.
- Scene-view previews for ray-traced sphere and light-sphere objects through gizmos and optional rasterized `RayObjectPreview` meshes.
- Scene-view bounds/average-level preview for procedural water.
- Optional Unity skybox preview synced from `GameManager.skyboxTexture` and tinted by `_skyboxLightColor`.
- Emissive sphere and mesh lights.
- Direct lighting with hard/soft shadow sampling.
- Opaque diffuse/specular BRDF evaluation, mixture-PDF calculation, GGX visible-normal (VNDF) specular sampling, and direct-light/continuation MIS. The T1 narrow-lobe GGX density repair is implemented with focused GPU BRDF/PDF and normal-incidence sampling-frequency coverage; broader acceptance remains pending, and MIS pairing and other estimator defects remain unresolved.
- Selectable direct-light sampling strategy (all lights, uniform random, or importance-sampled) with a configurable per-hit light sample count. The importance-sampled path uses local RIS for eligible primary opaque direct-light events; experimental temporal/spatial RIS and path guiding have opt-in dry wavefront wrappers with runtime eligibility gates.
- ACES filmic tone mapping with a configurable `exposure` control, applied to the final color (debug modes are left untone-mapped).
- Transparent/glass objects with Snell refraction, distance-based RGB absorption, a bounded medium stack, explicit sphere boundary events, and an approximate closed-mesh entry/exit shortcut with bounded interior-object detection and total internal reflection.
- Colored shadows through transparent blockers.
- Opaque rough reflections use GGX VNDF sampling; the water reflection branch still uses randomized normals.
- Depth of field with configurable aperture radius, blade count/rotation, and anamorphic ratio, plus CPU-side autofocus and GPU click-to-focus/tracking.
- Configurable samples per pixel via `numberOfPasses`.
- Owen-scrambled Sobol sampling through a configurable dimension limit, with hash RNG fallback for later dimensions.
- Fixed-8x8 adaptive scheduling with layered wavefront generation and per-pixel Welford accumulation. Adaptive allocation quality and correctness validation remain incomplete.
- Spatial A-trous denoising and experimental temporal reconstruction; integration does not imply complete image-quality validation.
- Configurable bounce count via `numBounces` / `_NumBounces`.
- Optional top-level object BVH and separate shadow-blocker BVH, each controlled by runtime thresholds so small scenes can stay on cheaper flat loops.
- One optional finite axis-aligned water volume with a procedural wavy top, flat sides/bottom, Fresnel reflection/refraction, and distance-based underwater absorption.
- Benchmark overlay and generated benchmark scenes for comparing optimization behavior across different workloads.

## Current Renderer Shape

`WavefrontPathTracingManager` dispatches generation, intersection, classification, direct-light work, shadow tracing/resolution, scattering, queue retirement, radiance resolve, and presentation stages. `WavefrontPathState` carries explicit radiance, throughput, medium, RNG, and previous-event PDF state. The monolithic `TracePath()`/`CSMain` route is retired, not a runtime fallback.

The lighting model still includes artistic controls and approximations, particularly water reflections and mesh dielectric traversal. The active wavefront, sampling, and reuse implementations also have unresolved defects and incomplete validation. See [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md) for current findings and repair gates; this overview records implementation, not proof that those defects are fixed.
