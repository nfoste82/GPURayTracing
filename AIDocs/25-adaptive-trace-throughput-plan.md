# Adaptive Trace Throughput History

## Historical / Superseded

This is the pre-wavefront layered-trace experiment record, following the August 2026 adaptive
work. Individual capture dates were not recorded in the original entries. The single authoritative
current plan is [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
The old measurement order, next optimizations, and standalone trace requirements are superseded.
Adaptive rendering now uses layered wavefront generation and resolve, not `CSAdaptiveTrace` or
`CSMain` as a runtime fallback. No current fix or benchmark validation is asserted here.

## Historical Policy And Layering

The selected experimental policy was luminance-normalized Welford (`adaptiveLuminanceErrorWeight=-1`),
8x8 groups, `H=1.7`, deterministic `oldPixelPathCount + localSample`, finite-radiance handling,
and count-weighted HDR/RGB Welford updates. Dammertz and its alternating-sample state were retired
because it was not the selected policy. The earlier throughput objective of approximately 20%
overhead was a target, not a demonstrated result.

The standalone layered trace ran one path per participating pixel per full-screen 4x4 dispatch,
then updated estimator state after tracing. A two-path group participated in layers zero and one.
The historical host bound `ceil(min(highestBucketRate, maxPathsPerPixel))` covered that H=1.7,
max-three established schedule in two layers. It was not proof that arbitrary bootstrap passes
fit the bound; the current bootstrap/max-layer audit finding remains in document 27.

Controlled historical parity compared two consecutive layers with a two-sample reference. It
supported that fixture's accumulation and sample-index behavior, not broad wavefront parity.

## Invalid Initial Timing

The initial `1024x1024`, 120-frame TeapotMaterials layered capture reported `915.87 ms/frame`
adaptive, but its uniform row claimed an implausible `0.146 ms/frame` for 120 frames and
125.83M paths. The comparison was rejected. Even the apparently encouraging adaptive value
cannot establish a speedup over the prior guarded `987.3 ms/frame` two-run mean across builds.

## Macrotile Experiment Rejected

A temporary same-frame macrotile route submitted contiguous regions in tile-outer/layer-inner
order while preserving scheduler and estimator behavior. Focused GPU parity reported matching
RGB accumulation, Welford state, beauty, and retired paths. Two `1024x1024` TeapotMaterials
fixed-frame runs with reversed variant order and 30-second cooldowns rejected its throughput:

| Candidate | Run 1 ms/frame | Run 2 ms/frame | Mean ms/frame | Versus Fullscreen |
| --- | ---: | ---: | ---: | --- |
| Fullscreen layered | 1556.0 | 1469.4 | 1512.7 | Baseline |
| 4x4 macrotiles | 1733.4 | 2023.1 | 1878.2 | 24.2% slower |
| 8x8 macrotiles | 2793.4 | 2842.4 | 2817.9 | 86.3% slower |

All adaptive candidates reported `125,777,152` paths and final RGB RMSE about `0.007278523`.
This was a throughput rejection rather than an allocation failure. Scheduler fences were broadly
comparable while trace time rose sharply. H=1.7 fullscreen used two dispatches per frame versus
32 or 128 macrotile rectangles; CPU/driver submission and smaller-dispatch costs outweighed
the proposed locality benefit on that backend. The macrotile implementation was removed.
This evidence rejects that CPU-submitted full-coverage experiment, not every possible GPU queue
or spatial compaction design; it does not authorize a new compaction project.

## Unvalidated Priority And Bootstrap Experiments

The early spatial-disagreement experiment added group RGB RMS disagreement around the group mean
as an optional priority bonus. It was explicitly not a noise estimate: real edges, textures, and
material boundaries can raise it. Its normalized bonus decayed by
`min(1, minSamples / averageGroupSpp)`. Strength zero preserved the then-selected Welford score.

`Assets/Editor/RayTracingExperiments/teapotmaterials_spatial_disagreement_priority_1024_120f.json`
recorded a zero-strength control and `0.25/0.5` candidates with H=1.7 and no coarse bootstrap.
No acceptance results were recorded here. The historical zero default and those small trial
strengths are not evidence for the current spatial-priority default of 4.

Low-resolution bootstrap was made opt-in at that milestone. The no-bootstrap path began with
genuine full-resolution samples rather than seeded coarse history or a held preview.
`Assets/Editor/RayTracingExperiments/teapotmaterials_layered_welford_fast_bootstrap_1024_120f.json`
compared it with an explicitly enabled twelve-frame low-resolution bootstrap. This document
recorded the experiment setup, not a completed quality or bias validation.

## Retained Measurement Lessons

- Instrumented accounting smoke and diagnostic-disabled throughput are different measurements.
  A manifest disabling per-frame instrumentation does not prove every current timed diagnostic is
  excluded; the current timing audit is authoritative.
- A uniform-allocation adaptive control separated trace/state overhead from heterogeneous service.
  It was a diagnostic concept, not proof that adaptive and uniform sequences currently match.
- Rotated order, cooldowns, scene/seed consistency, and actual path counts matter because thermal
  drift and startup cost were substantial.
- Equal fine-path quality gains from
  [Welford Recovery History](24-welford-scheduler-recovery-plan.md) did not establish an all-seed
  equal-time win or validate current defaults, layer-capacity memory, or retirement telemetry.

The proposed metadata gating, linear remap dispatch, parallel reduction, cached tiers, and tile
compaction follow-ups are no longer pending instructions. Any current work is prioritized only
in document 27.
