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
- `12-caustics.md`: Photon-mapped caustics architecture, disabled-path isolation, pipeline, estimator, lifecycle, and testing.
- `13-denoising-and-upscaling.md`: Future reconstruction architecture, denoiser feature buffers, temporal motion/history requirements, Unity 6.3 implications, and DLSS/FSR/MetalFX/STP integration options.
- `14-svgf-implementation-plan.md`: Ordered GPU-native A-trous/SVGF implementation milestones, current status, validation criteria, diagnostics, and remaining denoising work.
- `15-terrain-rendering.md`: GPU heightfield terrain data flow, acceleration structure, scene generation, rank-based layer weight painting, coverage reporting, and limitations.
- `16-accessibility-and-onboarding.md`: Completed public-project onboarding work and remaining accessibility, diagnostics, gallery, quality-preset, testing, and platform-validation improvements.
- `17-adaptive-sampling-continuation.md`: Historical adaptive architecture experiments, benchmark evidence, and rejected approaches; superseded as a plan.
- `18-adaptive-sampling-next-session-prompt.md`: Retired session-prompt redirect to the current sampling plan.
- `19-adaptive-scheduler-accounting-repair.md`: Historical bounded-service accounting failure and evidence; superseded repair instructions removed.
- `20-dammertz-adaptive-sampling-plan.md`: Historical fixed-8x8 Dammertz results and allocation diagnosis; not an active implementation plan.
- `21-adaptive-sampling-performance-plan.md`: Historical root-list cost model and phase-timing evidence; not current throughput acceptance.
- `22-shader-compile-splitting-handoff.md`: Split-compute compile history, measured Metal timings, and current targeted-asset compile guidance.
- `23-initial-ris-direct-lighting-plan.md`: Current local/reused RIS design, approximation constraints, unresolved audit findings, and historical evidence.
- `24-welford-scheduler-recovery-plan.md`: Historical Sponza Welford recovery experiments, candidate settings, and qualified quality/timing evidence.
- `25-adaptive-trace-throughput-plan.md`: Historical layered/guarded/compact trace comparisons and rejected macrotile results.
- `26-wavefront-renderer-handoff.md`: Active queue-driven architecture, historical compile measurements, and unresolved coverage boundaries.
- `27-renderer-sampling-audit-and-repair-plan.md`: Authoritative current sampling audit, source-referenced findings, ordered repair phases, decisions, and acceptance protocol (2026-09-15).

Document 27 supersedes the sampling continuation plans in documents 17-21 and 23-26. Historical
records preserve useful evidence, not instructions to resume retired renderer implementations.
Independent denoising, accessibility, terrain, and other future work remains in its focused docs.

## Fast Context Selection

- To change general shader flow, read `03-compute-shader-renderer.md`.
- To change intersections, mesh traversal, or BVH behavior, read `06-shader-intersections-and-bvh.md` and usually `02-runtime-data-flow.md` for buffer upload details.
- To change lighting, shadows, material scattering, or refraction, read `07-shader-lighting-and-materials.md` and usually `04-materials-lights-scene.md`.
- To change debug modes or sampling/noise behavior, read `08-shader-debugging-and-randomness.md`.
- To change Unity orchestration, object registration, buffers, camera controls, or render dispatch, read `02-runtime-data-flow.md`.
- To add features such as meshes, BVH, accumulation, material types, or better physical lighting, read the relevant shader doc plus `05-known-limitations.md` and `09-roadmap-and-improvements.md`.
- To benchmark or tune performance, read `10-benchmarking-and-performance.md`, plus `06-shader-intersections-and-bvh.md` for BVH-specific work.
- To reduce shader compile time, read `22-shader-compile-splitting-handoff.md` first, then `10-benchmarking-and-performance.md` and `08-shader-debugging-and-randomness.md`.
- To add or update correctness, reflection/refraction, GPU probe, BVH, or image-regression tests, read `11-regression-testing.md` plus the relevant renderer document.
- To change caustics, read `12-caustics.md`, `03-compute-shader-renderer.md`, and `07-shader-lighting-and-materials.md`.
- To plan denoising, internal-resolution rendering, temporal upscaling, Unity 6.3 migration choices, or DLSS/FSR/MetalFX/STP integration, read `13-denoising-and-upscaling.md`, then `02-runtime-data-flow.md` and `03-compute-shader-renderer.md` before implementation.
- To implement or continue the GPU-native A-trous/SVGF denoiser, read `14-svgf-implementation-plan.md` and `13-denoising-and-upscaling.md`, then the relevant renderer/shader documents.
- To understand the project quickly before making broad changes, read `01-project-overview.md` first.
- To change terrain layer textures, weights, or elevation/slope banding, read `15-terrain-rendering.md`; declare rules in rank/degree space via `TerrainLayerPainter` and verify with `Tools > Ray Tracing > Report Terrain Coverage`.
- To regenerate generated scenes after changing generator code, use `Tools > Ray Tracing > Regenerate Scenes (Delete Existing Scenes)`; plain `Generate Scenes` skips scenes that already exist.
- To continue reducing the public project's barrier to entry, read `16-accessibility-and-onboarding.md`.
- To investigate sampling correctness or convergence, start with `27-renderer-sampling-audit-and-repair-plan.md`; first T1 GGX, T2/local R5 MIS, T3/T7 terminal depth, and T4/T8 mesh-emitter/sun repairs have focused GPU coverage. Broader acceptance and S1/S2/S3 remain pending. Phase 1 is in progress, not complete.
- To change adaptive allocation or throughput, read document 27, then `26-wavefront-renderer-handoff.md`, `08-shader-debugging-and-randomness.md`, and `10-benchmarking-and-performance.md`. Load historical documents 17-21/24-25 only for a specific experiment's evidence.
- To change wavefront stages, read document 27 and `26-wavefront-renderer-handoff.md`, then the relevant shader and regression-testing documents. The migration is integrated, not a pending feature-porting plan.
- To change local, temporal, or spatial RIS, read document 27, then `23-initial-ris-direct-lighting-plan.md`, `07-shader-lighting-and-materials.md`, `10-benchmarking-and-performance.md`, and `11-regression-testing.md`.
