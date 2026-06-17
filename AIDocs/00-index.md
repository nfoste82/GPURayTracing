# AI Docs Index

Use this folder as focused context for LLM-assisted work on the Unity GPU ray tracer. Read only the documents relevant to the task.

## Documents

- `01-project-overview.md`: High-level project purpose, scene contents, runtime architecture, and feature set.
- `02-runtime-data-flow.md`: How Unity/C# registers objects, builds buffers, sets shader parameters, dispatches the compute shader, and presents the result.
- `03-compute-shader-renderer.md`: Compute shader data structures, ray generation, intersections, lighting, depth of field, transparency/refraction, and iterative path tracing flow.
- `04-materials-lights-scene.md`: How ray-traced sphere materials, emissive light spheres, scene objects, and non-ray-traced Unity physics/mesh objects are represented.
- `05-known-limitations-and-next-steps.md`: Known implementation limits, unused/partial systems, likely pitfalls, and good future improvement directions.

## Fast Context Selection

- To change rendering/shading behavior, read `03-compute-shader-renderer.md` and usually `04-materials-lights-scene.md`.
- To change Unity orchestration, object registration, buffers, camera controls, or render dispatch, read `02-runtime-data-flow.md`.
- To add features such as meshes, BVH, accumulation, material types, or better physical lighting, read `03-compute-shader-renderer.md` and `05-known-limitations-and-next-steps.md`.
- To understand the project quickly before making broad changes, read `01-project-overview.md` first.
