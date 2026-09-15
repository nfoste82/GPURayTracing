# Benchmarking And Performance

This document covers runtime benchmark tooling, performance hotspots, and benchmark recommendations.

The authoritative active sampling work is [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md). Earlier adaptive performance plans are historical evidence, not the current repair sequence. Tool availability and historical captures do not establish sampling correctness or benchmark readiness.

## Capture Caveats

These limitations apply to the capture workflows here and in [Regression Testing](11-regression-testing.md#scene-capture-comparisons):

- **Timing includes diagnostics.** In `Assets/Editor/RayTracingSceneCapture.cs:1246-1280`, the outer stopwatch controls duration and supplies report totals. It includes per-frame metric readbacks/output, adaptive telemetry, and heatmap output. The separate frame stopwatch stops at line 1257, after `RenderImage` and GPU synchronization but before those operations. Per-frame render timings and outer elapsed-time averages are different measurements; neither is an unfenced interactive GPU time.
- **Instrumentation-off is not metrics-off.** `-rayTracingDisableAdaptiveInstrumentation` / `"disableAdaptiveInstrumentation": true` removes adaptive-only instrumentation, not generic per-frame convergence metrics or their readbacks/output. `"requireReference": false` omits reference comparisons, not the generic convergence writer. Neither setting alone produces an uncontaminated throughput capture.
- **Retirement is not independently verified.** `RecordAdaptiveRetiredPaths` in `Assets/Resources/RayTracingAdaptiveScheduler.compute:375-379` copies assigned paths into retired metadata. Equality of those fields is not proof of actual completion. Without adaptive instrumentation, capture reports estimate retired paths as `displayWidth * displayHeight * measuredFrames` (`RayTracingSceneCapture.cs:1284-1286`), ignoring actual adaptive allocation, bootstrap, and internal render scale. Equal frames and reported retired-path totals do not establish equal work.
- **Variant settings accumulate.** Generic experiments apply overrides to the same manager (`RayTracingSceneCapture.cs:647`) without restoring baseline settings between variants. Accumulation reset/warmup does not isolate configuration. Specify every compared setting in every variant or use separate captures; the saved manifest is not a snapshot of each effective configuration.
- **PNG metrics are display-space evidence.** PNG comparisons linearize the exported display image's sRGB values; tone mapping, clamping, quantization, and any presentation processing are already baked in. Linearized-display MAE/RMSE/PSNR are not raw linear-HDR estimator-mean validation.

## Benchmarking Flow

When `-rayTracingSkipAdaptiveOff` is used with an adaptive comparison, adaptive-off is omitted while adaptive candidate comparisons and reference metrics remain enabled.

Every `GameManager` ensures that `RayTracingBenchmarkOverlay` and `RayTracingBenchmarkRunner` exist on the same GameObject at runtime, so scenes do not need to attach either component to their camera. Both overlays start hidden. Press `Z` to toggle the live debug/performance overlay and `X` to toggle the benchmark-runner overlay.

When the benchmark overlay is visible, press `B` to disable vSync temporarily, warm each configuration, and run its configured number of trials. Detailed trials, summaries, hardware information, renderer quality settings, enabled fog/caustics/water features, geometry counts, and sphere/mesh/triangle light counts are written as CSV under `TestCaptures/CausticsBenchmarks/` in the project root. With `sweepCausticPhotonCounts` enabled, the runner starts at `2^10` photons, measures 10 frames, doubles the photon count, and continues until a configuration is at least 25% slower than the first caustics-enabled configuration. It then retests the highest two tested photon counts for 30 frames each and measures caustics disabled for 30 frames. A configurable cooldown pauses renderer submission between configurations for 5 seconds by default; set `cooldownSeconds` to `0` to disable it. Each configuration resets frame accumulation before warming so accumulated output and progressive photon-map state do not carry between configurations. Keep the Game view resolution and all renderer settings fixed between runs. The CSV reports CPU-observed frame duration; use a GPU profiler alongside it when determining whether a workload is GPU-bound.

`Tools > Ray Tracing > Generate Scenes` runs `RayTracingSceneGenerator` and creates focused scenes under `Assets/Scenes/Generated/`. Generated scene filenames omit the `Benchmark_` prefix:

- `Benchmark_ManySpheres`: stresses flat sphere loops versus the general top-level BVH.
- `Benchmark_ShadowBlockers`: stresses direct-light shadow rays and the shadow-only blocker BVH.
- `Benchmark_ManyLights`: stresses the per-hit loop over emissive sphere lights.
- `Benchmark_DenseMesh`: stresses per-mesh BVH traversal and leaf triangle tests.
- `Benchmark_ManyMeshes`: stresses object-level culling for many registered mesh objects.
- `Benchmark_Glass`: stresses transparent/refraction paths and transparent shadows.
- `Benchmark_GlassTransmission`: visual test for light energy loss and RGB filtering through single colored panes, stacked colored panes, side-by-side thin versus thick closed glass, and colored transparent sphere shadows.
- `Benchmark_Caustics`: focused static scene with compact lights aligned above a clear glass sphere, a vertically aligned two-sphere multi-event chain, and a glass prism over a matte receiver. It uses 1 pass with final-color accumulation, 10 bounces, a dark environment, and disables the firefly clamp so rare caustic paths remain measurable. The `Caustics` debug mode isolates those paths without direct-light contamination.
- `Benchmark_CausticsTriangleLight`: focused static triangle-emitter fixture with one downward-facing emissive triangle aligned above a clear glass sphere and matte receiver. Enable the runner's optional caustic photon-count sweep to compare sphere-light and triangle-light photon generation independently.
- `Benchmark_Water`: stresses the finite water AABB's ray-marched top and flat side/bottom boundaries, Fresnel reflection/refraction, distance-based absorption, and distinct shore, deep-water ground, and raised shallow-bed regions. Accumulation is disabled for animated water.
- `Benchmark_GlassWaterPencil`: image-quality scene inspired by a pencil in a glass of water, stressing glass meshes, calm water refraction, nested transparent surfaces, and thin curved mesh highlights.
- `Benchmark_Sparse`: catches acceleration-structure overhead regressions in small scenes.
- `Benchmark_Dynamic`: stresses per-frame transform updates, BVH rebuilds, and buffer uploads.
- `Benchmark_CornellBox`: a Cornell-box-style image-quality/reference scene in an enclosed mirror-ended room with red/green side walls, rectangular mesh ceiling lights, reflective/glass objects, and recursive mirror views.
- `Benchmark_ApertureBokeh`: a dark, fixed-focus camera fixture with tiny bright point lights far beyond the focal plane. It starts at aperture radius `0.1` and three blades so the triangular bokeh silhouette is plainly visible; alter blade count, rotation, or anamorphic ratio on its `GameManager` to validate each lens-shape control.
- `DemofoxGlossyReflections`: an open-front red/green room modeled after [Demofox's glossy-reflections Shadertoy](https://www.shadertoy.com/view/WsBBR3), with five green metals progressing from smooth to fully rough and three foreground material references. The project ocean skybox is intentionally retained as the reflection environment.
- `DemofoxRefractionIndex`: seven otherwise matching smooth glass spheres progressing from IOR `1.0` to `1.5`, modeled after [Demofox's refraction-index fixture](https://www.shadertoy.com/view/ttfyzN). The stripe backdrop makes the increasing refraction distortion directly comparable.
- `DemofoxRoughRefraction`: seven otherwise matching low-IOR glass spheres progressing from smooth to frosted, in front of a black-and-white stripe backdrop. It is modeled after [Demofox's rough-refraction fixture](https://www.shadertoy.com/view/ttfyzN) and makes refraction blur and the broadening of floor patterns directly comparable.
- `DemofoxAbsorption`: the same layout with smooth glass spheres progressing from clear to dark reddish-brown distance-based absorption. It validates the existing RGB Beer-Lambert glass absorption used by transmission paths and transparent shadows.
- `Benchmark_DragonCornellBox`: a Cornell-box-style imported-model benchmark using `Assets/Models/stanford-dragon-pbr.fbx`, displayed as 30% opaque blue glass with refraction index 1.5. The generator forces the model importer's read/write setting on before loading the mesh, because the ray tracer extracts CPU-side vertices and indices when building triangle buffers and per-mesh BVHs.
- `Benchmark_EmissiveDragon`: a high-triangle emissive Stanford Dragon above diffuse receivers. It verifies that an emissive mesh remains one global light-selection entry while its area-weighted triangle CDF samples its surface.
- `Benchmark_BunnyCopper`: a 69,451-triangle Stanford bunny with a copper metal material over a neutral ground plane. Its camera, model, and floor are uniformly scaled by 1,000 to keep the scan mesh's tiny triangles above the renderer's fixed ray-offset tolerances without altering screen-space framing or triangle-count cost. It uses Poly Haven's CC0 Autumn Field (Pure Sky) 4K HDRI as the sole light source through the importance-sampled environment path, with no directional, sphere, or mesh lights. It mirrors the geometry, material category, 8-bounce depth, and 2560x1440 reference resolution of the Wavefront Path Tracer copper-bunny hero render; use 1 pass and frame accumulation for a like-for-like samples-per-pixel measurement.
- `Wolfenstein`: a low-ceiling stone-room scene with textured mesh walls, multiple sphere lights, and colored spheres.

Existing generated scene files are skipped rather than overwritten by the menu command, so saved local tweaks in `Assets/Scenes/Generated/` are preserved.

`RayTracingSceneCapture` accepts `-rayTracingGenerateScenes` to selectively regenerate and overwrite only the generated scene paths supplied to `-rayTracingScenes` before capture. This is the preferred non-interactive workflow when generator changes need to be applied.

Generate a standalone adaptive-off reference without running comparison tooling by adding `-rayTracingGenerateReference`. The command requires `-rayTracingScenes`, `-rayTracingWidth`, `-rayTracingHeight`, and a positive floating-point `-rayTracingDurationSeconds`. References are stored below `Assets/Editor/RayTracingSceneReferences/` with resolution and duration in the filename and JSON sidecar. Existing references are not overwritten unless `-rayTracingRefreshReferences` is supplied. Adaptive reference-metrics captures select the valid reference at the candidate resolution with the longest recorded duration.

For adaptive-sampling comparisons, add `-rayTracingCompareAdaptiveSampling`. In fixed-frame mode, each requested scene is rendered for the same requested frame count as `adaptive_off` and `adaptive_on`, not a verified equal retired-path count. Add `-rayTracingSkipAdaptiveOff` to render only `adaptive_on`; when reference metrics are enabled, the tool still compares that image with the reference and writes its metrics and difference heatmap. Candidate PNGs and diagnostic reports are written below `<output>/<label>/<scene>/`. The log is copied from Unity's console log automatically, so no `-logFile` argument is required. Add Unity's `-logFile -` option when live terminal output is useful. If capture or post-processing fails, the command reports the exception and the Unity/capture log paths to stderr.

Reports include outer elapsed time, derived average milliseconds per frame/FPS, and a reported cumulative retired-path total, subject to [Capture Caveats](#capture-caveats). Warmup is excluded from timing. Instrumented adaptive captures additionally write per-frame CSV/text telemetry, a final allocation heatmap, and numbered current-frame allocation images with metadata. Black means no current-frame pixel allocation, while blue through red indicates increasing assigned paths per selected pixel; these are allocation diagnostics, not independent completion evidence. Snapshot readback and output are outside the frame stopwatch but inside the outer stopwatch. Add `-rayTracingDisableAdaptiveInstrumentation` to omit adaptive-only phase fences, telemetry readbacks, diagnostics, and heatmaps, not generic metrics overhead. Final images, timing reports, and per-frame GPU synchronization remain. Open `Window > Ray Tracing > Adaptive Allocation Monitor` to watch the newest image or inspect a completed folder. Adaptive sampling remains experimental; production scheduler/resolve parity and capture-accounting validation are still required for performance conclusions.

   The editor's adaptive settings can be overridden for command-line captures with `-rayTracingAdaptiveSamplingMinSamples` (1-64), `-rayTracingEnableAdaptiveBootstrap` (`true` or `false`), `-rayTracingAdaptiveBootstrapFrames` (1-512), `-rayTracingAdaptiveBootstrapResolutionScale` (0.125-0.5), `-rayTracingAdaptiveGuidanceHistoryFrames` (0-8), `-rayTracingAdaptiveBootstrapGroupDivisor` (1-16), `-rayTracingAdaptiveLuminanceErrorWeight` (-3 to 3), `-rayTracingAdaptiveReclassificationInterval` (1-8), `-rayTracingAdaptiveHighestBucketSampleRate` (1-8), and `-rayTracingAdaptiveMaxPathsPerPixel` (1-16). Low-resolution bootstrap is disabled by default; set `-rayTracingEnableAdaptiveBootstrap true` only to evaluate its preview/seed behavior. A luminance error weight of 0 preserves absolute linear-RGB priority; negative values prioritize relative error in darker regions and positive values prioritize brighter regions. Use `-rayTracingAdaptiveSampling` with a normal, non-comparison capture to enable adaptive sampling. Omitted options retain the opened scene's serialized values.

   Generic experiment manifests require one or more named variants. Overrides accumulate across variants, so explicitly set every compared field in each variant. A one-variant manifest records timing/convergence metrics and, when `requireReference` is true, reference metrics and `<variant>_vs_reference_difference.png`; it produces no pairwise variant image. Multi-variant manifests generate pairwise variant difference images by default. Set `"generateVariantComparisonImages": false` to omit only those pairwise images; when `requireReference` is true, each variant's `<variant>_vs_reference_difference.png` remains generated.

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingCompareAdaptiveSampling \
  -rayTracingSamples 300 \
  -rayTracingWidth 512 \
  -rayTracingHeight 512 \
  -rayTracingCaptureLabel adaptive_test \
  -rayTracingOutput /Users/nic.foster/Projects/GPURayTracing/TestCaptures \
  -rayTracingScenes "Assets/Scenes/Generated/Benchmark_CornellBox.unity"
```

To measure display-image convergence rather than timing alone, add `-rayTracingReferenceMetrics` to an adaptive comparison at `1024x1024`. It supports either `-rayTracingSamples` for equal-frame tests (not verified equal-root-path tests) or `-rayTracingDurationSeconds` for diagnostic-inclusive wall-clock tests. References live under `Assets/Editor/RayTracingSceneReferences/` in a scene-path-derived folder, named after the scene, for example `Generated/TeapotMaterials/TeapotMaterials.png`. When absent, the capture automatically renders a deterministic 240-second adaptive-off reference with a matching JSON metadata sidecar; existing references are validated and never silently replaced. Reference generation is separate from the 120-second candidate timed-capture cap and may therefore run longer than that cap. Omit `-rayTracingRequireExistingReferences` when automatic reference generation is desired; use that flag only in CI or other jobs that must fail if the reference is missing. Use `-rayTracingRefreshReferences` only after reviewing an intentional renderer change. Each candidate gets a neighboring `.metrics.json` report with RGB/luminance MAE, RMSE, RGB PSNR, relative luminance error, and the fraction of pixels over an absolute luminance error of `0.01`, all computed from linearized display PNG values, not raw HDR. Adaptive comparisons with reference metrics also write three same-size heatmaps: `adaptive_off_vs_on_difference.png`, `adaptive_off_vs_reference_difference.png`, and `adaptive_on_vs_reference_difference.png`. In each image, blue is the smallest per-pixel linearized-display RGB difference and red is the 99th-percentile difference for that pair. Differences at or above that percentile are clamped to red so isolated outliers do not make the rest of the image appear uniformly blue.

Use `-rayTracingDurationSeconds 5` through `120` instead of `-rayTracingSamples` to give each candidate an outer wall-clock budget including per-frame diagnostics. Adaptive candidate requested durations are capped at 120 seconds; this loop check cannot interrupt a stalled dispatch or guarantee an exact deadline. The report records the frame count reached and outer elapsed-time average. More frames in that interval do not alone prove more retired paths or better convergence. Every measured frame synchronizes after rendering, preventing submission-only timing, but the outer total also includes metric/telemetry readbacks and output. Inspect instrumented telemetry as diagnostic evidence, not clean throughput data. When both options are supplied, duration takes precedence. Timed adaptive comparisons wait 10 seconds between candidates by default; override this with `-rayTracingCooldownSeconds <non-negative-seconds>`, or use `0` to disable the cooldown.

The old `adaptiveGuidanceBrightnessPriority`, `adaptiveGuidanceDirectLightPriority`, and `adaptiveGuidanceRoughnessPriority` fields are deserialization remnants, not active Welford allocation controls. Do not use their historical command-line overrides for new policy experiments. Use the active controls above, subject to document 27's measurement and correctness gates.

The same heatmap can be generated from any two readable, same-size images without opening a scene:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingDifferenceImageA /tmp/adaptive_off.png \
  -rayTracingDifferenceImageB /tmp/adaptive_on.png \
  -rayTracingDifferenceOutput /tmp/adaptive_difference.png \
  -logFile /tmp/gpuraytracing-difference.log
```

The per-frame stopwatch measures CPU-observed `RenderImage` plus GPU synchronization; reported outer totals also include per-frame diagnostics. Use the same Unity process conditions, scene, display and internal resolution, requested frame count, instrumentation/metrics settings, and graphics backend for both variants. The default cooldown reduces one source of thermal bias but does not resolve the capture caveats.

Experiments may set `"disableAdaptiveInstrumentation": true` to omit the adaptive-only phase
fences, readbacks, diagnostics, and heatmaps, but generic convergence metrics still run. Set
`"requireReference": false` to omit reference comparisons, not all per-frame metric output.
After an experiment without reference comparisons completes, generate its reference metrics and difference heatmaps
without rerendering by invoking `RayTracingSceneCapture.CaptureFromCommandLine` with
`-rayTracingPostProcessExperimentReferences <capture-root>`. It writes each variant's `.metrics.json`,
`_vs_reference_difference.png`, and `reference_comparison.csv` beside the captured PNGs.
Adaptive
captures additionally record capture-only GPU-fenced adaptive phase timing in
`adaptive_frame_telemetry.csv`: `scheduler_fence_ms`, `trace_fence_ms`, and `resolve_fence_ms`.
The labels group scheduler, trace, and resolve work; they are not a complete per-stage wavefront
profile or evidence of the retired compact-root trace route. Each phase fence uses a tiny
GPU buffer readback, so it includes fence/readback overhead and must be used to identify dominant
phase shape, not as an interactive frame-time result. `adaptive_frame_telemetry.txt` reports phase
averages. These labels are diagnostic phase groupings, not independent completion counters.
See [document 27](27-renderer-sampling-audit-and-repair-plan.md) for the active repair and acceptance
plan; `21-adaptive-sampling-performance-plan.md` retains historical phase-timing evidence only.

## Performance Hotspots

`GameManager.profileStartup` logs one startup timing report after the first successful compute dispatch. The report separates object registration/Unity startup, output texture allocation, triangle data and per-mesh BVH construction, new mesh BVH template time, texture-array construction, top-level and shadow BVHs, compute-buffer creation/upload, first-frame CPU preparation, and the first compute dispatch. The dispatch measurement includes synchronous shader compilation, while the total runs from `GameManager` initialization through that dispatch. This makes cold shader compilation distinguishable from scene preprocessing in large imported scenes.

For imported scenes where per-mesh template construction dominates startup, use the `GameManager` inspector's `Bake BVH` control. Current bakes load object-space mesh templates instead of rebuilding them. `Bake upon exit` can retain templates from the first slow Play session when no current bake existed.

- Soft shadows scale with lights, shadow quality, sphere count, and intersected mesh BVH nodes/leaves.
- Direct lighting cost scales with how many lights each hit shades. With the `AllLights` strategy this is the per-hit light count, so many-light scenes (`Benchmark_ManyLights`) are dominated by the per-hit light loop, not first-hit object lookup. This is why toggling the top-level BVH does not move `Benchmark_ManyLights` performance. Measured on an Apple M3 Max, `Benchmark_ManyLights` with `AllLights` scaled roughly linearly at ~2 ms per light (about 6 ms at 2 lights, ~150 ms at 72 lights).
- `UniformRandom` and `ImportanceSampled` reduce the number of selected lights shaded per hit relative to `AllLights`. Importance weighting adds an `O(lightCount)` proposal-weight pass without shadow rays; local initial RIS can add candidate-generation cost before selected-sample visibility. Noise and throughput benefits depend on scene and settings, not just `lightSampleCount`. The `MaxImportanceLights` (`128`) cap limits global light-selection entries: each emissive mesh consumes one entry, with its triangles sampled through that mesh's area CDF. Entries beyond the cap cannot be selected by the capped importance proposal; the cap is not 128 triangles per mesh or one entry per triangle. Current sampling/MIS gaps preclude a general correctness or quality claim.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`; triangle meshes are accelerated, but spheres, lights, BVH traversal, and leaf triangle tests still contribute.
- Transparent shadows and transparent ray paths add extra math and intersection tests. When a scene has no transparent shadow blockers, shadow rays take a cheaper boolean pure-occlusion path (`_HasTransparentShadowBlockers == 0`), so introducing any transparent blocker (sphere or mesh with opacity `< 1`) switches every shadow ray to the more expensive transparent-transmittance accumulation path.
- BVH traversal (top-level, shadow, and per-mesh) visits children near-first and reuses a precomputed inverse ray direction, and all three BVHs build with a SAH split, so first-hit and shadow traversal skip more subtrees than the previous median-split/no-ordering build. These help most in high-object-count and deep-mesh scenes (`Benchmark_ManySpheres`, `Benchmark_ManyMeshes`, `Benchmark_DenseMesh`, `Benchmark_ShadowBlockers`).
- Mesh refraction adds internal same-mesh triangle intersection work for transmitted glass paths.
- Surface scattering creates the water-only randomized legacy normal only after selecting the water material path; opaque and glass bounces avoid its six RNG calls and normalization. Direct-light sampling similarly builds a receiver-facing disk basis only for ordinary sphere-light samples, not triangle, directional, environment, or already-materialized RIS candidates.
- Emissive-mesh triangle selection uses binary search over its area CDF, so proposal cost is logarithmic rather than linear in emissive triangle count. Use `Benchmark_EmissiveDragon` to measure this path.
- Imported model meshes, such as FBX assets, must be CPU-readable because `GameManager.RebuildTriangleData()` reads `mesh.vertices`, `mesh.triangles`, and `mesh.uv`. Unity's model import step is cached by the editor, but the ray tracer still builds its triangle data and BVH at runtime when mesh objects register.
- Frame accumulation reduces static-view noise over multiple frames without increasing per-dispatch path tracing work. Increasing `numberOfPasses` still directly increases per-frame cost and remains the main noise reduction path when accumulation is disabled, the scene is moving, or debug modes are active.
## Benchmark Recommendations

- Use `Benchmark_ManySpheres` to evaluate `topLevelBvhMinObjectCount` for sphere-heavy first-hit traversal. Force TLAS on with `0`, and force flat loops by setting the threshold above the overlay's TLAS object count.
- Use `Benchmark_ManyMeshes` to evaluate the general top-level BVH for many registered mesh objects.
- Use `Benchmark_ShadowBlockers` to evaluate `shadowBvhMinObjectCount`. Force shadow BVH on with `0`, and force flat shadow loops by setting the threshold above the overlay's shadow blocker count.
- Keep `shadowBvhMinObjectCount` fixed while evaluating `topLevelBvhMinObjectCount`, and keep `topLevelBvhMinObjectCount` fixed while evaluating `shadowBvhMinObjectCount`, otherwise the results are hard to interpret.
- Use the overlay and configured thresholds to check the intended BVH route. `DebugRenderMode.AccelerationStructures` is diagnostic only: current wavefront presentation reads overwritten hit data and does not reliably establish the primary-hit route (see document 26).
- In shadow-heavy scenes, the shadow-only BVH has shown measurable benefit. In `Benchmark_ShadowBlockers`, the general top-level BVH is not expected to move performance much because the workload is dominated by shadow rays, not first-hit object lookup.
- Use `Benchmark_ManyLights` to evaluate `lightSamplingStrategy` and `lightSampleCount`. Acceleration-structure thresholds (`topLevelBvhMinObjectCount`, `shadowBvhMinObjectCount`) are not expected to help here because the cost is the per-hit light loop, not object lookup. Compare `AllLights` against `UniformRandom`/`ImportanceSampled` at matched `lightSampleCount`, and compare `UniformRandom` against `ImportanceSampled` at the same `lightSampleCount` to weigh noise versus the extra weight-pass cost. The `maxLightSamples` diagnostic cap can clamp the considered light count to confirm the light loop is the bottleneck.
- Use the runner's optional caustic photon-count sweep in `Benchmark_Caustics` to measure the caustics-on photon-count curve and the caustics-disabled cost. It reports the median of three trials, uses the first `2^10` caustics configuration as the photon-count reference, and stops after a count reaches 25% overhead relative to that reference. The summary also reports overhead relative to the disabled configuration, making the fixed cost of enabling caustics visible. `Benchmark_CausticsTriangleLight` can be run separately for triangle-emitter behavior.

### Caustics Grid Results

Measured on an Apple M3 Max at the checked-in benchmark resolution and settings, the world-space grid reduced the 2,048-photon median from an estimated 8.5-9 ms with linear gathering to 2.838 ms, versus a 2.452 ms disabled baseline (15.7% overhead). Performance remained effectively flat from 256 through 4,096 photons at approximately 2.8-2.9 ms. At 16,384 photons the median rose to 3.886 ms (58.5% overhead). The checked-in benchmark therefore uses 2,048 photons: this was visually sufficient and remains below the benchmark's 25% overhead threshold.

## Compile-Time Notes

Historical M3 Max measurements put a smaller monolithic `CSMain` cold compile around 60-90 seconds, later versions at 3-5 minutes, and an enlarged runtime-superset kernel at approximately 12 minutes. The retired geometry-debug asset also hit Metal's 10-minute compiler-task timeout. These are historical measurements, not current wavefront compile costs or active blockers. `22-shader-compile-splitting-handoff.md` retains that history with current targeted commands; the legacy debug-repair sequence has been removed.

Active final-color rendering uses `RayTracingWavefront`, `RayTracingWavefrontWater`, `RayTracingWavefrontFog`, and `RayTracingWavefrontWaterFog`, with terrain keyword variants and opt-in dry path-guiding/RIS wrappers. Adaptive generation and resolve use the wavefront pipeline alongside the separate scheduler. Geometry diagnostics use wavefront presentation, not an active `CSMain` or `RayTracingDebug` asset. Caustics generation, final-color gather, and dedicated debug gather remain in `RayTracingCaustics.compute`; disabled caustics retain resource/dispatch isolation. See `26-wavefront-renderer-handoff.md` for active routing and dated targeted compile results, not a blanket parity claim.

The historical `#pragma skip_optimizations metal` experiment on the M3 Max caustics scene at `768x768` measured `16.685 ms` optimized versus `16.919 ms` unoptimized median average frame time, a `0.234 ms` (`1.4%`) regression, with one `45.590 ms` unoptimized outlier. This is not a measurement of the current integrated wavefront renderer and does not establish today's compile/runtime tradeoff.

- **Dynamic loops.** Preserve intentional `[loop]` attributes on traversal and sampling loops to avoid excessive compiler unrolling; do not assume every loop or new stage has identical compile behavior.
- **Single inlined `SampleSingleLight()` call site.** Finite lights and environment samples share this call inside `GetLightHittingPoint()`. Duplicating its shadow traversal graph previously caused multi-minute compiles. The wavefront shadow stage retains this shared estimator shape; fog stays isolated by wrapper.
- **Metal register pressure.** Preserve bounded BVH stacks and matching CPU build-depth constraints. `MediumStack` read helpers use `in`, mutations use `inout`, and prior emissive-hit MIS metadata stores a position rather than a full `RayHit`. Benchmark representative scenes before changing wavefront stage group sizes; historical monolithic group-size measurements do not establish wavefront occupancy or MIS correctness.

Use targeted active-wavefront precompilation only, selecting the wrapper/terrain combination under investigation. Do not use `-rayTracingPrecompileAllVariants` or resurrect retired renderer/debug targets. The precompiler preserves `Library/ShaderCache` by default; add `-rayTracingColdShaderPrecompile` only for deliberate cold measurements. It appends per-asset/kernel/hash/variant cold and warm timings to `Library/RayTracingShaderCompileStats.csv` (ignored by git). For the common dry surface route:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingPrecompileAsset RayTracingWavefront \
  -rayTracingPrecompileVariant 'fog=0;terrain=0' \
  -logFile /tmp/raytracing-wavefront-compile.log
```

## Variant Warmup

First dispatch of a cold wavefront asset/variant can synchronously block Unity's main thread. `GameManager` defers a pending warmup by one frame, re-blits the previous output, and lets `OnGUI()` show "Compiling shader variant, this may take a minute..." before the blocking dispatch. Warmup keys distinguish the active asset family and fog/terrain state; adaptive bootstrap does not require a separate final-color asset. The enabled photon-map `Caustics` debug mode uses its dedicated `CSCausticsDebug` kernel.

The notice depends on Unity presenting the deferred frame before the next dispatch; it is not live progress during compilation. Successful warmup verifies executable kernels, not image, sampling, or first-hit debug correctness.
