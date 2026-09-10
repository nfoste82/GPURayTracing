# Wavefront Renderer Handoff

## Status

**Current phase: queue-driven surface, water, fog, and water+fog renderers are active for final
color. Surface, water, fog, water+fog, and dry-terrain routes are compiled; the user manually
smoke-tested water, fog, water+fog, and terrain. Fog and terrain image parity remain pending.
Adaptive scheduling uses the existing fixed-8x8 scheduler with layered wavefront generation and resolve.
Path guiding and temporal/spatial RIS use opt-in dry wavefront wrappers. Basic first-hit normals, albedo, emission, hit-distance, BVH,
and terrain-cell diagnostics are available from stored wavefront hits; throughput and bounce-count
diagnostics are captured when paths complete.**

The user explicitly chose not to preserve `CSMain` as a runtime fallback. Use Git history if old
behavior must be consulted. Do not restore an old path merely as a fallback during this migration.

## Active Route

`GameManager.ActiveFinalColorShader` lazily loads the dry
`Resources/RayTracingWavefront.compute` or water
`Resources/RayTracingWavefrontWater.compute` asset into nonserialized fields, selecting dedicated
fog and water+fog wrappers whenever those features are active. Existing scenes require no Inspector
assignment. The frame route is:

```text
GameManager.RenderImage
-> PrepareRenderFrame selects CSWavefrontPresent
-> UpdateTextureFromCompute
-> WavefrontPathTracingManager.Dispatch
```

Key files:

- `Assets/Resources/RayTracingWavefront.compute`: state layout and GPU kernels.
- `Assets/Resources/RayTracingWavefrontWater.compute`: water-enabled wrapper around the same
  wavefront stages, selected only while a water volume is registered.
- `Assets/Resources/RayTracingWavefrontFog.compute` and
  `Assets/Resources/RayTracingWavefrontWaterFog.compute`: dedicated volume variants.
- `Assets/Scripts/WavefrontPathTracingManager.cs`: buffer lifecycle, stage bindings, indirect
  dispatches, and host-side stage order.
- `Assets/Scripts/GameManager.cs`: shader load, renderer selection, resize/destruction release.
- `Assets/Editor/RayTracingShaderPrecompiler.cs`: targeted wavefront precompile registration.

The asset is surface-only: it includes `RayTracingShared.hlsl` but does not define
`WATER_ENABLED`, `FOG_ENABLED`, `PATH_GUIDING_ENABLED`, or `EXPERIMENTAL_RIS_REUSE`. Terrain is the
only current keyword variant.

## Implemented Pipeline

There is one persistent `WavefrontPathState` per pixel. Queues contain indices rather than copying
the full state and its expanded medium stack.

```text
CSWavefrontClearFrame

for each camera pass:
  CSWavefrontClearQueues
  CSWavefrontGenerate
  repeat _NumBounces times:
    build indirect args for current queue
    CSWavefrontIntersect
    CSWavefrontClassify
    CSWavefrontClearShadowQueue
    CSWavefrontDirectLight
    CSWavefrontTraceShadows
    CSWavefrontResolveShadowWork
    CSWavefrontScatter
    build indirect args for next queue
    CSWavefrontCopyNextQueue
    CSWavefrontPublishNextQueue
  retire paths still active at the maximum bounce
  resolve completed paths

CSWavefrontPresent
```

The explicit counter buffer holds current, next, completed, and shadow-work counts. Queue stages
use GPU-generated indirect arguments and `ComputeDispatch.DispatchIndirect`. Retirement after the
fixed host-side bounce loop is intentional: it accounts for all camera paths at the former maximum
bounce limit instead of silently dropping survivors.

`WavefrontPathState` carries current ray, radiance, throughput, full `MediumStack`, semantic
`RngState`, pixel/bounce identity, and the prior surface/PDF/direct-light/RIS/near-delta/soft-shadow
fields needed for complementary sky and emitter MIS. `RayHit` is stored after intersection rather
than recomputed.

Current C# buffer strides must match the HLSL layouts:

```text
WavefrontPathState: 352 bytes
RayHit:             144 bytes
```

If either struct changes, recalculate structured-buffer alignment and update both
`WavefrontPathTracingManager` and the precompiler dummy buffers.

## Smoke-Tested Coverage

The user manually tested and confirmed expected behavior for:

- Diffuse, metal, and glass.
- Reflection and refraction.
- Albedo, normal, metallic, and displacement/parallax textures.
- Caustics in the tested scene.

The surface pipeline retains spheres, meshes, scene/per-mesh BVHs, terrain keyword variants,
environment/sphere/mesh/directional light sampling, local initial RIS, complementary MIS,
transparent shadows, depth of field, semantic Sobol/hash RNG, Russian roulette, firefly clamp,
camera passes, frame accumulation, and existing post-trace caustic/feature/denoiser routes.

Manual testing is not image-regression parity.

## Shadow Queue Integration

`CSWavefrontDirectLight` now queues each eligible active path as a `ShadowWorkItem`.
`CSWavefrontTraceShadows` runs indirect over that queue and retains the established
`GetLightHittingPoint` estimator, including transparent shadow traversal, environment/mesh lights,
initial RIS, complementary MIS metadata, primary soft shadows, water attenuation, and fog shadow
attenuation. `CSWavefrontResolveShadowWork` applies the visibility-weighted radiance before
`CSWavefrontScatter` continues the path. Clearing the shadow counter per bounce prevents stale
work from being replayed.

This is deliberately a path-level queue boundary. Candidate generation and individual light sample
materialization remain in the trace kernel to preserve the shared estimator's single inlined
`SampleSingleLight` call site while moving the expensive shadow-BVH code out of the direct-light
kernel.

The dry default targeted cold Metal compile passed on the M3 Max:

```text
CSWavefrontDirectLight:       42 ms
CSWavefrontTraceShadows:  23.505 s
CSWavefrontScatter:        7.298 s
Total:                    33.066 s
```

All 16 wavefront kernels completed with no shader warnings or errors. The compile moved the
expensive work from direct-light into the dedicated shadow kernel and reduced the total cold compile
from the prior 44.142 s baseline. Log: `/tmp/raytracing-wavefront-shadow-queue-compile.log`.

## Experimental Wavefront Features

The dry `RayTracingWavefrontPathGuided.compute` wrapper defines `PATH_GUIDING_ENABLED` before
including the common queue pipeline. It keeps guide-training state out of `WavefrontPathState` by
using an opt-in per-pixel guide-state buffer, captures guided continuation context at scatter, and
records terminal/direct-light observations through the existing guide manager. It is selected only
for dry scenes while `enablePathGuiding` is set.

The dry `RayTracingWavefrontRis.compute` wrapper defines `EXPERIMENTAL_RIS_REUSE` and
`WAVEFRONT_RIS_REUSE`. It reuses the existing temporal/spatial RIS history manager and spatial
prepass, selecting the wrapper only under the established static, one-pass, non-water/non-fog
eligibility gate. Reuse remains experimental and default-off.

Adaptive scheduling reuses the fixed-8x8 scheduler. Every admitted group layer runs the standard
queue stages after guarded generation; adaptive resolve performs the Welford mean/M2 update and
present reads the per-pixel HDR accumulator. Adaptive bootstrap and debug modes remain excluded.
Validate deterministic count and image parity before treating this as benchmark-ready.

## Retired Legacy Routes

The separate `RayTracingAdaptiveTrace.compute` / `RayTracingWaterAdaptiveTrace.compute` route and
the monolithic `RayTracingExperimentalPathGuided.compute` / `RayTracingExperimentalRis.compute`
wrappers were retired after their active wavefront replacements landed. `GameManager` no longer
loads, serializes, dispatches, or precompiles those assets. Adaptive sampling now requires the
active wavefront final-color shader; path guiding and RIS select their wavefront wrappers directly.
Historical assets and measurements remain available in repository history.

## Unsupported Or Bypassed

- **Adaptive sampling:** uses the existing fixed-8x8 scheduler. Each admitted sample layer runs the
  queue pipeline, then wavefront resolve updates the persistent Welford state and HDR accumulation.
  Low-resolution bootstrap also uses the active wavefront shader. Runtime scheduler/heatmap and
  deterministic parity validation remain required before treating adaptive results as benchmark-ready.
- **Temporal/spatial RIS reuse:** available through the opt-in dry `RayTracingWavefrontRis` wrapper.
  It preserves the existing one-pass, static-scene eligibility restrictions and uses the existing history
  and spatial-prepass resources. Local initial RIS remains active in every wavefront route.
- **Path guiding:** available only through the opt-in dry `RayTracingWavefrontPathGuided` wrapper.
  It uses a separate per-pixel guide-state buffer while enabled, preserving the 352-byte path state.
- **Advanced geometry and path debug modes:** still unavailable. `Normals`, `Albedo`, `Emission`,
  `HitDistance`, `AccelerationStructures`, and `TerrainCells` use the stored first `RayHit`.
  `DirectLight` uses the stored first-bounce next-event estimate; `Throughput` and `BounceCount`
  use state captured at path completion. The direct-light and path-diagnostic buffers are allocated
  only while their modes are active; do not add diagnostic fields to `WavefrontPathState` or revive
  the monolithic debug tracer.
- **Per-candidate shadow queues:** the queue is currently one work item per path. Direct-light
  candidate generation and individual light samples remain materialized inside the shadow stage.

## Caustics Integration

Caustics are wavefront-compatible without a camera-path event stage. `PrepareRenderFrame` updates
the independent photon map before the camera dispatch. After `WavefrontPathTracingManager` writes
the wavefront beauty into `_outputTexture`, `DispatchRenderFrame` copies it to `_beautyTexture`,
dispatches `CSCausticsFinalColor`, and uses `CompositeCaustics` to add the gathered radiance.

This preserves the intentional split: `RayTracingCaustics.compute` owns photon generation, grid
build, camera-side gather, and its dedicated debug mode, rather than adding nested photon-grid
traversal to the wavefront shader. Glass and water photon transport continue to compile in the
dedicated caustics asset. The user manually confirmed water rendering; caustic image regression
parity is still required before claiming broad renderer parity.

## Fog Integration

Fog variants sample bounded free-flight after closest-hit traversal and before surface/terminal
classification. A sampled fog event replaces the pending surface event for that bounce, preserves
active-medium attenuation over the traveled distance, and uses the existing volume direct-light
estimator with fog shadow attenuation. Single scattering completes the path after direct light;
multiple scattering samples an isotropic continuation, resets surface MIS state, and requeues the
path. The dry and water variants remain free of fog branches.

The dry fog wrapper cold-compiled successfully on the M3 Max:

```text
CSWavefrontDirectLight: 34.099 s
CSWavefrontScatter:      9.745 s
Total:                  46.974 s
```

Log: `/tmp/raytracing-wavefront-fog-compile.log`. The water+fog wrapper reached its direct-light
kernel (`91.359 s`) but was still compiling when the 120-second command timeout elapsed; do not
claim that combined variant validated until it completes in a standalone Unity session.

## Terrain Integration

Terrain remains a `TERRAIN_ENABLED` keyword permutation rather than a wrapper asset. The
`TerrainManager` enables the keyword and binds heightfield, alphamap, layer textures, normals,
masks, and buffers each time `WavefrontPathTracingManager` binds a queue-stage kernel. This makes
the same terrain implementation available to the dry, water, fog, and water+fog wrappers without
adding separate terrain asset-selection logic.

The dry terrain variant cold-compiled successfully on the M3 Max:

```text
CSWavefrontIntersect:    9.943 s
CSWavefrontDirectLight: 51.599 s
CSWavefrontScatter:     13.638 s
Total:                  77.339 s
```

The compile completed all 13 wavefront kernels. Metal reported the existing terrain texture
sampling integer-modulus performance warnings in `RayTracingShared.hlsl`; it reported no errors.
Log: `/tmp/raytracing-wavefront-terrain-compile.log`. A structural regression test locks the
keyword declaration and per-stage terrain binding. Run the generated Terrain scene manually and
capture image parity before claiming visual terrain parity; then compile/test terrain-on water,
fog, and water+fog variants as their combinations are needed.

## Compile Results

All timings below are targeted cold Metal compiles of **only** `RayTracingWavefront`, default
`fog=0;terrain=0`, on the Apple M3 Max. No legacy all-assets compile was run.

Before splitting the original shade stage:

```text
CSWavefrontShade: 113.9 s
Total:             116.0 s
```

After splitting classify, direct light, and scatter:

```text
CSWavefrontGenerate:          322 ms
CSWavefrontIntersect:       1.735 s
CSWavefrontClassify:         317 ms
CSWavefrontDirectLight:    33.734 s
CSWavefrontScatter:         7.917 s
Other stages:             12-25 ms each
Total:                    44.142 s
```

The final targeted compile completed without a Metal register-pressure warning. Log:
`/tmp/raytracing-wavefront-compile-4.log`. CSV timings append to
`Library/RayTracingShaderCompileStats.csv`.

The water wrapper was cold-compiled successfully after it was added:

```text
CSWavefrontDirectLight: 32.528 s
CSWavefrontScatter:     10.220 s
Total:                  46.426 s
```

Log: `/tmp/raytracing-wavefront-water-compile.log`. This verifies kernels and `WATER_ENABLED`
resource bindings, but not water image parity.

Use only this targeted command while iterating on the common surface path:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingColdShaderPrecompile \
  -rayTracingPrecompileAsset RayTracingWavefront \
  -rayTracingPrecompileVariant 'fog=0;terrain=0' \
  -logFile /tmp/raytracing-wavefront-compile.log
```

Do not use `-rayTracingPrecompileAllVariants`; it selects unrelated assets and can take an hour.

## Validation State

Completed:

```text
dotnet build GPURayTracing.sln --no-restore
passed with 0 warnings and 0 errors

Targeted Unity cold precompile: RayTracingWavefront only
passed for all current wavefront kernels

Manual visual smoke test
passed for the user-tested material, texture, reflection, refraction, and caustic cases
```

Still required:

1. Run EditMode tests without `-nographics` so GPU probes execute.
2. Convert direct-CSMain image fixtures into a deterministic full-wavefront dispatch harness.
3. Add queue tests: zero/partial/exact group counts, counter reset, overflow, odd dimensions,
   completion accounting, and maximum-bounce retirement.
4. Capture image comparisons for Root, CornellBox, ManyLights, ManySpheres, ManyMeshes,
   GlassTransmission, mesh lights, texture-heavy glTF, dry caustics, and water caustics.
5. Benchmark equal samples and equal time, including stage cost and queue occupancy. Do not use
   synchronous readback in interactive timing.
6. Terrain manually smoke-tested; capture deterministic terrain image parity.
7. Compile/test terrain-on water, fog, and water+fog variants as their combinations are needed.

## Ordered Remaining Work

### 1. Validate And Refine Shadow Work

The path-level shadow queue has compiled successfully. Manually validate opaque and transparent
blockers, environment, sphere/mesh/directional lights, initial RIS, primary soft shadows, water,
and fog. Add queue-accounting and deterministic image fixtures before splitting one path's light
candidates into separate shadow work items; retain a single inlined `SampleSingleLight` call site
unless the estimator is deliberately decomposed.

### 2. Validate Water

`RayTracingWavefrontWater.compute` now defines `WATER_ENABLED` before including the shared
wavefront stages, and `GameManager` selects it only while `HasWaterVolume`. Water globals bind to
all stages. Compile its default and terrain variants, then run the existing water, nested
water/glass, underwater-camera, finite side/bottom exit, segment attenuation, and caustic fixtures.

### 3. Validate Terrain And Fog

The dry terrain keyword variant compiles. Manually validate the generated Terrain scene's
heightfield intersection, painted layer albedo, normal maps, masks, and shadows, then capture a
deterministic wavefront image fixture. Compile the water, fog, and water+fog terrain permutations
when validating those combinations.

Run bounded-fog GPU probes and deterministic light-shaft image fixtures against the wavefront
assets. Compile both terrain variants and let the combined water+fog default compile finish without
the command timeout. Move fog light-segment attenuation into the shadow stage after shadow queues
exist. Do not return fog to the common surface shader.

### 4. Debug, Adaptive, And Experiments

Build first-hit debug modes from `RayHit` and throughput/bounce/direct-light diagnostics from path
records; do not recreate `GetDebugRenderColor`. Integrate adaptive only after surface parity and
benchmarking, preserving per-pixel sample index and Welford accounting. Port temporal/spatial RIS
and path guiding separately after direct-light/shadow work is queue-based.

## Continuation Prompt

```text
Continue the GPURayTracing wavefront migration. Read AIDocs/26-wavefront-renderer-handoff.md,
AIDocs/03-compute-shader-renderer.md, AIDocs/07-shader-lighting-and-materials.md,
AIDocs/11-regression-testing.md, and AIDocs/10-benchmarking-and-performance.md.

The active final-color route is Resources/RayTracingWavefront.compute, dispatched by
Assets/Scripts/WavefrontPathTracingManager.cs. Do not restore CSMain as a runtime fallback and do
not run all-assets shader precompile. A path-level ShadowWorkItem queue is active:
CSWavefrontDirectLight enqueues active paths, CSWavefrontTraceShadows owns GetLightHittingPoint,
and CSWavefrontResolveShadowWork applies radiance before scatter. The dry default cold compile is
33.066 s on the M3 Max with the 23.505 s expensive kernel isolated in CSWavefrontTraceShadows.
Next: manually validate shadow behavior, add queue/accounting and deterministic image fixtures,
then decide whether per-candidate work items are worth the estimator decomposition. Build and
compile only RayTracingWavefront fog=0;terrain=0 after focused changes.
```
