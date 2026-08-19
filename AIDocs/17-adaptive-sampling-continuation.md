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
priority      = max(explorationFloor, uncertainty / (n + 1))
```

Before `adaptiveSamplingMinSamples`, pixels receive the uniform baseline number of paths. The exploration floor is a starvation safeguard only; it is not a material/feature prior.

### GPU Work-List Pipeline

The compute asset contains these adaptive kernels:

```text
ClearAdaptiveSamplingState
ClearAdaptiveWorkList
CSAdaptiveClassify
CSBuildAdaptiveDispatchArgs
CSAdaptiveTrace
```

`CSAdaptiveClassify` runs over the internal image with `4x4` groups. It currently preserves a fixed **per-threadgroup** path budget: each valid `4x4` group distributes its uniform-equivalent path count among its own pixels using priority-weighted deterministic tickets. Pixels receiving nonzero paths append one work item:

```text
uint2 AdaptiveWorkList item:
  x: flattened pixel coordinate
  y: requested paths for that unique pixel
```

`CSBuildAdaptiveDispatchArgs` converts the compacted count to 16-thread indirect-dispatch arguments. `CSAdaptiveTrace` uses `[numthreads(16,1,1)]`, maps one thread to one work item, traces its requested local batch, and writes the pixel state exactly once. This avoids concurrent floating-point accumulation writers.

Resources are owned by `GameManager` and resized with output textures:

```text
AdaptiveSamplingState              ARGBFloat texture
AdaptiveWorkList                   uint2 structured buffer, capacity width * height
AdaptiveWorkListMetadata           uint counter buffer
AdaptiveDispatchArgs               3-uint indirect-argument buffer
```

`ComputeDispatch.DispatchIndirect()` was added for this path.

### Current Limitation

The work-list and indirect trace dispatch are real, but allocation is still **local to each 4x4 block**. An easy block cannot donate its budget to a difficult block elsewhere in the image. This is not yet the desired globally prioritized sampler.

The classifier also still visits every pixel every frame, and the current ticket allocator does repeated local scans. The indirect path eliminated the previous capacity-sized trace dispatch, but the classifier/compaction cost remains.

## Current Files

- `Assets/Scripts/RayTracingCompute.compute`: adaptive clear, classify, indirect-argument, trace kernels.
- `Assets/Scripts/RayTracingShared.hlsl`: state/work-list declarations and adaptive globals.
- `Assets/Scripts/GameManager.cs`: state/buffer ownership, reset, adaptive dispatch orchestration.
- `Assets/Scripts/ComputeDispatch.cs`: indirect dispatch wrapper.
- `Assets/Scripts/SceneSettings.cs`: adaptive defaults.
- `Assets/Editor/GameManagerEditor.cs`: experimental controls.
- `Assets/Editor/RayTracingSceneCapture.cs`: off/on capture reports.
- `Assets/Tests/EditMode/RayTracingComputeRegressionTests.cs`: adaptive default and accumulation-hash coverage.

## Measurements

All values below use `Assets/Scenes/Generated/TeapotMaterials.unity`, the existing 120-second 1024x1024 uniform reference, deterministic sampling, `numberOfPasses = 1`, final color, temporal denoising disabled, and a 30-second wall-clock duration.

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

## Verification Status

- Metal compile: successful for the current default final-color variant.
- Scene capture: successful, wrote PNGs and reference metrics under `/tmp`.
- New state-hash regression: passed in the prior focused run.
- The prior focused `RayTracingComputeRegressionTests` invocation had three unrelated pre-existing failures: caustics debug source expectation, caustics scene sampling distribution, and glare behavior. Do not claim a clean suite without resolving or baselining those separately.
- `git diff --check` was clean after the current implementation.

## Required Next Work

The next iteration must implement a **global** budget allocator. Do not tune the local ticket policy further and expect it to solve the main problem.

### 1. Add Diagnostics First

Before changing allocation, add GPU metadata/readback and report it in `RayTracingSceneCapture`:

- requested root paths;
- assigned paths;
- retired paths;
- active work-item count;
- work-list overflow;
- per-pixel path-count min/mean/max and percentiles;
- display uncertainty mean/max/percentiles;
- exploration paths;
- priority bucket populations and admitted paths.

The current timing reports only describe the policy text and frame timing. Earlier documentation claiming sample-count or interval diagnostics is stale.

Use asynchronous diagnostic readback, following the caustics metadata pattern. Never add a synchronous metadata readback to every interactive frame.

### 2. Replace Local Allocation With Global Buckets

Use approximately 16-32 quantized priority buckets. A practical GPU sequence is:

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

An exact GPU sort is not a first milestone. A block pyramid/quadtree is also acceptable if it conserves one global root budget, preserves hotspots, and is validated against a CPU reference allocator.

### 3. Add Priors Only After Global Accounting Works

Measured display uncertainty should dominate after bootstrap. Use the existing stable feature buffers for conservative priors/floors:

- normal, depth, albedo, and identity discontinuities;
- glass, water, metal, emissive, and high-smoothness primary hits;
- fog/caustic candidates;
- randomly selected stable pixels.

Do not use temporal denoiser variance as authoritative Monte Carlo variance. It is reprojected, bounded history data with different semantics.

### 4. Validate Allocation Correctness

Add tests before comparing quality:

1. CPU reference tests for bucket/hierarchy budget conservation, zero priorities, ties, cutoff buckets, and odd dimensions.
2. GPU probes matching small CPU allocations.
3. Explicit per-pixel assignment parity: adaptive trace must match a controlled reference using identical sample indices.
4. Work-list invariants: no overflow, unique pixel item per frame, assigned equals retired paths.
5. Reset coverage for camera, geometry/material/light, resolution, settings, adaptive enable/disable, and temporal-mode changes.
6. Partial-group dimensions: at least `1x1`, `3x5`, and `13x7`.

### 5. Benchmark Properly

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

## Constraints And Non-Goals

- No automatic stopping.
- Adaptive stopping bias remains relevant to any confidence-driven de-prioritization. Keep exploration, bootstrap, and fixed-SPP uniform modes for references.
- Keep Welford statistics linear HDR. Apply ACES/exposure only to confidence bounds.
- Keep `CSMain` as a uniform baseline.
- Do not hide raw-beauty regressions behind denoising.
- Do not add a sophisticated path-cost model until diagnostics prove that concentrated expensive paths dominate wall time.
- New large path kernels increase Metal compile time and register pressure. Preserve small thread groups where required and recompile with generous timeouts.

## Standard Commands

Always write generated test/capture artifacts under `/tmp` unless the user asks for a persistent project location.

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -logFile /tmp/gpuraytracing-adaptive-compile.log
```

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingCompareAdaptiveSampling \
  -rayTracingReferenceMetrics \
  -rayTracingRequireExistingReferences \
  -rayTracingDurationSeconds 30 \
  -rayTracingWidth 1024 \
  -rayTracingHeight 1024 \
  -rayTracingCaptureLabel adaptive_candidate_30s \
  -rayTracingOutput /tmp/gpuraytracing-adaptive-captures \
  -rayTracingScenes "Assets/Scenes/Generated/TeapotMaterials.unity" \
  -logFile /tmp/gpuraytracing-adaptive-capture.log
```

## Handoff Summary

Adaptive sampling now has correct unequal-sample accumulation, deterministic per-pixel sample indexing, compact unique-pixel work items, and a Metal-validated indirect trace dispatch. It is still not globally allocating work and does not improve equal-time convergence. The next LLM should implement global budget accounting and diagnostics before adding new priority heuristics. Acceptance remains lower reference-image error than uniform at equal paths and equal wall-clock time, not higher FPS or a plausible screenshot.
