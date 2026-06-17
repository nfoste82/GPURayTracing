# Project Overview

This is a Unity 2019.3 real-time GPU ray/path tracing project. The scene runs inside Unity, but the actual image generation is performed by a compute shader in `Assets/Scripts/RayTracingCompute.compute`.

The renderer currently ray traces spheres, emissive sphere lights, and an implicit infinite ground plane. Unity scene meshes, walls, and colliders exist mostly for scene organization and physics; they are not currently traced by the compute shader.

## Key Files

- `Assets/Scripts/GameManager.cs`: Main Unity-side controller. Owns render texture creation, compute shader dispatch, quality settings, camera controls, autofocus, object buffers, and shader parameter uploads.
- `Assets/Scripts/RayTracingCompute.compute`: Main GPU renderer. Generates camera rays, performs intersections, computes lighting/shadows/reflections/refraction, and writes the final pixel color.
- `Assets/Scripts/RayTracingObject.cs`: Registers and unregisters ray-traced scene objects with the nearest parent `GameManager`.
- `Assets/Scripts/RayMaterial.cs`: Per-sphere render material data: color, smoothness, opacity, and refraction index.
- `Assets/Scripts/RayLight.cs`: Per-light sphere emission color.
- `Assets/Scripts/ColorExtensions.cs`: Converts `Color32` to normalized `Vector3` values for GPU upload.
- `Assets/Scenes/Root.unity`: Main scene with the camera, game manager, ray-traced spheres, light spheres, physics objects, and visual scene geometry.

## Runtime Feature Set

- Real-time compute-shader rendering through `OnRenderImage()`.
- Dynamic sphere transforms and physics-driven sphere movement.
- Emissive sphere lights.
- Direct lighting with hard/soft shadow sampling.
- Transparent objects with approximate refraction.
- Colored shadows through transparent blockers.
- Surface reflections with roughness approximated by randomized normals.
- Depth of field with optional CPU-side autofocus.
- Configurable samples per pixel via `numberOfPasses`.
- Configurable bounce count via `numBounces` / `_NumBounces`.

## Current Renderer Shape

The renderer has been refactored into an iterative path tracing structure. `TracePath()` uses explicit `radiance`, `throughput`, `albedo`, and `emission` terms instead of a manually unrolled second/third-bounce color tree.

The lighting model is still partly stylized. Direct lighting uses explicit light sampling each bounce, and shadow contribution still uses channel-wise `Combine()` inside the shadow functions. This is cleaner than the original layout, but it is not yet a fully physically based BRDF/path tracer.
