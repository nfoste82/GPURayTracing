# Materials, Lights, And Scene Representation

The compute renderer does not use Unity materials, mesh renderers, or built-in lights for ray-traced shading. It uses custom MonoBehaviours to upload compact sphere and light data to the compute shader.

## Ray-Traced Objects

Any object with `RayTracingObject` registers with `GameManager` when enabled.

`RayTracingObject` can represent a sphere, light sphere, or triangle mesh depending on attached components.

Sphere and light objects use a `SphereCollider`; the collider center is transformed to world space for the ray-traced sphere center, and the collider radius is scaled by the largest absolute object scale axis for the ray-traced radius.

Mesh objects use `RayMaterial` plus `MeshFilter` and should not have a `SphereCollider`, because sphere registration takes priority. The shared mesh triangles are transformed to world space and uploaded directly.

## Materials

`RayMaterial` marks a `RayTracingObject` as regular renderable geometry. With a `SphereCollider`, the object renders as a sphere. With a `MeshFilter` and no `SphereCollider`, the object renders as uploaded triangles.

Fields:

- `Type`: selects `Diffuse`, `Metal`, or `Glass` scattering in the compute shader. Defaults to `Metal`. (Mesh primitives created via `RayMeshPrimitive` override this to `Glass` in `Reset()`.)
- `Color`: uploaded as normalized RGB and used as albedo/tint.
- `Smoothness`: controls metal/glass reflection roughness by randomizing the hit normal. Higher values preserve the normal more closely.
- `Opacity`: `1` is opaque. Values below `1` allow glass/transparent transmission. Note that any opacity below `1` makes the shader treat the hit as glass (see below), regardless of `Type`.
- `RefractionIndex`: used by glass Fresnel reflectance and the custom approximate refraction path.

The emissive material constant (`MaterialEmissive = 3` in the shader) is not selectable here. It is assigned internally to `RayLight` sphere lights during registration.

In the shader, material color is retrieved through `GetAlbedo(hit)`. Diffuse and metal paths attenuate throughput by albedo. Glass transmission is tinted by albedo based on opacity, while glass reflection keeps white reflective throughput.

The glass/refraction path is selected by `IsGlassMaterial(hit)`, which is true when `materialType == Glass` **or** when `hit.opacity < 1.0`. So a `Diffuse` or `Metal` object with opacity under `1` will render through the glass transmission/Fresnel path.

Material behavior:

- `Diffuse`: direct lighting with cosine-weighted hemisphere scattering for later bounces.
- `Metal`: reflective scattering, with `Smoothness` controlling roughness.
- `Glass`: Schlick Fresnel weights approximate sphere refraction/transmission. Triangle meshes use approximate closed-mesh entry/exit refraction.

## Ray Mesh Primitives

`RayMeshPrimitive` procedurally generates simple mesh test objects for triangle rendering. It supports cube, pyramid, and dodecahedron shapes. It also ensures a `MeshCollider` exists and points at the generated mesh, so these primitives participate in Unity physics as static collision geometry by default.

Editor menu entries under `GameObject > Ray Tracing` create these primitives with `MeshFilter`, `MeshRenderer`, `MeshCollider`, `RayMaterial`, `RayMeshPrimitive`, and `RayTracingObject` components. They are visible in Scene view through the normal `MeshRenderer`, but `RayMeshPrimitive.HideRasterizedRendererInPlayMode` disables the rasterized renderer in Play mode by default so the Game view uses the compute ray tracer only.

The generated primitive material defaults are intended for glass/refraction testing: `Glass`, opacity `0.5`, smoothness `1.0`, and refraction index `1.5`.

## Scene View Previews

`RayTracingObject.OnDrawGizmos()` draws Scene view gizmos for sphere and light-sphere objects. Regular sphere gizmo color comes from `RayMaterial.Color` and alpha comes from `RayMaterial.Opacity`. Light-sphere gizmo color comes from `RayLight.Color` and uses full alpha because lights do not expose opacity.

`RayObjectPreview` is an optional preview helper for ray-traced sphere and light-sphere objects. It requires a `SphereCollider`, creates/adds a `MeshFilter` and `MeshRenderer` using a generated sphere mesh sized from the collider, and hides the rasterized mesh in Play mode by default. For `RayLight` objects it can also add/update a Unity point light so light positions are visible and useful while composing the scene. These preview renderers/lights are editor/Unity-scene aids; they are not used by the compute shader for ray-traced shading.

`GameObject > Ray Tracing > Sphere` and `GameObject > Ray Tracing > Light Sphere` create objects with the ray-tracing components plus `RayObjectPreview`. `GameObject > Ray Tracing > Ground Preview Plane` creates a rasterized scene-building reference plane; the actual ray-traced ground remains implicit.

## Lights

`RayLight` marks a `RayTracingObject` as an emissive sphere light.

Fields:

- `Color`: uploaded as normalized RGB emission.

Light objects are stored in `_Lights`, using the same `Sphere` data layout as regular spheres. When a camera/path ray directly hits a light sphere, `TracePath()` adds its emission and terminates the path.

Direct lighting also samples `_Lights` explicitly in `GetLightHittingPoint()`. Bounce 0 uses multiple stochastic area-light samples per shaded light, while later bounces use one sample per shaded light. Sampled light contributions are accumulated additively. How many lights are shaded per hit depends on `GameManager.lightSamplingStrategy` (all lights, uniform random, or importance-sampled) and `lightSampleCount`; see `07-shader-lighting-and-materials.md`.

## Ground Plane

The ray-traced ground is not the Unity scene mesh. It is an implicit infinite plane at world `y = 0` inside `IntersectGroundPlane()`.

Ground properties:

- Color is hard-coded to `float3(0.8f, 0.8f, 0.8f)`.
- Normal is hard-coded to `float3(0.0f, 1.0f, 0.0f)`.
- Smoothness comes from `_GroundSmoothness` and blends the first continuation ray between diffuse scatter and reflection.
- Opacity is always `1`.

`GameManager` draws an opaque Scene view preview for the implicit ground plane. In the editor this preview is drawn with depth-tested `Handles` to avoid the transparent gizmo draw-order problems that can otherwise make the ground appear in front of sphere gizmos.

## Skybox Preview

The compute shader samples `GameManager.skyboxTexture` through `_SkyboxTexture` and multiplies it by `_SkyboxLight`, which is derived from `GameManager._skyboxLightColor`. The equirectangular lookup in `GetSkyboxColor()` uses negated axes, so swapped skybox textures may need to be flipped/rotated to appear correctly.

For scene composition, `GameManager.syncUnitySkyboxToRayTracedSkybox` can create a transient Unity `Skybox/Panoramic` material from the same `skyboxTexture`, tint it with `_skyboxLightColor`, and assign it to `RenderSettings.skybox`. `unitySkyboxExposure` and `unitySkyboxRotation` tune this Unity preview. This makes the Scene view skybox closer to Play mode, but it does not affect compute-shader lighting beyond the existing `_SkyboxTexture` and `_SkyboxLight` parameters.

## Unity Scene Objects

Unity meshes are traced by the compute shader only when they are registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`, without being registered as spheres through `SphereCollider`. Other Unity meshes/colliders can still affect Unity physics, scene editing, and visual editor context without being ray traced.

The scene’s `Directional Light` is a Unity light and is not used by the compute shader lighting model.

## Physics

Several spheres have `Rigidbody` and `SphereCollider`. Unity physics can move them. Each rendered frame, `GameManager.UpdateSpheres()` reads object transforms and uploads updated positions/radii/materials/lights to the GPU.

This means the ray tracer can render dynamic physics-driven spheres even though it is not using Unity’s standard rendering path for the final image.

Ray mesh primitives have `MeshCollider` but no `Rigidbody` by default. Unity treats those colliders as static obstacles, so dynamic spheres can collide with them without making the meshes dynamic physics bodies.
