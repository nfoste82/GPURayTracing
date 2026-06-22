# Roadmap And Improvements

This document captures likely future work areas. For current implementation limits, see `05-known-limitations.md`. For performance hotspots and benchmark methodology, see `10-benchmarking-and-performance.md`.

## Good Near-Term Fixes

- Add legends or configurable ranges for debug render modes if more detailed diagnostics are needed.
- Add a simple material/debug preset workflow in the scene so material type changes can be compared quickly.
- Add optional benchmark logging to CSV/JSON so frame-time comparisons can be captured without manual note-taking.
- Add GPU timing instrumentation if feasible; Unity's profiler often collapses compute shader time into `Rendering`, so Xcode GPU Frame Capture remains useful on macOS.

## Cheap Rendering Improvements

These options should improve perceived quality with little or no extra ray-intersection cost. Most are better sampling, better parameter mapping, or cheap per-pixel math rather than more rays.

- Expose the depth-of-field aperture/lens radius as a parameter; it is currently a hard-coded `0.005` world-space ray-origin jitter in the shader.
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
- Improve importance-sampled light selection further: fold the surface normal (N·L) and a coarse visibility estimate into `LightImportanceWeight`, and consider a precomputed/global light CDF or a spatial light structure so many-light scenes scale beyond the current `MaxImportanceLights` (`128`) cap without the per-hit weight pass. Raising the cap is cheap but increases the per-hit weight loop cost.
- Pair the random/importance light strategies with frame accumulation so their per-frame noise averages out on static views; without accumulation, the noise must be reduced by raising `lightSampleCount` or `numberOfPasses`.
- Accumulate transmittance through multiple transparent shadow blockers instead of only using the nearest transparent blocker.
- Improve glass refraction with proper sphere entry/exit traversal, Snell-law behavior, and distance-based absorption.
- Improve direct light sampling by sampling sphere lights by visible solid angle instead of approximate disk samples.
- Consider a lightweight denoising or temporal stability pass after accumulation is in place.

## Geometry Improvements

- Improve top-level BVH build quality and traversal ordering if scenes grow to many separate objects; mesh triangles already use per-mesh BVHs.
- Add near-first traversal ordering for top-level and per-mesh BVHs so closer hits reduce `bestHit.distance` earlier and cull farther nodes sooner.
- Consider precomputing or carrying inverse ray direction through BVH traversal to avoid repeated divides in AABB tests.
- Consider a specialized opaque-shadow fast path that tests only whether an opaque blocker exists before the light distance, while preserving the current nearest-transparent-blocker path when transparent objects are present.
- Add imported vertex normal support for smoother mesh shading.
- Add texture/UV support if mesh materials need more than flat `RayMaterial` colors.
- Improve mesh refraction with robust closed-volume traversal, nested media support, internal reflection handling, and distance-based absorption.
- Keep the data model generic enough that spheres and mesh triangles can share the same material/emission shading path.
