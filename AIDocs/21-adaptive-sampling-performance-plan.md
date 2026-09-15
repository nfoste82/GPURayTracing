# Adaptive Sampling Performance History

## Historical / Superseded

This records the pre-wavefront performance investigation, not an active optimization plan.
The single authoritative current plan is
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).
The old ordered optimizations and validation commands are superseded. The guarded `CSAdaptiveTrace`
and uniform `CSMain` routes described here are not the active renderer.

## Historical Cost Model

The guarded-pixel milestone dispatched:

```text
clear scheduler -> classify groups -> remap buckets -> guarded full-screen trace
```

A regular 4x4 dispatch read each pixel's 8x8 group allocation, returned on zero, and otherwise
traced that pixel's assigned samples before updating HDR/Welford state. It removed compact-list
construction and indirection without changing the experimental score policy.

Capture-only instrumentation recorded synchronized elapsed times:

```text
schedulerMilliseconds: clear/classify/remap
traceMilliseconds: guarded full-screen CSAdaptiveTrace
resolveMilliseconds: zero (historical CSV compatibility field)
```

Each phase was fenced with `AsyncGPUReadback` on a metadata buffer. These were intentionally
expensive synchronized measurements, not pure GPU timestamp measurements or ordinary interactive
frame rates. Reclassification and reuse frames were reported separately. Historical assertions
that particular diagnostics were outside a render stopwatch do not establish clean current timing;
the current audit's timing concerns are tracked in document 27.

## Retained Evidence And Interpretation

The substantive measured results are retained in
[Welford Recovery History](24-welford-scheduler-recovery-plan.md): scheduler fences of
`1.245-1.424 ms` versus trace `549.0-684.8 ms` in Sponza did not support blaming scheduler cost
alone. A later TeapotMaterials fused capture attributed `45.7 ms/frame` to scheduler fences and
`1195.6 ms/frame` to tracing. These are different instrumented captures, not comparable isolated
kernel timings.

At `1024x1024`, two rotated-order compact-versus-guarded runs averaged `1179.7` versus
`987.3 ms/frame`, a 16.3% guarded improvement at near-equivalent work. The compact route was
removed at that milestone. Later layered and macrotile evidence is retained in
[Trace Throughput History](25-adaptive-trace-throughput-plan.md).

The lasting measurement distinctions are allocation quality at matched actual paths versus
end-to-end quality at matched time, bootstrap versus established scheduling, and diagnostic versus
uninstrumented timing. Deterministic sample sequences, honest accounting, and unchanged estimator
behavior were prerequisites for attributing a speed difference to dispatch shape. Neither the
historical counter equality nor these timings prove the active wavefront route meets those conditions.
