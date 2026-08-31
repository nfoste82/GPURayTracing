# Initial RIS Direct-Lighting Design Record

## Goal

This document records the platform-neutral **initial resampled importance sampling (RIS)** estimator for direct lighting. RIS is now part of the standard renderer path rather than an optional experiment. It reduces direct-light variance or improves convergence at the same wall-clock budget relative to the one-light `ImportanceSampled` mode.

The original milestone was local RIS only: one reservoir at one eligible shading point during one path evaluation. The renderer now also supports temporal RIS reuse in the supported temporal path. It remains portable across the project's Unity compute targets, including Metal. The HIPRT-Path-Tracer repository is GPL-3.0; use its algorithms as reference only, never copy its source.

## Current Status

Local primary direct-light RIS is enabled by default. Temporal ReSTIR-DI now maintains a separate camera-reprojected ping-pong reservoir history for static, primary opaque, non-reactive receivers when rendering one path sample per pixel. It is deliberately disabled for dynamic scenes, fog, animated water, transmission, highly smooth receivers, adaptive tracing, and multi-sample-per-pixel dispatches. History is limited to one represented prior candidate in addition to the current local reservoir because longer visibility-unaware histories produced persistent clumps in finite area-light penumbrae. The candidate count participates in accumulation/history invalidation and benchmark metadata, and the ordinary direct-light estimator remains the fallback for unsupported materials/events and later bounces. The implementation retains the single production visibility call site required for acceptable Metal compile times.

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

`Benchmark_TemporalRisStress` is the focused static temporal fixture. It uses twelve small,
alternating-color finite lights over a diffuse floor with opaque pillars that create direct-light
visibility boundaries. It disables environment lighting, directional lighting, denoising, motion,
and transmission so `temporal_ris_static_direct_light_fixed_work.json` can compare local RIS and
temporal RIS at the same 200 accumulated samples. This is the acceptance fixture for temporal
reuse; CornellBox remains a general stability check rather than a temporal-RIS quality benchmark.

`Benchmark_TemporalRisStableDirectLight` separates selection coherence from visibility failure. It
uses twenty varied finite lights over a large diffuse receiver with no environment, directional
light, motion, transmission, denoising, or adaptive sampling. A single screen-right blocker creates
a narrow controlled penumbra strip; the rest of the visible receiver remains unoccluded. This is the
positive-control fixture: temporal reuse must show an early-frame benefit in its open region before
visibility-aware changes are justified. It is not a replacement for `TemporalRisStress`, which
remains the rejection/stability fixture.

`temporal_ris_candidate_split_sweep_fixed_work.json` evaluates local candidate count independently
from the temporal retained-history `M` cap. Each temporal capture writes
`temporal_ris_diagnostics.json` beside its timing report. Inspect history acceptance/rejection,
merge/selection rates, and mean retained/effective `M` before interpreting quality changes. The
history cap defaults to one because larger visibility-unaware histories can create persistent
finite-area-light penumbra clumps; it is a benchmark tuning control, not evidence that a larger
value is safe by itself.

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

## Temporal RIS Net-Benefit Roadmap

Temporal RIS is enabled in existing production scene settings, but that older path does not consume
temporal reservoir data. Do not change that setting as part of this work. The implemented reservoir
reuse path remains experimental until it passes the acceptance gates below.

The candidate-split capture `temporal_ris_candidate_split_sweep_fixed_work_2` found local RIS
superior to every tested temporal split on `Benchmark_TemporalRisStress`. The closest configuration,
four fresh candidates plus one represented history candidate, was still 2.1 percent higher RGB RMSE
and 3.2 percent slower than local four-candidate RIS. History acceptance was 99.17 percent, so this
is not primarily a reprojection-availability failure. Quality worsened monotonically as history
selection increased, which makes represented-history correlation and visibility discontinuities the
leading hypotheses.

The independent `temporal_ris_one_frame_trials` run confirms a limited instantaneous benefit under
the current four-local-plus-one-history policy: at warm-up lengths 1, 2, 4, and 8, temporal RMSE was
2.48 to 2.66 percent lower than local RIS across 32 seed-paired trials. It was also 8.53 to 13.56
percent slower per measured frame. In contrast, the 200-frame static progressive capture lost from
frame 2 onward and finished 2.71 percent higher RGB RMSE. The current history path is therefore
active and useful as one additional selection observation, but it does not yet improve long static
accumulation. Its 98.68 percent history acceptance, 97.76 percent merge rate, and 21.33 percent
history-selection rate rule out simple reprojection unavailability as the main explanation.

The stable-scene positive control reaches the same conclusion without image-wide visibility
discontinuities. In `temporal_ris_stable_direct_light_fixed_work`, temporal RIS finished at RGB RMSE
0.00765151 versus local RIS at 0.00733272: a 4.35-percent regression after 200 accumulated frames.
It already loses at frame 2 (0.0678062 versus 0.0664890) and remains behind at frames 4, 8, 16, 32,
64, and 128. Its apparent 0.50-percent lower average render time (120.45 ms versus 121.06 ms) is too
small to treat as a benefit. The diagnostic counters show normal reuse: 98.85-percent history
acceptance, 98.15-percent merge rate, 20.68-percent history-selection rate, and mean effective M
of 4.951.

The independent `temporal_ris_stable_direct_light_one_frame_trials` positive control does show the
intended reset-frame effect. Across 32 paired seeds, temporal RMSE was lower by 2.74, 2.82, 2.86,
and 2.83 percent at warm-ups 1, 2, 4, and 8 respectively. Measured-frame time was higher by 8.52,
12.17, 9.15, and 12.25 percent. Temporal mean linear luminance was consistently about 0.21 to 0.23
percent higher, so a temporal-on high-sample mean comparison remains required. The reference
difference images show the controlled blocker/shadow region, but the progressive regression is also
present across the broad open receiver. Visibility boundaries can amplify the issue, but they are
not its sole cause.

Treat the following as ordered gates. Do not tune a later stage to compensate for a failed earlier
one.

### 2. Correct The Benchmark Baseline

1. Temporal reservoir history must be invalidated whenever progressive accumulation resets. This
   includes capture setup, a new capture variant, renderer-state invalidation, render-size changes,
   and any other operation that calls `GameManager.ResetFrameAccumulation()`.
2. Each capture variant starts with empty temporal history. The capture's shader warm-up may compile
   shaders, but cannot seed measured history after the following accumulation reset.
3. Generate an explicitly temporal-off trusted reference for `TemporalRisStress`, preferably using
   `AllLights` or a high-sample local RIS configuration. Reference metadata must record RIS state,
   candidate count, history cap, light strategy, seed, relevant renderer settings, scene/settings
   hash, source revision, and shader revision.
4. Re-run the candidate split in normal and reverse variant orders. Results must not depend on order.

### 3. Prove Mean Correctness

Status: reservoir normalization is covered by the production GPU regression probe. It verifies local
normalization, capped temporal target-ratio mass, local/history selected-target normalization, and
zero/invalid history rejection through the same helpers used by `CSMain`. Raw-HDR production-image
fixtures now compare ordinary NEE plus complementary continuation-hit contributions with local RIS
counts 1, 2, 4, and 8 for mesh-triangle-only, environment-only, and mixed sphere/triangle/environment
lighting. Triangle and environment fixtures use 1,024 samples per pixel with a two-percent per-RGB
channel tolerance; the noisier mixed fixture uses 16,384 samples per pixel with a 3.5-percent
tolerance. The mixed fixture exposed that continuation-hit MIS omitted the local RIS proposal branch
probability; `TracePath()` now carries the preceding initial-RIS state and applies that branch PDF to
triangle-light and sky-miss competing PDFs. The focused three-fixture run passes. Temporal-on
production-image comparison remains required before this gate is complete.

1. Add deterministic CPU/GPU checks for triangle-only, environment-only, and mixed
   sphere/triangle/environment lighting.
2. Compare explicit RIS NEE plus complementary BRDF-hit-emitter/environment contributions with a
   trusted high-sample estimator for local counts 1, 2, 4, and 8, with temporal reuse disabled and
   enabled.
3. Derive or repair the reservoir-aware NEE/BRDF MIS pairing before temporal tuning. The current
   explicit RIS path and continuation-hit MIS must be proven complementary; source-shape assertions
   are insufficient.
4. Add synthetic reservoir tests for stored selected target, target-ratio history mass, capped `M`,
   forced local/history selection, and final normalization.

### 4. Measure The Intended Benefit

The checked-in `temporal_ris_one_frame_trials.json` runs this gate at 1024x1024 with 32
deterministic seed-paired trials for warm-up lengths 1, 2, 4, and 8. For each trial it starts
from empty temporal history, accumulates the configured warm-up frames, and measures exactly one
non-accumulated frame against the trusted temporal-off reference. It writes per-trial RMSE, mean
linear luminance, and measured render time to `temporal_ris_one_frame_trials.csv`, with sample
means and unbiased variances in `temporal_ris_one_frame_summary.csv`.

1. Run independent one-frame trials: empty history, a specified warm-up length, one measured
   non-accumulated frame, and many fixed seeds. Report mean, variance, RMSE, and time.
2. Separately run independent progressive sequences and report error at 1, 2, 4, 8, 16, 32, 64,
   128, and 200 frames.
3. Record lag autocorrelation and estimate effective sample size. A temporal estimator may improve
   instantaneous low-SPP images while losing to independent local RIS in a long static average.
4. If only the former wins, limit temporal reuse to reset/interactive presentation rather than the
   static progressive path.
5. Run `temporal_ris_stable_direct_light_fixed_work.json` and
   `temporal_ris_stable_direct_light_one_frame_trials.json` using the
   `TemporalRisStableDirectLight` reference. Compare the
   open receiver and controlled penumbra strip separately with reference difference heatmaps.
   If temporal reuse does not win in the open region, audit stored proposal density, target-ratio
   mass, and final normalization before adding visibility-aware heuristics. If it wins only in the
   open region, prioritize spatially localized visibility/selection diagnostics for the penumbra.

Status: complete. The open-region positive control did not improve progressive convergence despite
its reset-frame gain. Before visibility-aware heuristics, audit whether recursively stored history
creates correlation that is not represented by its nominal capped M, then test previous-frame-only
history and normalized-local-reservoir-as-one-observation variants.

### 5. Limit History Persistence

1. Test a previous-frame-only history variant against recursively accumulated history.
2. Test storing a normalized local reservoir as one temporal observation rather than granting its
   local candidate count persistent temporal confidence.
3. Track reservoir age/ancestry and test short maximum ages of 1, 2, 4, and 8 frames.
4. Tune to a bounded history-selection rate, not nominal `M`. Begin below the current harmful
   21-percent rate of the four-local-plus-one-history configuration.
5. Retain only variants that improve one-frame quality without losing equal-time progressive
   convergence through excessive correlation.

### 6. Add Visibility Awareness Conservatively

1. Store the already-computed final visibility/transmittance result with the reservoir for
   diagnostics. Never assume it remains current visibility.
2. Measure history selection, visibility changes, age, and error around direct-light penumbrae.
3. Test rejecting samples that were fully occluded when stored, with high-sample mean validation.
4. Test conservative confidence reduction for aged samples and near-discontinuity reprojections.
5. Only if those fail, prototype one current-receiver visibility test for history in a separate
   compact path/kernel. Do not duplicate the inlined `SampleSingleLight()` shadow traversal in
   `CSMain`.
6. A two-reservoir defensive/pairwise correction is the maximum follow-up scope. Derive it
   independently and preserve the current fresh reservoir as the canonical candidate.

### 7. Tighten Reprojection Validation

1. Compare exact same-pixel reuse, current matrix reprojection, and stricter world-space
   plane-distance/normal validation.
2. Add capture-only maps for source pixel, accepted/rejected history, receiver displacement,
   history selection, visibility mismatch, and reservoir age.
3. Consider a nearby-pixel search only after exact reprojection validation, because it can cross
   direct-light visibility boundaries.
4. Use `TemporalRisStableDirectLight` to distinguish a global history-selection problem from a
   penumbra-local visibility problem before changing temporal confidence or history persistence.

### 8. Optimize Only A Winning Estimator

1. Use repeated equal-time captures with randomized order and cooldown.
2. Report completed frames, fresh candidates, temporal selections, and final error, not only
   nominal effective `M`.
3. Profile feature generation, structured-buffer traffic, reprojection, and diagnostics separately.
4. Disable capture diagnostics for final timing, retain one inlined production
   `SampleSingleLight()` call site, and precompile/measure the Metal variant.

### 9. Validate Across Scene Classes

Require success on the temporal stress scene, an unoccluded many-light scene, a single area-light
penumbra scene, environment-only, mixed triangle/environment, camera pan, disocclusion, CornellBox,
and a low-light-count overhead check. Dynamic geometry, fog, animated water, transmission, adaptive
sampling, and highly smooth receivers remain out of scope until static opaque reuse succeeds.

### 10. Acceptance Evidence

A candidate is acceptable only when it has lower equal-time linear-HDR error in its declared target
mode, agrees in high-sample mean radiance and color with the trusted reference, creates no reviewed
penumbra/disocclusion artifacts, and retains acceptable Metal compile/runtime cost. The ordinary
local RIS fallback must remain unchanged.

### 11. Reference Implementation Boundaries

HIPRT-Path-Tracer is a useful algorithmic reference for concepts such as canonical fresh reservoirs,
small history confidence caps, temporal validation, and visibility-aware correction. It is GPL-3.0:
do not copy its source, comments, or structure into this project. Independently derive and implement
any adopted algorithm.

### 12. Recommended Execution Sequence

1. Complete the baseline/isolation work in section 2.
2. Validate RIS/BRDF MIS mean correctness.
3. Separate independent-frame variance from progressive convergence.
4. Add age, autocorrelation, contribution, and visibility diagnostics.
5. Test one-frame-only and collapsed-confidence temporal policies.
6. Test age-decayed, low-selection-rate policies.
7. Add stored-visibility diagnostics and reject previously occluded history experimentally.
8. Escalate to a compact current-history visibility query only if justified.
9. Consider a derived two-reservoir defensive correction only if simpler policies fail.
10. Optimize only the first estimator that passes quality gates, then complete the scene matrix.

## Compact Future-Session Prompt

```text
Read AGENTS.md and AIDocs/00-index.md. Continue only the remaining follow-up work in AIDocs/23-initial-ris-direct-lighting-plan.md. Local primary direct-light RIS and supported temporal RIS are already standard/default paths. Do not replace them with spatial ReSTIR GI, light presampling, light trees, or adaptive-scheduler changes without a separate design and validation plan.

Preserve the existing initialRisCandidateCount and temporalRisEnabled controls, their default behavior, shader upload, accumulation/history invalidation, inspector, benchmark metadata, and generic experiment overrides.

For eligible bounce-0 opaque ImportanceSampled hits, draw N candidates from the existing proposal, choose one using a local weighted reservoir with unshadowed target luminance/proposalPdf, and trace the existing production shadow/transmittance path only for the selected candidate. Keep one inlined SampleSingleLight call site to avoid Metal compile explosion. Preserve or rigorously rederive current BSDF-hit MIS; stop if it cannot be proven non-double-counted.

Maintain deterministic reservoir tests, colored-many-light energy fixtures, Metal precompile validation, temporal-history coverage, and checked-in RayTracingSceneCapture comparisons for candidate counts 1/2/4/8. Measure equal-work and equal-time convergence with adaptive off and on using unchanged presets. Use apply_patch, preserve unrelated work, and update this record with measured changes.
```
