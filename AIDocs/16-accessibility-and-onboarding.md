# Accessibility And Onboarding Roadmap

This document records the remaining ideas for lowering the barrier to entry for public users of the Unity GPU ray tracer. It is a product/onboarding roadmap, not a renderer-feature roadmap.

## Implemented Entry Points

The following work is already in place:

- `Tools > Ray Tracing > Generate Getting Started Scene` generates `Assets/Scenes/Generated/GettingStarted.unity`.
- The Getting Started scene uses `Assets/Prefabs/Material_Ball_Room.prefab` for its floor, walls, and lights, then presents diffuse, metal, and glass spheres for evaluation.
- The Getting Started scene has a scene-local help overlay for movement, focus, refinement, diagnostics, and the help toggle.
- `Window > Ray Tracing > Quick Controls` now renders the same collapsible `GameManagerEditor` categories as the GameManager inspector, followed by scene-specific controls.
- The README provides the Unity version, a first-render workflow, runtime controls, compute-shader requirements, and recommended scenes.
- `SceneSettings` controls renderer defaults including firefly clamping, subpixel jitter scale, and spatial denoising configuration.

## Next Priorities

### 1. Add Setup Diagnostics

Replace ambiguous black output, null references, and long stalls with a direct explanation of what is wrong and how to proceed.

Validate before the first renderer dispatch:

- `SystemInfo.supportsComputeShaders`.
- The active graphics device is not Null.
- `GameManager.shader` is assigned and has required kernels.
- A render camera is assigned.
- That camera contains `RayTracingCameraRenderer`.
- `RayTracingCameraRenderer.GameManager` points to the same GameManager.
- Required random-write render texture formats are available.
- Required manager components are present.

Present errors both in the Console and Game view. Extend `Tools > Ray Tracing > Precompile Compute Shader` into a validation-first operation so it explains an unsupported setup before attempting a dispatch.

Likely files:

- `Assets/Scripts/GameManager.cs`
- `Assets/Scripts/RayTracingCameraRenderer.cs`
- `Assets/Editor/GameManagerEditor.cs`
- `Assets/Editor/RayTracingShaderPrecompiler.cs`

### 2. Add Renderer Quality Presets

Users should not need to understand the interaction between internal resolution, passes, bounces, denoising, accumulation, caustics, and fog before seeing a useful result.

Add project-specific presets to Quick Controls:

- Fast: lower internal resolution, one pass, modest bounces, expensive effects disabled.
- Balanced: sensible interactive defaults with accumulation and denoising.
- Quality: higher resolution and bounces for static evaluation.
- Custom: selected after manual edits diverge from a preset.

The preset code should alter the relevant existing `GameManager` fields and reset accumulation. It should not modify Unity's global Quality Settings.

Likely files:

- `Assets/Scripts/GameManager.cs`
- `Assets/Editor/RayTracingQuickControlsWindow.cs`
- New small preset type if a reusable representation is needed.

### 3. Build A Curated Scene Gallery

The generated scene directory contains both showcase scenes and stress fixtures. A gallery should make that distinction explicit.

Add an editor window with:

- Scene thumbnail.
- Short description.
- Expected cost: low, medium, high, or stress test.
- Special requirements, such as network access for the Khronos browser.
- Open Scene and Open And Play actions.

Implemented: `Window > Ray Tracing > Scene Gallery` is a dockable editor tab that opens beside the Inspector, matching Quick Controls. It provides curated showcase and stress sections, Unity scene-preview/icon thumbnails, descriptions, expected cost, special requirements, and Open Scene / Open And Play actions. Captured render thumbnails remain a future refinement.

Suggested ordering:

1. Getting Started
2. Glass
3. Cornell Box
4. Teapot Materials
5. Water
6. Volumetric Fog
7. Terrain
8. Benchmarks and stress fixtures in a separate section

Use `RayTracingSceneCapture` to create repeatable thumbnails rather than hand-maintaining editor screenshots.

Likely files:

- New `Assets/Editor/RayTracingSceneGalleryWindow.cs`
- `Assets/Editor/RayTracingSceneCapture.cs`
- `README.md`

### 4. Make Sample Images Reproducible

Map every public README image or video to the source scene and relevant renderer settings. This lets users distinguish a real-time demo from an offline/high-sample capture.

For each showcase artifact, record:

- Source scene path.
- Unity version and graphics backend.
- Render resolution.
- Passes, bounces, accumulation, and denoising state.
- Caustics, fog, and water state.
- Whether the result is interactive or offline.

The README scene table can link directly to those records.

### 5. Improve Content-Creation Guidance

Creating an object through `GameObject > Ray Tracing` can currently create an object outside a GameManager hierarchy, in which case it will not register for rendering.

Improve this flow by either:

- Parenting newly created ray-traced objects below the active scene's GameManager when no suitable parent is selected.
- Displaying an edit-time warning with a one-click reparent action when a `PathTracingObject` has no parent GameManager.

Also surface import prerequisites, especially the requirement that ray-traced mesh assets are CPU-readable.

Likely files:

- `Assets/Editor/RaySceneObjectMenu.cs`
- `Assets/Editor/RayMeshPrimitiveMenu.cs`
- `Assets/Scripts/PathTracingObject.cs`
- `Assets/Editor/GltfRayTracingImporter.cs`

### 6. Add Structural Scene Tests

The recommended scenes should be verified as serialized assets, rather than relying on Unity to repair missing components when opening them.

For Getting Started and every scene presented in the public gallery, test:

- An enabled GameManager exists.
- Its compute shader is assigned.
- Its CameraManager and render camera are configured.
- The camera has a RayTracingCameraRenderer referencing the same GameManager.
- Required `GameManager` dependencies are present.
- No missing scripts exist.
- The scene has appropriate ray-traced content and lighting.

Use a separate GPU smoke test at small resolution when compute support is available. Keep structural tests CPU-only so they are suitable for editor/CI environments without a GPU.

Likely files:

- New test under `Assets/Tests/EditMode/`
- `AIDocs/11-regression-testing.md`

### 7. Validate Clean-Clone And Platform Claims

The README currently states macOS as the tested platform. Make claims based on repeatable validation.

Add automation for:

- Unity import and script compilation.
- EditMode CPU tests.
- Scene-structure tests.
- GPU smoke tests on a macOS/Metal runner.
- Windows/Direct3D validation when available.

Continue to distinguish CPU/headless checks from GPU renderer checks; GPU tests cannot run under `-nographics`.

Likely files:

- New `.github/workflows/` configuration
- `AIDocs/11-regression-testing.md`
- `README.md`

## Supporting Maintenance

- Keep README controls synchronized with actual input bindings. `Space` currently toggles paused refinement, and `H` is only meaningful in Getting Started.
- Keep `AIDocs` limitation and benchmarking material current as renderer capabilities evolve; stale documentation makes troubleshooting harder for contributors.
- When adding a new showcase scene, classify it as a demo, feature fixture, or stress test and update the future gallery metadata at the same time.
