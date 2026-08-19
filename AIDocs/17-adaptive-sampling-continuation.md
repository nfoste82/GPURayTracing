# Adaptive Sampling Continuation

This document is the handoff for future work on adaptive sampling. It records the current implementation, measured behavior, conclusions from the reviewed paper, and the recommended architecture for making adaptive sampling improve image quality at a fixed wall-clock budget rather than only increasing FPS.

## Goal

The goal is not merely to skip stable pixels. The goal is:

```text
For the same rendering time or path-tracing work budget,
produce a lower-error image than uniform sampling.
```

Adaptive sampling is only useful for this project if difficult pixels receive the work saved from easy pixels and the resulting image converges faster than the adaptive-off baseline.

## Relevant Documentation

Before changing this area, read:

- `03-compute-shader-renderer.md` for `CSMain`, accumulation, shader globals, and path tracing.
- `08-shader-debugging-and-randomness.md` for sample sequences and deterministic randomness.
- `10-benchmarking-and-performance.md` for capture tooling and fixed-duration comparisons.
- `11-regression-testing.md` for image/GPU regression policy.
- `13-denoising-and-upscaling.md` and `14-svgf-implementation-plan.md` for feature-buffer and variance semantics.
- `04-materials-lights-scene.md` and `07-shader-lighting-and-materials.md` for difficult materials and lighting behavior.

## Legacy Implementation (Removed)

The interval/multiplier policy and its experimental work-list path described below were removed after failing to improve equal-time image error. The retained production surface is `GameManager.enableAdaptiveSampling`, which is currently a dormant toggle for `adaptive_off`/`adaptive_on` capture comparisons. Uniform progressive accumulation, captures, references, PNG metrics, and timing reports remain available for the next implementation.

### Historical Files

- `Assets/Scripts/RayTracingCompute.compute`: adaptive decision, path loop, and unequal-sample accumulation.
- `Assets/Scripts/RayTracingShared.hlsl`: adaptive state and shader globals.
- `Assets/Scripts/GameManager.cs`: adaptive settings, shader binding, state texture allocation, and accumulation lifecycle.
- `Assets/Scripts/SceneSettings.cs`: scene-setting defaults.
- `Assets/Editor/GameManagerEditor.cs`: inspector controls.
- `Assets/Editor/RayTracingSceneCapture.cs`: duration comparisons, diagnostics, references, and error metrics.
- `Assets/Tests/EditMode/RayTracingComputeRegressionTests.cs`: current source-level adaptive coverage.

### State Texture

`AdaptiveSamplingState` is an `ARGBFloat` random-write texture at internal trace resolution. Its current channels are:

```text
R: total path-sample count used by HDR accumulation
G: trusted Welford luminance mean
B: trusted Welford luminance M2
A: trusted Welford observation count
```

The separate trusted count is important. When adaptive sampling is enabled after ordinary progressive accumulation, the old HDR mean is preserved but historic variance is unknown. The shader therefore starts fresh variance observations instead of treating historic variance as zero.

### Current Decision Policy

After the trusted count reaches `_AdaptiveSamplingMinSamples`, the shader calculates a linear-HDR
standard error and transforms its confidence bounds with ACES and `_Exposure` before calculating
the visible error ratio:

```text
variance = M2 / (n - 1)
standardError = sqrt(variance / n)
displayUncertainty = ACES(exposure * (mean + standardError))
                    - ACES(exposure * max(0, mean - standardError))
errorRatio = displayUncertainty / displayTargetError
```

The ratio maps to a verification interval:

```text
errorRatio > 1.0       -> every frame
0.5 < ratio <= 1.0     -> every 2 frames
0.25 < ratio <= 0.5    -> every 8 frames, capped by max interval
ratio <= 0.25          -> configured max interval
```

Current scheduling uses a hashed phase shared by each `4x4` threadgroup. Stable pixels can skip and retain their previous accumulated HDR result.

Material-risk protection limits glass, water, metal, emissive, and highly smooth primary surfaces to an interval of at most two frames. When active, these pixels receive at least two paths.

### Current Sample Reallocation

The current branch also reallocates paths within active pixels:

```text
errorRatio <= 0.5 -> normal _NumberOfPasses
errorRatio > 0.5  -> at least 2x _NumberOfPasses
errorRatio > 1.0  -> adaptiveSamplingMaxPassMultiplier x _NumberOfPasses
```

The maximum multiplier is exposed as `Adaptive Max Pass Multiplier`, defaults to `4`, and is preset to `2` for Quality and `4` for Performance and UltraPerformance.

Adaptive confidence decisions now apply the active ACES/exposure presentation transform to the
linear HDR confidence bounds. Welford accumulation remains in linear HDR; only the uncertainty
comparison is display-space aware. Individual stochastic samples are never tone-mapped before
accumulation.

Accumulation uses the pre-batch sample count, so variable per-pixel path batches remain correctly weighted:

```text
newMean = (oldMean * oldCount + newSampleSum) / (oldCount + newCount)
```

### Important Limitation

The current shader still launches a full-screen `CSMain` dispatch. Skipped pixels still execute scheduling/state/retention logic, and active pixels are selected independently. There is no shared global budget and no compacted active-pixel work list.

Adaptive batches also reserve a per-frame sample-index stride equal to the configured maximum
multiplier. This prevents a high-uncertainty pixel's extra paths from reusing the sample indices
that a later frame would otherwise assign to it. The stride only changes when adaptive sampling is
active; the uniform sequence remains unchanged.

Therefore the current implementation is still primarily a local throttling policy with local sample reallocation. It is not yet a globally prioritized adaptive sampler.

## Capture And Reference Measurement

`RayTracingSceneCapture` supports fixed-duration adaptive comparisons:

```text
-rayTracingCompareAdaptiveSampling
-rayTracingReferenceMetrics
-rayTracingDurationSeconds <seconds>
-rayTracingWidth 1024
-rayTracingHeight 1024
```

Canonical references are stored under:

```text
Assets/Editor/RayTracingSceneReferences/<scene path>/<scene name>.png
```

When missing, the tool generates a deterministic 1024x1024 adaptive-off reference for 120 seconds. A JSON sidecar records the scene, dimensions, duration, frame count, Unity version, graphics backend, timestamp, and PNG SHA-256. Existing references are validated and not silently replaced. Use `-rayTracingRefreshReferences` only after deliberate review.

Candidate output includes PNGs, timing reports, and `.metrics.json` reports. Metrics are calculated after converting exported PNG values from sRGB to linear:

- RGB MAE and RMSE.
- RGB PSNR.
- Luminance MAE and RMSE.
- Mean relative luminance error with a `0.01` denominator floor.
- Fraction of pixels above absolute luminance error `0.01`.

Timing reports also include per-pixel sample-count min/mean/max, interval buckets, and predicted skipped pixels.

## Measured Evidence

Scene:

```text
Assets/Scenes/Generated/TeapotMaterials.unity
```

Reference:

```text
Assets/Editor/RayTracingSceneReferences/Generated/TeapotMaterials/TeapotMaterials.png
```

The 30-second pre-reallocation comparison was:

| Variant | Frames | Mean samples | RGB RMSE | PSNR |
|---|---:|---:|---:|---:|
| Adaptive off | 50 | 50.0 | 0.01134 | 38.91 dB |
| Quality | 69 | 40.54 | 0.01564 | 36.12 dB |
| Performance | 94 | 31.40 | 0.02134 | 33.41 dB |
| Ultra Performance | 94 | 24.39 | 0.02570 | 31.80 dB |

The post-reallocation 30-second run had unusually different overall GPU timing, so compare variants within that run rather than treating absolute before/after values as a controlled benchmark:

| Variant | Frames | Mean samples | RGB RMSE | PSNR |
|---|---:|---:|---:|---:|
| Adaptive off | 28 | 28.0 | 0.01995 | 34.00 dB |
| Quality | 30 | 24.46 | 0.02188 | 33.20 dB |
| Performance | 36 | 20.86 | 0.02488 | 32.08 dB |
| Ultra Performance | 38 | 17.38 | 0.02981 | 30.51 dB |

Within the post-reallocation run, adaptive quality penalties relative to adaptive-off were approximately:

- Quality: `+9.6%` RGB RMSE.
- Performance: `+24.7%` RGB RMSE.
- UltraPerformance: `+49.4%` RGB RMSE.

This is a meaningful improvement over the pre-reallocation penalties, but adaptive sampling still did not produce a better image than adaptive-off in equal time. The current work should therefore focus on global allocation and a better importance metric, not only more aggressive multipliers.

## Paper Findings

The reviewed paper is:

> Rasmus Tamstorf and Henrik Wann Jensen, *Adaptive Sampling and Bias Estimation in Path Tracing*.

Source:

```text
http://luthuli.cs.uiuc.edu/~daf/courses/Rendering/Papers-2/RTHWJ.article.pdf
```

The useful idea is to make convergence decisions in display space. Raw HDR variance does not directly represent visible error because exposure and tone mapping compress highlights and reshape contrast.

For a pixel with linear luminance mean `mu`, trusted sample count `n`, and variance `s2`, estimate a confidence half-width:

```text
standardError = sqrt(s2 / n)

Then apply the active display transform to the bounds:

```text
lower = ACES(exposure * max(0, mu - halfWidth))
upper = ACES(exposure * max(0, mu + halfWidth))

Continue sampling while display uncertainty exceeds the chosen tolerance. Do not tone-map individual stochastic samples before Welford accumulation. Keep statistics in linear HDR, then transform confidence bounds.

The paper also warns that adaptive stopping introduces optional-stopping bias. Correct unequal-sample weighting does not remove that bias. Retain fixed-SPP/reference modes, minimum sample counts, periodic rechecks, and conservative policies for rare-event transport.

The paper's bootstrap bias estimation/correction is not suitable for the runtime renderer. It requires large retained sample histories and many repeated resampling passes. It is only potentially useful as an offline validation experiment.

## Recommended Target Architecture

The next meaningful implementation should become a **fixed-budget, globally prioritized sampler**.

The previous experimental work-list path was removed with the legacy policy. Rebuild this path
behind the retained `GameManager.enableAdaptiveSampling` toggle only after the classification,
budget accounting, and accumulation-validation steps below are ready.

```text
classify pixel priorities
-> reserve a fixed path budget
-> select highest-value pixel/sample jobs
-> compact jobs into a GPU work list
-> indirect-dispatch path tracing for those jobs
```

The adaptive path should be compared against uniform sampling at approximately equal total path-tracing work. It should win by placing paths better, not by silently doing less work.

### Priority Metric

The priority should approximate expected visible error reduction per unit cost:

```text
priority = display-space uncertainty reduction
         * undersampling benefit
         * material/feature prior
         / estimated path cost
```

A practical first approximation is:

```text
priority = displayUncertainty / (n + 1)
```

or, before display-space confidence is available:

```text
priority = variance / (n * (n + 1))
```

Display-space uncertainty should become the main signal once the pixel has enough trusted observations.

### Bootstrap And Priors

Variance is unreliable early and can miss rare events. Reserve a small exploration budget, approximately 5-10%, for:

- pixels below the minimum trusted sample count;
- glass, water, metal, emissive, fog, and high-smoothness surfaces;
- caustic-preservation candidates;
- depth, normal, albedo, or identity edges;
- silhouettes and high display-space gradients;
- randomly selected stable pixels to prevent starvation.

After the minimum sample count, measured display-space uncertainty should dominate. Material and feature classification must be a prior or safety floor, not the final objective.

### Global Budget

For a baseline of one path per pixel:

```text
uniform budget = width * height paths
adaptive budget = sum(selected jobs and their requested paths)
```

Initially constrain the adaptive budget to approximately the uniform baseline. A later experiment can intentionally vary the budget, but quality comparisons must report total retired paths and GPU time.

### Work-List Implementation

Avoid exact GPU sorting for the first version. Use priority buckets:

1. A lightweight classification kernel evaluates display-space priority.
2. Quantize priority into approximately 16-32 buckets.
3. Count bucket populations and requested path costs.
4. Allocate from the highest buckets until the global budget is full.
5. Compact admitted coordinates and requested sample counts into an append/structured buffer.
6. Indirect-dispatch a work-list path kernel.

The path kernel must map:

```text
dispatch thread -> work-item index -> original pixel coordinate
```

and update the existing accumulation/state textures at that coordinate. Preserve the current full-screen `CSMain` as a fallback and baseline while the work-list path is validated.

### Cost Awareness

If practical, estimate path cost from cheap first-hit/material features. Glass/refraction, fog, deep glossy paths, and expensive mesh intersections can cost more than diffuse paths. The allocator should eventually prioritize:

```text
visible error reduction / estimated GPU cost
```

Do not add a complicated cost model before measuring whether path-cost variation affects the result.

## Implementation Order

1. Add and validate display-space confidence width using existing Welford state, `_Exposure`, and the ACES presentation transform.
2. Add a debug visualization for priority/display uncertainty and report mean/max uncertainty.
3. Add fixed-budget diagnostics: total requested paths, accepted paths, bucket histogram, exploration paths, and rejected work.
4. Implement a classification kernel and priority buckets without changing the production path.
5. Implement work-list compaction and indirect dispatch behind an experimental setting.
6. Validate work-list accumulation against full-screen accumulation at identical explicit per-pixel sample assignments.
7. Compare uniform, legacy adaptive, and fixed-budget adaptive at equal wall-clock time and approximately equal retired path work.
8. Add error heatmaps and inspect glass, glossy, indirect-light, caustic, shadow-boundary, and low-light regions separately.
9. Tune exploration share, confidence tolerance, bucket count, and cost normalization only after measurements exist.

## Validation Requirements

Every adaptive change should report:

- fixed-duration elapsed time;
- median and preferably multiple trials;
- total traced paths, not just completed frames;
- mean/min/max per-pixel path counts;
- priority bucket population and admitted-work counts;
- display-space confidence statistics;
- RGB and luminance RMSE/MAE/PSNR against the same reference;
- error heatmaps or at least regional error summaries;
- whether fixed-budget work was within the intended budget.

Required comparisons:

- adaptive-off uniform baseline;
- current legacy interval/multiplier policy;
- experimental display-space fixed-budget policy;
- high-sample reference.

Do not judge success from FPS or screenshots alone. The acceptance criterion is lower display-space error at equal or explicitly documented time/path cost.

## Risks And Non-Goals

- Adaptive stopping is statistically biased; preserve fixed-SPP output for unbiased/reference use.
- Rare-event paths can be missed by variance estimates; retain exploration and risk floors.
- Tone-map-dependent convergence intentionally changes with exposure and presentation policy.
- A global priority queue adds GPU synchronization, buffer, indirect-dispatch, and Metal portability complexity.
- Exact global sorting and bootstrap bias correction are not first milestones.
- Denoising should not hide a regression in raw adaptive beauty; retain raw output and reference metrics.

## Handoff Summary

The current implementation fixes unequal-sample accumulation and reallocates extra paths locally, improving the adaptive quality penalty but not yet beating uniform sampling at equal time. The paper's strongest contribution for this project is display-space confidence testing. The next architectural step is a globally budgeted GPU work list whose priority is expected display-space error reduction per path cost, with material/feature priors and a small exploration budget to protect rare transport.
