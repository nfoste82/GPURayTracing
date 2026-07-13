# Runtime Data Flow

`GameManager.cs` is the bridge between Unity objects and the compute shader renderer.

## Render Texture Allocation

On `Start()`, `GameManager` creates `_outputTexture` as a `RenderTexture` sized to the current screen dimensions with `enableRandomWrite = true`. This texture is bound as `Result` before dispatch and then blitted to the camera output. The blit is performed by `GameManager.RenderImage()`, which is invoked from `RayTracingCameraRenderer.OnRenderImage()`. The blit therefore happens on whichever camera holds the `RayTracingCameraRenderer` component; that is normally the camera wired into `GameManager.renderTextureCamera`, but the code does not enforce that they are the same camera.

During `RenderImage()`, `GameManager` calls `EnsureOutputTextureSize(src.width, src.height)` to check the source render target dimensions and recreate `_outputTexture` and the HDR `_accumulationTexture` if the runtime render size changes. This runs every call. It also updates `renderTextureCamera.aspect` from the active output texture size so the camera projection used for ray generation matches the resized render target.

## Object Registration

`RayTracingObject.OnEnable()` calls:

```csharp
GetComponentInParent<GameManager>().RegisterObject(this);
```

If the object has `RayMeshPrimitive`, `RayTracingObject.OnEnable()` first calls `RayMeshPrimitive.EnsureMesh()` so the procedural mesh exists before registration.

`RegisterObject()` classifies the object by rendering components:

- If it has `RayMaterial` and `SphereCollider`, it becomes a ray-traced sphere in `_spheres` and `_sphereObjects`.
- If it has `RayMaterial` and `MeshFilter`, but no `SphereCollider`, it becomes a triangle mesh in `_triangles`, `_meshInfos`, `_bvhNodes`, and `_meshObjects`.
- If it has `RayLight` and `SphereCollider`, it becomes an emissive sphere light in `_lights` and `_lightObjects`.
- If it has `RayLight` and `MeshFilter`, but no `SphereCollider`, it becomes an emissive mesh light: its triangles are uploaded to `_triangles`, and each triangle also contributes an entry to `_lights` for direct-light sampling.

Sphere objects and sphere lights require a `SphereCollider`. The collider center is transformed to world space for the ray-traced sphere position, and the collider radius is scaled by the largest absolute axis of the object's lossy scale for the ray-traced sphere radius. Mesh objects and mesh lights require a `MeshFilter`; the shared mesh triangles are transformed to world space, sorted into a per-mesh BVH, and uploaded with mesh and BVH node metadata.

Registration caches the `Transform`, `SphereCollider`, shared `Mesh`, and either `RayMaterial` or `RayLight` references so per-frame updates do not repeatedly call `GetComponent<>()`. `MeshCollider` components are not used by the compute renderer; ray mesh primitives add them only so Unity physics can treat the rendered meshes as static collision geometry.

`UnregisterObject()` removes the object from the CPU object cache and from the matching sphere/light data list, then marks buffers for rebuilding. This prevents disabled/destroyed ray-traced objects from leaving stale entries in the GPU buffers.

## Per-Frame Render Flow

`RayTracingCameraRenderer.OnRenderImage(RenderTexture src, RenderTexture dest)` is the render entry point. It delegates to `GameManager.RenderImage()`. This means the Unity camera that owns the `RayTracingCameraRenderer` component (the one whose `OnRenderImage` fires and is used for Game view gizmos) is intended to also be the camera whose transform/projection drives compute ray generation. In practice that camera should be the same object assigned to `GameManager.renderTextureCamera`, since `RenderImage()` reads camera matrices from `renderTextureCamera`, but nothing in code links the two automatically.

Each render callback:

1. Ensures `_outputTexture` matches the current source render target dimensions.
2. Updates `renderTextureCamera.aspect` to match `_outputTexture`.
3. `Update()` may adjust dynamic quality before render dispatch when `enableDynamicQuality` is enabled. It tracks an exponentially averaged unscaled frame time against `dynamicQualityTargetFrameRate`, waits at least `0.75` seconds between adjustments, and changes only `numberOfPasses`, `lightSamplingStrategy`, `lightSampleCount`, `shadowQuality`, or `numBounces` within their existing inspector slider ranges. It never changes `topLevelBvhMinObjectCount` or `shadowBvhMinObjectCount`.
4. Computes autofocus distance if `cameraAutoFocus` is enabled, ignoring ray-traced objects whose opacity is at or below `autoFocusTransparentOpacityThreshold` so focus can pass through mostly transparent glass. The autofocus search starts from a `numberOfPasses`-derived near distance (`12 - min(8, numberOfPasses * 1.75)`) rather than a fixed near plane, and very close hits (under `1.0`) are remapped by an additional close-focus modifier and smoothed toward the previous focal distance over time.
5. Writes the resulting focal distance to `cameraFocalDistance`.
6. Calls `UpdateSpheres()` to refresh CPU sphere/light structs from cached Unity object references.
7. Calls `UpdateTriangles()` to refresh registered mesh triangle data only if a cached mesh transform or material value changed.
8. Calls `UpdateTopLevelBvh()` and `UpdateShadowBvh()` to refresh acceleration structures if their runtime thresholds are met.
9. Checks whether final-color frame accumulation can continue. Accumulation resets when the render size, camera matrices, focus distance, quality settings, random-noise setting, skybox texture/tint, sphere/light data, mesh object transforms/materials, or relevant object counts change. Debug render modes and `enableFrameAccumulation == false` disable accumulation.
10. Finds the compute kernel `CSMain`.
11. Calls `SetShaderParameters()`.
12. When caustics and final-color frame accumulation are enabled, clears and rebuilds an independent fixed-size photon batch and spatial grid. Stable frames advance a caustic-only sequence index without invalidating HDR accumulation; relevant scene/settings changes reset both sequences. Without accumulation, the current batch remains fixed.
13. Dispatches the compute shader through `UpdateTextureFromCompute()`.
14. Increments `AccumulatedFrameCount` when accumulation is active.
15. Marks the active `debugRenderMode` as warmed and clears the variant-warmup flag.
16. In single-frame mode, keeps dispatching at the reduced single-frame presentation rate. Final-color accumulation progressively refines an unchanged view and resets when the camera or scene changes.

An on-demand debug-variant compile also happens in single-frame mode because render dispatch remains active.

### Debug Variant Warmup Deferral

Before dispatch, `RenderImage()` checks for a switch to a `debugRenderMode` whose `DEBUG_RENDER` shader variant has not been compiled yet (tracked in `_warmedDebugModes`, compared against `_appliedDebugRenderMode`). The first variant `Dispatch` compiles synchronously and freezes the main thread, so on detection `RenderImage()` sets `_pendingVariantWarmup`, re-blits the previous `_outputTexture`, and returns without the heavy dispatch. That extra frame lets `GameManager.OnGUI()` paint a centered "Compiling shader variant" notice; the next frame runs the stalling dispatch with the notice already on screen, then marks the mode warmed. See `10-benchmarking-and-performance.md` for the full rationale and `08-shader-debugging-and-randomness.md` for the variant split.

After dispatch, it always calls:

```csharp
Graphics.Blit(_outputTexture, dest);
```

## Buffer Rebuilds And Updates

`_buffersNeedRebuilding` is set when objects register or unregister. `Update()` calls `RebuildBuffers()` when this flag is true.

`RebuildBuffers()` releases and recreates the sphere buffer using stride `56`, matching the HLSL `Sphere` struct layout:

- `float3 position`
- `float3 color`
- `float3 emission`
- `float radius`
- `float smoothness`
- `float opacity`
- `float refraction`
- `int materialType`

The separate light buffer uses stride `72`. Its `Light` layout stores position, emission, two triangle edges, radius, area, normal, and type so the same buffer can represent sphere lights and emissive mesh triangles.

`RebuildBuffers()` also releases and recreates triangle, mesh-info, per-mesh BVH-node, and top-level BVH-node buffers. The triangle buffer uses stride `124`, matching the HLSL `MeshTriangle` struct layout:

- `float3 vertex0`
- `float3 vertex1`
- `float3 vertex2`
- `float3 normal`
- `float3 color`
- `float smoothness`
- `float2 uv0`
- `float2 uv1`
- `float2 uv2`
- `float opacity`
- `float3 emission`
- `float refraction`
- `int materialType`
- `int meshIndex`
- `int textureIndex`
- `int padding0`

The mesh-info buffer uses stride `48` and stores each mesh AABB, root BVH node index, triangle range, and mesh index. The per-mesh BVH-node buffer also uses stride `48` and stores each node AABB, child indices, and leaf triangle range. The top-level BVH-node buffer uses stride `48` and stores AABBs over sphere, light, and mesh objects, plus child indices or object type/index metadata. The shadow BVH-node buffer uses the same stride/layout but only includes shadow blockers: regular spheres and meshes, not light spheres.

When object counts change, `RebuildBuffers()` also writes `_NumSpheres`, `_NumLights`, `_NumTriangles`, `_NumMeshes`, `_NumTopLevelBvhNodes`, and `_NumShadowBvhNodes`, including zero counts, so the shader does not read stale buffer entries after unregistering objects.

Unity requires referenced structured buffers to be bound even when their active count is zero. `RebuildBuffers()` therefore creates sphere, light, triangle, mesh-info, per-mesh BVH-node, top-level BVH-node, and shadow BVH-node buffers with at least one dummy element, while the `_Num*` shader counts still contain the real active counts.

`UpdateSpheres()` then calls `SetData()` every rendered frame for existing sphere and light buffers so dynamic transforms/material values are reflected on the GPU.

`UpdateTriangles()` rebuilds world-space triangle data, mesh UVs, mesh AABBs, per-mesh BVH nodes, and the active mesh albedo `Texture2DArray`, then uploads `_Triangles`, `_Meshes`, and `_BvhNodes` only when a mesh object's transform, color, albedo texture, smoothness, opacity, refraction index, or material type changes. Mesh albedo textures are copied into fixed `128x128` array slices and sampled by `textureIndex`; meshes without an albedo texture use only `RayMaterial.Color`. `UpdateTopLevelBvh()` rebuilds and uploads the top-level object BVH every rendered frame after sphere/light and mesh updates, so dynamic objects keep correct top-level bounds. `UpdateShadowBvh()` does the same for the shadow-only blocker BVH.

`UpdateSpheres()` and `UpdateMeshChangeCache()` also recompute whether any shadow-casting blocker (regular sphere or mesh triangle) has opacity `< 1`. `SetShaderParameters()` uploads the combined result as the `_HasTransparentShadowBlockers` int so the shader can take its cheaper pure-occlusion shadow path when no transparent blockers exist (see `06-shader-intersections-and-bvh.md`).

`topLevelBvhMinObjectCount` and `shadowBvhMinObjectCount` are runtime thresholds. If the relevant object count is below the threshold, the matching BVH list is left empty, `_NumTopLevelBvhNodes` or `_NumShadowBvhNodes` is uploaded as `0`, and the compute shader uses lower-overhead flat object loops. Set a threshold to `0` to force that BVH on. Set it above the scene's object/blocker count, such as `1024`, to force flat loops. The benchmark overlay reports whether each BVH is active, object counts, node counts, and thresholds.

Profiling in the current scenes showed that the shadow-blocker BVH can improve `Benchmark_ShadowBlockers`, while the general top-level BVH has little effect there because that benchmark is dominated by shadow rays rather than first-hit object lookup. Use `Benchmark_ManySpheres` or `Benchmark_ManyMeshes` to evaluate `topLevelBvhMinObjectCount`; use `Benchmark_ShadowBlockers` to evaluate `shadowBvhMinObjectCount`.

## Benchmarking Flow

Benchmark scene generation and overlay behavior are documented in `10-benchmarking-and-performance.md` so runtime orchestration tasks do not need to load benchmark methodology by default.

## Scene View Preview Flow

`GameManager.OnValidate()` and `Start()` call `SyncUnitySkyboxPreview()` when `syncUnitySkyboxToRayTracedSkybox` is enabled. It creates a transient `Skybox/Panoramic` material from `skyboxTexture`, applies `_skyboxLightColor` as the skybox tint, applies `unitySkyboxExposure` and `unitySkyboxRotation`, and assigns it to `RenderSettings.skybox`. This affects Unity's Scene/Game skybox preview only; ray-traced sky sampling still uses `_SkyboxTexture` and `_SkyboxLight` in the compute shader.

`GameManager.OnDrawGizmos()`/`OnDrawGizmosSelected()` draws a ground preview for the implicit ground plane. In the editor it uses `Handles.DrawSolidRectangleWithOutline()` with depth testing so the opaque ground preview respects scene depth better than a filled `Gizmos.DrawCube()`.

`RayTracingObject.OnDrawGizmos()` draws sphere/light-sphere gizmos using the world-space collider center and scaled radius. Sphere gizmo alpha follows `RayMaterial.Opacity`; light-sphere gizmos use full opacity because `RayLight` has no opacity field.

`RayObjectPreview` can be attached to sphere/light objects to add a rasterized sphere mesh preview and, for `RayLight`, an optional Unity point-light preview. Its `MeshRenderer` is visible outside Play mode and hidden during Play mode by default, so the Game view remains compute-rendered.

## Shader Parameters

`SetShaderParameters()` sends:

- `_SkyboxTexture`
- `_MeshAlbedoTextures`
- `_CameraToWorld`
- `_CameraInverseProjection`
- `_SkyboxLight`
- `_Seed`
- `_SampleOffset`
- `_NumberOfPasses`
- `_NumBounces`
- `_DebugRenderMode`
- `_UseFrameAccumulation`
- `_AccumulatedFrameCount`
- `_MaxLightSamples`
- `_LightSamplingStrategy`
- `_LightSampleCount`
- `_ShadowQuality`
- `_ShadowRandomness`
- `_LightFalloffScale`
- `_FocalDistance`
- `_GroundSmoothness`
- `_Exposure`
- `_WaterEnabled`, `_WaterCenter`, `_WaterSize`, `_WaterDepth`, `_WaterColor`, `_WaterSmoothness`, `_WaterOpacity`, `_WaterAbsorptionStrength`, `_WaterRefraction`, `_WaterWaveAmplitude`, `_WaterWaveScale`, `_WaterWaveSpeed`, `_WaterTime`, `_WaterMarchSteps`, `_WaterRefinementSteps`. These are sourced from the registered `Water` component; its transform supplies center, footprint, and depth.
- `_Spheres`
- `_Lights`
- `_Triangles`
- `_Meshes`
- `_BvhNodes`
- `_TopLevelBvhNodes`
- `_ShadowBvhNodes`

`_Seed` is uploaded as an integer. When `randomNoise` is enabled, C# uploads a new random seed (`Random.Range(1, int.MaxValue)`) each rendered frame. When `randomNoise` is disabled, C# uploads the fixed literal value `1` every frame for stable deterministic sampling.

When frame accumulation is active, `_SampleOffset` advances by `AccumulatedFrameCount * numberOfPasses` so deterministic sampling still generates new samples across accumulated frames. `_AccumulatedFrameCount` tells the shader how many previous HDR final-color frames are stored in `AccumulationResult`. Accumulation is applied before exposure/tone mapping, and debug render modes are not accumulated.

Alongside `_DebugRenderMode`, `SetShaderParameters()` toggles the `DEBUG_RENDER` shader keyword: `EnableKeyword("DEBUG_RENDER")` when `debugRenderMode != FinalColor`, otherwise `DisableKeyword("DEBUG_RENDER")`. This selects the shader variant that includes or excludes the debug render path; see `08-shader-debugging-and-randomness.md` and `10-benchmarking-and-performance.md`.

`SetShaderParameters()` also logs a one-time warning when `lightSamplingStrategy == ImportanceSampled` and the active light count exceeds `MaxImportanceLights` (`128`), since lights beyond that count are dropped from importance weighting in the shader. The C# `MaxImportanceLights` constant must stay in sync with the shader's `MaxImportanceLights`.

## Dynamic Quality

`GameManager.enableDynamicQuality` is an optional CPU-side controller. It does not add shader variants or shader branches. Each `Update()` call folds `Time.unscaledDeltaTime` into `_dynamicQualityAverageFrameMs`; after the cooldown interval, it compares that average to `1000 / dynamicQualityTargetFrameRate`. It uses asymmetric thresholds: `dynamicQualityTolerance` controls how far over budget frame time can go before reducing quality, while `dynamicQualityIncreaseHeadroom` controls how far under budget frame time must be before increasing quality.

When frame time is too high, dynamic quality reduces settings in this order: `numberOfPasses`, then light sampling, then `shadowQuality`, then `numBounces`. If the frame is far over budget, `numberOfPasses` can drop by more than one step based on the measured cost ratio. The light-sampling step switches `AllLights` to `ImportanceSampled` when there are multiple active lights, initializes `lightSampleCount` to roughly one tenth of the active light count (respecting `maxLightSamples` and the `1..64` slider range), then reduces `lightSampleCount` toward `1` if more performance is needed. Existing `UniformRandom` scenes keep their strategy and reduce `lightSampleCount` directly.

When frame time has enough headroom, dynamic quality increases settings in this order: `numberOfPasses`, then light sample count / all-lights restoration, then `shadowQuality`, then `numBounces`. The existing `[Range]` slider limits are used as bounds: passes `1..32`, light sample count `1..64`, shadow quality `0..5`, and bounces `1..16`.

Dynamic quality resets frame accumulation whenever it changes a setting. Adjustments are skipped in single-frame mode because its intentional 10 FPS presentation cap is not a useful measure of render performance.

## Controls And Modes

- WASD moves `renderTextureCamera`.
- Arrow keys rotate `renderTextureCamera`.
- `T` toggles single-frame mode.
- `Space` resumes real-time rendering.
- `debugRenderMode` is exposed in the `GameManager` inspector and selects final color or one of the shader debug visualizations.
- `enableDynamicQuality` and `dynamicQualityTargetFrameRate` are exposed in the inspector for optional adaptive quality scaling.

Single-frame mode is exposed in the inspector through the serialized public field `_singleFrame` (under the "Render single frame" header). It freezes simulation time but keeps dispatching at a reduced presentation rate, allowing final-color frame accumulation to progressively refine the view. Camera controls use unscaled delta time, and camera or manually edited scene-object changes reset accumulation and immediately begin refining the updated view. `EnableSingleFrameSettings()` sets `Application.targetFrameRate = 10`, disables vSync, and sets `Time.timeScale = 0`; toggling it off in the inspector, pressing `T`, or pressing `Space` resumes real-time rendering and currently restores hard-coded real-time settings (`targetFrameRate = 60`, `vSyncCount = 2`, and `timeScale = 1`). Preserving and restoring the caller's previous global settings is recommended in `09-roadmap-and-improvements.md`.

Unity's editor toolbar Pause freezes the player loop, so runtime code cannot keep dispatching or blitting new frames while that pause is active. `Assets/Editor/GameViewPauseFocus.cs` listens for editor pause events and refocuses/repaints the Game view so the editor remains on the last presented render instead of switching to the Scene tab.

`Assets/Editor/GameViewGizmoPlayModeState.cs` turns the Game view gizmo toggle off when entering Play mode so the ray-traced view starts uncluttered. Gizmos can still be manually enabled during Play mode. When returning to Edit mode, the toggle is restored only if it was enabled before Play mode and was not manually reenabled during Play mode.
