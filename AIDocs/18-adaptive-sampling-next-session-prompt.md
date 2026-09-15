# Adaptive Scheduler Prompt History

## Historical / Superseded

The former next-session prompt is superseded by the single authoritative current plan:
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
This file remains to preserve existing references, not as an executable continuation prompt.

The prompt described a preparatory August 2026 milestone: scheduler settings, state-hash plumbing,
reclassification cadence, and preservation of the allocation snapshot existed, while replacement
of coarse-guide/permanent-promotion scheduling was still pending at that time. It contained no
independent benchmark or validation evidence.

Its instructions to implement RGB Welford state, quantile buckets, compact root tracing, and an
unchanged `CSMain` baseline are no longer a current work order. Adaptive rendering now uses the
wavefront route. The historical motivations were low scheduler overhead, deterministic fine
sample indices, fair service, honest accounting, and separation of preview from fine evidence.
Related measurements and limitations are retained in
[Adaptive Sampling History](17-adaptive-sampling-continuation.md).
