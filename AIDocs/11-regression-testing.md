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
- A GPU `CSRegressionProbe` kernel in the production compute shader that calls the same reflection, `RefractSnell()`, Fresnel formula, and `GetAbsorptionTransmittance()` behavior used by rendering. This catches divergence between CPU expectations and shader execution.
- Deterministic `32x32` final-color image signatures for a reflective metal sphere, a refractive glass sphere with geometry behind it, and calm finite water with submerged geometry. Each baseline stores the image average and eight fixed pixel probes after tone mapping.
- Initial medium-identity and transition probes for air -> water, water -> sphere glass, and sphere glass -> water. These establish object identity and source/target IOR semantics before medium state is carried through full paths.

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

The current fixtures cover spheres and procedural water. Mesh glass is still listed below because a meaningful mesh regression requires deterministic triangle, mesh-info, and BVH fixture data rather than bypassing production traversal.

## Medium Transition Foundation

`MediumIdentity` in `RayTracingCompute.compute` currently records medium type, object identity, IOR, opacity, and absorption color. `CreateHitMedium()`, `IsSameMedium()`, and `GetMediumTransitionIndices()` define the initial boundary semantics. They are exercised only by regression probes at this stage; `TracePath()` still uses the existing sphere/mesh/water-specific behavior, so image baselines remain unchanged.

The next implementation step is a fixed-capacity path medium stack with air as its implicit base. Entering a boundary will push its identity; matching exits will pop it and reveal the parent medium. Stack overflow and unmatched exits must be detectable rather than silently ignored.

## Next Coverage

- Brute-force versus per-mesh/top-level/shadow BVH equivalence over deterministic randomized scenes.
- BVH maximum-depth enforcement against the fixed traversal stack.
- Extend deterministic image baselines to mesh glass, transparent shadows, textures, and mesh lights.
- Carry medium identity through `TracePath()` and add nested air -> water -> glass -> water -> air image/probe coverage.
- Odd-resolution GPU dispatch smoke tests after `CSMain` adds an output-bounds guard.
