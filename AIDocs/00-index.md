# AI Docs Index

Use this folder as focused context for LLM-assisted work on the Unity GPU ray tracer. Read only the documents relevant to the task.

## Documents

- `01-project-overview.md`: High-level project purpose, scene contents, runtime architecture, and feature set.
- `02-runtime-data-flow.md`: How Unity/C# registers objects, builds buffers, sets shader parameters, dispatches the compute shader, and presents the result.
- `03-compute-shader-renderer.md`: Compute shader globals, data structures, ray generation, depth of field, and the high-level iterative path tracing loop.
- `04-materials-lights-scene.md`: How ray-traced sphere materials, emissive light spheres, scene objects, and non-ray-traced Unity physics/mesh objects are represented.
- `05-known-limitations.md`: Known implementation limits, recently completed work, and broad architectural direction.
- `06-shader-intersections-and-bvh.md`: Shader intersection flow, per-mesh BVHs, top-level object BVH, shadow BVH, and runtime BVH thresholds.
- `07-shader-lighting-and-materials.md`: Direct lighting, shadow tinting, material scattering, and sphere/mesh transparency/refraction behavior.
- `08-shader-debugging-and-randomness.md`: Debug render modes and shader random sampling behavior.
- `09-roadmap-and-improvements.md`: Near-term fixes and likely rendering, geometry, and quality improvements.
- `10-benchmarking-and-performance.md`: Benchmark overlay, generated benchmark scenes, performance hotspots, benchmark recommendations, shader compile-time techniques, and the debug-variant compile stall handling.
- `11-regression-testing.md`: EditMode CPU/GPU regression coverage, current-behavior baseline policy, test commands, and planned image/BVH coverage.
- `12-caustics-prototype.md`: Proposed photon-mapped caustics prototype, disabled-path isolation, pipeline, estimator, lifecycle, testing, and staged implementation.
- `13-denoising-and-upscaling.md`: Future reconstruction architecture, denoiser feature buffers, temporal motion/history requirements, Unity 6.3 implications, and DLSS/FSR/MetalFX/STP integration options.

## Fast Context Selection

- To change general shader flow, read `03-compute-shader-renderer.md`.
- To change intersections, mesh traversal, or BVH behavior, read `06-shader-intersections-and-bvh.md` and usually `02-runtime-data-flow.md` for buffer upload details.
- To change lighting, shadows, material scattering, or refraction, read `07-shader-lighting-and-materials.md` and usually `04-materials-lights-scene.md`.
- To change debug modes or sampling/noise behavior, read `08-shader-debugging-and-randomness.md`.
- To change Unity orchestration, object registration, buffers, camera controls, or render dispatch, read `02-runtime-data-flow.md`.
- To add features such as meshes, BVH, accumulation, material types, or better physical lighting, read the relevant shader doc plus `05-known-limitations.md` and `09-roadmap-and-improvements.md`.
- To benchmark or tune performance, read `10-benchmarking-and-performance.md`, plus `06-shader-intersections-and-bvh.md` for BVH-specific work.
- To reduce shader compile time, change the `DEBUG_RENDER` variant split, `[loop]` usage, or the debug-variant compile stall/overlay, read `10-benchmarking-and-performance.md` and `08-shader-debugging-and-randomness.md`.
- To add or update correctness, reflection/refraction, GPU probe, BVH, or image-regression tests, read `11-regression-testing.md` plus the relevant renderer document.
- To prototype caustics without affecting the disabled renderer, read `12-caustics-prototype.md`, `03-compute-shader-renderer.md`, and `07-shader-lighting-and-materials.md`.
- To plan denoising, internal-resolution rendering, temporal upscaling, Unity 6.3 migration choices, or DLSS/FSR/MetalFX/STP integration, read `13-denoising-and-upscaling.md`, then `02-runtime-data-flow.md` and `03-compute-shader-renderer.md` before implementation.
- To understand the project quickly before making broad changes, read `01-project-overview.md` first.
