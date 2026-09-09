# Wavefront Renderer Handoff

## Status

**Current phase: the queue-driven surface and water renderers are active for final color. The
surface route is Metal-compiled and manually smoke-tested. Fog, adaptive scheduling, path guiding,
temporal/spatial RIS, and general debug modes have not yet been ported to the active wavefront route.**

The user explicitly chose not to preserve `CSMain` as a runtime fallback. Use Git history if old
behavior must be consulted. Do not restore an old path merely as a fallback during this migration.

## Active Route

`GameManager.ActiveFinalColorShader` lazily loads the dry
`Resources/RayTracingWavefront.compute` or water
`Resources/RayTracingWavefrontWater.compute` asset into nonserialized fields, so existing scenes
require no Inspector assignment. The frame route is:

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
    CSWavefrontDirectLight
    CSWavefrontScatter
    build indirect args for next queue
    CSWavefrontCopyNextQueue
    CSWavefrontPublishNextQueue
  retire paths still active at the maximum bounce
  resolve completed paths

CSWavefrontPresent
```

The explicit counter buffer holds current, next, and completed path counts. Queue stages use
GPU-generated indirect arguments and `ComputeDispatch.DispatchIndirect`. Retirement after the
fixed host-side bounce loop is intentional: it accounts for all camera paths at the former maximum
bounce limit instead of silently dropping survivors.

`WavefrontPathState` carries current ray, radiance, throughput, full `MediumStack`, semantic
`RngState`, pixel/bounce identity, and the prior surface/PDF/direct-light/RIS/near-delta/soft-shadow
fields needed for complementary sky and emitter MIS. `RayHit` is stored after intersection rather
than recomputed.

Current C# buffer strides must match the HLSL layouts:

```text
WavefrontPathState: 348 bytes
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

## Unsupported Or Bypassed

- **Fog:** no free-flight events, fog scattering, fog direct lighting, or fog shadow attenuation.
- **Adaptive sampling:** the existing layered Welford scheduler remains in source but is bypassed.
- **Path guiding and temporal/spatial RIS reuse:** bypassed. Local initial RIS remains active.
- **General geometry debug modes:** still unavailable. Do not revive the monolithic debug tracer.
- **Wavefront-native shadow queues:** `CSWavefrontDirectLight` still owns candidate generation and
  shadow traversal through `GetLightHittingPoint`.

Caustics still run outside camera tracing. Treat water-dependent caustics as unsupported until the
water wavefront path passes regressions.

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
4. Capture dry image comparisons for Root, CornellBox, ManyLights, ManySpheres, ManyMeshes,
   GlassTransmission, mesh lights, texture-heavy glTF, and dry caustics.
5. Benchmark equal samples and equal time, including stage cost and queue occupancy. Do not use
   synchronous readback in interactive timing.
6. Compile and test the terrain keyword variant separately.

## Ordered Remaining Work

### 1. Extract Shadow Work

This is the highest-value next compile-time task because `CSWavefrontDirectLight` is 33.7 seconds.

1. Define a `ShadowWorkItem`: path ID, finite/environment identity, sampled direction/endpoint,
   unshadowed contribution, PDFs/RIS normalization, and needed MIS metadata.
2. Change `CSWavefrontDirectLight` to candidate generation only.
3. Add a shadow queue and `CSTraceShadowRays` owning `GetShadowTransmittance`.
4. Add a resolve stage that applies visibility-weighted contribution before scatter.
5. Preserve transparent shadows, local RIS, environment/mesh lights, primary soft shadows, and
   complementary MIS.
6. Re-run the targeted compile and image tests.

### 2. Validate Water

`RayTracingWavefrontWater.compute` now defines `WATER_ENABLED` before including the shared
wavefront stages, and `GameManager` selects it only while `HasWaterVolume`. Water globals bind to
all stages. Compile its default and terrain variants, then run the existing water, nested
water/glass, underwater-camera, finite side/bottom exit, segment attenuation, and caustic fixtures.

### 3. Fog Assets

Create separate fog and water+fog wavefront assets. After closest-hit traversal, sample free-flight
against `RayHit.distance`, attenuate through the active medium, and classify fog as a volume event.
Move fog light-segment attenuation into the shadow stage after shadow queues exist. Do not return
fog to the common surface shader.

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
not run all-assets shader precompile. The common surface wavefront compile is 44.1 s cold on the M3
Max; CSWavefrontDirectLight is 33.7 s. Next: add a ShadowWorkItem queue, splitting candidate
generation from shadow traversal while preserving transparent shadows, local RIS, environment/mesh
lights, and complementary MIS. Use explicit counters plus GPU-built indirect dispatch. Build and
compile only RayTracingWavefront fog=0;terrain=0 after focused changes, and add queue/accounting
tests before broad image/performance claims.
```
