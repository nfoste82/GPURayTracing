# Materials, Lights, And Scene Representation

The compute renderer does not use Unity materials, mesh renderers, or built-in lights for ray-traced shading. It uses custom MonoBehaviours to upload compact sphere and light data to the compute shader.

## Ray-Traced Objects

Any object with `RayTracingObject` registers with `GameManager` when enabled.

`RayTracingObject` can represent a sphere, light sphere, or triangle mesh depending on attached components.

Sphere and light objects use a `SphereCollider`; the collider radius becomes the ray-traced sphere radius and the object transform position becomes the sphere center.

Mesh objects use `RayMaterial` plus `MeshFilter` and should not have a `SphereCollider`, because sphere registration takes priority. The shared mesh triangles are transformed to world space and uploaded directly.

## Materials

`RayMaterial` marks a `RayTracingObject` as regular renderable geometry. With a `SphereCollider`, the object renders as a sphere. With a `MeshFilter` and no `SphereCollider`, the object renders as uploaded triangles.

Fields:

- `Type`: selects `Diffuse`, `Metal`, or `Glass` scattering in the compute shader.
- `Color`: uploaded as normalized RGB and used as albedo/tint.
- `Smoothness`: controls metal/glass reflection roughness by randomizing the hit normal. Higher values preserve the normal more closely.
- `Opacity`: `1` is opaque. Values below `1` allow glass/transparent transmission.
- `RefractionIndex`: used by glass Fresnel reflectance and the custom approximate refraction path.

In the shader, material color is retrieved through `GetAlbedo(hit)`. Diffuse and metal paths attenuate throughput by albedo. Glass transmission is tinted by albedo based on opacity, while glass reflection keeps white reflective throughput.

Material behavior:

- `Diffuse`: direct lighting with cosine-weighted hemisphere scattering for later bounces.
- `Metal`: reflective scattering, with `Smoothness` controlling roughness.
- `Glass`: Schlick Fresnel weights approximate sphere refraction/transmission. Triangle meshes use approximate closed-mesh entry/exit refraction.

## Ray Mesh Primitives

`RayMeshPrimitive` procedurally generates simple mesh test objects for triangle rendering. It supports cube, pyramid, and dodecahedron shapes.

Editor menu entries under `GameObject > Ray Tracing` create these primitives with `MeshFilter`, `MeshRenderer`, `RayMaterial`, `RayMeshPrimitive`, and `RayTracingObject` components. They are visible in Scene view through the normal `MeshRenderer`, but `RayMeshPrimitive.HideRasterizedRendererInPlayMode` disables the rasterized renderer in Play mode by default so the Game view uses the compute ray tracer only.

The generated primitive material defaults are intended for glass/refraction testing: `Glass`, opacity `0.5`, smoothness `1.0`, and refraction index `1.5`.

## Lights

`RayLight` marks a `RayTracingObject` as an emissive sphere light.

Fields:

- `Color`: uploaded as normalized RGB emission.

Light objects are stored in `_Lights`, using the same `Sphere` data layout as regular spheres. When a camera/path ray directly hits a light sphere, `TracePath()` adds its emission and terminates the path.

Direct lighting also samples `_Lights` explicitly in `GetLightHittingPoint()`. Bounce 0 uses multiple stochastic area-light samples per light, while later bounces use one sample per light. Sampled light contributions are accumulated additively.

## Ground Plane

The ray-traced ground is not the Unity scene mesh. It is an implicit infinite plane at world `y = 0` inside `IntersectGroundPlane()`.

Ground properties:

- Color is hard-coded to `float3(0.8f, 0.8f, 0.8f)`.
- Normal is hard-coded to `float3(0.0f, 1.0f, 0.0f)`.
- Smoothness comes from `_GroundSmoothness` and blends the first continuation ray between diffuse scatter and reflection.
- Opacity is always `1`.

## Unity Scene Objects

Unity meshes are traced by the compute shader only when they are registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`, without being registered as spheres through `SphereCollider`. Other Unity meshes/colliders can still affect Unity physics, scene editing, and visual editor context without being ray traced.

The scene’s `Directional Light` is a Unity light and is not used by the compute shader lighting model.

## Physics

Several spheres have `Rigidbody` and `SphereCollider`. Unity physics can move them. Each rendered frame, `GameManager.UpdateSpheres()` reads object transforms and uploads updated positions/radii/materials/lights to the GPU.

This means the ray tracer can render dynamic physics-driven spheres even though it is not using Unity’s standard rendering path for the final image.
