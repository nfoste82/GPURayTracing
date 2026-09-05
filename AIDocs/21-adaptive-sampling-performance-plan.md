# Adaptive Sampling Performance Plan

## Purpose

Adaptive sampling is currently slower than uniform progressive sampling even before considering
its allocation-quality regressions. The immediate objective is to identify the cost of each GPU
phase before changing the validated scheduling/accounting behavior.

This plan is intentionally separate from the fixed-8x8 quality-policy experiments described in
`17-adaptive-sampling-continuation.md` and `20-dammertz-adaptive-sampling-plan.md`.

## Current Cost Model

The adaptive route performs these steps on a reclassification frame:

```text
clear scheduler -> classify groups -> remap buckets -> guarded full-screen trace
```

The production adaptive route dispatches a regular 4x4 full-screen grid. Each thread reads its
8x8 group's assigned sample count and returns immediately when it is zero. This avoids compact
list construction, indirect dispatch, and list indirection while preserving the scheduler policy.

## Phase Instrumentation Milestone

The first implementation milestone is capture-only phase instrumentation, not a scheduler change.

`GameManager` records these synchronized GPU elapsed times when adaptive capture diagnostics are
enabled:

```text
schedulerMilliseconds: clear/classify/remap
traceMilliseconds:     guarded full-screen CSAdaptiveTrace
resolveMilliseconds:   zero; retained for CSV schema compatibility
```

Each measurement fences the submitted GPU phase with an `AsyncGPUReadback` request on the existing
metadata buffer. This is intentionally expensive and must never run during ordinary interactive
rendering. The scene-capture telemetry CSV includes the three values and summary statistics split
between reclassification and reuse frames.

Use a `512x512` and `1024x1024` `TeapotMaterials` capture with diagnostics enabled. Compare the
phase totals with the existing synchronized per-frame wall-clock time; do not interpret the timing
instrumentation capture as a production frame-rate measurement.

## Optimization Order

1. Establish the scheduler, trace, and resolve costs using the capture-only telemetry.
2. The guarded full-screen kernel is the production path. Each pixel reads its 8x8 group's
   assignment, returns when it is zero, otherwise loops over assigned samples and updates
   HDR/Welford state once. It uses `sampleIndex = oldPixelPathCount + localSample`, retains
   finite-radiance handling and exact accounting, and leaves adaptive-off `CSMain` unchanged.
3. If reclassification is material, split expensive score classification from cheap fractional
rate rotation. The current `adaptiveHighestBucketSampleRate > 1` condition forces reclassification
every frame; cached source scores may be reusable while tier admission rotates.
4. Only after those measurements, replace lane-zero 8x8 classification reduction with a parallel
reduction if it remains significant.

## Required Validation

For every optimization, retain:

```text
requested == assigned == active-pixel paths == retired
deterministic per-pixel sample index sequence
unchanged CSMain behavior when adaptive sampling is disabled
```

Run focused adaptive EditMode tests, then a `512x512` accounting smoke before an equal-retired-path
reference comparison. Treat equal-path quality as the acceptance criterion before assessing
wall-clock improvement.
