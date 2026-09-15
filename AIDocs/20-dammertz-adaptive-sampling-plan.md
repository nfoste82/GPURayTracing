# Dammertz Adaptive Sampling History

## Historical / Superseded

This is the August 21-22, 2026 fixed-8x8 split-estimator record. Its bounded-service implementation
plan and continuation prompt are superseded by the single authoritative current plan:
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
Dammertz scheduling was subsequently retired; adaptive rendering now uses Welford with wavefront
generation/resolve. This record is not an instruction to restore either Dammertz or compact roots.

## Measured Result

`TestCaptures/dammertz_three_way_60s_4/TeapotMaterials/` was the first useful post-bootstrap
three-way duration capture (`1024x1024`, 60 seconds):

| Candidate | Reported Retired Paths | RGB RMSE | RGB PSNR | Average FPS |
| --- | ---: | ---: | ---: | ---: |
| Adaptive off | 114,294,784 | 0.00853382 | 41.3771 | 1.8138 |
| Welford | 105,906,176 | 0.00958030 | 40.3724 | 1.6826 |
| Dammertz | 87,031,808 | 0.01083300 | 39.3050 | 1.3787 |

Dammertz was 26.9% worse than uniform RGB RMSE and retired 23.9% fewer paths; Welford was
12.3% worse and retired 7.3% fewer. This duration run did not isolate allocation from throughput,
but it clearly rejected those end-to-end candidates.

The final Dammertz schedule served `4,100 / 16,384` groups (25.0%), averaging `255.75` paths per
served valid 8x8 group and zero for the remaining 75%. The allocation/error Pearson correlation
was approximately `0.079` for both adaptive policies. Dammertz's score/error Spearman was better
than Welford's (`0.3022` versus `0.1790`), but that weak ranking did not justify a hard
zero-versus-four-path-per-pixel cutoff.

| Policy / Service | Mean Luminance | Mean Reference Group RGB RMSE |
| --- | ---: | ---: |
| Dammertz served | 0.6743 | 0.00969 |
| Dammertz unserved | 0.5984 | 0.00994 |
| Welford served | 0.6963 | 0.00922 |
| Welford unserved | 0.5911 | 0.00858 |

The brighter served groups did not have a corresponding reference-error increase. The upper
checkerboard/diffuse allocation concern was supported by measurements, not just the heatmap.
Evidence files include `adaptive_variant_comparison.csv`, `adaptive_group_diagnostics.csv`,
allocation heatmaps, and candidate/reference difference images in the capture folder.

## Rejected Approach

The preliminary eight-frame equal-path comparison had shown only 0.037% lower RGB RMSE for
Dammertz, while every group still received 64 paths. It did not establish useful redistribution.
The later duration/group evidence rejected the fixed-8x8 split policy as implemented, not the
external hierarchical Dammertz method in general.

The proposed broader rotating-service experiment was never a justification to add hierarchy,
material priors, permanent one-SPP floors, CPU readback, global sorting, or serial image-wide
allocation. Better score correlation alone was not acceptance; actual equal-path image error and
separate equal-time results were the relevant measures. The old prescription to preserve compact
roots and unchanged `CSMain` applied only to the retired implementation.

Related accounting failure evidence is retained in
[Accounting Failure History](19-adaptive-scheduler-accounting-repair.md); later Welford results are
in [Welford Recovery History](24-welford-scheduler-recovery-plan.md). None of these historical
results establishes current wavefront accounting, allocation fairness, or quality.
