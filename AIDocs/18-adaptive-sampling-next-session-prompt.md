# Adaptive Sampling Next-Session Prompt

Read `AIDocs/00-index.md`, then read `AIDocs/17-adaptive-sampling-continuation.md`, especially the latest implementation-status and replacement-plan sections. Continue the adaptive-sampling implementation from the current working tree; do not assume the old bucket allocator or coarse-guide design is production-ready.

The completed preparatory work is limited to configurable scheduler settings, accumulation-hash plumbing, reclassification cadence, and preservation of the current `AdaptiveGroupInfo` allocation snapshot during classification. The substantive replacement is still pending.

Implement the remaining scheduler as a cohesive, measured change:

1. Replace coarse guide/permanent-promotion behavior with full-resolution 8x8 group scheduling. Keep `CSMain` unchanged as the uniform baseline.
2. Add full-resolution RGB Welford statistics, or another state layout that directly supports linear-RGB RMSE. Use expected MSE reduction or standard error as the primary score; use statistically normalized recent mean change only as a bounded secondary boost. Do not infer convergence direction from the sign of mean changes.
3. Recompute priorities at a configurable cadence with low overhead. Use GPU group reductions, a small 256-bin histogram, prefix scan, and rank-based mapping to 16 quantile buckets. Avoid sorting, CPU readback, and image-sized serial loops.
4. Ensure bucket populations remain broadly distributed even when all absolute errors shrink or scores tie. Apply bounded bucket hysteresis only after quantile assignment, and report target versus final populations.
5. Preserve an exploration floor and rotating group/pixel service so no group or pixel is permanently starved. Allocate the fixed frame budget exactly, with no cutoff that silently drops work. Support partial edge groups.
6. Replace the current one-thread `CSAdaptiveAllocateGroups` path and any full-capacity compaction dispatches with parallel scans/compaction and indirect dispatch where practical. Keep expensive path tracing dominant and scheduler overhead small; benchmark classification intervals rather than hiding overhead with a long interval.
7. Preserve deterministic per-pixel sample indexing, unbiased full-resolution accumulation, finite-radiance handling, and exact accounting:

   `requested == assigned == work-list paths == root paths == retired paths`

8. Add behavior-level CPU/GPU parity tests for score calculation, Welford updates, quantile/tie handling, hysteresis, exact bucket budgets, fairness rotation, partial dimensions, and accounting. Replace obsolete source-string tests instead of preserving misleading guide/promotion assertions.
9. Add capture diagnostics for priority/bucket populations, bucket budgets and retired paths, demand distribution, exploration age, scheduler timings, and root-list utilization.
10. Precompile Metal, run focused tests, run odd-size accounting smoke tests, then run equal-retired-path quality comparisons before equal-wall-time captures. Compare linear-RGB RMSE and verify the scheduler does not add material frame overhead.

Work incrementally, inspect existing modifications before editing, use `apply_patch`, and do not revert unrelated user changes. If the complete replacement cannot be finished safely in one session, implement only a coherent validated milestone and document the remaining work in `AIDocs/17-adaptive-sampling-continuation.md`.
