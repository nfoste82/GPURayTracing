# Materials, Lights, And Scene Representation

The compute renderer does not use Unity materials, mesh renderers, or built-in lights for ray-traced shading. It uses custom MonoBehaviours to upload compact sphere and light data to the compute shader.

## Ray-Traced Objects

Any object with `RayTracingObject` registers with `GameManager` when enabled.

`RayTracingObject` requires a `SphereCollider`. The collider radius becomes the ray-traced sphere radius. The object transform position becomes the sphere center.

## Materials

`RayMaterial` marks a `RayTracingObject` as a regular renderable sphere.

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
- `Glass`: Schlick Fresnel weights approximate sphere refraction/transmission.

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

The root scene contains Unity meshes/colliders for walls and ground. These are not traced by the compute shader. They can still affect Unity physics, scene editing, and visual editor context.

The scene’s `Directional Light` is a Unity light and is not used by the compute shader lighting model.

## Physics

Several spheres have `Rigidbody` and `SphereCollider`. Unity physics can move them. Each rendered frame, `GameManager.UpdateSpheres()` reads object transforms and uploads updated positions/radii/materials/lights to the GPU.

This means the ray tracer can render dynamic physics-driven spheres even though it is not using Unity’s standard rendering path for the final image.
