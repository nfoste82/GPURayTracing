# Regression Testing

The project uses EditMode tests under `Assets/Tests/EditMode/` to make rendering behavior changes explicit. These are regression tests, not only physical-correctness tests: where the renderer intentionally uses an approximation, the expected value records the current behavior. If a later change makes the renderer more physically accurate, the old test should fail first; review the image/math change, then deliberately update the baseline.

## Current Coverage

- CPU sphere intersections, including rays starting inside a sphere and rays pointing away.
- CPU triangle hit distance and barycentric coordinates.
- CPU axis-aligned bounding-box hits, misses, and parallel ray components.
- Reflection direction at 45 degrees.
- Snell air-to-glass refraction at 45 degrees.
- Glass-to-air total internal reflection above the critical angle.
- Current Schlick Fresnel values at normal, 45-degree, and grazing incidence.
- Current distance/color/opacity glass absorption approximation.
- A GPU `CSRegressionProbe` kernel in the production compute shader that calls the same reflection, `RefractSnell()`, Fresnel formula, rough-glass boundary sampling, interpolated mesh optical-normal path, and `GetAbsorptionTransmittance()` behavior used by rendering. This catches divergence between CPU expectations and shader execution.
- Deterministic `32x32` final-color image signatures for a reflective metal sphere, a refractive glass sphere with geometry behind it, a camera starting inside a translucent glass sphere, closed mesh glass through production triangle/mesh/BVH buffers, calm finite water with submerged geometry, nested water with sphere and closed-mesh glass, a camera starting underwater, textured geometry, triangle mesh lights, and transparent sphere/closed-mesh/stacked shadows. The nested closed-mesh fixture targets submerged production mesh/BVH refraction pixels, while the shadow fixtures exercise ordered boundary traversal and actual closed-mesh segment absorption. Each baseline stores the image average and eight fixed pixel probes after tone mapping.
- Medium-identity and stack probes for air -> water -> sphere glass -> water -> air, parent lookup, matching exits, overflow, unmatched exits, underwater initialization, and flat water-volume side/bottom intersections.
- Deterministic randomized CPU reference comparisons for per-mesh, top-level, and shadow BVH traversal against brute force, with maximum build depth checked against the fixed stack capacity of `64`.
- GPU dispatch smoke coverage at `1x1`, `3x5`, and `13x7`; `CSMain` now returns before accessing output textures for ceiling-dispatch threads outside their dimensions.
- Production GPU probes cover shared Lambert/GGX BRDF values, PDFs, and finite positive sampled throughput.
- Production GPU probes cover the MIS power heuristic and triangle area-to-solid-angle PDF conversion.
- A focused high-sample image regression verifies that a reflected sphere light does not develop a dark center.
- Caustics coverage dispatches the production clear/trace kernels with a fixed seed, compares canonically sorted photon records, verifies sphere and triangle emitters produce receiver photons through sphere glass, requires useful photon yield through closed-mesh glass, checks the multi-event sphere transport bounce budget, verifies default-disabled resource isolation, locks the smooth-kernel focused sphere-caustic image signature, and checks energy stability across photon counts and gather radii.

## Running Tests

In Unity, open `Window > General > Test Runner`, select EditMode, and run all tests.

From the command line on this project's current macOS Unity version:

```sh
/Applications/Unity/Hub/Editor/2022.3.72f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -runTests -testPlatform EditMode \
  -testResults /tmp/gpuraytracing-editmode-results.xml \
  -logFile /tmp/gpuraytracing-editmode.log \
  -quit
```

The GPU probe is skipped if the active graphics device does not support compute shaders or does not compile the probe kernel. On macOS, `-nographics` imports the compute shader without an executable Metal kernel, so it runs the CPU suite and skips the GPU probe. Run through the Test Runner or omit `-nographics` to validate all tests.

## Updating A Baseline

1. Run the full suite before the renderer change and confirm it passes.
2. Make one behavioral change at a time.
3. Treat any changed expected value as a review point, even if the new result is more physically correct.
4. Confirm the new result analytically or through a focused reference scene.
5. Update the baseline and explain the intentional behavior change in the commit message.

Do not loosen tolerances simply to make a changed render pass. CPU math uses tight tolerances; GPU probes allow slightly wider tolerances for backend floating-point differences. Image signatures use a small per-channel tolerance because GPU backends may vary slightly, but they are deliberately not perceptual comparisons: a changed probe is intended to force review.

## Image Fixtures

`RayTracingImageRegressionTests` drives `CSMain` directly with in-memory structured buffers and textures. It uses a fixed seed, fixed camera, no frame accumulation, flat object loops, and no scene assets, so the result does not depend on editor scene state. The first execution of `CSMain` may take longer while Unity compiles the kernel.

The fixtures use deterministic in-memory sphere, light, triangle, mesh-info, BVH, and texture-array data. They do not depend on scene assets or editor scene state.

## Medium Transition Foundation

`MediumIdentity` in `RayTracingCompute.compute` records medium type, object identity, IOR, opacity, and absorption color. `TracePath()` carries a fixed-capacity stack with implicit air and initializes containing water and translucent spheres at the camera origin. Containing spheres are pushed from largest to smallest so the innermost sphere is active. Transmission updates the stack while reflection and TIR preserve it. Sphere/mesh helpers that internally cross both faces leave the net stack unchanged; paths that stop inside a volume retain that medium for the next production bounce.

Stack overflow and genuinely unmatched exits set explicit status bits and preserve valid existing state. A focused overlap probe verifies that exiting a non-current interpenetrating sphere removes it by identity while retaining the active sphere. Per-segment probes cover glass/water attenuation, neutral air, finite water side and surface exits, clipping at the next hit, and finite-medium sky misses. Production probes also cover water -> glass and glass -> water source/target selection, refraction direction, Fresnel, and the case where glass -> air would incorrectly produce TIR but glass -> water transmits.

## Remaining Coverage

- Add production-GPU BVH-on versus flat-loop image equivalence in addition to the deterministic CPU traversal comparisons.
- The water, nested-water/glass, and underwater-camera signatures were recaptured after tracing their drift to the intentional finite-water AABB change in `5fd1d33`, whose fixtures gained `_WaterDepth` without corresponding baseline updates.
