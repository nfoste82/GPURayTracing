# Adaptive Trace Throughput Plan

## Status

**Current phase: layered one-sample Welford trace implemented; establish same-build performance
and equal-path correctness before changing the scheduler further.**

The initial 1024x1024/120-frame `TeapotMaterials` capture wrote `915.87 ms/frame` for layered
adaptive, but its adaptive-off row reported an implausible `0.146 ms/frame` while still claiming
120 frames and 125.83M paths. Do not use that capture for comparison. The adaptive number is
directionally encouraging versus the prior guarded two-run mean of `987.3 ms/frame`, but it needs
a repeatable rotated-order capture with credible adaptive-off timings before it becomes evidence.

## Objective

The fixed-8x8 luminance-normalized Welford scheduler produces better images at equal fine-path
budgets, but adaptive path throughput remains below uniform `CSMain`. The goal is to reduce total
adaptive overhead to approximately 20 percent while preserving the validated allocation policy.

## Retained Policy

```text
adaptiveLuminanceErrorWeight = -1
Welford standard-error group score
8x8 allocation groups
sampleIndex = oldPixelPathCount + localSample
finite-radiance replacement, HDR accumulation, RGB Welford M2
requested == assigned == active-pixel paths == retired in capture diagnostics
CSMain unchanged while adaptive sampling is disabled
```

Dammertz split-estimator scheduling is retired. It was not the selected policy and its alternating
state kept unnecessary values live through adaptive tracing. Capture comparison now evaluates the
single Welford adaptive candidate against adaptive-off.

## Current Implementation

### Early Spatial-Disagreement Priority Experiment

The scheduler now exposes `adaptiveSpatialDisagreementPriority`, defaulting to zero. When enabled,
an established 8x8 group receives an additive early-priority bonus from the RGB RMS disagreement of
its accumulated pixel means around their group mean. This is deliberately not a noise estimate:
edges, textures, and material boundaries can also raise it. The bonus is luminance-normalized with
the established score policy and decays as `min(1, minSamples / averageGroupSpp)`, so persistent
scene contrast becomes negligible as the group accumulates samples. Bootstrap groups remain under
the existing count-driven path and strength zero retains the prior Welford-only score.

`Assets/Editor/RayTracingExperiments/teapotmaterials_spatial_disagreement_priority_1024_120f.json`
provides adaptive-off context, a zero-strength adaptive control, and `0.25`/`0.5` candidates. It
uses the normalized Welford policy, fixed 8x8 groups, `H=1.7`, and no low-resolution bootstrap.
Its timed pass disables per-frame adaptive instrumentation to avoid capture timing distortion while
retaining final images, reference metrics, and difference comparisons. Run a separate instrumented
accounting/diagnostic smoke before treating a candidate's timing or image result as valid. Accept a
candidate only after exact accounting and equal-path RGB RMSE improve over the zero-strength control;
then repeat the selected strength across rotated three-seed captures.

### Macrotile Dispatch Experiment Rejected

A same-frame macrotile trace experiment was implemented temporarily to preserve the full scheduler
and estimator while submitting contiguous regions in tile-outer/layer-inner order. It passed focused
GPU parity: full-screen and tiled routes had matching RGB accumulation, Welford state, beauty, and
exact retired paths. It was then removed because two fixed-frame `1024x1024` TeapotMaterials runs,
with reversed variant order and 30-second cooldowns, rejected it decisively:

```text
candidate                 run 1 ms/frame   run 2 ms/frame   mean     versus fullscreen
fullscreen layered              1556.0           1469.4     1512.7        baseline
4x4 macrotiles                  1733.4           2023.1     1878.2        24.2% slower
8x8 macrotiles                  2793.4           2842.4     2817.9        86.3% slower
```

All adaptive candidates retired `125,777,152` paths and produced the same reported final RGB RMSE
(`~0.007278523`), so this is a throughput rejection rather than an allocation or correctness
failure. Scheduler fences remained broadly comparable while trace time increased sharply. With the
validated `H=1.7` policy, fullscreen uses two trace dispatches per frame; the candidates submitted
32 (`4x4`) or 128 (`8x8`) rectangles. The CPU/driver submission and smaller-dispatch cost outweighs
any locality benefit on this backend.

Do not reintroduce CPU-submitted full-coverage macrotiles. Keep the fullscreen layered trace as the
production route. Revisit spatial work compaction only if service breadth becomes much lower than
the current roughly 86%, and only as a separate GPU-generated active-tile/indirect-dispatch
experiment with its own accounting and parity gates.

Low-resolution bootstrap is now opt-in (`enableAdaptiveBootstrap = false` by default). The default
adaptive path begins with the same full-resolution first frame as `CSMain`; enable bootstrap only
when its preview/seed tradeoff is deliberately being evaluated.

`CSAdaptiveTrace` now executes exactly one path when a pixel's group allocation is greater than
`_AdaptiveSampleLayer`. `GameManager` submits one regular full-screen 4x4 dispatch for each
required layer. For example, an allocation of two paths participates in layers zero and one.

The trace reads only the old path count before `TracePath()`, then loads and updates accumulation
and Welford state after tracing. This should reduce dynamic-loop divergence and estimator register
pressure while retaining the deterministic per-pixel sample sequence.

The initial runtime layer count is `ceil(min(highestBucketRate, maxPathsPerPixel))`. This covers
the current validated `H=1.7`, max-three schedule with two layers without submitting unused
third-layer work. If a future allocation can exceed this bound, update the bound and rerun parity.

## Measurement Order

1. Run the controlled GPU parity fixture. Its group allocation is two and therefore verifies that
   consecutive one-sample layers produce the same sample-indexed accumulation and Welford state as
   the two-sample reference kernel.
2. Run a 512x512 accounting smoke with adaptive diagnostics enabled. Require exact accounting and
   a valid allocation heatmap.
3. Run a same-build 1024x1024 TeapotMaterials 120-frame capture with diagnostics disabled for
   throughput. Compare its average frame time and retired paths with the guarded baseline recorded
   in `AIDocs/24-welford-scheduler-recovery-plan.md`.
4. Repeat in reversed variant order or after a cooldown. Thermal drift is large enough that one
   ordered run is insufficient.
5. Only after throughput is established, run equal-fine-path image comparisons followed by a
   three-seed duration capture.

## Interpretation

Use a uniform-like control before blaming allocation quality:

```text
adaptive scheduler with every pixel assigned one path
versus CSMain with one path per pixel
same resolution, scene, seed, and disabled capture instrumentation
```

If the layered adaptive trace remains substantially slower, the residual cost is trace-kernel
shape/state traffic rather than sparse allocation. If it approaches `CSMain`, the remaining gap is
from inactive/heterogeneous execution and scheduler work.

## Next Optimizations

Apply one item at a time and rerun the measurement order above.

1. Gate metadata clears and global accounting atomics to capture diagnostics if they are not used
   for interactive scheduling.
2. Change bucket remapping from 8x8 groups with 63 idle lanes to a linear one-lane-per-group
   dispatch.
3. Replace classification's lane-zero 64-element reduction with a groupshared parallel reduction.
4. Cache target tiers on classification frames and limit reuse frames to fractional-rate rotation,
   only if equal-path quality is unchanged.
5. Consider compact spatial tiles, never a compact pixel list, only if served-group breadth becomes
   far lower than the current roughly 86 percent.

Do not introduce VCM, hierarchy, material priors, global sorting, CPU scheduling readback, or a
per-pixel indirect work list as part of this throughput work.

## Fast Bootstrap Experiment

`Assets/Editor/RayTracingExperiments/teapotmaterials_layered_welford_fast_bootstrap_1024_120f.json`
compares the default no-bootstrap path against the former twelve-frame low-resolution bootstrap.
The no-bootstrap candidate begins full-resolution adaptive tracing on its first rendered frame,
without seeding coarse history or holding a preview. The retained bootstrap candidate explicitly
sets `enableAdaptiveBootstrap = true` so historic behavior remains available for comparison.
