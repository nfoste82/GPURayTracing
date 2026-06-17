# Project Overview

This is a Unity real-time GPU ray/path tracing project. The scene runs inside Unity, but the actual image generation is performed by a compute shader in `Assets/Scripts/RayTracingCompute.compute`.

The renderer currently ray traces spheres, emissive sphere lights, registered triangle meshes, and an implicit infinite ground plane. Unity scene meshes, walls, and colliders that are not registered as ray-traced objects still exist mostly for scene organization and physics; they are not traced by the compute shader.

## Key Files

- `Assets/Scripts/GameManager.cs`: Main Unity-side controller. Owns render texture creation, compute shader dispatch, quality settings, camera controls, autofocus, object buffers, shader parameter uploads, implicit ground preview drawing, and optional Unity skybox preview sync.
- `Assets/Scripts/RayTracingCompute.compute`: Main GPU renderer. Generates camera rays, performs intersections, computes lighting/shadows/reflections/refraction, and writes the final pixel color.
- `Assets/Scripts/RayTracingObject.cs`: Registers and unregisters ray-traced scene objects with the nearest parent `GameManager`.
- `Assets/Scripts/RayMaterial.cs`: Per-object render material data: material type, color, smoothness, opacity, and refraction index.
- `Assets/Scripts/RayMeshPrimitive.cs`: Procedural mesh primitive helper for ray-traced cube, pyramid, and dodecahedron test objects.
- `Assets/Scripts/RayObjectPreview.cs`: Editor/runtime helper that adds rasterized sphere previews and optional Unity point-light previews for ray-traced sphere and light objects.
- `Assets/Scripts/RayLight.cs`: Per-light sphere emission color.
- `Assets/Scripts/ColorExtensions.cs`: Converts `Color32` to normalized `Vector3` values for GPU upload.
- `Assets/Editor/RayMeshPrimitiveMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for creating ray-traced mesh primitive test objects in the hierarchy.
- `Assets/Editor/RaySceneObjectMenu.cs`: Adds `GameObject > Ray Tracing` menu entries for ray-traced spheres, light spheres, and a ground preview plane.
- `Assets/Scenes/Root.unity`: Main scene with the camera, game manager, ray-traced spheres, light spheres, physics objects, and visual scene geometry.

## Runtime Feature Set

- Real-time compute-shader rendering through `OnRenderImage()`.
- Dynamic sphere transforms and physics-driven sphere movement.
- Registered triangle mesh objects through `RayTracingObject` + `RayMaterial` + `MeshFilter`.
- Editor-created ray-traced cube, pyramid, and dodecahedron primitives that remain visible in Scene view but hide their rasterized `MeshRenderer` in Play mode by default.
- Scene-view previews for ray-traced sphere and light-sphere objects through gizmos and optional rasterized `RayObjectPreview` meshes.
- Scene-view ground preview for the implicit ray-traced ground plane.
- Optional Unity skybox preview synced from `GameManager.skyboxTexture` and tinted by `_skyboxLightColor`.
- Emissive sphere lights.
- Direct lighting with hard/soft shadow sampling.
- Transparent/glass objects with approximate sphere refraction and approximate closed-mesh entry/exit refraction for triangle meshes.
- Colored shadows through transparent blockers.
- Surface reflections with roughness approximated by randomized normals.
- Depth of field with optional CPU-side autofocus.
- Configurable samples per pixel via `numberOfPasses`.
- Configurable bounce count via `numBounces` / `_NumBounces`.

## Current Renderer Shape

The renderer has been refactored into an iterative path tracing structure. `TracePath()` uses explicit `radiance`, `throughput`, `albedo`, and `emission` terms instead of a manually unrolled second/third-bounce color tree.

The lighting model is still partly stylized. Direct lighting uses explicit stochastic light sampling each bounce, accumulates sampled light additively, and uses a clamped inverse-square-style falloff, but transparent shadow tinting is not yet fully physically based. This is cleaner than the original layout, but it is not yet a fully physically based BRDF/path tracer.
