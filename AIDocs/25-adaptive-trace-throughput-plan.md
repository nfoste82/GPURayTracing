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
adaptiveNormalizePriorityByLuminance = 1
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
