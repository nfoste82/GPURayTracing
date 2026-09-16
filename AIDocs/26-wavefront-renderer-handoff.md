# Wavefront Renderer Handoff

## Status

Reviewed against current code on 2026-09-15. Queue-driven surface, water, fog, and water+fog
renderers are active for final color. Surface, water, fog, water+fog, and terrain have user-reported
manual smoke coverage, not deterministic image parity. Dated compile evidence and its limits are
recorded below. Adaptive scheduling uses the existing fixed-8x8 scheduler with layered wavefront
generation and resolve. Path guiding and temporal/spatial RIS already use opt-in dry wavefront
wrappers; their integration is not remaining migration work.

Debug modes are wired, but `_WavefrontHits` is overwritten every bounce. Final presentation does
not reliably show first-hit diagnostics and can suppress separately stored diagnostics on a terminal
sky hit. Final-bounce MIS and per-path depth enforcement were repaired after this review; see
document 27 for current evidence.

This document retains architecture and historical evidence, not an active migration sequence.
[Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md) is the
authoritative active sampling repair plan. The former ordered migration tasks and continuation
prompt are superseded. No shader fixes or new validation runs are claimed by this review.

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

Low-resolution adaptive bootstrap and the following full-resolution adaptive schedule dispatch the
same selected wavefront asset. Shader warmup is keyed by the asset family plus fog/terrain bits, not
by bootstrap state, so enabling bootstrap cannot cause a second final-color shader compilation at
handoff.

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

There is one persistent `WavefrontPathState` per pixel/sample layer. Ordinary camera passes reuse
the per-pixel slots; adaptive layers have distinct indices. Queues contain indices rather than
copying the full state and its expanded medium stack.

```text
CSWavefrontClearFrame

for each camera pass:
  CSWavefrontClearQueues
  CSWavefrontGenerate
  repeat up to _NumBounces full scatter stages:
    build indirect args for current queue
    CSWavefrontIntersect
    CSWavefrontClassify
    CSWavefrontClearShadowQueue
    CSWavefrontDirectLight
    CSWavefrontTraceShadows
    optionally record direct-light path-guide observations
    CSWavefrontResolveShadowWork
    rebuild indirect args for the current path queue
    CSWavefrontScatter
    build indirect args for next queue
    CSWavefrontCopyNextQueue
    CSWavefrontPublishNextQueue
  intersect and classify once more for terminal sky/emitter radiance
  retire remaining exhausted non-emissive paths
  resolve completed paths
  optionally record terminal path-guide observations

resolve adaptive layers in deterministic per-pixel sample order, when enabled
CSWavefrontPresent
```

The explicit counter buffer holds current, next, completed, and shadow-work counts. Queue stages
use GPU-generated indirect arguments and `ComputeDispatch.DispatchIndirect`. The terminal-only pass
applies continuation MIS to sky/emitter hits and classification retires non-emissive paths at their
own consumed-event budget. Mesh helpers that consume multiple events can therefore exhaust depth
before the fixed host loop does.

`WavefrontPathState` carries current ray, radiance, throughput, full `MediumStack`, semantic
`RngState`, pixel/bounce identity, and the prior surface/PDF/direct-light/RIS/near-delta/soft-shadow
fields used for complementary sky and emitter MIS. Their presence does not establish that the MIS
pairing is correct. `RayHit` is stored after every intersection and reused by the stages of that
bounce; it is not a preserved primary-hit record.

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

The dry default targeted cold Metal compile recorded on 2026-09-09 passed on the M3 Max:

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
queue stages after one layered generation dispatch; path indices include the sample layer so all
admitted layers share one queue traversal. A final per-pixel adaptive resolve applies layer
radiances to the Welford mean/M2 in deterministic sample order, and present reads the HDR
accumulator. Adaptive bootstrap and debug modes remain excluded.
Validate deterministic count and image parity before treating this as benchmark-ready.

## Retired Legacy Routes

The separate `RayTracingAdaptiveTrace.compute` / `RayTracingWaterAdaptiveTrace.compute` route and
the monolithic `RayTracingExperimentalPathGuided.compute` / `RayTracingExperimentalRis.compute`
wrappers were retired after their active wavefront replacements landed. `GameManager` no longer
loads, serializes, dispatches, or precompiles those assets. Adaptive sampling now requires the
active wavefront final-color shader; path guiding and RIS select their wavefront wrappers directly.
Historical assets and measurements remain available in repository history.

## Coverage Limits

- **Adaptive sampling:** uses the existing fixed-8x8 scheduler. Each admitted sample layer runs the
  queue pipeline, then wavefront resolve updates the persistent Welford state and HDR accumulation.
  Low-resolution bootstrap also uses the active wavefront shader. Runtime scheduler/heatmap and
  deterministic parity validation remain required before treating adaptive results as benchmark-ready.
- **Temporal/spatial RIS reuse:** available through the opt-in dry `RayTracingWavefrontRis` wrapper.
  It preserves the existing one-pass, static-scene eligibility restrictions and uses the existing history
  and spatial-prepass resources. Local initial RIS remains active in every wavefront route.
- **Path guiding:** available only through the opt-in dry `RayTracingWavefrontPathGuided` wrapper.
  It uses a separate per-pixel guide-state buffer while enabled, preserving the 352-byte path state.
- `Normals`, `Albedo`, `Emission`, `HitDistance`, `AccelerationStructures`, and `TerrainCells`
  read the final contents of `_WavefrontHits`, not reliably the first hit. `GlassScatter` has a
  bounce-zero diagnostic record, `DirectLight` a first-bounce next-event estimate, and `Throughput`
  and `BounceCount` completion records, but final presentation can mask these on a sky hit. These
  are current correctness gaps, not validated first-hit modes. Dedicated diagnostic buffers are
  opt-in; preserve that isolation and do not revive the monolithic debug tracer.
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

The dry fog wrapper cold compile recorded on 2026-09-09 succeeded on the M3 Max:

```text
CSWavefrontDirectLight: 34.099 s
CSWavefrontScatter:      9.745 s
Total:                  46.974 s
```

Log: `/tmp/raytracing-wavefront-fog-compile.log`. In that recorded attempt, the water+fog wrapper
reached its direct-light kernel (`91.359 s`) but was still compiling when the 120-second command
timeout elapsed. Later user-reported combined-water/fog smoke coverage is not a completed targeted
cold-compile timing or deterministic image-parity result.

## Terrain Integration

Terrain remains a `TERRAIN_ENABLED` keyword permutation rather than a wrapper asset. The
`TerrainManager` enables the keyword and binds heightfield, alphamap, layer textures, normals,
masks, and buffers each time `WavefrontPathTracingManager` binds a queue-stage kernel. This makes
the same terrain implementation available to the dry, water, fog, and water+fog wrappers without
adding separate terrain asset-selection logic.

The dry terrain cold compile recorded on 2026-09-09 succeeded on the M3 Max:

```text
CSWavefrontIntersect:    9.943 s
CSWavefrontDirectLight: 51.599 s
CSWavefrontScatter:     13.638 s
Total:                  77.339 s
```

The compile completed all 13 wavefront kernels. Metal reported the existing terrain texture
sampling integer-modulus performance warnings in `RayTracingShared.hlsl`; it reported no errors.
Log: `/tmp/raytracing-wavefront-terrain-compile.log`. A structural regression test covers the
keyword declaration and per-stage terrain binding. Terrain has since been manually smoke-tested;
deterministic terrain image parity and terrain-on water/fog/water+fog combination coverage remain
unresolved.

## Compile Results

The following historical timings were recorded on 2026-09-09 on the Apple M3 Max. The surface
measurements target **only** `RayTracingWavefront`, default `fog=0;terrain=0`; the separately labeled
water result targets its wrapper. No legacy all-assets compile was run. These earlier stage layouts
and kernel counts are not new measurements of the current integrated wrappers.

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

Historical completion record (2026-09-09; subsequent manual coverage recorded through 2026-09-11):

```text
dotnet build GPURayTracing.sln --no-restore
passed with 0 warnings and 0 errors

Targeted Unity cold precompile: RayTracingWavefront only
passed for all current wavefront kernels

Manual visual smoke test
passed for the user-tested material, texture, reflection, refraction, and caustic cases
```

Unresolved validation gaps, not an ordered implementation plan:

1. Run EditMode tests without `-nographics` so GPU probes execute.
2. Recapture and review deterministic image-fixture baselines after the full-wavefront dispatch conversion.
3. Add queue tests: zero/partial/exact group counts, counter reset, overflow, odd dimensions,
   completion accounting, and maximum-bounce retirement.
4. Capture image comparisons for Root, CornellBox, ManyLights, ManySpheres, ManyMeshes,
   GlassTransmission, mesh lights, texture-heavy glTF, dry caustics, and water caustics.
5. Benchmark equal samples and equal time, including stage cost and queue occupancy. Do not use
   synchronous readback in interactive timing.
6. Terrain manually smoke-tested; capture deterministic terrain image parity.
7. Compile/test terrain-on water, fog, and water+fog variants as their combinations are needed.

Material/volume parity still includes opaque and transparent blockers, nested water/glass,
underwater cameras, finite water exits, segment attenuation, bounded-fog probes, light shafts, and
caustics. Adaptive integration still needs scheduler/heatmap, deterministic sample-count, and image
validation. Dry experimental wrappers still need production-path correctness and quality evidence;
having integrated them is not acceptance.

## Open Correctness Findings

The 2026-09-15 code review identified the following unresolved behavior. Sampling repair priorities
and acceptance gates belong to [document 27](27-renderer-sampling-audit-and-repair-plan.md), with RIS
architecture/evidence retained in [document 23](23-initial-ris-direct-lighting-plan.md).

- **First-hit debug presentation is wrong:** `CSWavefrontIntersect` overwrites `_WavefrontHits`
  on every active bounce, and `CSWavefrontPresent` reads that last hit for first-surface modes.
  A secondary surface or terminal sky therefore replaces the intended primary receiver. Its
  `DidHitSky` branch also precedes the stored direct-light, throughput, bounce-count, and
  glass-scatter branches, masking valid dedicated records when the last hit is sky.
- **RIS metadata/PDF pairing is incomplete:** continuation-hit code uses ordinary environment/light
  counts and all-light PDF reconstruction with a RIS branch multiplier, while its RIS flag records
  successful selection rather than the attempted technique. Empty outcomes and non-default counts
  are not generally complementary to the explicit RIS estimator.
- **Reuse lifecycle and receiver domains remain unsafe:** missing empty temporal writes, per-bind
  random seeds, omitted zero-target represented M, missing support/domain correction, source PDFs
  used at different receivers, receiver-facing sphere disks, and unjittered temporal features are
  detailed in document 23. Spatial precedence means both flags still select spatial-only, not a
  combined spatiotemporal estimator.

No fixes for these findings are implemented by this documentation change. Manual surface smoke
coverage and historical shader/build success must not be reported as broad renderer parity or
general reservoir-aware MIS validation.
