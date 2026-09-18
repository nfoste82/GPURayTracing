# Renderer Sampling Audit And Repair Plan

## Status And Scope

Audit date: **2026-09-15**. Audit source revision: `7c557cd5548c112c38d6955248dc0bd446b1a033`.
This is the **authoritative active plan** for renderer sampling correctness, adaptive allocation,
RIS reuse, and their convergence/performance validation. The first T1 GGX repair is now implemented
with focused GPU coverage; broader T1 acceptance remains pending. T2 and local R5 MIS consistency
are now repaired with focused production-GPU coverage. T3/T7 terminal-event and per-path depth
handling and T4/T8 triangle/sun lighting are also repaired with focused production-GPU coverage.
Phase 1 is in progress, not complete. Phase 0 and Phases 2-4 remain pending; S1/S2/S3 remain open.

The review inspected the active wavefront shaders, shared transport/sampling helpers, C# lifecycle,
adaptive scheduler, tests, and existing capture reports. No new Unity tests, compiles, or captures
were run during the audit. Line references identify the audited revision and will drift with edits;
function names provide durable search anchors. Code-established defects are not claims that their
visible magnitude was reproduced in a new render.

Documents 17-21 and 24-25 now retain historical evidence rather than active implementation plans.
Document 23 retains RIS design/evidence and document 26 retains wavefront architecture/compile
evidence. Their superseded continuation recipes must not override this plan. Independent future
work such as denoising, accessibility, instancing, and a light BVH is not cancelled by this audit.

## Assessment

Adaptive and temporal/spatial RIS losses cannot currently be attributed solely to tuning or GPU
overhead. There are estimator, lifecycle, sample-bound, and measurement defects. More samples do not
remove a systematic mean error. Fix the baseline and measurement contract before adding heuristics.

Sound foundations worth preserving:

- Adaptive per-pixel sample indices advance by completed layer order; the RGB Welford recurrence is
  standard, and adaptive presentation does not average the running mean a second time.
- Local RIS counts attempted fresh candidates and its core selected-sample normalization is
  recognizable. It can resample a fixed MIS-weighted NEE integrand without requiring the marginal
  density of the final selected reservoir sample. That does not validate current reuse or MIS pairing.
- Opaque lobe selection uses a mixture PDF; the VNDF construction is recognizable. The evaluated
  GGX distribution, rather than simply a missing mixture probability, was the T1 defect. Its first
  repair is implemented with focused coverage, not full estimator acceptance.
- Snell/TIR handling and compensated Russian roulette provide useful shared foundations. Sphere
  glass boundaries are explicit events; mesh glass still uses an internal traversal shortcut.
- Wavefront queues store indices and resolve shadow radiance before scatter. Do not restore the
  retired monolithic renderer as a compatibility fallback.

## Finding Register

This register preserves findings and source references at the historical audit revision above,
not a claim that every row still describes current code. T1/T2/T3/T4/T7/T8/local R5 are marked repaired below; subsequent
implementation evidence and remaining gates are recorded in Repair Progress.

Priority **P1** means correctness/safety or a prerequisite for trustworthy acceptance. **P2** means
localized correctness, convergence policy, diagnostics, or optimization. Performance effects and
statistical magnitudes require measurement even where the underlying code behavior is established.

### Base Transport

| ID | Priority | Finding And Impact | Source At Audit Revision |
| --- | --- | --- | --- |
| T1 | P1 | **Repaired (first repair; broader acceptance pending).** At the audit revision, `GgxDistribution` floored the entire squared denominator at `1e-6`, clipping smooth lobes and reporting a PDF different from the VNDF sampler. At roughness 0.03, the analytic normal-incidence D was about 392,975 versus 0.81 implemented. Pure-metal f/pdf cancellation could conceal this; mixture sampling and MIS did not generally cancel it. See Repair Progress for the current implementation and focused evidence. | [Shared:2932-2936](../Assets/Scripts/RayTracingShared.hlsl#L2932), `EvaluateMaterialBrdf` at 3039-3079 |
| T2 | P1 | **Repaired; focused acceptance below.** At audit, environment NEE used `H(q,p)` while continuation used `H(p,n*q)`. For n > 1 these were not complementary; at n=4 and p=q their sum was about 0.559. | [Shared:3664-3671](../Assets/Scripts/RayTracingShared.hlsl#L3664), [Wavefront:308-315](../Assets/Resources/RayTracingWavefront.compute#L308) |
| T3 | P1 | **Repaired; focused acceptance below.** At audit, final-event NEE competed with a BSDF continuation that was retired without evaluating sky/emitter hits. The missing complementary contribution caused energy loss. | [Manager:118-143](../Assets/Scripts/WavefrontPathTracingManager.cs#L118), [Wavefront:525-535](../Assets/Resources/RayTracingWavefront.compute#L525) |
| T4 | P1 | **Repaired; focused acceptance below.** At audit, triangle NEE used clamped artistic falloff while emitter hits returned unscaled emission, and zero shape PDFs fell into a sphere/point fallback. Mesh emitters now use one-sided radiance with physical area geometry and no legacy falloff scale. | [Shared:3689-3718](../Assets/Scripts/RayTracingShared.hlsl#L3689), [Wavefront:325-338](../Assets/Resources/RayTracingWavefront.compute#L325) |
| T5 | P1 | **Repaired with continuation-only dielectric transport; full dielectric NEE remains future work.** Dielectric surfaces no longer run the unmatched opaque-GGX direct estimator, and straight NEE segments no longer pass through dielectric boundaries. Delta and rough dielectric direct sampling would still require coherent sample/evaluate/PDF contracts before reintroduction. | `ShouldSampleDirectLight`, `GetShadowTransmittance`, `CreateScatteredRay` |
| T6 | P1 | The mesh-glass shortcut refracts through the exit whenever Snell permits, without exit Fresnel reflection. The internally consumed segment also bypasses ordinary wavefront fog events. | [Shared:4853-4926](../Assets/Scripts/RayTracingShared.hlsl#L4853) |
| T7 | P1 | **Repaired; focused acceptance below.** At audit, mesh scattering could consume multiple events, but requeueing never checked the updated per-path bounce count. The host counted queue iterations instead, allowing paths past their event budget. | [Wavefront:473-503](../Assets/Resources/RayTracingWavefront.compute#L473) |
| T8 | P1/P2 | **Repaired; focused acceptance below.** Zero-radius suns upload analytic directional-delta records. Positive-radius virtual sun triangles remain direct-only penumbra proposals and no longer receive triangle/BRDF-hit MIS. | [LightingManager:544-582](../Assets/Scripts/Lighting/LightingManager.cs#L544), [Shared:2976-2981](../Assets/Scripts/RayTracingShared.hlsl#L2976), 3691-3718, 4050-4067 |
| T9 | P2 | Multiple-scattering fog uses direct lighting with competing phase PDF zero, then adds phase-sampled emitter hits without complementary MIS. This double-counts overlapping finite-emitter connections. | [Shared:3679-3684](../Assets/Scripts/RayTracingShared.hlsl#L3679), [Wavefront:447-456](../Assets/Resources/RayTracingWavefront.compute#L447) |
| T10 | P2 | Initial medium construction includes water/spheres, not containing closed meshes. Cameras or shadow origins inside mesh glass can start in the wrong medium. Opaque normal-mapped reflection also checks shading rather than geometric hemisphere, allowing below-surface rays. | [Shared:845-944](../Assets/Scripts/RayTracingShared.hlsl#L845), 3039-3048, 4755-4765, 5000-5003 |
| T11 | P2 | Hard throughput/PDF cutoffs introduce non-vanishing bias; low throughput does not bound contribution from a bright emitter. First-hit debug presentation reads a hit buffer overwritten on later bounces. | [Shared:279](../Assets/Scripts/RayTracingShared.hlsl#L279), 3659-3662; [Wavefront:291-295](../Assets/Resources/RayTracingWavefront.compute#L291), 643-716 |

### Random Sampling

| ID | Priority | Finding And Impact | Source At Audit Revision |
| --- | --- | --- | --- |
| S1 | P1 for affected budgets | Ordinary AllLights can exceed the 108-coordinate direct-light range. Ten sphere lights with six two-coordinate shadow samples consume 120 coordinates and overlap the explicit scatter range. Longer mesh internal paths can also exceed the reserved range. This is exact coordinate reuse when those coordinates are Sobol-backed; a larger table alone does not repair it. | [Shared:314-324](../Assets/Scripts/RayTracingShared.hlsl#L314), 3531-3590, 4241-4263 |
| S2 | P2 | XOR of multiplied x/y coordinates is not a unique pixel identity. Pixels (226,1) and (215,4) have the same scramble input and sample stream. Post-hashing cannot undo input collisions. This creates spatial correlation, not proof of per-pixel bias. | [Shared:335-342](../Assets/Scripts/RayTracingShared.hlsl#L335) |
| S3 | P1 for spatial reuse; P2 for QMC | `randomNoise` generates a new seed on every shared parameter binding. Spatial prepass and camera generation use different stochastic receivers. Fresh scrambles on successive generation also lose the intended fixed progressive Sobol prefix. | [GameManager:3900-3912](../Assets/Scripts/GameManager.cs#L3900), 1499-1526 |

### Local And Reused RIS

| ID | Priority | Finding And Impact | Source At Audit Revision |
| --- | --- | --- | --- |
| R1 | P1 | Only a successfully selected temporal reservoir writes the next slot. Early exits/all-zero outcomes leave stale contents, while commit swaps history and labels it with fresh features. Invalidation of the global flag alone does not overwrite every slot. | [Shared:4198-4209](../Assets/Scripts/RayTracingShared.hlsl#L4198), [TemporalRisManager:137-169](../Assets/Scripts/Lighting/TemporalRisManager.cs#L137) |
| R2 | P1 | Reused M is added only for positive merged weights; empty source observations disappear. Normalization depends on whether the source happened to produce a useful sample. | [Shared:4111-4129](../Assets/Scripts/RayTracingShared.hlsl#L4111), 4169-4192 |
| R3 | P1 | Target-ratio merging with nominal M does not establish common support across receivers. A source with zero target on part of the center's contributing domain cannot represent that part. Feature similarity is not a support proof. | [Shared:3266-3278](../Assets/Scripts/RayTracingShared.hlsl#L3266), `GetTemporalRisMergedWeight` at 3446-3454 |
| R4 | P1 | Reused target/final MIS retains source `proposalPdf`, but continuation computes receiver-dependent light importance at the current surface. Source reservoir interpretation and current integrand partition must be separate. | [Shared:3221-3225](../Assets/Scripts/RayTracingShared.hlsl#L3221), 3361-3393, 4322-4344 |
| R5 | P1 | **Repaired for local RIS; reuse remains unvalidated.** At audit, continuation used ordinary light/shadow/environment counts and could switch to all-lights PDF=1. Its RIS flag recorded selection success rather than the attempted technique. Nondefault counts and empty reservoirs broke complementary weighting. | [Shared:4233-4248](../Assets/Scripts/RayTracingShared.hlsl#L4233), 4311-4324; [Wavefront:400-402](../Assets/Resources/RayTracingWavefront.compute#L400) |
| R6 | P1 | Sphere proposals are receiver-facing disks, not fixed emitter-surface points. Retaining their world-space sample across receivers changes the integration domain. | [Shared:4032-4036](../Assets/Scripts/RayTracingShared.hlsl#L4032), 3193-3199, 3375-3393 |
| R7 | P2 | Temporal validation features come from an unjittered pinhole trace, not necessarily the stochastic receiver that wrote the reservoir. Depth checks compare ray distances, not a complete receiver reprojection contract. | [Shared:5222-5245](../Assets/Scripts/RayTracingShared.hlsl#L5222), 3162-3183; [TemporalRisManager:140-143](../Assets/Scripts/Lighting/TemporalRisManager.cs#L140) |
| R8 | P2 | Nonprogressive/moving-camera resets often invalidate temporal reuse each frame, paying overhead without useful history. Temporal resources are not released by GameManager destruction; allocation includes both modes even if only one is used. | [GameManager:2450-2453](../Assets/Scripts/GameManager.cs#L2450), 1971-1977, 1281-1339; [TemporalRisManager:60-75](../Assets/Scripts/Lighting/TemporalRisManager.cs#L60) |

R2 has a simple same-receiver counterexample. Let fresh X and history Y independently be 0 or 2
with equal probability, so both means are 1. If Y=0 is omitted from M, output is X; otherwise the
expected reservoir result is (X+2)/2. Overall expectation is `0.5*1 + 0.5*1.5 = 1.25`, not 1.
Preserving attempted M is necessary, but moving an increment alone does not solve R3/R4/R6.

The finite-light `proposalPdf` is branch times global selection, not the complete world-area or
solid-angle density. Shape/triangle compensation is partly in the integrand. Adding another shape
factor without changing that measure consistently would double-compensate. See [RIS design record](23-initial-ris-direct-lighting-plan.md).

Deferred visibility is not by itself a local-RIS bias: final selected shading evaluates production
RGB transmittance. Visibility-unaware selection and recursive temporal correlation are plausible
efficiency losses after correctness repairs. Nominal M does not count independent observations.
Spatial takes precedence over temporal; enabling both does not enable combined spatiotemporal reuse.

### Adaptive Allocation

| ID | Priority | Finding And Impact | Source At Audit Revision |
| --- | --- | --- | --- |
| A1 | P1 | Bootstrap requests `numberOfPasses`, but allocated/generated layers are `ceil(min(highestRate,maxPaths))`. Resolve loops over the uncapped grant. Four passes with highest rate three can read beyond radiance storage. Established scheduling also ignores the configured pass multiplier. | [Scheduler:294-342](../Assets/Resources/RayTracingAdaptiveScheduler.compute#L294), [GameManager:1597-1605](../Assets/Scripts/GameManager.cs#L1597), [Wavefront:613-627](../Assets/Resources/RayTracingWavefront.compute#L613) |
| A2 | P2 | Defaults min2/H3/weight-2/spatial4/divisor16 differ materially from the historical promising min8/H1.7/weight-1/spatial0/divisor1 policy. Fine-bootstrap cohorts remain throttled after disabling low-resolution bootstrap. | [SceneSettings:27-41](../Assets/Scripts/SceneSettings.cs#L27), [Scheduler:297-308](../Assets/Resources/RayTracingAdaptiveScheduler.compute#L297) |
| A3 | P2 | Spatial disagreement rewards real image structure as well as uncertainty. Quantile mapping can assign unequal rates even to equal-score groups. For truly equal independent variances, unequal counts worsen average error at fixed work. Net impact needs controlled measurement. | [Scheduler:100-155](../Assets/Resources/RayTracingAdaptiveScheduler.compute#L100), 224-256 |
| A4 | P2 | Positive coarse guidance history seeds fine RGB, synthetic count, and pseudo-M2. It can satisfy the fine sample floor without genuine fine observations. Welford dilutes rather than replaces that prior. | [Utility:69-85](../Assets/Resources/RayTracingUtility.compute#L69), [GameManager:1647-1662](../Assets/Scripts/GameManager.cs#L1647) |
| A5 | P2 hypothesis | Using the same observations to choose future sample counts and report a sample mean is not generally unbiased at finite adaptive horizons. Rare events are poorly diagnosed by two samples. Positive service rates help consistency but do not prove finite-time unbiasedness or bounded service gaps. IID Welford error is not a calibrated confidence interval for correlated Sobol/reuse observations. | [Scheduler:177-188](../Assets/Resources/RayTracingAdaptiveScheduler.compute#L177), 287-360; [Wavefront:619-627](../Assets/Resources/RayTracingWavefront.compute#L619) |

### Measurement And Coverage

| ID | Priority | Finding And Impact | Source At Audit Revision |
| --- | --- | --- | --- |
| V1 | P1 | Retired metadata is copied from assigned metadata, not measured from completed paths. The advertised equality mostly checks the schedule against itself. Without instrumentation capture estimates paths as display width*height*frames, ignoring actual adaptive work and internal scale. | [Scheduler:363-379](../Assets/Resources/RayTracingAdaptiveScheduler.compute#L363), [Capture:1282-1286](../Assets/Editor/RayTracingSceneCapture.cs#L1282), 1370-1398 |
| V2 | P1 | Outer duration/timing stopwatch includes metric readbacks, CSV/heatmap work, and diagnostics. Per-frame render timing stops earlier. Disabling adaptive instrumentation does not disable convergence metrics. | [Capture:1246-1280](../Assets/Editor/RayTracingSceneCapture.cs#L1246) |
| V3 | P1 | Variant overrides accumulate on the same manager; omitted values inherit the previous variant. References hash only the main shader rather than all dependencies, and compatibility checks do not establish current scene/shader/settings equivalence. | [Capture:642-653](../Assets/Editor/RayTracingSceneCapture.cs#L642), 2223-2237, 2336-2340 |
| V4 | P2 | Group diagnostics reconstruct a raw mean standard-error score, not the actual weighted/RMS/spatial score and cached tier. Internal-resolution indices are also applied directly to display images. Sponza at 50% scale can compare unrelated image regions. | [Capture:1856-1913](../Assets/Editor/RayTracingSceneCapture.cs#L1856), 2429-2441 |
| V5 | P1 for acceptance | PNG metrics are linearized display color after presentation, not raw linear HDR. Adaptive and reuse structural/helper tests do not validate production scheduling, lifecycle, support, or mean correctness. Local image tests use default counts that miss several MIS defects. | [GameManager:2588-2603](../Assets/Scripts/GameManager.cs#L2588), [Compute tests:205-260](../Assets/Tests/EditMode/RayTracingComputeRegressionTests.cs#L205), 954-989; [Image tests:1055-1113](../Assets/Tests/EditMode/RayTracingImageRegressionTests.cs#L1055) |

Stale experiment manifests also need review: `initial_ris_many_lights_fixed_work.json` uses duration
and labels candidate counts that do not match its overrides; `spatial_ris_stable_direct_light_45s.json`
uses ten seconds and requests neighbor counts above the four-neighbor clamp. Record resolved values,
not labels or raw overrides, before using these manifests for acceptance.

## Existing Evidence

These are saved results, not new measurements or proof of current-revision behavior. Their original
timing/accounting/PNG limitations still apply.

| Record | Observation | Interpretation |
| --- | --- | --- |
| [Wavefront Teapot 120s, run 8](../TestCaptures/wavefront_teapot_adaptive_equal_time_1024_120s_8/TeapotMaterials/variant_comparison.csv) | Uniform: 153 frames, RGB RMSE 0.0132813. Adaptive: 97 frames, 0.0179903, about 35.5% higher error. | Supports the reported loss; not clean render-only timing or independently counted equal work. |
| [Welford history](24-welford-scheduler-recovery-plan.md) | Normalized historical candidate improved nearest-matched-fine-path RMSE by 3.88%, 12.48%, 12.45% over three seeds. Improved-cooling equal-time outcomes were 24.11% worse, 1.50% better, 6.50% better. | Allocation had promise, but no robust equal-time win and not validation of current defaults/wavefront. |
| [RIS history](23-initial-ris-direct-lighting-plan.md) | Temporal could improve independent reset-frame error while losing progressive convergence; CornellBox spatial reuse also lost. | Correlation and cost are plausible, but current estimator defects prevent attributing the losses solely to them. |

Historical compact-versus-guarded speedups and rejected CPU macrotiles concern retired tracing
architectures. Do not restore them or transplant their timings to the current layered queues.

## Repair Progress

### T1: First GGX Repair Implemented

- `GgxDistribution` now uses stable cross-product `sin^2(theta)` GGX D evaluation without a
  denominator floor, preserving narrow-lobe density instead of clipping the peak.
- Added 88 focused GPU cases covering roughness `0.03/0.05/0.1/0.2`, normal/grazing
  `NdotV = 1/0.1/0.01`, and metallic `0/0.5`. Checks compare BRDF, evaluated PDF, and sampled PDF
  against double-precision references. Normal-incidence cone and null-event frequencies use
  `65,536` attempts per seed with seeds `12345/81723`.
- Before the repair, 54/88 cases failed. The final focused Metal run passed 90/90 with none skipped,
  including existing baseline/hot-path checks, finite/null weights, and analytical `BRDF * cos / PDF`
  weights outside the existing low-PDF gate and grazing-denominator clamp.
- The added weight checks exposed a separate grazing defect: `EvaluateMaterialBrdf` floors
  `4 * NdotV * NdotL` at `1e-6`, suppressing grazing specular throughput. An intermediate run failed
  16 weight cases. Repair this with stable Smith/BRDF algebra in follow-up; the focused T1 weight
  comparison explicitly excludes that clamp's active region without weakening PDF checks.
- Targeted dry wavefront precompilation (`fog=0;terrain=0`) completed all 19 kernel dispatches
  successfully in about 57 seconds. A broader compute-test-class attempt timed out after 120 seconds
  without results. No full-suite result or image-baseline update accompanies this repair.
- Remaining T1 gates: grazing sampling-frequency integration, white-furnace and raw-HDR mean
  acceptance, and relevant water/fog/RIS/guiding wrapper compiles. Focused density/PDF coverage does
  not establish general estimator correctness or full Phase 1 completion.
- T2/local R5 have since been repaired below. No triangle-light or glass policy change is made or authorized by
  this repair; the appearance decision below remains open.

Local verification artifacts are under `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/`:
`ggx-t1-before.xml`, `ggx-t1-verified.xml`, and `ggx-t1-wavefront.log`. No image signatures were changed.

### T2 / Local R5: MIS Consistency Implemented

- Ordinary environment NEE now uses `H(n*q,p)` paired with continuation `H(p,n*q)`, with the
  same count clamped to at least one. Local RIS retains its fixed one-proposal partition:
  `q = branch * environmentPdf` or `branch * emitterSelection * triangleSelection * shapePdf`.
  Reservoir candidate count affects normalization/variance, not this MIS partition.
- Continuation records `previousInitialRisAttempted` even for empty reservoirs, reconstructs local
  receiver importance without the ordinary all-lights fallback, and ignores ordinary environment,
  light, and shadow counts for local MIS. Path layout remains 352 bytes. Classify and guide-training
  terminal weights use the same policy. Near-delta opaque local RIS retains complementary emitter
  weighting because its GGX proposal is finite-width; ordinary bypass and sphere exclusion remain.
- Eight new count/empty cases failed before the repair. Final focused Metal run passed 13/13 with
  no skips: ten new cases, two existing local mean tests, and the single-shadow-call-site check.
  Environment counts `1/2/4/16` use three seeds (`1/81723/12345`), 256 spp at 16x16, no clamp or
  tone mapping, on isolated convex opaque geometry. Combined RGB means differ from BSDF-only by
  at most 0.13%; seed-mean standard errors are recorded, with an unchanged 2% test tolerance.
- Local candidates `1/4/16` give exact full-pixel HDR invariance when independently varying ordinary
  environment/light/shadow counts `1/2/4/16` in a mixed mesh/sphere/environment fixture. Forced empty
  candidates `1/2/4/16` verify production shadow-stage metadata and primary/later-bounce gating.
  Isolated classify checks cover sky/triangle/sphere weights, unequal emitter importance, all-lights
  thresholds, empty-result metadata consumption, and near-delta flags.
- Targeted Metal precompiles completed all 19 dispatches each for dry, water, fog, water+fog,
  RIS reuse, and guided wavefront assets with terrain off. Spatial prepass completed four dispatches
  and regression probe two. Prepass integer-modulus and probe division-by-zero warnings point to
  unchanged shader lines. No full-suite or image-baseline update was performed.
- Remaining gates: broader multi-seed finite/mixed-light mean acceptance on physically matched
  integrands, guided/reuse runtime validation, and terrain permutations. These checks do not repair
  low-PDF/throughput cutoffs or reuse R1-R4/R6. Temporal/spatial reuse stays experimental/default-off.
  T3/T7 and T4/T8 were repaired subsequently as recorded below.

Artifacts: `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/mis-t2-r5-*`.
The initial post-edit run caught a missing HLSL forward declaration; it was fixed before the passing
verified/final runs and wrapper compiles.

### T3 / T7: Terminal Events And Per-Path Depth Implemented

- `_NumBounces` is the maximum number of scatter events. After the final allowed scatter, the host
  performs one terminal-only intersection and classification pass. Sky and mesh-emitter hits contribute
  with existing complementary MIS metadata; non-emissive surfaces retire without NEE or another scatter.
- `CSWavefrontClassify` retires surviving nonterminal paths whose `path.bounce` reached the budget.
  Mesh transmission/internal reflection advances this value by `bouncesConsumed`, so multi-event paths
  can no longer exceed configured depth merely because host iterations remain. Fog multiple scattering
  uses the same gate on its next classification.
- Before repair, four focused cases failed: a one-scatter sky fixture returned zero, two local-RIS
  terminal batches left exhausted surface paths active, and the host contract had no terminal pass.
  The final focused Metal set passed 10/10 with no skips, including local-RIS terminal MIS, one-event
  versus two-event sky agreement, underwater terminal radiance, and closed-mesh depth.
- Two reviewed image baselines changed intentionally. The underwater fixture gained missing terminal
  sky radiance. One closed-mesh-glass probe became darker because internally consumed events can no
  longer shade a later surface. An unrelated unstable caustic debug baseline was not changed.
- Targeted Metal precompiles completed all 19 dispatches for dry, water, fog, water+fog, RIS reuse,
  and guided terrain-off wavefront assets with no shader errors. The extra terminal intersection has
  runtime cost proportional to final queue occupancy and should be measured, not removed without
  equivalent semantics.
- Remaining gates include terrain permutations, broader bounce-count sequences and finite-emitter
  terminal means, and runtime fog/guiding/reuse validation. S1/S2/S3 RNG consistency are next.

Artifacts: `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/t3-t7-*`.

### T4 / T8: Physical Mesh Emitters And Sun Semantics Implemented

- Mesh-light emission is radiance. NEE now uses the physical one-sided area geometry factor
  `cos(light) * area / distance^2`; `_LightFalloffScale` no longer affects mesh-light radiometry or
  its importance proposal. Direct emitter hits use the same one-sided radiance contract.
- Back-facing or degenerate triangle samples contribute zero and cannot fall through to the legacy
  sphere/point fallback. Back-face emitter hits still terminate the path but return zero radiance.
- A zero-angular-radius `RayDirectionalLight` uploads two half-radiance analytic directional records,
  preserving existing two-slot indexing while producing a true delta sun and infinite shadow ray.
  Positive-radius virtual sun triangles remain finite penumbra proposals but are direct-only because
  their virtual geometry is not intersectable by continuation rays.
- Focused Metal coverage checks front/back triangle PDFs, inverse-square area geometry, one-sided
  emitter hits, mesh-light invariance under legacy falloff-scale changes, local-RIS/ordinary mean
  agreement, triangle-caustic photon production, and hard-sun upload. The reviewed mesh-light image
  baseline changed under the new physical brightness/sidedness contract.

Artifacts: `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/t4-*`.

### T5: Continuation-Only Dielectric Transport Implemented

The original `GlassLighting_BaseTransportHdrDiagnostic` exposed the mismatch below. A downward-facing camera sees a diffuse floor beneath a mesh panel, with no
sphere, an index-matched lossless sphere, or a sphere using Demofox's smooth glass parameters
(IOR 1.14, specular 0.02, opacity 0.04, near-white tint). It compares registered mesh-light NEE
against the same intersectable emission with NEE registration removed. Photon mapping, adaptive
sampling, denoising, environment lighting, tone mapping, and firefly clamping are disabled.

At 16x16, 2048 spp, eight scatter events, seeds 1/81723/12345, red-channel mean radiance was:

| Fixture | Continuation Only | NEE Plus Continuation |
| --- | --- | --- |
| No sphere | 0.56310 | 0.56157 |
| Index-matched glass | 0.59515 | 1.15285 |
| Demofox-parameter glass | 1.10656 | 1.65828 |

This established a substantial base-transport energy discrepancy without photon mapping. Straight
transparent shadow connections retained absorption-only transmission while refracted continuation
paths also reached the emitter; dielectric scatter reported PDF zero and those emitter hits retained
full weight.
The index-matched fixture is not an exact implemented identity: Schlick's grazing term remains
nonzero even for equal IORs. Do not interpret continuation-only results as a fully physical oracle.
The repair uses one coherent supported estimator rather than pretending the straight connection is
refracted: dielectric surfaces skip direct-light sampling, and every surface boundary occludes
ordinary NEE shadow rays. Refracted illumination is sampled by dielectric continuation and optional
photon caustics. Reintroducing dielectric NEE requires refractive path connections and matching
reflection/BTDF PDFs; absorption-only straight shadows are not sufficient.

The promoted `GlassLighting_NeeAndContinuationOnlyHdrMeansAgree` Metal test passed. Across the same
three seeds, Demofox glass measured 1.10656 continuation-only versus 1.10322 with the emitter also
registered for NEE (0.30% difference), replacing the prior 1.65828 result. Index-matched glass was
0.59515 versus 0.59393 (0.20%). The 2% raw-HDR acceptance covers sampling variance. Reviewed sphere
and stacked dielectric-shadow baselines changed because straight light leakage was removed; all
three shadow fixtures pass. No scene settings were changed.

The environment finite-light-type guard regression and existing three-seed opaque environment
MIS mean test also passed. Artifacts: `glass-identity-diagnostic.*` and `glass-base-diagnostic.*`
under `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/`.
These isolated fixtures do not establish the cause of overall darkness in the full Demofox scene.

## Ordered Repair Plan

Phase 1 is in progress with first T1, T2/local R5, and T3/T7 repairs implemented; remaining gates and all other
phases are pending. Keep fixes small and independently reviewable. Add a failing analytical or
production-path fixture before changing behavior where feasible; do not loosen baselines merely to
pass. Work may proceed in independent branches, but later acceptance depends on earlier gates.

### Phase 0: Safety And Trustworthy Measurement

1. Resolve A1 with one shared bound and an explicit pass-budget definition. Either support adaptive
   multi-pass consistently or explicitly restrict it; never silently read ungenerated layers.
2. Implement independent generated, completed, and resolved counts. Distinguish genuine fine paths,
   coarse preview work, and synthetic history. Read final counts outside timed measurement even when
   per-frame instrumentation is disabled.
3. Separate render-only fenced time from end-to-end capture time. Throughput runs omit per-frame
   metrics/output and always-on diagnostics; final quality readback occurs after timing.
4. Restore a complete baseline before every variant and export resolved settings, internal/output
   dimensions, seed, revision, shader/include hashes, and reference provenance. Repair stale manifests.
5. Add raw-HDR output/error alongside explicitly configured display-space error. Correct internal-to-
   display group mapping and export actual cached score/tier rather than reconstructing a different one.

Gate: production tests cross passes 1/2/4/32, representative layer caps, zero/partial/multi-layer
queues, bootstrap transitions, resets, and odd dimensions. An intentionally omitted completion must
fail accounting. A/A and reversed partial-override experiments must resolve to identical settings.

### Phase 1: Base Estimator And RNG

1. T1's first stable GGX repair and focused analytic BRDF/PDF checks at roughness
   0.03/0.05/0.1/0.2, normal/grazing views, and mixed diffuse/specular materials are implemented.
   Complete the remaining T1 acceptance gates in Repair Progress; do not treat this as phase completion.
2. T2/local R5 attempted-technique metadata, MIS partitions, proposal probabilities, and counts are
   repaired. Focused counts 1/2/4/16 and empty-RIS coverage pass; broader acceptance remains above.
3. T3/T7 use `_NumBounces` as a scatter-event budget plus one terminal sky/emitter check. Exhausted
   non-emissive paths retire in classify, enforcing per-path depth after multi-event scattering.
4. Repair S1/S2/S3: unique pixel identity, bounded semantic coordinate addressing with a separately
   domain-separated overflow stream, and one bound seed per frame. Fixed progressive captures retain
   one scramble across frames. Test prepass/main ray and RNG parity with aperture on/off.
5. T4/T8 triangle back-face, hard-sun, and non-intersectable soft-sun MIS behavior are repaired.
   Mesh lights use the accepted physical one-sided radiance contract; reviewed historical brightness
   changes are recorded above.

Gate: analytical white-furnace/PDF fixtures; NEE-only, BSDF-only, and combined mean agreement where
techniques estimate the same integrand; no systematic mean drift when only sample counts change.
Use linear HDR, multiple seeds, and measured reference uncertainty. Recompile only affected active
assets, starting with the dry wavefront asset; shared fixes require relevant wrappers/probes too.

### Phase 2: Reuse Correctness And Isolation

1. Write every temporal output slot each frame, including empty outcomes and attempted M. Add
   valid-to-empty-to-valid and light-index/reset lifecycle tests; release resources on destruction.
2. Store exact stochastic receiver features from wavefront primary hits. Establish same-receiver,
   pinhole/static correctness before allowing aperture, disocclusion, or motion reuse.
3. Derive R2/R3/R4 together: source participation independent of sampled success, correct zero-result
   confidence, current-target MIS separate from source proposal interpretation, and support-aware or
   defensive normalization. Do not transplant isolated M caps or pairwise weights without derivation.
4. Restrict unsupported domains while deriving reuse. Sphere disks require canonical coordinates
   rematerialized in the current receiver basis, or explicit exclusion; ordinary mesh emitter points
   should not receive the same remapping indiscriminately.
5. Split mode-specific allocations and avoid temporal commits in spatial-only mode. Generate spatial
   canonical reservoirs from existing primary hits rather than another camera intersection if the
   measured pipeline supports that change.
6. Only after receiver validation works, separate progressive color reset from valid motion-history
   reuse. Preserve hard invalidation for cuts, scene/light changes, capture variants, and resolution.

Gate: production reuse-wrapper tests for zero/positive observations, forced local/history/neighbor
selection, changed normals/positions, sphere-domain mapping, reset isolation, and raw-HDR means.
Keep reuse default-off until both mean-correctness and equal-time gates pass. Test independent
one-frame trials separately from long progressive runs; measure autocorrelation and history ancestry.

### Phase 3: Adaptive Allocation Controls

1. Establish uniform versus adaptive neutral parity with highest rate 1, cohort divisor 1, no coarse
   history, fixed seed, identical clamp/transport/postprocess settings, and verified genuine counts.
2. Compare current defaults against the historical conservative control: minSamples 8, highest rate
   1.7, luminance weight -1, spatial priority 0, cohort divisor 1, history 0, low-resolution bootstrap
   disabled. This is a test candidate, not a promised optimal preset.
3. Test equal-variance fields, noiseless detail, sparse bright events, and glass/caustic tails. Measure
   marginal error reduction per group quantum and actual service gaps across reclassification. Reduce
   rate contrast when between-group evidence is weak rather than forcing unequal rates on ties.
4. Keep coarse preview separate from genuine fine statistics. Test signed bias across seeds using an
   independent-pilot/frozen-schedule control; do not add naive inverse-admission weights to the mean.
5. Only tune luminance normalization, RMS blend, exploration, or granularity after equal-genuine-path
   quality improves reliably. Measure their runtime cost separately.

Gate: neutral fixed-sequence count/mean/M2 parity, bounded storage, declared exploration/service
behavior, and no material multi-seed equal-work regressions in the acceptance scenes. A rate of one
is an allocation-neutral control, not a throughput-neutral renderer.

### Phase 4: Transport Coverage And Throughput

Address T5/T6/T8/T9/T10/T11 with focused fixtures: coherent delta/rough dielectrics, explicit mesh
boundary events, containing-mesh media, phase MIS, geometric-hemisphere validity, and primary-debug
persistence. Provide a reference mode without positive-throughput cutoffs/firefly clamping when
testing mean correctness. Appearance-changing transport decisions require reviewed references.

Optimize measured hot spots without changing the accepted estimator:

- Skip zero-sample adaptive resolve loads/writes and unused adaptive frame-result clears; gate
  diagnostic metadata/atomics. Pack one-thread remap groups and parallelize serial classification
  reduction if scheduler timing warrants it.
- Hoist finite-light importance totals out of the RIS candidate loop; remove duplicate BRDF/PDF
  evaluation and repeated material fetches where event-local values can be shared.
- Cache kernel IDs and frame-invariant bindings. Avoid full sphere scans for every primary/shadow
  medium initialization using a compact volume list and empty-list fast path.
- Specialize opaque triangle any-hit work while preserving alpha-mask policy.
- Profile hot/cold wavefront state, occupancy, spills, and bandwidth before repacking queues or batching
  layers. Current storage is about 536 bytes/pixel/layer: 536 MiB at 1024 squared for one layer, about
  1.57 GiB for three. Maximum-layer storage is not evidence of equally multiplied active work.
- Reuse allocates about 228 bytes/pixel before existing feature/wavefront buffers. Mode-specific
  resources, avoided duplicate intersections, and removed history copies are concrete cost targets.

The base correctness work need not wait for all optional dielectric/volume features to be physically
complete. Declare the validated transport subset and keep unsupported cases out of promotion claims.

## Acceptance Protocol

1. Use the same revision, scene, complete settings, graphics backend, internal resolution, seed
   sequence, and presentation configuration. Compile/warm each variant before measurement, then reset
   accumulation/history; do not count warm history as free work.
2. Evaluate uniform, allocation-neutral adaptive, candidate adaptive, and local RIS/reuse separately.
   There is no public local-RIS off toggle: count one still uses RIS. A controlled ordinary-importance
   comparison needs explicit test support, not a mislabeled count-one candidate.
3. Equal-work tests use independently verified genuine camera paths, not frames or nominal reservoir M.
   Report direct-light error separately from total image error and coarse/extra primary/shadow work.
4. Equal-time tests include all required prepasses, feature/history work, queue stages, and presentation,
   but exclude diagnostic readbacks/output. Also report end-to-end diagnostic time under its own name.
5. Use at least three independent seeds initially, repeated rotated-order trials, cooldown, and
   first/second-half timing to detect drift. Increase repetitions when gains are within measurement
   uncertainty. A finite noisy reference has uncertainty too; do not treat it as exact truth.
6. Report signed RGB mean error/confidence, variance, HDR and display RMSE, genuine paths/second,
   service gaps, queue occupancy, memory, and reuse correlation/effective sample size as appropriate.
7. Start with analytical diffuse/environment/triangle fixtures and an open many-light scene below the
   128-global-light cap. Then test controlled penumbrae, CornellBox, TeapotMaterials, Sponza, and the
   validated glass/volume subset. Require lower equal-time error without a systematic mean shift or
   unacceptable regional regression before promoting a mode/default.

Use [Regression Testing](11-regression-testing.md) for Unity test commands and
[Wavefront Renderer Handoff](26-wavefront-renderer-handoff.md) for targeted compile commands. Avoid
all-assets/all-variants precompile during focused iteration. Shared estimator changes must eventually
validate the affected water/fog/RIS/guiding wrappers, not only the dry shader.

## Decisions And Non-Goals

- **Open user decision:** may physical-correctness repairs deliberately change existing brightness
  and material appearance, especially triangle falloff and glass? Recommended: yes, with explicit
  before/after review and justified baseline updates. This documentation request is not approval of
  an appearance policy or of silently changing scene presets.
- Define whether adaptive rates multiply `numberOfPasses` or represent absolute paths/frame. The
  safety repair must make unsupported combinations explicit while that contract is resolved.
- Preserve documented artistic approximations where deliberately chosen, but do not label them an
  unbiased physical reference. Firefly clamping, sphere disk/falloff, straight transparent shadows,
  rough dielectric approximations, and finite light caps require explicit acceptance scope.
- Do not increase reuse history/neighbors, add a new adaptive heuristic, or replace the renderer
  architecture to conceal unresolved estimator/accounting failures. No speedup is promised before
  controlled measurement.
