# Roadmap And Improvements

This document captures likely future work areas in priority order. For current implementation limits, see `05-known-limitations.md`. For performance hotspots and benchmark methodology, see `10-benchmarking-and-performance.md`.

## Recommended Order

The recent texture, mesh-light, glass, specular, imported-model, and procedural-water work makes correctness and regression coverage more valuable than adding another major rendering feature. The current recommended order is:

1. Finish explicit medium identity/state and carry it through production paths.
2. Unify segment absorption around the active medium and actual distance traveled.
3. Rework transparent shadow rays as ordered boundary traversal.
4. Replace the approximate direct specular path with shared BRDF/BSDF evaluation and sampling.
5. Add multiple importance sampling (MIS) after material and light PDFs are trustworthy.
6. Address independent correctness/lifecycle hazards and measured CPU-side performance work alongside the renderer sequence where they do not destabilize it.

## Current Status

- **Regression foundation: implemented.** CPU intersection/math tests, production GPU reflection/refraction/Fresnel/absorption probes, deterministic rendering signatures, transparent sphere/closed-mesh/stacked shadow fixtures, texture and mesh-light fixtures, deterministic BVH equivalence/depth checks, and tiny/odd-resolution GPU dispatch smoke tests are implemented. The water-family signature drift from finite-water AABB changes has been reviewed and recaptured.
- **Medium identity and path state: implemented.** `TracePath()` carries a fixed-capacity stack with implicit air, initializes underwater camera rays, updates transmitted water/sphere/mesh paths, and exposes overflow/unmatched-exit status through regression probes. Refraction now consumes stack state; starting inside closed sphere/mesh glass remains unsupported.
- **Segment absorption: implemented.** Production paths attenuate each traveled segment from the active medium; finite water clips against surface/XZ exits and finite-medium sky misses avoid infinite attenuation. Coherent BRDF/BSDF sampling and MIS remain.
- **Stack-driven refraction: implemented.** Path-selection Fresnel and sphere, mesh, and water transmission derive source/target IORs from the active medium and its entered/revealed neighbor. Reflection and TIR preserve stack state; production probes cover water -> glass -> water indices, direction, Fresnel, and water-surrounded TIR behavior.
- **Shadow boundary traversal: implemented.** Transparent shadow rays process nearest boundaries in order, attenuate actual active-medium segments, pair closed mesh entries/exits, retain a thin/open fallback, and preserve the opaque-only fast path.
- **Shared BRDF evaluation and sampling: implemented.** Opaque diffuse and metal paths share Lambert/GGX evaluation, Schlick Fresnel, Smith masking-shadowing, mixture PDFs, and `f * abs(N dot L) / pdf` continuation weighting. Dielectric transmission remains on the established medium-stack path while direct dielectric reflection uses shared GGX evaluation.
- **Multiple importance sampling: implemented for opaque reflection paths.** Explicit sphere/triangle light samples and opaque BRDF continuation samples use solid-angle PDFs and power-heuristic weights; emissive hits receive complementary weighting. Dielectric transmission and zero-radius delta-light fallbacks retain their established paths.

## Priority 0: Protect Upcoming Changes

- Deterministic mesh-glass image fixture data exercises production triangle, mesh-info, and per-mesh BVH traversal, including standalone and submerged closed-mesh refraction.
- Current-behavior nested-media fixtures cover air -> water -> sphere/closed-mesh glass -> water -> air, camera-starting-underwater, and camera-starting-inside-sphere-glass paths.
- Deterministic transparent-shadow fixtures for a sphere, a closed mesh, and stacked blockers now protect later boundary-distance changes.
- Deterministic randomized per-mesh, top-level, and shadow BVH reference traversals are compared against brute force and assert maximum depth against `BvhStackSize`.
- Tiny and odd-resolution GPU smoke tests now cover the `CSMain` output-dimension guard.

## Priority 1: Medium Identity And Path State

- Add a fixed-capacity path medium stack with implicit air at its base. Each entry should preserve medium type, object identity, IOR, opacity, and absorption color.
- Carry medium state through `TracePath()` without changing scattering first. This isolates state-management changes and should leave existing image signatures unchanged.
- Define tested push, matching-pop, parent lookup, and current-medium operations. A boundary exit must match object identity, not only material type or IOR.
- Initialize medium state for cameras/rays that begin inside the single water body. Define and test how starting inside closed sphere/mesh glass will be detected or explicitly unsupported in the first version.
- Make stack overflow and unmatched exits detectable through probe/debug output. Do not silently discard entries or pop an unrelated medium.
- Properly nested closed volumes are fully supported. Interpenetrating spheres retain the most recently entered active medium and can remove a non-current sphere on exit without corrupting state; arbitrary overlapping meshes/water and a physically complete active-medium-set model remain unsupported.

Completion criteria: production paths carry stable, test-covered medium state across bounces, nested transition probes pass, mismatch/overflow behavior is explicit, and existing final-color baselines remain unchanged.

## Priority 2: Segment Absorption

- Apply absorption per traveled ray segment using the medium active before the next boundary/hit, rather than applying separate post-hoc sphere, mesh, and water rules.
- Convert glass color/opacity and water color/absorption settings into one documented attenuation representation while preserving current values as closely as practical during the first refactor.
- Keep water segment distance clipped against the nearest wavy-top, side, or bottom boundary of the finite volume.
- Handle sky misses from finite media without attenuating by infinite hit distance.
- Keep the existing object-specific absorption helpers temporarily available only while migrating fixtures one path at a time; remove them once all production paths use segment state.

Completion criteria: every finite path segment is attenuated by its actual active medium and distance, air adds no attenuation, finite-water exits are respected, and nested-medium absorption tests pass.

## Priority 3: Stack-Driven Refraction

Status: implemented.

- Use the current medium IOR as the source and the pushed medium or revealed parent IOR as the target for every dielectric boundary.
- Replace hard-coded air -> material and material -> air assumptions in sphere, mesh, and water transmission helpers.
- Preserve Schlick/TIR branch behavior initially so this step changes only transition indices, not the whole material model.
- Update medium state only when transmission crosses a boundary. Reflection and total internal reflection remain in the current medium.
- Validate air -> glass -> air, air -> water -> air, and air -> water -> glass -> water -> air, including TIR where the outside medium is water rather than air.

Completion criteria: underwater glass refracts water -> glass -> water, reflected/TIR paths do not corrupt the stack, and source/target IOR probes agree with rendered nested-media fixtures.

## Priority 4: Shadow Boundary Traversal

Status: implemented.

- Treat a shadow ray as a finite ordered sequence of medium boundary events between the shaded point and sampled light.
- Pair closed-mesh entry and exit crossings and apply absorption over the actual internal distance instead of applying `ThinTransparentSurfaceDistance` independently to each triangle.
- Reuse medium identity/transition semantics for nested transparent shadow blockers while preserving the opaque fast path.
- Retain an explicit thin/open-surface fallback when no valid paired exit exists; report or visualize use of that fallback during debugging.
- Bound transparent crossing count and terminate when accumulated transmittance is negligible.

Completion criteria: thick and thin closed mesh blockers produce distance-dependent attenuation, stacked/nested blockers are processed in order, and opaque scenes retain their current fast path.

## Priority 5: Shared BRDF/BSDF Evaluation And Sampling

Status: implemented for opaque diffuse/metal reflection and direct dielectric reflection. Full sampled dielectric transmission remains part of future BSDF/MIS refinement.

- Replace the separate direct-light specular approximation and continuation-ray logic with shared material evaluation and sampling functions.
- Start with matched Lambert evaluation/cosine sampling, then add GGX reflection with Schlick Fresnel and Smith masking-shadowing.
- Use one perceptual mapping such as `roughness = 1 - smoothness` and `alpha = roughness^2` consistently in direct and indirect paths.
- Enforce an energy-conscious diffuse/specular split: metals have no diffuse term; dielectric diffuse response is reduced by Fresnel/specular energy.
- Return/evaluate material PDFs alongside BRDF/BSDF values and weight continuation throughput by `f * abs(N dot L) / pdf`.
- Integrate dielectric transmission only after stack-driven IOR transitions are stable.

Completion criteria: direct and continuation rays evaluate the same material model, sampled PDFs match their distributions, roughness behavior is shared, and numeric tests cover finite/non-NaN values and known-angle responses.

## Priority 6: Multiple Importance Sampling

Status: implemented for opaque diffuse/metal continuation and explicit sphere/triangle light sampling. Full dielectric BSDF integration remains future material work.

- Add light-sampling PDFs for sphere and triangle lights in the same measure used by material PDFs.
- Combine explicit light samples and BRDF/BSDF samples with a documented MIS heuristic, initially the power heuristic.
- Ensure emissive hits reached through BSDF sampling are weighted consistently rather than double-counted with next-event estimation.
- Preserve the shader's single inlined `SampleSingleLight()` call-site constraint to avoid the previous Metal compile-time explosion.
- Benchmark noise and frame cost across diffuse, glossy, small-light, and many-light fixtures before changing defaults.

Completion criteria: light and material sampling can both discover the same paths without full double-counting, PDFs are comparable and tested, and image/noise regressions show the expected tradeoff.

## Parallel Correctness And Safety

- The output-dimension guard at the start of `CSMain` is implemented and covered at `1x1`, `3x5`, and `13x7`.
- Make ray-traced object registration and `_buffersNeedRebuilding` manager-local, or explicitly enforce and reset a supported singleton. Their current static lifetime conflicts with instance-owned buffers and is fragile with multiple managers or disabled domain reload.
- Preserve and restore the application's previous `QualitySettings.vSyncCount`, `Application.targetFrameRate`, and `Time.timeScale` when entering/leaving single-frame mode, including disable/destruction cleanup.
- The fixed BVH stack-depth invariant is enforced during mesh, top-level, and shadow builds; construction fails clearly before exceeding the CPU/GPU stack size of `64`.
- Detect `MeshFilter.sharedMesh` replacement and define an explicit dirty path for runtime vertex/topology/UV changes. Validate `mesh.isReadable` with an actionable error before reading imported mesh data.
- Update dynamic scene data before CPU autofocus and bring CPU sphere/water intersection behavior into parity with the shader. Autofocus currently sees previous-frame transforms and uses the average water plane rather than procedural waves.
- Correct finite-water segment accounting as part of Priority 2 rather than adding another independent water-only attenuation path.
- Validate required `shader`/camera wiring at startup and ensure the camera presenting `RayTracingCameraRenderer.OnRenderImage()` matches the camera whose matrices and controls `GameManager` uses.

## Additional Regression Coverage

- CPU sphere/triangle/AABB, reflection, Snell/TIR, Fresnel, and absorption tests are implemented; extend them as production medium and BRDF state is added.
- Compare per-mesh, top-level, and shadow BVH results against brute-force intersections over deterministic randomized scenes, and test maximum tree depth against `BvhStackSize`.
- Add registration/unregistration and lifecycle tests, including multiple managers and domain-reload-disabled-style static-state reset cases.
- Add GPU smoke tests that dispatch final and debug variants at tiny, odd, and non-multiple-of-eight resolutions after bounds protection exists.
- Reflective sphere, refractive sphere, and water image signatures are implemented; extend low-resolution deterministic regressions to opaque/transparent shadows, stacked/nested glass, mesh Snell/TIR, mesh lights, textures, and BVH-on versus flat-loop equivalence.
- Add focused validation scenes or numeric probes for nested/interpenetrating media and water entry/exit distances before changing those systems further.

## Further Material And Medium Work

- Follow Priorities 1-6 above for medium state, absorption, refraction, shadow traversal, coherent material sampling, and MIS rather than implementing isolated object-specific fixes.
- Harden sphere and mesh glass for repeated internal reflection, concave/non-manifold/open meshes, exhausted bounce budgets inside a medium, and analytic Snell/TIR validation. Basic Snell transmission, distance absorption, bounded interior-object tests, and mesh TIR are already implemented.
- Improve wavy-top intersection with adaptive/root-finding behavior and optionally support multiple/transformed water volumes.
- Add optional bounded volumetric fog after the existing surface and medium paths are stable. Start with a homogeneous volume using density, scattering color, absorption, and optional emission; sample free-flight distance and a simple isotropic or Henyey-Greenstein phase function rather than treating fog as a transparent surface. Integrate fog with medium identity, direct-light sampling, shadows, accumulation resets, debug output, and regression fixtures. Keep the disabled path allocation- and dispatch-free, and benchmark it separately because volume scattering can add intersections and scattering events at every bounce.

## Caustics Prototype

- Prototype photon-mapped caustics as an optional, default-disabled feature. The disabled path must not allocate photon resources, dispatch caustics kernels, or add photon gathering to the default final-color shader variant.
- Begin with sphere lights, one glass-sphere transmission, diffuse receivers, and a small linear photon gather. Validate estimator energy and feature value before adding a world-space grid, glass meshes, or water.
- The isolated photon prototype, deterministic photon/image fixtures, photon-count/gather-radius energy checks, and bounded world-space photon grid are implemented. The visually sufficient 2,048-photon setting measured 15.7% overhead and is now the practical default. Emissive triangle lights, reflected/multi-event sphere transport, and closed glass-mesh targeting and boundary transport are implemented.
- See `12-caustics-prototype.md` for the proposed architecture, invalidation rules, diagnostics, testing, and acceptance criteria.

## Priority 7: Lighting And Geometry Quality

- Add imported vertex normals and interpolate them barycentrically. This is now high-value because the Stanford Dragon benchmark and direct specular highlights make flat triangle normals visibly facet smooth models.
- Make mesh-light selection hierarchical: choose an emissive mesh by total emitted power, then choose one of its triangles through a second area/power-weighted distribution. Build both distributions on the CPU when emissive geometry, transforms, materials, or emission values change, and upload compact CDF or alias-table data for constant-time GPU selection. The selected triangle must retain nonzero probability, return the complete mesh-times-triangle selection probability, and convert its area PDF to the same solid-angle measure used by BRDF PDFs and MIS. Keep sphere lights in the global emitter distribution without expanding every emissive triangle into a global entry. This avoids treating every emissive triangle as an independent top-level light, removes pressure on the `MaxImportanceLights` (`128`) cap, and reduces per-hit work for dense emissive meshes.
- Introduce hierarchical mesh-light sampling in stages: first preserve current triangle emission and one-sided behavior while replacing only selection; then integrate the resulting PDFs with existing light/BRDF MIS; finally consider texture-aware triangle weights if emissive textures are added. Add deterministic distribution tests, verify empirical selection frequencies against expected probabilities, compare rendered energy against `AllLights`, and benchmark low- and high-tessellation emitters at matched total area and power. Diagnostics should report emissive mesh count, emissive triangle count, whether hierarchical selection is active, and invalid/zero-weight emitters.
- Hierarchical mesh-light completion criteria: every eligible emissive mesh and triangle remains selectable, selection PDFs agree with the sampled distribution and MIS measure, subdividing an emitter does not materially change its brightness, dense emitters no longer consume the global importance-light limit per triangle, and many-triangle light cost does not scale linearly per shaded hit.
- Replace or redesign the global importance-light cap so every active emitter keeps nonzero selection probability. A precomputed CDF/alias table or spatial light structure should avoid the current per-hit full weight scan while preserving the shader's single `SampleSingleLight()` call-site compile constraint.
- Improve rough metal continuation sampling around the ideal reflection lobe instead of randomizing the normal with axis-aligned noise.
- Improve diffuse basis construction and add consistent material-specific BRDF/PDF handling as the material model evolves.
- Sample sphere lights by visible solid angle instead of approximate disk samples.

## Priority 8: Performance And Tooling

- Avoid rebuilding/uploading both top-level BVHs every rendered frame when object bounds are unchanged. Reuse static trees and evaluate refitting for transform-only changes before a full SAH rebuild.
- Separate mesh geometry, material, light, and texture dirtiness. A material or transform change currently rebuilds every world-space triangle, per-mesh BVH, mesh-light entry, and texture-array slice.
- Upload sphere/light data only when relevant transforms or component values change.
- Add benchmark CSV/JSON export with warmup, fixed settings, sample duration/count, median/p95, and scene/settings metadata.
- Add GPU timing when supported; Unity's CPU frame time often collapses compute work into `Rendering`, so Xcode GPU Frame Capture remains useful on macOS.
- Add focused water march/refinement and mesh-light tessellation benchmarks before optimizing those paths.
- Consider dynamic-quality presets or user-selectable priorities if users need to favor bounces/shadows over sample count or light quality.

## Priority 9: Lower-Risk Visual Improvements

- Add a physically configurable camera model. Expose focus distance plus aperture as lens radius and/or f-stop, derive aperture size consistently from focal length, and sample the aperture in camera space instead of using the hard-coded `0.005` three-axis origin jitter. Follow with configurable aperture blade count and rotation for shaped bokeh and an anamorphic ratio for elliptical bokeh. Preserve the current pinhole path when aperture is zero, reset accumulation when lens parameters change, and add deterministic focus-plane and bokeh-shape fixtures.
- Broaden mesh material texture support beyond fixed `128x128` point-filtered, mipless albedo slices. Start by preserving source or configurable resolution, adding bilinear filtering and mip/LOD support, validating color space, and defining repeat/clamp behavior. Then add roughness/metallic and tangent-space normal maps, followed by emission and opacity maps if needed. Upload vertex tangents or generate a stable tangent basis before enabling normal maps; keep geometric normals for intersection orientation and medium-boundary identity while using the mapped shading normal only for BSDF evaluation. Define accumulation and buffer dirtiness per texture/material change so replacing one material map does not rebuild unrelated geometry or BVHs.
- Add texture-specific debug modes for base color, roughness/metallic, shading normal, emission, opacity, UV coordinates, selected mip, and invalid texture/tangent data. Regression coverage should include UV interpolation, wrapping, color-space conversion, normal-map handedness, missing-map defaults, texture replacement invalidation, and stable lighting when texture resolution or filtering changes. Alpha masking or blended opacity must be a separate later step because it affects primary, shadow, refraction, selection, autofocus, and caustic-photon traversal rather than shading alone.
- Broader texture completion criteria: textured materials use documented color spaces and sampling rules, normal maps preserve geometric boundary behavior, roughness drives the same BRDF model in direct and continuation paths, texture-only edits rebuild only affected resources, and deterministic fixtures cover each supported map type.
- Add an optional firefly/outlier clamp for rare bright speckles in single-frame renders.
- Add denoiser feature buffers alongside linear HDR beauty accumulation. At minimum, output world- or view-space shading normals, diffuse albedo, and a validity/sample weight; useful later additions include depth, motion vectors, and material/object identity. Keep these buffers linear and untone-mapped, reset them with the same camera/scene state that invalidates beauty accumulation, and provide debug modes for every feature buffer. For mirrors and glass, evaluate whether features should follow near-delta reflection/transmission to the first stable diffuse surface rather than always describing the primary specular boundary; make that policy explicit and visualize feature depth/validity so denoiser artifacts can be diagnosed.
- Integrate denoising in stages. First add and regression-test feature-buffer generation without changing final color. Next add an on-demand final-render denoise path using HDR beauty plus albedo/normal guidance. Only then evaluate a real-time spatial or temporal path for interactive rendering and animated water. Photon caustics need focused validation: retain an undenoised/raw output, ensure sparse high-energy caustic structure is not interpreted as noise or smeared across receiver edges, and compare low-sample denoised results against high-sample references with caustics enabled and disabled. Consider a caustics radiance feature or conservative denoiser blend/mask if the beauty denoiser consistently suppresses focused caustic detail.
- Denoiser testing should cover feature correctness through diffuse, metal, glass, nested media, textured meshes, water, and caustic receivers; finite/non-NaN output; accumulation resets; odd resolutions; and unchanged beauty signatures while denoising is disabled. Measure edge preservation, temporal stability, residual variance, caustic peak/total energy, and execution cost at fixed low sample counts instead of judging only screenshots.
- Denoiser completion criteria: the disabled path does not change existing final-color output or allocate unnecessary resources, feature buffers are stable and inspectable, low-sample diffuse/glossy scenes improve without material-edge bleeding, and low-sample caustics retain recognizable shape and approximately stable energy relative to a high-sample raw reference.
- Tune defaults for light falloff, shadow randomness, passes, shadow quality, and noise after reference-image testing.
- Add debug legends/configurable ranges and material/debug presets only when they serve a specific diagnosis workflow.
