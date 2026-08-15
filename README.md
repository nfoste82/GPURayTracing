# Realtime Path Tracing
Realtime 3D path-tracer running in a GPU compute shader in Unity. Full disclosure that LLMs were used to assist during the later work on this project.

## Features:
* GPU compute-shader path tracing for spheres and registered triangle meshes
* Emissive sphere and mesh lights with direct-light and sampling
* Surface reflections (configurable smoothness of surfaces), diffuse indirect lighting, directional lighting, and multiple ray bounces
* Reflection/refraction, distance-based absorption, and colored transparent shadows
* Photon-mapped caustics
* Mesh UV/albedo/metallic/normal texturing
* Optional animated procedural water with reflection, refraction, and underwater RGB absorption
* Depth of field, variable camera aperture, different aperture types, ability to focus on a point in the scene even while camera is in motion
* Frame accumulation, debug views, and example scenes
* Volumetric fog (homogeneous fog)
* Spatial denoising (basic hand-rolled, not machine-learned denoising)
* ACES filmic tone mapping with configurable exposure and optional firefly luminance clamping
* Normal and parallax mapping
* glTF/GLB import support, including automatic conversion of base-color, metallic-roughness, normal, and transmission/IOR material data into ray-traced materials.
* Support for Unity terrains with multi-texture splatting

## Features missing or approximate:
* Spectral refractions (different wavelengths of light refract differently), current lighting system does not handle wavelengths
* Temporal denoising is a work-in-progress
* Considering adding the option for machine-learning-based upscaling and denoising, and when rendering in real time, possibly even frame insertion to improve frame rate
* Fog that isn't homogenous
* Subsurface scattering
* Shader compilation can take quite a while, I'd like to optimize this
* Environment mapped lighting instead of just direct sky reflections

There are multiple quality settings on the GameManager object in the root scene. Depending on the scene and quality, and your hardware, your frame rate may vary by quite a bit. Realtime can look decent on the right hardware with the right scene and settings. Some features like water and caustics are too expensive to look good in realtime currently.

Project has only been tested for MacOS, but all code should be OS-agnostic, so if it works in Unity then you should be able to run it.

### Caustics
[![Caustics](ExampleImages/caustics.png)](ExampleImages/caustics.png)

### Water with light absorption, reflection, refraction, and caustics
[![Water with reflection, refraction, light absorption](ExampleImages/water_scene.png)](ExampleImages/water_scene.png)

https://github.com/user-attachments/assets/c2fa9427-c246-47ff-9919-e17c34094d6f

https://github.com/user-attachments/assets/4ff5669a-c7db-427c-8167-707b0ca8e22f

Note: These video were rendered offline, at around 45-75 seconds per frame.

### Overlapping materials, emissive materials
[![Emissive wrapped by glass](ExampleImages/light-ball-in-dark.png)](ExampleImages/light-ball-in-dark.png)
[![Core wrapped by brass](ExampleImages/material_ball_scene.png)](ExampleImages/material_ball_scene.png)
[![Core wrapped by rough glass](ExampleImages/green-glass-ball.png)](ExampleImages/green-glass-ball.png)

### Can load in models directly from Khronos
[![Watch](ExampleImages/watch.png)](ExampleImages/watch.png)
[![Fruit basket](ExampleImages/fruit_basket.png)](ExampleImages/fruit_basket.png)

### Volumetric Fog
[![Volumetric Fog](ExampleImages/volumetric_fog.png)](ExampleImages/volumetric_fog.png)

### Cornell box
[![Dragon model rendered in a Cornell box](ExampleImages/dragon_cornell_box.png)](ExampleImages/dragon_cornell_box.png)

### Special Thanks
Thanks to these projects which have been great reference and learning material:

* https://github.com/gkjohnson/three-gpu-pathtracer
* https://github.com/knightcrawler25/GLSL-PathTracer/
* https://github.com/tylertms/vkrt
