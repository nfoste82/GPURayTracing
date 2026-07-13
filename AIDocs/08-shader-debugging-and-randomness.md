# Shader Debugging And Randomness

This document covers compute-shader debug render modes and random sampling behavior.

## Debug Render Modes

`GameManager.debugRenderMode` uploads `_DebugRenderMode` to the compute shader. `FinalColor` uses the normal `TracePath()` output. Other modes use `GetDebugRenderColor()` to visualize a single diagnostic quantity.

`GetDebugRenderColor()` and its `CSMain` call site are compiled behind a `#pragma multi_compile _ DEBUG_RENDER` keyword. The default (non-debug) variant compiles only the `TracePath()` path, so the large debug intersection/scatter code is kept out of the hot kernel; this was a major shader compile-time reduction. `GameManager.SetShaderParameters()` calls `EnableKeyword("DEBUG_RENDER")` whenever `debugRenderMode != FinalColor` and `DisableKeyword("DEBUG_RENDER")` for `FinalColor`. Because the keyword switch produces a separate shader variant, the first time each debug mode is selected at runtime Unity compiles that variant synchronously on its first `Dispatch`, which briefly freezes the main thread. See `10-benchmarking-and-performance.md` for how `GameManager` defers that dispatch by one frame and shows a "Compiling shader variant" overlay so the stall is not mistaken for a hang.

Available modes:

- `FinalColor`: normal path-traced render.
- `Normals`: first-hit normal mapped from `[-1, 1]` to `[0, 1]`.
- `Albedo`: first-hit surface color.
- `Emission`: first-hit emission, clamped to displayable `[0, 1]` color.
- `DirectLight`: first-hit direct light from soft light sampling, clamped to `[0, 1]`.
- `Throughput`: remaining path throughput after iterative scattering, clamped to `[0, 1]`.
- `BounceCount`: completed non-terminal bounces normalized by `_NumBounces`.
- `HitDistance`: first-hit distance divided by `25`, clamped to grayscale `[0, 1]`; sky renders white.
- `AccelerationStructures`: visualizes whether the top-level and shadow BVHs are active. First-hit surfaces encode top-level activity in red and shadow-BVH activity in green, with blue used to distinguish glass/mesh/non-mesh hits. Sky shows BVH node-count intensity.
- `GlassScatter`: first-hit glass scattering diagnostic. Non-glass surfaces render as dim albedo for context. Glass pixels render red when the sampled reflection branch is chosen and blue when it transmits; green is the Schlick Fresnel reflectance probability, and blue intensity is the opacity-derived material transmission amount.
- `Caustics`: isolates currently discoverable caustic transport. It suppresses direct lighting and returns emissive radiance only for stochastic camera paths that hit a diffuse receiver, subsequently scatter from glass or water, and then reach an emitter. Black output is expected until one of these rare paths is sampled; use high `numberOfPasses` when diagnosing the current estimator.

Debug modes still use the normal camera ray generation and depth-of-field jitter path, so high `numberOfPasses` can average noisy debug samples for modes involving randomized normals, direct light, or throughput.

## Randomness

`CSMain` creates a local `uint rngState` for each pixel and sample pass using `_Seed`, pixel coordinates, and the sample index. `rand(inout rngState)` advances that local state through an integer hash and returns a normalized float in `[0, 1)`.

The RNG is used for subpixel camera jitter, depth-of-field aperture jitter, stochastic area-light samples, cosine-weighted diffuse bounce sampling, Russian roulette termination, and rough reflection normal randomization.

When `randomNoise` is false, C# sends a fixed integer seed, while `_SampleOffset` advances to provide deterministic but distinct samples each rendered frame. When `randomNoise` is true, C# additionally sends a new random integer seed each frame.
