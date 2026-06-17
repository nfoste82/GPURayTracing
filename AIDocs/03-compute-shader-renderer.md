# Compute Shader Renderer

The renderer lives in `Assets/Scripts/RayTracingCompute.compute`. It has one kernel, `CSMain`, using `[numthreads(8,8,1)]`.

## GPU Inputs

Important shader globals:

- `Result`: writable output texture.
- `_CameraToWorld`: camera transform matrix.
- `_CameraInverseProjection`: inverse projection matrix for camera ray generation.
- `_SkyboxTexture`: sampled when rays miss all geometry.
- `_CheckerboardTexture`: declared and sampled by an unused helper.
- `_SkyboxLight`: skybox lighting multiplier.
- `_NumberOfPasses`: per-frame samples per pixel.
- `_NumBounces`: maximum bounces for `TracePath()`.
- `_DebugRenderMode`: selects final path-traced color or a debug visualization.
- `_ShadowQuality`: soft-shadow grid radius. Total samples per light are `(2 * _ShadowQuality + 1)^2`.
- `_ShadowRandomness`: jitter amount for soft shadow samples.
- `_FocalDistance`: depth-of-field focal distance.
- `_GroundSmoothness`: smoothness for the implicit ground plane.
- `_Seed` and `_Pixel`: shader-side random number state.
- `_NumSpheres`, `_NumLights`: active buffer counts.
- `_Spheres`, `_Lights`: structured buffers of `Sphere` data.

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
};
```

`Ray` contains only origin and direction.

`RayHit` stores hit position, object position/radius, normal, emission, color, distance, smoothness, opacity, transparent travel distance, and refraction index.

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

There is no acceleration structure. Every ray is `O(numSpheres + numLights)`.

## Lighting And Shadows

Direct lighting comes from emissive sphere lights.

`GetLightHittingPoint()` computes soft shadows by sampling a grid of points around each light sphere. Shadow rays test blockers only against `_Spheres`, not `_Lights`.

`GetLightHittingPointHardShadow()` takes one jittered sample per light and is used for later bounces to reduce cost.

Transparent blockers can tint shadow light by using the blocking sphere color and opacity.

Both direct-light functions use `Combine()`, which is a channel-wise max operation. This keeps the existing stylized look but is not physically additive radiance.

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
4. Randomize the normal based on surface smoothness.
5. Sample direct light. Bounce 0 uses soft shadows; later bounces use hard shadows.
6. Add direct contribution: `throughput * albedo * directLight * hit.opacity`.
7. Create the next ray by reflection, or approximate refraction for transparent objects.
8. Update `throughput *= albedo`.
9. If transparent, also scale throughput by `GetTransmissionAmount()`.
10. Stop early when throughput is effectively black.

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

Transparent sphere refraction is approximate. `ApplySphereRefraction()`:

1. Refracts from air into the sphere using `Refract()`.
2. Estimates the exit point by finding a closest point across the sphere chord.
3. Computes the exit normal.
4. Refracts back out into air.

This is not a full Snell-law/Fresnel model. It preserves the project’s existing look while fitting the iterative path tracing structure.

## Randomness

`rand()` uses `_Seed`, `_Pixel`, and a sine/hash-style expression. `_Seed` is mutated during shader execution. When `randomNoise` is false, C# sends a deterministic seed each frame, giving stable noise patterns.

## Unused Or Partial Shader Pieces

- `_AmbientLight` is declared but unused.
- `_CheckerboardTexture` is currently only used by `GetTextureColorOnSphere()`, which is unused.
- `ModifyNormalByBumpColor()` is unused.
- `distanceThroughOpacity` is written for transparent/refraction calculations and transparent shadow logic, but is not part of a full absorption model.
