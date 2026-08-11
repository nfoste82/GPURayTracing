# Compute Shader Renderer

The renderer lives in `Assets/Scripts/RayTracingCompute.compute`. Its main image kernel, `CSMain`, uses `[numthreads(4,4,1)]` (16 threads) to keep Metal's threadgroup-wide temporary-register use within its recommended budget. The shader also contains regression and optional caustics kernels.

## GPU Inputs

Important shader globals:

- `Result`: writable internal-resolution linear HDR trace output. It remains available for debug visualizations; final color is presented through the separate reconstruction pass.
- `AccumulationResult`: writable HDR accumulation texture used to progressively average final-color frames before exposure/tone mapping.
- `_CameraToWorld`: camera transform matrix.
- `_CameraInverseProjection`: inverse projection matrix for camera ray generation.
- `_SkyboxTexture`: sampled when rays miss all geometry. Sampling in `GetSkyboxColor()` uses a non-standard equirectangular mapping with negated axes (`theta = acos(dir.y) / -PI`, `phi = atan2(dir.x, -dir.z) / -PI * 0.5`), so skybox orientation/handedness is not obvious; expect to flip or rotate textures when swapping them in.
- `_MeshAlbedoTextures`: fixed-size `Texture2DArray` containing active mesh albedo textures. Triangle hits interpolate uploaded UVs and sample `textureIndex`; untextured meshes keep using `RayMaterial.Color` only.
- `_SkyboxLight`: skybox lighting multiplier.
- `_NumberOfPasses`: per-frame samples per pixel.
- `_SubpixelJitterScale`: width of the random primary-ray pixel filter. `1` samples the full pixel footprint; values above `1` intentionally extend into neighboring pixels and blur the image.
- `_UseFrameAccumulation`, `_AccumulatedFrameCount`, `_SampleOffset`: control progressive final-color accumulation and advance deterministic sample indices across frames. The sample sequence also advances when accumulation is disabled, so animated scenes do not repeat the same stochastic samples every frame.
- `_NumBounces`: maximum bounces for `TracePath()`.
- `_DebugRenderMode`: selects final path-traced color or a debug visualization.
- `_ShadowQuality`: soft-shadow sample budget control. Bounce-0 direct lighting takes `max(1, _ShadowQuality + 1)` stochastic area-light samples per light.
- `_ShadowRandomness`: area-light sampling radius multiplier for soft shadow samples.
- `_LightFalloffScale`: distance falloff scale for direct light. Higher values make light intensity decrease faster with distance.
- `_FocalDistance`: depth-of-field focal distance.
- `_ApertureRadius`, `_ApertureBladeCount`, `_ApertureBladeRotation`, and `_AnamorphicRatio`: thin-lens aperture size and shape. Radius zero is an exact pinhole path; blade counts below three use a circular aperture.
- `_Exposure`: master brightness multiplier applied before tone mapping. Acts like a camera exposure dial.
- `_FireflyClamp`: optional maximum HDR luminance for each complete path sample before per-pixel averaging. `0` disables it; lower positive values clamp more strongly. This deliberately biased variance control suppresses rare specular samples that otherwise remain visible in low-sample animated scenes.
- `_WaterAbsorptionStrength`: distance-based water-medium absorption density. When a path or direct-light segment travels underwater, the shader applies exponential transmittance from `_WaterColor` and this strength. The active `Water` component supplies these globals; its transform position is `_WaterCenter`, X/Z scale is `_WaterSize`, and Y scale is `_WaterDepth` below the wavy top.
- `_FogEnabled`, `_FogBoundsMin`, `_FogBoundsMax`, `_FogDensity`, `_FogScatteringAlbedo`, `_FogInScatteringIntensity`, and `_FogMultipleScattering`: configure one optional axis-aligned homogeneous participating volume. Fog overlaps glass/water medium state, samples isotropic free-flight events, and attenuates direct-light segments through its bounds. `_FogInScatteringIntensity` is an explicitly display-oriented multiplier that reveals shafts without changing extinction. Single scattering is the default for lower cost and stronger light-shaft contrast; multiple scattering can be enabled from `GameManager` but adds noise and fills shadowed regions.
- `_LightSamplingStrategy`: selects how `GetLightHittingPoint()` samples scene lights (`0` = all lights, `1` = uniform random pick, `2` = importance-sampled pick). See `07-shader-lighting-and-materials.md`.
- `_LightSampleCount`: for the random/importance strategies, how many lights each shading point draws per hit. Ignored by the all-lights strategy.
- `_MaxLightSamples`: diagnostic cap on how many lights any strategy considers. `0` means no cap (use the real light count); a positive value clamps the considered light count to confirm the per-hit light loop is the bottleneck.
- `_Seed`: integer seed used to initialize per-pixel/per-pass shader RNG state.
- `_NumSpheres`, `_NumLights`, `_NumTriangles`, `_NumMeshes`: active buffer counts.
- `_NumTopLevelBvhNodes`: active top-level object BVH node count; `0` means first-hit traversal uses flat object loops.
- `_NumShadowBvhNodes`: active shadow-only BVH node count; `0` means shadow traversal uses flat blocker loops.
- `_Spheres`: structured buffer of sphere data. `_Lights`: structured buffer of sphere and triangle light data.
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
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float smoothness;
    float opacity;
    float refraction;
    int materialType;
    int meshIndex;
    int textureIndex;
};
```

`meshIndex` identifies which uploaded triangles belong to the same mesh object. It is used by approximate closed-mesh refraction to find the exit face. `textureIndex` selects a slice of `_MeshAlbedoTextures`; `-1` means no texture.

## Material Type Constants

The shader defines five material type constants:

- `MaterialDiffuse = 0`
- `MaterialMetal = 1`
- `MaterialGlass = 2`
- `MaterialEmissive = 3`
- `MaterialWater = 4`

`RayMaterial` only exposes `Diffuse`, `Metal`, and `Glass` (0-2). `MaterialEmissive = 3` is assigned to emissive sphere/mesh lights and is not selectable in `RayMaterial`; lights are detected by nonzero emission via `DidHitLight()`. `MaterialWater = 4` is assigned internally by the procedural water intersection.

Triangle meshes also upload `MeshInfo` and `BvhNode` data. Each mesh has an object-level AABB in `_Meshes`, and its triangles are arranged into a binary BVH whose leaf nodes contain small contiguous triangle ranges in `_Triangles`.

The scene also uploads a top-level BVH over ray-traced spheres, emissive light spheres, and registered mesh AABBs. First-hit traversal uses this BVH to skip whole objects before testing sphere intersections or entering a mesh's per-mesh BVH. Shadow traversal uses a separate shadow-only BVH over regular spheres and mesh AABBs, excluding light spheres because lights are not shadow blockers.

`RayHit` stores hit position, object position/radius, normal, emission, color, distance, smoothness, opacity, transparent travel distance, refraction index, material type, mesh index, and sphere object index.

`MediumIdentity` distinguishes air, sphere, mesh, and water media by both type and object identity, and stores IOR/opacity/absorption color. `TracePath()` carries a fixed-capacity stack with implicit air, initializes water for rays that start underwater, and updates it only for transmitted boundary crossings. Matching exits reveal the parent medium; overflow and unmatched exits set explicit stack status bits. Every traveled segment receives absorption from the active medium before its hit is shaded. Refraction and path-selection Fresnel use the current medium as the source and the entered medium or revealed parent as the target, including nested water/glass transitions.

## Ray Generation

`CreateCameraRay()` constructs a world-space ray by:

1. Transforming camera origin through `_CameraToWorld`.
2. Transforming clip-space UV through `_CameraInverseProjection`.
3. Transforming the direction through `_CameraToWorld`.
4. Normalizing the result.

`CSMain` maps each pixel to `[-1, 1]` UV space with subpixel jitter from `rand()`. The jitter samples the full pixel footprint by default (`_SubpixelJitterScale = 1`); progressive accumulation and `_NumberOfPasses` increase the number of such samples rather than widening that footprint.

## Tone Mapping And Exposure

Each final-color path sample is optionally luminance-clamped before averaging. After all passes are averaged, `CSMain` optionally blends final-color HDR radiance into `AccumulationResult` using `_AccumulatedFrameCount`. This happens before exposure/tone mapping, so exposure changes can remap the accumulated HDR result without changing the stored radiance. Debug visualizations skip both the clamp and accumulation and are written with their raw diagnostic values.

`CSMain` leaves final-color radiance linear HDR. `RayTracingSpatialDenoiser.compute` then uses `CSPresent` to reconstruct it at display resolution with Catmull-Rom filtering and applies `ACESFilmicToneMap(color * _Exposure)`, the Narkowicz 2015 ACES filmic approximation. This maps open-ended HDR radiance into `[0, 1]` so bright values roll off smoothly instead of clipping hard to white. `_Exposure` comes from `GameManager.exposure`.

## Depth Of Field

For each pass with a nonzero aperture, `CSMain` intersects the pinhole ray with a camera-forward focal plane:

```hlsl
float focusRayDistance = _FocalDistance / dot(ray.direction, cameraForward);
float3 focalPoint = ray.origin + ray.direction * focusRayDistance;
```

It samples a configurable circular or polygonal aperture in camera right/up space, optionally stretches it anamorphically while preserving area, shifts the origin across that lens, and re-aims at the focal point. `GameManager` supports an exact pinhole, direct world-space lens radius, or a physical radius derived from the Unity camera focal length and f-number. The optional aperture scale accounts for project world-unit conventions. Lens changes invalidate final-color accumulation.

`CSFocusQuery` creates an unjittered pinhole ray from a clicked normalized screen coordinate and calls the same `GetNearestIntersection()` used by rendering. It writes the first surface's world-space hit to a one-element readback buffer regardless of opacity, allowing glass to be selected directly while preserving parity with GPU-only geometry such as procedural water.

## Core Path Tracing Loop

`TracePath()` is the main iterative renderer.

It maintains:

- `radiance`: accumulated light returned to the camera.
- `throughput`: accumulated material/tint/energy carried by the current path.
- `mediumStack`: the nested closed volumes currently containing the path, with implicit air at the base.
- `albedo`: surface color from `hit.color`.
- `emission`: emitted light from `hit.emission`.

Per bounce:

1. Trace the ray with `GetNearestIntersection()` and sample a fog free-flight distance over the bounded ray segment. A fog event before the surface receives shadowed direct lighting through an isotropic phase function, scatters in a uniform direction, consumes the bounce, and continues without shading the surface.
2. Attenuate `throughput` for the actual finite distance traveled through the stack's active medium. Water segments stop at the nearest wavy-top, side, or bottom boundary; air is neutral and finite glass sky misses do not use infinite distance.
3. If it hits sky, add `throughput * skyColor` and stop.
4. If it hits a light, add `throughput * emission` and stop.
5. Sample direct light if the path throughput is above `MinDirectLightThroughput`. Bounce 0 uses multiple stochastic soft-shadow samples; later bounces use one light sample. `EvaluateMaterialBrdf()` evaluates the same Lambert/GGX material model used to sample opaque continuation rays. Explicit-light and opaque BRDF samples use power-heuristic MIS weights when both can discover the same emissive sphere or triangle.
6. Add direct contribution: `throughput * directLight`.
7. Create the next ray using the hit material type.
8. Update `throughput` with the scatter attenuation.
9. Stop early when throughput is effectively black.
10. Starting after the first few bounces, apply Russian roulette termination and scale surviving throughput by survival probability.

Before frame accumulation, `ClampFirefly()` optionally caps each sample by luminance; `FireflyClamp = 0` disables that cap. The Demofox HDR-reference fixtures disable it so a bright area-light reflection is not reduced to the same radiance as the skybox before ACES tone mapping.

Material scattering currently supports:

- `Diffuse`: mixes cosine-weighted Lambert and GGX continuation samples and weights throughput by `brdf * abs(N dot L) / pdf`.
- `Metal`: samples a GGX reflection lobe using albedo as Fresnel F0 and the same evaluation used for direct highlights.
- `Glass`: uses IOR-derived Schlick Fresnel reflectance to choose reflection versus approximate transmission independently of opacity. Water retains its separate opacity-scaled surface behavior. Fresnel and Snell calculations use source/target IORs from the path medium stack. Transmitted paths are filtered by distance-based absorption. For spheres, the transmitted path refracts into the sphere, checks the bounded internal segment before the sphere exit for any closer scene object, and only refracts back out when no interior/interpenetrating hit is found. For mesh triangles, it uses an approximate closed-mesh entry/exit path that refracts into the mesh, finds the nearest exit triangle with the same `meshIndex`, then checks the top-level scene traversal for any closer object inside that bounded internal segment. If an interior object is found, tracing continues inside the transparent object; otherwise the ray refracts back out and continues from the exit point.

Note: the glass/refraction path is selected by `IsGlassMaterial(hit)`, which returns true when `materialType == Glass` **or** when `hit.opacity < 1.0`. A `Diffuse` or `Metal` object with opacity below `1` therefore takes the glass transmission/Fresnel path regardless of its declared material type.

For intersection, BVH, lighting, refraction, debugging, and randomness details, see the focused shader docs listed in `00-index.md`.
