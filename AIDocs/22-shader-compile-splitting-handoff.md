# Shader Compile Splitting Handoff

## Purpose

This document retains the August 25, 2026 compile-splitting evidence and describes targeted
precompilation of the active wavefront assets. The old debug-kernel timeout is historical: that
route is retired, not a current repair prerequisite. For current renderer/sampling defects and
validation gates, use [Renderer Sampling Audit And Repair Plan](27-renderer-sampling-audit-and-repair-plan.md).

## Goal

Reduce time between renderer edits and usable compile feedback without changing final-color render
behavior. Do not use one broad cold run as an edit-time check: a full Metal compile can take more
than 30 minutes.

## Current Asset Layout

The monolithic `CSMain` renderer and its water, fog, water+fog, and geometry-debug wrappers were
retired after wavefront replacements landed. The active final-color assets are
`RayTracingWavefront.compute`, `RayTracingWavefrontWater.compute`,
`RayTracingWavefrontFog.compute`, and `RayTracingWavefrontWaterFog.compute`; image fixtures now
dispatch the same queue pipeline. The historical layout and timings below are retained as context.

`RayTracingWavefrontPathGuided.compute` and `RayTracingWavefrontRis.compute` are opt-in dry
wrappers for path guiding and experimental temporal/spatial RIS. Adaptive sample layers use the
same selected wavefront renderer, with scheduling in `RayTracingAdaptiveScheduler.compute`.
Geometry diagnostics also use wavefront stages; there is no active `RayTracingDebug` asset.
Features, focus queries, spatial RIS prepass, utility, regression probes, and caustics retain
separate owning assets. Water/fog wrappers isolate volume code; terrain remains a keyword variant.

`GameManager` loads these assets from `Resources` and binds scene parameters to their owning
stages. `RayTracingShared.hlsl` owns shared intersection, material, lighting, medium, and RNG
helpers; `RayTracingAdaptiveSchedulerShared.hlsl` isolates scheduler declarations from the tracer.
Camera-side photon gathering runs in `RayTracingCaustics.compute` with a utility composite pass.
See `26-wavefront-renderer-handoff.md` for stage order and dated wavefront compile evidence.

## Historical Split Layout

The former monolithic `Assets/Scripts/RayTracingCompute.compute` was split as follows:

```text
RayTracingCompute.compute              CSMain surface final color; TERRAIN_ENABLED
RayTracingWater.compute                CSMain water-capable final color; TERRAIN_ENABLED
RayTracingFog.compute                  CSMain fog-capable final color; TERRAIN_ENABLED
RayTracingWaterFog.compute             CSMain water + fog final color; TERRAIN_ENABLED
RayTracingDebug.compute                CSDebugMain; FOG_ENABLED x TERRAIN_ENABLED
RayTracingAdaptiveScheduler.compute    clear/classify/remap/compact/diagnostics; no variants
RayTracingFeatures.compute             CSFeatures; FOG_ENABLED x TERRAIN_ENABLED
RayTracingFocus.compute                CSFocusQuery; TERRAIN_ENABLED only
RayTracingUtility.compute              ClearAccumulation only; no shared tracer include
RayTracingRegressionProbe.compute      CSRegressionProbe only; no variants
RayTracingCaustics.compute             existing separate caustics kernels
```

The split subsequently isolated water and fog into separate final-color assets and moved
camera-side caustics gathering out of the megakernel. The fog and surface branches in the former
`TracePathWithDirectLight()` converged before one `GetLightHittingPoint()` call to avoid duplicate
inlining of the light-selection/shadow-traversal graph. These were compile-size reductions in the
retired renderer, not descriptions of the current dispatch route.

### August 25, 2026 Water-Free Default Attempt

The default asset was changed to compile out water-only declarations and paths, with companion
water-capable final-color, feature, focus, and adaptive-trace assets selected by `HasWaterVolume`.
After `dotnet build GPURayTracing.sln --no-restore` and `git diff --check` both passed, exactly
one cold compile was attempted for the common default path:

```text
Asset                                   Kernel  Variant                  First dispatch  Result
Assets/Scripts/RayTracingCompute.compute CSMain  fog=0;terrain=0          7 ms            compile error
```

Metal reported `undeclared identifier 'EstimateWaterDistanceAlongSegment'` in
`RayTracingShared.hlsl(3061)`, meaning the water-free direct-light path still references a
water-only helper. The `7 ms` dispatch timing is invalid because the kernel did not compile; its
warm dispatch was `0 ms`. The precompiler log is
`/tmp/raytracing-main-waterfree-fog0-terrain0.log`, and the appended CSV row has shader hash
`40c10c480c35cf3e8f8404edfc9bd5d7`. No additional variants or assets were compiled after this
error.

The direct-light finite-light water-attenuation branch was subsequently moved inside the existing
`WATER_ENABLED` guard. After the same lightweight checks passed, one retry of the same cold
variant completed successfully:

```text
Asset                                   Kernel  Variant                  First dispatch  Warm dispatch
Assets/Scripts/RayTracingCompute.compute CSMain  fog=0;terrain=0          100.517 s       0 ms
```

This is a 101.548 s (50.3%) reduction from the earlier 202.065 s no-fog/no-terrain cold timing.
The successful retry log is `/tmp/raytracing-main-waterfree-fog0-terrain0-retry.log`; the CSV row
has shader hash `5f64b696cad49fb384ea73d095a296bb`. No further variants or assets were compiled.

## Historical Main Result

The precompiler now records `shaderAsset` and `kernel` in
`Library/RayTracingShaderCompileStats.csv`, retaining older rows and appending a V2 header before
new-format rows. Current commands are listed under Precompiler Commands below; the asset names
in this historical measurement table are retired.

The first completed cold Metal matrix after the split on the Apple M3 Max was:

```text
Asset                                      Kernel  Variant                  First dispatch
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=0;terrain=0          202.065 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=1;terrain=0          468.843 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=0;terrain=1          220.698 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=1;terrain=1          544.412 s
Total                                                               1,436.018 s (23m 56s)
```

In this historical matrix, fog dominated final-color compile cost, especially with terrain.
The then-current no-fog/no-terrain final-color path took about 202 seconds cold. This is not an
estimate for the active wavefront renderer. The measurement is not directly
comparable to the earlier reported 8-minute aggregate because it was deliberately cold, compiled
all four final-color combinations independently, and Unity compilation varies materially.

## Historical Debug Timeout

The former `RayTracingDebug.compute` split fixed the asset boundary but did not reduce debug code
complexity. Its `CSDebugMain` defined `DEBUG_RENDER 1` before including `RayTracingShared.hlsl`
so the then-current `GetDebugRenderColor()` implementation compiled only in that asset.

A targeted cold compile of `RayTracingDebug` reached Unity's compiler task timeout twice:

```text
Debug CSDebugMain fog=0;terrain=0: 600.040 s, compiler timed out
Debug CSDebugMain fog=1;terrain=0: 600.184 s, compiler timed out
```

Unity reported:

```text
Compiler timed out. This can happen with extremely complex shaders or when processing
resources are limited. UNITY_SHADER_COMPILER_TASK_TIMEOUT_MINUTES can override the limit.
```

This recorded excessive traversal, lighting, and scattering inlining in the retired debug kernel.
It is not a current blocker and does not gate active wavefront tests or compiles.

### August 25, 2026 Follow-up Attempt

The unreachable legacy `TraceCausticPaths()` branch was removed from `GetDebugRenderColor()`.
At that time, `DebugCaustics` already used the dedicated `RayTracingCaustics.compute`
`CSCausticsDebug` kernel when caustics were enabled and was excluded from
`UsesGeometryDebugShader()` otherwise. The change removed the dead branch's full scatter/medium
traversal path from `CSDebugMain` without intentionally changing geometry diagnostic semantics.

The command-line precompiler now accepts `-rayTracingPrecompileVariant` for a single selected
asset, allowing each fog/terrain combination to run in its own process. After `dotnet build
GPURayTracing.sln --no-restore` and `git diff --check` both passed, exactly one targeted cold
compile was run:

```text
Asset                                      Kernel       Variant                  First dispatch  Result
Assets/Resources/RayTracingDebug.compute   CSDebugMain  fog=0;terrain=0          600.133 s       compiler timed out
```

Unity reported `Compiler timed out` for `RayTracingDebug.compute - CSDebugMain`; the kernel was
invalid for the attempted warm dispatch (`0 ms`). The precompiler log is
`/tmp/raytracing-debug-fog0-terrain0.log`, and the appended CSV row has shader hash
`f257f579baf0113180337f6482ade1db`. No further debug variants or shader assets were compiled.

## Precompiler Commands

Editor menus under `Tools > Ray Tracing > Precompile Compute Shader` provide one asset category
at a time. For the common dry path, choose `Main Final Color > Default (Fog Off, Terrain Off)`.
Use one active asset and one variant per process while iterating; the tool dispatches that asset's
registered kernels and records per-kernel timings. These commands are validation recipes, not new
compile evidence from this documentation audit.

```sh
# Dry wavefront stages, terrain off. Preserves the existing shader cache.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingPrecompileAsset RayTracingWavefront \
  -rayTracingPrecompileVariant 'fog=0;terrain=0' \
  -logFile /tmp/raytracing-wavefront-surface.log

# Water wavefront stages, terrain off. Run separately when that wrapper needs validation.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingPrecompileAsset RayTracingWavefrontWater \
  -rayTracingPrecompileVariant 'fog=0;terrain=0' \
  -logFile /tmp/raytracing-wavefront-water.log

# Fog wavefront stages, terrain off. Fog is fixed on by this wrapper.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingPrecompileAsset RayTracingWavefrontFog \
  -rayTracingPrecompileVariant 'fog=1;terrain=0' \
  -logFile /tmp/raytracing-wavefront-fog.log

# Adaptive scheduler only; adaptive tracing belongs to the selected wavefront asset.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingPrecompileAsset RayTracingAdaptiveScheduler \
  -logFile /tmp/raytracing-adaptive-scheduler.log
```

Use `RayTracingWavefrontWaterFog` with `fog=1;terrain=0` for water+fog. The opt-in dry wrappers
`RayTracingWavefrontRis` and `RayTracingWavefrontPathGuided` accept `fog=0;terrain=0`.
Set `terrain=1` for a terrain-enabled variant. Other accepted basenames include
`RayTracingFeatures`, `RayTracingFocus`, `RayTracingSpatialRisPrepass`, `RayTracingUtility`, and
`RayTracingRegressionProbe`. Add `-rayTracingColdShaderPrecompile` only when a deliberate cold
measurement is needed; it clears the generated shader cache. Do not use
`-rayTracingPrecompileAllVariants` during normal iteration. Inspect each log and CSV before
starting another expensive compile, and do not use `-nographics` for GPU dispatch.

When another Unity editor already has the project open, batchmode may be unable to acquire the
project lock. Close that editor before a command-line compile. Do not use `-quit` for Test Runner
jobs, but it is appropriate for the precompiler entry point because that method calls
`EditorApplication.Exit` itself.

## Historical Verification

Completed after the split:

```text
dotnet build GPURayTracing.sln --no-restore    passed, 0 warnings and 0 errors
```

The full cold all-assets timing command was intentionally stopped by the tool timeout after the
four final-color variants completed. It did not validate later assets. The targeted debug command
also exceeded the external tool timeout after Unity's own two 10-minute compiler-task timeouts.
Those runs did not establish GPU regression parity. They also do not describe the validation
state of the current wavefront pipeline. Current defects and required validation are tracked in
document 27; historical compile success alone is not evidence that a sampling repair is correct.
