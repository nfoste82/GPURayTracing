# Adaptive Sampling History

## Historical / Superseded

This is an August 2026 experimental record, not an implementation handoff. The single authoritative
current plan is [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
The active renderer is queue-driven wavefront; the `CSMain`, compact-root, coarse-guide,
guarded-pixel, and standalone adaptive trace descriptions below refer only to historical routes.
Their validation does not establish current wavefront correctness or performance.

## Retained Constraints

- The objective was lower raw-image reference error at equal traced-path cost and, separately,
  equal wall-clock time, not automatic render stopping.
- Deterministic per-pixel sampling used `oldPixelPathCount + localSampleIndex`; RGB accumulation
  and Welford statistics used actual sample counts in linear HDR.
- Coarse preview is not unbiased full-resolution beauty. Seeding fine RGB/count/M2 with upscaled
  coarse samples risks structured blur and edge, glass, highlight, and caustic bias. History-zero
  experiments separated preview from fine evidence; they did not prove all bootstrap behavior safe.
- A one-path-per-pixel floor consumes an entire one-SPP budget, leaving no budget for redistribution.
  Historical rotating-service experiments were intended to avoid that limitation and starvation.
- Accounting evidence requires actual generated/resolved work, not nominal counters. Equal metadata
  values alone do not prove retirement. Coarse work is an additional cost even when omitted from
  the fine estimator.
- CPU scheduling readback, global sorting, and image-sized serial allocation were rejected as
  interactive overhead. Capture readback and timing fences are diagnostic costs, not free work.
- Temporal denoiser history is not Monte Carlo variance, and denoising does not excuse a raw-beauty
  regression. Score/error correlation and visually plausible heatmaps are not image-quality wins.

## Early Trace Measurements

These captures used different historical settings/builds. Values are retained for provenance, not
as a cross-build ranking or a current throughput baseline.

| Historical Route | Uniform ms/frame / RGB RMSE | Adaptive ms/frame / RGB RMSE | Artifact Root |
| --- | --- | --- | --- |
| Local in-kernel allocator | 715.525 / 0.01164 | 896.792 / 0.02435 | `/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/adaptive-captures/` |
| Capacity-sized guarded compact list | 599.352 / 0.00990 | 1268.652 / 0.03194 | `/tmp/gpuraytracing-adaptive-worklist-captures/` |
| Indirect compact list | 678.578 / 0.01215 | 766.042 / 0.02229 | `/tmp/gpuraytracing-adaptive-indirect-captures/adaptive_indirect_30s/TeapotMaterials/` |

The parallel root-list diagnostic used `512x512` TeapotMaterials, deterministic one-pass final
color, no temporal denoising, and ten seconds. Uniform completed 51 frames at `198.440 ms/frame`
and `13,369,344` paths; adaptive completed 43 at `235.467 ms/frame` and `11,272,192` paths.
Adaptive improved over the preceding sparse-wave implementation (`314.736 ms/frame`) but remained
18.7% slower than uniform. The final list contained `198,528` active pixels and `262,144` roots,
with no overflow. This was an accounting/performance milestone, not a quality pass.
Artifacts: `/tmp/gpuraytracing-adaptive-rootlist-diagnostic/adaptive_rootlist_diagnostic_10s/TeapotMaterials/`.

## Accounting Failure (August 20, 2026)

The pre-repair 60-second `1024x1024` TeapotMaterials diagnostic reported `1,048,576` requested
paths but only `58,933` assigned/retired, zero overflow, and `630,124` pixels still in bootstrap.
Its apparent speedup (`222.303` adaptive versus `603.854` uniform ms/frame) was under-allocation,
not a throughput win. RGB RMSE was `0.06245491` versus uniform `0.01220348`; that invalid run
cannot establish allocation quality at matched work. Bucket diagnostics were also untrustworthy.
Artifacts: `/tmp/gpuraytracing-adaptive-indirect-captures/adaptive_diagnostics_60s/TeapotMaterials/`.

Later historical smoke tests reported `91` requested/assigned/work-item/retired paths at `13x7`
and `262,144` at `512x512`, with zero overflow. Those results applied to that allocator only.
A separate half-budget failure is retained in [Accounting Failure History](19-adaptive-scheduler-accounting-repair.md).

## Equal-Path Regression (August 20, 2026)

`TestCaptures/adaptive_rootlist_reference_30s_2/TeapotMaterials/` compared `1024x1024` candidates
with exactly 28 frames and `29,360,128` retired paths each:

| Candidate | ms/frame | RGB RMSE | RGB PSNR | Luminance Relative MAE |
| --- | ---: | ---: | ---: | ---: |
| Uniform | 1071.763 | 0.02612 | 31.66 dB | 0.14544 |
| Adaptive | 1097.554 | 0.03708 | 28.62 dB | 0.13623 |

Adaptive's 42.0% higher RGB RMSE at equal work ruled out throughput as the sole explanation.
The final schedule served `499,776` of `1,048,576` pixels, leaving 52.3% unserved for the
eight-frame reuse window. Median accumulated count was 16 versus uniform's 28; maximum was 88.

The historical diagnosis identified block-maximum reduction amplifying outliers, hard bucket
cutoff and fixed row-major admission, an exploration term affecting diagnostics but not service,
and a mismatch between ACES confidence priority and linear-RGB acceptance. Firefly/non-finite
handling was also flagged. These were findings about the old kernels, not a current defect list.
The stronger attribution to individual causes was not isolated by the equal-path result itself.

The block maximum was changed to a valid-pixel mean on August 20. Isolation runs recorded the
same 11 failures before/after that edit in the WIP tree: eight allocator parity failures plus
three caustics/glare failures, compared with three failures in the committed baseline. The planned
post-edit quality capture was not performed in that session, so the 42.0% figure is not a result
for the mean-reduction change or the current renderer.

Additional user-provided `1024x1024`, one-pass duration evidence:

| Capture | Uniform Paths / RGB RMSE | Adaptive Paths / RGB RMSE |
| --- | --- | --- |
| `adaptive_rootlist_reference_30s_3` | 61,865,984 / 0.01533497 | 40,894,464 / 0.03533929 |
| `adaptive_rootlist_reference_45s` | 82,837,504 / 0.01262559 | 54,525,952 / 0.02615932 |

Both are under `TestCaptures/<capture>/TeapotMaterials/`. They rejected those historical
duration candidates, not every subsequent Welford policy.

## Group And Guide Experiments (August 21, 2026)

The early group-scheduler smoke (`TestCaptures/group_scheduler_smoke_fixed/TeapotMaterials/`)
produced `4096` guide paths and zero fine paths on its first reclassification at `512x512`.
Three frames did not exercise promotion and were not a fine-quality comparison.

A later fixed-budget guide experiment spent 64 guide rays per unpromoted group, then one fine
ray per pixel after promotion. `TestCaptures/group_scheduler_fixed_budget_smoke/TeapotMaterials/`
reported `262,144` total paths on all five frames: guide/fine counts were `262144/0` for frames
1-3, `222848/39296` for frame 4, and `174144/88000` for frame 5. It demonstrated that version's
budget use and mixed promotion, but averaged `777.671 ms/frame` because each coarse invocation
serialized 64 traces. The old parallel-guide prescription and permanent-promotion design are
superseded, not pending implementation.

## Dammertz Experiment (August 21-22, 2026)

The split estimator compared all-sample RGB with a deterministic alternating-sample subset,
updated by sample-index parity rather than frame parity. It was scheduling-only state, not
beauty. At eight frames and `8,388,608` paths per candidate, Welford RGB RMSE was `0.05217450`
and Dammertz `0.05215506` (0.037% better). All groups still received 64 paths, so this did not
demonstrate redistribution. Focused historical Metal tests passed `25/25`.

The useful post-bootstrap 60-second result rejected the fixed-8x8 split policy: Dammertz was
26.9% worse than uniform RGB RMSE and retired 23.9% fewer paths. Its weak score signal became a
zero-versus-four-path cutoff serving only 25% of groups. Full results and the rejected-approach
conclusion are retained in [Dammertz History](20-dammertz-adaptive-sampling-plan.md).

## Interpretation And References

Yining Karl Li's [adaptive sampling article](https://blog.yiningkarlli.com/2015/03/adaptive-sampling.html)
describes rejected local contrast schemes and a hierarchical Dammertz method reporting 42.9%
sample reduction in its own scene. That external result does not validate this project's fixed
8x8 split experiment or justify restoring hierarchy as an approved implementation task.

For ideal allocation with known independent per-pixel variances and a fixed total budget,
`MSE_optimal / MSE_uniform = mean(sigma)^2 / mean(sigma^2) = 1 / (1 + CV^2)` illustrates the
available headroom. The historical display-uncertainty distribution was not an empirical proof
of that ceiling, and confidence-driven allocation can itself introduce sampling bias.

Later substantive evidence is retained in [Welford Recovery History](24-welford-scheduler-recovery-plan.md)
and [Trace Throughput History](25-adaptive-trace-throughput-plan.md). Historical acceptance claims,
timing exclusions, state layouts, default values, and `CSMain` preservation requirements do not
carry over to wavefront. Unresolved bootstrap, accounting, timing, allocation, memory, and test
coverage findings are governed solely by document 27; this cleanup does not claim they are fixed.
