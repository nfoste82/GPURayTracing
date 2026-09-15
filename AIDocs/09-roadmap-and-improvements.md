# Roadmap And Improvements

This document retains future work outside the active sampling repair sequence. `27-renderer-sampling-audit-and-repair-plan.md` is the authoritative plan for renderer sampling, MIS/RIS, debug ownership, and related validation. For current implementation limits, see `05-known-limitations.md`; for benchmark methodology, see `10-benchmarking-and-performance.md`.

## Recommended Order

Complete the correctness and evidence gates in document 27 before promoting sampling optimizations or reuse. Independent lifecycle, authoring, geometry, and reconstruction work below can proceed where it does not destabilize that sequence. These sections are future options, not a competing numbered repair plan.

## Current Status

- The active renderer is queue-driven wavefront, not `TracePath()`/`CSMain`. Medium identity, stack-driven refraction, segment absorption, ordered transparent shadows, shared Lambert/GGX evaluation, and opaque VNDF sampling are integrated. Sphere entry/exit boundaries are explicit path events, and camera initialization includes containing translucent spheres. See `03-compute-shader-renderer.md` and `07-shader-lighting-and-materials.md`.
- Local RIS is standard for eligible primary opaque `ImportanceSampled` events; ordinary sampling handles unsupported events, later bounces, and diagnostic strategies. Temporal/spatial reuse is integrated through an opt-in dry wrapper but remains experimental and default-off. Historical local mean checks and manual wavefront smoke coverage do not validate general MIS/RIS correctness. See documents 23, 26, and 27.
- The audit identifies unresolved GGX denominator/PDF distortion, explicit/continuation MIS and triangle-falloff mismatches, semantic RNG overlap and pixel-scramble collisions, first-hit debug ownership, and wavefront depth/terminal handling. Document 27 owns their repair order and acceptance gates; none are claimed fixed here.
- Existing CPU/GPU probes and image fixtures are a regression foundation, not proof of current wavefront parity. Active-route queue, diagnostic, mean-correctness, and image validation remain required. See `11-regression-testing.md` and document 27.

## Parallel Correctness And Safety

- Preserve and restore the application's previous `QualitySettings.vSyncCount`, `Application.targetFrameRate`, and `Time.timeScale` when entering/leaving single-frame mode, including disable/destruction cleanup.
- Detect `MeshFilter.sharedMesh` replacement and define an explicit dirty path for runtime vertex/topology/UV changes. Validate `mesh.isReadable` with an actionable error before reading imported mesh data.
- Bring CPU center-autofocus sphere/water intersection behavior into parity with production GPU intersections; the CPU water query uses an average plane rather than procedural waves. Scene-data update ordering and event-driven focus caching are already implemented.
- Validate required `shader`/camera wiring at startup and ensure the camera presenting `RayTracingCameraRenderer.OnRenderImage()` matches the camera whose matrices and controls `GameManager` uses.

## Additional Regression Coverage

- Extend registration/unregistration and lifecycle tests for multiple managers and disabled domain reload. Registration and `_buffersNeedRebuilding` are already manager-local; this is coverage work, not a pending ownership migration.
- Preserve existing deterministic intersection/BVH, medium, shadow, material, and texture fixtures as these independent systems evolve. Active wavefront bounds/queue and sampling regression gaps belong to document 27 rather than a repeated legacy `CSMain` test plan.
- Add focused validation scenes or numeric probes for nested/interpenetrating media and water entry/exit distances before changing those systems further.

## Further Material And Medium Work

- Full eta-dependent dielectric BSDF/PDF integration remains future work beyond the current opaque reflection sampler; coordinate it with document 27 rather than implementing isolated object-specific sampling fixes.
- Harden sphere and mesh glass for repeated internal reflection, concave/non-manifold/open meshes, exhausted bounce budgets inside a medium, and analytic Snell/TIR validation. Basic Snell transmission, distance absorption, bounded interior-object tests, and mesh TIR are already implemented.
- Improve wavy-top intersection with adaptive/root-finding behavior and optionally support multiple/transformed water volumes.
- Optional bounded homogeneous volumetric fog is implemented as a single axis-aligned `FogVolume`. Follow-up work includes analytic/GPU regression probes, a fog debug mode, volume-light MIS, caustic photon attenuation, and multiple or oriented volumes. Henyey-Greenstein anisotropy should expose a bounded phase parameter such as `[-0.9, 0.9]`, preserve isotropic behavior at zero, and share phase evaluation/sampling between direct lighting and continuation. Add finite-value, normalization, and forward/backward-scattering probes before changing defaults.

## Caustics

- Photon-mapped caustics are an optional, default-disabled feature. The disabled path does not allocate photon resources or dispatch caustics kernels; `_CausticsEnabled` makes the shared camera shader skip photon gathering without adding a caustics shader variant.
- The bounded world-space photon grid, deterministic photon/image fixtures, photon-count/gather-radius energy checks, triangle and directional emitters, multi-event sphere transport, closed glass-mesh targeting, and water transport are implemented. The visually sufficient 2,048-photon setting measured 15.7% overhead and is the practical default.
- Follow-up work includes photon attenuation through fog, more exhaustive dynamic-scene performance measurements, and further transport/estimator improvements.
- See `12-caustics.md` for the current architecture, invalidation rules, diagnostics, and testing.

## Lighting And Geometry Quality

- Imported vertex normals, hierarchical mesh/triangle light selection, and GGX VNDF opaque continuation are already integrated. Remaining global-emitter scalability work is the light BVH below, not another mesh-light hierarchy implementation.
- Extend mesh materials with emission textures and texture-aware triangle weights. Emissive hits and light construction must use consistent emission, with complete selection PDFs and subdivision-invariant energy tests after the sampling repairs in document 27.
- Extend environment importance proposals only where measured useful, preserving rotation, intensity, color space, invalidation, and zero-luminance-map handling under the repaired estimator.
- Sample sphere lights by visible solid angle instead of approximate disk samples.

## Performance And Tooling

- Avoid rebuilding/uploading both top-level BVHs every rendered frame when object bounds are unchanged. Reuse static trees and evaluate refitting for transform-only changes before a full SAH rebuild.
- Separate mesh geometry, material, light, and texture dirtiness. A material or transform change currently rebuilds every world-space triangle, per-mesh BVH, mesh-light entry, and texture-array slice.
- Upload sphere/light data only when relevant transforms or component values change.
- Extend existing benchmark CSV/JSON reporting where needed with repeated-trial median/p95 and complete scene/settings metadata; preserve warmup and diagnostic-overhead separation described in `10-benchmarking-and-performance.md`.
- Add GPU timing when supported; Unity's CPU frame time often collapses compute work into `Rendering`, so Xcode GPU Frame Capture remains useful on macOS.
- Add focused water march/refinement and mesh-light tessellation benchmarks before optimizing those paths.
- Consider dynamic-quality presets or user-selectable priorities if users need to favor bounces/shadows over sample count or light quality.

### Object-Space BLAS Instancing

The existing mesh-template cache already shares object-space topology construction, but runtime still transforms and uploads triangles and BVH bounds for every mesh instance. Replace that expanded world-space representation with true instances: retain one object-space BLAS and triangle buffer per unique mesh/template, and upload a compact record for each scene instance. This is a portable compute-shader technique; it does not depend on hardware ray tracing, a vendor API, or bindless textures.

- Each instance record should contain its shared BLAS/geometry offsets and counts, object-to-world and world-to-object transforms, inverse-transpose normal transform, world-space TLAS AABB, stable object identity, and material/submesh override references.
- The TLAS and shadow TLAS should continue to contain world-space instance AABBs. Once a leaf selects an instance, transform the ray into its object space, traverse the shared BLAS, then transform the hit position and geometric/shading normals back to world space.
- Establish and test a single ray-distance convention before implementation. With normalized world-space rays transformed into object space, non-uniform scale must not corrupt nearest-hit comparisons, shadow maximum distance, ray offsets, refraction segment lengths, or light PDFs. Normals require inverse-transpose transformation and re-normalization.
- Preserve per-instance material assignment and stable IDs. Sharing a BLAS must not accidentally share material data across instances, and temporal feature/history identity must remain stable when buffers rebuild.
- Keep unique/dynamic topology on the current expanded path initially. Do not make skinned/deforming meshes depend on the static shared BLAS path until deformation/refit data and temporal motion semantics exist.

Expected tradeoffs: equal image quality and Monte Carlo convergence for equivalent scenes; substantially lower geometry/BVH memory and CPU upload work for repeated meshes; small per-hit transform overhead; and better scene scalability. More instances can still increase TLAS/shadow traversal cost, so this is not a replacement for culling or LOD.

Validation and benchmarks:

- Add deterministic brute-force versus instanced-BLAS hit equivalence tests for identity, translation, rotation, uniform scale, and non-uniform scale, including closest-hit `t`, normals, UVs, material selection, shadow distance, and mesh refraction boundaries.
- Add image fixtures comparing a repeated-object scene rendered through the old expanded representation and the instanced path, including reflective, glass, textured, and emissive instances.
- Add repeated-instance benchmark scenes and report unique mesh count, instance count, triangle/BVH bytes, upload bytes, TLAS nodes, CPU preparation time, and GPU frame time. Compare one source mesh with many instances against equivalent duplicated meshes.
- Retain the existing path as a temporary A/B fallback until these tests show equivalence and measurements demonstrate a repeated-geometry benefit.

### Light BVH For Global Emitter Selection

The current global importance sampler has a fixed `128`-emitter consideration cap. Although each emissive mesh already uses one global entry and selects a triangle internally, scenes beyond that cap are biased because omitted emitters have zero probability. After the estimator repairs in document 27, replace the capped linear selection pass with a compact CPU-built light hierarchy, beginning with a conventional light BVH rather than a spherical-Gaussian tree. `AllLights` comparisons below test selection changes; they do not independently validate the shared estimator.

- Build or refit the hierarchy only when emitter membership, transformed bounds, emission, or mesh-triangle distributions change. A node should conservatively store bounds, aggregate emitted flux, a normal cone or equivalent directional bound, and child/leaf ranges.
- At a shading point, traverse/sample branches using a conservative receiver-dependent contribution bound based on node flux, distance, and orientation. Every branch and leaf probability must contribute to the selected emitter's PDF.
- Keep the existing per-mesh area-weighted triangle CDF as the second stage after selecting a mesh emitter. Sphere and directional lights can remain leaves in the same global hierarchy; environment sampling remains its separate distribution and MIS path.
- Convert the complete hierarchy-selection times triangle-selection times shape PDF into the existing solid-angle measure before applying the power heuristic. A zero-weight emitter must either be excluded consistently or retain an explicit nonzero fallback probability; never silently omit a valid emitter.
- Preserve `GetLightHittingPoint()`'s one `[loop]` and one inlined `SampleSingleLight()` call site. Selection can move to helper functions, but do not duplicate the BVH-traversing shadow/light-evaluation body, which is known to cause severe Metal compile-time expansion.

Expected tradeoffs: better direct-light quality and lower variance when many emissive objects exist; removal of the current cap-induced selection bias; logarithmic selection work instead of an O(light-count) per-hit weighting scan; and a small additional light-node buffer. Small-light scenes may see a minor fixed traversal cost with no visible benefit, so retain simple all-lights/uniform paths for diagnostics and benchmark before selecting defaults.

Validation and benchmarks:

- Test hierarchy bounds, flux aggregation, PDF normalization, and nonzero selection probability for every eligible emitter. Empirically sample selections and compare observed emitter/triangle frequencies against their reported PDFs.
- Compare direct-light energy and image signatures against `AllLights` in deterministic low-light, dense-emissive-mesh, many-mesh-light, glossy, and moving-emitter fixtures. Confirm that subdividing an emitter does not materially alter its brightness.
- Extend `Benchmark_EmissiveDragon` and `Benchmark_ManyLights`, or add a focused light-hierarchy fixture, to report emitter count, mesh-light count, triangle count, hierarchy depth/node count, selection time, direct-light variance, and GPU frame time.
- Measure hierarchy rebuild/refit cost and define a conservative fallback for rapidly moving lights before enabling it by default.

### Ray Material Preset Assets

Add optional data-only `RayMaterialPreset` assets as an authoring layer over the existing `RayMaterial`; do not introduce a second shader material model or general Unity-shader introspection. A preset should populate the established scalar and texture references for a recognizable material category while leaving the renderer's uploaded material representation unchanged.

- Presets may define material type, base color, metallic, smoothness, opacity, refraction index, specular/transmission, emission, normal/metallic-roughness/albedo texture references, and documented map-channel/color-space expectations.
- Define a clear override policy: applying a preset copies values into `RayMaterial`; later manual edits intentionally diverge. Inspector UI should display the applied preset and whether the component matches it, but runtime shading must read only the normal `RayMaterial` data.
- Applying or changing a preset must use the existing texture/material dirty path, rebuild only affected texture-array slices/material records, and invalidate accumulation exactly as direct material edits do. It must not rebuild unrelated mesh geometry or BVHs.
- Start with a small set of calibrated diffuse, metal, glass, and emissive examples. Avoid presets that claim physical accuracy beyond the renderer's supported BRDF, medium, texture-filtering, and emissive-light behavior.

Expected tradeoffs: better authoring consistency and quicker scene setup, with no per-sample shader cost or convergence change. GPU memory is unchanged unless a selected preset introduces previously unused textures; the preset assets themselves are negligible. Reasonable parameter defaults can indirectly reduce fireflies or noise caused by extreme authoring values, but presets do not improve the estimator.

Validation:

- Add editor tests for apply, match/divergence detection, undo/redo, missing textures, and accumulation invalidation.
- Add material fixtures that compare a preset-applied object with the equivalent manually configured `RayMaterial`, covering diffuse, metal, glass, emissive, textured, and normal-mapped cases.
- Confirm preset-only changes update only the expected material/texture resources through startup/dirty-path diagnostics and leave geometry/BVH buffers untouched.

### Low-Visual-Cost Interactive Quality Trades

These are opt-in quality modes or future dynamic-quality ladder steps. They are intended to reduce interactive rendering cost with a limited, explicit visual tradeoff; preserve the current native-resolution/high-quality path and benchmark each mode before changing defaults.

- **Internal-resolution tracing plus spatial reconstruction: implemented baseline.** `renderResolutionPercent` in Render Quality controls internal tracing and feature-buffer dimensions from `25%` to `100%`; linear HDR beauty is Catmull-Rom reconstructed to the full-size camera target, then exposure and ACES tone mapping are applied. A `50%` width/height scale traces one quarter of the primary pixels before post-process overhead. Thin silhouettes, fine textures, tiny specular highlights, and focused caustics are the principal risks. CAS/FSR 1 remain follow-up work. See `13-denoising-and-upscaling.md` for the resource and testing contract.
- **Event-driven center autofocus: implemented.** The CPU center-focus query runs after scene-data updates only when camera pose, ray-traced scene data, water state, or `numberOfPasses` changes; stable progressive-accumulation frames reuse the current focus target. Validate camera motion, fast foreground crossings, glass opacity policy, water, and click-to-focus behavior. A later refinement can use the production GPU focus query at a bounded asynchronous cadence after confirming latency behavior.
- **Interactive caustic photon budgets.** Keep high photon counts as a still/offline-quality option, but add lower interactive presets and rely on progressive accumulation where applicable. The existing caustics benchmark found `2,048` photons visually sufficient at `15.7%` overhead on its M3 Max fixture; measure lower counts such as `256`, `512`, and `1,024` for each target scene and select the first count with acceptable caustic structure and energy. The tradeoff is transient photon noise/flicker, especially without accumulation. Use the existing photon-count sweep and preserve raw/caustic-debug comparisons.

Completion criteria: each mode is clearly labeled in the inspector/benchmark metadata, has a native or high-quality fallback, resets relevant accumulation/history safely, and is evaluated with GPU timing plus the named visual fixtures rather than only CPU frame duration.

## Visual And Reconstruction Work

- The physically configurable camera model is implemented with focus distance, pinhole/direct-radius/f-stop aperture modes, camera-space lens sampling, configurable blade count/rotation, anamorphic bokeh, accumulation invalidation, and asynchronous GPU click-to-focus. Add deterministic focus-plane and bokeh-shape image fixtures if this area receives further changes.
- Broaden mesh material texture support beyond mipless, repeat-wrapped arrays sized to each channel's largest source texture. Add mip/LOD support, configurable resolution limits, validated color-space handling, and per-texture repeat/clamp behavior. Consider emission and opacity maps if needed. Keep geometric normals for intersection orientation and medium-boundary identity while using mapped shading normals only for BSDF evaluation. Define accumulation and buffer dirtiness per texture/material change so replacing one material map does not rebuild unrelated geometry or BVHs.
- Add texture-specific debug modes for base color, roughness/metallic, shading normal, emission, opacity, UV coordinates, selected mip, and invalid texture/tangent data. Regression coverage should include UV interpolation, wrapping, color-space conversion, normal-map handedness, missing-map defaults, texture replacement invalidation, and stable lighting when texture resolution or filtering changes. Alpha masking or blended opacity must be a separate later step because it affects primary, shadow, refraction, selection, autofocus, and caustic-photon traversal rather than shading alone.
- Broader texture completion criteria: textured materials use documented color spaces and sampling rules, normal maps preserve geometric boundary behavior, roughness drives the same BRDF model in direct and continuation paths, texture-only edits rebuild only affected resources, and deterministic fixtures cover each supported map type.
- The optional firefly clamp, HDR feature buffers, and A-trous/SVGF stages are implemented; do not restart their original implementation sequence. Follow `13-denoising-and-upscaling.md` and `14-svgf-implementation-plan.md` for remaining reconstruction work. For mirrors and glass, evaluate whether features should follow near-delta reflection/transmission to the first stable diffuse surface rather than always describing the primary specular boundary; make that policy explicit and visualize feature depth/validity.
- Continue focused caustic denoising validation: retain undenoised/raw output, check that sparse high-energy structure is not suppressed or smeared across receiver edges, and compare low-sample results against high-sample references with caustics enabled and disabled. Consider a caustics radiance feature or conservative denoiser blend/mask if needed.
- Denoiser testing should cover feature correctness through diffuse, metal, glass, nested media, textured meshes, water, and caustic receivers; finite/non-NaN output; accumulation resets; odd resolutions; and unchanged beauty signatures while denoising is disabled. Measure edge preservation, temporal stability, residual variance, caustic peak/total energy, and execution cost at fixed low sample counts instead of judging only screenshots.
- Denoiser completion criteria: the disabled path does not change existing final-color output or allocate unnecessary resources, feature buffers are stable and inspectable, low-sample diffuse/glossy scenes improve without material-edge bleeding, and low-sample caustics retain recognizable shape and approximately stable energy relative to a high-sample raw reference.
- Tune defaults for light falloff, shadow randomness, passes, shadow quality, and noise after reference-image testing.
- Add debug legends/configurable ranges and material/debug presets only when they serve a specific diagnosis workflow.
