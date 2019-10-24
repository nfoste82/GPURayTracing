# Realtime Ray Tracing
Realtime 3D raytracer running in a GPU compute shader.

Features:
* Surface reflections
* Diffuse and ambient lighting
* Transparency and light refraction
* Hard and soft shadows
* Transparent objects created colored shadows
* Depth of field with auto-focusing
* Multiple ray bounces

There are multiple quality settings on the GameManager object in the root scene. At normal settings you should be able to easily sustain 60+ frames per second. At the highest settings you'll end up with frames taking one or more seconds to render.

![Depth of field, soft shadows, surface reflections](https://imgur.com/ZR4qbcz.jpg)

![Transparency, refraction, colored shadows](https://i.imgur.com/vJXKV0J.png)

![Raytracing is fun](https://imgur.com/rXc3fBq.jpg)
