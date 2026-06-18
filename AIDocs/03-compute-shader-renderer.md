# Compute Shader Renderer

The renderer lives in `Assets/Scripts/RayTracingCompute.compute`. It has one kernel, `CSMain`, using `[numthreads(8,8,1)]`.

## GPU Inputs

Important shader globals:

- `Result`: writable output texture.
- `_CameraToWorld`: camera transform matrix.
- `_CameraInverseProjection`: inverse projection matrix for camera ray generation.
- `_SkyboxTexture`: sampled when rays miss all geometry.
- `_SkyboxLight`: skybox lighting multiplier.
- `_NumberOfPasses`: per-frame samples per pixel.
- `_NumBounces`: maximum bounces for `TracePath()`.
- `_DebugRenderMode`: selects final path-traced color or a debug visualization.
- `_ShadowQuality`: soft-shadow sample budget control. Bounce-0 direct lighting takes `max(1, _ShadowQuality + 1)` stochastic area-light samples per light.
- `_ShadowRandomness`: area-light sampling radius multiplier for soft shadow samples.
- `_LightFalloffScale`: distance falloff scale for direct light. Higher values make light intensity decrease faster with distance.
- `_FocalDistance`: depth-of-field focal distance.
- `_GroundSmoothness`: smoothness for the implicit ground plane.
- `_Seed`: integer seed used to initialize per-pixel/per-pass shader RNG state.
- `_NumSpheres`, `_NumLights`, `_NumTriangles`, `_NumMeshes`: active buffer counts.
- `_Spheres`, `_Lights`: structured buffers of `Sphere` data.
- `_Triangles`: structured buffer of `MeshTriangle` data.
- `_Meshes`: structured buffer of per-mesh AABBs, triangle ranges, root BVH node indices, and mesh indices.
- `_BvhNodes`: structured buffer of per-mesh BVH nodes.

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

Triangle meshes also upload `MeshInfo` and `BvhNode` data. Each mesh has an object-level AABB in `_Meshes`, and its triangles are arranged into a binary BVH whose leaf nodes contain small contiguous triangle ranges in `_Triangles`.

`RayHit` stores hit position, object position/radius, normal, emission, color, distance, smoothness, opacity, transparent travel distance, refraction index, material type, and mesh index.

## Ray Generation

`CreateCameraRay()` constructs a world-space ray by:

1. Transforming camera origin through `_CameraToWorld`.
2. Transforming clip-space UV through `_CameraInverseProjection`.
3. Transforming the direction through `_CameraToWorld`.
4. Normalizing the result.

`CSMain` maps each pixel to `[-1, 1]` UV space with subpixel jitter from `rand()`.

## Depth Of Field

For each pass, `CSMain` computes a focal point:

```hlsl
float3 focalPoint = ray.origin + ray.direction * _FocalDistance;
```

It jitters the ray origin by a small fixed amount and re-aims the ray at the focal point. This approximates lens aperture blur.

## Intersections

`GetNearestIntersection()` checks:

1. `IntersectGroundPlane()` for an infinite plane at world `y = 0`.
2. Every sphere in `_Spheres`.
3. Every emissive sphere in `_Lights`.
4. Every mesh AABB in `_Meshes`, then only the intersected BVH nodes and leaf triangles for that mesh.

Spheres and lights are still traced with flat loops. Triangle meshes use a per-mesh AABB plus BVH traversal, so rays can skip whole meshes and large triangle groups before running expensive triangle tests.

## Lighting And Shadows

Direct lighting comes from emissive sphere lights.

`GetLightHittingPoint()` computes direct lighting by taking stochastic disk samples across each emissive sphere light. Bounce 0 uses `max(1, _ShadowQuality + 1)` samples per light, while later bounces use one sample per light to reduce cost.

Shadow rays test blockers against `_Spheres` and mesh BVHs, but not `_Lights`. Opaque blockers early-out immediately, while transparent blockers use the nearest transparent hit before the light distance to tint transmitted shadow light.

Transparent blockers can tint shadow light by using the blocking sphere color and opacity.

Direct light from sampled light points is accumulated additively rather than combined with a channel-wise max operation. Light falloff now uses a clamped inverse-square-style distance term scaled by light radius and `_LightFalloffScale`, although transparent shadow tinting remains approximate.

## Path Tracing Loop

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

## Debug Render Modes

`GameManager.debugRenderMode` uploads `_DebugRenderMode` to the compute shader. `FinalColor` uses the normal `TracePath()` output. Other modes use `GetDebugRenderColor()` to visualize a single diagnostic quantity.

Available modes:

- `FinalColor`: normal path-traced render.
- `Normals`: first-hit normal mapped from `[-1, 1]` to `[0, 1]`.
- `Albedo`: first-hit surface color.
- `Emission`: first-hit emission, clamped to displayable `[0, 1]` color.
- `DirectLight`: first-hit direct light from soft light sampling, clamped to `[0, 1]`.
- `Throughput`: remaining path throughput after iterative scattering, clamped to `[0, 1]`.
- `BounceCount`: completed non-terminal bounces normalized by `_NumBounces`.
- `HitDistance`: first-hit distance divided by `25`, clamped to grayscale `[0, 1]`; sky renders white.

Debug modes still use the normal camera ray generation and depth-of-field jitter path, so high `numberOfPasses` can average noisy debug samples for modes involving randomized normals, direct light, or throughput.

## Transparency And Refraction

Transparent/glass sphere refraction is approximate. `ApplySphereRefraction()`:

1. Refracts from air into the sphere using `Refract()`.
2. Estimates the exit point by finding a closest point across the sphere chord.
3. Computes the exit normal.
4. Refracts back out into air.

Glass material scattering now uses Schlick Fresnel reflectance to weight transmission, but the transmitted ray still uses the project’s approximate sphere refraction helper rather than a full Snell-law volume traversal. This avoids the high variance of randomly choosing reflection or transmission per sample.

Triangle mesh refraction uses `ApplyPlanarTransmission()` rather than the sphere helper:

1. Refract from air into the hit triangle using the project `Refract()` helper.
2. Cast an internal ray against triangles with the same `meshIndex`.
3. Use the nearest internal triangle hit as the exit face.
4. Refract from material back into air.
5. Continue the path from the exit point.

This gives visible prism-like behavior for simple closed meshes such as pyramids. It is still approximate: it assumes a mostly closed/convex mesh, does not handle nested media, and does not model distance-based absorption.

## Randomness

`CSMain` creates a local `uint rngState` for each pixel and sample pass using `_Seed`, pixel coordinates, and the sample index. `rand(inout rngState)` advances that local state through an integer hash and returns a normalized float in `[0, 1)`.

The RNG is used for subpixel camera jitter, depth-of-field aperture jitter, stochastic area-light samples, cosine-weighted diffuse bounce sampling, Russian roulette termination, and rough reflection normal randomization. When `randomNoise` is false, C# sends a fixed integer seed each frame for deterministic stable noise patterns. When `randomNoise` is true, C# sends a new random integer seed each frame.

## Partial Shader Pieces

- `distanceThroughOpacity` is written for transparent/refraction calculations and transparent shadow logic, but is not part of a full absorption model.
