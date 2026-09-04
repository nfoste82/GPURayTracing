# Welford Scheduler Recovery Plan

## Status

**Current phase: 3/3 - full luminance normalization passed the three-seed equal-path and
bright-service-bias gates; the improved-cooling equal-time run does not pass all three seeds, so
preserve the score policy and proceed to runtime optimization.**

This document is the continuation record for the Welford fixed-8x8 adaptive scheduler. Update the
status line and the completed-work list before stopping a session. Do not interpret a duration
capture as allocator evidence until the corresponding equal-fine-path diagnostics pass.

## Problem Statement

`TestCaptures/sponza_adaptive_sampling_comparison_120s_4/` evaluated the repaired history-zero
bootstrap presentation path at 1066x600 against a 1200-second reference:

```text
candidate                         fine paths    RGB RMSE      RGB PSNR
adaptive off                      126,640,800   0.04478190    26.97795 dB
Welford, history 8                115,767,600   0.04716299    26.52797 dB
Welford, history 0 + preview      129,199,200   0.04713843    26.53250 dB
```

History-zero preview is a presentation repair: it does not seed coarse RGB, count, or variance
into the fine estimator. It is faster than history eight but is still 5.26 percent worse than
uniform RGB RMSE despite reporting 2.02 percent more fine paths. The remaining problem is
Welford allocation policy, not bootstrap presentation.

The reported fine path count excludes the 24 half-resolution bootstrap frames. At 533x300 and one
path per frame, that is approximately 3,837,600 additional coarse paths. History zero intentionally
does not use them as Welford evidence.

## Constraints

Keep these properties unless an experiment explicitly validates a replacement:

```text
CSMain remains unchanged with adaptive sampling disabled.
sampleIndex = oldPixelPathCount + localSample.
requested == assigned == compact work-item paths == retired.
No zero-path compact work item.
No CPU readback in interactive scheduling.
No global sort or image-sized serial GPU allocation pass.
No hierarchy or material priors before fixed-8x8 allocation wins.
```

The acceptance metric is exported linear-sRGB RGB RMSE against the existing scene reference. Run
equal-fine-path quality before equal-time quality.

## Phase 1: Diagnose And Establish A Neutral Control

### 1. Capture Diagnostics

Enable generic experiment captures to retain adaptive instrumentation for adaptive variants and
write a Welford group report for each variant independently. Each report must include, at minimum:

```text
current assigned paths
cumulative fine paths from AdaptiveSamplingState.x
Welford score
reference RGB RMSE
mean luminance
served-group fraction
score/error Spearman
assigned-path/error Spearman
cumulative-path/error Spearman
served versus unserved luminance and reference error
```

The diagnostics are capture-only and must not alter interactive scheduling. Current assignment is
the final schedule; cumulative paths are final accumulated fine samples. Label both explicitly.

### 2. Allocation-Neutral Control

Add an experiment variant with:

```text
adaptiveGuidanceHistoryFrames = 0
adaptiveHighestBucketSampleRate = 1.0
```

At `H=1`, every post-bootstrap tier has rate one. This preserves the adaptive bootstrap, state,
compaction, and trace path while removing meaningful redistribution.

Compare `adaptive_off`, `adaptive_welford_history_0_h1`, and the existing history-zero `H=1.7`
candidate. The `H=1` candidate must match uniform at equal fine paths within run-to-run noise
before modifying Welford scoring or allocation.

### 3. Stable-Evidence And Contrast Sweep

If the neutral control passes, sweep settings only, in this order:

```text
min samples: 2, 4, 8
highest rate: 1.0, 1.2, 1.35, 1.5, 1.7
```

Hold history zero, Sobol dimensions, seed, firefly clamp, resolution, and reference constant.
The expected first plausible candidate is `minSamples=8` with `H=1.2` or `H=1.35`. Reject a
candidate if it fails equal-path RGB RMSE or if allocation/error correlation remains weak.

## Phase 2: Improve The Score Or Mapping, One Change At A Time

### 4. Marginal RGB-MSE Score

If stable Welford evidence plus conservative contrast still loses, compare the existing score:

```text
sqrt(M2rgbSum / (n * (n - 1)))
```

with estimated one-sample marginal squared-error reduction:

```text
M2rgbSum / ((n - 1) * n * (n + 1))
```

Reduce the candidate score over each fixed 8x8 group by mean. Change no tier, rate, bootstrap, or
admission behavior in this A/B experiment.

### 5. Score-To-Tier Fidelity

Inspect source-bucket occupancy, within-bucket score spread, score-to-tier correlation, and

1. Keep score-derived tiers stable during a reclassification interval.
2. Continue rotating fractional path admission separately.
3. Improve histogram resolution or score calibration without a global sort.

Do not rerandomize a group’s target tier every frame as the first choice when its source score has
not been reclassified.

### 6. Exact Budget Reconciliation

The current tier populations conserve work only in expectation because each group independently
rounds fractional rates. Add an exact or tightly bounded GPU reconciliation only after score and
mapping experiments show useful allocation. It must grant complete valid-group quanta, rotate ties,
and preserve the accounting contract.

## Phase 3: Spatial Granularity And Performance

### 7. Fixed 4x4 Diagnostic

Only if score and allocation correlations are useful but final RMSE still loses, compare fixed 4x4
against 8x8 groups. Accept the additional scheduler cost only if equal-path quality improves.

### 8. Equal-Time Confirmation

After an equal-fine-path winner survives at least three seeds and rotated candidate order, run the
120-second comparison. Separate bootstrap paths, fine paths, scheduler/trace timing, and final
RMSE. Do not claim a win from extra unaccounted bootstrap work.

## Resume Checklist

1. Read `AGENTS.md`, `AIDocs/00-index.md`, this document, `AIDocs/17-adaptive-sampling-continuation.md`,
   `AIDocs/20-dammertz-adaptive-sampling-plan.md`, and `AIDocs/21-adaptive-sampling-performance-plan.md`.
2. Inspect `git status` and preserve unrelated worktree changes.
3. Continue at the phase named in **Status**.
4. Run focused adaptive EditMode tests after each implementation change.
5. Record exact capture path, variants, settings, and result in this document before advancing.

## Completed Work

- Repaired the history-zero bootstrap so coarse color is presentation-only until each fine pixel
  reaches the configured genuine sample floor.
- Captured `sponza_adaptive_sampling_comparison_120s_4`; it rejected the current history-zero
  Welford policy as an equal-time quality candidate.
- Identified initial likely causes: variance ranking at `n=2`, rate contrast `H=1.7`, randomized
  within-bucket tier assignment, expected rather than exact frame budgets, and score/metric mismatch.
- Generic adaptive experiment variants now retain capture-only instrumentation and emit independent
  `<variant>_group_diagnostics.json` and `.csv` reports. The reports distinguish final current
  assignment from final cumulative fine sample count, and summarize service, score/error,
  allocation/error, cumulative-allocation/error, and served/unserved bias.
- Added `adaptive_welford_history_0_h1` to the Sponza manifest. It differs from the history-zero
  `H=1.7` candidate only by `adaptiveHighestBucketSampleRate = 1.0`.
- Focused generic-experiment regression coverage passed after the Phase 1 implementation.
- Captured `sponza_adaptive_sampling_comparison_120s_5`. The `H=1` final schedule was genuinely
  uniform: 638,400 active work items and assigned paths for 638,400 pixels, all 9,975 groups
  served, and `assignedPathErrorSpearman = 0`. Its cumulative heatmap looked non-uniform only
  because it retained the harmless first fine-bootstrap cohort offsets (`145..160` fine samples
  per pixel). The capture heatmap now displays a uniform final schedule in neutral green rather
  than presenting that history as current allocation bias.
- At approximately matched fine work, `H=1` is a useful adaptive-path parity control. It finished
  with 97,956,160 fine paths and RGB RMSE 0.05107340; adaptive-off at frame 153, approximately
  97.9 million paths, had RGB RMSE 0.05221321. It remains worse at equal time because the adaptive
  route completed only 187 frames versus uniform’s 197, so this is not an end-to-end win.
- History 8 remained the best adaptive duration candidate in this run (RGB RMSE 0.04771065), but
  it was still worse than uniform (0.04488079). Its final allocation/reference-error Spearman was
  only 0.0927, while its served groups were brighter (0.2228 versus 0.0673) without higher
  reference error (0.04449 versus 0.04223).
- Captured `sponza_welford_stability_sweep_180f` using the initial `minSamples=8` rate sweep.
  The three `min8` candidates were effectively indistinguishable as allocation-policy tests:

  ```text
  candidate                  fine paths    RGB RMSE      post-bootstrap condition
  adaptive off               115,128,000   0.04767548    uniform
  min2, H=1.0                 92,848,960   0.05261719    uniform adaptive control
  min8, H=1.0                 36,071,360   0.08420971    warm-up dominated
  min8, H=1.2                 36,054,144   0.08205928    warm-up dominated
  min8, H=1.35                36,055,104   0.08142859    warm-up dominated
  ```

  With `adaptiveBootstrapGroupDivisor=16`, a fine floor of eight requires up to `8 * 16 = 128`
  full-resolution scheduling frames to reach every rotating group cohort. The fixed 180-frame
  capture therefore left only about 28 frames after the final cohort could become established.
  Its output was held at the coarse preview during this handoff, then dropped from the preview to
  sparse honest fine estimates. The observed poor RMSE and near-identical path totals are expected
  from that warm-up, not evidence that `H=1.0`, `1.2`, and `1.35` have equivalent steady-state
  quality.
- The final group reports confirm the floor, rather than rate contrast, dominated the run. `H=1`
  remained uniform; `H=1.2` and `H=1.35` had only weak assignment/error Spearman values (0.0890
  and 0.1048) and showed the existing bright-service bias. These values are insufficient to accept
  an allocation policy, but they are too early to reject stable Welford evidence itself.
- Captured `sponza_welford_stability_sweep_fast_bootstrap_180f` with
  `adaptiveBootstrapGroupDivisor=1`. This establishes the eight-sample floor in eight fine frames,
  so the remaining capture is an established-schedule measurement. At essentially equal fine paths
  to adaptive-off frame 156 (`99,590,400` paths, RGB RMSE `0.05150841`), all `min8` variants were
  better:

  ```text
  candidate       fine paths    RGB RMSE      improvement versus uniform frame 156
  min8, H=1.0      99,590,400   0.05050798    1.94%
  min8, H=1.2      99,586,880   0.04980124    3.31%
  min8, H=1.35     99,582,656   0.04919781    4.49%
  ```

  `H=1.35` is the first equal-fine-path Welford winner. Its final schedule retained exact
  accounting (`requested = assigned = retired = 640,960`) and its cumulative distribution ranged
  from 103 to 217 fine samples per pixel. It is not an equal-time winner yet: the candidate took
  125.97 seconds for 180 frames while uniform took 108.73 seconds.
- The group diagnostics still show that direct allocation/error correlation is modest rather than
  decisive: final assigned-path/error Spearman is 0.0993 and cumulative-path/error Spearman is
  0.2264 for `min8, H=1.35`. Served groups are brighter (0.2126 versus 0.0664) and only moderately
  more erroneous (0.04549 versus 0.04214). Treat the equal-path image result as provisional and
  require the remaining rate sweep plus multi-seed confirmation before score or mapping changes.
- Captured `sponza_welford_rate_extension_fast_bootstrap_180f`, completing the configured
  contrast sweep. The quality result improved monotonically as `H` increased. At the nearest
  uniform checkpoint (frame 156: 99,590,400 paths and RGB RMSE 0.05150841), the candidates were:

  ```text
  candidate       fine paths    RGB RMSE      improvement versus uniform
  min8, H=1.35     98,941,696   0.04935433    4.16% (uniform frame 155)
  min8, H=1.5      99,582,144   0.04911788    4.64%
  min8, H=1.7      99,539,712   0.04892996    5.01%
  ```

  `min8, H=1.7` is the provisional equal-path winner. It preserved exact final accounting
  (`requested = assigned = retired = 639,936`) and reduced equal-path RGB RMSE by about five
  percent. It also reduced final service breadth to 85.91% from 92.32% at `H=1.35`; this is an
  expected tradeoff of larger contrast but remains a risk to validate. Its assigned/error Spearman
  remained comparable (0.0960 versus 0.0978), while cumulative-path/error Spearman fell from
  0.2269 to 0.2015. Do not proceed to equal-time measurement unless this image-quality improvement
  survives the required multi-seed test.
- Captured `sponza_welford_h1_7_three_seed_180f`. `minSamples=8`, `H=1.7` passed the equal-path
  gate across all three Sobol seeds and rotated candidate order:

  ```text
  seed   adaptive fine paths  adaptive RMSE  nearest uniform paths  uniform RMSE  improvement
  1        98,899,776         0.04909989       98,841,600           0.04999985    1.80%
  19       99,534,848         0.04926747       99,517,400           0.05511887   10.62%
  37       99,533,184         0.04927021       99,517,400           0.05511887   10.61%
  ```

  The latter two uniform sequences are independently noisier for those seeds, so do not average
  raw cross-seed RMSE values. The within-seed matched-path comparison is the acceptance evidence.
  Adaptive allocation diagnostics were stable across seeds: 85.9% of groups were served,
  assigned-path/error Spearman was 0.1048 to 0.1128, and cumulative-path/error Spearman was
  0.2013 to 0.2061. The bright-service bias remains, but did not prevent the repeated equal-path
  improvement. Advance without score/mapping changes to the required equal-time confirmation.
- Captured `sponza_welford_h1_7_equal_time_three_seed_120s`. The policy failed the end-to-end
  equal-time gate: it retired 88.68M to 91.24M fine paths versus uniform's 117.05M to 127.28M,
  and was worse in all three final images. Its average final RGB RMSE was 0.05153 versus uniform's
  0.04977. The direct cause is lower throughput (1.35 to 1.39 adaptive FPS versus 1.52 to 1.65
  uniform FPS), not a loss of the previously validated equal-path quality gain.
- The user's heatmap diagnosis is confirmed quantitatively. Across the equal-time seeds, served
  groups had mean luminance 0.2224 to 0.2245 while unserved groups were only 0.0658 to 0.0665
  (about 3.4x brighter). Their reference errors differed much less, 0.04797 to 0.04871 versus
  0.04425 to 0.04545. Current assignment/error Spearman was only 0.1227 to 0.1320. The absolute
  RGB standard-error score is therefore spending too much of its rate contrast on bright regions.
- Capture-only phase timing does not support treating scheduler cost as the sole explanation:
  scheduler fence averages were 1.245 to 1.424 ms, while root trace averaged 549.0 to 684.8 ms.
  The adaptive root workload and capture synchronization dominate this duration result. Preserve
  the equal-path policy result, but do not claim an equal-time win or change the compact accounting
  infrastructure.
- Captured `sponza_welford_luminance_normalization_180f`. The existing regularized correction
  improves image quality monotonically across the tested blend and materially corrects the visible
  bright-service bias. At essentially uniform frame 156's 99.59M fine paths (RGB RMSE 0.05150841),
  the full blend is best at 99.58M paths and RGB RMSE 0.04791464, a 6.98% equal-path improvement.
  This also improves on the absolute-score control's 0.04909989 RMSE.
- Full normalization is the appropriate provisional score calibration: served versus unserved mean
  luminance fell from 0.2231 versus 0.0666 (3.35x) at blend zero to 0.2098 versus 0.1464 (1.43x)
  at blend one. It retained similar service breadth (86.15% versus 85.89%), improved score/error
  Spearman (0.2218 versus 0.2037), and slightly improved assignment/error Spearman (0.1163 versus
  0.1128). The 0.5 blend is not a candidate despite good aggregate RMSE because its
  assignment/error Spearman dropped to 0.0737 and the visible brightness bias remained unchanged.
- This single-seed diagnostic remains slower than uniform (1.36 adaptive FPS at full normalization
  versus 1.65 uniform FPS), so it is not an equal-time result. First require the full normalized
  score to survive the three-seed equal-path gate; then compare runtime overhead without conflating
  it with the rejected absolute-RGB score.
- Captured `sponza_welford_normalized_h1_7_three_seed_180f`. Full luminance-normalized
  `minSamples=8`, `H=1.7` passed the equal-path gate across all required seeds and the rotated
  candidate order. At the nearest uniform fine-path checkpoints, it improved RGB RMSE in every
  pair:

  ```text
  seed   adaptive fine paths  adaptive RMSE  nearest uniform paths  uniform RMSE  improvement
  1        98,938,688         0.04806174       98,841,600           0.04999985    3.88%
  19       99,581,632         0.04824200       99,517,400           0.05511887   12.48%
  37       99,598,464         0.04825752       99,517,400           0.05511887   12.45%
  ```

  Each pair uses the nearest fine-path checkpoint retained by the experiment comparison workflow;
  do not average raw cross-seed RMSE values because the independently scrambled uniform sequences
  have different noise realizations. The within-seed matched-path comparisons are the acceptance
  evidence.
- The full-normalization diagnostic also clears the explicit bright-service-bias gate in every
  seed. Served/unserved mean luminance was `0.2102/0.1417` (1.48x), `0.2100/0.1460` (1.44x), and
  `0.2095/0.1473` (1.42x), all below the required 2x. Service breadth was stable at 86.13% to
  86.49%; score/error Spearman was 0.2213 to 0.2261; current assignment/error Spearman was 0.1203
  to 0.1346. This is a material correction from the absolute score's approximately 3.4x
  served/unserved luminance ratio.
- The normalized candidate still has a substantial capture throughput deficit in this fixed-frame
  run: 1.15 to 1.17 FPS versus uniform's 1.35 to 1.66 FPS. Fixed-frame FPS is contextual only;
  the next required measurement is a 120-second equal-time capture using this validated score.
- Captured `sponza_welford_normalized_h1_7_equal_time_three_seed_120s`. It is thermally
  confounded and superseded for end-to-end judgment: adaptive completed only 100, 104, and 123
  frames, and adaptive first-half to second-half frame time rose from `739/808/699 ms` to
  `1388/1219/971 ms` for seeds 1, 19, and 37. The run is useful only as evidence that duration
  throughput cannot be inferred from a single ordered pass under those environmental conditions.
- Captured `sponza_welford_normalized_h1_7_equal_time_three_seed_120s_2` under improved cooling.
  Adaptive throughput improved materially, completing 137, 142, and 170 frames with 71.49M,
  75.31M, and 93.20M fine paths. The final equal-time quality outcome is mixed:

  ```text
  seed   adaptive paths  adaptive RMSE  uniform paths  uniform RMSE  adaptive result
  1        71,486,144     0.05638712    122,803,200    0.04543394    24.11% worse
  19       75,311,680     0.05526862     98,498,400    0.05611272     1.50% better
  37       93,199,552     0.04990774    109,371,600    0.05337738     6.50% better
  ```

  This is the first duration capture where normalized adaptive beats uniform in two seeds despite
  retiring fewer fine paths (76.5% and 85.2% of uniform work, respectively), supporting the
  earlier equal-path quality result. It does not pass the required three-seed equal-time gate
  because seed 1 remains decisively worse at 58.2% of uniform work.
- Thermal drift still prevents treating the second duration run as a final throughput measurement.
  Adaptive first-half/second-half frame times rose `625 -> 865 ms` (38.4%), `614 -> 813 ms`
  (32.3%), and `510 -> 637 ms` (24.8%) in seed order. The saved experiment manifest records
  `cooldownSeconds = 20.0`; this is acceptable for the improved-thermal confirmation and does not
  invalidate its conclusion. It rules out the earlier extreme throttling as the sole explanation,
  but it is not a stable all-seed end-to-end win.
- The normalized score policy remains stable during the improved-cooling run: served/unserved
  luminance ratios were 1.34x, 1.35x, and 1.37x; score/error Spearman was 0.2272 to 0.2286; and
  service breadth was 85.35% to 85.94%. Do not change the score or scheduler mapping based on the
  thermal rerun.

## Next Action

Preserve `adaptiveNormalizePriorityByLuminance = 1` as the validated score calibration and begin
the runtime optimization order in `AIDocs/21-adaptive-sampling-performance-plan.md`. The first
implementation candidate is a compact-pixel fused adaptive kernel: one indirect thread per active
pixel traces its assigned local samples and updates HDR accumulation, RGB Welford state, and any
scheduler-only state once. This removes root-list expansion, root offsets, root-radiance traffic,
and the separate resolve dispatch. Preserve `sampleIndex = oldPixelPathCount + localSample`, exact
requested/assigned/compact/retired accounting, finite-radiance handling, and the unchanged
adaptive-off `CSMain` path. Validate focused tests, a 512x512 accounting smoke, equal-fine-path
quality, then repeat the three-seed 120-second capture.
