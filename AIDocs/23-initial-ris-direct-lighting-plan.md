# Initial RIS Direct-Lighting Design Record

## Goal

This document records the platform-neutral **initial resampled importance sampling (RIS)** estimator for direct lighting. RIS is now part of the standard renderer path rather than an optional experiment. It reduces direct-light variance or improves convergence at the same wall-clock budget relative to the one-light `ImportanceSampled` mode.

The original milestone was local RIS only: one reservoir at one eligible shading point during one path evaluation. The renderer now also supports temporal RIS reuse in the supported temporal path. It remains portable across the project's Unity compute targets, including Metal. The HIPRT-Path-Tracer repository is GPL-3.0; use its algorithms as reference only, never copy its source.

## Current Status

Local primary direct-light RIS and temporal RIS are implemented and enabled by default. The candidate count is configurable, participates in accumulation/history invalidation and benchmark metadata, and the ordinary direct-light estimator remains the fallback for unsupported materials/events and later bounces. The implementation retains the single production visibility call site required for acceptable Metal compile times.

## Scope

In scope:

- Primary-bounce opaque direct lighting only.
- Current-frame local weighted reservoir; no persistent GPU resources.
- Existing emissive-light and environment proposal paths where PDFs are available.
- Inspector/capture candidate count and temporal-reuse state so the standard path can be benchmarked against fallback/reference variants.
- Correctness tests and equal-work/equal-time convergence measurements.

Not in scope:

- Spatial reuse, ReSTIR GI, indirect resampling, visibility caching, light presampling, ReGIR, or light trees.
- Adaptive-scheduler work. Adaptive sampling remains a separate, useful feature.
- Glass/water transmission, fog-event, caustic, or later-bounce RIS.
- New reservoir textures, G-buffer outputs, or motion vectors beyond the existing temporal path contract.

## Existing Constraints

Read `03-compute-shader-renderer.md`, `07-shader-lighting-and-materials.md`, `10-benchmarking-and-performance.md`, and `11-regression-testing.md` before editing.

The current direct-light flow is in `RayTracingShared.hlsl`:

- `GetLightHittingPoint()` selects finite and environment samples.
- `SelectLightForDraw()` applies `AllLights`, `UniformRandom`, or `ImportanceSampled` selection.
- `SampleSingleLight()` evaluates BRDF, visibility, transparent shadow transmittance, and the contribution.

Keep **one inlined `SampleSingleLight()` call site** inside `GetLightHittingPoint()`. Adding another inlined light/shadow traversal path previously caused extreme Metal compilation times. Candidate generation and reservoir selection must be cheap helpers; only the selected candidate reaches the existing expensive call site.

The renderer already has environment importance sampling, finite-emitter/BSDF MIS, a shared Lambert/GGX model, deterministic capture, and scene-light PDFs. Preserve the ordinary estimator as a fallback/reference when RIS is bypassed.

## Adaptive Sampling

Adaptive sampling is not being removed or redesigned by this task. It can be more efficient than adaptive-off when its settings suit a scene. The presently observed Dammertz configuration is slower than Welford, but that is a deferred tuning/scheduling issue, not a reason to disable the feature.

Current adaptive startup uses the low-resolution bootstrap implemented in `GameManager` and `SceneSettings`:

- `adaptiveBootstrapResolutionScale` runs normal `CSMain` at `0.125-0.5` resolution.
- `adaptiveBootstrapFrames` controls bootstrap duration.
- `adaptiveGuidanceHistoryFrames` controls approximate fine history from the upscaled bootstrap; `0` keeps fine accumulation unbiased and bootstrap display-only.

Every RIS benchmark must be runnable with adaptive sampling off and on. For adaptive-on comparisons, fallback and RIS variants must use the exact same serialized or command-line adaptive preset, including bootstrap scale/frame count/history, priority mode, and scheduler settings. Count bootstrap paths in retired-path accounting and report the complete preset in capture metadata.

## Public Controls

Add the smallest setting set under `LightingManager`, mirrored through `SceneSettings`:

```text
initialRisCandidateCount        int, range 1-16, default 4
```

Upload both shader parameters and include them in:

- Final-color accumulation invalidation hash.
- Temporal-reconstruction/denoising state hash if it hashes direct-light settings.
- Inspector, benchmark overlay, benchmark CSV, and capture metadata.
- Generic `RayTracingSceneCapture -rayTracingExperiment` field/property overrides.

RIS wraps the existing base proposal rather than adding a `LightSamplingStrategy` enum value:

```text
ImportanceSampled + RIS off: one ordinary selected-light estimate
ImportanceSampled + RIS on: N local candidates, one selected visibility evaluation
```

RIS remains attached to `ImportanceSampled`. `AllLights` and `UniformRandom` retain their existing paths because their proposal PDFs and cost tradeoffs are distinct. The current 128-light importance cap is biased when exceeded; benchmark below that cap or against a later unbiased proposal distribution. RIS does not hide this limitation.

## Estimator

### Eligibility

Use RIS only when all conditions hold:

- Bounce is zero.
- The hit is opaque and uses the existing shared opaque BRDF path.
- The surface/path throughput is valid and nonzero.
- The current base light strategy is `ImportanceSampled`.

Use the current direct estimator unchanged for glass, water, fog events, unsupported/delta cases, later bounces, or invalid candidates. Enabling RIS must not remove lighting paths that the baseline supports.

### Candidate Generation

Generate `N = initialRisCandidateCount` candidates from the existing importance-sampled finite-light/environment proposal. Each candidate must retain enough information to evaluate it later:

- Light index/type.
- Sampled point or direction.
- Full proposal PDF.
- Any sampled shape/triangle data needed by the existing direct-light path.

Do not add BSDF-hit-emitter candidates in the first milestone. Existing complementary BSDF MIS should remain correct and unchanged unless a derivation and focused test prove otherwise. Add BSDF candidates only as a separate future change.

### Local Reservoir

For candidate `y` at receiver `x`, calculate an unshadowed scalar target and weight:

```text
target(y) = luminance(unshadowed direct-light RGB contribution at x from y)
weight(y) = target(y) / proposalPdf(y)
```

The unshadowed RGB contribution must use the same BRDF, light radiance, finite-light geometry conversion, and environment convention as the baseline. `proposalPdf` must include all selection decisions: global light selection, mesh-light triangle choice, shape-point sample, and environment-choice/distribution probabilities.

Update the reservoir with weighted replacement:

```text
candidateCount += 1
weightSum += weight
replace selected candidate with probability weight / weightSum
```

After candidates are processed, route only the selected candidate through the existing `SampleSingleLight()` call site. Multiply its unshadowed RGB value and production shadow transmittance by:

```text
weightSum / (candidateCount * selectedTarget)
```

Return zero for invalid, non-finite, zero-PDF, back-facing, or zero-target candidates. Guard all reservoir denominators/state against NaN and Inf. Use scalar luminance only for selection and normalization; preserve RGB for the final result.

This is ordinary same-receiver local RIS. It needs no temporal/spatial correction factor because all candidates are drawn from known proposals at the same shading point.

### Visibility And MIS

Trace production visibility only for the selected candidate. Do not skip final visibility, reuse candidate visibility, or replace transparent-shadow transmittance with boolean occlusion.

The complementary emissive-hit/environment MIS logic must remain consistent with the reservoir-selected proposal. A BSDF continuation that hits an emitter must be neither double-counted nor darkened by the RIS direct estimate.

### Historical Implementation Blocker (2026-08-28)

The live renderer confirms this condition blocks the proposed first milestone as currently specified:

- `SampleSingleLight()` applies power-heuristic NEE/opaque-BRDF MIS to triangle and environment samples using the ordinary NEE proposal PDF.
- `TracePathWithDirectLight()` applies the complementary power heuristic when an opaque BRDF continuation reaches an emissive triangle or the environment.
- A local RIS reservoir resamples NEE candidates based on unshadowed targets, so its selected-sample distribution is no longer the ordinary NEE proposal used by the current terminal-hit MIS calculation.

This was the blocker before the reservoir-aware estimator was implemented. The current renderer uses the validated reservoir-aware policy rather than treating a selected RIS candidate as an ordinary NEE sample.

The historical resolution was to choose and validate one of these derivation paths:

1. Derive the RIS-selected NEE density and use it consistently in both explicit-light and complementary BRDF-hit MIS weights, including the candidate count and reservoir normalization.
2. Establish a separately validated multi-sample-reservoir MIS estimator for triangle and environment proposals.
3. Narrow a first milestone to proposal families with no competing opaque-BRDF technique, only if that restricted estimator remains unbiased for the complete enabled light set and is explicitly approved.

The future derivation needs deterministic CPU/GPU tests that compare high-sample mean radiance against the existing estimator for triangle-light, environment-only, and mixed sphere/mesh/environment scenes before any performance implementation.

## Implementation Order

1. Inspect the live direct-light/MIS helpers and find a minimal candidate record that does not duplicate the expensive call site.
2. Preserve the controls, C# propagation, shader upload, hashes, inspector/overlay/capture metadata.
3. Preserve local reservoir/candidate helpers with no shadow traversal and no persistent allocation.
4. Route only the selected candidate to the current `SampleSingleLight()` call site and keep fallback behavior for ineligible paths.
5. Maintain deterministic CPU/GPU reservoir tests: zero weight, one candidate, selected-first/last, normalization, non-finite rejection, and chromatic contribution preserved while selection target is luminance.
6. Maintain a high-sample colored many-light image fixture: fallback and RIS variants must agree in mean radiance within reviewed tolerance.
7. Add low-sample variance/convergence fixtures, Metal precompile validation, and capture experiments.
8. Benchmark candidate counts `1`, `2`, `4`, and `8` as tuning variants around the default configuration.

## Benchmark Plan

Use `RayTracingSceneCapture -rayTracingExperiment` with a checked-in manifest. Create named variants differing only in:

```text
Lighting.EnableInitialDirectLightingRis = false/true
Lighting.InitialRisCandidateCount = 1, 2, 4, 8
```

Use `Benchmark_ManyLights` plus an existing or new static fixture containing varied-size, varied-color sphere and mesh lights. Keep resolution, bounces, seed, base strategy, shadows, environment, caustics, fog, denoising, and render scale fixed per comparison.

Run both:

1. Equal retired-path/frame-budget capture: measures estimator quality independent of candidate-generation cost.
2. Equal wall-clock-duration capture: measures interactive convergence, frame time, and retired paths.

Repeat both with:

- Adaptive sampling off.
- Adaptive sampling on using an unchanged known-good scene preset, including its low-resolution bootstrap settings.

Required acceptance evidence:

- The ordinary fallback preserves its deterministic output and performance within measurement noise.
- High-sample fallback/RIS variants agree in mean radiance, with no systematic energy or color shift.
- At least one many-light fixture has lower error at equal work or equal time for a practical count.
- RIS does not materially regress ordinary low-light-count scenes.
- Instrumentation/code review shows one selected shadow evaluation, not N shadow rays per RIS candidate.
- Metal shader compilation remains acceptable.

## Diagnostics

Add opt-in aggregate counters or isolated debug output for:

- Eligible primary hits and invalid candidates.
- Mean candidate count per eligible hit.
- Selected finite/environment/directional type histogram.
- Selected shadow queries per eligible hit.
- Non-finite reservoir `weightSum`, selected target, or final normalization.

Avoid permanent per-pixel debug resources in the final shader unless a measured debugging need justifies them.

## Deferred Follow-Ups

Remaining follow-ups after the now-standard local/temporal RIS path:

1. Add BSDF-hit emissive/environment candidates with a separately validated RIS/MIS derivation.
2. Replace the capped global importance scan with a portable unbiased light hierarchy/alias distribution.
3. Add light presampling only if candidate selection is measured as a bottleneck.
4. Extend temporal RIS coverage to additional eligible path/material classes after motion/history validation.
5. Consider spatial reuse only after temporal/local results establish a need.

## Compact Future-Session Prompt

```text
Read AGENTS.md and AIDocs/00-index.md. Continue only the remaining follow-up work in AIDocs/23-initial-ris-direct-lighting-plan.md. Local primary direct-light RIS and supported temporal RIS are already standard/default paths. Do not replace them with spatial ReSTIR GI, light presampling, light trees, or adaptive-scheduler changes without a separate design and validation plan.

Preserve the existing initialRisCandidateCount and temporalRisEnabled controls, their default behavior, shader upload, accumulation/history invalidation, inspector, benchmark metadata, and generic experiment overrides.

For eligible bounce-0 opaque ImportanceSampled hits, draw N candidates from the existing proposal, choose one using a local weighted reservoir with unshadowed target luminance/proposalPdf, and trace the existing production shadow/transmittance path only for the selected candidate. Keep one inlined SampleSingleLight call site to avoid Metal compile explosion. Preserve or rigorously rederive current BSDF-hit MIS; stop if it cannot be proven non-double-counted.

Maintain deterministic reservoir tests, colored-many-light energy fixtures, Metal precompile validation, temporal-history coverage, and checked-in RayTracingSceneCapture comparisons for candidate counts 1/2/4/8. Measure equal-work and equal-time convergence with adaptive off and on using unchanged presets. Use apply_patch, preserve unrelated work, and update this record with measured changes.
```
