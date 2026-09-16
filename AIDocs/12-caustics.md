# Caustics

The renderer provides optional photon-mapped caustics for focused refracted and reflected lighting through glass and water. `GameManager.enableCaustics` defaults to `false`; the disabled renderer does not allocate photon resources, dispatch caustics kernels, or gather photon radiance. Photon/grid generation, camera-side gathering, and the dedicated gather debug kernel are compiled from `Assets/Resources/RayTracingCaustics.compute`. Keeping camera gathering out of the final-color megakernels prevents its nested grid traversal and specular visibility path from inflating optimized Metal compilation, especially for water plus fog.

## Supported Transport

- Sphere, triangle, and directional light photon emission.
- Glass spheres and closed glass meshes, including reflection, transmission, absorption, and bounded multi-event transport.
- Finite procedural water, including nested water/glass paths.
- Opaque diffuse receivers visible through ordinary camera paths, specular water/glass boundaries, and smooth metal reflections (roughness at most `0.20`).
- A dedicated `Caustics` debug mode that dispatches the gather-only `CSCausticsDebug` kernel.

## Pipeline

When caustics are enabled, the renderer maintains a world-space photon map independently from camera sampling:

```text
ClearCausticPhotons
TraceCausticPhotons
ClearCausticGrid
BuildCausticGrid

CSCausticsFinalColor or CSCausticsDebug:
    Trace camera path through glass/water and smooth metal boundaries
    Gather nearby receiver-facing photons at diffuse hits
CompositeCaustics:
    Add photon radiance to final-color beauty
```

Static final-color rendering and the dedicated `Caustics` debug view use stochastic progressive photon mapping (SPPM) when frame accumulation is enabled. Each accumulated frame supplies a fresh photon batch to persistent per-pixel flux, effective-photon-count, and gather-radius state. Without accumulation, the current photon batch remains a fixed-map diagnostic. Live animated water disables accumulation; use `Render Paused View` to freeze simulation while refining it.

Camera, lens, resolution, mode, scene, lighting, material, and caustic-setting changes discard the pixel-space SPPM state. Camera-only changes do not invalidate the world-space photon map or light-side target distribution: the current photon batch is reused once for the new view before photon sequence advancement resumes. Transport-affecting changes rebuild the map and rewind its deterministic sequence.

For a batch with `M` accepted photons, historical effective count `N`, radius `R`, flux `tau`, and SPPM alpha `a`, the update is `N' = N + aM`, `R' = R sqrt(N' / (N + M))`, and `tau' = (tau + Phi) R'^2 / R^2`. Radiance divides `tau'` by `PI R'^2` and all photon attempts emitted across the accumulated iterations. Zero-hit batches preserve flux, count, and radius while the total-emission normalization continues to advance. `SPPM Radius Reduction` maps from zero (alpha one, fixed radius) to one (small alpha, aggressive shrinkage); its existing serialized field remains `GatherRadiusDecayRate`. Radius has a `0.001` floor. The grid uses the initial radius, so all smaller per-pixel searches remain covered.

The radius-floor update uses the actual clamped `R'^2 / R^2` area ratio. Once a pixel reaches the floor, new photon flux continues accumulating without the artificial energy loss that would result from applying the unclamped count ratio.

`CSCausticsDebug` displays raw linear SPPM radiance without denoising or tone mapping. Final-color caustics use the same estimator before their separate beauty composite. Camera passes in one photon iteration are averaged before a single per-pixel recurrence, avoiding multiple updates from the same photon map.

Final-color `CSMain` never compiles camera-side photon gathering. When caustics are enabled, `GameManager` dispatches `CSCausticsFinalColor`, maintains its matching progressive accumulation, and then runs the small `CompositeCaustics` utility kernel. Disabled rendering does not allocate the scene photon map or dispatch these kernels. Caustic photon target-distribution helpers still compile only for `TraceCausticPhotons`.

Adaptive sampling stores its base radiance in `AccumulationResult` while `Beauty` is updated only for
scheduled pixels. Before compositing the full-frame caustic estimate, `GameManager` restores
`Beauty` from that base accumulator (except during the temporary bootstrap preview). This prevents
the compositor from adding the full caustic image repeatedly to pixels that adaptive scheduling did
not update that frame.

## Sampling And Estimation

Photon attempts use a deterministic seed plus a progressive photon-frame index. CPU-built distributions compact eligible light/refractor pairs, weighted by approximate useful flux. Glass-mesh targets use an area-weighted triangle CDF; the selected triangle probability is included in the area-to-solid-angle PDF conversion.

Two-dimensional emitter, refractor-surface, and sphere-cone coordinates use independently hashed dimensions rather than paired affine bit-reversal dimensions. This avoids batch-scale spatial lattices that can leave large portions of an area emitter or glass triangle unsampled. Targeted launches are accepted only when the selected refractor is the actual nearest scene boundary, so opaque blockers and nearer refractors are not bypassed.

Photon power includes emitter power, selection PDFs, emission PDFs, Fresnel branch probability, transmission throughput, and glass/water absorption. The gather uses a normalized Epanechnikov disk kernel:

```text
irradiance = sum(photon power * receiver terms) / (photonAttemptCount * PI * radius^2)
```

Normalization uses attempted photon count, not successfully stored count. Receiver-facing and exact-radius tests are applied during gathering.

## Spatial Grid

A bounded world-space grid indexes photons through atomic per-cell linked lists:

1. Registered sphere and mesh geometry determines padded grid bounds.
2. The gather radius determines cell size unless the grid would exceed 262,144 cells.
3. Each stored photon is inserted into its cell's linked list.
4. Gathering visits only cells overlapping the requested radius.

The benchmark overlay reports grid-cell count, indexed photons, out-of-bounds photons, and capacity overflow metadata. Metadata arrives asynchronously, so it can represent the most recently completed batch.

## Controls And Invalidation

`GameManager` exposes:

- `enableCaustics`
- `causticPhotonCount`
- `causticGatherRadius`
- `causticSeed`
- `causticIntensity`

The current manager API exposes the seed as `GameManager.Caustics.Seed`. In **Ray Tracing Controls > Caustics > Photon Seed**, changing the serialized seed rebuilds the map and restarts its photon sequence, but preserves compatible SPPM accumulation. It is independent of the camera/path sampler seed and `Random Noise`; a fixed photon seed still produces fresh deterministic batches during accumulation.

Photon resources and the map rebuild when relevant emitter, refractor, receiver, material, geometry, photon-count, radius, seed, or algorithm state changes. Animated water rebuilds the map as its wave phase changes. Exposure, tone mapping, depth of field, and camera transforms do not invalidate the map.

Turning caustics off releases their resources and returns to the unchanged default renderer.

## Validation

EditMode coverage verifies:

- Disabled-state resource isolation and unchanged non-caustics rendering.
- Fixed-seed photon positions and powers.
- Sphere, triangle, directional, glass-mesh, and water transport.
- Multi-event bounce-budget behavior.
- Valid normalized light/refractor and mesh-triangle target distributions.
- Focused caustic image signatures and energy stability across photon counts and gather radii.
- Production-scene photon-map construction and indexed receiver photons in `Assets/Scenes/Generated/Caustics.unity`.

Use the benchmark runner's caustic photon-count sweep to measure the enabled photon-count curve from `2^10` upward, retest the highest two counts for 30 frames, and then compare them with a 30-frame caustics-disabled run. The runner pauses renderer submission for its configurable cooldown between configurations. `Benchmark_Caustics` and `Benchmark_CausticsTriangleLight` provide focused fixtures for photon-map tuning.

For image-quality and convergence comparisons, use the generic manifest at `Assets/Editor/RayTracingExperiments/caustics_gather_radius_decay.json`. It compares the fixed-radius schedule (`GatherRadiusDecayRate = 0`) with the progressive schedule (`GatherRadiusDecayRate = 1`) against the checked-in 1024x1024 high-quality caustics reference. This manifest uses a 60-second wall-clock capture; experiment manifests should generally set either `durationSeconds` or `samples`, not both. The command-line experiment requires that existing reference and does not regenerate it:

```sh
/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath /Users/nic.foster/Projects/GPURayTracing \
  -executeMethod RayTracingSceneCapture.CaptureFromCommandLine \
  -rayTracingExperiment Assets/Editor/RayTracingExperiments/caustics_gather_radius_decay.json \
  -rayTracingOutput /Users/nic.foster/Projects/GPURayTracing/TestCaptures \
  -logFile -
```
