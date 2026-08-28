# Initial RIS Direct-Lighting Plan

## Goal

Implement an optional, platform-neutral **initial resampled importance sampling (RIS)** estimator for direct lighting. It should reduce direct-light variance or improve convergence at the same wall-clock budget relative to the current one-light `ImportanceSampled` mode.

This is local RIS only: one reservoir at one eligible shading point during one path evaluation. It is not spatial/temporal ReSTIR DI, needs no history, motion vectors, or hardware ray tracing, and must remain portable across the project's Unity compute targets, including Metal. The HIPRT-Path-Tracer repository is GPL-3.0; use its algorithms as reference only, never copy its source.

## Scope

In scope:

- Primary-bounce opaque direct lighting only.
- Current-frame local weighted reservoir; no persistent GPU resources.
- Existing emissive-light and environment proposal paths where PDFs are available.
- Inspector/capture toggle and candidate count so RIS-off/RIS-on can be benchmarked.
- Correctness tests and equal-work/equal-time convergence measurements.

Not in scope:

- Spatial or temporal ReSTIR, ReSTIR GI, indirect resampling, visibility caching, light presampling, ReGIR, or light trees.
- Adaptive-scheduler work. Adaptive sampling remains a separate, useful feature.
- Glass/water transmission, fog-event, caustic, or later-bounce RIS.
- New reservoir textures, G-buffer outputs, reprojection, or motion vectors.

## Existing Constraints

Read `03-compute-shader-renderer.md`, `07-shader-lighting-and-materials.md`, `10-benchmarking-and-performance.md`, and `11-regression-testing.md` before editing.

The current direct-light flow is in `RayTracingShared.hlsl`:

- `GetLightHittingPoint()` selects finite and environment samples.
- `SelectLightForDraw()` applies `AllLights`, `UniformRandom`, or `ImportanceSampled` selection.
- `SampleSingleLight()` evaluates BRDF, visibility, transparent shadow transmittance, and the contribution.

Keep **one inlined `SampleSingleLight()` call site** inside `GetLightHittingPoint()`. Adding another inlined light/shadow traversal path previously caused extreme Metal compilation times. Candidate generation and reservoir selection must be cheap helpers; only the selected candidate reaches the existing expensive call site.

The renderer already has environment importance sampling, finite-emitter/BSDF MIS, a shared Lambert/GGX model, deterministic capture, and scene-light PDFs. Preserve the current estimator unchanged when RIS is disabled.

## Adaptive Sampling

Adaptive sampling is not being removed or redesigned by this task. It can be more efficient than adaptive-off when its settings suit a scene. The presently observed Dammertz configuration is slower than Welford, but that is a deferred tuning/scheduling issue, not a reason to disable the feature.

Current adaptive startup uses the low-resolution bootstrap implemented in `GameManager` and `SceneSettings`:

- `adaptiveBootstrapResolutionScale` runs normal `CSMain` at `0.125-0.5` resolution.
- `adaptiveBootstrapFrames` controls bootstrap duration.
- `adaptiveGuidanceHistoryFrames` controls approximate fine history from the upscaled bootstrap; `0` keeps fine accumulation unbiased and bootstrap display-only.

Every RIS benchmark must be runnable with adaptive sampling off and on. For adaptive-on comparisons, RIS-off and RIS-on must use the exact same serialized or command-line adaptive preset, including bootstrap scale/frame count/history, priority mode, and scheduler settings. Count bootstrap paths in retired-path accounting and report the complete preset in capture metadata.

## Public Controls

Add the smallest setting set under `LightingManager`, mirrored through `SceneSettings`:

```text
enableInitialDirectLightingRis   bool, default false
initialRisCandidateCount        int, range 1-16, default 4
```

Upload both shader parameters and include them in:

- Final-color accumulation invalidation hash.
- Temporal-reconstruction/denoising state hash if it hashes direct-light settings.
- Inspector, benchmark overlay, benchmark CSV, and capture metadata.
- Generic `RayTracingSceneCapture -rayTracingExperiment` field/property overrides.

Do not add a `LightSamplingStrategy` enum value initially. RIS wraps the existing base proposal, making the baseline clear:

```text
ImportanceSampled + RIS off: one ordinary selected-light estimate
ImportanceSampled + RIS on: N local candidates, one selected visibility evaluation
```

Initially restrict RIS to `ImportanceSampled`. Leave `AllLights` and `UniformRandom` on their existing path until proposal PDFs and cost comparisons are deliberately designed. The current 128-light importance cap is biased when exceeded; benchmark below that cap or against a later unbiased proposal distribution. RIS must not hide this limitation.

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

Before implementation, identify the current complementary emissive-hit/environment MIS logic. Verify a BSDF continuation that hits an emitter remains neither double-counted nor darkened after the RIS direct estimate is added. If that cannot be shown correct without changing the current MIS policy, stop and derive/test the estimator before coding a workaround.

### Current Implementation Blocker (2026-08-28)

The live renderer confirms this condition blocks the proposed first milestone as currently specified:

- `SampleSingleLight()` applies power-heuristic NEE/opaque-BRDF MIS to triangle and environment samples using the ordinary NEE proposal PDF.
- `TracePathWithDirectLight()` applies the complementary power heuristic when an opaque BRDF continuation reaches an emissive triangle or the environment.
- A local RIS reservoir resamples NEE candidates based on unshadowed targets, so its selected-sample distribution is no longer the ordinary NEE proposal used by the current terminal-hit MIS calculation.

Therefore routing a RIS-selected triangle or environment candidate through the existing NEE MIS weighting while retaining the existing terminal-hit weight has no demonstrated balance/power-heuristic derivation. It can double-count or underweight paths discoverable by both techniques. The requested implementation explicitly prohibits using an unproven workaround, so no RIS shader path was added.

Resume only after choosing and validating one of these derivations:

1. Derive the RIS-selected NEE density and use it consistently in both explicit-light and complementary BRDF-hit MIS weights, including the candidate count and reservoir normalization.
2. Establish a separately validated multi-sample-reservoir MIS estimator for triangle and environment proposals.
3. Narrow a first milestone to proposal families with no competing opaque-BRDF technique, only if that restricted estimator remains unbiased for the complete enabled light set and is explicitly approved.

The future derivation needs deterministic CPU/GPU tests that compare high-sample mean radiance against the existing estimator for triangle-light, environment-only, and mixed sphere/mesh/environment scenes before any performance implementation.

## Implementation Order

1. Inspect the live direct-light/MIS helpers and find a minimal candidate record that does not duplicate the expensive call site.
2. Add controls, C# propagation, shader upload, hashes, inspector/overlay/capture metadata. With RIS disabled, confirm unchanged output.
3. Add local reservoir/candidate helpers with no shadow traversal and no persistent allocation.
4. Route only the selected candidate to the current `SampleSingleLight()` call site. Keep baseline behavior for ineligible paths.
5. Add deterministic CPU/GPU reservoir tests: zero weight, one candidate, selected-first/last, normalization, non-finite rejection, and chromatic contribution preserved while selection target is luminance.
6. Add a high-sample colored many-light image fixture: RIS-off/on must agree in mean radiance within reviewed tolerance.
7. Add low-sample variance/convergence fixtures, Metal precompile validation, and capture experiments.
8. Benchmark candidate counts `1`, `2`, `4`, and `8`; choose no default until measurements justify it.

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

- RIS-off preserves current deterministic output and performance within measurement noise.
- High-sample RIS-on/off agree in mean radiance, with no systematic energy or color shift.
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

Only after local RIS is correct and beneficial:

1. Add BSDF-hit emissive/environment candidates with a separately validated RIS/MIS derivation.
2. Replace the capped global importance scan with a portable unbiased light hierarchy/alias distribution.
3. Add light presampling only if candidate selection is measured as a bottleneck.
4. Consider temporal ReSTIR for opaque primary hits after motion/history validation.
5. Consider spatial reuse only after temporal/local results establish a need.

## Compact Future-Session Prompt

```text
Read AGENTS.md and AIDocs/00-index.md. Implement only AIDocs/23-initial-ris-direct-lighting-plan.md: local initial RIS for primary opaque direct lighting. Do not implement spatial/temporal ReSTIR, ReSTIR GI, light presampling, light trees, or adaptive-scheduler changes. Preserve the current low-resolution adaptive bootstrap and benchmark RIS with adaptive sampling both off and on.

Add enableInitialDirectLightingRis (default false) and initialRisCandidateCount (1-16, default 4) under LightingManager/SceneSettings, including shader upload, accumulation/history invalidation, inspector, benchmark metadata, and generic experiment overrides. RIS-off must preserve output.

For eligible bounce-0 opaque ImportanceSampled hits, draw N candidates from the existing proposal, choose one using a local weighted reservoir with unshadowed target luminance/proposalPdf, and trace the existing production shadow/transmittance path only for the selected candidate. Keep one inlined SampleSingleLight call site to avoid Metal compile explosion. Preserve or rigorously rederive current BSDF-hit MIS; stop if it cannot be proven non-double-counted.

Add deterministic reservoir tests, a colored-many-light high-sample energy fixture, Metal precompile validation, and checked-in RayTracingSceneCapture experiment manifests for RIS off/on at 1/2/4/8 candidates. Measure equal-work and equal-time convergence with adaptive off and one unchanged good adaptive preset, including bootstrap settings. Use apply_patch, preserve unrelated work, and update the plan with results.
```
