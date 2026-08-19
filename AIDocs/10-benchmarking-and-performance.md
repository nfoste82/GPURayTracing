# Benchmarking And Performance

This document covers runtime benchmark tooling, performance hotspots, and benchmark recommendations.

## Benchmarking Flow

Every `GameManager` ensures that `RayTracingBenchmarkOverlay` and `RayTracingBenchmarkRunner` exist on the same GameObject at runtime, so scenes do not need to attach either component to their camera. Both overlays start hidden. Press `Z` to toggle the live debug/performance overlay and `X` to toggle the benchmark-runner overlay.

When the benchmark overlay is visible, press `B` to disable vSync temporarily, warm the current scene configuration for 30 frames, and run three 120-frame trials. Detailed trials, summaries, hardware information, renderer quality settings, enabled fog/caustics/water features, geometry counts, and sphere/mesh/triangle light counts are written as CSV under `Application.persistentDataPath/Benchmarks`. `sweepCausticPhotonCounts` retains the specialized caustics matrix: it measures caustics disabled and each configured photon count, reports the first count that exceeds the baseline by 25% or misses the 60 FPS budget, and restores the original scene settings afterward. Keep the Game view resolution and all renderer settings fixed between runs. The CSV reports CPU-observed frame duration; use a GPU profiler alongside it when determining whether a workload is GPU-bound.

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

For adaptive-sampling comparisons, add `-rayTracingCompareAdaptiveSampling`. Each requested scene is rendered four times with the same fixed sample count: adaptive sampling disabled, then the `quality`, `performance`, and `ultra_performance` presets. The output is organized as `<output>/<label>/<scene>/adaptive_off.png`, `quality.png`, `performance.png`, `ultra_performance.png`, and matching `.txt` reports. The reports include total measured render time, average milliseconds per measured frame, and average FPS. One warm-up dispatch is excluded from timing so shader compilation and initial setup do not dominate the comparison. The maximum custom verification interval is 16 frames; stable pixels can therefore be sampled at most once every 16 frames. Example:

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
  -rayTracingOutput /tmp/gpuraytracing-captures \
  -rayTracingScenes "Assets/Scenes/Generated/Benchmark_CornellBox.unity" \
  -logFile /tmp/gpuraytracing-adaptive.log
```

For a single non-comparison capture, use `-rayTracingAdaptiveSamplingPreset quality`, `performance`, or `ultraPerformance`.

Use `-rayTracingDurationSeconds 10` instead of `-rayTracingSamples` to render each variant for a wall-clock duration. The timing report records the actual frame count reached during that interval and the measured average frame time. When both options are supplied, duration takes precedence.

This comparison currently measures CPU-observed `RenderImage` wall time, which includes the synchronous GPU dispatch and presentation work. Use the same Unity process conditions, scene, resolution, sample count, and graphics backend for both variants. The adaptive-on image is expected to be visually close to the adaptive-off image, while its timing report should show the speedup after stable pixels begin skipping dispatch work.

## Performance Hotspots

`GameManager.profileStartup` logs one startup timing report after the first successful compute dispatch. The report separates object registration/Unity startup, output texture allocation, triangle data and per-mesh BVH construction, new mesh BVH template time, texture-array construction, top-level and shadow BVHs, compute-buffer creation/upload, first-frame CPU preparation, and the first compute dispatch. The dispatch measurement includes synchronous shader compilation, while the total runs from `GameManager` initialization through that dispatch. This makes cold shader compilation distinguishable from scene preprocessing in large imported scenes.

For imported scenes where per-mesh template construction dominates startup, use the `GameManager` inspector's `Bake BVH` control. Current bakes load object-space mesh templates instead of rebuilding them. `Bake upon exit` can retain templates from the first slow Play session when no current bake existed.

- Soft shadows scale with lights, shadow quality, sphere count, and intersected mesh BVH nodes/leaves.
- Direct lighting cost scales with how many lights each hit shades. With the `AllLights` strategy this is the per-hit light count, so many-light scenes (`Benchmark_ManyLights`) are dominated by the per-hit light loop, not first-hit object lookup. This is why toggling the top-level BVH does not move `Benchmark_ManyLights` performance. Measured on an Apple M3 Max, `Benchmark_ManyLights` with `AllLights` scaled roughly linearly at ~2 ms per light (about 6 ms at 2 lights, ~150 ms at 72 lights).
- The `UniformRandom` and `ImportanceSampled` light strategies cut this cost dramatically by shading only `lightSampleCount` lights per hit instead of all of them, at the cost of more per-frame noise. `ImportanceSampled` adds a cheap `O(lightCount)` weight pass per hit (no shadow rays) but produces much less noise per sample than `UniformRandom`, so it costs a little more than `UniformRandom` at the same `lightSampleCount` while looking cleaner. It is biased relative to the full scene when active lights exceed `MaxImportanceLights` (`128`) because entries beyond the cap cannot be selected; emissive mesh triangles each consume one entry.
- Path tracing cost scales with `_NumberOfPasses * _NumBounces * geometryCount`; triangle meshes are accelerated, but spheres, lights, BVH traversal, and leaf triangle tests still contribute.
- Transparent shadows and transparent ray paths add extra math and intersection tests. When a scene has no transparent shadow blockers, shadow rays take a cheaper boolean pure-occlusion path (`_HasTransparentShadowBlockers == 0`), so introducing any transparent blocker (sphere or mesh with opacity `< 1`) switches every shadow ray to the more expensive transparent-transmittance accumulation path.
- BVH traversal (top-level, shadow, and per-mesh) visits children near-first and reuses a precomputed inverse ray direction, and all three BVHs build with a SAH split, so first-hit and shadow traversal skip more subtrees than the previous median-split/no-ordering build. These help most in high-object-count and deep-mesh scenes (`Benchmark_ManySpheres`, `Benchmark_ManyMeshes`, `Benchmark_DenseMesh`, `Benchmark_ShadowBlockers`).
- Mesh refraction adds internal same-mesh triangle intersection work for transmitted glass paths.
- Imported model meshes, such as FBX assets, must be CPU-readable because `GameManager.RebuildTriangleData()` reads `mesh.vertices`, `mesh.triangles`, and `mesh.uv`. Unity's model import step is cached by the editor, but the ray tracer still builds its triangle data and BVH at runtime when mesh objects register.
- Frame accumulation reduces static-view noise over multiple frames without increasing per-dispatch path tracing work. Increasing `numberOfPasses` still directly increases per-frame cost and remains the main noise reduction path when accumulation is disabled, the scene is moving, or debug modes are active.
## Benchmark Recommendations

- Use `Benchmark_ManySpheres` to evaluate `topLevelBvhMinObjectCount` for sphere-heavy first-hit traversal. Force TLAS on with `0`, and force flat loops by setting the threshold above the overlay's TLAS object count.
- Use `Benchmark_ManyMeshes` to evaluate the general top-level BVH for many registered mesh objects.
- Use `Benchmark_ShadowBlockers` to evaluate `shadowBvhMinObjectCount`. Force shadow BVH on with `0`, and force flat shadow loops by setting the threshold above the overlay's shadow blocker count.
- Keep `shadowBvhMinObjectCount` fixed while evaluating `topLevelBvhMinObjectCount`, and keep `topLevelBvhMinObjectCount` fixed while evaluating `shadowBvhMinObjectCount`, otherwise the results are hard to interpret.
- Use `DebugRenderMode.AccelerationStructures` and the overlay to confirm the intended BVH path is actually active before comparing frame times.
- In shadow-heavy scenes, the shadow-only BVH has shown measurable benefit. In `Benchmark_ShadowBlockers`, the general top-level BVH is not expected to move performance much because the workload is dominated by shadow rays, not first-hit object lookup.
- Use `Benchmark_ManyLights` to evaluate `lightSamplingStrategy` and `lightSampleCount`. Acceleration-structure thresholds (`topLevelBvhMinObjectCount`, `shadowBvhMinObjectCount`) are not expected to help here because the cost is the per-hit light loop, not object lookup. Compare `AllLights` against `UniformRandom`/`ImportanceSampled` at matched `lightSampleCount`, and compare `UniformRandom` against `ImportanceSampled` at the same `lightSampleCount` to weigh noise versus the extra weight-pass cost. The `maxLightSamples` diagnostic cap can clamp the considered light count to confirm the light loop is the bottleneck.
- Use the runner's optional caustic photon-count sweep in `Benchmark_Caustics` to compare the disabled renderer against the caustics variants and locate the linear-gather knee. It reports the median of three trials and treats the first photon count whose median frame time exceeds the disabled baseline by 25% or misses the target frame budget as the practical linear-gather limit. Proceed with the world-space grid if useful caustic quality requires that count or higher.

### Caustics Grid Results

Measured on an Apple M3 Max at the checked-in benchmark resolution and settings, the world-space grid reduced the 2,048-photon median from an estimated 8.5-9 ms with linear gathering to 2.838 ms, versus a 2.452 ms disabled baseline (15.7% overhead). Performance remained effectively flat from 256 through 4,096 photons at approximately 2.8-2.9 ms. At 16,384 photons the median rose to 3.886 ms (58.5% overhead). The checked-in benchmark therefore uses 2,048 photons: this was visually sufficient and remains below the benchmark's 25% overhead threshold.

## Compile-Time Notes

Shader compile time (not render time) was a real pain point. Historical measurements on an M3 Max put a smaller `CSMain` cold compile around 60-90 seconds, and the README records a later 3-5 minute cold compile. During the environment-lighting work, the enlarged runtime-superset kernel reached approximately 12 minutes before fog and terrain were isolated again. Several techniques keep the common path smaller. Photon caustics remain runtime-controlled through `_CausticsEnabled`, while fog and terrain use isolated variants because their full paths materially inflate the common shader. CPU photon-map allocation and dispatch remain disabled when caustics are off. The bounded production set is `DEBUG_RENDER` x `FOG_ENABLED` x `TERRAIN_ENABLED` (eight combinations), which the editor precompiler warms deliberately. When changing the shader, preserve these:

The compute shader currently uses `#pragma skip_optimizations metal` as a compile-time experiment. It leaves non-Metal platforms unchanged and is intended to measure the tradeoff between Metal compile time and runtime frame time; benchmark representative scenes before retaining it permanently.

On the Apple M3 Max caustics scene at `768x768`, the optimized baseline measured a `16.685 ms` median average frame time and the unoptimized Metal build measured `16.919 ms`, a `0.234 ms` (`1.4%`) regression. The unoptimized run also had one `45.590 ms` outlier; the other two trial averages were close to the baseline. This scene alone therefore shows only a small runtime difference, but other workload shapes should be measured before keeping the pragma.

- **`multi_compile` debug variant.** `GetDebugRenderColor()` and its `CSMain` call site are wrapped in `#if DEBUG_RENDER` behind `#pragma multi_compile _ DEBUG_RENDER`. The debug path inlines its own `GetNearestIntersection()`/`GetDirectLight()`/`CreateScatteredRay()` copies; compiling that into the final-color kernel via the old `_DebugRenderMode == DebugFinalColor ? TracePath(...) : GetDebugRenderColor(...)` ternary roughly doubled the kernel's traversal code. Splitting it into a keyword variant was the single biggest compile-time win for the default (non-debug) variant you normally render with. `GameManager.SetShaderParameters()` toggles the `DEBUG_RENDER` keyword based on `debugRenderMode`.
- **`[loop]` attributes.** Every `for`/`while` loop in the shader (pass, bounce, BVH traversal `while` loops, per-mesh triangle leaf loops, flat object/shadow loops, soft-shadow sample loops, importance-sampling loops) is marked `[loop]` to stop the HLSL/Metal compiler from unrolling them into a huge instruction stream. This is a smaller win than the variant split but should be kept; loop bounds here are dynamic at runtime, so `[loop]` rarely hurts and often helps.
- **Single inlined `SampleSingleLight()` call site.** Finite lights and environment samples are shaded through one inlined `SampleSingleLight()` call site inside `GetLightHittingPoint()`. Duplicating either the BVH-traversing sampling body or its `GetShadowTransmittance()` call across multiple direct-light paths previously made the Metal/HLSL compiler expand the shadow traversal loop many times, producing multi-minute compiles that hung Unity on "Importing Assets". Keep direct-light changes within that single-call-site shape; fog uses the isolated `FOG_ENABLED` variant and caustics remain runtime-controlled.
- **Metal register pressure.** `CSMain` uses a 16-thread `[numthreads(4,4,1)]` group and `TraceCausticPhotons` uses a 32-thread `[numthreads(32,1,1)]` group to keep Metal's group-level temporary-register use within its recommended budget. All shader BVH traversals use 32-entry indexable stacks, and CPU BVH construction constrains split imbalance to guarantee that depth. `MediumStack` read helpers take `in` parameters and mutation helpers use `inout` to avoid source-level aggregate copies. Complementary emissive-hit MIS carries only the previous surface position instead of a complete `RayHit`. Benchmark representative scenes when changing these group sizes: smaller groups can reduce occupancy even while avoiding compiler warnings.

Use `Tools > Ray Tracing > Precompile Compute Shader` to compile and dispatch all eight `CSMain` debug/fog/terrain combinations from edit mode with timing per combination and surfaced compile messages, so slow or failing kernels appear there instead of stalling on first Play. The submenu also exposes each individual combination, so use a single variant while iterating on shader changes and reserve `All Variants` for a complete warmup. Each run clears Unity's generated `Library/ShaderCache` data and force-reimports the compute asset before dispatching, so the selected variant's first dispatch is intended to be a cold compile; a second dispatch records the warm baseline. The cache clear affects generated local editor data only and can make the next import slower. The tool shows a cancelable editor progress bar with the complete keyword combination and remaining count, and appends each completed row immediately to `Library/RayTracingShaderCompileStats.csv` (the file is intentionally ignored by git). It disables runtime caustics because caustics do not produce a shader variant. The tool binds minimal dummy terrain resources, including `_SkyboxTexture`, and disables environment lighting for the warmup dispatch; those runtime resources do not change the shader variant key.

## Debug Variant Compile Stall

Because each `DEBUG_RENDER` variant compiles synchronously on its first `Dispatch`, the first time a debug render mode is selected during Play the main thread freezes while Unity compiles it (the macOS spinning-wheel stall). A live progress bar is impossible during this freeze because the main thread is blocked, so `GameManager` instead defers the blocking dispatch by one frame:

The photon-map `Caustics` debug mode is an exception when caustics are enabled: it dispatches the dedicated gather-only `CSCausticsDebug` kernel with `DEBUG_RENDER` disabled. This avoids compiling the very large combined caustics/general-debug `CSMain` variant.

- `GameManager.RenderImage()` tracks compiled combinations using a key containing debug, fog, and terrain state, plus the currently applied key. When the requested combination is not warmed, it sets `_pendingVariantWarmup`, re-blits the previous output without running the heavy dispatch, and returns.
- That extra frame lets `GameManager.OnGUI()` paint a centered "Compiling shader variant, this may take a minute..." notice (gated on `_pendingVariantWarmup`). The notice is hosted in `GameManager` rather than `RayTracingBenchmarkOverlay` so it appears in every scene, including `Root.unity`, not just the benchmark scenes.
- The next frame runs the stalling `Dispatch` with that notice already on screen, then marks the mode warmed and clears the flag. Subsequent switches to an already-warmed mode dispatch immediately with no notice.

This relies on Unity painting the notice frame (`OnGUI` + present) before the next frame's `OnRenderImage` dispatch, which is the normal main-loop order.
