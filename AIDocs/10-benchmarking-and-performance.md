# Benchmarking And Performance

This document covers runtime benchmark tooling, performance hotspots, and benchmark recommendations.

## Benchmarking Flow

`Assets/Scripts/RayTracingBenchmarkOverlay.cs` can be attached to the camera and references the active `GameManager`. It shows averaged frame time, render size, quality settings, dynamic-quality status, accumulated frame count, sphere/light/mesh/triangle counts, top-level BVH status, shadow BVH status, and thresholds. Press `F3` to toggle the overlay.

`Benchmark_Caustics` also includes `CausticsBenchmarkRunner`. Press `F4` in Play mode to disable dynamic quality and vSync temporarily, warm each shader configuration for 30 frames, then run three 120-frame trials with caustics disabled and at 64, 256, 1024, 2048, 4096, and 16384 photons. Results show each configuration's median and the first count that either exceeds the disabled baseline by 25% or misses the 60 FPS frame budget. Detailed trials and summaries are written as CSV under `Application.persistentDataPath/Benchmarks`. The overlay also reports caustic grid cells, indexed photons, and out-of-bounds photons. Keep the Game view resolution and all renderer settings fixed between runs. The CSV reports CPU-observed frame duration; use a GPU profiler alongside it when determining whether the gather is GPU-bound.

`Tools > Ray Tracing > Generate Benchmark Scenes` runs `RayTracingBenchmarkSceneGenerator` and creates focused scenes under `Assets/Scenes/Benchmarks/`:

- `Benchmark_ManySpheres`: stresses flat sphere loops versus the general top-level BVH.
- `Benchmark_ShadowBlockers`: stresses direct-light shadow rays and the shadow-only blocker BVH.
- `Benchmark_ManyLights`: stresses the per-hit loop over emissive sphere lights.
- `Benchmark_DenseMesh`: stresses per-mesh BVH traversal and leaf triangle tests.
- `Benchmark_ManyMeshes`: stresses object-level culling for many registered mesh objects.
- `Benchmark_Glass`: stresses transparent/refraction paths and transparent shadows.
- `Benchmark_GlassTransmission`: visual test for light energy loss and RGB filtering through single colored panes, stacked colored panes, side-by-side thin versus thick closed glass, and colored transparent sphere shadows.
- `Benchmark_Caustics`: focused static scene with compact lights aligned above a clear glass sphere and glass prism over a matte receiver. It uses 1 pass with final-color accumulation, 10 bounces, a dark environment, and disables the firefly clamp so rare caustic paths remain measurable. The `Caustics` debug mode isolates those paths without direct-light contamination.
- `Benchmark_Water`: stresses the finite water AABB's ray-marched top and flat side/bottom boundaries, Fresnel reflection/refraction, distance-based absorption, and distinct ground-only, water-only, and water-over-ground regions. Accumulation is disabled for animated water.
- `Benchmark_GlassWaterPencil`: image-quality scene inspired by a pencil in a glass of water, stressing glass meshes, calm water refraction, nested transparent surfaces, and thin curved mesh highlights.
- `Benchmark_Sparse`: catches acceleration-structure overhead regressions in small scenes.
- `Benchmark_Dynamic`: stresses per-frame transform updates, BVH rebuilds, and buffer uploads.
- `Benchmark_CornellBox`: a Cornell-box-style image-quality/reference scene in an enclosed mirror-ended room with red/green side walls, rectangular mesh ceiling lights, reflective/glass objects, and recursive mirror views.
- `Benchmark_DragonCornellBox`: a Cornell-box-style imported-model benchmark using `Assets/Models/stanford-dragon-pbr.fbx`, displayed as 30% opaque blue glass with refraction index 1.5. The generator forces the model importer's read/write setting on before loading the mesh, because the ray tracer extracts CPU-side vertices and indices when building triangle buffers and per-mesh BVHs.
- `Wolfenstein`: a low-ceiling stone-room scene with textured mesh walls, multiple sphere lights, and colored spheres.

Existing benchmark scene files are skipped rather than overwritten, so saved local tweaks in `Assets/Scenes/Benchmarks/` are preserved when regenerating scenes.

This also means generator changes do not automatically update an already-created benchmark scene. Delete or manually update an existing generated scene before running `Tools > Ray Tracing > Generate Benchmark Scenes` when generator changes need to be applied.

## Performance Hotspots

- Soft shadows scale with lights, shadow quality, sphere count, and intersected mesh BVH nodes/leaves.
- Direct lighting cost scales with how many lights each hit shades. With the `AllLights` strategy this is the per-hit light count, so many-light scenes (`Benchmark_ManyLights`) are dominated by the per-hit light loop, not first-hit object lookup. This is why toggling the top-level BVH does not move `Benchmark_ManyLights` performance. Measured on an Apple M3 Max, `Benchmark_ManyLights` with `AllLights` scaled roughly linearly at ~2 ms per light (about 6 ms at 2 lights, ~150 ms at 72 lights).
- The `UniformRandom` and `ImportanceSampled` light strategies cut this cost dramatically by shading only `lightSampleCount` lights per hit instead of all of them, at the cost of more per-frame noise. `ImportanceSampled` adds a cheap `O(lightCount)` weight pass per hit (no shadow rays) but produces much less noise per sample than `UniformRandom`, so it costs a little more than `UniformRandom` at the same `lightSampleCount` while looking cleaner. It is biased relative to the full scene when active lights exceed `MaxImportanceLights` (`128`) because entries beyond the cap cannot be selected; emissive mesh triangles each consume one entry.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`; triangle meshes are accelerated, but spheres, lights, BVH traversal, and leaf triangle tests still contribute.
- Transparent shadows and transparent ray paths add extra math and intersection tests. When a scene has no transparent shadow blockers, shadow rays take a cheaper boolean pure-occlusion path (`_HasTransparentShadowBlockers == 0`), so introducing any transparent blocker (sphere or mesh with opacity `< 1`) switches every shadow ray to the more expensive transparent-transmittance accumulation path.
- BVH traversal (top-level, shadow, and per-mesh) visits children near-first and reuses a precomputed inverse ray direction, and all three BVHs build with a SAH split, so first-hit and shadow traversal skip more subtrees than the previous median-split/no-ordering build. These help most in high-object-count and deep-mesh scenes (`Benchmark_ManySpheres`, `Benchmark_ManyMeshes`, `Benchmark_DenseMesh`, `Benchmark_ShadowBlockers`).
- Mesh refraction adds internal same-mesh triangle intersection work for transmitted glass paths.
- Imported model meshes, such as FBX assets, must be CPU-readable because `GameManager.RebuildTriangleData()` reads `mesh.vertices`, `mesh.triangles`, and `mesh.uv`. Unity's model import step is cached by the editor, but the ray tracer still builds its triangle data and BVH at runtime when mesh objects register.
- Frame accumulation reduces static-view noise over multiple frames without increasing per-dispatch path tracing work. Increasing `numberOfPasses` still directly increases per-frame cost and remains the main noise reduction path when accumulation is disabled, the scene is moving, or debug modes are active.
- Dynamic quality, when enabled, adjusts only `numberOfPasses`, `lightSamplingStrategy`, `lightSampleCount`, `shadowQuality`, and `numBounces` to approach `dynamicQualityTargetFrameRate`. It never changes BVH thresholds. Because `numberOfPasses` has the most predictable cost, it is the first setting reduced under pressure and the first setting increased when there is headroom. In many-light scenes, the next dynamic step switches `AllLights` to `ImportanceSampled` at about one tenth of the active light count, then reduces `lightSampleCount` if more performance is needed.

## Dynamic Quality Recommendations

- Use dynamic quality when you care about holding an approximate presentation frame rate while spending spare frame budget on cleaner sampling.
- Leave dynamic quality disabled when collecting fixed-setting benchmark numbers, otherwise it will change the workload during the measurement.
- Prefer tuning `dynamicQualityTargetFrameRate`, `dynamicQualityTolerance`, and `dynamicQualityIncreaseHeadroom` before changing the quality ladder. A wider tolerance changes settings less often; a larger increase headroom requires more spare budget before raising quality and reduces oscillation near setting boundaries.
- Do not compare BVH thresholds while dynamic quality is enabled. Keep `numberOfPasses`, `lightSamplingStrategy`, `lightSampleCount`, `shadowQuality`, and `numBounces` fixed for those tests.

## Benchmark Recommendations

- Use `Benchmark_ManySpheres` to evaluate `topLevelBvhMinObjectCount` for sphere-heavy first-hit traversal. Force TLAS on with `0`, and force flat loops by setting the threshold above the overlay's TLAS object count.
- Use `Benchmark_ManyMeshes` to evaluate the general top-level BVH for many registered mesh objects.
- Use `Benchmark_ShadowBlockers` to evaluate `shadowBvhMinObjectCount`. Force shadow BVH on with `0`, and force flat shadow loops by setting the threshold above the overlay's shadow blocker count.
- Keep `shadowBvhMinObjectCount` fixed while evaluating `topLevelBvhMinObjectCount`, and keep `topLevelBvhMinObjectCount` fixed while evaluating `shadowBvhMinObjectCount`, otherwise the results are hard to interpret.
- Use `DebugRenderMode.AccelerationStructures` and the overlay to confirm the intended BVH path is actually active before comparing frame times.
- In shadow-heavy scenes, the shadow-only BVH has shown measurable benefit. In `Benchmark_ShadowBlockers`, the general top-level BVH is not expected to move performance much because the workload is dominated by shadow rays, not first-hit object lookup.
- Use `Benchmark_ManyLights` to evaluate `lightSamplingStrategy` and `lightSampleCount`. Acceleration-structure thresholds (`topLevelBvhMinObjectCount`, `shadowBvhMinObjectCount`) are not expected to help here because the cost is the per-hit light loop, not object lookup. Compare `AllLights` against `UniformRandom`/`ImportanceSampled` at matched `lightSampleCount`, and compare `UniformRandom` against `ImportanceSampled` at the same `lightSampleCount` to weigh noise versus the extra weight-pass cost. The `maxLightSamples` diagnostic cap can clamp the considered light count to confirm the light loop is the bottleneck.
- Use the `Benchmark_Caustics` `F4` matrix to compare the disabled renderer against the caustics variants and locate the linear-gather knee. It reports the median of three trials and treats the first photon count whose median frame time exceeds the disabled baseline by 25% or misses the target frame budget as the practical linear-gather limit. Proceed with the world-space grid if useful caustic quality requires that count or higher.

### Caustics Grid Results

Measured on an Apple M3 Max at the checked-in benchmark resolution and settings, the world-space grid reduced the 2,048-photon median from an estimated 8.5-9 ms with linear gathering to 2.838 ms, versus a 2.452 ms disabled baseline (15.7% overhead). Performance remained effectively flat from 256 through 4,096 photons at approximately 2.8-2.9 ms. At 16,384 photons the median rose to 3.886 ms (58.5% overhead). The checked-in benchmark therefore uses 2,048 photons: this was visually sufficient and remains below the benchmark's 25% overhead threshold.

## Compile-Time Notes

Shader compile time (not render time) was a real pain point: the single `CSMain` kernel compiled in roughly 60-90 s on an M3 Max. Several techniques keep it down. When changing the shader, preserve these:

- **`multi_compile` debug variant.** `GetDebugRenderColor()` and its `CSMain` call site are wrapped in `#if DEBUG_RENDER` behind `#pragma multi_compile _ DEBUG_RENDER`. The debug path inlines its own `GetNearestIntersection()`/`GetDirectLight()`/`CreateScatteredRay()` copies; compiling that into the final-color kernel via the old `_DebugRenderMode == DebugFinalColor ? TracePath(...) : GetDebugRenderColor(...)` ternary roughly doubled the kernel's traversal code. Splitting it into a keyword variant was the single biggest compile-time win for the default (non-debug) variant you normally render with. `GameManager.SetShaderParameters()` toggles the `DEBUG_RENDER` keyword based on `debugRenderMode`.
- **`[loop]` attributes.** Every `for`/`while` loop in the shader (pass, bounce, BVH traversal `while` loops, per-mesh triangle leaf loops, flat object/shadow loops, soft-shadow sample loops, importance-sampling loops) is marked `[loop]` to stop the HLSL/Metal compiler from unrolling them into a huge instruction stream. This is a smaller win than the variant split but should be kept; loop bounds here are dynamic at runtime, so `[loop]` rarely hurts and often helps.
- **Single inlined `SampleSingleLight()` call site.** Lights are shaded through one inlined `SampleSingleLight()` call site inside `GetLightHittingPoint()`. Duplicating that BVH-traversing body across multiple call sites previously made the Metal/HLSL compiler expand the shadow traversal loop many times, producing multi-minute compiles that hung Unity on "Importing Assets". Keep direct-light changes within that single-call-site shape.

Use `Tools > Ray Tracing > Precompile Compute Shader` to compile and dispatch the shader from edit mode with timing and surfaced compile messages, so slow or failing kernels appear there instead of stalling on first Play. Note it dispatches `CSMain` with no keyword set, so it warms only the default (non-debug) variant; debug variants still compile on first selection in Play.

## Debug Variant Compile Stall

Because each `DEBUG_RENDER` variant compiles synchronously on its first `Dispatch`, the first time a debug render mode is selected during Play the main thread freezes while Unity compiles it (the macOS spinning-wheel stall). A live progress bar is impossible during this freeze because the main thread is blocked, so `GameManager` instead defers the blocking dispatch by one frame:

- `GameManager.RenderImage()` tracks compiled debug variants in `_warmedDebugModes` and the currently applied mode in `_appliedDebugRenderMode`. When `debugRenderMode` switches to a mode not yet warmed, it sets `_pendingVariantWarmup`, re-blits the previous output without running the heavy dispatch, and returns.
- That extra frame lets `GameManager.OnGUI()` paint a centered "Compiling shader variant, this may take a minute..." notice (gated on `_pendingVariantWarmup`). The notice is hosted in `GameManager` rather than `RayTracingBenchmarkOverlay` so it appears in every scene, including `Root.unity`, not just the benchmark scenes.
- The next frame runs the stalling `Dispatch` with that notice already on screen, then marks the mode warmed and clears the flag. Subsequent switches to an already-warmed mode dispatch immediately with no notice.

This relies on Unity painting the notice frame (`OnGUI` + present) before the next frame's `OnRenderImage` dispatch, which is the normal main-loop order.
