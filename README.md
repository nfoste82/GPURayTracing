# Realtime Path Tracing
Realtime 3D raytracer running in a GPU compute shader in Unity.

Features:
* GPU compute-shader path tracing for spheres, registered triangle meshes, and an implicit ground plane
* Emissive sphere and mesh lights with direct-light sampling
* Surface reflections, diffuse indirect lighting, and multiple ray bounces
* Glass reflection/refraction, distance-based absorption, and colored transparent shadows
* Mesh UV/albedo textures
* Optional animated procedural water with reflection, refraction, and underwater absorption
* Hard and soft shadows
* Depth of field with auto-focusing
* Frame accumulation, dynamic quality, debug views, and benchmark scenes

Features missing or approximate:
* Caustics and a fully consistent energy-conserving BRDF/PDF formulation
* General nested-medium handling for overlapping glass and water volumes
* Imported smooth vertex normals and material texture maps beyond mesh albedo

There are multiple quality settings on the GameManager object in the root scene. At normal settings you should be able to easily sustain 60+ frames per second. At the highest settings you'll end up with frames taking one or more seconds to render.

![Depth of field, soft shadows, surface reflections](https://imgur.com/ZR4qbcz.jpg)

![Transparency, refraction, colored shadows](https://i.imgur.com/vJXKV0J.png)

![Raytracing is fun](https://imgur.com/rXc3fBq.jpg)
