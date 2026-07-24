# Denoising And Upscaling

This document records the renderer work required to add path-tracing denoising and image reconstruction, including the implications of upgrading from Unity `2022.3.72f1` to Unity `6.3 LTS`. It is architectural guidance for a future implementation, not a claim that the current renderer supports DLSS, FSR, MetalFX, or Unity STP.

## Executive Summary

The project can benefit substantially from rendering fewer camera pixels and reconstructing a native-resolution image, but its current output contract is not suitable for modern spatial or temporal reconstruction. The renderer currently produces one full-resolution, tone-mapped color texture and does not expose stable depth, motion, material, or history-validity data.

The common renderer foundation is more important than selecting a vendor SDK:

1. Separate internal render resolution from display resolution.
2. Keep beauty radiance linear and HDR until reconstruction is complete.
3. Generate stable depth, normal, albedo, identity, and validity buffers alongside beauty.
4. Add deterministic frame-level camera jitter and previous-frame camera state.
5. Generate motion vectors for camera and object motion.
6. Add history rejection for disocclusion, transparency, reflection, refraction, and animated water.
7. Denoise path-tracing noise before or as part of temporal upscaling.

Unity 6.3 improves the available integration paths, particularly through URP/HDRP, Spatial-Temporal Post-Processing (STP), HDRP FSR 2, and HDRP DLSS Super Resolution. An engine upgrade by itself does not make this custom compute renderer reconstruction-ready.

## Terminology And Scope

Denoising and upscaling solve related but different problems:

- A **path-tracing denoiser** estimates a clean radiance signal from a low and noisy sample count, usually with depth, normal, albedo, and temporal history guidance.
- A **temporal upscaler** reconstructs a larger anti-aliased image from lower-resolution frames, motion vectors, depth, camera jitter, and history.
- A **spatial upscaler** enlarges the current image without temporal history. It is easier to integrate but reconstructs less detail.
- **DLSS Super Resolution**, **FSR 2/4**, **MetalFX Temporal Scaling**, and **Unity STP** are primarily reconstruction/upscaling technologies. They should not be assumed to clean severe path-tracing noise.
- **DLSS Ray Reconstruction** is intended to replace ray-tracing denoisers, but it requires a richer and more specialized integration than DLSS Super Resolution.

Temporal upscaling can hide modest noise, but severe stochastic noise commonly becomes shimmer, trails, unstable detail, or ghosting. The pipeline should therefore include a dedicated denoising stage or explicitly choose a reconstruction system that supports ray-traced signal reconstruction.

## Current Renderer Contract

The current frame pipeline is approximately:

```text
Full-resolution path trace
    -> optional progressive running average
    -> exposure and ACES tone map
    -> Graphics.Blit to camera destination
```

Important implementation details:

- `GameManager.CreateOutputTexture()` creates `_outputTexture` and `_accumulationTexture` at the camera source dimensions.
- `GameManager.EnsureOutputTextureSize()` keeps those textures at display resolution and updates the camera aspect ratio.
- `RayTracingCompute.compute` exposes only `Result` and `AccumulationResult` as image outputs.
- `CSMain` uses independent stochastic subpixel positions for every path sample.
- Final-color accumulation is a simple running average and is reset when camera, scene, or quality state changes.
- Frame accumulation is disabled for animated water because the current history model cannot reproject motion.
- Exposure and ACES tone mapping occur inside `CSMain` before `Result` is presented.
- `RayTracingCameraRenderer.OnRenderImage()` invokes `GameManager.RenderImage()`, and presentation ends with `Graphics.Blit(_outputTexture, dest)`.
- Existing normal and albedo debug modes contain useful first-hit logic, but diagnostic display output is not a stable denoiser feature-buffer interface.

The intended future pipeline is approximately:

```text
Low-resolution path trace
    -> linear HDR beauty and feature buffers
    -> spatial/temporal path-tracing denoiser
    -> temporal or spatial upscaler
    -> exposure and tone mapping
    -> native-resolution display output
```

The exact denoise/upscale ordering can depend on the selected SDK. Preserve separate stages and raw intermediate outputs so different backends can be evaluated without restructuring the path tracer again.

## Required Renderer Foundation

### Internal And Display Resolution

Introduce an internal render scale independent of the camera destination. Typical reconstruction scales are approximately:

- Quality: `67%` width and height.
- Balanced: `58%` width and height.
- Performance: `50%` width and height.

Rendering at half width and half height traces one quarter of the camera pixels. Actual speedup will be lower after denoising, reconstruction, and fixed per-frame work, but primary path cost should fall substantially.

Likely persistent or transient resources include:

- Linear HDR beauty at internal resolution.
- Linear depth or hit distance.
- World- or view-space shading normal.
- Diffuse albedo.
- Roughness/material classification if useful to the chosen denoiser.
- Stable object/material identity.
- Validity and sample weight.
- Motion vectors.
- Reactive/transparency/history-rejection mask.
- Previous-frame feature and color histories.
- Denoised internal-resolution color.
- Native-resolution reconstructed HDR output.
- Native-resolution tone-mapped presentation output.

Prefer formats such as half precision where their range and precision are sufficient. Do not default every auxiliary texture to `ARGBFloat`; reconstruction can otherwise exchange ray cost for excessive memory bandwidth.

### Linear HDR Separation

Move exposure and `ACESFilmicToneMap()` out of `CSMain` and into a final presentation pass. Denoisers and temporal reconstruction generally expect linear or documented pre-exposed HDR data, not the current tone-mapped `Result`.

Retain an inspectable raw beauty output and an undenoised presentation option. The disabled reconstruction path must preserve current final-color behavior within reviewed regression tolerance and should not allocate unnecessary reconstruction resources.

### Feature Buffers

At minimum, generate these buffers with the same camera sample and primary visibility decision as beauty:

- Linear HDR beauty.
- Shading normal.
- Diffuse albedo.
- Depth.
- Validity/sample weight.

Useful later additions are:

- Roughness and material type.
- Stable object and primitive identity.
- Diffuse/specular radiance separation.
- First-hit and feature depth when they differ.
- Emission and caustic radiance or a conservative preservation mask.

Every feature buffer needs a debug mode with documented encoding and range. Reset or invalidate it under the same camera and scene changes that invalidate beauty history.

Feature semantics for mirrors, glass, and water must be explicit. Primary-surface depth and normal can describe a glass boundary while visible radiance comes from geometry behind it. That mismatch can cause edge bleeding and rejected or incorrectly retained history. Evaluate whether near-delta reflection/transmission features should follow the path to the first stable diffuse surface, while retaining separate primary-surface data for motion and boundary decisions.

### Camera Jitter And History State

Replace unrelated per-sample camera jitter as the temporal reconstruction signal with one known frame-level projection jitter, commonly from a low-discrepancy sequence. Upload the exact jitter to the reconstruction backend.

Path samples may continue to use independent random values for BRDF, direct-light, aperture, and continuation sampling. The camera projection position used for temporal reconstruction must be deterministic and known at frame scope.

Retain at least:

- Current and previous jittered and unjittered view-projection matrices.
- Current and previous jitter offsets.
- Current and previous internal resolution.
- Frame index.
- Camera-cut/history-reset state.

Depth of field requires deliberate treatment. The current shader applies independent three-axis origin jitter per sample. Temporal reconstruction may need a stable pinhole visibility/motion signal separate from stochastic lens samples, or conservative rejection in defocused regions.

### Motion Vectors

For a static scene, initial motion vectors can be derived by projecting the current world-space hit position with current and previous view-projection matrices.

Dynamic ray-traced geometry requires previous object state:

- Previous sphere transforms.
- Previous mesh object transforms or previous world-space triangle positions.
- Stable object IDs across buffer rebuilds.
- Previous procedural water phase/time and a policy for wave displacement motion.
- Explicit disocclusion when identity or visibility changes.

Reflected and refracted radiance does not necessarily move like the primary surface. Start with primary-hit motion plus conservative reactive/history-rejection masks around glass, water, mirrors, emissive surfaces, rapidly changing caustics, and invalid feature paths. Refine specular motion only if measured artifacts justify the added complexity.

### Temporal History

The current unbounded running average is appropriate for progressive refinement of an unchanged view, but it is not suitable for an interactive moving scene. A real-time temporal path should:

- Reproject previous data with motion vectors.
- Reject history on depth, normal, identity, material, or validity mismatch.
- Detect disocclusions and camera cuts.
- Clamp or bound history to suppress stale bright samples and fireflies.
- Give recent samples meaningful weight instead of averaging against arbitrarily old history.
- Reset all relevant histories together when the reconstruction contract changes.

Progressive static accumulation can remain as a separate final-render mode. Do not force one history policy to serve both interactive reconstruction and converged still rendering.

## Denoiser Strategy

Implement denoising in stages so feature correctness can be isolated from filtering artifacts:

1. Generate and regression-test feature buffers without changing final color.
2. Add an on-demand spatial denoiser using HDR beauty plus normal/albedo/depth guidance.
3. Add temporal reprojection and history rejection.
4. Add a real-time spatial-temporal denoiser if temporal feature quality is adequate.
5. Evaluate ML ray reconstruction only after the vendor-neutral signal contract is stable.

A GPU A-trous or SVGF-style filter is a pragmatic first implementation. It is not machine learned, but it validates feature semantics, provides a portable real-time baseline, and makes vendor SDK quality measurable rather than anecdotal.

An offline ML denoiser such as Intel Open Image Denoise can be useful for on-demand still renders using beauty, albedo, and normal guidance. It is not necessarily appropriate for low-latency frame presentation.

Caustics require focused validation. Sparse high-energy caustic structure can be classified as noise and smeared or suppressed. Preserve raw output and measure caustic peak and total energy against high-sample references. Consider a separate caustic feature, mask, or conservative denoiser blend if focused detail is consistently lost.

## Reconstruction Technology Options

### Unity STP

Unity 6 URP and HDRP include Spatial-Temporal Post-Processing (STP), a cross-platform spatial-temporal upscaler and anti-aliasing solution. It is the most attractive first built-in temporal reconstruction option when vendor neutrality matters.

STP is not a path-tracing denoiser. It still requires a sufficiently stable input and correct depth, motion, jitter, and history behavior. A prototype must verify whether the public URP/HDRP integration can consume this renderer's custom ray-traced resources without relying on raster pipeline depth and motion that describe different visible geometry.

### DLSS Super Resolution

Unity 6.3 HDRP `17.3` documents native DLSS 4.5 Super Resolution support on Windows x86-64 with DirectX 11, DirectX 12, or Vulkan. It is not supported by that HDRP integration on Metal, Linux, or other platforms.

This official path is straightforward for normal HDRP camera rendering, but the custom path tracer must still participate in HDRP's expected camera color, depth, motion, exposure, jitter, and history-reset contract. Remaining on Built-in would require a custom native Streamline/NGX integration instead.

DLSS Super Resolution should not be treated as a replacement for a path-tracing denoiser.

### DLSS Ray Reconstruction

DLSS Ray Reconstruction is the NVIDIA technology most directly related to ML denoising of ray-traced signals. It is a larger integration than Super Resolution and can require specialized signal decomposition and SDK inputs. Unity 6.3's documented HDRP DLSS Super Resolution support does not mean this custom path tracer automatically gains Ray Reconstruction.

Treat Ray Reconstruction as a later Windows/NVIDIA backend after diffuse/specular/feature semantics and temporal motion are stable.

### AMD FSR

Unity 6 HDRP documents integrated FSR 2.2.1 temporal upscaling. FSR 2 is useful both as a deployment option and as a public reference for the common depth/motion/jitter/reactive-mask contract.

FSR 4 is a machine-learned reconstruction option with narrower API and hardware requirements. Do not design the renderer around FSR 4 specifically. Build the vendor-neutral reconstruction inputs first and treat FSR 4 as a later platform backend if the target hardware and current Unity/AMD integration support it.

FSR temporal upscaling is not, by itself, a general path-tracing denoiser.

### MetalFX

MetalFX is a natural Apple-platform temporal upscaling candidate, but Unity 6.3 does not provide a documented Built-in/HDRP integration equivalent to its documented DLSS support. A custom integration would likely use a native macOS/iOS plugin that:

- Receives Unity texture handles.
- Creates and manages a MetalFX scaler.
- Encodes work at the correct point in a Metal command buffer.
- Receives color, depth, motion, exposure, jitter, and reset state.
- Returns or writes a native-resolution texture Unity can tone-map or present.
- Correctly synchronizes resource ownership and execution with Unity.

Unity-to-Metal command-buffer synchronization is the highest-risk part. MetalFX temporal scaling still benefits from a dedicated path-tracing denoiser before reconstruction.

### Spatial Baseline

Before temporal or native SDK integration, add a simple spatial reconstruction path such as Catmull-Rom, bicubic, CAS, or FSR 1. This proves:

- Internal versus display resolution sizing.
- Correct UV/projection behavior.
- Resource allocation and resize handling.
- Tone-map placement.
- Quality settings and fallback behavior.

It also provides an immediate cross-platform performance mode and a fallback for unsupported hardware.

## Unity 6.3 Implications

### Engine-Only Upgrade While Remaining Built-In

`Camera.OnRenderImage()` remains supported in Unity 6.3's Built-in Render Pipeline, so the current dispatch and blit architecture can continue after an engine-only upgrade.

An engine-only upgrade does not expose the custom ray-traced texture automatically to STP, HDRP FSR 2, or HDRP DLSS. All common renderer prerequisites in this document still apply. MetalFX and vendor SDK integrations remain custom work.

Unity 6.3 is nevertheless a better-supported base for current graphics APIs, dynamic resolution, native plugins, shader tooling, and a later SRP migration.

### URP Migration

URP offers STP, motion-vector infrastructure, per-camera history APIs, Render Graph, dynamic/fixed render scaling, and defined custom render injection points. It is likely the best balance when the goals are cross-platform temporal upscaling and preservation of the custom compute path tracer.

`OnRenderImage()` is not supported in URP or HDRP. Migration requires replacing `RayTracingCameraRenderer.OnRenderImage()` with a `ScriptableRenderPass`, likely integrated with Render Graph.

URP's raster depth and motion buffers cannot be assumed to match the custom ray tracer. The project would need to supply or adapt ray-traced depth and motion to the reconstruction stage. Verify this with a focused STP prototype before committing to a full migration.

### HDRP Migration

HDRP provides the most direct official route to FSR 2 and Windows DLSS Super Resolution, along with STP and mature dynamic-resolution infrastructure. It also adds substantial pipeline complexity that this educational/custom renderer otherwise does not require.

HDRP is appropriate if official Windows DLSS/FSR deployment is a primary goal. It is not automatically the best choice for a small compute path tracer, Apple-focused development, or custom ML denoising. The path tracer still needs to populate HDRP-compatible color, depth, motion, jitter, exposure, and reset resources.

### Upgrade Risks

The Unity `2022.3.72f1` to Unity `6.3 LTS` upgrade should be isolated from the reconstruction feature work and validated first.

Relevant risks include:

- Unity 6 changed Metal layouts for CPU-visible shader buffers containing `half`, `min16float`, or `real`. The current documented scene buffers predominantly use full `float` and `int`, but generated Metal code and C#/HLSL strides must still be checked.
- Shader compiler changes can alter performance, compile time, floating-point behavior, and deterministic image signatures.
- Existing packages and platform service integrations may need compatible updates.
- An SRP migration removes the current `OnRenderImage()` integration and is substantially more disruptive than the engine upgrade itself.

Use the existing CPU/GPU/image regression suite, shader precompiler, odd-resolution coverage, and benchmark scenes to validate the upgrade before changing renderer architecture. Capture reviewed baseline changes rather than combining engine migration, denoising, and upscaling in one change set.

## Recommended Staged Plan

### Stage 0: Upgrade Validation

- Upgrade a branch to Unity 6.3 while retaining Built-in.
- Update only packages required for compatibility.
- Run EditMode CPU/GPU and image regression tests.
- Precompile all relevant compute variants on Metal and intended Windows APIs.
- Benchmark representative scenes and review image-signature drift.

### Stage 1: Reconstruction-Neutral Outputs

- Separate internal render size from display size.
- Split linear HDR beauty from final tone mapping.
- Add normal, albedo, depth, identity, and validity buffers.
- Add debug modes and deterministic regression probes for each feature.
- Preserve a no-reconstruction mode matching existing output.

### Stage 2: Portable Spatial Path

- Add an edge-aware spatial denoiser.
- Add Catmull-Rom/bicubic, CAS, or FSR 1 spatial upscaling.
- Measure fixed-sample quality, frame cost, edge preservation, and caustic energy.

This stage is the best near-term payoff. A `50-67%` internal-resolution path trace plus a spatial denoiser and good upscale can materially reduce ray work while establishing most later resource plumbing.

### Stage 3: Temporal Infrastructure

- Add frame-level projection jitter and previous camera matrices.
- Add static-camera motion vectors first.
- Add stable identities and dynamic sphere/mesh motion.
- Add water and transparency/reactive policies.
- Implement history reprojection, rejection, clamping, and reset diagnostics.

### Stage 4: Built-In Temporal Prototype

- Prototype a portable temporal denoiser/upscaler against the custom buffers, or prototype URP plus STP in an isolated branch.
- Compare both paths before committing to an SRP migration.
- Keep progressive still accumulation separate from real-time temporal history.

### Stage 5: Platform Backends

- Prefer MetalFX first if Apple deployment is primary and native plugin work is acceptable.
- Prefer HDRP DLSS/FSR if Windows vendor reconstruction is a primary product requirement.
- Evaluate DLSS Ray Reconstruction only after signal decomposition and motion are mature.
- Add one backend at a time behind a vendor-neutral interface and retain spatial fallback behavior.

A conceptual backend boundary is:

```csharp
interface IImageReconstructor
{
    bool IsSupported { get; }
    void ResetHistory();
    void Execute(ReconstructionInputs inputs, RenderTexture output);
}
```

Do not introduce this abstraction until a second implementation or concrete platform need exists. The important architectural requirement is that renderer outputs are vendor-neutral, not that speculative interfaces are added early.

## Testing And Completion Criteria

Feature and reconstruction testing should cover:

- Diffuse, metal, glass, nested media, textures, water, emission, and caustic receivers.
- Static camera, camera translation/rotation, autofocus changes, and camera cuts.
- Dynamic spheres and transformed meshes.
- Animated water and procedural visibility changes.
- Finite/non-NaN feature and reconstructed output.
- Odd internal and display resolutions.
- Resize and quality-mode transitions.
- History reset on camera, scene, material, texture, and quality changes.
- Unsupported-hardware fallback.
- Unchanged raw beauty and no unnecessary resources while reconstruction is disabled.

Measure rather than relying only on screenshots:

- GPU execution cost and memory use.
- Edge preservation and material-edge bleeding.
- Temporal stability, shimmer, trails, and disocclusion recovery.
- Residual variance at fixed sample counts.
- Caustic peak and total energy against high-sample raw references.
- Quality at fixed internal scales such as `67%`, `58%`, and `50%`.

Completion means the renderer has stable and inspectable feature buffers, low-sample scenes improve without unacceptable edge bleeding or temporal artifacts, caustics retain recognizable structure and approximately stable energy, unsupported systems fall back cleanly, and the disabled path preserves existing renderer behavior.

## Rough Scope

These estimates are planning ranges for one engineer already familiar with this renderer. Native SDK and difficult temporal artifacts can move them substantially.

- Internal resolution, untone-mapped HDR separation, and initial feature buffers: `2-4 weeks`.
- Spatial GPU denoiser and diagnostics: `2-4 weeks`.
- Temporal reprojection and basic dynamic-object motion: `3-6 weeks`.
- MetalFX native plugin after the common foundation: `3-6 weeks`.
- Custom DLSS or FSR backend: approximately `4-8 weeks` per backend.
- Ray Reconstruction-quality signal decomposition and tuning: potentially another `1-3 months`.

The largest uncertainties are native Unity graphics synchronization, feature semantics through glass/water/specular paths, and preserving sparse caustic energy.

## Decision Guide

- If the goal is the quickest portable performance gain, remain Built-in initially and implement low-resolution HDR rendering, feature buffers, spatial denoising, and spatial upscaling.
- If the goal is cross-platform temporal upscaling with Unity-managed infrastructure, prototype URP plus STP.
- If the goal is official Windows DLSS/FSR integration, evaluate HDRP after the custom signal buffers exist.
- If Apple platforms are primary, keep the renderer vendor-neutral and evaluate a MetalFX native plugin after denoising and temporal inputs are proven.
- If converged still images matter more than interactivity, prioritize progressive accumulation plus an on-demand feature-guided ML denoiser rather than temporal upscaling.

The renderer-side foundation is shared across all choices and should be implemented before committing the project architecture to a vendor technology.
