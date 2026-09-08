# Shader Compile Splitting Handoff

## Purpose

This document records the compile-time reduction work completed on August 25, 2026, the current
Metal measurements, the remaining debug-kernel blocker, and the required low-disruption workflow
for continuing. Read this before changing compute-asset boundaries or starting another cold shader
compile.

## Goal

Reduce time between renderer edits and usable compile feedback without changing final-color render
behavior. Do not use one broad cold run as an edit-time check: a full Metal compile can take more
than 30 minutes.

## Current Asset Layout

The former monolithic `Assets/Scripts/RayTracingCompute.compute` was split as follows:

```text
RayTracingCompute.compute              CSMain final color only; FOG_ENABLED x TERRAIN_ENABLED
RayTracingWater.compute                CSMain water-capable final color; FOG_ENABLED x TERRAIN_ENABLED
RayTracingExperimentalPathGuided.compute Opt-in path-guided final color; FOG_ENABLED x TERRAIN_ENABLED
RayTracingExperimentalRis.compute      Opt-in temporal/spatial RIS final color; FOG_ENABLED x TERRAIN_ENABLED x TEMPORAL_RIS_ENABLED
RayTracingDebug.compute                CSDebugMain; FOG_ENABLED x TERRAIN_ENABLED
RayTracingAdaptiveTrace.compute        guidance/root trace/resolve/reference; FOG_ENABLED x TERRAIN_ENABLED
RayTracingAdaptiveScheduler.compute    clear/classify/remap/compact/diagnostics; no variants
RayTracingFeatures.compute             CSFeatures; FOG_ENABLED x TERRAIN_ENABLED
RayTracingFocus.compute                CSFocusQuery; TERRAIN_ENABLED only
RayTracingUtility.compute              ClearAccumulation only; no shared tracer include
RayTracingRegressionProbe.compute      CSRegressionProbe only; no variants
RayTracingCaustics.compute             existing separate caustics kernels
```

`GameManager` loads split assets from `Resources` using Unity object null checks, so old serialized
scenes do not need manual inspector assignment. It dispatches final color, geometry diagnostics,
features, focus queries, utility clears, and adaptive scheduler/trace work through their owning
asset. `CameraManager.DispatchPendingFocusQuery` receives the focus asset with a callback that
binds parameters to that same asset.

`Assets/Scripts/RayTracingAdaptiveSchedulerShared.hlsl` contains adaptive-only declarations so the
scheduler does not include the full tracer. `RayTracingShared.hlsl` remains shared by tracing,
debug, feature, focus, regression, and caustics assets.

The ordinary final-color asset compiles without water geometry, finite-volume, medium, absorption,
or scatter code. `GameManager` selects `RayTracingWater.compute` and matching water-capable
feature, focus, and adaptive-trace assets only while `HasWaterVolume` is true. This avoids adding
a water keyword to the normal fog/terrain matrix.

The ordinary final-color asset also excludes experimental path-guiding and temporal/spatial RIS
source entirely. `GameManager` selects their dedicated assets only while the corresponding
experimental mode is runnable; unsupported water and adaptive combinations retain the production
renderer. Owen-scrambled Sobol is always used up to the configurable dimension limit, with the
existing hash RNG handling later dimensions.

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

## Main Compile Result

The precompiler now records `shaderAsset` and `kernel` in
`Library/RayTracingShaderCompileStats.csv`, retaining older rows and appending a V2 header before
new-format rows. The command-line tool supports a single named asset:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingColdShaderPrecompile \
  -rayTracingPrecompileAsset RayTracingCompute \
  -logFile /tmp/raytracing-main-compile.log
```

Accepted asset values are the compute asset basename, for example `RayTracingDebug`,
`RayTracingAdaptiveTrace`, `RayTracingFeatures`, `RayTracingFocus`, and
`RayTracingAdaptiveScheduler`. Omit `-rayTracingColdShaderPrecompile` for a cache-preserving warm
dispatch. Pass `-rayTracingPrecompileVariant fog=0;terrain=0` with one selected asset to compile
one fog/terrain combination at a time; quote the value when invoking through a shell.
`-rayTracingPrecompileAllVariants` deliberately compiles every renderer asset and must not be used
during normal iteration.

The first completed cold Metal matrix after the split on the Apple M3 Max was:

```text
Asset                                      Kernel  Variant                  First dispatch
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=0;terrain=0          202.065 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=1;terrain=0          468.843 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=0;terrain=1          220.698 s
Assets/Scripts/RayTracingCompute.compute   CSMain  fog=1;terrain=1          544.412 s
Total                                                               1,436.018 s (23m 56s)
```

This proves that fog is the dominant final-color compile cost, especially combined with terrain.
The no-fog/no-terrain final-color path is about 202 seconds cold. This measurement is not directly
comparable to the earlier reported 8-minute aggregate because it was deliberately cold, compiled
all four final-color combinations independently, and Unity compilation varies materially.

## Debug Blocker

The `RayTracingDebug.compute` split fixed the asset boundary but did not reduce debug code
complexity. Its `CSDebugMain` defines `DEBUG_RENDER 1` before including `RayTracingShared.hlsl` so
the existing `GetDebugRenderColor()` implementation is compiled only in that asset.

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

Do not raise that timeout as the first response. The useful result is that `GetDebugRenderColor()`
still inlines too much traversal, direct-light, and scattering logic for Metal. The next change
should simplify or split the debug implementation, then compile **only** `RayTracingDebug` to
validate it. Do not run the all-assets command until the targeted debug asset compiles.

### August 25, 2026 Follow-up Attempt

The unreachable legacy `TraceCausticPaths()` branch was removed from `GetDebugRenderColor()`.
`DebugCaustics` already uses the dedicated `RayTracingCaustics.compute` `CSCausticsDebug` kernel
when caustics are enabled, and is excluded from `UsesGeometryDebugShader()` otherwise. Removing
the dead branch preserves the geometry diagnostic display semantics and their RNG sequence while
excluding its full scatter/medium traversal path from `CSDebugMain`.

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
at a time. Use individual final-color variants while iterating. The command-line equivalent is
preferable for a cold timing because it keeps the editor usable.

```sh
# Default final-color only. Expected scale: about 3-4 minutes cold on the measured M3 Max.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingColdShaderPrecompile -rayTracingPrecompileAsset RayTracingCompute \
  -logFile /tmp/raytracing-main.log

# Debug asset only. Run after a debug-code change; currently expected to expose the timeout.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
   -rayTracingColdShaderPrecompile -rayTracingPrecompileAsset RayTracingDebug \
   -logFile /tmp/raytracing-debug.log

# One debug variant only. Repeat as separate Unity processes to observe progress and isolate errors.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingColdShaderPrecompile -rayTracingPrecompileAsset RayTracingDebug \
  -rayTracingPrecompileVariant 'fog=0;terrain=0' \
  -logFile /tmp/raytracing-debug-fog0-terrain0.log

# Adaptive trace only. Do not combine with other assets while debugging it.
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine \
  -rayTracingColdShaderPrecompile -rayTracingPrecompileAsset RayTracingAdaptiveTrace \
  -logFile /tmp/raytracing-adaptive-trace.log
```

When another Unity editor already has the project open, batchmode may be unable to acquire the
project lock. Close that editor before a command-line compile. Do not use `-quit` for Test Runner
jobs, but it is appropriate for the precompiler entry point because that method calls
`EditorApplication.Exit` itself.

## Remaining Work

1. Refactor `GetDebugRenderColor()` in `RayTracingShared.hlsl` so `RayTracingDebug.compute` no
   longer times out. Preserve displayed debug values and random sequence semantics. The likely
   direction is separate small diagnostic kernels or a smaller set of helper paths rather than
   one all-purpose debug function that carries direct lighting and full scattering variants.
2. Compile only `RayTracingDebug` after each focused change. Stop after one cold attempt; inspect
   the log and CSV before changing more code.
3. Once the debug asset compiles, collect one cold timing for each asset class, one process at a
   time: debug, adaptive trace, features, focus, scheduler, utility, regression probe. Do not
   clear the shader cache between warm measurements.
4. Run focused GPU parity and image regression tests only after the debug asset compiles. No full
   test or all-assets precompile was completed in this session after the split.
5. Consider fog-specific decomposition only after final-color and debug timing is understood.
   Fog accounts for roughly 55% of the current four-variant final-color cold total. Do not return
   fog or terrain to runtime branches; that previously caused a much larger common-kernel compile.

## Verification State

Completed after the split:

```text
dotnet build GPURayTracing.sln --no-restore    passed, 0 warnings and 0 errors
```

The full cold all-assets timing command was intentionally stopped by the tool timeout after the
four final-color variants completed. It did not validate later assets. The targeted debug command
also exceeded the external tool timeout after Unity's own two 10-minute compiler-task timeouts.
Do not describe the shader split as GPU regression-tested until the targeted compile and focused
tests listed above complete.
