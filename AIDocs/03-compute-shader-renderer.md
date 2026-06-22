# Compute Shader Renderer

The renderer lives in `Assets/Scripts/RayTracingCompute.compute`. It has one kernel, `CSMain`, using `[numthreads(8,8,1)]`.

## GPU Inputs

Important shader globals:

- `Result`: writable output texture.
- `_CameraToWorld`: camera transform matrix.
- `_CameraInverseProjection`: inverse projection matrix for camera ray generation.
- `_SkyboxTexture`: sampled when rays miss all geometry. Sampling in `GetSkyboxColor()` uses a non-standard equirectangular mapping with negated axes (`theta = acos(dir.y) / -PI`, `phi = atan2(dir.x, -dir.z) / -PI * 0.5`), so skybox orientation/handedness is not obvious; expect to flip or rotate textures when swapping them in.
- `_SkyboxLight`: skybox lighting multiplier.
- `_NumberOfPasses`: per-frame samples per pixel.
- `_NumBounces`: maximum bounces for `TracePath()`.
- `_DebugRenderMode`: selects final path-traced color or a debug visualization.
- `_ShadowQuality`: soft-shadow sample budget control. Bounce-0 direct lighting takes `max(1, _ShadowQuality + 1)` stochastic area-light samples per light.
- `_ShadowRandomness`: area-light sampling radius multiplier for soft shadow samples.
- `_LightFalloffScale`: distance falloff scale for direct light. Higher values make light intensity decrease faster with distance.
- `_FocalDistance`: depth-of-field focal distance.
- `_GroundSmoothness`: smoothness for the implicit ground plane.
- `_Exposure`: master brightness multiplier applied before tone mapping. Acts like a camera exposure dial.
- `_LightSamplingStrategy`: selects how `GetLightHittingPoint()` samples scene lights (`0` = all lights, `1` = uniform random pick, `2` = importance-sampled pick). See `07-shader-lighting-and-materials.md`.
- `_LightSampleCount`: for the random/importance strategies, how many lights each shading point draws per hit. Ignored by the all-lights strategy.
- `_MaxLightSamples`: diagnostic cap on how many lights any strategy considers. `0` means no cap (use the real light count); a positive value clamps the considered light count to confirm the per-hit light loop is the bottleneck.
- `_Seed`: integer seed used to initialize per-pixel/per-pass shader RNG state.
- `_NumSpheres`, `_NumLights`, `_NumTriangles`, `_NumMeshes`: active buffer counts.
- `_NumTopLevelBvhNodes`: active top-level object BVH node count; `0` means first-hit traversal uses flat object loops.
- `_NumShadowBvhNodes`: active shadow-only BVH node count; `0` means shadow traversal uses flat blocker loops.
- `_Spheres`, `_Lights`: structured buffers of `Sphere` data.
- `_Triangles`: structured buffer of `MeshTriangle` data.
- `_Meshes`: structured buffer of per-mesh AABBs, triangle ranges, root BVH node indices, and mesh indices.
- `_BvhNodes`: structured buffer of per-mesh BVH nodes.
- `_TopLevelBvhNodes`: structured buffer of top-level BVH nodes over sphere, light, and mesh objects.
- `_ShadowBvhNodes`: structured buffer of top-level BVH nodes over shadow blockers only: regular spheres and mesh objects.

## Data Structures

`Sphere` is used for both renderable spheres and emissive lights:

```hlsl
struct Sphere
{
    float3 position;
    float3 color;
    float3 emission;
    float radius;
    float smoothness;
    float opacity;
    float refraction;
    int materialType;
};
```

`Ray` contains only origin and direction.

Triangle meshes are uploaded as world-space triangles:

```hlsl
struct MeshTriangle
{
    float3 vertex0;
    float3 vertex1;
    float3 vertex2;
    float3 normal;
    float3 color;
    float smoothness;
    float opacity;
    float refraction;
    int materialType;
    int meshIndex;
};
```

`meshIndex` identifies which uploaded triangles belong to the same mesh object. It is used by approximate closed-mesh refraction to find the exit face.

## Material Type Constants

The shader defines four material type constants:

- `MaterialDiffuse = 0`
- `MaterialMetal = 1`
- `MaterialGlass = 2`
- `MaterialEmissive = 3`

`RayMaterial` only exposes `Diffuse`, `Metal`, and `Glass` (0-2). The `MaterialEmissive = 3` value is assigned by `GameManager.RegisterObject()` to emissive light spheres (it is not selectable in `RayMaterial`). The shader does not branch on `MaterialEmissive` in scatter logic; lights are detected by nonzero emission via `DidHitLight()` instead.

Triangle meshes also upload `MeshInfo` and `BvhNode` data. Each mesh has an object-level AABB in `_Meshes`, and its triangles are arranged into a binary BVH whose leaf nodes contain small contiguous triangle ranges in `_Triangles`.

The scene also uploads a top-level BVH over ray-traced spheres, emissive light spheres, and registered mesh AABBs. First-hit traversal uses this BVH to skip whole objects before testing sphere intersections or entering a mesh's per-mesh BVH. Shadow traversal uses a separate shadow-only BVH over regular spheres and mesh AABBs, excluding light spheres because lights are not shadow blockers.

`RayHit` stores hit position, object position/radius, normal, emission, color, distance, smoothness, opacity, transparent travel distance, refraction index, material type, and mesh index.

## Ray Generation

`CreateCameraRay()` constructs a world-space ray by:

1. Transforming camera origin through `_CameraToWorld`.
2. Transforming clip-space UV through `_CameraInverseProjection`.
3. Transforming the direction through `_CameraToWorld`.
4. Normalizing the result.

`CSMain` maps each pixel to `[-1, 1]` UV space with subpixel jitter from `rand()`.

## Tone Mapping And Exposure

After all passes are averaged, `CSMain` applies exposure and tone mapping **only when `_DebugRenderMode == DebugFinalColor`**, so debug visualizations are written with their raw diagnostic values. The final color is computed as `ACESFilmicToneMap(color * _Exposure)`, where `ACESFilmicToneMap()` is the Narkowicz 2015 ACES filmic approximation. This maps open-ended HDR radiance into `[0, 1]` so bright values roll off smoothly instead of clipping hard to white. `_Exposure` comes from `GameManager.exposure`.

## Depth Of Field

For each pass, `CSMain` computes a focal point:

```hlsl
float3 focalPoint = ray.origin + ray.direction * _FocalDistance;
```

It jitters the ray origin by a small fixed amount and re-aims the ray at the focal point. This approximates lens aperture blur. The jitter magnitude is a hard-coded `0.005` world-space offset applied independently to each ray-origin axis; there is no configurable aperture/f-stop parameter.

## Core Path Tracing Loop

`TracePath()` is the main iterative renderer.

It maintains:

- `radiance`: accumulated light returned to the camera.
- `throughput`: accumulated material/tint/energy carried by the current path.
- `albedo`: surface color from `hit.color`.
- `emission`: emitted light from `hit.emission`.

Per bounce:

1. Trace the ray with `GetNearestIntersection()`.
2. If it hits sky, add `throughput * skyColor` and stop.
3. If it hits a light, add `throughput * emission` and stop.
4. For non-diffuse materials, randomize the normal based on surface smoothness.
5. Sample direct light if the path throughput is above `MinDirectLightThroughput`. Bounce 0 uses multiple stochastic soft-shadow samples; later bounces use one light sample.
6. Add direct contribution: `throughput * albedo * directLight * hit.opacity`.
7. Create the next ray using the hit material type.
8. Update `throughput` with the scatter attenuation.
9. Stop early when throughput is effectively black.
10. Starting after the first few bounces, apply Russian roulette termination and scale surviving throughput by survival probability.

Material scattering currently supports:

- `Diffuse`: uses direct lighting and cosine-weighted hemisphere scattering on later bounces, attenuated by albedo. On bounce 0, smoothness blends the continuation ray between diffuse scattering and reflection, which allows the implicit ground plane's `_GroundSmoothness` to affect visible reflections.
- `Metal`: reflects around the surface normal, with smoothness controlling rough reflection direction randomization, and attenuates by albedo.
- `Glass`: uses Schlick Fresnel reflectance to weight the existing approximate sphere refraction path for spheres. For mesh triangles, it uses an approximate closed-mesh entry/exit path that refracts into the mesh, intersects the nearest exit triangle with the same `meshIndex`, refracts back out, and continues from the exit point.

Note: the glass/refraction path is selected by `IsGlassMaterial(hit)`, which returns true when `materialType == Glass` **or** when `hit.opacity < 1.0`. A `Diffuse` or `Metal` object with opacity below `1` therefore takes the glass transmission/Fresnel path regardless of its declared material type.

For intersection, BVH, lighting, refraction, debugging, and randomness details, see the focused shader docs listed in `00-index.md`.
