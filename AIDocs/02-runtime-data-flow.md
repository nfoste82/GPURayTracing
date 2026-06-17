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

`RegisterObject()` classifies the object by components:

- If it has `RayMaterial`, it becomes a ray-traced sphere in `_spheres` and `_sphereObjects`.
- Otherwise, it is expected to have `RayLight` and becomes an emissive sphere light in `_lights` and `_lightObjects`.

Each registered object requires a `SphereCollider`. The collider radius is used as the ray-traced sphere radius.

Registration caches the `Transform`, `SphereCollider`, and either `RayMaterial` or `RayLight` references so per-frame sphere updates do not repeatedly call `GetComponent<>()`.

`UnregisterObject()` removes the object from the CPU object cache and from the matching sphere/light data list, then marks buffers for rebuilding. This prevents disabled/destroyed ray-traced objects from leaving stale entries in the GPU buffers.

## Per-Frame Render Flow

`OnRenderImage(RenderTexture src, RenderTexture dest)` is the render entry point.

When `_running` is true, it:

1. Ensures `_outputTexture` matches the current source render target dimensions.
2. Updates `renderTextureCamera.aspect` to match `_outputTexture`.
3. Computes autofocus distance if `cameraAutoFocus` is enabled.
4. Writes the resulting focal distance to `cameraFocalDistance`.
5. Calls `UpdateSpheres()` to refresh CPU sphere/light structs from cached Unity object references.
6. Finds the compute kernel `CSMain`.
7. Calls `SetShaderParameters()`.
8. Dispatches the compute shader through `UpdateTextureFromCompute()`.
9. Stops rendering again if single-frame mode is enabled.

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

When object counts change, `RebuildBuffers()` also writes `_NumSpheres` and `_NumLights`, including zero counts, so the shader does not read stale buffer entries after unregistering objects.

`UpdateSpheres()` then calls `SetData()` every rendered frame for existing sphere and light buffers so dynamic transforms/material values are reflected on the GPU.

## Shader Parameters

`SetShaderParameters()` sends:

- `_SkyboxTexture`
- `_CheckerboardTexture`
- `_CameraToWorld`
- `_CameraInverseProjection`
- `_AmbientLight`
- `_SkyboxLight`
- `_Seed`
- `_NumberOfPasses`
- `_NumBounces`
- `_DebugRenderMode`
- `_ShadowQuality`
- `_ShadowRandomness`
- `_FocalDistance`
- `_GroundSmoothness`
- `_Spheres`
- `_Lights`

`_Seed` is uploaded as an integer. When `randomNoise` is enabled, C# uploads a new random seed each rendered frame. When `randomNoise` is disabled, C# uploads a fixed seed for stable deterministic sampling.

`_AmbientLight` is declared in the compute shader but is not currently used by the renderer.

## Controls And Modes

- WASD moves `renderTextureCamera`.
- Arrow keys rotate `renderTextureCamera`.
- `T` toggles single-frame mode.
- `Space` resumes real-time rendering.
- `debugRenderMode` is exposed in the `GameManager` inspector and selects final color or one of the shader debug visualizations.

Single-frame mode lowers target framerate and freezes `Time.timeScale`. Real-time mode restores target framerate and `Time.timeScale`.
