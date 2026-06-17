# Runtime Data Flow

`GameManager.cs` is the bridge between Unity objects and the compute shader renderer.

## Render Texture Allocation

On `Start()`, `GameManager` creates `_outputTexture` as a `RenderTexture` sized to the current screen dimensions with `enableRandomWrite = true`. This texture is bound as `Result` before dispatch and then blitted to the camera output.

During `OnRenderImage()`, `GameManager` checks the source render target dimensions and recreates `_outputTexture` if the runtime render size changes. It also updates `renderTextureCamera.aspect` from the active output texture size so the camera projection used for ray generation matches the resized render target.

## Object Registration

`RayTracingObject.OnEnable()` calls:

```csharp
GetComponentInParent<GameManager>().RegisterObject(this);
```

If the object has `RayMeshPrimitive`, `RayTracingObject.OnEnable()` first calls `RayMeshPrimitive.EnsureMesh()` so the procedural mesh exists before registration.

`RegisterObject()` classifies the object by components:

- If it has `RayMaterial` and `SphereCollider`, it becomes a ray-traced sphere in `_spheres` and `_sphereObjects`.
- If it has `RayMaterial` and `MeshFilter`, but no `SphereCollider`, it becomes a triangle mesh in `_triangles` and `_meshObjects`.
- If it has `RayLight` and `SphereCollider`, it becomes an emissive sphere light in `_lights` and `_lightObjects`.

Sphere and light objects require a `SphereCollider`. The collider radius is used as the ray-traced sphere radius. Mesh objects require a `MeshFilter`; the shared mesh triangles are transformed to world space and uploaded directly.

Registration caches the `Transform`, `SphereCollider`, shared `Mesh`, and either `RayMaterial` or `RayLight` references so per-frame updates do not repeatedly call `GetComponent<>()`.

`UnregisterObject()` removes the object from the CPU object cache and from the matching sphere/light data list, then marks buffers for rebuilding. This prevents disabled/destroyed ray-traced objects from leaving stale entries in the GPU buffers.

## Per-Frame Render Flow

`OnRenderImage(RenderTexture src, RenderTexture dest)` is the render entry point.

When `_running` is true, it:

1. Ensures `_outputTexture` matches the current source render target dimensions.
2. Updates `renderTextureCamera.aspect` to match `_outputTexture`.
3. Computes autofocus distance if `cameraAutoFocus` is enabled.
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

`RebuildBuffers()` also releases and recreates a triangle buffer using stride `80`, matching the HLSL `MeshTriangle` struct layout:

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

When object counts change, `RebuildBuffers()` also writes `_NumSpheres`, `_NumLights`, and `_NumTriangles`, including zero counts, so the shader does not read stale buffer entries after unregistering objects.

Unity requires referenced structured buffers to be bound even when their active count is zero. `RebuildBuffers()` therefore creates sphere, light, and triangle buffers with at least one dummy element, while the `_Num*` shader counts still contain the real active counts.

`UpdateSpheres()` then calls `SetData()` every rendered frame for existing sphere and light buffers so dynamic transforms/material values are reflected on the GPU.

`UpdateTriangles()` rebuilds world-space triangle data and uploads `_Triangles` only when a mesh object's transform, color, smoothness, opacity, refraction index, or material type changes.

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

`_Seed` is uploaded as an integer. When `randomNoise` is enabled, C# uploads a new random seed each rendered frame. When `randomNoise` is disabled, C# uploads a fixed seed for stable deterministic sampling.

## Controls And Modes

- WASD moves `renderTextureCamera`.
- Arrow keys rotate `renderTextureCamera`.
- `T` toggles single-frame mode.
- `Space` resumes real-time rendering.
- `debugRenderMode` is exposed in the `GameManager` inspector and selects final color or one of the shader debug visualizations.

Single-frame mode renders one frame, then freezes rendering and `Time.timeScale`. Toggling it off in the inspector, pressing `T`, or pressing `Space` resumes real-time rendering and restores `Time.timeScale`.
