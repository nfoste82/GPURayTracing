# Caustics

The renderer provides optional photon-mapped caustics for focused refracted and reflected lighting through glass and water. `GameManager.enableCaustics` defaults to `false`; the disabled renderer does not allocate photon resources, dispatch caustics kernels, or gather photon radiance.

## Supported Transport

- Sphere, triangle, and directional light photon emission.
- Glass spheres and closed glass meshes, including reflection, transmission, absorption, and bounded multi-event transport.
- Finite procedural water, including nested water/glass paths.
- Opaque diffuse receivers visible through ordinary camera paths and after specular water/glass boundaries.
- A dedicated `Caustics` debug mode that dispatches the gather-only `CSCausticsDebug` kernel.

## Pipeline

When caustics are enabled, the renderer maintains a world-space photon map independently from camera sampling:

```text
ClearCausticPhotons
TraceCausticPhotons
ClearCausticGrid
BuildCausticGrid

CSMain or CSCausticsDebug:
    Trace camera path
    Gather nearby receiver-facing photons at diffuse hits
    Add photon radiance
```

Static final-color rendering with frame accumulation advances an independent photon sequence for each rendered batch and averages the complete estimates. Without accumulation, the current photon batch remains fixed. Caustic state changes reset both final-color accumulation and the photon sequence; camera-only changes do not rebuild the photon map.

The default final-color shader variant does not contain photon buffers or gathering work. Caustic photon target-distribution helpers compile only for `TraceCausticPhotons`, keeping the register-heavy camera kernel within Metal's practical compiler limits.

## Sampling And Estimation

Photon attempts use a deterministic seed plus a progressive photon-frame index. CPU-built distributions compact eligible light/refractor pairs, weighted by approximate useful flux. Glass-mesh targets use an area-weighted triangle CDF; the selected triangle probability is included in the area-to-solid-angle PDF conversion.

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

Use the benchmark runner's caustic photon-count sweep to compare disabled and enabled configurations on target hardware. `Benchmark_Caustics` and `Benchmark_CausticsTriangleLight` provide focused fixtures for photon-map tuning.
