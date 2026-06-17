# Known Limitations And Next Steps

This document captures current implementation limits and likely future work areas.

## Known Limitations

- Only spheres, emissive sphere lights, and an implicit infinite ground plane are ray traced.
- Unity scene meshes, box colliders, walls, and the scene `Directional Light` are not used by the compute shader renderer.
- There is no acceleration structure. Every ray loops over all spheres and lights.
- `UpdateSpheres()` uploads all sphere/light data every rendered frame, even though component references are cached.
- Mesh buffer fields and `RebuildMeshObjectBuffers()` are present but commented out/inactive.
- `_AmbientLight` is uploaded and declared but unused.
- `GetTextureColorOnSphere()` and `ModifyNormalByBumpColor()` are unused.
- Debug render modes are basic first-hit/path diagnostics and do not include UI overlays, legends, or configurable visualization ranges.
- Shadow rays check regular spheres as blockers, but not light spheres.
- Refraction/transparency use Fresnel material selection, but transmitted paths still use approximate sphere refraction rather than physically accurate Snell-law volume traversal.
- Direct lighting accumulates additively and uses clamped inverse-square-style falloff, but transparent shadow tinting is still approximate rather than fully physically based.
- Diffuse scattering uses cosine-weighted hemisphere sampling on later bounces, but there is no denoising/accumulation to control variance.

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

## Good Near-Term Fixes

- Release mesh buffers in `OnDestroy()` if mesh tracing is re-enabled.
- Remove or wire up unused shader parameters and helpers.
- Add UI overlays, legends, or configurable ranges for debug render modes if more detailed diagnostics are needed.
- Add a simple material/debug preset workflow in the scene so material type changes can be compared quickly.

## Rendering Improvements

- Add frame accumulation for progressive refinement when camera and scene are static.
- Reset accumulation when camera, focus, quality settings, material values, object transforms, or render size change.
- Further improve diffuse indirect lighting with lower-variance sampling and material-specific BRDF/PDF handling as new materials are added.
- Improve transparent absorption and next-event estimation toward a more physically based formulation.
- Consider a lightweight denoising or temporal stability pass after accumulation is in place.

## Geometry Improvements

- Activate triangle mesh upload from Unity meshes.
- Add triangle intersection in the compute shader.
- Add a BVH or other acceleration structure before supporting large meshes.
- Keep the data model generic enough that spheres and mesh triangles can share the same material/emission shading path.

## Performance Hotspots

- Soft shadows scale with lights, shadow quality, and blocker count.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`.
- Transparent shadows and transparent ray paths add extra math and intersection tests.
- Without accumulation, increasing `numberOfPasses` is the main anti-aliasing/noise reduction path and directly increases per-frame cost.

## Architectural Direction

The shader now has a cleaner iterative `TracePath()` loop with explicit radiance, throughput, albedo, and emission terms. Future renderer work should preserve that separation rather than reintroducing one-off per-bounce color trees.

For major features, prefer building toward a generic hit/material/shading abstraction:

- Intersections produce a `RayHit`.
- `RayHit` maps to material data.
- Shading consumes ray, hit, material, and lights.
- Path state remains explicit in `radiance` and `throughput`.
