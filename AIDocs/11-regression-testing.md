# Regression Testing

The project uses EditMode tests under `Assets/Tests/EditMode/` to make rendering behavior changes explicit. These are regression tests, not only physical-correctness tests: where the renderer intentionally uses an approximation, the expected value records the current behavior. If a later change makes the renderer more physically accurate, the old test should fail first; review the image/math change, then deliberately update the baseline.

The authoritative active sampling repair and acceptance plan is [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md). Coverage below describes existing tests, not a claim that the current wavefront integration passes every baseline or that source assertions establish production sampling correctness.

## Current Coverage

- CPU sphere intersections, including rays starting inside a sphere and rays pointing away.
- CPU triangle hit distance and barycentric coordinates.
- CPU axis-aligned bounding-box hits, misses, and parallel ray components.
- Reflection direction at 45 degrees.
- Snell air-to-glass refraction at 45 degrees.
- Glass-to-air total internal reflection above the critical angle.
- Current Schlick Fresnel values at normal, 45-degree, and grazing incidence.
- Current distance/color/opacity glass absorption approximation.
- A GPU `CSRegressionProbe` kernel in its own compute asset that calls the same reflection, `RefractSnell()`, Fresnel formula, rough-glass boundary sampling, interpolated mesh optical-normal path, and `GetAbsorptionTransmittance()` behavior used by rendering. This catches divergence between CPU expectations and shader execution without bloating the final-color asset.
- Deterministic `32x32` final-color image signatures for a reflective metal sphere, a refractive glass sphere with geometry behind it, a camera starting inside a translucent glass sphere, closed mesh glass through production triangle/mesh/BVH buffers, calm finite water with submerged geometry, nested water with sphere and closed-mesh glass, a camera starting underwater, textured geometry, triangle mesh lights, and sphere/closed-mesh/stacked dielectric occlusion. The nested closed-mesh fixture targets submerged production mesh/BVH refraction pixels, while the shadow fixtures verify that straight NEE connections do not leak through dielectric boundaries. Each baseline stores the image average and eight fixed pixel probes after tone mapping.
- Image fixtures mirror the production `260`-byte triangle layout and bind neutral albedo, metallic/roughness, and normal texture arrays so missing-map defaults remain deterministic.
- Medium-identity and stack probes for air -> water -> sphere glass -> water -> air, parent lookup, matching exits, overflow, unmatched exits, underwater initialization, and flat water-volume side/bottom intersections.
- Deterministic randomized CPU reference comparisons for per-mesh, top-level, and shadow BVH traversal against brute force, with maximum build depth checked against the fixed stack capacity of `32`.
- GPU dispatch smoke coverage at `1x1`, `3x5`, and `13x7`; wavefront generation and presentation guard output texture accesses outside partial thread groups.
- Adaptive coverage is mostly source-string assertions for scheduler budgets, RGB Welford scoring, rotation, group assignments/offsets, reset/hash invalidation, wavefront integration, and capture phase-timing wiring, plus small CPU models for partial groups, rate/rotation arithmetic, and layered path counts. These do not dispatch the production scheduler and resolve to verify exact budgets, independent retirement, deterministic sample-index progression, or Welford/RGB accumulation parity. Controlled production trace/resolve parity remains missing.
- Camera coverage verifies that the serialized lens defaults preserve the previous `0.005` blur scale and enable click-to-focus with clicked focus-point tracking. Existing image fixtures explicitly use the pinhole path; deterministic focus-plane and aperture-shape image fixtures remain future coverage.
- Production GPU probes cover shared Lambert/GGX BRDF values, PDFs, and finite positive sampled throughput.
- The first T1 GGX repair adds 88 focused GPU cases at roughness `0.03/0.05/0.1/0.2`, normal/grazing `NdotV = 1/0.1/0.01`, and metallic `0/0.5`: double-precision reference checks for BRDF, evaluated PDF, and sampled PDF, plus normal-incidence cone and null-event frequencies with `65,536` attempts per seed (`12345/81723`). Before the repair, 54/88 cases failed; the final focused Metal run passed 90/90 with none skipped, including existing baseline/hot-path checks. All sampled weights are checked for finiteness and null weights for zero; analytical `BRDF * cos / PDF` comparisons exclude the existing low-PDF gate and grazing-denominator clamp. The latter suppressed weights in 16 intermediate cases and remains a documented follow-up defect, not validated physical behavior.
- Production GPU probes cover the MIS power heuristic and triangle area-to-solid-angle PDF conversion.
- T2/local R5 focused Metal run passed 13/13: ten new `MisSampleCounts` cases, two existing local
  triangle/environment mean tests, and the single-shadow-call-site check. Before repair, eight new
  count/empty cases failed. Ordinary environment counts 1/2/4/16 agree with BSDF-only raw-HDR means
  within 0.13% per channel across three seeds, with seed-mean standard errors logged. Local RIS tests
  verify exact per-pixel invariance under ignored ordinary budgets, forced empty-reservoir metadata,
  and analytical terminal weights including near-delta, unequal emitter probabilities, and sphere
  exclusion. See document 27 for fixture budgets, compile evidence, and remaining acceptance gates.
- T3/T7 focused Metal set passed 10/10 after four pre-fix failures. Production wavefront coverage
  verifies that one allowed scatter still evaluates a terminal sky hit, exhausted non-emissive paths
  retire without another event, local-RIS terminal MIS remains active, and internally consumed mesh
  events obey the same budget. Reviewed underwater and closed-mesh baselines record intentional
  terminal-radiance and strict-depth changes. All six terrain-off wavefront wrappers completed their
  19 registered precompile dispatches. Terrain and broader terminal-depth coverage remain open.
- T4/T8 focused Metal coverage verifies back-facing triangle rejection, physical inverse-square area
  geometry, one-sided emitter termination, mesh-light independence from `_LightFalloffScale`,
  local-RIS/ordinary triangle-light mean agreement, triangle-caustic production, and zero-radius
  analytic sun upload. The mesh-light image signature was reviewed and updated for the intentional
  physical radiance and sidedness change.
- Static shader tests assert source patterns for Burley-style four-dimensional index shuffling, Owen coordinate scrambling, semantic dimension constants, adaptive sample-index progression, and configured high-dimension hash fallback. They do not verify runtime dimension consumption or non-overlap of RIS/scatter/roulette ranges across candidate counts, retries, and bounces. Genuine CPU coverage checks representative generated Joe-Kuo direction numbers.
- RIS GPU helper probes cover reservoir normalization and invalid-history rejection. Existing local image tests vary candidate counts `1`, `2`, `4`, and `8`, check finite mesh-light radiance, and compare untone-mapped triangle/environment/mixed-light fixture means against ordinary NEE plus BRDF-hit estimates at default counts. The T2/R5 additions above cover nondefault ordinary budgets, empty outcomes, and isolated terminal weights; T3/T7 coverage exercises terminal production flow. These tests do not establish temporal/spatial history mean correctness.
- A focused high-sample image regression verifies that a reflected sphere light does not develop a dark center.
- A constant-white-environment rough-glass furnace regression verifies that smoothness `1.0`, `0.75`, and `0.5` sphere paths retain matching raw-HDR energy across three seeds. This guards the view-conditioned GGX boundary-normal sampler against roughness-dependent loss, but does not establish a complete eta-dependent rough-BTDF evaluate/PDF contract.
- Caustics coverage dispatches the production clear/trace kernels with a fixed seed and the compact target-pair/mesh-triangle buffers, compares canonically sorted photon records, verifies sphere and triangle emitters produce receiver photons through sphere glass, requires useful photon yield through closed-mesh glass, rejects targeted launches blocked by an opaque first hit, checks the multi-event sphere transport bounce budget, verifies smooth metal reflections continue camera-side caustic visibility while diffuse and rough-metal hits terminate it, verifies default-disabled resource isolation, locks the smooth-kernel focused sphere-caustic image signature, and checks energy stability across photon counts and gather radii. Focused analytical/source coverage also requires broad joint occupancy for paired photon coordinates, nearest-scene-hit target ownership, matched debug/final-color camera filtering, and SPPM flux scaling from the actual clamped radius. Runtime multi-iteration SPPM convergence remains a GPU validation gap.
- A production-scene caustics lifecycle test loads `Assets/Scenes/Generated/Caustics.unity`, builds the real `GameManager` sampling distribution, verifies valid normalized target probabilities for both the light/refractor pair CDF and each glass mesh's area-weighted triangle CDF, dispatches its production photon-map update, and requires indexed receiver photons. This closes the gap where in-memory image fixtures could validate photon generation without exercising CPU target-distribution construction. The per-mesh triangle CDF assertions (positive per-triangle probability, monotonic cumulative values, probabilities summing to one, and a final entry reaching exactly one) matter because photon power divides by the triangle probability: a normalization error there scales mesh photon power toward zero or negative without changing photon counts, so counting stored or indexed photons alone cannot detect it.
- Benchmark-tool coverage verifies that both overlays default to hidden, use the `Z`/`X`/`B` controls, and are added idempotently to the `GameManager` GameObject.
- Registration coverage verifies that a collider-backed `RayLight` remains one analytic sphere light when a Scene-view preview mesh is also present.
- Terrain layer coverage verifies that the generated terrain has four layers and that each is dominant on between two and 85 percent of alphamap texels, in addition to a nonzero average weight. The dominance bounds are the meaningful assertions: an average-weight check alone passes both a terrain that blends all four layers equally everywhere and a terrain whose visible area is a single layer, and both render as one flat texture.

## Running Tests

In Unity, open `Window > General > Test Runner`, select EditMode, and run all tests.

From the command line on this project's current macOS Unity version:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -runTests -testPlatform EditMode \
  -testResults /tmp/gpuraytracing-editmode-results.xml \
  -logFile /tmp/gpuraytracing-editmode.log \
```

Unity Test Framework `1.6.0` exits after a command-line run without requiring `-quit`. In Unity `6.3`, supplying `-quit` can terminate the editor before the test run starts.

## Scene Capture Comparisons

All workflows below share the [Capture Caveats](10-benchmarking-and-performance.md#capture-caveats): diagnostic-inclusive outer timing, instrumentation-off versus metrics-off, unverified/estimated retirement, cumulative variant overrides, and linearized-display PNG metrics rather than raw HDR. Capture output is diagnostic evidence, not a substitute for production sampling tests.

### Generic Experiments

`RayTracingSceneCapture` also accepts `-rayTracingExperiment <json>`. An experiment manifest defines one or more scenes, capture settings, an existing validated reference root, and one or more named variants. Each variant can override writable `GameManager` fields or properties using dotted paths and scalar values (`bool`, `int`, `float`, or enum), for example `Caustics.GatherRadiusDecayRate`. Set either a positive `durationSeconds` for a wall-clock capture or a positive `samples` count for a fixed-frame capture. If both are present, `durationSeconds` takes precedence. Experiment captures never generate or replace references; create a reference separately with `-rayTracingGenerateReference` and review it before use.

Each variant resets accumulation and warms before capture, but its configuration is not independently restored: overrides accumulate on the same manager. Explicitly set every compared field in every variant or use separate captures. Outputs include a final PNG, per-frame convergence `metrics.csv`, a generic `variant_comparison.csv`, and reference metrics when `requireReference` is true. A one-variant experiment writes a variant-to-reference difference image when a reference is required, but no pairwise image. Multi-variant experiments also write pairwise difference images by default. The saved manifest records requested overrides, not a complete snapshot of each effective configuration. Disabling adaptive instrumentation or reference comparisons does not disable generic per-frame metrics overhead.

With `-rayTracingSkipAdaptiveOff`, adaptive-off is not rendered. Adaptive candidate reference metrics, candidate-to-reference differences, and candidate-to-candidate comparisons still run; outputs that specifically require adaptive-off are omitted.

`RayTracingSceneCapture` is an editor tool, not a test. It loads the production scenes supplied on its command line, enters Play mode, requests 200 accumulated frames at `512x512` by default, and writes display PNGs for visual before/after comparison. This is not a verified per-pixel sample or retired-path count under adaptive sampling or internal render scaling. Command-line capture uses the Play-mode lifecycle rather than a separate direct-dispatch path, exercising scene initialization and renderer registration. It fixes the random seed, freezes simulation, and disables temporal denoising. Scenes do not need to be in Build Settings. Capture output defaults to the project-root `TestCaptures/` directory; the output subfolder defaults to a local timestamp in `YYYY-MM-DD_HH-mm-ss` format, and `-rayTracingCaptureLabel` can override it for named before/after comparisons. If the requested label folder already exists, the tool selects `_2`, `_3`, and higher numeric suffixes automatically.

For adaptive comparisons, `-rayTracingReferenceMetrics` requires `-rayTracingCompareAdaptiveSampling`, final-color mode, and `1024x1024`. Use `-rayTracingSamples` for equal-frame comparisons, not verified equal-root-path comparisons, or `-rayTracingDurationSeconds 5` through `120` for diagnostic-inclusive wall-clock budgets. Add `-rayTracingSkipAdaptiveOff` to skip the adaptive-off candidate while retaining adaptive-on metrics and reference difference heatmap output. Adaptive candidate requested durations are capped at 120 seconds; the loop cannot interrupt a stalled dispatch. A missing reference is generated automatically as a deterministic 240-second adaptive-off capture at `Assets/Editor/RayTracingSceneReferences/<scene path>/<scene name>.png`; this reference-generation duration is independent of the candidate cap. References include a SHA-256 checked metadata sidecar and must be deliberately regenerated with `-rayTracingRefreshReferences`; `-rayTracingRequireExistingReferences` makes missing references fail for automated jobs. These comparisons measure linearized display PNG differences, not raw HDR estimator means. Instrumented adaptive captures also emit per-frame timing telemetry and an allocation heatmap showing assigned work, not independent completion. Capture-only GPU-fenced scheduler, trace, and resolve phase times include synchronization/readback overhead and identify diagnostic bottleneck shape rather than normal interactive frame cost.

The tool can also be run non-interactively, which allows automated change workflows to capture before/after images. Close any Unity instance using the project first, then run:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
   -projectPath /Users/nic.foster/Projects/GPURayTracing \
   -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
   -rayTracingGenerateScenes \
   -rayTracingWidth 1024 \
   -rayTracingHeight 768 \
   -rayTracingSamples 400 \
   -rayTracingCaptureLabel before \
    -rayTracingOutput /Users/nic.foster/Projects/GPURayTracing/TestCaptures \
   -rayTracingScenes "Assets/Scenes/Root.unity;Assets/Scenes/Generated/CornellBox.unity" \
  -logFile /tmp/gpuraytracing-scene-capture.log
```

`-rayTracingScenes` is required and accepts semicolon-separated project-relative scene paths. `-rayTracingGenerateScenes` is optional; when supplied, it overwrites only the requested generated scene paths before capture. `-rayTracingWidth`, `-rayTracingHeight`, and `-rayTracingSamples` each accept a positive integer and default to `512`, `512`, and `200`, respectively. Run the same command with `-rayTracingCaptureLabel after` after the renderer change. Compare like-named PNGs in the two output folders. GPU output is not pixel-identical across all platforms, so use the same graphics backend for a meaningful comparison.

Use `-rayTracingGenerateReference` instead of `-rayTracingCompareAdaptiveSampling` to render one adaptive-off reference without comparison artifacts. It requires a positive floating-point `-rayTracingDurationSeconds`; the reference filename and JSON sidecar record the resolution and duration. References at the same scene and resolution can coexist, and adaptive reference-metrics captures select the longest valid duration. Use `-rayTracingRefreshReferences` to intentionally replace a reference with the same resolution and duration.

The GPU probe is skipped if the active graphics device does not support compute shaders or does not compile the probe kernel. On macOS, `-nographics` imports the compute shader without an executable Metal kernel, so it runs the CPU suite and skips the GPU probe. Run through the Test Runner or omit `-nographics` to validate all tests.

## Updating A Baseline

1. Run the full suite before the renderer change and confirm it passes.
2. Make one behavioral change at a time.
3. Treat any changed expected value as a review point, even if the new result is more physically correct.
4. Confirm the new result analytically or through a focused reference scene.
5. Update the baseline and explain the intentional behavior change in the commit message.

Do not loosen tolerances simply to make a changed render pass. CPU math uses tight tolerances; GPU probes allow slightly wider tolerances for backend floating-point differences. Image signatures use a small per-channel tolerance because GPU backends may vary slightly, but they are deliberately not perceptual comparisons: a changed probe is intended to force review.

## Image Fixtures

`RayTracingImageRegressionTests` dispatches the production wavefront queue pipeline with in-memory structured buffers and textures. It uses a fixed seed, fixed camera, no frame accumulation, flat object loops, and no scene assets, so the result does not depend on editor scene state. The first execution may take longer while Unity compiles the kernels.

The fixtures use deterministic in-memory sphere, light, triangle, mesh-info, BVH, and texture-array data. They do not depend on scene assets or editor scene state.

## Medium Transition Foundation

`MediumIdentity` in `RayTracingShared.hlsl` records medium type, object identity, IOR, opacity, and absorption color. Wavefront paths carry a fixed-capacity stack with implicit air and initialize containing water and translucent spheres at the camera origin. Containing spheres are pushed from largest to smallest so the innermost sphere is active. Transmission updates the stack while reflection and TIR preserve it. Sphere/mesh helpers that internally cross both faces leave the net stack unchanged; paths that stop inside a volume retain that medium for the next production bounce.

Stack overflow and genuinely unmatched exits set explicit status bits and preserve valid existing state. A focused overlap probe verifies that exiting a non-current interpenetrating sphere removes it by identity while retaining the active sphere. Per-segment probes cover glass/water attenuation, neutral air, finite water side and surface exits, clipping at the next hit, and finite-medium sky misses. Production probes also cover water -> glass and glass -> water source/target selection, refraction direction, Fresnel, and the case where glass -> air would incorrectly produce TIR but glass -> water transmits.

## Remaining Coverage

Sampling priorities and acceptance gates belong to document 27, not an older adaptive optimization sequence. Existing gaps include production-GPU scheduler/generation/resolve count and Welford parity, independent completion accounting, runtime semantic-dimension non-overlap, and temporal/spatial RIS history mean correctness across non-default counts, empty histories, receiver changes, and terminal bounces. Source-string checks and toy CPU models do not close these gaps.

- T1 still requires grazing sampling-frequency integration and white-furnace/raw-HDR mean acceptance.
  T2/R5 verification subsequently completed dry/water/fog/water+fog/RIS/guided wavefront, spatial
  prepass, and regression-probe compiles (terrain off for wavefront). Runtime guiding/reuse mean
  coverage and broader physically matched finite/mixed-light acceptance remain open. A prior broader
  compute-test-class attempt timed out after 120 seconds. No full-suite result or image-baseline
  update accompanies these repairs; focused coverage does not complete Phase 1.
- Add production-GPU BVH-on versus flat-loop image equivalence in addition to the deterministic CPU traversal comparisons.
- The water, nested-water/glass, and underwater-camera signatures were recaptured after tracing their drift to the intentional finite-water AABB change in `5fd1d33`, whose fixtures gained `_WaterDepth` without corresponding baseline updates.
- Affected Metal image signatures were recaptured after the Unity `6000.3.18f1` upgrade. Two consecutive full image-test runs produced identical signatures, while CPU/GPU behavior probes, photon generation, odd-resolution dispatch, the water baseline, and mesh-light baseline remained stable.
- Add focused bounded-fog interval/transmittance GPU probes and deterministic light-shaft image signatures. Existing image fixtures upload `_FogEnabled = 0` and neutral fog parameters for the runtime-disabled path.
