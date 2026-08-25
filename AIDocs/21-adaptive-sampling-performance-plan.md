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
clear scheduler -> classify groups -> remap buckets -> compact pixels and expand roots
-> indirect root trace -> indirect pixel resolve
```

On reused schedules it still performs the indirect root trace and pixel resolve. In contrast,
uniform `CSMain` traces and accumulates each pixel's samples in one kernel invocation. Adaptive
currently adds image-sized work-list/root-list traffic, root-radiance writes, a second radiance
read, and an additional indirect dispatch.

## Phase Instrumentation Milestone

The first implementation milestone is capture-only phase instrumentation, not a scheduler change.

`GameManager` records these synchronized GPU elapsed times when adaptive capture diagnostics are
enabled:

```text
schedulerMilliseconds: clear/classify/remap/compact/dispatch-argument construction
traceMilliseconds:     indirect CSAdaptiveTraceRoot
resolveMilliseconds:   indirect CSAdaptiveResolveRoot
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
2. If trace plus resolve dominates, implement a compact-pixel fused kernel:

```text
one indirect thread per active pixel
-> read its compact work item
-> trace its assigned samples in a local loop
-> update HDR accumulation, Welford RGB M2, and alternating state once
```

This removes the root work list, root offsets, root radiance buffer, root expansion loop, and
separate resolve dispatch from the hot route. Preserve `sampleIndex = oldPixelPathCount +
localSample`, exact accounting, finite-radiance handling, and `CSMain` as the uniform baseline.

3. If reclassification is material, split expensive score classification from cheap fractional
rate rotation. The current `adaptiveHighestBucketSampleRate > 1` condition forces reclassification
every frame; cached source scores may be reusable while tier admission rotates.
4. If compaction is material while most pixels are active, compare the compact route with a
full-screen guarded per-pixel dispatch. Do not replace the compact route without a same-scene,
same-path benchmark.
5. Only after those measurements, replace lane-zero 8x8 classification reduction with a parallel
reduction if it remains significant.

## Required Validation

For every optimization, retain:

```text
requested == assigned == compact work-item paths == root paths == retired
no zero-path compact work item
deterministic per-pixel sample index sequence
unchanged CSMain behavior when adaptive sampling is disabled
```

Run focused adaptive EditMode tests, then a `512x512` accounting smoke before an equal-retired-path
reference comparison. Treat equal-path quality as the acceptance criterion before assessing
wall-clock improvement.
