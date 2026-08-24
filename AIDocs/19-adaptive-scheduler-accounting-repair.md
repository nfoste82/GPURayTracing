# Adaptive Scheduler Accounting Repair

## Status

The current working tree contains an uncommitted full-resolution adaptive-scheduler rewrite. It is
not safe for quality or performance evaluation yet. The bounded-service experiment was reproduced
and capture validation rejected it because the scheduler metadata, emitted work list, and retired
root count diverge.

Do not treat the generated images, heatmaps, or metrics from the failing run as adaptive-quality
evidence. The work list is incomplete while metadata claims a full budget.

## Reproduction

This shortened version of the user-provided capture command reproduces the problem while skipping
the adaptive-off variant:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath "/Users/nic.foster/Projects/GPURayTracing" \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingCompareAdaptiveSampling \
  -rayTracingSkipAdaptiveOff \
  -rayTracingReferenceMetrics \
  -rayTracingDurationSeconds 10 \
  -rayTracingWidth 1024 \
  -rayTracingHeight 1024 \
  -rayTracingAdaptiveBrightnessPriority 1.3 \
  -rayTracingAdaptiveDirectLightPriority 1.3 \
  -rayTracingAdaptiveRoughnessPriority 0.2 \
  -rayTracingCaptureLabel bounded_service_1024_10s \
  -rayTracingOutput "/Users/nic.foster/Projects/GPURayTracing/TestCaptures" \
  -rayTracingScenes "Assets/Scenes/Generated/TeapotMaterials.unity"
```

The failed run is recorded in:

```text
TestCaptures/bounded_service_1024_10s/
/tmp/gpuraytracing-bounded-service-1024-10s.log
```

The capture throws:

```text
Adaptive allocation invariant failed:
requested=1048576
assigned=1048576
guidance=0
workItemPaths=524288
fullResolutionRetired=524288
workItems=1048576
pixels=1048576
```

The equivalent `512x512`, eight-sample smoke run failed with exactly half of the claimed work:

```text
requested=262144
assigned=262144
workItemPaths=131072
fullResolutionRetired=131072
```

## What Changed Before Failure

The current live adaptive route replaced coarse guidance/permanent promotion with three scheduler
kernels:

```text
ClearAdaptiveScheduler
-> CSAdaptiveClassifyGroups
-> CSAdaptiveAllocateGroupBuckets
-> CSAdaptiveAssignGroups
-> CSAdaptiveBuildRootWorkList
-> indirect CSAdaptiveTraceRoot
-> indirect CSAdaptiveResolveRoot
```

Relevant implementation locations:

- `Assets/Scripts/RayTracingCompute.compute`
  - `ClearAdaptiveScheduler`
  - `CSAdaptiveClassifyGroups`
  - `CSAdaptiveAllocateGroupBuckets`
  - `CSAdaptiveAssignGroups`
  - `CSBuildAdaptiveDispatchArgs`
  - `CSAdaptiveBuildRootWorkList`
  - `CSAdaptiveTraceRoot` / `CSAdaptiveResolveRoot`
- `Assets/Scripts/RayTracingShared.hlsl`
  - `AdaptiveSamplingM2`
  - group bucket/demand/grant buffers
  - bucket demand/budget/usage buffers
  - adaptive metadata slots
- `Assets/Scripts/GameManager.cs`
  - adaptive resource allocation/binding/reset
  - `DispatchAdaptiveSampling`
- `Assets/Editor/RayTracingSceneCapture.cs:516-558`
  - capture invariant enforcement

The desired bounded-service policy is sound, but its implementation must be repaired:

```text
Buckets 0-3:   serve each group on a rotating 1-in-4 scheduler epoch
Buckets 4-7:   serve each group on a rotating 1-in-3 epoch
Buckets 8-11:  serve each group on a rotating 1-in-2 epoch
Buckets 12-15: serve each epoch
```

When a bucket has eligible groups, it must receive a positive complete-group quantum when the
global budget permits. This is a bounded-service contract, not a permanent one-path-per-pixel floor.

## Exact Invariants

The scheduler must not write a claimed value until it has derived the matching actual value. On every
frame, including reused schedules, the following must hold:

```text
requestedPaths
== assignedPaths
== fullResolutionPaths
== sum(actual group grants)
== sum(compact workItem.paths)
== root dispatch count
== retired root paths
```

Additional invariants:

```text
guidancePaths == 0
activeWorkItems == number of pixels with workItem.paths > 0
each active compact work item maps to one valid, unique pixel
inactive pixels must have workItem.paths == 0 and must not resolve/write accumulation state
sum(bucket admitted paths) == fullResolutionPaths
sum(bucket budgets) == requestedPaths
bucket admitted paths <= bucket budgets <= bucket eligible demand
overflow == 0
```

Do not set `requestedPaths` to the nominal uniform frame budget if the scheduler cannot produce
that many real roots. For the intended fixed-budget renderer, the correct repair is instead to make
the bucket allocator and group grants exactly consume that nominal budget.

## Likely Failure Mechanism

The observed 50% result is consistent with mismatch between global nominal budget and actual group
grants. Inspect these first:

1. `CSAdaptiveAllocateGroupBuckets` currently publishes `requestedPaths = width * height * passes`
   independent of actual bucket demand/budget.
2. `CSAdaptiveAssignGroups` atomically reserves a group demand from a bucket budget. It can produce
   `extra = 0` for many groups, leaving work-list entries with zero samples.
3. The work list remains physically one entry per pixel, but `workItemCount` is set to all pixels.
   That is valid only if zero-sample entries are excluded from compact accounting and resolve.
4. `CSBuildAdaptiveDispatchArgs` currently dispatches resolve for `workItemCount`, so zero-sample
   items must explicitly early-return before divide-by-zero/state writes. This does not make them
   compact, and capture accounting must only sum actual work.
5. Bucket reservations should operate in complete group-update quanta, never arbitrary root counts
   that can leave partial policy states or underuse the budget.
6. A final leftover distribution is required. A high-to-low `min(remaining, demand)` pass can leave
   unused roots if all eligible demand is capped. Do not silently claim that remainder as assigned.

The immediate task is not to tune brightness/direct-light/roughness priorities. The legacy command
line priors are no longer authoritative in the new full-resolution allocator and should not be used
to explain an accounting mismatch.

## Required Repair Design

Use full 8x8 group updates as the allocation quantum. A group costs its true valid-pixel count at
one path per selected pixel; partial edge groups use their actual count.

1. Build per-group data in parallel:

```text
bucket
eligible this epoch
validPixelCount
requested group updates, bounded by per-pixel cap
```

2. Build bucket totals from eligible complete group quanta.

3. Use a constant-sized 16-bucket allocator to reserve a positive quantum for every non-empty,
eligible bucket when possible. Then allocate remaining quanta by bucket weight. Use largest-remainder
rounding if fractional weights are used.

4. Select groups in each bucket through a deterministic rotating rank/cursor, not raw GPU atomic
arrival order. The existing atomic-order behavior is only an intermediate implementation and can
cause unstable spatial service. A block prefix scan or a bucket-local rotating permutation is needed.

5. Derive actual `groupGrant` first. Compute all metadata from the sum of those grants, not the
nominal budget. If the policy cannot consume the full nominal budget because of caps, redistribute
the leftover to eligible groups in another bounded pass. Do not discard it.

6. Compact only active pixels, or maintain a full pixel-indexed request buffer plus a separate active
flag/prefix scan. The final `AdaptiveWorkList` consumed by resolve and capture diagnostics must have
exactly one entry per active pixel and no zero-path entries.

7. Build root offsets from actual compact per-pixel path counts. Ensure root dispatch arguments use
the same total.

8. For reused frames, preserve the exact completed compact schedule and all metadata associated with
it. Do not clear/recompute only a subset of counters.

## Do Not Reintroduce

- Coarse guide paths that do not contribute to full-resolution accumulation.
- A permanent per-pixel floor every frame. It prevents one-pass adaptive redistribution.
- High-bucket cutoff allocation with no lower-tier reservation/rotation.
- CPU readback, global sorting, or image-sized single-thread loops in interactive scheduling.
- Metadata that reports nominal paths rather than actual scheduled roots.

## Required Tests

Add behavior-level tests before relying on captures:

1. CPU reference scheduler tests:
   - exact budget conservation;
   - bucket reservations;
   - no eligible bucket receives zero when the budget can cover all reservations;
   - rotating 1/2/3/4 epoch service;
   - partial 1x1, 3x5, 13x7, and 17x19 images;
   - group and per-pixel cap behavior;
   - all equal scores / all zero variance;
   - rotating tie order.
2. GPU probe:
   - controlled group bucket/eligibility/demand inputs;
   - compare group grants, work-list items, root offsets, and metadata to the CPU reference;
   - assert no zero-path compact work item;
   - assert `sum(workItem.paths) == assigned == retired`.
3. Existing controlled root-trace parity:
   - preserve deterministic `oldPixelPathCount + localSample` indexing;
   - compare RGB Welford M2 and beauty means with a CPU/reference path.
4. Capture smoke tests:
   - 512x512, eight fixed samples;
   - 1024x1024, 10 seconds, adaptive-on only;
   - only then run the full 60-second on/off reference-metric command.

## Validation Commands

Run these in order. Close other Unity instances using the project first.

### 1. Metal precompile

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath "/Users/nic.foster/Projects/GPURayTracing" \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -logFile /tmp/gpuraytracing-adaptive-scheduler-repair-precompile.log
```

### 2. Focused EditMode tests

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath "/Users/nic.foster/Projects/GPURayTracing" \
  -runTests \
  -testPlatform EditMode \
  -testFilter "GPURayTracing.Tests.RayTracingComputeRegressionTests.Adaptive" \
  -testResults /tmp/gpuraytracing-adaptive-scheduler-repair-tests.xml \
  -logFile /tmp/gpuraytracing-adaptive-scheduler-repair-tests.log
```

### 3. Fixed-sample accounting smoke

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath "/Users/nic.foster/Projects/GPURayTracing" \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingCompareAdaptiveSampling \
  -rayTracingSkipAdaptiveOff \
  -rayTracingSamples 8 \
  -rayTracingWidth 512 \
  -rayTracingHeight 512 \
  -rayTracingCaptureLabel adaptive_scheduler_repair_smoke \
  -rayTracingOutput "/Users/nic.foster/Projects/GPURayTracing/TestCaptures" \
  -rayTracingScenes "Assets/Scenes/Generated/TeapotMaterials.unity"
```

### 4. Ten-second adaptive-only reference check

Use the reproduction command above, changing the label after the accounting smoke passes.

## Compact Future-Session Prompt

```text
Read AIDocs/00-index.md, AIDocs/17-adaptive-sampling-continuation.md, and
AIDocs/19-adaptive-scheduler-accounting-repair.md. Fix the uncommitted full-resolution adaptive
scheduler, starting with exact GPU accounting, not image quality. The reproduced 1024x1024 10-second
capture fails because metadata claims 1,048,576 roots while compact work items and retired roots total
524,288. Implement a low-overhead GPU group scheduler with bounded rotating low-priority service
(1-in-2/3/4 epochs), positive reservations for every eligible bucket, complete group-update quanta,
deterministic rotating group selection, compact active-pixel work items only, and exact fixed-budget
conservation: requested == assigned == group grants == work-item paths == root count == retired.
Preserve RGB Welford statistics, deterministic sample indices, indirect root tracing, and CSMain as
the uniform baseline. Do not use CPU readback, global sort, image-sized serial allocation, permanent
per-pixel floor, or hard-cutoff starvation. Add CPU/GPU scheduler parity tests, precompile Metal, run
focused tests, then run the documented 512x512 smoke and 1024x1024 10-second adaptive-only capture.
Use apply_patch; inspect and preserve unrelated work; do not commit.
```
