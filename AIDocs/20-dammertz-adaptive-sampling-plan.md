# Dammertz Adaptive Sampling Next Plan

## Current Result

Read `17-adaptive-sampling-continuation.md` first. The validated compact full-resolution group
scheduler is correct for accounting and must not be structurally rewritten. The initial
fixed-8x8 Dammertz split estimator is implemented and capture diagnostics are available, but the
`dammertz_three_way_60s_4` run rejects its current allocation policy.

```text
1024x1024 TeapotMaterials, 60 seconds
adaptive_off:       114,294,784 paths, RGB RMSE 0.00853382
adaptive_welford:   105,906,176 paths, RGB RMSE 0.00958030
adaptive_dammertz:   87,031,808 paths, RGB RMSE 0.01083300
```

Dammertz's score/error Spearman is better than Welford (`0.3022` versus `0.1790`), but the final
allocation-to-reference-error Pearson correlation is only approximately `0.079`. The scheduler
serves 4,100 of 16,384 groups, granting nearly exactly 256 paths to each served full group and
zero to the other 75%. It over-serves bright upper checkerboard/diffuse groups without a matching
reference-error benefit.

## Next Isolated Milestone

Keep the Dammertz score and Welford A/B mode unchanged for this step. Modify only the existing GPU
group-service allocation policy so it distributes multiple bounded complete-group quanta instead
of expressing a weak score as a hard 0-or-4-path group cutoff.

The intended shape is:

```text
all groups remain eligible through bounded rotation
-> a broad set receives a first complete group quantum
-> higher-score groups receive bounded extra complete group quanta
-> compact only active pixels
-> expand roots and indirectly trace exactly the compact allocation
```

Do not add a permanent per-pixel floor. This is a rotating group-level service policy, not uniform
sampling in disguise. Do not use CPU readback, a global sort, or an image-sized serial GPU loop.

## Invariants

Every reclassification and reuse frame must preserve:

```text
requested == assigned == compact work-item paths == root paths == retired
guidance paths == 0
work-list overflow == 0
each compact work item is active, unique, and valid
```

Preserve deterministic sample indexing:

```text
sampleIndex = oldPixelPathCount + localSample
```

`CSMain` must remain unchanged when adaptive sampling is disabled.

## Evidence Required

Use the existing capture output:

```text
adaptive_variant_comparison.csv
adaptive_group_diagnostics.csv
adaptive_allocation_heatmap.png
adaptive_*_vs_reference_difference.png
```

Before judging fixed-duration performance, run an equal-retired-path comparison. The next policy
is promising only if it improves the per-group service/error relationship:

1. More than 25% of groups receive paths on a representative post-bootstrap schedule.
2. Served groups are not systematically brighter than unserved groups unless actual reference RGB
   RMSE is also higher.
3. Assigned paths have materially stronger correlation with actual group RGB RMSE than `~0.079`.
4. Dammertz score/error correlation remains no worse than Welford.
5. Equal-path RGB RMSE improves before fixed-duration quality is considered.

Do not implement hierarchy until these fixed-8x8 criteria are met.

## Validation Order

1. Add CPU reference tests for the changed group quantum allocation: exact budget, ties, rotation,
   complete-group/partial-group quanta, broad service, and zero priorities.
2. Add GPU parity probes for grants, compact list, root offsets, metadata, and exact retire count.
3. Precompile Metal.
4. Run focused adaptive EditMode tests.
5. Run 512x512 three-way fixed-frame accounting smoke.
6. Run 1024x1024 three-way equal-retired-path reference capture.
7. Inspect the CSVs and images.
8. Only then run fixed-duration three-way comparisons, preferably alternating order across trials.

## Compact Future-Session Prompt

```text
Read AGENTS.md, AIDocs/00-index.md, AIDocs/17-adaptive-sampling-continuation.md,
AIDocs/19-adaptive-scheduler-accounting-repair.md, and AIDocs/20-dammertz-adaptive-sampling-plan.md.
Continue the uncommitted adaptive scheduler work without reverting unrelated changes. Do not alter
the validated compact scheduler's full-resolution allocation infrastructure, compaction, indirect
root tracing, or accounting except where required to make the next bounded group-service policy
correct. The fixed-8x8 Dammertz score is implemented but the 60-second capture
TestCaptures/dammertz_three_way_60s_4 shows a harmful 0-or-4-path group cutoff: only 25% of groups
are served, bright diffuse upper groups are over-served, allocation-to-reference-error correlation
is ~0.079, and Dammertz is worse than uniform. Keep Welford and Dammertz modes unchanged as A/B
scores. Implement only a GPU-native, bounded, rotating complete-group-quantum service policy that
broadens group service while allowing higher-score groups bounded extra quanta. Preserve exact
requested == assigned == compact work-item paths == root paths == retired accounting, no zero-path
compact item, no CPU readback in interactive scheduling, no global sort, no image-sized serial
kernel, no permanent per-pixel floor, and no hierarchy. Extend CPU/GPU parity tests, precompile
Metal, run focused tests, run 512x512 three-way smoke, then 1024x1024 equal-retired-path reference
comparison. Use adaptive_group_diagnostics.csv to require more than 25% served groups, a stronger
assigned-path/reference-error relationship than ~0.079, and no bright-diffuse service bias before
running duration captures. Use apply_patch; do not commit.
```
