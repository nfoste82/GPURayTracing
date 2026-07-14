# Caustics Prototype

This document proposes a deliberately narrow photon-mapped caustics prototype for the Unity GPU ray tracer. The primary constraint is that disabling caustics must preserve the existing normal renderer's behavior and cost as closely as possible.

## Goals

- Produce stable, recognizable focused caustics from sphere lights through glass spheres onto diffuse receivers.
- Keep the first implementation small enough to validate before adding spatial indexing, glass meshes, or water.
- Preserve the existing final-color path tracer when caustics are disabled.
- Reuse photons while the camera moves through an otherwise static scene.
- Provide a caustics-only debug mode and deterministic test coverage.

## Non-Goals For The First Prototype

- General bidirectional path tracing or specular-manifold sampling.
- Glass meshes, concave refractors, procedural water, or nested media.
- Multiple refractive or reflective objects in one photon path.
- Real-time dynamic-scene photon-map rebuilding.
- A production-quality photon lookup structure in the first validation pass.

## Disabled-Path Requirement

The feature should have an explicit `GameManager.enableCaustics` toggle, defaulting to `false`. When disabled:

- Do not allocate caustic photon buffers or auxiliary lookup buffers.
- Do not dispatch caustics kernels.
- Do not gather photons at camera-ray hits.
- Do not add caustics state to normal frame-accumulation invalidation unless the feature is enabled.
- Do not add photon work to object registration, BVH traversal, direct-light sampling, or material scattering.
- Preserve existing final-color image signatures and performance within measurement noise.

Prefer a separate compute-shader variant for the final-color gather, such as `#pragma multi_compile _ CAUSTICS_ENABLED`, rather than a runtime branch inside the hot shading loop. `GameManager` should enable the keyword only when the feature is active. This lets the default variant compile without the photon buffer declarations and gather loop and avoids per-hit branch overhead when caustics are disabled.

The existing `DEBUG_RENDER` split should remain independent. The caustics debug visualization may require both `DEBUG_RENDER` and `CAUSTICS_ENABLED`, but selecting another debug mode while caustics are disabled should not allocate or dispatch photon resources.

Before adopting the variant, compare shader compile time and generated performance against the current default kernel. If adding another `multi_compile` materially worsens import or warmup time, use a separate caustics-enabled compute shader asset that shares an include containing common data structures and intersection helpers. Do not duplicate the production intersection implementation manually.

## Proposed Pipeline

Use separate forward photon generation and normal camera rendering:

```text
If caustics enabled and photon map dirty:
    ClearCausticPhotons
    TraceCausticPhotons
    ClearCausticGrid
    BuildCausticGrid

CSMain:
    Trace the existing camera path
    At visible diffuse hits, gather nearby caustic photons
    Add gathered caustic radiance
```

The photon map is world-space lighting data, not camera-space data. Static final-color rendering with HDR frame accumulation now traces an independent fixed-size photon batch each rendered frame and averages those complete caustic estimates. When accumulation is unavailable, the current batch remains fixed instead of flickering. Scene, light, material, or caustic-setting changes reset both accumulation and the progressive photon sequence; advancing the photon frame index does not reset accumulation.

## Initial Supported Path

The first prototype targets one path family:

```text
sphere light -> glass sphere transmission -> diffuse receiver -> camera
```

Photons should be stored only after a path has scattered from a supported glass sphere and then hits a diffuse receiver. Ordinary light-to-diffuse photons are not caustics and should not be stored.

Start with transmission caustics. First-surface reflection, total internal reflection, multiple glass interactions, glass meshes, and water can follow after the estimator is stable.

## Photon Data

A minimal photon record is:

```hlsl
struct CausticPhoton
{
    float3 position;
    float3 incomingDirection;
    float3 power;
};
```

Keep validity/count metadata separate so the record stays compact. A GPU append buffer is convenient, but an explicit atomic count plus fixed-capacity `RWStructuredBuffer` may be easier to clear, inspect, and test consistently across Metal and other backends. Clamp writes at capacity and expose overflow through a diagnostic counter.

The CPU-side stride must be derived explicitly and checked against the HLSL layout. Avoid adding a radius to every record; use a global gather radius initially.

## Photon Generation

Add dedicated kernels rather than extending `CSMain`:

- `ClearCausticPhotons`: reset photon count, overflow status, and any later grid counters.
- `TraceCausticPhotons`: launch one thread per attempted photon.

For each photon:

1. Select a sphere light using a documented probability distribution.
2. Select a supported glass sphere, initially using a power/distance/bounds importance weight.
3. Sample a direction from the light toward the sphere's visible solid angle or a conservative bounding cone.
4. Divide photon power by the light-selection, refractor-selection, and directional PDFs.
5. Intersect the target sphere and apply the existing Snell/Fresnel and absorption rules.
6. Continue from the sphere exit toward the scene.
7. Store the photon if the next relevant hit is an opaque diffuse receiver.

Reusing the production sphere-refraction behavior is important, but its current helper is coupled to camera-path `RayHit`, bounce accounting, and medium-stack state. Refactor only the smallest shared optical operation needed by both paths. Do not make the normal path call a more generic or more expensive abstraction merely to support photons.

Photon generation uses its own deterministic base seed and progressive frame index. Light/refractor selection, triangle-light emission positions, mesh target positions, and sphere-target cone directions use scrambled low-discrepancy samples; the scramble changes each photon frame while remaining deterministic. Changing camera sampling does not change the photon sequence.

## Estimator And Energy Accounting

The prototype should be energy-stable, even if it is initially biased by finite-radius density estimation.

Track these probabilities explicitly:

- Light-selection PDF.
- Photon emission position/direction PDF.
- Refractor-selection PDF.
- Targeted cone or solid-angle PDF.
- Fresnel transmission branch probability.

Photon power should include the corresponding inverse-PDF factors, emitter power, transmission throughput, and distance-based glass absorption. Avoid a free intensity multiplier as the primary normalization mechanism. A user-facing intensity control may be useful for art direction later, but the default value should represent neutral scaling.

The gather uses a normalized Epanechnikov disk kernel. A photon at squared receiver distance `d2` within radius `r` receives weight `2 * (1 - d2 / r2)`; the factor of two gives the kernel the same integrated energy as a uniform disk. The estimator remains:

```text
irradiance = sum(photon power * receiver terms) / (photonAttemptCount * PI * radius^2)
```

Use the number of attempted photons for normalization, not only the number successfully stored. Otherwise changing targeting efficiency changes image brightness.

The smooth kernel fades each photon contribution to zero at the gather boundary, avoiding visible hard circular splats without changing the expected total energy. It does not replace the need for adequate photon density; isolated photons remain visible as smooth spots until enough useful paths overlap.

Reject photons whose incoming direction lies behind the receiver normal. Define whether diffuse albedo and the Lambert factor are applied when storing or gathering, and apply them exactly once.

## Initial Linear Gather

For validation, camera-visible diffuse hits may scan the valid photon buffer and gather photons within `causticGatherRadius`.

This is intentionally simple but scales as:

```text
camera diffuse hits * stored photon count
```

Keep the initial buffer small and use the benchmark scene at a modest render resolution. The linear gather is a proof of the transport and normalization, not the intended production implementation.

The initial gather was compiled only into the caustics-enabled final-color variant so it did not add a loop or buffer access to the default renderer. Milestone 4 has replaced this linear scan with the spatial lookup below.

## Spatial Lookup

The implemented lookup uses a fixed world-space grid:

1. Derive padded grid bounds from registered sphere and mesh geometry.
2. Use the gather radius as the target cell size, increasing it only when needed to stay below 262,144 cells.
3. Clear one integer head per cell.
4. Insert stored photons into per-cell linked lists using one integer next index per photon.
5. Gather only from cells overlapping the search radius and retain the exact radius and receiver-facing tests.

The benchmark overlay reports total cells, indexed photons, and out-of-bounds photons. Photon-buffer overflow remains separately observable through metadata. The grid is rebuilt only when the world-space photon map is dirty, not when the camera moves.

## Runtime State And Dirtiness

Initial controls:

- `enableCaustics`, default `false`.
- `causticPhotonCount`, interpreted as attempted photons per rendered batch.
- `causticGatherRadius`.
- `causticSeed`.
- Optional `causticIntensity`, default `1.0` and not used to hide normalization errors.

Regenerate the photon map when enabled and any of these change:

- Light transform, radius, color, or registration.
- Supported refractive sphere transform, radius, material color, opacity, or IOR.
- Receiver geometry or transform.
- Photon count, radius-dependent lookup settings, or caustic seed.
- Relevant shader/algorithm version state.

Do not regenerate for camera transforms, exposure, tone mapping, depth of field, number of camera passes, or camera-only debug settings.

The first implementation may conservatively rebuild when any scene geometry changes. Later revisions can track caustic emitters, refractors, and receivers separately.

Changing photon-map contents or gather settings must reset final-color frame accumulation while caustics are enabled. Toggling caustics off must release resources and reset accumulation once, then return to the unchanged default renderer.

## Debugging

Extend the existing `Caustics` debug mode in two stages:

- Current baseline: visualize rare caustic paths discovered by the ordinary backward tracer.
- Photon prototype: visualize only gathered photon-map radiance, with no direct or ordinary indirect lighting.

Add diagnostics for:

- Attempted photons.
- Stored photons.
- Photon-buffer overflow.
- Photons that miss the targeted refractor.
- Photons absorbed, reflected, or transmitted.
- Photons reaching diffuse receivers.
- Later, grid overflow and out-of-bounds photons.

These can initially appear in the benchmark overlay or editor logs. A photon-density debug view is useful after spatial binning exists.

Debug output should remain untone-mapped, consistent with existing debug modes. If raw photon values are too dim or bright to inspect, use an explicit documented visualization scale that does not affect final-color rendering.

## Testing

Required coverage for the first prototype:

- Existing final-color image signatures remain unchanged with caustics disabled.
- A disabled-state test confirms no photon buffers are allocated and no caustics dispatch occurs.
- A deterministic photon-generation probe verifies fixed-seed photon count, positions, and power for a simple light/sphere/plane fixture.
- A caustics-enabled image fixture verifies a focused sphere caustic on a diffuse receiver.
- Photon count changes reduce variance without materially changing average energy.
- Gather radius changes trade sharpness for noise without large energy drift.
- Camera motion reuses the photon map while resetting only final-color accumulation.
- Light, refractor, receiver, and caustic-setting changes invalidate the photon map.
- Photon-buffer overflow is bounded and observable.

Benchmark disabled and enabled rendering separately. The disabled benchmark should use the current default renderer and show no statistically meaningful GPU frame-time regression.

## Milestones

Milestones 1 and 2 are implemented as the initial prototype. Milestone 3 correctness and regression fixtures are implemented; performance measurement remains the next step.

### Milestone 1: Isolated Skeleton

- Implemented: controls and lifecycle state, default disabled.
- Implemented: an isolated `CAUSTICS_ENABLED` shader variant and dedicated clear/trace kernels.
- Implemented: photon resources allocate and dispatch only while enabled.
- Existing image regressions pass with the caustics keyword explicitly disabled; disabled-path performance measurement remains part of validation.

### Milestone 2: Sphere Photon Prototype

- Implemented: sphere lights, one glass sphere transmission, and opaque diffuse receivers.
- Implemented: targeted solid-angle photon generation with light/refractor selection, Fresnel transmission, and attempted-photon normalization.
- Implemented: a fixed-capacity photon buffer with stored/overflow metadata.
- Implemented: a first-visible-hit linear gather and photon-map `Caustics` debug output.

### Milestone 3: Validation

- Implemented: deterministic photon-generation coverage, a disabled-resource lifecycle test, and a focused image fixture based on the supported sphere half of `Benchmark_Caustics`.
- Implemented: energy-stability checks across photon counts and gather radii, including the expected sharper peak at a smaller radius.
- Implemented: photon power now includes material transmission and the gather applies the Lambert factor once rather than twice.
- Implemented: the unsupported prism light is disabled in the checked-in benchmark and omitted by the generator so it cannot be redirected through the sphere prototype.
- Implemented: an `F4` benchmark matrix measures the disabled variant and 64-16384-photon enabled variants after warmup, displays results, and writes CSV output for repeatable comparison.
- Run the matrix on target hardware and record the practical linear-gather limit before deciding whether to start Milestone 4.

### Milestone 4: Spatial Grid

- Implemented: a bounded world-space photon grid with atomic per-cell linked lists.
- Implemented: gathering visits only neighboring cells overlapping the radius.
- Implemented: grid cell, indexed-photon, and out-of-bounds diagnostics appear in the benchmark overlay; photon capacity overflow remains bounded.
- Implemented: on an Apple M3 Max, the 2,048-photon grid benchmark measured 2.838 ms versus 2.452 ms disabled (15.7% overhead); performance stayed approximately flat through 4,096 photons and rose to 3.886 ms at 16,384 photons.
- Implemented: 2,048 photons is the practical default based on visual sufficiency and measured cost.

### Milestone 5: Broader Transport

- Implemented: emissive triangle lights can emit targeted photons through supported glass spheres, with uniform area sampling, one-sided emission, and area/cosine power weighting.
- Implemented: reflected and multi-event glass-sphere caustics use an iterative photon transport loop with shared scene intersections, medium-stack transitions, Fresnel/Snell behavior, absorption, and a bounded bounce budget.
- Implemented: closed glass meshes are targeted by sampling their triangle surfaces with an area-to-solid-angle PDF correction, validating visibility against their mesh BVHs, and transporting boundary-by-boundary through the same medium-stack loop. This avoids the sparse photon maps produced by loose bounding-sphere targeting on non-convex meshes.
- Implemented: glass mesh photon Fresnel, transmission, and reflection use interpolated vertex normals when `InterpolateNormals` is enabled, while intersections and boundary identity remain geometric.
- Add procedural water only after static glass behavior and photon-map invalidation are stable.

## Prototype Acceptance Criteria

The first useful prototype is complete when:

- Caustics are disabled by default and normal renderer outputs remain unchanged.
- Disabled rendering performs within measurement noise of the pre-caustics renderer.
- `Benchmark_Caustics` shows a stable focused pattern under the glass sphere.
- `Caustics` debug mode cleanly isolates photon-map radiance.
- Increasing photon count mainly reduces noise rather than changing brightness.
- Reducing gather radius sharpens the pattern while increasing variance.
- Static photon maps survive camera movement and regenerate after relevant scene changes.
- Fixed-seed tests and all existing regressions pass.

## Recommended First Implementation Boundary

Implement Milestones 1 and 2 with sphere lights, glass spheres, diffuse receivers, and a small linear photon gather. Do not include glass meshes, water, or a spatial grid in the first code change. This boundary tests the complete feature architecture, disabled-path isolation, photon estimator, resource lifecycle, and visual value before committing to broader complexity.
