# Runtime Data Flow

`GameManager.cs` is the bridge between Unity objects and the compute shader renderer.

## Render Texture Allocation

On `Start()`, `GameManager` creates `_outputTexture` as a `RenderTexture` sized to the current screen dimensions with `enableRandomWrite = true`. This texture is bound as `Result` before dispatch and then blitted to the camera output by `RayTracingCameraRenderer` on the same camera referenced by `GameManager.renderTextureCamera`.

During `OnRenderImage()`, `GameManager` checks the source render target dimensions and recreates `_outputTexture` if the runtime render size changes. It also updates `renderTextureCamera.aspect` from the active output texture size so the camera projection used for ray generation matches the resized render target.

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

Sphere and light objects require a `SphereCollider`. The collider center is transformed to world space for the ray-traced sphere position, and the collider radius is scaled by the largest absolute axis of the object's lossy scale for the ray-traced sphere radius. Mesh objects require a `MeshFilter`; the shared mesh triangles are transformed to world space, sorted into a per-mesh BVH, and uploaded with mesh and BVH node metadata.

Registration caches the `Transform`, `SphereCollider`, shared `Mesh`, and either `RayMaterial` or `RayLight` references so per-frame updates do not repeatedly call `GetComponent<>()`. `MeshCollider` components are not used by the compute renderer; ray mesh primitives add them only so Unity physics can treat the rendered meshes as static collision geometry.

`UnregisterObject()` removes the object from the CPU object cache and from the matching sphere/light data list, then marks buffers for rebuilding. This prevents disabled/destroyed ray-traced objects from leaving stale entries in the GPU buffers.

## Per-Frame Render Flow

`RayTracingCameraRenderer.OnRenderImage(RenderTexture src, RenderTexture dest)` is the render entry point. It delegates to `GameManager.RenderImage()` so the enabled Unity camera used for Game view gizmos is also the camera whose transform/projection drives compute ray generation.

When `_running` is true, it:

1. Ensures `_outputTexture` matches the current source render target dimensions.
2. Updates `renderTextureCamera.aspect` to match `_outputTexture`.
3. Computes autofocus distance if `cameraAutoFocus` is enabled, ignoring ray-traced objects whose opacity is at or below `autoFocusTransparentOpacityThreshold` so focus can pass through mostly transparent glass.
4. Writes the resulting focal distance to `cameraFocalDistance`.
5. Calls `UpdateSpheres()` to refresh CPU sphere/light structs from cached Unity object references.
6. Calls `UpdateTriangles()` to refresh registered mesh triangle data only if a cached mesh transform or material value changed.
7. Finds the compute kernel `CSMain`.
8. Calls `SetShaderParameters()`.
9. Dispatches the compute shader through `UpdateTextureFromCompute()`.
10. Stops rendering again if single-frame mode is enabled.

After dispatch, it always calls:

```csharp
Graphics.Blit(_outputTexture, dest);
```

## Buffer Rebuilds And Updates

`_buffersNeedRebuilding` is set when objects register or unregister. `Update()` calls `RebuildBuffers()` when this flag is true.

`RebuildBuffers()` releases and recreates sphere/light buffers using stride `56`, matching the HLSL `Sphere` struct layout:

- `float3 position`
- `float3 color`
- `float3 emission`
- `float radius`
- `float smoothness`
- `float opacity`
- `float refraction`
- `int materialType`

`RebuildBuffers()` also releases and recreates triangle, mesh-info, and BVH-node buffers. The triangle buffer uses stride `80`, matching the HLSL `MeshTriangle` struct layout:

- `float3 vertex0`
- `float3 vertex1`
- `float3 vertex2`
- `float3 normal`
- `float3 color`
- `float smoothness`
- `float opacity`
- `float refraction`
- `int materialType`
- `int meshIndex`

The mesh-info buffer uses stride `48` and stores each mesh AABB, root BVH node index, triangle range, and mesh index. The BVH-node buffer also uses stride `48` and stores each node AABB, child indices, and leaf triangle range.

When object counts change, `RebuildBuffers()` also writes `_NumSpheres`, `_NumLights`, `_NumTriangles`, and `_NumMeshes`, including zero counts, so the shader does not read stale buffer entries after unregistering objects.

Unity requires referenced structured buffers to be bound even when their active count is zero. `RebuildBuffers()` therefore creates sphere, light, triangle, mesh-info, and BVH-node buffers with at least one dummy element, while the `_Num*` shader counts still contain the real active counts.

`UpdateSpheres()` then calls `SetData()` every rendered frame for existing sphere and light buffers so dynamic transforms/material values are reflected on the GPU.

`UpdateTriangles()` rebuilds world-space triangle data, mesh AABBs, and BVH nodes, then uploads `_Triangles`, `_Meshes`, and `_BvhNodes` only when a mesh object's transform, color, smoothness, opacity, refraction index, or material type changes.

## Scene View Preview Flow

`GameManager.OnValidate()` and `Start()` call `SyncUnitySkyboxPreview()` when `syncUnitySkyboxToRayTracedSkybox` is enabled. It creates a transient `Skybox/Panoramic` material from `skyboxTexture`, applies `_skyboxLightColor` as the skybox tint, applies `unitySkyboxExposure` and `unitySkyboxRotation`, and assigns it to `RenderSettings.skybox`. This affects Unity's Scene/Game skybox preview only; ray-traced sky sampling still uses `_SkyboxTexture` and `_SkyboxLight` in the compute shader.

`GameManager.OnDrawGizmos()`/`OnDrawGizmosSelected()` draws a ground preview for the implicit ground plane. In the editor it uses `Handles.DrawSolidRectangleWithOutline()` with depth testing so the opaque ground preview respects scene depth better than a filled `Gizmos.DrawCube()`.

`RayTracingObject.OnDrawGizmos()` draws sphere/light-sphere gizmos using the world-space collider center and scaled radius. Sphere gizmo alpha follows `RayMaterial.Opacity`; light-sphere gizmos use full opacity because `RayLight` has no opacity field.

`RayObjectPreview` can be attached to sphere/light objects to add a rasterized sphere mesh preview and, for `RayLight`, an optional Unity point-light preview. Its `MeshRenderer` is visible outside Play mode and hidden during Play mode by default, so the Game view remains compute-rendered.

## Shader Parameters

`SetShaderParameters()` sends:

- `_SkyboxTexture`
- `_CameraToWorld`
- `_CameraInverseProjection`
- `_SkyboxLight`
- `_Seed`
- `_NumberOfPasses`
- `_NumBounces`
- `_DebugRenderMode`
- `_ShadowQuality`
- `_ShadowRandomness`
- `_LightFalloffScale`
- `_FocalDistance`
- `_GroundSmoothness`
- `_Spheres`
- `_Lights`
- `_Triangles`
- `_Meshes`
- `_BvhNodes`

`_Seed` is uploaded as an integer. When `randomNoise` is enabled, C# uploads a new random seed each rendered frame. When `randomNoise` is disabled, C# uploads a fixed seed for stable deterministic sampling.

## Controls And Modes

- WASD moves `renderTextureCamera`.
- Arrow keys rotate `renderTextureCamera`.
- `T` toggles single-frame mode.
- `Space` resumes real-time rendering.
- `debugRenderMode` is exposed in the `GameManager` inspector and selects final color or one of the shader debug visualizations.

Single-frame mode renders one frame, then stops compute dispatch while the camera continues to blit the last `_outputTexture` into the Game view. It lowers the target frame rate but does not set `Time.timeScale` to zero, so Unity keeps presenting the last ray-traced render instead of appearing to fall back to an editor/Scene view. Toggling it off in the inspector, pressing `T`, or pressing `Space` resumes real-time rendering and restores real-time presentation settings.

Unity's editor toolbar Pause freezes the player loop, so runtime code cannot keep dispatching or blitting new frames while that pause is active. `Assets/Editor/GameViewPauseFocus.cs` listens for editor pause events and refocuses/repaints the Game view so the editor remains on the last presented render instead of switching to the Scene tab.

`Assets/Editor/GameViewGizmoPlayModeState.cs` turns the Game view gizmo toggle off when entering Play mode so the ray-traced view starts uncluttered. Gizmos can still be manually enabled during Play mode. When returning to Edit mode, the toggle is restored only if it was enabled before Play mode and was not manually reenabled during Play mode.
