# Known Limitations And Next Steps

This document captures current implementation limits and likely future work areas.

## Known Limitations

- Only spheres, emissive sphere lights, and an implicit infinite ground plane are ray traced.
- Unity scene meshes, box colliders, walls, and the scene `Directional Light` are not used by the compute shader renderer.
- There is no acceleration structure. Every ray loops over all spheres and lights.
- `UpdateSpheres()` uploads all sphere/light data every rendered frame, even though component references are cached.
- Mesh buffer fields and `RebuildMeshObjectBuffers()` are present but commented out/inactive.
- `_AmbientLight` is uploaded and declared but unused.
- `_PixelOffset` is uploaded from C# but not declared/used by the shader.
- `GetTextureColorOnSphere()` and `ModifyNormalByBumpColor()` are unused.
- Shadow rays check regular spheres as blockers, but not light spheres.
- Refraction/transparency are approximate and do not use Fresnel or physically accurate Snell-law handling.
- Direct lighting still uses stylized channel-wise `Combine()` inside shadow-light helpers.

## Good Near-Term Fixes

- Release mesh buffers in `OnDestroy()` if mesh tracing is re-enabled.
- Remove or wire up unused shader parameters and helpers.
- Add debug render modes for normals, albedo, emission, direct light, throughput, bounce count, and hit distance.

## Rendering Improvements

- Add frame accumulation for progressive refinement when camera and scene are static.
- Replace sine/hash mutable RNG with a clearer hash-based per-pixel/per-frame random generator.
- Add material types such as diffuse, metal, glass, and emissive.
- Add Fresnel reflectance and better refraction/transmission handling.
- Add diffuse hemisphere sampling for true indirect light and color bleeding.
- Decide whether direct lighting should remain explicitly sampled every bounce or move toward next-event estimation with a more physically based formulation.

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
