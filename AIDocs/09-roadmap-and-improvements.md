# Roadmap And Improvements

This document captures likely future work areas in priority order. For current implementation limits, see `05-known-limitations.md`. For performance hotspots and benchmark methodology, see `10-benchmarking-and-performance.md`.

## Recommended Order

The recent texture, mesh-light, glass, specular, imported-model, and procedural-water work makes correctness and regression coverage more valuable than adding another major rendering feature. Recommended order:

1. Fix concrete correctness and lifecycle hazards.
2. Add deterministic CPU/GPU regression coverage for intersections and acceleration structures.
3. Make medium/material behavior internally consistent.
4. Measure and remove avoidable CPU rebuild/upload work.
5. Continue lower-risk visual and workflow improvements.

## Priority 0: Correctness And Safety

- Add an output-dimension guard at the start of `CSMain`. Dispatch uses ceiling-divided `8x8` groups, so render sizes not divisible by eight currently launch threads outside `Result`/`AccumulationResult`. Verify very small and odd resolutions.
- Make ray-traced object registration and `_buffersNeedRebuilding` manager-local, or explicitly enforce and reset a supported singleton. Their current static lifetime conflicts with instance-owned buffers and is fragile with multiple managers or disabled domain reload.
- Preserve and restore the application's previous `QualitySettings.vSyncCount`, `Application.targetFrameRate`, and `Time.timeScale` when entering/leaving single-frame mode, including disable/destruction cleanup.
- Enforce the fixed BVH stack-depth invariant. Record maximum build depth and assert/fail clearly before a tree can exceed the CPU/GPU stack size of `64`; traversal currently drops overflowing children and could miss intersections.
- Detect `MeshFilter.sharedMesh` replacement and define an explicit dirty path for runtime vertex/topology/UV changes. Validate `mesh.isReadable` with an actionable error before reading imported mesh data.
- Update dynamic scene data before CPU autofocus and bring CPU sphere/water intersection behavior into parity with the shader. Autofocus currently sees previous-frame transforms and uses the average water plane rather than procedural waves.
- Correct finite-water segment accounting. Paths starting underwater currently attenuate for the full distance to the next hit even if they leave the finite X/Z water region, while segments with two above-water endpoints can miss an intervening water crossing.
- Validate required `shader`/camera wiring at startup and ensure the camera presenting `RayTracingCameraRenderer.OnRenderImage()` matches the camera whose matrices and controls `GameManager` uses.

## Priority 1: Regression Coverage

- Add an EditMode test assembly for CPU sphere/triangle/AABB intersections, including rays starting inside spheres and axis-aligned/zero-component directions.
- Compare per-mesh, top-level, and shadow BVH results against brute-force intersections over deterministic randomized scenes, and test maximum tree depth against `BvhStackSize`.
- Add registration/unregistration and lifecycle tests, including multiple managers and domain-reload-disabled-style static-state reset cases.
- Add GPU smoke tests that dispatch final and debug variants at tiny, odd, and non-multiple-of-eight resolutions.
- Add low-resolution deterministic image regressions for opaque/transparent shadows, stacked glass, sphere/mesh Snell and TIR behavior, mesh lights, textures, water absorption, and BVH-on versus flat-loop equivalence.
- Add focused validation scenes or numeric probes for nested/interpenetrating media and water entry/exit distances before changing those systems further.

## Priority 2: Material And Medium Consistency

- Introduce explicit current-medium state, eventually a small medium stack, so transitions among overlapping sphere glass, mesh glass, and water do not rely only on surface orientation and object-specific helpers.
- Replace the approximate direct specular highlight with a coherent energy-conserving material model. Use a shared perceptual roughness mapping, matching BRDF/PDF sampling, correct dielectric/metal Fresnel, and eventually multiple importance sampling between light and BSDF samples.
- Refine transparent shadow transport by pairing closed-mesh entry/exit hits for path length, tracking nested media, and early-outing when accumulated transmittance is negligible. Multiple blocker accumulation itself is already implemented.
- Harden sphere and mesh glass for repeated internal reflection, concave/non-manifold/open meshes, exhausted bounce budgets inside a medium, and analytic Snell/TIR validation. Basic Snell transmission, distance absorption, bounded interior-object tests, and mesh TIR are already implemented.
- Improve water intersection with adaptive/root-finding behavior, consistent side/bottom volume boundaries, and optional support for multiple/transformed water bodies.

## Priority 3: Lighting And Geometry Quality

- Add imported vertex normals and interpolate them barycentrically. This is now high-value because the Stanford Dragon benchmark and direct specular highlights make flat triangle normals visibly facet smooth models.
- Make mesh-light selection hierarchical: choose an emissive mesh by total area/power, then a triangle through an area/power distribution. This avoids treating every emissive triangle as a global light and removes pressure on the `MaxImportanceLights` (`128`) cap.
- Replace or redesign the global importance-light cap so every active emitter keeps nonzero selection probability. A precomputed CDF/alias table or spatial light structure should avoid the current per-hit full weight scan while preserving the shader's single `SampleSingleLight()` call-site compile constraint.
- Improve rough metal continuation sampling around the ideal reflection lobe instead of randomizing the normal with axis-aligned noise.
- Improve diffuse basis construction and add consistent material-specific BRDF/PDF handling as the material model evolves.
- Sample sphere lights by visible solid angle instead of approximate disk samples.

## Priority 4: Performance And Tooling

- Avoid rebuilding/uploading both top-level BVHs every rendered frame when object bounds are unchanged. Reuse static trees and evaluate refitting for transform-only changes before a full SAH rebuild.
- Separate mesh geometry, material, light, and texture dirtiness. A material or transform change currently rebuilds every world-space triangle, per-mesh BVH, mesh-light entry, and texture-array slice.
- Upload sphere/light data only when relevant transforms or component values change.
- Add benchmark CSV/JSON export with warmup, fixed settings, sample duration/count, median/p95, and scene/settings metadata.
- Add GPU timing when supported; Unity's CPU frame time often collapses compute work into `Rendering`, so Xcode GPU Frame Capture remains useful on macOS.
- Add focused water march/refinement and mesh-light tessellation benchmarks before optimizing those paths.
- Consider dynamic-quality presets or user-selectable priorities if users need to favor bounces/shadows over sample count or light quality.

## Priority 5: Lower-Risk Visual Improvements

- Expose depth-of-field aperture/lens radius and sample a camera-space lens disk instead of using the hard-coded `0.005` three-axis origin jitter.
- Improve mesh textures beyond fixed `128x128` point-filtered, mipless albedo slices: configurable/source resolution, filtering/mips and LOD, color-space validation, then normal and roughness maps.
- Add an optional firefly/outlier clamp for rare bright speckles in single-frame renders.
- Consider a lightweight denoising or temporal-stability pass, especially for animated water where normal frame accumulation is disabled.
- Tune defaults for light falloff, ground smoothness, shadow randomness, passes, shadow quality, and noise after reference-image testing.
- Add debug legends/configurable ranges and material/debug presets only when they serve a specific diagnosis workflow.
