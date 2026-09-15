# Shader Debugging And Randomness

This document covers compute-shader debug render modes and random sampling behavior.

## Debug Render Modes

`GameManager.debugRenderMode` uploads `_DebugRenderMode` to the compute shader. The active route is
wavefront-based: intended first-hit diagnostics read `_WavefrontHits`, but that buffer is overwritten
at every active intersection and does not preserve the primary hit. Direct-light and path diagnostics
use opt-in buffers, and `GlassScatter` records its primary glass decision during classification; the
presentation sky branch can still mask those records when the last hit is sky. These are open audit
findings, not validated first-hit behavior. See `27-renderer-sampling-audit-and-repair-plan.md` for repairs.

Fog uses a separate `FOG_ENABLED` wavefront variant only while an active fog volume is present, and terrain independently uses `TERRAIN_ENABLED` while terrain resources are present. Photon caustics remain runtime-controlled through `_CausticsEnabled`, while CPU photon resources and dispatches remain disabled when caustics are off. The first use of any selected renderer-asset/fog/terrain combination compiles synchronously on its first dispatch.

Available modes and intended semantics (subject to the presentation defects above):

- `FinalColor`: normal path-traced render.
- `Normals`: first-hit normal mapped from `[-1, 1]` to `[0, 1]`.
- `Albedo`: first-hit surface color.
- `Emission`: first-hit emission, clamped to displayable `[0, 1]` color.
- `DirectLight`: first-hit direct light from soft light sampling, clamped to `[0, 1]`.
- `Throughput`: remaining path throughput after iterative scattering, clamped to `[0, 1]`.
- `BounceCount`: completed non-terminal bounces normalized by `_NumBounces`.
- `HitDistance`: first-hit distance divided by `25`, clamped to grayscale `[0, 1]`; sky renders white.
- `AccelerationStructures`: visualizes whether the top-level and shadow BVHs are active. The wavefront route derives this from stored first hits and current BVH globals: first-hit surfaces encode top-level activity in red and shadow-BVH activity in green, with blue used to distinguish glass/mesh/non-mesh hits. Sky shows BVH node-count intensity.
- `GlassScatter`: first-hit glass scattering diagnostic. Non-glass surfaces render as dim albedo for context. Glass pixels render red when the sampled reflection branch is chosen and blue when it transmits; green is the Schlick Fresnel reflectance probability, and blue intensity is the opacity-derived material transmission amount.
- `Caustics`: isolates currently discoverable caustic transport. It suppresses direct lighting and returns emissive radiance only for stochastic camera paths that hit a diffuse receiver, subsequently scatter from glass or water, and then reach an emitter. Black output is expected until one of these rare paths is sampled; use high `numberOfPasses` when diagnosing the current estimator.
- `RawBeauty`: linear HDR beauty before exposure and tone mapping. This remains the existing accumulated radiance when frame accumulation is enabled.
- `FeatureNormal`, `FeatureAlbedo`, `FeatureDepth`, `FeatureIdentity`, and `FeatureValidity`: stable, unjittered primary-hit reconstruction features. Depth uses the existing `HitDistance` display range, identity uses a deterministic hash color, and sky pixels are invalid.
- `TerrainCells`: terrain-only diagnostic for the coarse acceleration-cell coordinate. The wavefront route derives it from the stored terrain first hit. Red and green show the fractional X/Z coordinate within a cell; blue highlights cell boundaries. Use it to compare a suspected terrain artifact with the acceleration grid. It renders black for non-terrain hits and sky.

Wavefront debug modes still use camera/depth-of-field jitter, but presentation reads the last stored path/hit rather than averaging diagnostic values across passes. Increasing `numberOfPasses` is not currently a reliable debug-noise reduction control.

## Randomness

`CSWavefrontGenerate` creates `RngState` for each pixel and path sample, then stores it in `WavefrontPathState` for subsequent queue stages. Camera and path dimensions use genuine Joe-Kuo Sobol direction numbers uploaded through `_SobolDirectionNumbers`; keeping the table in a GPU buffer avoids embedding a compiler-heavy array in every shared-shader variant. The per-pixel seed incorporates `_Seed`, so changing `_Seed` changes the randomized sequence without changing sample-index progression. The current `pixelScramble = Hash(_Seed ^ (pixel.x * 1973u) ^ (pixel.y * 9277u))` combines coordinate products with XOR, which admits collisions before hashing and can give different pixels identical sampler streams. It is not a collision-free pixel identity.

The sampler follows Burley's shuffled-scrambled construction. Every four-dimensional Sobol block receives a stable nested sample-index shuffle, implemented with the prefix-preserving Owen/Laine-Karras-style permutation, before the corresponding Sobol coordinates are evaluated. The RNG state caches that shuffle across all coordinates in the block instead of repeating the same permutation up to four times. A semantic dimension jump marks the shuffle as pending rather than calculating it immediately; the first consumed Sobol coordinate initializes the same shuffle, so ranges skipped by later control flow incur no sampler work. Each coordinate then receives an independent Owen scramble. Sobol evaluation walks only the set bits of the shuffled index. These optimizations are sequence-identical to the direct construction while avoiding unused or redundant ALU, zero-bit branches, and direction-buffer reads. Reusing four-dimensional blocks with distinct shuffles retains useful low-dimensional stratification while decorrelating padding across the renderer's large semantic dimension space. Dimensions above the configured limit use the hash fallback; neither generator fixes the semantic ownership defects below.

Semantic starts are explicit, but their budgets are intended reservations, not enforced non-overlap. Pixel-filter samples use dimensions `0-1`, lens samples start at `2`, and path dimensions start at `8`. Each bounce reserves 160 dimensions: fog starts at offset `0`, direct-light selection and surface coordinates at `4`, BSDF/dielectric continuation at `112`, and Russian roulette at `156`. The direct-light reservation was sized for all 16 local-RIS candidates at their worst-case six dimensions each. Ordinary `AllLights` loops and soft-shadow draws can exceed it and consume scatter coordinates before scatter resets to offset `112`; variable dielectric/mesh work also needs bounded dimension accounting. Increasing the Sobol table or configured limit changes which generator supplies a coordinate, not its semantic ownership, and cannot repair overlap. Document 27 owns the dimension audit and repair plan.

The dimensioned sampler is used for subpixel camera jitter, depth-of-field aperture jitter, stochastic area-light and environment samples, cosine/GGX bounce sampling, dielectric choices, fog, and Russian roulette. Photon-map generation retains its specialized photon sequence and hash RNG.

`GameManager` exposes `sobolDimensionLimit` and `samplingSeed` under **Sampling and Accumulation > Path Sampler**, with `randomNoise` controlling seed randomization. There is no current `useOwenScrambledSobol` toggle: coordinates below the limit use Owen-Sobol. The direction-number resource covers 2568 dimensions, matching the intended camera-plus-16-bounce reservation, not proving actual draws fit it. The default limit is 328, covering camera plus the first two reserved path bounces; serialized scenes can override it. Later coordinates use the hash fallback. A larger limit is a convergence/throughput experiment, not a correctness fix. With `randomNoise` false, C# sends a fixed scramble seed while sample indices advance. Keeping that seed and sampler configuration fixed is required to extend one progressive low-discrepancy prefix. With `randomNoise` true, the current implementation draws a new seed per shared shader binding, not merely once per frame; this can also break spatial-RIS prepass/receiver agreement. Accumulation may remain enabled across sampler changes, but mixing scrambles does not retain a single progressive prefix or establish estimator correctness.
