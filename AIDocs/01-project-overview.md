# Project Overview

This is a Unity real-time GPU ray/path tracing project. The scene runs inside Unity, but the actual image generation is performed by a compute shader in `Assets/Scripts/RayTracingCompute.compute`.

The renderer currently ray traces spheres, emissive sphere and mesh lights, registered triangle meshes, an implicit infinite ground plane, and one optional finite procedural water surface. Unity scene meshes, walls, and colliders that are not registered as ray-traced objects still exist mostly for scene organization and physics; they are not traced by the compute shader.

## Key Files

- `Assets/Scripts/GameManager.cs`: Main Unity-side controller. Owns render texture creation, compute shader dispatch, quality settings, camera controls, autofocus, object buffers, shader parameter uploads, implicit ground/water preview drawing, and optional Unity skybox preview sync. It does not implement `OnRenderImage()` itself; it exposes `RenderImage(src, dest)`, which is called by `RayTracingCameraRenderer`.
- `Assets/Scripts/RayTracingCameraRenderer.cs`: Camera component whose `OnRenderImage()` delegates to `GameManager.RenderImage()`. It runs on whatever camera holds this component (typically the same camera wired into `GameManager.renderTextureCamera`, but that link is only inspector wiring, not enforced in code).
- `Assets/Scripts/RayTracingCompute.compute`: Main GPU renderer. Generates camera rays, performs intersections, computes lighting/shadows/reflections/refraction, and writes the final pixel color.
- `Assets/Scripts/RayTracingBenchmarkOverlay.cs`: Runtime benchmark overlay for frame timing, geometry counts, BVH status, and quality settings.
- `Assets/Scripts/BenchmarkOrbitMover.cs`: Simple deterministic movement helper for dynamic benchmark scenes.
- `Assets/Scripts/RayTracingObject.cs`: Registers and unregisters ray-traced scene objects with the nearest parent `GameManager`.
- `Assets/Scripts/RayMaterial.cs`: Per-object render material data: material type, color, optional mesh albedo texture, smoothness, opacity, and refraction index.
- `Assets/Scripts/RayMeshPrimitive.cs`: Procedural mesh primitive helper for ray-traced cube, pyramid, and dodecahedron test objects.
- `Assets/Scripts/RayObjectPreview.cs`: Editor/runtime helper that adds rasterized sphere previews and optional Unity point-light previews for ray-traced sphere and light objects.
- `Assets/Scripts/RayLight.cs`: Per-light sphere emission color.
- `Assets/Scripts/ColorExtensions.cs`: Converts `Color32` to normalized `Vector3` values for GPU upload.
- `Assets/Editor/RayMeshPrimitiveMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for creating ray-traced mesh primitive test objects in the hierarchy.
- `Assets/Editor/RaySceneObjectMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for ray-traced spheres, light spheres, and a ground preview plane.
- `Assets/Editor/RayTracingBenchmarkSceneGenerator.cs`: Adds `Tools > Ray Tracing > Generate Benchmark Scenes` for creating focused performance scenes.
- `Assets/Editor/RayTracingShaderPrecompiler.cs`: Adds `Tools > Ray Tracing > Precompile Compute Shader`. Forces the compute shader to compile and dispatch once from edit mode (with timing and surfaced compile messages) so a slow or failing kernel shows up here instead of stalling Unity on first Play. Unity compiles compute kernels lazily on first `Dispatch`, which is why pathological kernels previously only hung when entering Play mode.
- `Assets/Scenes/Root.unity`: Main scene with the camera, game manager, ray-traced spheres, light spheres, physics objects, and visual scene geometry.
- `Assets/Scenes/Benchmarks/*.unity`: Generated benchmark scenes for stressing specific renderer paths.

## Runtime Feature Set

- Real-time compute-shader rendering driven through `RayTracingCameraRenderer.OnRenderImage()`, which calls `GameManager.RenderImage()`.
- Dynamic sphere transforms and physics-driven sphere movement.
- Registered triangle mesh objects through `RayTracingObject` + `RayMaterial` + `MeshFilter`.
- Mesh UV/albedo texture sampling through a fixed-resolution texture array. This is currently mesh-only and does not include normal, roughness, opacity, or emission maps.
- Editor-created ray-traced cube, pyramid, and dodecahedron primitives that remain visible in Scene view but hide their rasterized `MeshRenderer` in Play mode by default.
- Scene-view previews for ray-traced sphere and light-sphere objects through gizmos and optional rasterized `RayObjectPreview` meshes.
- Scene-view ground preview for the implicit ray-traced ground plane and a bounds/average-level preview for procedural water.
- Optional Unity skybox preview synced from `GameManager.skyboxTexture` and tinted by `_skyboxLightColor`.
- Emissive sphere and mesh lights.
- Direct lighting with hard/soft shadow sampling.
- Smoothness-controlled direct specular highlights. These are an approximate direct-light lobe rather than a fully consistent path-traced BRDF.
- Selectable direct-light sampling strategy (all lights, uniform random, or importance-sampled) with a configurable per-hit light sample count, for trading noise against cost in many-light scenes.
- ACES filmic tone mapping with a configurable `exposure` control, applied to the final color (debug modes are left untone-mapped).
- Transparent/glass objects with Snell refraction, distance-based RGB absorption, approximate sphere and closed-mesh entry/exit traversal, bounded interior-object detection, and bounded mesh total internal reflection.
- Colored shadows through transparent blockers.
- Surface reflections with roughness approximated by randomized normals.
- Depth of field with optional CPU-side autofocus. The aperture jitter is a hard-coded `0.005` world-space ray-origin offset in the shader and is not currently exposed as a configurable aperture.
- Configurable samples per pixel via `numberOfPasses`.
- Configurable bounce count via `numBounces` / `_NumBounces`.
- Optional top-level object BVH and separate shadow-blocker BVH, each controlled by runtime thresholds so small scenes can stay on cheaper flat loops.
- One optional finite, animated procedural water surface with Fresnel reflection/refraction and distance-based underwater absorption.
- Benchmark overlay and generated benchmark scenes for comparing optimization behavior across different workloads.

## Current Renderer Shape

The renderer has been refactored into an iterative path tracing structure. `TracePath()` uses explicit `radiance`, `throughput`, `albedo`, and `emission` terms instead of a manually unrolled second/third-bounce color tree.

The lighting model is still partly stylized. Direct lighting uses explicit stochastic light sampling each bounce, accumulates sampled light additively, and uses a clamped inverse-square-style falloff, but its direct specular response, transparent shadow tinting, and medium tracking are not yet part of a fully consistent physical BRDF/volume formulation. This is cleaner than the original layout, but it is not yet a fully physically based path tracer.
