# Initial RIS Direct-Lighting Design Record

## Goal

This document records the platform-neutral **initial resampled importance sampling (RIS)** estimator for direct lighting. Local RIS is part of the standard renderer path. Temporal and spatial reuse remain experimental, incomplete paths and are disabled by default because they are currently slower than local RIS and have not met the renderer's quality/performance promotion criteria.

The original milestone was local RIS only: one reservoir at one eligible shading point during one path evaluation. The renderer now also supports temporal RIS reuse in the supported temporal path. It remains portable across the project's Unity compute targets, including Metal. The HIPRT-Path-Tracer repository is GPL-3.0; use its algorithms as reference only, never copy its source.

## Current Status

Local primary direct-light RIS is enabled by default. Temporal and spatial reuse are experimental opt-in paths. Temporal ReSTIR-DI maintains a separate camera-reprojected ping-pong reservoir history for static, primary opaque, non-reactive receivers when rendering one path sample per pixel; spatial reuse reads neighboring reservoirs and currently takes precedence over temporal reuse rather than combining both paths. Both paths are incomplete and slower than local RIS in current measurements, so they are disabled by default. They remain deliberately restricted for dynamic scenes, fog, animated water, transmission, highly smooth receivers, adaptive tracing, and multi-sample-per-pixel dispatches. The ordinary direct-light estimator remains the fallback for unsupported materials/events and later bounces.

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

## Experimental Spatial RIS

An experimental, default-off spatial prototype now reads cardinal-neighbor fresh local reservoirs
from the preceding completed frame using the temporal ping-pong resources. It is not same-frame
spatial RIS: a correct same-frame implementation requires a local-reservoir prepass followed by a
separate spatial resolve. The prototype validates receiver identity, relative depth (5 percent), and
normal agreement (dot at least 0.9), re-evaluates each candidate at the current receiver, and
performs production visibility only for the final selected candidate. It stores only fresh local
reservoirs to prevent recursive spatial ancestry.

On `TemporalRisStableDirectLight`, 200-frame fixed-work local-4 versus spatial-4 reduced RGB RMSE
from 0.00734392 to 0.00715252 (2.61 percent), but cost 136.21 ms/frame versus 131.58 ms/frame
(3.52 percent). The 45-second equal-time candidate sweep rejects reducing fresh candidates to make
up this overhead: local-1 reached RGB RMSE 0.00810266 in 244 frames while spatial-1 reached
0.02509391 in 223 frames; local-2 reached 0.00651741 in 305 frames while spatial-2 reached
0.00970189 in 233 frames. This staged-neighbor prototype therefore has no viable equal-time
configuration in the tested range. It was removed rather than tuned further.

The replacement is an experimental same-frame design. `RayTracingSpatialRisPrepass.compute` traces
the stochastic primary ray, builds the ordinary fresh local reservoir through the shared
`GetLightHittingPoint()` candidate path, and writes the selected reservoir, post-candidate RNG
state, and matching primary receiver features. `CSMain` reads only those immutable current-frame
records, validates same-object/depth/normal neighbors, re-evaluates each source sample at the current
receiver, and routes only the final selection to the existing `SampleSingleLight()` call. Normal
denoiser features are refreshed after final color, so the prepass feature record matches its stochastic
receiver exactly. Spatial mode disables temporal history merging rather than combining experimental
reuse domains.

The replacement has Metal precompile coverage but no runtime or convergence acceptance evidence.
Do not promote it, update defaults, or claim a quality/performance benefit until local-versus-spatial
fixed-work, equal-time, and high-sample mean comparisons pass.

The CornellBox four-way fixed-work capture confirmed that enabling both temporal and spatial flags
produces the same pixels as spatial-only by design: spatial reuse currently takes precedence and the
renderer does not merge temporal history in spatial mode. The initial 200-frame result also rejected
both reuse paths for this scene: local RIS RGB RMSE was 0.00735842, temporal RIS was 0.00757391, and
spatial RIS was 0.00858836. Spatial diagnostics were all zero because the spatial resolve had not
instrumented the shared reuse counters; this has been corrected. The same audit found that reused
triangle and environment targets omitted the local RIS power-heuristic MIS factor during
current-receiver re-evaluation. Reused candidates now apply the same target convention as fresh local
candidates. Re-run the CornellBox and focused triangle/environment mean checks before interpreting
the earlier quality result.

That rerun populated the corrected spatial diagnostics but retained essentially the same ranking:
local 0.00734088 RGB RMSE, temporal 0.00755635, and spatial 0.00858627. Spatial accepted 49.20
percent of neighbor opportunities, selected a neighbor for 21.50 percent of eligible receivers, and
represented mean effective M 9.85, so inactivity was ruled out. A further audit found that temporal
and spatial reservoir replacement consumed the main path RNG after local candidate generation. This
changed all later-bounce samples relative to local RIS and made a primary direct-light comparison
needlessly noisy in a 12-bounce scene. Reuse replacement now uses a separate deterministic RNG stream
while the main RNG remains at the post-local-candidate state. Re-run before drawing conclusions from
the second CornellBox capture.

Capture diagnostics now emit `spatial_ris_diagnostics.json` for spatial runs. The prior output was
absent because the writer was gated exclusively on `TemporalRisEnabled`; spatial mode intentionally
disables that flag. Spatial acceptance and merge rates use the number of eligible receiver-neighbor
opportunities as their denominator, rather than eligible receivers alone.

The post-RNG-isolation 100-frame capture in
`TestCaptures/cornellbox_ris_reuse_fixed_work_3/CornellBox/variant_comparison.csv` still does not
establish a reuse benefit. Local RIS reached RGB RMSE 0.01241675 at 258.42 ms/frame. Temporal RIS
reached 0.01238808 at 262.89 ms/frame: 0.23 percent lower fixed-work error but 1.73 percent slower,
so its tiny advantage is consumed at equal time and is too small to resolve without repeated trials.
Spatial RIS reached 0.01293435 at 261.45 ms/frame: 4.17 percent higher error and 1.17 percent slower.
The both-flags result again matched spatial-only. Temporal merged history for 51.77 percent of
eligible receivers with mean effective M 2.63. Spatial merged 49.17 percent of neighbor
opportunities and reached mean effective M 9.85. Reuse inactivity and main-path RNG perturbation are
therefore ruled out as explanations for the spatial loss. One hundred frames are sufficient to
reject the repeated, larger spatial regression, but not to distinguish the sub-percent temporal and
local fixed-work difference confidently.

## ReSTIR Architecture Audit

The current experimental reuse paths are not a complete spatiotemporal ReSTIR DI implementation.
The original 2020 ReSTIR paper and two independent implementations were reviewed after the third
CornellBox capture:

- Bitterli et al., `Spatiotemporal reservoir resampling for real-time ray tracing with dynamic
  direct lighting`: https://benedikt-bitterli.me/restir/bitterli20restir.pdf
- `TomClabault/HIPRT-Path-Tracer`, revision `d114ed0`: a production-oriented GPU implementation with
  fused/separate temporal and spatial passes, visibility reuse, light presampling, and several modern
  bias-correction schemes. It is GPL-3.0.
- `MrMagnifico/cpp-restir`, revision `8e4f0ea`: a small CPU/Whitted teaching implementation of the
  original paper's biased and Algorithm 6-style unbiased combinations. Its repository declares no
  license and its source must not be copied.

Use these references only to identify independently implementable concepts from the published
algorithms. Never copy code, comments, naming, or structure from either repository.

### Confirmed Architectural Gaps

1. Temporal and spatial reuse are mutually exclusive here. Spatial takes precedence when both flags
   are enabled, so `temporal_and_spatial_ris` is spatial-only. A normal separate-pass ReSTIR flow is
   canonical generation, temporal combination, spatial combination, final shading, and persistence
   of the final spatial result for the next frame. A fused implementation still combines current
   canonical, temporal, and spatial domains in one estimator.
2. Spatial mode never feeds its final result into temporal history. It therefore cannot build the
   recursive spatiotemporal feedback loop used by the paper and both references.
3. Spatial sampling uses four deterministic one-pixel cardinal neighbors in one pass. The paper uses
   five random neighbors in a roughly 30-pixel radius and two passes for its biased mode. HIPRT DI
   defaults to five randomized neighbors in a 16-pixel radius and one configurable pass.
   `cpp-restir` defaults to five random neighbors, radius 10, and two immutable ping-pong passes.
   Cardinal neighbors provide little proposal diversity and strongly correlated ancestry.
4. Canonical reservoirs are propagated without visibility. The selected source can be occluded,
   displace a useful sample during reuse, and consume the sole final visibility query before
   producing zero. The paper evaluates canonical visibility before temporal/spatial propagation.
   HIPRT enables canonical visibility reuse and visibility-aware bias correction by default and
   restores visibility consistency before a spatial result is reused temporally or by a later pass.
5. Reuse uses a basic target-ratio/nominal-M reservoir combination. It has no pairwise or defensive
   multi-distribution correction, no explicit canonical defensive term, and no normalization based
   on which receiver techniques could have produced the selected sample. HIPRT defaults to pairwise
   MIS and supports `1/M`, `1/Z`, MIS-like, generalized balance, defensive pairwise, and ratio
   formulations. The original paper explains that simple combination is biased when receiver PDFs
   have differing support; naive `1/Z` correction can have extreme variance for near-zero PDFs.
6. The local four-candidate reservoir enters reuse with represented M equal to four, and every
   spatial source can contribute its full represented M. Mean effective M 9.85 did not improve
   convergence, indicating nominal M is not independent sample count. HIPRT first normalizes four
   light plus one BSDF candidate and then deliberately sets canonical M to one before reuse. Test a
   completed local reservoir as one reuse observation rather than granting all represented local
   candidates persistent confidence.
7. The current temporal cap of one history candidate is intentionally conservative but cannot repair
   the missing spatiotemporal pipeline or correction. Do not raise it in the current estimator. The
   paper caps previous history at 20 times current M; `cpp-restir` demonstrates faster static
   many-light convergence with larger caps; HIPRT instead collapses canonical M to one, uses pairwise
   correction, and defaults to total M cap 3. Confidence semantics and normalization must be designed
   together.
8. Current initial RIS has light/environment proposals only. HIPRT uses four light candidates plus
   one BSDF candidate. BSDF candidates may improve glossy, environment, and large-emitter coverage,
   but must not be added until their reservoir-aware NEE/continuation-hit MIS pairing passes the
   existing high-sample mean gates.
9. The one inlined production `SampleSingleLight()` constraint protects Metal compile time, but a
   faithful visibility-aware design requires additional visibility work. Put canonical visibility in
   a separate compact kernel rather than duplicating shadow traversal in `CSMain`; profile the cost
   before accepting the estimator.

### Benchmark Interpretation

The paper's large gains target direct lighting from thousands to millions of emitters and use 32
initial candidates. HIPRT likewise targets many-light scenes. `cpp-restir` specifically replaces the
ordinary CornellBox ceiling light with 512 rectangular lights in a `Cornell Nightclub` scene because
the one-light box does not demonstrate ReSTIR's light-selection advantage. Its report observes that
small spatial neighborhoods can converge faster when nearby receiver distributions agree, but also
create blotches from correlation; the appropriate radius is scene dependent.

CornellBox remains a useful bias, stability, overhead, and low-light-count regression scene. It is
not the primary acceptance fixture for a large ReSTIR convergence claim: it has one dominant finite
emitter plus environment lighting, local four-candidate importance sampling already finds the
important proposal, visibility is a major residual variance source, and total 12-bounce RGB error is
heavily influenced by indirect transport that primary ReSTIR DI cannot directly improve. Do not
require a two-times whole-image CornellBox improvement. Require a material direct-light or
equal-time benefit on a true many-light fixture first, then ensure CornellBox does not regress
unacceptably.

Add a static direct-light fixture with approximately 256-512 individually selectable varied finite
lights, broad diffuse receivers, controlled occlusion, no environment/directional light, and no
indirect-path ambiguity. Report first-bounce direct-light or region-isolated linear-HDR error in
addition to complete-image error. Keep `Benchmark_TemporalRisStableDirectLight` as an open-receiver
positive control and `Benchmark_TemporalRisStress` as the visibility/rejection control.

### Reference Caveats

HIPRT is the stronger architectural reference, but its defaults are not constants to transplant.
Its canonical M=1, total cap 3, confidence weights, pairwise MIS, and visibility policy form one
coupled estimator. Re-derive any equivalent for this renderer's proposal PDFs, finite-light
conventions, transparent transmittance, and complementary BSDF-hit MIS.

`cpp-restir` is useful because its simple code and report independently confirm temporal-then-spatial
ordering, final-spatial-history feedback, randomized neighborhoods, multiple immutable spatial
passes, and a 512-light acceptance scene. It is not a correctness oracle: it has no motion-vector
reprojection; checked-in visibility options are off; it uses a simplified Phong Whitted renderer;
its shared random generator is used inside an OpenMP loop; and its temporal clamp scales `wSum` with
an apparent integer division that becomes zero whenever clamping is needed. Do not reproduce these
implementation details.

### Replacement Plan

Do not spend further time tuning cardinal-neighbor count or raising history M in the current simple
merge. Replace the experimental reuse architecture in ordered, separately validated stages:

1. Add the 256-512-light direct-light benchmark and trusted high-sample local/off reference. Record
   fixed-work, equal-time, direct-light-region error, mean radiance/color, and pass timings.
2. Define a canonical reservoir record with selected light sample, stored source target, local RIS
   normalization/UCW equivalent, confidence M, receiver features, and visibility state. Preserve
   enough proposal information for finite, mesh-triangle, directional, and environment samples.
3. Keep local candidate generation mathematically unchanged, but collapse each completed normalized
   local reservoir to canonical reuse confidence M=1. Add synthetic/GPU tests proving this does not
   alter local-only output or mean.
4. Add a compact canonical visibility/transmittance pass. Fully occluded canonical reservoirs may be
   invalidated for propagation only after high-sample mean tests confirm the selected estimator.
   Preserve transparent shadow transmittance semantics; do not silently replace it with opaque
   boolean visibility.
5. Implement temporal combination of the current canonical reservoir with the previous final
   spatiotemporal reservoir. Start with total M cap 3 and validated reprojection/receiver rejection.
6. Implement one immutable spatial ping-pong pass over the temporal output using five randomized,
   deterministic low-discrepancy neighbors in an initial 8-16 pixel radius. Include the canonical
   center reservoir defensively. Preserve reproducibility across captures.
7. Independently derive defensive pairwise weighting/normalization for this renderer. Account for
   valid neighbor count/confidence and evaluate the selected/reused sample under the relevant source
   and current receiver targets. Do not import HIPRT source. Add forced-selection synthetic tests and
   high-sample triangle/environment/mixed production-image tests before performance tuning.
8. Store the final visibility-consistent spatial output as next-frame temporal input. Track age or
   ancestry and expose capture diagnostics for source domain, selected history/spatial neighbor,
   valid-neighbor count, confidence M, visibility rejection, and effective sample autocorrelation.
9. Evaluate one spatial pass first. Add a second ping-pong pass only if one pass is mean-correct and
   improves equal-time direct-light convergence; re-establish visibility consistency before any
   output is consumed by another pass or future frame.
10. Add one BSDF canonical candidate only after the NEE/BSDF reservoir MIS derivation and existing
    mean fixtures pass. Light presampling is deferred until candidate generation is measured as the
    bottleneck in the large-light fixture.
11. Preserve local RIS as the unchanged fallback and keep the new spatiotemporal path default-off
    until it improves equal-time linear-HDR direct-light error, agrees in high-sample mean/color,
    survives shadow/disocclusion review, and retains acceptable Metal compile/runtime cost.

The first implementation milestone is therefore not “more neighbors.” It is a mathematically
coherent canonical-observation pipeline:

```text
normalized local canonical reservoir (reuse M=1)
-> canonical visibility/transmittance
-> temporal merge with previous final reservoir (initial cap 3)
-> one randomized spatial ping-pong pass
-> defensive pairwise normalization
-> final visibility/shading
-> persist visibility-consistent final reservoir
```

## Temporal RIS Net-Benefit Roadmap

Temporal RIS is disabled in production scene settings. The implemented reservoir reuse path remains
experimental and opt-in until it passes the acceptance gates below.

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

See the detailed ReSTIR Architecture Audit above. HIPRT-Path-Tracer is a useful GPL-3.0 algorithmic
reference for normalized canonical observations, small confidence caps, temporal validation,
visibility consistency, randomized spatial reuse, and pairwise correction. `cpp-restir` is an
unlicensed teaching reference useful only as corroboration of the original paper's pass ordering and
many-light test design. Do not copy source, comments, naming, or structure from either repository.
Independently derive and implement every adopted algorithm from published material.

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
Read AGENTS.md and AIDocs/00-index.md. Continue only the remaining follow-up work in AIDocs/23-initial-ris-direct-lighting-plan.md. Local primary direct-light RIS is the standard/default path. Temporal and spatial RIS are experimental, opt-in paths and remain disabled by default because they are incomplete and slower than local RIS. Do not replace them with spatial ReSTIR GI, light presampling, light trees, or adaptive-scheduler changes without a separate design and validation plan.

Preserve the existing initialRisCandidateCount, temporalRisEnabled, and spatialRisEnabled controls, their default-off behavior, shader upload, accumulation/history invalidation, inspector, benchmark metadata, and generic experiment overrides.

The current temporal and spatial paths are not complete spatiotemporal ReSTIR: spatial suppresses temporal, uses one-pixel cardinal neighbors, has no canonical visibility reuse or pairwise correction, and does not persist final spatial output as temporal history. Do not tune neighbor count or raise history M in that estimator. Follow the ordered ReSTIR Architecture Audit replacement plan: add a 256-512-light direct-light fixture; normalize each completed local reservoir as one reuse observation; add a compact visibility/transmittance pass; merge previous final history then randomized spatial neighbors with independently derived defensive pairwise normalization; and persist the visibility-consistent final reservoir. Start with total M cap 3 and one five-neighbor 8-16-pixel spatial pass.

For eligible bounce-0 opaque ImportanceSampled hits, preserve the existing local weighted reservoir and its unshadowed target/proposal convention. Keep one inlined SampleSingleLight call site in CSMain to avoid Metal compile explosion, using separate compact kernels for any additional visibility work. Preserve transparent transmittance and rigorously rederive current BSDF-hit MIS; stop if it cannot be proven non-double-counted.

Maintain deterministic reservoir tests, colored-many-light energy fixtures, Metal precompile validation, temporal-history coverage, and checked-in RayTracingSceneCapture comparisons for candidate counts 1/2/4/8. Measure equal-work and equal-time convergence with adaptive off and on using unchanged presets. Use apply_patch, preserve unrelated work, and update this record with measured changes.
```
