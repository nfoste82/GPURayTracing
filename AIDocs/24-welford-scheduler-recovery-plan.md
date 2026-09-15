# Welford Scheduler Recovery History

## Historical / Superseded

This preserves the pre-wavefront Welford recovery experiments following the August 2026 adaptive
work. The original entries did not supply individual capture dates; capture labels below identify
their evidence. The single authoritative current plan is
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
Recovery phases, resume checklists, score-change prescriptions, and runtime next actions have been
retired. `CSMain`, compact-root, fused-pixel, and guarded-pixel routes refer to historical builds.

The strongest recorded normalized policy used `minSamples=8`, `H=1.7`, luminance weight `-1`,
spatial priority `0`, bootstrap divisor `1`, and guidance history `0`. This is a historical
candidate, not the current defaults or a validated wavefront preset. The current audit reports
defaults of min `2`, `H=3`, weight `-2`, spatial `4`, and divisor `16`; old quality gains cannot
be attributed to that configuration. Document 27 owns current findings and decisions.

## Evidence Boundaries

- Acceptance used exported linear-sRGB RGB RMSE against the same scene reference. Equal-fine-path
  comparisons assessed fine allocation, while equal-time comparisons included startup and runtime.
- Reported fine paths exclude coarse bootstrap. History-zero preview did not seed coarse RGB,
  count, or variance into the fine estimator in the historical repair; this is not proof that all
  bootstrap configurations are unbiased or that the current bootstrap layer bound is correct.
- Historical counter equality describes the reported checks in those captures, not independent
  proof of current wavefront retirement. The current assigned-as-retired issue remains in document 27.
- Final assignment, cumulative sample counts, and score/error correlation are different diagnostics.
  A cumulative bootstrap-cohort pattern need not imply nonuniform current service.
- Thermal drift and readback fences materially affected duration measurements. None of these
  timings is a clean current wavefront benchmark.

All abbreviated capture labels below are under `TestCaptures/`.

## Sponza Baseline And Neutral Control

`sponza_adaptive_sampling_comparison_120s_4` used `1066x600`, 120 seconds, and a 1200-second
reference:

| Candidate | Fine Paths | RGB RMSE | RGB PSNR |
| --- | ---: | ---: | ---: |
| Adaptive off | 126,640,800 | 0.04478190 | 26.97795 dB |
| Welford, history 8 | 115,767,600 | 0.04716299 | 26.52797 dB |
| Welford, history 0 + preview | 129,199,200 | 0.04713843 | 26.53250 dB |

History zero was 5.26% worse in RGB RMSE despite 2.02% more reported fine paths. The 24
half-resolution bootstrap frames at `533x300`, one path per frame, cost another `3,837,600`
coarse paths that were not fine Welford evidence. Presentation repair alone did not establish an
allocation-quality win.

`sponza_adaptive_sampling_comparison_120s_5` introduced the history-zero `H=1` neutral control.
Its final schedule served all `9,975` groups with `638,400` assigned paths and active items;
assignment/error Spearman was zero. Cumulative `145..160` fine samples per pixel reflected
bootstrap cohort offsets, not final allocation bias. At approximately matched fine work, its
`97,956,160` paths and `0.05107340` RMSE compared favorably with uniform frame 153 (about 97.9M
paths, `0.05221321`). It was still worse at equal time, completing 187 versus 197 frames.
History 8 was the best adaptive duration candidate (`0.04771065`) but lost to uniform
(`0.04488079`); its allocation/error Spearman was only `0.0927`.

## Bootstrap And Rate Sweeps

`sponza_welford_stability_sweep_180f` initially confounded the min-eight floor with divisor 16:

| Candidate | Fine Paths | RGB RMSE | Interpretation |
| --- | ---: | ---: | --- |
| Adaptive off | 115,128,000 | 0.04767548 | Uniform |
| min2, H=1.0 | 92,848,960 | 0.05261719 | Adaptive neutral control |
| min8, H=1.0 | 36,071,360 | 0.08420971 | Warm-up dominated |
| min8, H=1.2 | 36,054,144 | 0.08205928 | Warm-up dominated |
| min8, H=1.35 | 36,055,104 | 0.08142859 | Warm-up dominated |

Divisor 16 could require `8 * 16 = 128` fine scheduling frames to reach every cohort, leaving
about 28 established frames in this capture after coarse startup. The preview-to-sparse-fine
transition explained why this was not a steady-state rate-policy comparison. H=1.2/1.35
assignment/error Spearman was only `0.0890/0.1048`.

`sponza_welford_stability_sweep_fast_bootstrap_180f` used divisor 1, establishing the floor in
eight fine frames. Uniform frame 156 had `99,590,400` paths and `0.05150841` RGB RMSE:

| Candidate | Fine Paths | RGB RMSE | Improvement At Approximately Matched Fine Paths |
| --- | ---: | ---: | ---: |
| min8, H=1.0 | 99,590,400 | 0.05050798 | 1.94% |
| min8, H=1.2 | 99,586,880 | 0.04980124 | 3.31% |
| min8, H=1.35 | 99,582,656 | 0.04919781 | 4.49% |

H=1.35 reported final `requested = assigned = retired = 640,960` and 103-217 cumulative fine
samples per pixel. It took 125.97 seconds versus uniform's 108.73 for 180 frames, so it was not
an equal-time win. Assignment/error and cumulative-path/error Spearman were `0.0993/0.2264`;
served groups remained brighter (`0.2126/0.0664` luminance) with only modestly higher reference
error (`0.04549/0.04214`).

`sponza_welford_rate_extension_fast_bootstrap_180f` extended the rate sweep:

| Candidate | Fine Paths | RGB RMSE | Improvement At Nearest Uniform Checkpoint |
| --- | ---: | ---: | ---: |
| min8, H=1.35 | 98,941,696 | 0.04935433 | 4.16% (frame 155) |
| min8, H=1.5 | 99,582,144 | 0.04911788 | 4.64% (frame 156) |
| min8, H=1.7 | 99,539,712 | 0.04892996 | 5.01% (frame 156) |

H=1.7 reported final `639,936` requested/assigned/retired paths. Service breadth fell to 85.91%
from 92.32% at H=1.35. Assignment/error Spearman stayed near `0.0960/0.0978`, while cumulative
correlation fell to `0.2015` from `0.2269`. Better image error did not imply a strong error ranking.

## Three-Seed Absolute-Score Results

`sponza_welford_h1_7_three_seed_180f` used rotated candidate order:

| Seed | Adaptive Fine Paths | Adaptive RMSE | Nearest Uniform Paths | Uniform RMSE | Improvement |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 98,899,776 | 0.04909989 | 98,841,600 | 0.04999985 | 1.80% |
| 19 | 99,534,848 | 0.04926747 | 99,517,400 | 0.05511887 | 10.62% |
| 37 | 99,533,184 | 0.04927021 | 99,517,400 | 0.05511887 | 10.61% |

These are nearest-checkpoint within-seed comparisons, not exactly equal counts or an average
cross-seed acceptance metric. Service breadth was about 85.9%; assignment/error Spearman was
`0.1048-0.1128`, cumulative correlation `0.2013-0.2061`.

`sponza_welford_h1_7_equal_time_three_seed_120s` lost in all three final images. Adaptive
reported 88.68M-91.24M fine paths versus uniform's 117.05M-127.28M; recorded average final
RMSE was `0.05153` versus `0.04977`. Adaptive FPS was `1.35-1.39` versus `1.52-1.65`.
Served groups were about 3.4x brighter (`0.2224-0.2245` versus `0.0658-0.0665`) without a
matching reference-error difference (`0.04797-0.04871` versus `0.04425-0.04545`).
Assignment/error Spearman was only `0.1227-0.1320`. Scheduler fence averages of `1.245-1.424 ms`
versus trace `549.0-684.8 ms` did not support scheduler cost as the sole explanation; trace
workload and capture synchronization were substantial.

## Luminance Normalization

`sponza_welford_luminance_normalization_180f` improved monotonically across the tested blend.
Full normalization reached about 99.58M fine paths and `0.04791464` RGB RMSE, 6.98% below
uniform frame 156 (`0.05150841`) and better than the absolute-score control (`0.04909989`).
Served/unserved luminance fell from `0.2231/0.0666` (3.35x) to `0.2098/0.1464` (1.43x).
Service breadth stayed near 86%; score/error Spearman improved `0.2037 -> 0.2218` and
assignment/error `0.1128 -> 0.1163`. The 0.5 blend left the brightness bias and reduced
assignment/error correlation to `0.0737`; it was not selected. Full normalization still ran at
1.36 FPS versus uniform 1.65, so this was not equal-time evidence.

`sponza_welford_normalized_h1_7_three_seed_180f` confirmed the historical normalized candidate:

| Seed | Adaptive Fine Paths | Adaptive RMSE | Nearest Uniform Paths | Uniform RMSE | Improvement |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 98,938,688 | 0.04806174 | 98,841,600 | 0.04999985 | 3.88% |
| 19 | 99,581,632 | 0.04824200 | 99,517,400 | 0.05511887 | 12.48% |
| 37 | 99,598,464 | 0.04825752 | 99,517,400 | 0.05511887 | 12.45% |

Served/unserved luminance ratios were `1.48/1.44/1.42`, below the historical 2x gate.
Service breadth was `86.13-86.49%`, score/error Spearman `0.2213-0.2261`, and assignment/error
`0.1203-0.1346`. Fine-path comparisons passed the recorded three-seed gate, but fixed-frame
capture throughput remained `1.15-1.17` FPS versus uniform `1.35-1.66`.

## Equal-Time Limits

`sponza_welford_normalized_h1_7_equal_time_three_seed_120s` was thermally confounded: adaptive
completed 100/104/123 frames, with first-half frame times `739/808/699 ms` rising to
`1388/1219/971 ms`. It was superseded for end-to-end judgment by the improved-cooling `_2` run:

| Seed | Adaptive Fine Paths | Adaptive RMSE | Uniform Paths | Uniform RMSE | Adaptive Result |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | 71,486,144 | 0.05638712 | 122,803,200 | 0.04543394 | 24.11% worse |
| 19 | 75,311,680 | 0.05526862 | 98,498,400 | 0.05611272 | 1.50% better |
| 37 | 93,199,552 | 0.04990774 | 109,371,600 | 0.05337738 | 6.50% better |

The two wins supported the earlier fine-path evidence but did not pass the all-seed equal-time
gate. Adaptive completed 137/142/170 frames; first/second-half times still rose `625 -> 865`,
`614 -> 813`, and `510 -> 637 ms` despite the recorded 20-second cooldown. Thermal drift
remained material, but extreme throttling was not the sole explanation. Luminance ratios
`1.34/1.35/1.37`, score/error `0.2272-0.2286`, and breadth `85.35-85.94%` stayed stable.

## Retired Trace Comparisons

The compact-pixel fused route already looped over each active pixel's local samples and updated
HDR/Welford once; it no longer used separate root expansion/radiance/resolve. Removing a redundant
buffer alias passed its controlled GPU parity fixture but did not itself prove a speedup.

`teapotmaterials_fused_adaptive_baseline_1024_60f_cooldown30` was warm-up dominated by 24
half-resolution preview frames plus an eight-sample fine floor. The subsequent
`teapotmaterials_fused_adaptive_baseline_1024_120f_bootstrap12_cooldown30` reduced preview to
12 frames, leaving about 100 established frames. It reported 112.14M adaptive versus 125.83M
uniform paths, yet took 182.61 versus 104.20 seconds (`1.522/0.868 s/frame`). Scheduler fences
averaged `45.7 ms/frame`, fused trace `1195.6 ms/frame`. Final counter checks reported
`1,050,816` requested/assigned/work-item/retired paths; final RMSE was `0.007925` adaptive versus
`0.007473` uniform. The deficit could not be explained by classification alone.

`teapotmaterials_adaptive_compact_vs_guarded_1024_120f_2` and rotated-order `_3` compared the
same scheduler settings at near-equivalent work (112.14M/113.18M paths):

| Run Order | Guarded ms/frame | Compact ms/frame | Guarded Improvement |
| --- | ---: | ---: | ---: |
| Compact first | 1020.4 | 1147.3 | 11.1% |
| Guarded first | 954.2 | 1212.2 | 21.3% |
| Mean | 987.3 | 1179.7 | 16.3% |

The reported final images were equivalent. Compact construction/indirection was removed in favor
of guarded full-screen dispatch at that milestone. That historical decision is not a prescription
to restore guarded tracing over the current wavefront renderer. Later layered experiments are
retained in [Trace Throughput History](25-adaptive-trace-throughput-plan.md).
