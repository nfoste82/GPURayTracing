# Shader Debugging And Randomness

This document covers compute-shader debug render modes and random sampling behavior.

## Debug Render Modes

`GameManager.debugRenderMode` uploads `_DebugRenderMode` to the compute shader. `FinalColor` uses the normal `TracePath()` output. Other modes use `GetDebugRenderColor()` to visualize a single diagnostic quantity.

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

Debug modes still use the normal camera ray generation and depth-of-field jitter path, so high `numberOfPasses` can average noisy debug samples for modes involving randomized normals, direct light, or throughput.

## Randomness

`CSMain` creates a local `uint rngState` for each pixel and sample pass using `_Seed`, pixel coordinates, and the sample index. `rand(inout rngState)` advances that local state through an integer hash and returns a normalized float in `[0, 1)`.

The RNG is used for subpixel camera jitter, depth-of-field aperture jitter, stochastic area-light samples, cosine-weighted diffuse bounce sampling, Russian roulette termination, and rough reflection normal randomization.

When `randomNoise` is false, C# sends a fixed integer seed each frame for deterministic stable noise patterns. When `randomNoise` is true, C# sends a new random integer seed each frame.
