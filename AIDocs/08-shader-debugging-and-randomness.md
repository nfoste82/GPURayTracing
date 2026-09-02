# Shader Debugging And Randomness

This document covers compute-shader debug render modes and random sampling behavior.

## Debug Render Modes

`GameManager.debugRenderMode` uploads `_DebugRenderMode` to the compute shader. `FinalColor` uses the normal `TracePath()` output. Other modes use `GetDebugRenderColor()` to visualize a single diagnostic quantity.

`CSMain` is final-color-only. Geometry diagnostics use the separate `RayTracingDebug.compute` asset, whose `CSDebugMain` defines `DEBUG_RENDER` locally before including the shared tracer. This keeps debug intersection/scatter code out of the common final-color compile. Fog uses a separate `FOG_ENABLED` variant only while an active fog volume is present, and terrain independently uses `TERRAIN_ENABLED` while terrain resources are present. Photon caustics remain runtime-controlled through `_CausticsEnabled`, while CPU photon resources and dispatches remain disabled when caustics are off. The first use of any selected renderer-asset/fog/terrain combination compiles synchronously on its first dispatch. The debug asset currently exceeds Metal's compiler timeout; read `22-shader-compile-splitting-handoff.md` before compiling or modifying it.

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
- `RawBeauty`: linear HDR beauty before exposure and tone mapping. This remains the existing accumulated radiance when frame accumulation is enabled.
- `FeatureNormal`, `FeatureAlbedo`, `FeatureDepth`, `FeatureIdentity`, and `FeatureValidity`: stable, unjittered primary-hit reconstruction features. Depth uses the existing `HitDistance` display range, identity uses a deterministic hash color, and sky pixels are invalid.
- `TerrainCells`: terrain-only diagnostic for the coarse acceleration-cell coordinate. Red and green show the fractional X/Z coordinate within a cell; blue highlights cell boundaries. Use it to compare a suspected terrain artifact with the acceleration grid. It renders black for non-terrain hits and sky.

Debug modes still use the normal camera ray generation and depth-of-field jitter path, so high `numberOfPasses` can average noisy debug samples for modes involving randomized normals, direct light, or throughput.

## Randomness

`CSMain` creates a local four-component sampling state for each pixel and path sample. Camera and path dimensions use genuine Joe-Kuo Sobol direction numbers uploaded through `_SobolDirectionNumbers`; keeping the table in a GPU buffer avoids embedding a compiler-heavy array in every shared-shader variant. The per-pixel seed incorporates `_Seed`, so changing `_Seed` changes the randomized sequence without changing sample-index progression.

The sampler follows Burley's shuffled-scrambled construction. Every four-dimensional Sobol block receives a stable nested sample-index shuffle, implemented with the prefix-preserving Owen/Laine-Karras-style permutation, before the corresponding Sobol coordinates are evaluated. The RNG state caches that shuffle across all coordinates in the block instead of repeating the same permutation up to four times. Each coordinate then receives an independent Owen scramble. Sobol evaluation walks only the set bits of the shuffled index; these optimizations are sequence-identical to the direct construction while avoiding redundant ALU, zero-bit branches, and direction-buffer reads. Reusing four-dimensional blocks with distinct shuffles retains useful low-dimensional stratification while decorrelating padding across the renderer's large semantic dimension space. Dimensions above the configured limit use the unbiased hash fallback.

Dimensions are explicit rather than implicitly inherited from earlier control flow. Pixel-filter samples use dimensions `0-1`, lens samples start at `2`, and path dimensions start at `8`. Each bounce owns 160 dimensions: fog starts at offset `0`, direct-light selection and surface coordinates at `4`, BSDF/dielectric continuation at `112`, and Russian roulette at `156`. The direct-light range accommodates all 16 local-RIS candidates at their worst-case six dimensions each, and the scatter range accommodates rough dielectric rejection plus closed-mesh exit sampling. This prevents one fixed-seed coordinate from being reused for multiple decisions. The configured Sobol limit can cover the complete semantic range; dimensions above it use the hash generator.

The dimensioned sampler is used for subpixel camera jitter, depth-of-field aperture jitter, stochastic area-light and environment samples, cosine/GGX bounce sampling, dielectric choices, fog, and Russian roulette. Photon-map generation retains its specialized photon sequence and hash RNG.

`GameManager` exposes these controls under **Sampling and Accumulation > Path Sampler**: `useOwenScrambledSobol`, `sobolDimensionLimit`, `samplingSeed`, and `randomNoise`. The direction-number resource covers 2568 dimensions, enough for all semantic dimensions at the maximum 16-bounce setting. The default limit is 168, covering camera and first-bounce decisions; later bounces use the faster hash fallback because a CornellBox wall-clock sweep retained a convergence advantage at this limit while avoiding most of the full-dimensional sampler cost. Set the limit to 2568 when equal-sample convergence is more important than throughput. When `randomNoise` is false, C# sends the fixed scramble seed while `_SampleOffset` advances to provide deterministic but distinct samples each rendered frame. When `randomNoise` is true, C# additionally sends a new random integer seed each frame. Changing any path-sampler setting resets progressive accumulation.
