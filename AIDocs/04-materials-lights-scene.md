# Materials, Lights, And Scene Representation

The compute renderer does not use Unity materials, mesh renderers, or built-in lights for ray-traced shading. It uses custom MonoBehaviours to upload compact sphere and light data to the compute shader.

## Ray-Traced Objects

Any object with `RayTracingObject` registers with `GameManager` when enabled.

`RayTracingObject` can represent a sphere, light sphere, triangle mesh, or mesh light depending on attached components.

Sphere objects and sphere lights use a `SphereCollider`; the collider center is transformed to world space for the ray-traced sphere center, and the collider radius is scaled by the largest absolute object scale axis for the ray-traced radius.

Mesh objects use `RayMaterial` plus `MeshFilter` and should not have a `SphereCollider`, because sphere registration takes priority. Mesh lights use `RayLight` plus `MeshFilter` and no `SphereCollider`. The shared mesh triangles are transformed to world space and uploaded directly.

## Materials

`RayMaterial` marks a `RayTracingObject` as regular renderable geometry. With a `SphereCollider`, the object renders as a sphere. With a `MeshFilter` and no `SphereCollider`, the object renders as uploaded triangles.

Fields:

- `Type`: selects `Diffuse`, `Metal`, `Glass`, or the editor-authored `Emissive` mode. Selecting `Emissive` adds `RayLight` and a Unity point-light preview; leaving it removes both. `RayLight` remains the renderer's efficient direct-light registration marker. Defaults to `Metal`. (Mesh primitives created via `RayMeshPrimitive` override this to `Glass` in `Reset()`.)
- `Color`: uploaded as normalized RGB and used as albedo/tint. For transmitted glass, it also acts as the RGB absorption/filter color, so stacked colored glass compounds per channel.
- `AlbedoTexture`: optional mesh-only albedo texture. Mesh triangle UVs are uploaded and sampled from a fixed-size texture array; the sampled texture color multiplies `Color`. Sphere materials ignore this field.
- `TextureUvScale`: shared mesh/sphere UV scale applied to all assigned material textures, including albedo, metallic-roughness, normal, and parallax maps. Values above `1` repeat the textures more often; the default is `(1, 1)`.
- `Metallic`: continuous mesh metallic response. `0` is dielectric and `1` is metal; existing `Metal` materials with the default zero value retain their historical fully metallic behavior.
- `MetallicRoughnessTexture`: optional mesh-only linear data map using glTF channels (green roughness, blue metallic). Both channels multiply the material scalar values.
- `NormalTexture`: optional mesh-only tangent-space normal map. Imported tangents are transformed with the mesh when available; meshes without tangents use a stable fallback basis. Mapped normals affect shading and optics while geometric normals remain authoritative for boundaries and ray offsets.
- `InterpolateNormals`: mesh-only smooth shading. When enabled and the mesh contains vertex normals, the renderer barycentrically interpolates those normals for lighting and opaque reflection. Triangle geometry is still used for intersections, ray-origin offsets, shadow boundaries, and glass refraction.
- `Smoothness`: controls metal/glass reflection roughness by randomizing the hit normal. Higher values preserve the normal more closely.
- `Opacity`: controls glass absorption density and transparent-shadow strength, but does not increase surface reflection. Together with `Color`, this provides Beer-Lambert-style RGB absorption through the distance traveled inside glass. Dielectric reflection remains controlled by IOR, viewing angle, and smoothness. Note that any opacity below `1` makes the shader treat the hit as glass (see below), regardless of `Type`.
- `Specular`: glass's minimum reflection chance. IOR-based Fresnel raises this toward `1` at grazing angles. `0` is IOR-only physical glass; `0.02` matches the Demofox reference's explicit minimum reflection.
- `Transmission`: glass transmission chance after reflection. `1` retains fully refractive glass; lower values reserve the remaining paths as absorbed/opaque without increasing reflection.
- `RefractionIndex`: used by glass Fresnel reflectance and the custom approximate refraction path.

`Emissive` is an authoring shortcut for `RayLight`, not an independent shader material path. Its color is mirrored to `RayLight.Color`, and its inspector exposes the linked `RayLight.Intensity`. The shader's `MaterialEmissive = 3` is assigned from `RayLight` during registration.

In the shader, material color is retrieved through `GetAlbedo(hit)`. Diffuse and metal paths attenuate throughput by albedo. Glass transmission attenuates throughput with distance-based RGB absorption using albedo/color and opacity, while dielectric reflection remains untinted.

The glass/refraction path is selected by `IsGlassMaterial(hit)`, which is true when `materialType == Glass` **or** when `hit.opacity < 1.0`. So a `Diffuse` or `Metal` object with opacity under `1` will render through the glass transmission/Fresnel path.

Material behavior:

- `Diffuse`: direct lighting with cosine-weighted hemisphere scattering for later bounces.
- `Metal`: reflective scattering, with `Smoothness` controlling roughness.
- `Glass`: `Specular` establishes a minimum reflection chance and Schlick Fresnel weights derived from the medium IORs raise it toward one at grazing angles. `Transmission` independently controls the remaining refractive paths. Transmitted glass paths and transparent shadows apply accumulated RGB absorption, so light loses energy and changes color through colored layers.

## Ray Mesh Primitives

`RayMeshPrimitive` procedurally generates simple mesh test objects for triangle rendering. It supports cube, pyramid, and dodecahedron shapes. It also ensures a `MeshCollider` exists and points at the generated mesh, so these primitives participate in Unity physics as static collision geometry by default.

Editor menu entries under `GameObject > Ray Tracing` create these primitives with `MeshFilter`, `MeshRenderer`, `MeshCollider`, `RayMaterial`, `RayMeshPrimitive`, and `RayTracingObject` components. They are visible in Scene view through the normal `MeshRenderer`, but `RayMeshPrimitive.HideRasterizedRendererInPlayMode` disables the rasterized renderer in Play mode by default so the Game view uses the compute ray tracer only.

The generated primitive material defaults are intended for glass/refraction testing: `Glass`, opacity `0.5`, smoothness `1.0`, and refraction index `1.5`.

## Scene View Previews

`RayTracingObject.OnDrawGizmos()` draws Scene view gizmos for sphere and light-sphere objects. Regular sphere gizmo color comes from `RayMaterial.Color` and alpha comes from `RayMaterial.Opacity`. Light-sphere gizmo color comes from `RayLight.Color` and uses full alpha because lights do not expose opacity.

`RayObjectPreview` is automatically added to every `RayTracingObject`. For sphere and light-sphere objects, it creates a raster sphere mesh from the collider; for mesh objects and lights it reuses the existing mesh. It synchronizes a transient material using the project-owned, double-sided `Hidden/RayTracing/ScenePreview` shader. The shader applies a deliberately sub-unit fixed directional key light and a scaled ambient fill color from the nearest `GameManager._skyboxLightColor`, preserving lighting headroom so white geometry remains visibly shaded. It does not enumerate Unity/ray-traced lights, render shadows, or use GI. It shows `RayMaterial` color and albedo without depending on render-pipeline shaders, and maps `RayMaterial.Opacity` to standard raster alpha blending for Scene-view transparency. Double-sided preview rendering keeps generated planes visible from either side, matching the compute renderer's intersection behavior. Ray lights receive an optional Unity point light and scale their preview emission by `Intensity`. The preview renderer is hidden in Play mode by default, so these editor/Unity-scene aids never participate in compute-shader shading. Metallic-roughness, normal mapping, glass, and other path-traced responses remain Game-view-only approximations.

`GameObject > Ray Tracing > Sphere` and `GameObject > Ray Tracing > Light Sphere` create ray-tracing components; the preview is attached automatically when the object is enabled.

## Lights

`RayLight` marks a `RayTracingObject` as an emissive light. With a `SphereCollider` it becomes an emissive sphere light. With a `MeshFilter` and no `SphereCollider` it becomes an emissive triangle-mesh light, so rectangular panels, discs, and other mesh shapes can light the scene.

Fields:

- `Color`: uploaded as normalized RGB emission.
- `Intensity`: nonnegative HDR emission multiplier. Values above `1` make the light brighter than the normalized skybox and other default lights without clipping its color.

Light objects are stored in `_Lights` using a compact light layout. Sphere lights also participate in the top-level BVH as directly visible light objects. Mesh lights are uploaded through `_Triangles` with emissive material data, so when a camera/path ray directly hits a mesh-light triangle, `TracePath()` adds its emission and terminates the path.

Direct lighting also samples `_Lights` explicitly in `GetLightHittingPoint()`. Sphere lights use disk samples across their radius. Each emissive mesh has one global light entry; it selects a triangle by world-space area from a CPU-built CDF, then samples that triangle barycentrically. This prevents a tessellated emitter from consuming one global light entry per triangle. `RayDirectionalLight` is implemented as two virtual no-falloff triangle lights at `10,000` units: its transform forward axis defines the direction light travels, its intensity is distance-independent HDR radiance, and its angular radius sizes the virtual square sun for soft shadows. The virtual triangles have no visible scene geometry and do not enter the intersection BVH, but they reuse triangle-light sampling, MIS, and caustic photon emission. Every scene built through `RayTracingSceneGenerator.CreateBaseScene()` receives a warm directional light with intensity `1.0` by default. Bounce 0 uses multiple stochastic area-light samples per shaded light, while later bounces use one sample per shaded light. Sampled light contributions are accumulated additively. How many lights are shaded per hit depends on `GameManager.lightSamplingStrategy` (all lights, uniform random, or importance-sampled) and `lightSampleCount`; see `07-shader-lighting-and-materials.md`.

## Skybox Preview

The compute shader samples `GameManager.skyboxTexture` through `_SkyboxTexture` and multiplies it by `_SkyboxLight`, which is derived from `GameManager._skyboxLightColor`. The equirectangular lookup in `GetSkyboxColor()` uses negated axes, so swapped skybox textures may need to be flipped/rotated to appear correctly.

For scene composition, `GameManager.syncUnitySkyboxToRayTracedSkybox` can create a transient Unity `Skybox/Panoramic` material from the same `skyboxTexture`, tint it with `_skyboxLightColor`, and assign it to `RenderSettings.skybox`. `unitySkyboxExposure` and `unitySkyboxRotation` tune this Unity preview. This makes the Scene view skybox closer to Play mode, but it does not affect compute-shader lighting beyond the existing `_SkyboxTexture` and `_SkyboxLight` parameters.

## Unity Scene Objects

Unity meshes are traced by the compute shader only when they are registered through `RayTracingObject` plus `RayMaterial` and `MeshFilter`, without being registered as spheres through `SphereCollider`. Other Unity meshes/colliders can still affect Unity physics, scene editing, and visual editor context without being ray traced.

Imported model assets such as FBX files are supported through their imported `MeshFilter`/`Mesh` data, but the mesh must be CPU-readable because `GameManager` reads vertices, indices, and UVs to build its own ray-tracing triangle buffer and per-mesh BVH. For model importer assets this means Unity's Read/Write import setting must be enabled. The Dragon Cornell benchmark generator enables this automatically for `Assets/Models/stanford-dragon-pbr.fbx` before loading its mesh.

`.gltf` and `.glb` assets are imported by the `com.unity.cloud.gltfast` package. Drag a glTF asset, along with any externally referenced `.bin` and image files, into `Assets`. `GltfRayTracingImporter` creates or refreshes a sibling `<asset>.RayTracing.prefab`; drag that prefab into a scene below the `GameManager`. It has a `RayMaterial` and `PathTracingObject` on each mesh node, copies the imported base color, base-color texture, metallic, metallic-roughness texture, normal texture, and roughness/smoothness defaults, and enables smooth imported normals. Remote glTF loads use the source glTF PBR factors and textures directly, rather than relying on generated Unity shader properties. They also map `KHR_materials_transmission` materials to ray-traced glass, including the extension's transmission factor and optional IOR; transmission textures are not yet supported. Multi-material meshes are separated into ray-traced child meshes so each glTF material is preserved. The generated prefab is labeled `RayTracingGeneratedGltf` and is regenerated whenever its source glTF is reimported; make material customizations on a prefab variant rather than the generated prefab.

For content that should not be committed to this repository, add `RemoteGltfRayTracingAsset` to an empty child of a `GameManager`, set its absolute HTTPS `.glb` or `.gltf` `Url`, and enter Play mode. It downloads and instantiates the asset through glTFast, resolving a `.gltf` file's relative `.bin` and image URLs from the model URL, then applies the same `RayMaterial` conversion and registration as editor-imported assets. Base-color textures support glTFast's `baseColorTexture` property as well as the built-in pipeline names. Imported camera nodes are removed from the instantiated hierarchy; their first pose/FOV is retained through `HasImportedCameraPose`, `ImportedCameraPosition`, `ImportedCameraRotation`, and `ImportedCameraFieldOfView` for a scene controller to apply to the project's render camera. The downloaded content exists only in memory, so a connection is required each time the scene starts. Use versioned, CORS-accessible URLs you control or are licensed to redistribute. Prefer a self-contained `.glb` when possible; it is one request and does not depend on relative asset paths.

When `RemoteGltfRayTracingAsset.UseDiskCache` is enabled (the default), self-contained `.glb` assets download once into `Application.persistentDataPath/RemoteGltfCache` and later loads use the URL-hashed local file first. `LoadedFromCache` reports which path was used, and `ClearCachedAsset()` removes the cached file for the current URL. `.gltf` files are not cached because their external buffers and images require a dependency manifest; use `.glb` for persistent offline-ready remote content.

`Tools > Ray Tracing > Generate Teapot Material Scene` creates `Assets/Scenes/Generated/TeapotMaterials.unity`. It uses both meshes under `Assets/Models/Teapot`, applies the original RenderMan swatch albedo, metallic-roughness, and normal maps from `Assets/Textures/RenderManSwatch`, and places six material variants over `Assets/checkerboard.png`. Existing generated output is not overwritten by the menu command.

The scene’s `Directional Light` is a Unity light and is not used by the compute shader lighting model.

## Physics

Several spheres have `Rigidbody` and `SphereCollider`. Unity physics can move them. Each rendered frame, `GameManager.UpdateSpheres()` reads object transforms and uploads updated positions/radii/materials/lights to the GPU.

This means the ray tracer can render dynamic physics-driven spheres even though it is not using Unity’s standard rendering path for the final image.

Ray mesh primitives have `MeshCollider` but no `Rigidbody` by default. Unity treats those colliders as static obstacles, so dynamic spheres can collide with them without making the meshes dynamic physics bodies.
