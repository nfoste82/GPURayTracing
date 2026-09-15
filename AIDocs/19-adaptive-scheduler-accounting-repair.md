# Adaptive Scheduler Accounting Failure History

## Historical / Superseded

This records an August 2026 bounded-service experiment, not the current renderer's repair plan.
The single authoritative current plan is
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
The old repair design, command sequence, and continuation prompt have been retired; the file is
retained because its reproduced failure is substantive evidence and existing links use this path.

## Reproduced Failure

A ten-second `1024x1024` adaptive-only TeapotMaterials reference capture rejected the experiment:

```text
requested=1048576
assigned=1048576
guidance=0
workItemPaths=524288
fullResolutionRetired=524288
workItems=1048576
pixels=1048576
```

Artifacts: `TestCaptures/bounded_service_1024_10s/` and
`/tmp/gpuraytracing-bounded-service-1024-10s.log`.
An eight-sample `512x512` smoke reproduced exactly half of the claimed budget:
`262144` requested/assigned versus `131072` work-item/retired paths.
The original command also supplied legacy brightness/direct-light/roughness priorities
`1.3/1.3/0.2`; those flags were not authoritative in the rewritten allocator and did not explain
the accounting mismatch.

These runs are failure evidence, not adaptive-quality or throughput comparisons. Images and
heatmaps cannot validate a scheduler whose emitted work is incomplete while metadata claims a
full budget.

## Historical Mechanism

That route classified full-resolution 8x8 groups, allocated bucket budgets, assigned group grants,
expanded roots, and indirectly traced/resolved them. The observed 50% deficit was consistent with
nominal global budget publication diverging from actual grants and a physically pixel-sized list
being counted as fully active despite zero-sample entries. This was the recorded hypothesis, not
an isolated proof of the exact defective operation.

The abandoned repair prescription specified complete-group quanta, rotating 1-in-2/3/4-epoch
service, positive bucket reservations, leftover reconciliation, and active-pixel compaction.
Those details are not requirements to restore the retired root-list architecture. The later
history contains accounting passes for subsequent implementations, but neither those passes nor
this failure establish the current wavefront retirement count.

## Retained Lessons

- Requested, assigned, emitted, generated, and retired work are distinct observations. Publishing
  the same nominal value into multiple counters is not conservation evidence.
- Actual group grants and compact item path totals were the historical accounting cross-checks.
  Zero-path entries could not be counted as active or allowed to resolve invalid estimator state.
- Reused schedules needed matching retained metadata; clearing only some counters could make
  reuse frames disagree with their actual work.
- Partial edge groups cost their true valid-pixel count. Overflow, dropped work, and capped demand
  could not be hidden by reporting the intended full budget.
- GPU-native scheduling, deterministic sample indices, and unbiased fine accumulation were
  correctness constraints; preserving `CSMain` and compact root buffers was architecture-specific.

Current accounting and validation gaps remain tracked in document 27. No current fix or new test
result is asserted by this historical consolidation.
