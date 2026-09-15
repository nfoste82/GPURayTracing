# Initial RIS Direct-Lighting Design Record

## Status And Ownership

Reviewed against the active renderer on 2026-09-15. This is a design/evidence record, not an
implementation or continuation plan. [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md)
is the authoritative active plan for sampling correctness and reuse repair. The former local-RIS
implementation order, automatic-policy proposal, reuse replacement recipe, temporal tuning roadmap,
and future-session prompt are superseded by that plan. No shader fixes are claimed by this review.

Local primary direct-light RIS is part of the standard renderer for eligible opaque
`ImportanceSampled` hits. There is no public local-RIS enable/disable toggle. The existing
`Lighting.InitialRisCandidateCount` control is clamped to 1-16 and defaults to 4; count 1 still runs
the RIS technique, not the ordinary estimator. `AllLights` and `UniformRandom` retain their own
estimators, and unsupported events and later bounces use ordinary direct lighting.

Temporal and spatial reuse are experimental, opt-in, default-off paths in the dry
`RayTracingWavefrontRis.compute` wrapper. They have not passed broad mean-correctness or equal-time
promotion gates. Their static, primary opaque, non-reactive, one-path-sample-per-pixel eligibility
excludes water/fog, dynamic scenes, transmission, highly smooth receivers, adaptive tracing, and
multi-pass dispatches. Spatial takes precedence when both flags are enabled: there is no combined
spatiotemporal estimator and no final-spatial-result feedback into temporal history.

## Architecture Constraints

The active queue route is documented in [Wavefront Renderer Handoff](26-wavefront-renderer-handoff.md).
`CSWavefrontTraceShadows` calls `GetLightHittingPoint()` in `Assets/Scripts/RayTracingShared.hlsl`.
`SelectLightForDraw()` chooses global emitters; `SampleSingleLight()` evaluates the selected light,
BRDF, visibility, and transparent shadow transmittance.

- Preserve one inlined `SampleSingleLight()` call site in `GetLightHittingPoint()`. Duplicating the
  shadow/BVH traversal previously caused extreme Metal compile times. Candidate selection is cheap;
  only the selected local-RIS candidate reaches production visibility.
- Preserve RGB contributions and transparent transmittance. Luminance controls reservoir selection
  and normalization, not the final light color. Stored visibility is not current visibility.
- Local RIS uses no persistent reservoir resources; experimental reuse owns separate buffers and
  receiver features. Reuse must not silently replace the ordinary estimator for unsupported events.
- Importance selection considers at most 128 global emitters. Omitted lights have zero probability,
  so this cap is biased relative to the complete scene. RIS does not repair it.
- Keep deterministic capture, settings/hash invalidation, and candidate/reuse metadata reproducible.
  Adaptive comparisons require identical presets, bootstrap settings, and complete retired-path
  accounting. Adaptive scheduling is a separate feature, not a workaround for estimator errors.

## Current Local Estimator

`IsInitialRisEligible()` and the wavefront primary-bounce gate restrict RIS to the shared opaque
BRDF path under `ImportanceSampled`. `GetLightHittingPoint()` draws
`N = _InitialRisCandidateCount` fresh finite-light/environment candidates. When both proposal
families exist, each branch has probability one half. Finite candidates select a global emitter
and, for mesh lights, an area-CDF triangle and barycentric point. Environment candidates use the
existing importance CDF. The analytic directional branch uses a cone convention, but production
directional lights are currently uploaded as virtual sun triangles; that distinction and the
zero-radius defect are covered in document 27. Sphere candidates use a receiver-facing disk through
the light center.

Each candidate stores light/triangle identity, sample position or direction, distance, environment
PDF, triangle-selection probability, and `proposalPdf`. The target is luminance of the unshadowed
RGB contribution, including the current explicit-light MIS factor for triangles/environment:

```text
weight = target / proposalPdf
weightSum += weight
select with probability weight / weightSum
selected scale = weightSum / (N * selectedTarget)
```

The local denominator retains the configured attempted candidate count even when a candidate has
zero weight. Non-finite/non-positive weights cannot win. An empty eligible RIS reservoir produces
zero direct contribution, not an ordinary-estimator retry. Production visibility is evaluated only
for the selected candidate.

### Measure And Approximation Caveat

`proposalPdf` is **not a full shape-sample density**. For finite lights it stores branch probability
times global emitter-selection probability; mesh triangle probability is compensated separately
in the contribution. Area/facing/distance terms are folded into the renderer's light contribution,
and triangle-selection and shape-to-solid-angle PDFs are applied separately for MIS. For environment
samples, `proposalPdf` includes branch probability times the environment solid-angle density.

The historical sphere falloff and receiver-facing disk, clamped triangle distance convention, and
directional-light conventions are renderer approximations, not a uniform physical area-light
measure. Same-receiver reservoir normalization can resample that existing integrand without making
its conventions physically exact. Neither the stored field nor the reservoir-selected distribution
should be described as a generally validated full-density, reservoir-aware MIS policy.

## Experimental Reuse

Temporal reuse camera-reprojects into ping-pong history, checks validity, object identity, relative
depth, and normal agreement, then re-evaluates the selected source sample at the current receiver.
The history cap defaults to one represented history candidate in addition to the fresh candidates.
It scales retained weight mass with retained `M` and uses a target-ratio merge. Final visibility is
deferred until after selection.

The current same-frame spatial path uses `RayTracingSpatialRisPrepass.compute` to trace stochastic
primary rays and write immutable local reservoirs, post-candidate RNG state, and receiver features.
The wavefront resolve consumes the center and up to four one-pixel cardinal neighbors, with
same-object, relative-depth (5 percent), and normal (dot at least 0.9) checks. It re-evaluates source
samples and shades the final selection through the shared visibility call. Normal denoiser features
are refreshed separately. The intended prepass/receiver match is not guaranteed under `randomNoise`
because of the seed-binding defect below.

### Open Correctness Findings (2026-09-15)

These are unresolved findings, not implemented repairs. Ordering, derivation choices, and acceptance
work belong to [document 27](27-renderer-sampling-audit-and-repair-plan.md).

- **Missing empty temporal writes:** `StoreTemporalRisReservoir()` runs only after a valid selected
  candidate. Misses, ineligible hits, and empty results do not consistently write empty next-history
  slots, allowing old ping-pong contents to survive.
- **Seed changes per bind:** `GameManager` draws a new `_Seed` for each shared shader binding when
  `randomNoise` is enabled. Spatial prepass and wavefront generation can therefore trace different
  stochastic receivers; restoring only post-candidate RNG state does not restore that contract.
- **Zero-target reused M is omitted:** temporal and spatial merges increment effective candidate
  count only for positive merged weight. A valid source observation whose current target is zero
  loses its represented `M`, changing normalization through selection-dependent rejection.
- **Support/domain correction is missing:** target-ratio/nominal-M combination alone does not account
  for which receiver proposal domains could generate the selected sample. Source support, current
  support, and defensive normalization have not been derived together.
- **Source selection PDF enters current MIS:** re-evaluation retains source `proposalPdf`, although
  finite-light importance selection depends on receiver position. It is not generally the current
  receiver's competing NEE PDF.
- **Continuation MIS uses ordinary counts/PDFs:** `CSWavefrontClassify` reconstructs competing PDFs
  with `_EnvironmentLightSampleCount` or `GetLightPdfForHit()` and its ordinary all-light/count
  behavior, then multiplies by the RIS branch probability. That is not generally the proposal used
  by the RIS direct path. `previousInitialRisSampled` records successful selection/normalization,
  not which technique was attempted, so an empty RIS outcome can change complementary weighting.
- **Sphere disks are not reusable world emitter points:** the sampled disk faces the source
  receiver. Reusing its world point at another receiver does not sample that receiver's disk domain;
  merely reconstructing distance/direction does not supply a valid domain mapping or Jacobian.
- **Temporal features do not describe the stochastic source receiver:** unjittered feature hits can
  differ from the actual jittered/thin-lens path that wrote the reservoir. Identity/depth/normal
  tests on those features do not prove a valid reservoir receiver match.
- **No combined spatial+temporal path:** spatial suppresses temporal merging and does not persist
  its final result as temporal input. Enabling both flags does not test spatiotemporal ReSTIR DI.

Additional architectural limitations remain: canonical visibility is not evaluated before
propagation, deterministic cardinal neighbors provide little proposal diversity, and nominal `M`
is not an independent-sample count under correlated history. Larger caps, randomized neighborhoods,
canonical `M=1`, BSDF candidates, or pairwise correction are possible design choices, not standalone
validated fixes or constants to transplant from another renderer.

## Historical Evidence

The dates below identify the repository records containing these results, not a new execution.
These captures predate the September wavefront integration and do not establish current-path parity.
Prior helper/target/RNG changes are historical observations, not repairs made by this documentation
update. Known current defects prevent treating these numbers as general estimator validation.

### Local Mean Checks (Recorded 2026-08-31)

The production GPU reservoir probe covered local normalization, capped temporal target-ratio mass,
selected-target normalization, and zero/invalid history rejection. Raw-HDR fixtures compared
ordinary NEE plus continuation hits with local counts 1, 2, 4, and 8. Triangle-only and
environment-only used 1,024 spp with 2-percent per-channel tolerance; mixed sphere/triangle/environment
used 16,384 spp with 3.5-percent tolerance. A focused three-fixture run was recorded as passing after
adding the RIS branch probability to continuation PDFs. This limited result does not cover arbitrary
light/sample counts, all-light fallbacks, empty reservoirs, reuse domains, or the current wavefront
route. Temporal-on production-image mean comparison remained open.

### Temporal Captures (Recorded 2026-08-31 To 2026-09-01)

- `temporal_ris_candidate_split_sweep_fixed_work_2`, `Benchmark_TemporalRisStress`: local RIS beat
  every temporal split. Four fresh plus one history candidate was 2.1 percent higher RGB RMSE and
  3.2 percent slower than local-4, despite 99.17 percent history acceptance.
- `temporal_ris_one_frame_trials`: across 32 paired seeds and warm-ups 1/2/4/8, temporal reset-frame
  RMSE was 2.48-2.66 percent lower, but measured-frame cost was 8.53-13.56 percent higher. The
  200-frame progressive run lost from frame 2 onward and ended 2.71 percent higher RMSE. Acceptance
  was 98.68 percent, merge rate 97.76 percent, and history selection 21.33 percent.
- `temporal_ris_stable_direct_light_fixed_work`: after 200 frames, temporal RMSE was 0.00765151
  versus local 0.00733272 (4.35 percent worse). Frame-2 errors were 0.0678062 versus 0.0664890;
  temporal remained behind at 4/8/16/32/64/128. Timings were 120.45 versus 121.06 ms/frame, an
  inconclusive 0.50-percent difference. Acceptance was 98.85 percent, merge rate 98.15 percent,
  history selection 20.68 percent, and mean effective M 4.951.
- `temporal_ris_stable_direct_light_one_frame_trials`: reset-frame RMSE improved 2.74/2.82/2.86/2.83
  percent at warm-ups 1/2/4/8, while frame cost increased 8.52/12.17/9.15/12.25 percent. Mean linear
  luminance was about 0.21-0.23 percent higher. Progressive regression also affected the open
  receiver, so visibility boundaries alone do not explain it.

### Spatial And CornellBox Captures (Recorded 2026-09-01)

The retired preceding-frame cardinal-neighbor prototype stored only fresh local reservoirs.
On `TemporalRisStableDirectLight`, 200-frame local-4 versus spatial-4 RMSE was 0.00734392 versus
0.00715252 (2.61 percent lower), at 131.58 versus 136.21 ms/frame (3.52 percent slower). Its
45-second sweep rejected cheaper candidate configurations: local-1 reached 0.00810266 in 244 frames
versus spatial-1 0.02509391 in 223; local-2 reached 0.00651741 in 305 versus spatial-2 0.00970189
in 233. That prototype was replaced, not promoted.

The replacement same-frame prepass received Metal precompile coverage, then CornellBox captures:

| Capture | Local RGB RMSE | Temporal RGB RMSE | Spatial RGB RMSE | Qualification |
| --- | --- | --- | --- | --- |
| Initial, 200 frames | 0.00735842 | 0.00757391 | 0.00858836 | Spatial counters absent; reused triangle/environment targets omitted explicit-light MIS factor |
| Target/counter rerun | 0.00734088 | 0.00755635 | 0.00858627 | Spatial acceptance 49.20%, neighbor selection 21.50%, mean effective M 9.85 |
| Post reuse-RNG isolation, 100 frames | 0.01241675 | 0.01238808 | 0.01293435 | Local 258.42, temporal 262.89, spatial 261.45 ms/frame |

The last capture is `TestCaptures/cornellbox_ris_reuse_fixed_work_3/CornellBox/variant_comparison.csv`.
Temporal merged for 51.77 percent of eligible receivers with mean effective M 2.63; spatial merged
49.17 percent of neighbor opportunities with mean effective M 9.85. Both flags matched spatial-only
in all four-way comparisons. The temporal 0.23-percent fixed-work gain was too small to resolve
without repeated trials and cost 1.73 percent more time; spatial was 4.17 percent worse and
1.17 percent slower. Counters establish activity, not correctness. Reuse RNG isolation does not
rule out the separate `randomNoise` per-bind seed defect identified in the current audit.

Spatial captures now emit `spatial_ris_diagnostics.json`; temporal captures emit
`temporal_ris_diagnostics.json`. Spatial acceptance/merge rates use eligible receiver-neighbor
opportunities, not just eligible receivers.

## Validation Boundaries

`Benchmark_TemporalRisStress` has twelve alternating-color finite lights and visibility-boundary
pillars. `Benchmark_TemporalRisStableDirectLight` has twenty varied finite lights, an open diffuse
receiver, and one controlled blocker strip. Their isolated static direct-light settings make them
useful rejection and positive controls. CornellBox is a bias/stability/overhead regression scene,
not evidence of a many-light selection advantage: one dominant emitter and substantial indirect
transport limit what primary DI reuse can improve.

Open evidence gaps include trusted high-sample local/reuse mean and color comparisons under the
active wavefront route; zero-target/support and receiver-domain cases; reset/history isolation;
camera motion, disocclusion, jitter and depth of field; and repeated equal-work/equal-time trials.
Independent reset-frame quality and long progressive convergence must be reported separately,
including correlation/effective-sample-size diagnostics where relevant. Compile success, broad
helper tests, nominal M, and manual smoke tests are not substitutes for those gates. Promotion
requires measured equal-time linear-HDR benefit in a declared target mode without mean/color bias
or unacceptable artifacts. Follow document 27 rather than the removed tuning sequences.

## Reference Boundaries

The architecture review recorded on 2026-09-01 consulted:

- Bitterli et al., *Spatiotemporal reservoir resampling for real-time ray tracing with dynamic
  direct lighting*: https://benedikt-bitterli.me/restir/bitterli20restir.pdf
- `TomClabault/HIPRT-Path-Tracer`, revision `d114ed0`, GPL-3.0.
- `MrMagnifico/cpp-restir`, revision `8e4f0ea`, no declared license.

Use published algorithms as independently implementable references; do not copy repository source,
comments, naming, or structure. HIPRT's canonical M=1, total cap 3, confidence weights, pairwise MIS,
and visibility policy form a coupled estimator, not independent tuning recommendations. The CPU
teaching implementation corroborates pass ordering and its 512-light Cornell Nightclub fixture,
but lacks motion reprojection and has implementation caveats including shared OpenMP RNG and an
apparent integer-division temporal-clamp issue. Neither repository is a correctness oracle for this
renderer's approximate light measure, transparent transmittance, or continuation-hit MIS.
