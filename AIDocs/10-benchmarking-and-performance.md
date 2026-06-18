# Benchmarking And Performance

This document covers runtime benchmark tooling, performance hotspots, and benchmark recommendations.

## Benchmarking Flow

`Assets/Scripts/RayTracingBenchmarkOverlay.cs` can be attached to the camera and references the active `GameManager`. It shows averaged frame time, render size, quality settings, sphere/light/mesh/triangle counts, top-level BVH status, shadow BVH status, and thresholds. Press `F3` to toggle the overlay.

`Tools > Ray Tracing > Generate Benchmark Scenes` runs `RayTracingBenchmarkSceneGenerator` and creates focused scenes under `Assets/Scenes/Benchmarks/`:

- `Benchmark_ManySpheres`: stresses flat sphere loops versus the general top-level BVH.
- `Benchmark_ShadowBlockers`: stresses direct-light shadow rays and the shadow-only blocker BVH.
- `Benchmark_ManyLights`: stresses the per-hit loop over emissive sphere lights.
- `Benchmark_DenseMesh`: stresses per-mesh BVH traversal and leaf triangle tests.
- `Benchmark_ManyMeshes`: stresses object-level culling for many registered mesh objects.
- `Benchmark_Glass`: stresses transparent/refraction paths and transparent shadows.
- `Benchmark_Sparse`: catches acceleration-structure overhead regressions in small scenes.
- `Benchmark_Dynamic`: stresses per-frame transform updates, BVH rebuilds, and buffer uploads.

## Performance Hotspots

- Soft shadows scale with lights, shadow quality, sphere count, and intersected mesh BVH nodes/leaves.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`; triangle meshes are accelerated, but spheres, lights, BVH traversal, and leaf triangle tests still contribute.
- Transparent shadows and transparent ray paths add extra math and intersection tests.
- Mesh refraction adds internal same-mesh triangle intersection work for transmitted glass paths.
- Without accumulation, increasing `numberOfPasses` is the main anti-aliasing/noise reduction path and directly increases per-frame cost.

## Benchmark Recommendations

- Use `Benchmark_ManySpheres` to evaluate `topLevelBvhMinObjectCount` for sphere-heavy first-hit traversal. Force TLAS on with `0`, and force flat loops by setting the threshold above the overlay's TLAS object count.
- Use `Benchmark_ManyMeshes` to evaluate the general top-level BVH for many registered mesh objects.
- Use `Benchmark_ShadowBlockers` to evaluate `shadowBvhMinObjectCount`. Force shadow BVH on with `0`, and force flat shadow loops by setting the threshold above the overlay's shadow blocker count.
- Keep `shadowBvhMinObjectCount` fixed while evaluating `topLevelBvhMinObjectCount`, and keep `topLevelBvhMinObjectCount` fixed while evaluating `shadowBvhMinObjectCount`, otherwise the results are hard to interpret.
- Use `DebugRenderMode.AccelerationStructures` and the overlay to confirm the intended BVH path is actually active before comparing frame times.
- In shadow-heavy scenes, the shadow-only BVH has shown measurable benefit. In `Benchmark_ShadowBlockers`, the general top-level BVH is not expected to move performance much because the workload is dominated by shadow rays, not first-hit object lookup.
