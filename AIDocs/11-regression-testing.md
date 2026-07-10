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

Do not loosen tolerances simply to make a changed render pass. CPU math uses tight tolerances; GPU probes allow slightly wider tolerances for backend floating-point differences.

## Next Coverage

- Brute-force versus per-mesh/top-level/shadow BVH equivalence over deterministic randomized scenes.
- BVH maximum-depth enforcement against the fixed traversal stack.
- Deterministic low-resolution image baselines for reflected geometry, sphere and mesh glass, transparent shadows, water, textures, and mesh lights.
- Odd-resolution GPU dispatch smoke tests after `CSMain` adds an output-bounds guard.
