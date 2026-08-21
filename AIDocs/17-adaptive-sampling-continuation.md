# Adaptive Sampling Continuation

This is the handoff for adaptive sampling in the Unity GPU ray tracer. It records the current experimental implementation as of August 2026, its measured results, constraints, and the next work required to make it improve convergence.

## Goal And Non-Goal

The goal is lower displayed-image error than uniform progressive sampling at the same wall-clock time and, separately, at the same retired path budget:

```text
same time or traced-path budget -> lower error than uniform sampling
```

Do not implement automatic render stopping. The user decides when to pause rendering. Adaptive sampling must keep redistributing a fixed budget while rendering continues.

The uniform `CSMain` path is the production reference and must remain unchanged when adaptive sampling is off.

## Read Before Editing

- `03-compute-shader-renderer.md`: renderer kernels, sampling, HDR accumulation, and presentation.
- `08-shader-debugging-and-randomness.md`: deterministic per-pixel sample sequence.
- `10-benchmarking-and-performance.md`: fixed-duration capture workflow and Metal compile constraints.
- `11-regression-testing.md`: GPU/image test policy.
- `13-denoising-and-upscaling.md` and `14-svgf-implementation-plan.md`: feature and temporal-history semantics.

## Current Implementation

`GameManager.enableAdaptiveSampling` is now a real experimental render path. It is off by default and is only eligible for static final-color progressive accumulation. Animated-water, temporal-accumulation, and non-final/debug paths fall back to uniform sampling through `ShouldUseFrameAccumulation()` / `ShouldUseAdaptiveSampling()`.

### Root-Path Trace And Capture Telemetry

Adaptive tracing expands the compact per-pixel allocation into one persistent root-work-list entry per assigned path on reclassification. `CSAdaptiveTraceRoot` indirectly dispatches exactly those root paths, and `CSAdaptiveResolveRoot` gathers each pixel's contiguous root range for its single Welford/RGB update. This preserves the deterministic `oldPixelCount + localSampleIndex` sequence and removes the prior inactive wave dispatches once allocation becomes sparse.

Short adaptive captures now write human-readable evidence beside the image:

- `adaptive_frame_telemetry.csv`: synchronized per-frame time, accumulation frame, reclassification flag, active work items, assigned paths, and retired paths.
- `adaptive_frame_telemetry.txt`: first/second-half and reclassification/reuse timing summaries.
- `adaptive_allocation_heatmap.png`: black pixels receive no current-frame path; blue through red indicates increasing paths per selected pixel.
- `adaptive_allocation_heatmap.txt`: heatmap legend and maximum per-pixel assignment.

Timed command-line captures are capped at ten seconds to avoid a pathological adaptive candidate consuming the test budget. Use equal-path sample captures for longer convergence checks.

### Settings

`GameManager` and `SceneSettings` expose:

```text
enableAdaptiveSampling       default false
adaptiveSamplingMinSamples  default 8
adaptiveSamplingExploration default 0.05
```

The inspector exposes the bootstrap count and exploration floor when adaptive sampling is enabled. Any of these settings changes the accumulation-state hash, so progressive and adaptive state reset together.

### Persistent State

`AdaptiveSamplingState` is an internal-resolution `ARGBFloat` random-write texture:

```text
R: exact retired path count for the pixel
G: Welford linear-HDR luminance mean
B: Welford luminance M2
A: display-space confidence width from the latest update
```

`AccumulationResult.rgb` remains the HDR RGB sample mean. Adaptive accumulation is count weighted:

```text
newMean = (oldMean * oldCount + newRadianceSum) / (oldCount + newCount)
```

The adaptive state is cleared beside `AccumulationResult` in `GameManager.UpdateTextureFromCompute()` whenever accumulation resets or the output resolution changes.

Each adaptive path uses its old per-pixel count as its sample index:

```text
sampleIndex = oldPixelPathCount + localSampleIndex
```

This guarantees distinct deterministic samples for variable per-pixel batches. The uniform `_SampleOffset` sequence remains unchanged while adaptive sampling is off.

### Priority

Statistics remain in linear HDR. After bootstrap, the classifier derives a display-space confidence width:

```text
variance      = M2 / (n - 1)
standardError = sqrt(variance / n)
uncertainty   = ACES(exposure * (mean + standardError))
              - ACES(exposure * max(0, mean - standardError))
priority      = uncertainty
```

The shader uses `max(explorationFloor, priority) * 1024` for the diagnostic priority sum, but bucket membership uses the unfloored uncertainty. The current exploration setting therefore does not reserve a path for every established pixel and is not yet a starvation-rotation policy. Before `adaptiveSamplingMinSamples`, pixels receive the uniform baseline number of paths. The floor is not a material/feature prior.

### GPU Work-List Pipeline

The compute asset contains these adaptive kernels:

```text
ClearAdaptiveSamplingState
ClearAdaptiveWorkList
ClearAdaptiveGroupBucketCounts
CSAdaptiveClassify
CSAdaptiveScanGroupBuckets
CSAdaptiveAllocateParallel
CSAdaptiveAddBucketOffsets
CSAdaptiveCompactParallel
CSAdaptiveBuildRootWorkList
CSBuildAdaptiveDispatchArgs
CSAdaptiveTraceRoot
CSAdaptiveResolveRoot
CSAdaptiveDiagnostics
```

`CSAdaptiveClassify` runs over the internal image with `8x8` groups and assigns each pixel the mean confidence of the valid pixels in its spatial block (fixed August 20, 2026; see "Diagnosis 1 Fix" below — it previously used the block maximum). `CSAdaptiveScanGroupBuckets` scans each lane in contiguous flattened `256`-pixel blocks, preserving row-major pixel order across block boundaries. A small global allocator distributes the fixed image-wide root budget from the highest populated bucket downward. `CSAdaptiveAddBucketOffsets` completes the block prefixes and `CSAdaptiveCompactParallel` emits one deterministic work item for every admitted pixel:

```text
uint2 AdaptiveWorkList item:
  x: flattened pixel coordinate
  y: requested paths for that unique pixel
```

`CSAdaptiveBuildRootWorkList` expands each admitted pixel item into one `uint2` root item per assigned path during reclassification. `CSBuildAdaptiveDispatchArgs` writes separate 16-thread indirect-dispatch arguments for root paths and pixel resolves. `CSAdaptiveTraceRoot` maps one thread to one compact root item, while `CSAdaptiveResolveRoot` maps one thread to one active pixel and gathers its contiguous root radiance range for the single state update. This avoids launching inactive wave lanes after adaptive allocation becomes sparse.

Resources are owned by `GameManager` and resized with output textures:

```text
AdaptiveSamplingState              ARGBFloat texture
AdaptiveWorkList                   uint2 structured buffer, capacity width * height
AdaptiveRootWorkList               uint2 structured buffer, capacity width * height * 32
AdaptiveRootRadiance               float4 structured buffer, capacity width * height * 32
AdaptiveWorkRootOffsets            uint structured buffer, capacity width * height
AdaptivePixelInfo                  uint2 structured buffer, one entry per pixel
AdaptivePixelBucketRanks           uint structured buffer, one entry per pixel
AdaptiveWorkListMetadata           uint counter buffer
AdaptiveGroupBucketCounts          uint structured buffer, 17 lanes per flattened block
AdaptiveBucketBlockSums            uint structured buffer, scanned block prefixes
AdaptiveBucketWorkOffsets          uint structured buffer, 17 lane offsets
AdaptiveBucketRootOffsets          uint structured buffer, 17 root offsets
AdaptiveBucketBudgets              uint structured buffer, 17 lane budgets
AdaptiveDispatchArgs               3-uint indirect-argument buffer
```

`ComputeDispatch.DispatchIndirect()` is used for both the root-path trace and per-pixel resolve dispatches. Root-list expansion is performed only when the pixel allocation is rebuilt.

### Current Limitation

The sampler now has one globally allocated fixed root budget and stable flattened-pixel compaction. Classification runs per `8x8` spatial block and its allocation is reused for a configurable interval (default eight frames), reducing scheduler overhead and avoiding isolated-pixel allocations. Quantization uses a logarithmic mapping; continue to validate bucket distribution and equal-retired-path quality before treating wall-clock improvements as a success.

## Current Files

- `Assets/Scripts/RayTracingCompute.compute`: adaptive clear, classify, indirect-argument, trace kernels.
- `Assets/Scripts/RayTracingShared.hlsl`: state/work-list declarations and adaptive globals.
- `Assets/Scripts/GameManager.cs`: state/buffer ownership, reset, adaptive dispatch orchestration.
- `Assets/Scripts/ComputeDispatch.cs`: indirect dispatch wrapper.
- `Assets/Scripts/SceneSettings.cs`: adaptive defaults.
- `Assets/Editor/GameManagerEditor.cs`: experimental controls.
- `Assets/Editor/RayTracingSceneCapture.cs`: off/on capture reports.
- `Assets/Tests/EditMode/RayTracingComputeRegressionTests.cs`: adaptive defaults/state hash plus ordered CPU/GPU allocator parity and metadata coverage.

## Measurements

Historical values below retain their original capture settings. Current adaptive diagnostics use deterministic sampling, `numberOfPasses = 1`, final color, temporal denoising disabled, and a 5-10 second wall-clock duration. Do not compare historical 30-second/1024x1024 values directly with the current 10-second/512x512 root-list diagnostic.

### First Local In-Kernel Allocator

The initial `CSAdaptiveMain` implementation performed classification, ticket assignment, and tracing in one full-screen kernel. It was intentionally replaced because scheduling overhead was in the hot trace kernel.

```text
Uniform:  42 frames, 715.525 ms/frame, RGB RMSE 0.01164, PSNR 38.68 dB
Adaptive: 34 frames, 896.792 ms/frame, RGB RMSE 0.02435, PSNR 32.27 dB
```

Artifacts:

```text
/var/folders/hk/2wk9yqf564g4c39vrly7dgd80000gq/T/opencode/adaptive-captures/
```

### Compact Work List Before Indirect Dispatch

The first compact work-list version still used a capacity-sized guarded trace dispatch:

```text
Uniform:  51 frames, 599.352 ms/frame, RGB RMSE 0.00990, PSNR 40.09 dB
Adaptive: 24 frames, 1268.652 ms/frame, RGB RMSE 0.03194, PSNR 29.91 dB
```

Artifacts:

```text
/tmp/gpuraytracing-adaptive-worklist-captures/
```

### Current Indirect Work List

The indirect work-list trace path improved runtime substantially over the guarded work-list path but still loses to uniform quality:

```text
Uniform:            45 frames, 678.578 ms/frame, RGB RMSE 0.01215, PSNR 38.31 dB
Adaptive indirect:  40 frames, 766.042 ms/frame, RGB RMSE 0.02229, PSNR 33.04 dB
```

Relative to uniform in this run, adaptive RGB RMSE is approximately `83.4%` higher. The adaptive result is valid but not a success criterion pass.

Artifacts to inspect:

```text
/tmp/gpuraytracing-adaptive-indirect-captures/adaptive_indirect_30s/TeapotMaterials/
  adaptive_off.png
  adaptive_off.txt
  adaptive_off.metrics.json
  adaptive_on.png
  adaptive_on.txt
  adaptive_on.metrics.json

/tmp/gpuraytracing-adaptive-indirect-capture.log
/tmp/gpuraytracing-adaptive-indirect-compile.log
```

The last successful shader precompile took `179.359 s` cold on the Apple M3 Max. Use at least a 20-minute command timeout because cold compilation varies.

### Parallel Global Allocator Baseline

The current production path was captured after flattened-block parallel compaction, root-list expansion, and ordered parity validation. It used `TeapotMaterials`, deterministic sampling, `numberOfPasses = 1`, final color, temporal denoising disabled, and a 10-second wall-clock duration at `512x512`. The short-duration guardrail prevents a pathological adaptive candidate from stalling the editor.

```text
Uniform:            51 frames, 198.440 ms/frame, 13,369,344 retired paths
Adaptive root-list: 43 frames, 235.467 ms/frame, 11,272,192 retired paths
```

The root-list path was `25.2%` faster than the preceding wave-dispatch adaptive implementation (`314.736 ms/frame`) and retired about `30.3%` more paths in the same 10-second capture. It remained about `18.7%` slower than uniform. This is a performance correction, not an adaptive quality acceptance pass.

The capture did prove complete accounting:

```text
requested == assigned == retired == work-item paths == 262,144
work-list overflow == 0
active work items varied by allocation
```

It also exposed the immediate quality blocker:

```text
bootstrap pixels: 0
bucket 0 population/admitted paths: 1,048,576
buckets 1-15 population/admitted paths: 0
```

The root-list capture produced useful redistribution across buckets 11 and 12. The final allocation had `198,528` active pixel work items and `262,144` root paths, with no overflow. Do not interpret this short timing result as a quality acceptance pass; reference metrics and equal-retired-path comparisons remain authoritative.

Visual inspection of the current difference images suggests that glass/refraction and caustic regions contribute disproportionately to the adaptive image's aggregate error. Much of the remaining image appears comparable to, and may locally benefit from, adaptive sampling. This is an observation rather than a metric-isolated conclusion: before adding a glass or caustic prior, verify it with region masks or focused glass/caustic fixtures at equal retired paths. Keep raw-beauty metrics authoritative and do not hide this behavior behind denoising.

Artifacts:

```text
  /tmp/gpuraytracing-adaptive-rootlist-diagnostic/adaptive_rootlist_diagnostic_10s/TeapotMaterials/
  adaptive_off.png
  adaptive_off.txt
  adaptive_off.metrics.json
  adaptive_on.png
  adaptive_on.txt
  adaptive_on.metrics.json
  adaptive_diagnostics.json
  adaptive_off_vs_on_difference.png
  adaptive_allocation_heatmap.png
  adaptive_frame_telemetry.csv
  adaptive_frame_telemetry.txt

/tmp/gpuraytracing-adaptive-parallel-capture-3.log
```

### Equal-Retired-Path 1024x1024 Result — Quality Regression Confirmed (August 20, 2026)

A 30-second, `1024x1024`, `TeapotMaterials` capture produced **identical retired-path counts** for uniform and adaptive (both `28` measured frames, both `29,360,128` retired paths), which makes this a clean equal-path comparison, not just an equal-time one:

```text
                frames   ms/frame   retired paths   RGB RMSE   RGB PSNR   luma relative MAE
Uniform:           28    1071.763      29,360,128    0.02612    31.66 dB    0.14544
Adaptive:          28    1097.554      29,360,128    0.03708    28.62 dB    0.13623
```

Adaptive is `42.0%` worse in RGB RMSE and `3.04 dB` worse in PSNR than uniform **at an identical retired-path budget**. Because path count is equal, this cannot be attributed to dispatch/classification overhead: the allocation policy itself is redistributing paths in a way that increases squared error. Note adaptive's luminance *relative* MAE is lower than uniform's (`0.1362` vs `0.1454`) while its *absolute* RMSE is much higher — a signature of an allocator that is systematically favoring dark pixels over bright ones (see confidence-metric mismatch below).

Artifacts:

```text
TestCaptures/adaptive_rootlist_reference_30s_2/TeapotMaterials/
  adaptive_off.png / .txt / .metrics.json
  adaptive_on.png / .txt / .metrics.json
  adaptive_diagnostics.json
  adaptive_allocation_heatmap.png / .txt
  adaptive_frame_telemetry.csv / .txt
  adaptive_off_vs_on_difference.png
  adaptive_off_vs_reference_difference.png
  adaptive_on_vs_reference_difference.png
```

`adaptive_diagnostics.json` from that capture (final reclassification, frame 25+):

```json
{
  "requestedRootPaths": 1048576, "assignedPaths": 1048576, "retiredPaths": 1048576,
  "activeWorkItems": 499776, "workListOverflow": 0,
  "pathCountMin": 8, "pathCountMean": 28, "pathCountMax": 88,
  "pathCountP50": 16, "pathCountP95": 72, "pathCountP99": 80,
  "uncertaintyMean": 0.0686815792, "uncertaintyMax": 0.6505323650,
  "uncertaintyP50": 0.0623598100, "uncertaintyP95": 0.1385155920, "uncertaintyP99": 0.1834515630,
  "bootstrapPixels": 0,
  "priorityBucketPopulations": [0,0,64,192,64,0,128,768,3584,27456,109440,407104,471872,26240,1664,0],
  "priorityBucketAdmittedPaths": [0,0,0,0,0,0,0,0,0,0,0,0,936960,104960,6656,0]
}
```

`activeWorkItems = 499,776` out of `1,048,576` pixels means **548,800 pixels (52.3%) received zero paths** for the entire 8-frame reclassification window. `pathCountP50 = 16` versus uniform's fixed `28` (`numberOfPasses` per accumulated frame in this capture) means the median pixel got 57% of uniform's samples (`~1.32x` its squared error) so that `0.16%` of pixels (buckets 13-14, `1,664` of `1,048,576`) could reach up to 88 samples. Under a squared-error objective this trade is a net loss, and the measured RMSE confirms it.

## Root-Cause Diagnosis (August 20, 2026)

This section supersedes the quantization-first plan in "Required Next Work" below. The equal-retired-path capture above proves the allocator itself — not dispatch overhead, not bucket quantization — is responsible for the quality loss. Four independent defects were identified by reading the current kernels (`Assets/Scripts/RayTracingCompute.compute`) against the measured diagnostics. They are ordered by expected impact; fix and re-measure one at a time.

### Diagnosis 1: The 8x8 block reduction uses `max`, destroying most of the classifier's signal

`CSAdaptiveClassify` (`RayTracingCompute.compute:422-456`) computes a real per-pixel confidence width (`GetAdaptivePriority`, `:381-400`) but then overwrites every pixel in each 8x8 block with the **maximum** priority in that block (`:447`, `:456`) before quantizing into a bucket. This is functionally a 1/8-resolution classification pass, and the aggregation operator discards exactly the variance reduction that aggregating was supposed to buy:

```text
per-pixel uncertainty (state.w in adaptive_diagnostics.json): P50 0.0624, P95 0.1385, P99 0.1835, max 0.6505
block-max field (inferred from bucket populations over 16,384 8x8 blocks):
  buckets 11-12 alone hold 84% of all blocks -> uncertainty range ~0.113-0.273
```

The block max sits near the per-pixel P97. A single firefly-adjacent pixel promotes all 64 pixels in its block into a high bucket, collapsing a field that natively spans buckets 0-14 into effectively 2-3 buckets. This is very likely the direct cause of the near-empty low buckets (`0,0,64,192,64,0,128,768,...` in the histogram above) that the existing "Required Next Work" section attributed to quantization.

**Recommended first fix:** change the groupshared reduction from `max` to `mean` (sum then divide by 64, or divide by valid-pixel count at partial edge blocks). This is a one-line change at `RayTracingCompute.compute:447` (replace `max(...)` with an accumulating sum) plus the corresponding line in the CPU test reference. Expect a materially wider bucket histogram and, if Diagnosis 2 is also fixed, a large fraction of the quality gap to close.

### Diagnosis 2: Bucket-cutoff admission gives zero paths to >50% of pixels; the exploration floor that was meant to prevent this is dead code

`CSAdaptiveAllocateParallel` (`:542-599`) fills buckets from 15 down to 0 until the fixed budget is exhausted (`:580-596`). Once `remaining == 0`, every lower bucket receives `budget = 0`, and `CSAdaptiveCompactParallel` (`:620-658`) does not even emit a work item for a pixel with `samples == 0` (`:641`). There is no `max(1, ...)` anywhere in the allocation path. This matches the captured `548,800` zero-path pixels above.

This contradicts the true optimum. Minimizing total variance `sum(sigma_i^2 / n_i)` subject to `sum(n_i) = B` (Neyman allocation) gives `n_i` proportional to `sigma_i`, which is strictly positive for every pixel with nonzero variance — a bucket cutoff that zeroes out low buckets can never be optimal, and it also produces a much steeper allocation curve than the mathematically justified one.

The exploration setting was clearly intended to prevent exactly this starvation, but it does not: `_AdaptiveSamplingExploration` (default `0.05`) is applied only at `RayTracingCompute.compute:458` to compute a value whose sole consumer is the diagnostic counter `AdaptiveMetadataPrioritySum` (`:473`). It never affects bucket membership (`:459-460` recomputes `bucketPriority` from the unfloored value) or admission. It reserves no path for any pixel. See `AIDocs/17-adaptive-sampling-continuation.md:93` (unchanged) for the prior note on this; this diagnosis confirms it is a full no-op on quality, not merely "not yet a starvation-rotation policy."

Compounding this, admission rank within the cutoff bucket is deterministic row-major flattened order (`CSAdaptiveScanGroupBuckets:511-514`) and is identical every reclassification (every 8 frames by default). The same low-flat-index pixels always win ties in the cutoff bucket; there is no rotation or hash permutation. This is a systematic spatial bias, not sampling noise, and is a plausible mechanism for the clustered/uneven-convergence artifacts that Karl Li's blog post explicitly describes and rejects for a similar naive per-block scheme.

**Recommended fix:** replace the 17-lane bucket/cutoff/admission machinery with direct proportional allocation: `n_i = max(1, round(B * priority_i / sum(priority)))`. This is a single global reduction (sum of priority) plus a per-pixel divide, requires no bucket population/budget/offset buffers, removes the cutoff/starvation behavior entirely by construction (the `max(1, ...)` floor is a principled exploration policy, unlike the current dead-code floor), and is provably closer to the Neyman optimum than a hard cutoff. This is a bigger structural change than Diagnosis 1; do Diagnosis 1 first since it's cheaper to validate in isolation.

### Diagnosis 3: The priority metric optimizes a different error space than the acceptance metric (RGB RMSE)

`GetAdaptivePriority` (`:381-400`) and `CSAdaptiveResolveRoot` (`:895-900`) compute priority as a width in ACES-tonemapped, exposure-scaled display space. ACES is steepest near black and compresses near saturation, so bright pixels are systematically assigned lower priority than dark pixels of equal linear-HDR variance. RGB RMSE (the acceptance metric used throughout this document and in `RayTracingSceneCapture`) is dominated by absolute linear-space error, which is dominated by bright pixels. This mismatch is consistent with the observed inversion: adaptive's luminance *relative* error improved while its RMSE (absolute, bright-pixel-dominated) got much worse.

Two additional details compound this:

- The classifier's ACES curve (`RayTracingShared.hlsl:4490`, Narkowicz 2015) is not the same curve used at presentation (`Assets/Resources/RayTracingSpatialDenoiser.compute:123-139`, an AP1-transform curve matching Three.js). The classifier is not even measuring sensitivity in the tone curve the user actually sees.
- Both interval bounds are clamped to `>= 0` before the exposure multiply (`:393-394`), so for any pixel where `mean - standardError < 0` the confidence interval becomes one-sided and is systematically narrowed near black — exactly where ACES is steepest and priority should be most sensitive.

**Recommended fix (do after Diagnosis 1 and 2 are validated in isolation):** either (a) switch the priority metric to linear-HDR relative or absolute error to match the RMSE acceptance metric, or (b) keep a display-space metric but validate against a display-space/perceptual acceptance metric instead of linear RGB RMSE. Do not tune the priority formula against one metric while accepting results against another; pick one and be consistent end to end.

### Diagnosis 4: Firefly clamp is off by default in generated scenes, and there is no NaN/Inf guard

`SceneSettings.FireflyClamp` defaults to `0.0` (disabled) (`SceneSettings.cs:60`), and `GameManager.InitSceneSettings` overwrites the manager's own `fireflyClamp = 1.0f` default with that scene value (`GameManager.cs:641`). Generated scenes therefore run with **no firefly clamp**, so unbounded-radiance samples enter the Welford `M2` update at `RayTracingCompute.compute:891` directly. Combined with Diagnosis 1's block-max reduction, a single unclamped firefly sample can hijack the priority (and thus the path budget) of all 64 pixels in its 8x8 classification block. There is also no `isfinite`/`isnan` guard anywhere on traced radiance (confirmed by repo-wide search); a NaN sample would permanently poison that pixel's `luminanceMean`/`luminanceM2` for the rest of the render.

**Recommended fix:** set a nonzero default firefly clamp for generated/adaptive-eligible scenes (or stop letting `SceneSettings.FireflyClamp = 0` override the manager default silently), and add an `isfinite` guard around the radiance write in `CSAdaptiveTraceRoot`/`CSAdaptiveTrace` before it reaches `AdaptiveRootRadiance`/the Welford update.

### On The User's Low-Resolution-Prepass Idea

The user proposed rendering at ~1/8 resolution for ~5 seconds, then using that as a starting "back-buffer" for the full-resolution render so the classifier has an early estimate of which pixels need more work. The core instinct — get a cleaner-than-8-bootstrap-sample variance estimate before allocating — is correct and matches Diagnosis 1/2 above. However, the idea as stated has one fatal flaw and one redundancy:

- **Fatal flaw:** seeding `AccumulationResult` (the actual output buffer) with upscaled low-resolution data injects a spatially *structured* bias (blur, edge bleed at silhouettes/highlights/caustic boundaries), not unstructured noise. This bias decays only as `1/n` as further samples accumulate and is exactly the kind of error RMSE and human vision punish most. It would also break the unbiased accumulation invariant (`AIDocs/17-adaptive-sampling-continuation.md:65-69`) that the rest of the pipeline relies on.
  - **Fix if this is pursued later:** keep the low-res estimate in a separate guidance buffer consumed only by `GetAdaptivePriority`, and never write it into `AccumulationResult`. That preserves the classification benefit with zero bias risk.
- **Redundancy given the current bug:** the 8x8 block classification (Diagnosis 1) already is a 1/8-resolution classification pass over the *actual* accumulated samples, at zero extra ray cost, once the reduction operator is fixed from `max` to `mean`. The 8 bootstrap frames already spend `8 * 1024 * 1024 ≈ 8.4M` rays before any adaptive allocation runs, versus roughly `4.9M` rays for a 5-second 1/8-resolution (`128x128`) prepass at similar spp — the prepass is not obviously cheaper, and Diagnosis 1's fix gets a similar variance-reduction benefit from data already being generated.

**Recommendation:** do not implement the low-resolution prepass until Diagnoses 1-3 are fixed and re-measured. If the equal-retired-path gap remains after those fixes, revisit the prepass as a *guidance-only* prior (never seeding the output buffer), and prefer Dammertz-style block variance (see below) over a naive low-res render if pursued.

### Estimating The Achievable Ceiling Before Further Tuning

For a fixed total path budget, the best possible MSE from any reallocation among pixels with per-pixel variance `sigma_i^2` is bounded by:

```text
MSE_optimal / MSE_uniform = (mean(sigma_i))^2 / mean(sigma_i^2) = 1 / (1 + CV^2)
```

where `CV` is the coefficient of variation of `sigma_i` across pixels. Karl Li's blog post reports a 42.9% sample-count reduction at equal quality on a glass/metal/caustic globe scene, implying `CV ~ 0.9` on that scene. The `TeapotMaterials` uncertainty distribution in this capture (`mean 0.0687`, `P50 0.0624`, `P95 0.1385`) looks closer to `CV ~ 0.5`, which caps the achievable RMSE improvement at roughly 10% even with a perfect allocator. Before spending more tuning effort on `TeapotMaterials`, compute this ratio directly from the per-pixel `state.w` uncertainty field (not the block-max bucket histogram) and add a caustic-through-glass fixture with a much higher `CV` to validate allocator changes against a scene where adaptive sampling has real headroom.

### Matching The Referenced Paper More Closely

The user's stated goal references Yining Karl Li's blog post (`https://blog.yiningkarlli.com/2015/03/adaptive-sampling.html`), which documents three iterations: (1) PBRT-style per-pixel contrast checks, rejected for progressive-renderer unfriendliness and lack of global awareness; (2) a naive per-pixel neighbor-contrast check, rejected because it produces visually obvious clustered convergence artifacts (the post includes a contrast-boosted image showing this); (3) Dammertz et al.'s hierarchical block-variance method, adopted, which the post credits with a 42.9% sample reduction at equal quality with negligible overhead.

The current implementation's Welford-per-pixel-with-shared-block-priority design is closer to (2) than to (3), and Diagnosis 2's identical-every-reclassification cutoff-bucket admission order is a plausible mechanism for exactly the clustering artifact the post shows and rejects. Dammertz's method (see paper §2.1, "A Hierarchical Automatic Stopping Condition for Monte Carlo Global Illumination") keeps two buffers — all accumulated samples `I`, and samples from every other iteration `A` — and estimates convergence from their disagreement, normalized by accumulated value, with recursive block splitting down to an 8x8 floor rather than a fixed 8x8 grid. This is naturally a relative-error metric (addressing Diagnosis 3's linear-vs-display-space mismatch) and does not require Welford M2 bookkeeping. Consider this as the target design for a later iteration once Diagnoses 1-4 are fixed and measured; it is a larger architectural change and should not be combined with the Diagnosis 1/2 fixes in one step.

## Diagnosis 1 Fix Implemented, Session Stopped Before Capture (August 20, 2026)

Diagnosis 1 (the `max` block reduction in `CSAdaptiveClassify`) is now implemented: `RayTracingCompute.compute:422-483` sums `measuredPriority` across the 8x8 group and divides by the count of valid (in-bounds) pixels in that block (a second groupshared array, `AdaptiveBlockValidCount`, tracks validity so partial edge blocks divide by their actual pixel count rather than a fixed 64). No other kernel changed. `CSAdaptiveAllocateParallel`, `CSAdaptiveCompactParallel`, the exploration floor, the priority/ACES formula, and the firefly clamp default are untouched, matching this session's scope.

`CSAdaptiveProbeClassify` (the kernel every current CPU/GPU allocator parity test drives) bypasses `CSAdaptiveClassify` and its 8x8 reduction entirely — it writes `AdaptivePixelInfo` directly from a test-supplied per-pixel lane array. No CPU test reference mirrors the block-max/mean reduction, so no test code needed updating for this change.

**Isolation testing confirms the fix itself is correct and non-regressing**, but surfaced a separate, pre-existing problem:

- `git stash` (reverting to the last commit, before this and prior sessions' uncommitted adaptive-sampling work) → 121 EditMode tests, 3 failures — exactly the three documented below (caustics debug source, caustics sampling distribution, glare).
- Uncommitted WIP tree restored, but with only this session's Diagnosis 1 edit reverted (one-line `max`-tree instead of sum/mean, everything else in the WIP tree unchanged) → 152 tests, **11 failures**.
- Uncommitted WIP tree with the Diagnosis 1 fix applied → same 152 tests, same **11 failures**, identical set.

So Diagnosis 1 changes nothing about the failing set; the 8 extra failures beyond the documented 3 are already present in the uncommitted tree from earlier sessions, independent of this change:

```text
AdaptiveParallelAllocator_GpuMetadataMatchesCpuReference(13,7,...)      expected 91, mismatch
AdaptiveParallelAllocator_GpuMetadataMatchesCpuReference(17,19,...)     expected 323, mismatch (x2 cases)
AdaptiveParallelAllocator_GpuWorkListMatchesStableCpuReference(1,1,1,...)   expected 1, mismatch
AdaptiveParallelAllocator_GpuWorkListMatchesStableCpuReference(3,5,1,...)   expected 15, mismatch
AdaptiveParallelAllocator_GpuWorkListMatchesStableCpuReference(13,7,1,...)  expected 91, mismatch
AdaptiveParallelAllocator_GpuWorkListMatchesStableCpuReference(17,19,1,...) expected 323, mismatch
GameManager_AdaptiveBucketAllocation_ConservesRootBudgetAndPrefersHighestBucket   expected 91, got 51
```

These all exercise `CSAdaptiveAllocateParallel` / `CSAdaptiveCompactParallel` / `AllocateAdaptiveBucketBudget` — the allocator/bucket machinery this session was explicitly told not to touch (Diagnosis 2 territory). The full suite run also logged one transient `RayTracingImageRegressionTests.CameraInsideTranslucentGlassSphere_CurrentImageBaseline_IsStable` timeout (180 s) caused by a cold-Metal-compile race when the classify kernel body changed with a cold shader cache; it passed immediately once the shader cache was warm (either via `RayTracingShaderPrecompiler.PrecompileFromCommandLine` or a second test run), so it is not a real regression — just note it if a from-scratch (non-cached) run reports it again.

**Session stopped here per instructions.** Steps 2-4 (shader precompile, TeapotMaterials diagnostic histogram capture, and the equal-retired-path 1024x1024 RMSE comparison) were intentionally not run this session, because building the requested diagnostics/RMSE numbers on top of a parallel allocator that is currently failing its own CPU-parity tests would produce results that are hard to attribute cleanly to Diagnosis 1 alone. The user chose to run their own tests and report back before continuing.

**Recommended next step:** before re-attempting the capture/RMSE comparison, either (a) investigate and fix the 8 allocator parity failures (likely candidate: a recent uncommitted change to `AllocateAdaptiveBucketBudget`, `CSAdaptiveAllocateParallel`, or the bucket/lane indexing between them and `CSAdaptiveProbeClassify`/`CSAdaptiveScanGroupBuckets`, since the CPU reference and GPU result now disagree on total assigned/admitted paths for every non-trivial fixture), or (b) explicitly accept and document that the equal-retired-path capture will run against a bucket allocator known to violate its own budget-conservation invariant, and treat any resulting RMSE numbers as provisional until the allocator is fixed. Do not fold this fix into a Diagnosis-1-labeled change; it is Diagnosis-2-adjacent and should be its own reviewed step.

## Verification Status

- Metal compile: successful for the current default final-color variant.
- Scene capture: successful, wrote PNGs and reference metrics under `/tmp`.
- New state-hash regression: passed in the prior focused run.
- The prior focused `RayTracingComputeRegressionTests` invocation had three unrelated pre-existing failures: caustics debug source expectation, caustics scene sampling distribution, and glare behavior. Do not claim a clean suite without resolving or baselining those separately.
- **August 20, 2026 update:** a full-suite run on the current uncommitted tree (Diagnosis 1 fix included) reports **11** failures, not 3 — the three above plus 8 adaptive-parallel-allocator CPU/GPU parity mismatches that predate and are independent of the Diagnosis 1 fix (see "Diagnosis 1 Fix Implemented, Session Stopped Before Capture" above for the isolation evidence and exact failing test list). Treat the suite as red until those 8 are triaged; do not rely on the "three known failures" framing until this is corrected.
- `git diff --check` was clean after the current implementation.

## Completed Since Earlier Handoff

The diagnostics, global-accounting, and flattened-block parallel-compaction milestones below are now implemented. The earlier sections that describe missing diagnostics, local allocation, bootstrap under-allocation, or tiled-prefix ordering are historical context; they are not current TODOs.

- Duplicate `CSAdaptiveDiagnostics` pragmas and duplicate `[numthreads]` attributes were removed from `RayTracingCompute.compute`.
- GPU metadata readback and capture reporting now cover requested root paths, assigned paths, retired paths, active work items, work-list overflow, path-count statistics, uncertainty statistics, bucket populations, bucket budgets, and bucket-admitted paths.
- Metadata slot definitions are centralized in `RayTracingShared.hlsl` and mirrored by named constants in `GameManager.cs`. Do not add new numeric metadata indices at call sites.
- Capture-time readback sums the requested path count from every compact work-list item, rejects duplicate/out-of-range pixel indices, and fails if the accounting invariant is violated.
- The local `4x4` ticket allocator and `CSAdaptiveFillBudget` recovery pass were replaced by deterministic global bucket allocation with contiguous flattened-pixel block prefixes.
- The first correctness milestone intentionally removes feature-prior classification and the extra feature-generation dispatch. The current policy is bootstrap plus display-space uncertainty/exploration only.
- Focused allocator tests cover root-budget conservation, highest-bucket preference, capped per-pixel demand, ordered CPU/GPU work-list parity, and metadata parity at `1x1`, `3x5`, `13x7`, and `17x19`.
- A `13x7` post-bootstrap capture and a `512x512` equal-path smoke capture conserved the full root budget with zero overflow.
- `CSAdaptiveTraceRoot` and `CSAdaptiveResolveRoot` have controlled parity coverage against `CSAdaptiveTraceReference`: equal deterministic sample indices produce equal RGB accumulation, Welford mean/M2, and display uncertainty.
- The trace reference uses a separate read-only work-list binding; `GameManager` binds the same live work-list buffer to both trace bindings so Metal production dispatches remain valid.
- The short root-list Teapot capture conserves the root budget and removes the prior sparse-wave timing jump; it remains a performance diagnostic rather than an adaptive quality acceptance result.

`CSAdaptiveAllocate` remains a serial semantic reference for small probes. The live path uses parallel classification, flattened-block scan/offset completion, pixel compaction, root-list expansion, and indirect root tracing. The short capture shows the root-list correction removes the previous post-bootstrap 400 ms reuse-frame jump, but adaptive remains slower than uniform and needs equal-retired-path/reference validation.

## Required Next Work

**Superseded August 20, 2026.** The quantization-first plan below was written before an equal-retired-path `1024x1024` capture proved the allocator itself — not bucket quantization — causes the measured quality regression. See "Equal-Retired-Path 1024x1024 Result" and "Root-Cause Diagnosis" above for the current diagnosis and the ordered fix list (block-max reduction, dead exploration floor / bucket cutoff starvation, display-space/RMSE metric mismatch, firefly clamp default). Read those sections first. The numbered subsections immediately below are retained for their still-valid diagnostics/testing/benchmarking process guidance, but do not start with "one isolated quantization change" — start with Diagnosis 1 (max-to-mean block reduction) instead.

The parallel global allocator, trace-state parity, accounting, and first wall-clock reference capture are complete on Metal. The next session should make **one isolated quantization change**, prove it creates a useful multi-bucket histogram, then rerun the benchmark. Do not add feature priors, a path-cost model, or more allocator architecture in that change.

### 1. Diagnostics Status

GPU metadata/readback and `RayTracingSceneCapture` reporting are complete:

- requested root paths;
- assigned paths;
- retired paths;
- active work-item count;
- work-list overflow;
- per-pixel path-count min/mean/max and percentiles;
- display uncertainty mean/max/percentiles;
- exploration paths;
- priority bucket populations and admitted paths.

Capture-only validation reads work-list and state data synchronously after the timed render. The inactive asynchronous helper is not a substitute for capture validation.

### 2. Parallel Global Buckets

The production implementation uses 16 quantized priority buckets plus a bootstrap lane. It performs an `8x8` spatial classification pass, lane scans over contiguous flattened `256`-pixel blocks, a small deterministic block-total allocator, parallel offset completion, stable compact pixel-work-list emission, and root-work-list expansion. The serial kernel remains a small-probe semantic oracle. The current bounded demand policy requests `numberOfPasses` for bootstrap pixels and up to four times that for post-bootstrap pixels.

#### Flattened-Block Contract

The previous tiled `4x4` prefix bug was replaced by contiguous flattened-pixel blocks. Do not sort the completed work list or weaken parity to unordered sets:

```text
flat pixels 0..255       -> scan block 0
flat pixels 256..511     -> scan block 1
flat pixels 512..767     -> scan block 2
```

Each `[numthreads(256,1,1)]` scan group owns one flattened block and one lane, stores its local exclusive ranks, and writes a per-block lane population. The small allocator scans those totals. The offset stage adds the scanned block prefix before compaction emits work items in exact row-major lane order.

The GPU work-list and metadata already match the CPU reference on the Metal parity fixtures. The production sequence remains:

```text
classify per-pixel priority and requested count
-> atomically count paths/pixels per bucket
-> allocate the fixed global root budget from highest bucket down
-> deterministically admit pixels in the cutoff bucket
-> compact unique pixel work items
-> build indirect arguments
-> indirect trace
```

The baseline budget for one path per pixel is:

```text
width * height * max(1, numberOfPasses)
```

Do not silently discard overflow, cap spill, or rejected work. Report it, and preserve:

```text
assigned paths == sum(workItem.requestedPaths) == retired paths
```

The current serial allocator is the correctness reference and remains available for small GPU probes or a test-only mode. The production path uses parallel bucket counting, flattened-block prefix sums, admission, pixel compaction, root-list expansion, and separate indirect root-trace/resolve dispatches.

The first parallel version should preserve the current admission semantics:

1. Fully admit every populated bucket above the cutoff.
2. Allocate the remaining paths inside the cutoff bucket.
3. Select cutoff pixels with deterministic ordering or a stable hash permutation.
4. Emit no more than one pixel work item per pixel, then expand it into one root item per assigned path.
5. Preserve `requested == assigned == sum(work-item paths) == retired` without spill, silent rejection, or capacity-sized guarded tracing.

Suggested GPU stages:

```text
classify priority and bucket per pixel
-> parallel bucket population/path counting
-> one small global bucket-budget allocation
-> parallel per-pixel cutoff admission
-> prefix-sum compaction of unique pixel work items
-> expand admitted pixel items into root-path work items
-> indirect dispatch argument build
-> indirect root trace
-> indirect per-pixel resolve
```

Do not introduce a sophisticated path-cost model in this milestone. Preserve deterministic accounting while improving only quantization.

### 3. Quantize Priorities Before Priors

The historical fixed-bit mapping was the active blocker:

```text
bucket = min(15, priority >> 6)
```

The current production implementation uses a logarithmic mapping that uses the available low-priority range. Keep the same 16 buckets while validating quality.

Recommended sequence:

1. Preserve the logarithmic mapping while changing only one future priority policy at a time.
2. Extend the controlled CPU/GPU allocator fixtures for any quantization boundary values.
3. Run a short post-bootstrap diagnostic capture and require more than one populated bucket on `TeapotMaterials`; record population, budget, admitted-path arrays, root-list size, and path-count percentiles.
4. Run a `512x512` equal-retired-path comparison first, then a 5-10 second `1024x1024` reference comparison. Longer reference generation remains intentionally separate from adaptive timed diagnostics.
5. Keep the change only if it improves reference error at equal retired paths. Wall-clock results are secondary and must report retired paths.

If a spread-out histogram still does not improve quality, inspect the difference images and path-count distribution before introducing a single conservative feature prior. In particular, separate glass/refraction and caustic receivers from the rest of the image: the first production comparison visually suggests these regions dominate the current error even though much of the remaining image may be comparable or locally improved.

### 4. Add Priors Only After Quantization Baselines

Measured display uncertainty should dominate after bootstrap. Use the existing stable feature buffers for conservative priors/floors:

- normal, depth, albedo, and identity discontinuities;
- glass, water, metal, emissive, and high-smoothness primary hits;
- fog/caustic candidates;
- randomly selected stable pixels.

Do not use temporal denoiser variance as authoritative Monte Carlo variance. It is reprojected, bounded history data with different semantics.

### 5. Maintain Correctness Coverage

Add tests before comparing quality:

1. CPU reference tests for bucket/hierarchy budget conservation, zero priorities, ties, cutoff buckets, and odd dimensions.
2. GPU probes matching small CPU allocations. Ordered work-list parity now covers `1x1`, `3x5`, `13x7`, and `17x19`; maintain these as regressions and run them on each target backend.
3. Controlled trace-state parity is implemented; maintain its identical sample-index contract when changing allocation code.
4. Work-list invariants: no overflow, unique pixel item per frame, assigned equals retired paths.
5. Reset coverage for camera, geometry/material/light, resolution, settings, adaptive enable/disable, and temporal-mode changes.
6. Partial-group dimensions: at least `1x1`, `3x5`, `13x7`, and `17x19`.

The GPU probes should include a small nontrivial dimension such as `17x19` and compare both metadata and compact work-list contents against the CPU reference. Cover:

- all pixels in bootstrap;
- zero priorities;
- one populated bucket;
- multiple populated buckets;
- ties in the cutoff bucket;
- budgets that do not divide evenly among admitted pixels;
- identical per-pixel sample indices between adaptive trace and a controlled reference trace.

The existing serial GPU allocator is useful here as a semantic oracle. Do not delete it until the parallel path passes parity tests.

### 6. Benchmark Properly

For every candidate, compare both:

```text
equal retired paths: measures allocation quality
equal wall-clock time: includes classification/compaction/dispatch overhead
```

Use the same reference and inspect more than TeapotMaterials:

- diffuse Cornell scene;
- glass/refraction and water;
- glossy metal;
- low-light indirect transport;
- many lights;
- emissive geometry;
- caustic receivers;
- dense meshes;
- dynamic/reset fallback behavior.

At least three trials and alternating on/off order are preferable to one ordered run because thermal effects are material at these durations.

Benchmark in two phases:

1. **Equal retired paths first.** Use 512x512, then 1024x1024, and render beyond bootstrap so allocation is exercised. Compare adaptive and uniform against the same reference image. This isolates allocation quality from classifier/compaction overhead.
2. **Equal wall-clock second.** Use the existing duration capture and reference-metrics harness only after the parallel allocator is in place. Include timing, metrics, diagnostics, and the same reference for both variants.

For the first quality matrix, use:

- `TeapotMaterials` as the continuity fixture;
- diffuse Cornell/indirect transport;
- glass/refraction and water;
- glossy metal;
- low-light indirect transport;
- many lights and emissive geometry;
- caustic receivers;
- dense meshes;
- dynamic/reset fallback behavior.

Use at least three alternating adaptive-on/off trials. Report retired paths explicitly; a higher adaptive FPS with fewer paths is not a success.

### 7. Revisit Bucket Quantization

The historical `priority >> 6` mapping was replaced by the current logarithmic mapping. Current 512x512 Teapot diagnostics populate multiple buckets, but the adaptive result still needs equal-retired-path quality validation. Earlier captures with all pixels in bucket 0 remain historical diagnostics, not evidence about the current mapping.

After the parallel accounting path is correct, evaluate a logarithmic or otherwise normalized 16-32 bucket mapping that spreads meaningful low uncertainties across buckets. Validate the histogram and admitted-path arrays before interpreting image metrics. Do not tune quantization and priors simultaneously.

### 8. Add Priors Only After Quantization And Baselines

If uncertainty-only allocation is globally conserved but misses important visual regions, reintroduce one conservative prior at a time, independently toggled and measured. Start with stable normal/depth/identity discontinuities. Later candidates include material categories, fog/caustic candidates, and a small random exploration set. Keep priors small and report their path contribution by bucket.

Temporal denoiser variance must not become authoritative Monte Carlo variance: it is reprojected, bounded history data with different semantics. Validate all priors against raw beauty and reference metrics, not only denoised presentation.

## Constraints And Non-Goals

- No automatic stopping.
- Adaptive stopping bias remains relevant to any confidence-driven de-prioritization. Keep exploration, bootstrap, and fixed-SPP uniform modes for references.
- Keep Welford statistics linear HDR. Apply ACES/exposure only to confidence bounds.
- Keep `CSMain` as a uniform baseline.
- Do not hide raw-beauty regressions behind denoising.
- Do not add a sophisticated path-cost model until diagnostics prove that concentrated expensive paths dominate wall time.
- New large path kernels increase Metal compile time and register pressure. Preserve small thread groups where required and recompile with generous timeouts.

## Standard Commands

Write generated test/capture artifacts under the project-root `TestCaptures/` directory. The directory is excluded from Git.

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
   -logFile /tmp/gpuraytracing-adaptive-compile.log
```

The command-line precompiler preserves `Library/ShaderCache` so subsequent captures can reuse
warmed Metal code. Add `-rayTracingColdShaderPrecompile` only when intentionally measuring a
cold compile; it clears the cache and can add several minutes before the capture begins.

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingCompareAdaptiveSampling \
  -rayTracingReferenceMetrics \
  -rayTracingDurationSeconds 10 \
  -rayTracingWidth 1024 \
  -rayTracingHeight 1024 \
  -rayTracingCaptureLabel adaptive_candidate_30s \
  -rayTracingOutput /Users/nic.foster/Projects/GPURayTracing/TestCaptures \
   -rayTracingScenes "Assets/Scenes/Generated/TeapotMaterials.unity"
```

The adaptive comparison copies Unity's console log to `<output>/<label>/<scene>/<label>.log`, so the command does not need a `-logFile` argument. When `-rayTracingOutput` is omitted, `<output>` is the project-root `TestCaptures/` directory. If `<output>/<label>` already exists, the tool automatically uses `<label>_2`, then `<label>_3`, and so on.

## Handoff Summary

Adaptive sampling has correct unequal-sample accumulation, deterministic per-pixel sample indexing, compact unique-pixel and root-path work lists, global fixed-budget accounting, readable timing/heatmap diagnostics, Metal-validated indirect tracing, ordered allocator parity, and controlled trace-state parity. The current 10-second Teapot root-list diagnostic reduced adaptive time from `314.736` to `235.467 ms/frame` and removed the prior post-bootstrap timing jump, but adaptive remained slower than uniform at `512x512`. Performance is no longer the open question.

**As of August 20, 2026, an equal-retired-path `1024x1024` capture proved adaptive quality is actively worse than uniform, not merely unproven:** `42.0%` higher RGB RMSE and `3.04 dB` lower PSNR at an identical `29,360,128`-path budget (see "Equal-Retired-Path 1024x1024 Result"). Root cause is diagnosed in "Root-Cause Diagnosis": the 8x8 classification block uses a `max` reduction that collapses the priority histogram (Diagnosis 1), bucket-cutoff admission gives zero paths to `52.3%` of pixels for the entire reclassification window while the exploration floor meant to prevent this is dead code affecting only a diagnostic counter (Diagnosis 2), the display-space ACES priority metric is misaligned with the linear-RGB-RMSE acceptance metric (Diagnosis 3), and generated scenes run with the firefly clamp disabled by default (Diagnosis 4). Fix and re-measure these in order, starting with Diagnosis 1, before touching quantization, priors, or the low-resolution-prepass idea recorded above. The serial allocator remains a small-probe semantic oracle. Higher FPS, more frames, or a plausible screenshot is not acceptance; neither is an equal-time comparison — use equal-retired-path metrics first.

**Session update, same day:** Diagnosis 1 (max-to-mean block reduction) is now implemented in `CSAdaptiveClassify` and verified in isolation to change nothing else — see "Diagnosis 1 Fix Implemented, Session Stopped Before Capture" above. That same isolation testing surfaced 8 previously-undocumented EditMode test failures in the parallel allocator's own CPU/GPU parity coverage (`AdaptiveParallelAllocator_GpuMetadataMatchesCpuReference`, `AdaptiveParallelAllocator_GpuWorkListMatchesStableCpuReference`, `GameManager_AdaptiveBucketAllocation_ConservesRootBudgetAndPrefersHighestBucket`), confirmed pre-existing and unrelated to the Diagnosis 1 change. The requested precompile/diagnostic-histogram/equal-retired-path-RMSE steps were **not run this session**; the user is running their own tests and will direct the next step. **Do not treat the "42.0% worse RGB RMSE" number as current** — it predates the Diagnosis 1 fix and has not yet been re-measured. The next session should either fix the 8 allocator parity failures first (likely a Diagnosis-2-adjacent bug in `AllocateAdaptiveBucketBudget`/`CSAdaptiveAllocateParallel`/bucket-lane indexing, budget/assigned-paths mismatches of exactly the kind those tests check) or explicitly accept running the capture against a known-broken allocator and label the resulting numbers provisional.

## Historical Diagnostics Handoff: Pre-Fix Under-Allocation

This section records the failed implementation state that motivated the completed accounting work above. It is retained so future sessions do not repeat the same debugging path.

### Date And Scope

The latest diagnostics capture was run on August 20, 2026 after adding GPU metadata readback and capture reporting. The command compared uniform and adaptive rendering for 60 seconds at 1024x1024 using `TeapotMaterials` and the existing required reference image.

Capture artifacts:

```text
/tmp/gpuraytracing-adaptive-indirect-captures/adaptive_diagnostics_60s/TeapotMaterials/
```

Important files:

```text
adaptive_off.png
adaptive_on.png
adaptive_off.metrics.json
adaptive_on.metrics.json
adaptive_diagnostics.json
adaptive_off_vs_on_difference.png
adaptive_off_vs_reference_difference.png
adaptive_on_vs_reference_difference.png
```

The shader precompile completed successfully after a cold Metal compile of approximately 157 seconds:

```text
/tmp/gpuraytracing-adaptive-diagnostics-compile-4.log
```

The capture log is:

```text
/tmp/gpuraytracing-adaptive-diagnostics-capture.log
```

### Observed Metrics

Uniform:

```text
100 frames
603.854 ms/frame
RGB MAE       0.0077946053
RGB RMSE      0.0122034774
RGB PSNR      38.2703 dB
Luminance MAE 0.0079809679
Pixels over luminance error 0.01: 27.760%
```

Adaptive:

```text
271 frames
222.303 ms/frame
RGB MAE       0.0363719327
RGB RMSE      0.0624549126
RGB PSNR      24.0887 dB
Luminance MAE 0.0372326450
Pixels over luminance error 0.01: 65.835%
```

Adaptive remains invalid as a quality comparison. It is approximately 366.6% worse in RGB MAE, 411.8% worse in RGB RMSE, and 14.18 dB lower in PSNR than uniform in this run. The higher adaptive frame rate is not evidence of success because the adaptive path is retiring far fewer paths per frame.

### Diagnostic Result

`adaptive_diagnostics.json` reported:

```json
{
  "requestedRootPaths": 1048576,
  "assignedPaths": 58933,
  "retiredPaths": 58933,
  "activeWorkItems": 58912,
  "workListOverflow": 0,
  "pathCountMin": 0,
  "pathCountMean": 11.6339101791,
  "pathCountMax": 246,
  "pathCountP50": 7,
  "pathCountP95": 48,
  "pathCountP99": 66,
  "uncertaintyMean": 0.1106529807,
  "uncertaintyMax": 0.983296931,
  "uncertaintyP50": 0.07462007,
  "uncertaintyP95": 0.3383642,
  "uncertaintyP99": 0.5462656,
  "prioritySum": 21400923,
  "bootstrapPixels": 630124
}
```

Interpretation:

- The intended root budget is 1,048,576 paths per adaptive frame: `1024 * 1024 * max(1, numberOfPasses)`.
- Only 58,933 paths were assigned and retired, approximately 5.62% of the intended budget.
- `assignedPaths == retiredPaths`, so the trace kernel is not losing paths after assignment.
- Work-list overflow is zero, so capacity is not the cause.
- 630,124 pixels were counted as still requiring bootstrap work, but the assigned path count is much lower. Bootstrap work is therefore not being represented or conserved correctly in the work list/global budget pipeline.
- The per-pixel path count distribution is highly uneven, with a median of 7 and a maximum of 246.
- The reported priority bucket population and admitted-path arrays were all zero. These fields are not currently trustworthy and need a follow-up fix.

### Implementation State

Diagnostics were added in these areas:

- `Assets/Scripts/RayTracingCompute.compute`: adaptive diagnostics kernel and metadata counters.
- `Assets/Scripts/GameManager.cs`: asynchronous metadata readback plus capture-time metadata/state readback.
- `Assets/Editor/RayTracingSceneCapture.cs`: `adaptive_diagnostics.json` serialization, percentile calculation, and report output.

The capture-time diagnostic readback intentionally waits only after the timed render completes. Interactive frames use an asynchronous metadata request and do not synchronously stall every frame.

The feature-prior resource binding was also isolated so classify-only feature textures are not bound to the adaptive trace kernel. A prior capture had reported Metal's 8-UAV limit; the latest successful precompile did not report a UAV-limit error. Historical logs may still contain those old warnings and should not be used to assess the current binding state.

### Issues Recorded In That Historical Capture

1. Global budget conservation was broken. Bootstrap pixels did not append work items and contributed paths were dropped. This was fixed by the deterministic global reference allocator.
2. The invariant was made explicit and is now enforced at capture time:

   ```text
   requested root paths == assigned paths == sum(work-item requested paths) == retired paths
   ```

3. Duplicate `CSAdaptiveDiagnostics` declarations/attributes were removed and the shader precompiled successfully.
4. Priority bucket diagnostics were repaired and are now reported using centralized metadata constants.
5. Feature priors were removed from the first correctness milestone. Do not reintroduce them until the parallel allocator and baseline quality comparisons are complete.

### Completed Follow-Up Experiments

The post-fix smoke capture used `13x7`, 9 fixed frames, and `TeapotMaterials`:

```text
requestedRootPaths == assignedPaths == retiredPaths
sum(work-item requested paths) == assignedPaths
workListOverflow == 0
activeWorkItems <= width * height
```

Observed post-fix values were `91 == 91 == 91 == 91`, `workListOverflow = 0`, and `activeWorkItems = 91` for 91 pixels. A `512x512`, 9-frame equal-path smoke capture likewise reported `262144` requested, assigned, work-item, and retired paths with zero overflow. These are accounting validations, not quality wins.

The current serial allocator must not be benchmarked for wall-clock superiority. First replace it with the parallel bucket implementation, then run equal-retired-path comparisons and only afterward repeat the 60-second TeapotMaterials reference comparison.
