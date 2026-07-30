# SVGF Implementation Plan

This document is the implementation tracker for the renderer's GPU-native A-trous/SVGF denoising path. Update milestone status, decisions, validation, and relevant document references as work lands. Read `13-denoising-and-upscaling.md` first for the broader reconstruction architecture and platform options.

## Goal

Produce a portable real-time denoising path that keeps ray-traced data on the GPU:

```text
Path-traced linear HDR beauty and features
    -> temporal reprojection and validation
    -> bounded temporal accumulation and variance estimation
    -> edge-aware A-trous spatial filtering
    -> exposure and ACES tone mapping
    -> presentation
```

This is distinct from progressive still-image accumulation and from external denoisers such as Intel Open Image Denoise. Progressive accumulation may remain available as a separate still-render mode.

## Current Status

- Milestones 1 and 2 are complete.
- Milestones 3-12 remain unimplemented.
- The existing tone-mapped `Result` texture remains the fallback presentation path. When spatial denoising is enabled, HDR beauty is filtered first and a denoiser presentation pass writes the tone-mapped result.
- Do not conflate this work with a Unity version or render-pipeline migration.

## Milestone 1: Reconstruction-Neutral Outputs

**Status:** Complete

**Purpose:** Establish denoiser inputs without changing the normal presentation path.

**Implemented:**

- Persistent full-resolution GPU textures for linear HDR beauty, normal, albedo, hit distance, identity, and validity.
- `Beauty` stores the path-traced HDR radiance after per-frame sampling and optional progressive accumulation, but before exposure and ACES tone mapping.
- Feature buffers use an unjittered pinhole center ray for a stable primary visibility decision.
- Sky pixels have zero depth, identity, and validity.
- `RawBeauty`, `FeatureNormal`, `FeatureAlbedo`, `FeatureDepth`, `FeatureIdentity`, and `FeatureValidity` debug modes inspect the outputs.
- Existing final-color behavior and image-regression baselines are preserved.

**Known policy:** Primary-hit normal/albedo are appropriate for directly visible opaque surfaces. They do not fully describe reflected or refracted radiance behind mirrors, glass, or water; later milestones must treat those paths conservatively.

## Milestone 2: Spatial A-Trous Denoiser

**Status:** Complete

**Purpose:** Validate feature semantics and obtain a usable static-scene denoising baseline before temporal history is introduced.

**Work:**

- Add a dedicated denoiser compute shader and a denoiser enable toggle.
- Allocate HDR ping-pong render textures.
- Implement three initial edge-aware A-trous iterations with step widths `1`, `2`, and `4`; make additional iterations configurable for evaluation.
- Use kernel, depth, normal, albedo, identity/validity, and luminance edge-stopping weights.
- Present the filtered HDR result only when denoising is enabled; retain raw beauty presentation as the fallback.

**Implemented:**

- `Assets/Resources/RayTracingSpatialDenoiser.compute` contains an edge-aware 5x5 A-trous kernel and a final HDR presentation kernel.
- `GameManager.enableSpatialDenoising` runs between one and five iterations, defaulting to steps `1`, `2`, and `4`.
- The filter uses primary-hit validity, identity, relative depth, normal alignment, albedo difference, and normalized HDR luminance difference to stop filtering across unrelated surfaces and sharp lighting changes.
- `SpatialDenoised`, `AtrousIteration1`, `AtrousIteration2`, and `AtrousIteration3` debug modes run the spatial path and present the selected HDR iteration through the same exposure/ACES pass.
- The disabled path allocates no A-trous ping-pong resources and keeps the prior presentation behavior.

**Validation:**

- Test low-light diffuse scenes, silhouettes, touching materials, thin geometry, textured meshes, emissive surfaces, glass, water, and caustic receivers.
- Add debug views for spatial-only output and individual iteration results.
- Measure edge bleeding, highlight preservation, caustic peak/energy, GPU time, and memory use.

## Milestone 3: Previous-Frame Camera State

**Status:** Not started

**Purpose:** Establish deterministic camera information required for temporal reprojection.

**Work:**

- Store current and previous camera-to-world and view-projection matrices.
- Introduce known frame-level projection jitter, retaining separate random samples for path tracing.
- Store current/previous jitter, render dimensions, frame index, and a camera-cut/history-reset flag.
- Initially constrain temporal validation to a pinhole camera path or define a conservative depth-of-field policy.

**Validation:**

- Debug current/previous matrices and jitter values.
- Verify every camera, resize, quality, and lens transition resets the temporal state deterministically.

## Milestone 4: Camera-Only Motion Vectors

**Status:** Not started

**Purpose:** Reproject static primary surfaces across camera movement.

**Work:**

- Preserve or reconstruct current primary-hit world position.
- Project it with current and previous view-projection matrices to create a current-pixel-to-previous-pixel motion vector.
- Add a motion-vector texture and debug visualization.
- Reject history for dynamic geometry until object motion is implemented.

**Validation:**

- Test camera pan, translation, rotation, and small/large movements against static spheres and meshes.
- Document the vector sign convention and verify screen-edge behavior.

## Milestone 5: Temporal History Allocation And Lifecycle

**Status:** Not started

**Purpose:** Safely retain prior-frame data without using the unbounded progressive accumulation model.

**Work:**

- Add double-buffered histories for radiance, depth, normal, identity, history length, and later luminance moments.
- Swap read/write histories after each frame.
- Reset all related histories together for camera cuts, size changes, incompatible settings changes, scene changes without valid motion, denoiser toggles, or changed feature semantics.
- Keep progressive still accumulation independent from temporal denoiser history.

**Validation:**

- Add reset diagnostics and test resize, toggle, scene, material, camera, and quality transitions.
- Confirm disabled denoising avoids history allocation and dispatch cost where practical.

## Milestone 6: Reprojection And History Validation

**Status:** Not started

**Purpose:** Accept prior data only when it represents the current visible surface.

**Work:**

- Reproject current pixels through camera-only motion vectors.
- Validate prior history by bounds, validity, identity, relative depth, normal agreement, and material/reactive classification.
- Start with nearest-neighbor history sampling; evaluate compatible bilinear selection after correctness is proven.
- Record a per-pixel rejection reason for diagnostics.

**Validation:**

- Test disocclusions, silhouettes, foreground/background crossings, camera cuts, and sky transitions.
- Debug accepted/rejected history and each rejection reason.
- Prioritize avoiding ghosting over maximizing reuse.

## Milestone 7: Bounded Temporal Accumulation

**Status:** Not started

**Purpose:** Reduce per-frame Monte Carlo variance while retaining responsiveness to change.

**Work:**

- Blend valid reprojected radiance with current noisy HDR radiance.
- Track and cap history length rather than allowing unlimited accumulation.
- Choose an initial maximum effective history range, then tune it using measured stability and responsiveness.
- Use no history on invalid/disoccluded pixels.

**Validation:**

- Compare raw, spatial-only, and temporal-only output during camera motion.
- Verify low-sample noise reduction without long-lived trails after lighting or visibility changes.

## Milestone 8: History Clamping

**Status:** Not started

**Purpose:** Prevent stale bright or dark history from contaminating current pixels.

**Work:**

- Compute current-frame neighborhood bounds or moments.
- Clamp reprojected history to a plausible luminance-aware range before blending.
- Tune conservatively for sparse bright paths; avoid independent RGB clamping that causes hue shifts.

**Validation:**

- Test moving emissive objects, reflections, lighting changes, fireflies, and caustic receivers.
- Compare trail suppression against caustic peak and total-energy loss.

## Milestone 9: Luminance Moments And Variance

**Status:** Not started

**Purpose:** Identify where residual uncertainty requires stronger spatial filtering.

**Work:**

- Accumulate first and second luminance moments with temporal history.
- Derive non-negative variance from those moments.
- Estimate variance spatially for newly disoccluded pixels with insufficient history.
- Store variance in a bandwidth-conscious scalar format after validating range requirements.

**Validation:**

- Add moment and variance debug views.
- Verify variance falls as stable history grows and rises correctly after resets/disocclusions.

## Milestone 10: Variance-Guided A-Trous Filtering

**Status:** Not started

**Purpose:** Complete the initial SVGF-style pipeline by adapting spatial filtering to residual uncertainty.

**Work:**

- Feed temporally accumulated HDR radiance and variance into A-trous filtering.
- Scale luminance edge thresholds using center/neighborhood variance.
- Filter or propagate variance through the A-trous iterations as required by the selected formulation.
- Evaluate whether history stores temporal radiance, filtered radiance, or a lightly filtered intermediate; choose based on measured blur/stability.

**Validation:**

- Compare spatial-only and temporal-plus-variance-guided paths at fixed samples per pixel.
- Measure residual variance, detail preservation, temporal shimmer, GPU cost, and memory use.

## Milestone 11: Dynamic Geometry And Difficult Materials

**Status:** Not started

**Purpose:** Extend temporal reuse safely beyond static opaque primary surfaces.

**Work:**

- Add stable object IDs and previous transforms for dynamic spheres and meshes.
- Derive motion from object-local hit information and previous transforms.
- Define conservative reactive/history policies for glass, water, mirrors, glossy reflections, emissive surfaces, fog, and depth of field.
- Start by reducing or rejecting temporal history for paths whose primary motion does not represent visible radiance motion.

**Validation:**

- Test dynamic spheres, transformed meshes, animated water, transparent boundaries, reflected objects, refracted objects, fog, autofocus, and lens changes.
- Favor short-lived noise over persistent ghosting or incorrect temporal detail.

## Milestone 12: Caustic Preservation, Performance, And Completion

**Status:** Not started

**Purpose:** Make the denoiser viable for the target scenes without erasing rare high-energy transport.

**Work:**

- Measure denoising against high-sample caustic references using peak luminance, total receiver energy, shape, sharpness, and temporal stability.
- Add a separate caustic signal, preservation/reactive mask, or independently tuned filtering path only if measurements show the beauty denoiser consistently destroys valid caustics.
- Tune render formats, iteration counts, resource lifetimes, and internal resolution only after correctness is established.
- Preserve a no-denoise fallback and confirm disabled-path resource isolation.

**Completion Criteria:**

- Low-sample opaque scenes improve visibly without unacceptable edge bleeding.
- Camera motion remains stable without obvious trails or disocclusion ghosts.
- Dynamic and difficult material policies fail conservatively.
- Caustics remain recognizable with approximately stable energy relative to a reviewed reference.
- Resize, settings, scene, and camera transitions reset history correctly.
- GPU execution cost, memory use, and unsupported-system fallback are documented.
- Existing raw beauty/no-denoise behavior remains available and regression-tested.

## Required Diagnostics

Maintain or add debug outputs as relevant milestones land:

- Raw HDR beauty.
- Primary normal, albedo, depth, identity, and validity.
- Motion vectors.
- Temporal-only radiance.
- Final denoised radiance.
- History length.
- Accepted/rejected history and rejection reason.
- Luminance moments and variance.
- Reactive/preservation mask.
- Individual A-trous iterations.

## Implementation Rules

- Keep denoiser inputs and outputs linear HDR until the final presentation pass.
- Do not replace the progressive still-render accumulation policy with temporal history.
- Add one stage at a time and retain a raw/no-denoise comparison path.
- Treat glass, water, reflection, refraction, fog, depth of field, and caustics as explicit feature-semantics decisions, not ordinary diffuse surfaces.
- Measure caustic preservation rather than judging only from screenshots.
- Avoid speculative vendor abstractions until a second backend or concrete platform requirement exists.
