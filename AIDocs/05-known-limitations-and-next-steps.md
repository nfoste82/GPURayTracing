# Known Limitations And Next Steps

This document captures current implementation limits and likely future work areas.

## Known Limitations

- Spheres, emissive sphere lights, registered triangle meshes, and an implicit infinite ground plane are ray traced.
- Unity meshes are traced only when registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`; box colliders and the scene `Directional Light` are not used by the compute shader renderer.
- There is no acceleration structure. Every ray loops over all spheres, lights, and triangles.
- `UpdateSpheres()` uploads all sphere/light data every rendered frame, even though component references are cached.
- Debug render modes are basic first-hit/path diagnostics and do not include UI overlays, legends, or configurable visualization ranges.
- Shadow rays check regular spheres and triangles as blockers, but not light spheres.
- Refraction/transparency use Fresnel material selection, but sphere and mesh transmitted paths are still approximate rather than a full physically accurate volume traversal.
- Direct lighting accumulates additively and uses clamped inverse-square-style falloff, but transparent shadow tinting is still approximate rather than fully physically based.
- Diffuse scattering uses cosine-weighted hemisphere sampling on later bounces, but there is no denoising/accumulation to control variance.
- Mesh triangle normals are flat face normals; imported vertex normals, smoothing groups, UVs, and textures are not used.
- Mesh refraction assumes a mostly closed/convex mesh and uses the nearest same-mesh triangle as the exit face. It does not handle nested media, multi-hit internal reflections, or distance-based absorption.

## Recently Completed

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
- Single-frame mode can be disabled from the inspector, `T`, or `Space` to resume real-time rendering.
- Unused ambient/checkerboard shader parameters, unused shader helpers, and inactive mesh-buffer scaffolding were removed.
- Direct light sampling is skipped when path throughput is below `MinDirectLightThroughput`.
- Triangle mesh upload from Unity `MeshFilter` components was added for registered mesh objects.
- Triangle intersection was added to first-hit tracing, autofocus checks, and shadow blockers.
- `RayMeshPrimitive` and `GameObject > Ray Tracing` editor menu items were added for cube, pyramid, and dodecahedron mesh test objects.
- Mesh triangle uploads now rebuild only when registered mesh transforms or ray material values change.
- Mesh glass refraction now approximates entry and exit through closed triangle meshes using per-object `meshIndex` values.

## Good Near-Term Fixes

- Add UI overlays, legends, or configurable ranges for debug render modes if more detailed diagnostics are needed.
- Add a simple material/debug preset workflow in the scene so material type changes can be compared quickly.

## Cheap Rendering Improvements

These options should improve perceived quality with little or no extra ray-intersection cost. Most are better sampling, better parameter mapping, or cheap per-pixel math rather than more rays.

- Add tone mapping and exposure controls so bright lighting rolls off more pleasantly instead of clipping harshly.
- Add an optional firefly/outlier clamp to reduce rare bright speckles in single-frame renders.
- Improve `Smoothness` to roughness mapping, such as using a perceptual squared roughness curve, so material controls feel more predictable.
- Improve rough metal reflection sampling by sampling around the ideal reflection lobe instead of randomizing the normal with axis-aligned noise.
- Refine diffuse sampling basis construction and below-surface rejection to avoid unstable or invalid sample directions.
- Tune direct light defaults, including `lightFalloffScale`, light radius/intensity expectations, and scene light colors.
- Consider adding a global light intensity or exposure scale if light setup remains hard to balance.
- Skip work that cannot contribute much, such as direct lighting for nearly black throughput, roughness randomization when smoothness is effectively `1`, or continuation work when `_NumBounces <= 1`.
- Review default inspector values for `groundSmoothness`, `shadowRandomness`, `numberOfPasses`, `shadowQuality`, and `randomNoise` after visual testing.
- Add UI overlays, legends, or configurable ranges for debug render modes if more detailed diagnostics are needed.
- Add a simple material/debug preset workflow in the scene so material type and smoothness changes can be compared quickly.

## More Expensive Rendering Improvements

- Add frame accumulation for progressive refinement when camera and scene are static.
- Reset accumulation when camera, focus, quality settings, material values, object transforms, or render size change.
- Further improve diffuse indirect lighting with lower-variance sampling and material-specific BRDF/PDF handling as new materials are added.
- Improve transparent absorption and next-event estimation toward a more physically based formulation.
- Accumulate transmittance through multiple transparent shadow blockers instead of only using the nearest transparent blocker.
- Improve glass refraction with proper sphere entry/exit traversal, Snell-law behavior, and distance-based absorption.
- Improve direct light sampling by sampling sphere lights by visible solid angle instead of approximate disk samples.
- Consider a lightweight denoising or temporal stability pass after accumulation is in place.

## Geometry Improvements

- Add a BVH or other acceleration structure before supporting large meshes.
- Add imported vertex normal support for smoother mesh shading.
- Add texture/UV support if mesh materials need more than flat `RayMaterial` colors.
- Improve mesh refraction with robust closed-volume traversal, nested media support, internal reflection handling, and distance-based absorption.
- Keep the data model generic enough that spheres and mesh triangles can share the same material/emission shading path.

## Performance Hotspots

- Soft shadows scale with lights, shadow quality, and blocker count.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`, where geometry now includes triangles.
- Transparent shadows and transparent ray paths add extra math and intersection tests.
- Mesh refraction adds internal same-mesh triangle intersection work for transmitted glass paths.
- Without accumulation, increasing `numberOfPasses` is the main anti-aliasing/noise reduction path and directly increases per-frame cost.

## Architectural Direction

The shader now has a cleaner iterative `TracePath()` loop with explicit radiance, throughput, albedo, and emission terms. Future renderer work should preserve that separation rather than reintroducing one-off per-bounce color trees.

For major features, prefer building toward a generic hit/material/shading abstraction:

- Intersections produce a `RayHit`.
- `RayHit` maps to material data.
- Shading consumes ray, hit, material, and lights.
- Path state remains explicit in `radiance` and `throughput`.
