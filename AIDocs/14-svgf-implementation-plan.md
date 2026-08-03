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
- Milestones 3-11 are complete. Milestone 12 remains unimplemented.
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

**Status:** Complete

**Purpose:** Establish deterministic camera information required for temporal reprojection.

**Work:**

- Store current and previous camera-to-world and view-projection matrices.
- Introduce known frame-level projection jitter, retaining separate random samples for path tracing.
- Store current/previous jitter, render dimensions, frame index, and a camera-cut/history-reset flag.
- Initially constrain temporal validation to a pinhole camera path or define a conservative depth-of-field policy.

**Implemented:**

- `GameManager` retains current/previous unjittered view-projection matrices, current/previous frame jitter, a temporal frame index, camera-cut thresholds, and a separate temporal reset state.
- A deterministic Halton(2,3) frame jitter is applied to beauty sampling only while temporal diagnostics/history are active; pinhole feature visibility remains unjittered.
- Resize, relevant rendering/scene changes, large camera discontinuities, and temporal disable/re-enable reset temporal state without reusing progressive accumulation state.
- Initial temporal validation uses stable pinhole primary features as conservative motion guidance under depth of field. Fog and water remain globally rejected because their radiance motion is not represented by those features.

**Validation:**

- Debug current/previous matrices and jitter values.
- Verify every camera, resize, quality, and lens transition resets the temporal state deterministically.

## Milestone 4: Camera-Only Motion Vectors

**Status:** Complete

**Purpose:** Reproject static primary surfaces across camera movement.

**Work:**

- Preserve or reconstruct current primary-hit world position.
- Project it with current and previous view-projection matrices to create a current-pixel-to-previous-pixel motion vector.
- Add a motion-vector texture and debug visualization.
- Reject history for dynamic geometry until object motion is implemented.

**Implemented:**

- Stable unjittered primary-hit depth is reconstructed through the current pinhole camera to obtain world position.
- `CSGenerateCameraMotion` projects that position with current and previous unjittered view-projection matrices and writes `current UV - previous UV` motion vectors.
- `MotionVectors` provides a signed-vector debug visualization.
- Dynamic geometry is not supported for temporal reuse yet; changed scene state resets history.

**Validation:**

- Test camera pan, translation, rotation, and small/large movements against static spheres and meshes.
- Document the vector sign convention and verify screen-edge behavior.

## Milestone 5: Temporal History Allocation And Lifecycle

**Status:** Complete

**Purpose:** Safely retain prior-frame data without using the unbounded progressive accumulation model.

**Work:**

- Add double-buffered histories for radiance, depth, normal, identity, history length, and later luminance moments.
- Swap read/write histories after each frame.
- Reset all related histories together for camera cuts, size changes, incompatible settings changes, scene changes without valid motion, denoiser toggles, or changed feature semantics.
- Keep progressive still accumulation independent from temporal denoiser history.

**Implemented:**

- Temporal radiance, normal, depth, identity, and validity each have isolated double-buffered GPU histories.
- Temporal resources are allocated only while temporal denoising or a temporal debug mode is active, then released when disabled.
- Read/write histories swap only after validation dispatch; temporal state is separate from progressive accumulation.

**Validation:**

- Add reset diagnostics and test resize, toggle, scene, material, camera, and quality transitions.
- Confirm disabled denoising avoids history allocation and dispatch cost where practical.

## Milestone 6: Reprojection And History Validation

**Status:** Complete

**Purpose:** Accept prior data only when it represents the current visible surface.

**Work:**

- Reproject current pixels through camera-only motion vectors.
- Validate prior history by bounds, validity, identity, relative depth, normal agreement, and material/reactive classification.
- Start with nearest-neighbor history sampling; evaluate compatible bilinear selection after correctness is proven.
- Record a per-pixel rejection reason for diagnostics.

**Implemented:**

- `CSTemporalReprojectValidate` uses nearest-neighbor reprojected history and validates global reset state, current/prior validity, bounds, identity, relative depth, normal agreement, and reactive/unsupported paths.
- Rejection diagnostics encode reset, invalid current feature, out-of-bounds motion, invalid prior feature, identity, depth, normal, and reactive-policy failures.
- `TemporalReprojectedRadiance`, `TemporalHistoryAcceptance`, and `TemporalRejectionReason` provide diagnostics. This milestone records a prior-radiance candidate but does not blend it; bounded temporal accumulation remains milestone 7.
- Diagnostic convention: reprojected radiance is black when no prior sample was accepted; acceptance is green or red; rejection reasons use distinct colors rather than ambiguous beauty-like output.

**Validation:**

- Test disocclusions, silhouettes, foreground/background crossings, camera cuts, and sky transitions.
- Debug accepted/rejected history and each rejection reason.
- Prioritize avoiding ghosting over maximizing reuse.

## Milestone 7: Bounded Temporal Accumulation

**Status:** Complete

**Purpose:** Reduce per-frame Monte Carlo variance while retaining responsiveness to change.

**Work:**

- Blend valid reprojected radiance with current noisy HDR radiance.
- Track and cap history length rather than allowing unlimited accumulation.
- Choose an initial maximum effective history range, then tune it using measured stability and responsiveness.
- Use no history on invalid/disoccluded pixels.

**Implemented:**

- Valid nearest-neighbor reprojected HDR history blends with current beauty using `1 / historyLength` current-frame weight.
- Per-pixel history length starts at one after rejection, increments only on accepted history, and is capped by `GameManager.temporalMaxHistoryLength` (default `16`).
- Temporal history stores the blended HDR radiance, while progressive still accumulation remains independent.
- `TemporalDenoised` and `TemporalHistoryLength` debug modes expose the temporal-only result and effective history range.
- `TemporalDenoisedTint` presents temporal output with a magenta overlay wherever validated history actually contributed; untinted pixels used current beauty only.
- With temporal denoising enabled, the bounded temporal HDR result is tone mapped and presented. Unsupported or rejected paths present current beauty only.
- When `enableFrameAccumulation` is also enabled, the renderer automatically selects bounded temporal accumulation while the camera is moving and switches to independent progressive still accumulation once camera motion stops. Temporal history continues updating while still, so it is immediately available when movement resumes. The camera-motion thresholds are configurable through `temporalMotionDistance` and `temporalMotionAngle`.

**Validation:**

- Compare raw, spatial-only, and temporal-only output during camera motion.
- Verify low-sample noise reduction without long-lived trails after lighting or visibility changes.

## Milestone 8: History Clamping

**Status:** Complete

**Purpose:** Prevent stale bright or dark history from contaminating current pixels.

**Work:**

- Compute current-frame neighborhood bounds or moments.
- Clamp reprojected history to a plausible luminance-aware range before blending.
- Tune conservatively for sparse bright paths; avoid independent RGB clamping that causes hue shifts.

**Implemented:**

- Accepted history luminance is constrained to the current pixel's 3x3 neighborhood luminance range before blending.
- Clamping rescales the complete HDR color by luminance rather than clipping RGB channels independently, preserving hue.
- Current-frame weight increases with reprojected motion in pixels, preventing a capped `15/16` history contribution from visibly dragging across the image during faster camera movement.
- Camera motion and rotation shorten effective history to a conservative two-frame average rather than eliminating temporal contribution entirely. Validation failures and camera cuts still reject history completely.
- Motion vectors use Unity's render-texture GPU projection convention for both current and previous endpoints, avoiding the Metal clip-space orientation mismatch that otherwise rejects or misaligns history during motion.
- Feature generation runs in a separate `CSFeatures` dispatch so `CSMain` stays within Metal's eight-UAV limit; temporal resolve packs acceptance/rejection into one diagnostics target and reuses the next-radiance history as output to remain within the same limit.

**Validation:**

- Test moving emissive objects, reflections, lighting changes, fireflies, and caustic receivers.
- Compare trail suppression against caustic peak and total-energy loss.

## Milestone 9: Luminance Moments And Variance

**Status:** Complete

**Purpose:** Identify where residual uncertainty requires stronger spatial filtering.

**Work:**

- Temporal reprojection now accumulates first and second luminance moments in double-buffered
  half-precision `RG` histories and writes a non-negative scalar variance texture.
- Moment/variance generation runs as a separate two-UAV dispatch, preserving the temporal
  reprojection kernel's Metal-compatible eight-UAV limit.
- Rejected/disoccluded pixels initialize their moments from current beauty and use a local 3x3
  current-frame luminance estimate for variance, avoiding invalid history reuse or treating
  newly visible noisy pixels as noiseless.
- `TemporalVariance` provides a log-scaled diagnostic view.

**Validation:**

- Add moment and variance debug views.
- Verify variance falls as stable history grows and rises correctly after resets/disocclusions.

## Milestone 10: Variance-Guided A-Trous Filtering

**Status:** Complete

**Purpose:** Complete the initial SVGF-style pipeline by adapting spatial filtering to residual uncertainty.

**Work:**

- When temporal presentation is active, the existing A-Trous passes consume bounded temporal HDR
  radiance and scalar variance. Variance relaxes only the luminance edge threshold for noisy
  pixels; geometry, normal, albedo, identity, and validity stops remain unchanged.
- Temporal history continues to store unfiltered temporal radiance, preventing spatial blur from
  feeding back into future reprojection. `temporalVarianceGuidedFiltering` allows direct
  temporal-only comparison.

**Validation:**

- Compare spatial-only and temporal-plus-variance-guided paths at fixed samples per pixel.
- Measure residual variance, detail preservation, temporal shimmer, GPU cost, and memory use.

## Milestone 11: Dynamic Geometry And Difficult Materials

**Status:** Complete (conservative primary-surface policy)

**Purpose:** Extend temporal reuse safely beyond static opaque primary surfaces.

**Work:**

- Stable primary identities remain part of every validation. Camera-only motion remains the
  supported dynamic-geometry policy: scene/material changes reset temporal state rather than
  reusing invalid history. Object-local previous-transform motion is deferred until the renderer
  preserves local hit coordinates in a feature buffer.
- Glass and static water boundaries are classified as transmission and capped at four effective
  history samples. Metals, highly glossy surfaces, emissive hits, and fog remain fully reactive;
  animated water remains a global reject because wave motion is not yet represented.
- This milestone intentionally favors short-lived noise over ghosting. Full dynamic-object and
  specular-path motion remains a future quality extension, not a prerequisite for safe reuse.

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
