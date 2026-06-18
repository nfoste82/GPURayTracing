using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RayTracingBenchmarkSceneGenerator
{
    private const string BenchmarkSceneFolder = "Assets/Scenes/Benchmarks";
    private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
    private const string SkyboxPath = "Assets/skyboxOcean.jpg";

    [MenuItem("Tools/Ray Tracing/Generate Benchmark Scenes")]
    public static void GenerateBenchmarkScenes()
    {
        Directory.CreateDirectory(BenchmarkSceneFolder);

        CreateManySpheresScene();
        CreateShadowBlockersScene();
        CreateManyLightsScene();
        CreateDenseMeshScene();
        CreateManyMeshesScene();
        CreateGlassScene();
        CreateSparseScene();
        CreateDynamicScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static BenchmarkContext CreateBaseScene(string sceneName, Vector3 cameraPosition, Vector3 cameraEuler, int passes = 1, int bounces = 3, int shadowQuality = 2)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = sceneName;

        var cameraObject = new GameObject("Ray Tracing Camera");
        cameraObject.transform.position = cameraPosition;
        cameraObject.transform.eulerAngles = cameraEuler;
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 60.0f;
        camera.clearFlags = CameraClearFlags.Skybox;
        cameraObject.AddComponent<AudioListener>();

        var managerObject = new GameObject("Game Manager");
        var manager = managerObject.AddComponent<GameManager>();
        manager.shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
        manager.renderTextureCamera = camera;
        manager.numberOfPasses = passes;
        manager.numBounces = bounces;
        manager.shadowQuality = shadowQuality;
        manager.shadowRandomness = 0.65f;
        manager.randomNoise = false;
        manager.cameraAutoFocus = false;
        manager.cameraFocalDistance = 18.0f;
        manager.groundSmoothness = 0.5f;
        manager.lightFalloffScale = 0.08f;
        manager.topLevelBvhMinObjectCount = 64;
        manager.shadowBvhMinObjectCount = 64;
        manager.skyboxTexture = AssetDatabase.LoadAssetAtPath<Texture>(SkyboxPath);
        manager._skyboxLightColor = new Color32(95, 95, 105, 255);

        var renderer = cameraObject.AddComponent<RayTracingCameraRenderer>();
        renderer.GameManager = manager;

        var overlay = cameraObject.AddComponent<RayTracingBenchmarkOverlay>();
        overlay.gameManager = manager;

        return new BenchmarkContext(scene, managerObject.transform);
    }

    private static void CreateManySpheresScene()
    {
        var context = CreateBaseScene("Benchmark_ManySpheres", new Vector3(0.0f, 7.0f, -24.0f), new Vector3(15.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(0.0f, 13.0f, -4.0f), 1.8f, new Color32(255, 235, 210, 255));

        const int gridX = 24;
        const int gridZ = 16;
        for (int z = 0; z < gridZ; z++)
        {
            for (int x = 0; x < gridX; x++)
            {
                float px = (x - (gridX - 1) * 0.5f) * 1.15f;
                float pz = z * 1.15f - 2.0f;
                float height = 0.45f + 0.35f * Mathf.PerlinNoise(x * 0.19f, z * 0.31f);
                var color = Color.HSVToRGB((x + z * 0.07f) / gridX, 0.55f, 0.9f);
                AddSphere(context.Root, "Sphere", new Vector3(px, height, pz), 0.42f, color, RayMaterial.MaterialType.Diffuse, 0.25f);
            }
        }

        Save(context.Scene, "Benchmark_ManySpheres");
    }

    private static void CreateShadowBlockersScene()
    {
        var context = CreateBaseScene("Benchmark_ShadowBlockers", new Vector3(0.0f, 8.0f, -22.0f), new Vector3(18.0f, 0.0f, 0.0f), shadowQuality: 5);
        context.Manager.topLevelBvhMinObjectCount = 64;
        context.Manager.shadowBvhMinObjectCount = 0;
        AddLight(context.Root, "Wide Light", new Vector3(0.0f, 12.0f, -6.0f), 2.4f, new Color32(255, 240, 220, 255));

        for (int z = 0; z < 9; z++)
        {
            for (int x = 0; x < 18; x++)
            {
                float px = (x - 8.5f) * 1.25f;
                float pz = z * 1.45f - 1.0f;
                float radius = 0.25f + 0.18f * Mathf.PerlinNoise(x * 0.3f, z * 0.6f);
                AddSphere(context.Root, "Shadow Blocker", new Vector3(px, 1.0f + radius, pz), radius, new Color32(165, 170, 180, 255), RayMaterial.MaterialType.Diffuse, 0.2f);
            }
        }

        Save(context.Scene, "Benchmark_ShadowBlockers");
    }

    private static void CreateManyLightsScene()
    {
        var context = CreateBaseScene("Benchmark_ManyLights", new Vector3(0.0f, 6.5f, -20.0f), new Vector3(15.0f, 0.0f, 0.0f), shadowQuality: 1);

        for (int i = 0; i < 72; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 72.0f;
            float radius = 7.0f + (i % 3) * 1.6f;
            var color = Color.HSVToRGB(i / 72.0f, 0.45f, 1.0f);
            AddLight(context.Root, "Light", new Vector3(Mathf.Cos(angle) * radius, 4.0f + (i % 5) * 0.7f, Mathf.Sin(angle) * radius + 5.0f), 0.28f, color);
        }

        for (int i = 0; i < 40; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 40.0f;
            AddSphere(context.Root, "Receiver Sphere", new Vector3(Mathf.Cos(angle) * 4.2f, 0.6f, Mathf.Sin(angle) * 4.2f + 5.0f), 0.55f, new Color32(185, 185, 190, 255), RayMaterial.MaterialType.Diffuse, 0.1f);
        }

        Save(context.Scene, "Benchmark_ManyLights");
    }

    private static void CreateDenseMeshScene()
    {
        var context = CreateBaseScene("Benchmark_DenseMesh", new Vector3(0.0f, 7.0f, -18.0f), new Vector3(16.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(-3.0f, 12.0f, -5.0f), 1.7f, new Color32(255, 238, 218, 255));
        AddRayMesh(context.Root, "Dense Terrain Mesh", CreateGridMesh("Dense Terrain", 180, 180, 0.11f), new Vector3(-9.9f, 0.15f, -2.0f), Vector3.zero, Vector3.one, new Color32(170, 185, 160, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        Save(context.Scene, "Benchmark_DenseMesh");
    }

    private static void CreateManyMeshesScene()
    {
        var context = CreateBaseScene("Benchmark_ManyMeshes", new Vector3(0.0f, 8.0f, -22.0f), new Vector3(18.0f, 0.0f, 0.0f));
        context.Manager.topLevelBvhMinObjectCount = 0;
        AddLight(context.Root, "Key Light", new Vector3(0.0f, 14.0f, -5.0f), 2.0f, new Color32(255, 238, 218, 255));

        for (int z = 0; z < 10; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                var primitive = (RayMeshPrimitive.PrimitiveType)((x + z) % 3);
                var color = Color.HSVToRGB((x + z * 0.2f) / 16.0f, 0.45f, 0.9f);
                AddPrimitiveMesh(context.Root, "Mesh Object", primitive, new Vector3((x - 7.5f) * 1.35f, 0.75f, z * 1.35f), new Vector3(0.0f, x * 17.0f + z * 9.0f, 0.0f), Vector3.one * 0.9f, color, RayMaterial.MaterialType.Metal, 0.75f, 1.0f);
            }
        }

        Save(context.Scene, "Benchmark_ManyMeshes");
    }

    private static void CreateGlassScene()
    {
        var context = CreateBaseScene("Benchmark_Glass", new Vector3(0.0f, 5.5f, -16.0f), new Vector3(12.0f, 0.0f, 0.0f), passes: 2, bounces: 5, shadowQuality: 3);
        AddLight(context.Root, "Key Light", new Vector3(-3.0f, 9.0f, -4.0f), 1.5f, new Color32(255, 235, 220, 255));
        AddLight(context.Root, "Blue Light", new Vector3(4.0f, 5.5f, 4.0f), 0.8f, new Color32(110, 165, 255, 255));

        for (int i = 0; i < 28; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 28.0f;
            float radius = 4.0f + (i % 4) * 0.45f;
            var color = Color.HSVToRGB(i / 28.0f, 0.32f, 1.0f);
            AddSphere(context.Root, "Glass Sphere", new Vector3(Mathf.Cos(angle) * radius, 0.95f, Mathf.Sin(angle) * radius + 3.0f), 0.8f, color, RayMaterial.MaterialType.Glass, 1.0f, 0.35f, 1.5f);
        }

        AddPrimitiveMesh(context.Root, "Glass Pyramid", RayMeshPrimitive.PrimitiveType.Pyramid, new Vector3(0.0f, 1.4f, 3.0f), new Vector3(0.0f, 45.0f, 0.0f), Vector3.one * 2.2f, new Color32(180, 215, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.4f, 1.65f);
        Save(context.Scene, "Benchmark_Glass");
    }

    private static void CreateSparseScene()
    {
        var context = CreateBaseScene("Benchmark_Sparse", new Vector3(0.0f, 4.0f, -12.0f), new Vector3(10.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(-2.0f, 8.0f, -2.0f), 1.0f, new Color32(255, 240, 220, 255));
        AddSphere(context.Root, "Single Sphere", new Vector3(0.0f, 1.0f, 3.0f), 1.0f, new Color32(210, 80, 75, 255), RayMaterial.MaterialType.Metal, 0.9f);
        Save(context.Scene, "Benchmark_Sparse");
    }

    private static void CreateDynamicScene()
    {
        var context = CreateBaseScene("Benchmark_Dynamic", new Vector3(0.0f, 7.0f, -22.0f), new Vector3(15.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(0.0f, 12.0f, -5.0f), 1.7f, new Color32(255, 238, 218, 255));

        for (int i = 0; i < 96; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 96.0f;
            float ring = 4.0f + (i % 4) * 1.6f;
            var sphere = AddSphere(context.Root, "Moving Sphere", new Vector3(Mathf.Cos(angle) * ring, 1.0f + (i % 5) * 0.25f, Mathf.Sin(angle) * ring + 5.0f), 0.45f, Color.HSVToRGB(i / 96.0f, 0.55f, 0.95f), RayMaterial.MaterialType.Diffuse, 0.2f);
            var mover = sphere.AddComponent<BenchmarkOrbitMover>();
            mover.center = new Vector3(0.0f, sphere.transform.position.y, 5.0f);
            mover.radius = ring;
            mover.angularSpeed = 8.0f + (i % 7) * 3.0f;
            mover.phaseDegrees = i * 360.0f / 96.0f;
            mover.verticalAmplitude = 0.2f + (i % 3) * 0.1f;
        }

        Save(context.Scene, "Benchmark_Dynamic");
    }

    private static GameObject AddSphere(Transform parent, string name, Vector3 position, float radius, Color color, RayMaterial.MaterialType type, float smoothness, float opacity = 1.0f, float refraction = 1.0f)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;
        obj.transform.localScale = Vector3.one;

        var collider = obj.AddComponent<SphereCollider>();
        collider.radius = radius;

        var material = obj.AddComponent<RayMaterial>();
        material.Type = type;
        material.Color = color;
        material.Smoothness = smoothness;
        material.Opacity = opacity;
        material.RefractionIndex = refraction;

        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static GameObject AddLight(Transform parent, string name, Vector3 position, float radius, Color color)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;

        var collider = obj.AddComponent<SphereCollider>();
        collider.radius = radius;

        var light = obj.AddComponent<RayLight>();
        light.Color = color;

        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static GameObject AddPrimitiveMesh(Transform parent, string name, RayMeshPrimitive.PrimitiveType primitiveType, Vector3 position, Vector3 euler, Vector3 scale, Color color, RayMaterial.MaterialType type, float smoothness, float opacity, float refraction = 1.0f)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;
        obj.transform.eulerAngles = euler;
        obj.transform.localScale = scale;

        var material = obj.AddComponent<RayMaterial>();
        material.Type = type;
        material.Color = color;
        material.Smoothness = smoothness;
        material.Opacity = opacity;
        material.RefractionIndex = refraction;

        obj.AddComponent<MeshFilter>();
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<MeshCollider>();
        var primitive = obj.AddComponent<RayMeshPrimitive>();
        primitive.Type = primitiveType;
        primitive.EnsureMesh();
        return obj;
    }

    private static GameObject AddRayMesh(Transform parent, string name, Mesh mesh, Vector3 position, Vector3 euler, Vector3 scale, Color color, RayMaterial.MaterialType type, float smoothness)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.position = position;
        obj.transform.eulerAngles = euler;
        obj.transform.localScale = scale;

        var material = obj.AddComponent<RayMaterial>();
        material.Type = type;
        material.Color = color;
        material.Smoothness = smoothness;
        material.Opacity = 1.0f;
        material.RefractionIndex = 1.0f;

        obj.AddComponent<RayTracingObject>();
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>();
        return obj;
    }

    private static Mesh CreateGridMesh(string name, int xSegments, int zSegments, float spacing)
    {
        var vertices = new Vector3[(xSegments + 1) * (zSegments + 1)];
        var triangles = new int[xSegments * zSegments * 6];

        for (int z = 0; z <= zSegments; z++)
        {
            for (int x = 0; x <= xSegments; x++)
            {
                float height = Mathf.PerlinNoise(x * 0.07f, z * 0.07f) * 1.4f + Mathf.PerlinNoise(x * 0.17f, z * 0.13f) * 0.45f;
                vertices[z * (xSegments + 1) + x] = new Vector3(x * spacing, height, z * spacing);
            }
        }

        int triangleIndex = 0;
        for (int z = 0; z < zSegments; z++)
        {
            for (int x = 0; x < xSegments; x++)
            {
                int a = z * (xSegments + 1) + x;
                int b = a + 1;
                int c = a + xSegments + 1;
                int d = c + 1;

                triangles[triangleIndex++] = a;
                triangles[triangleIndex++] = c;
                triangles[triangleIndex++] = b;
                triangles[triangleIndex++] = b;
                triangles[triangleIndex++] = c;
                triangles[triangleIndex++] = d;
            }
        }

        var mesh = new Mesh
        {
            name = name,
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            vertices = vertices,
            triangles = triangles
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void Save(Scene scene, string sceneName)
    {
        EditorSceneManager.SaveScene(scene, $"{BenchmarkSceneFolder}/{sceneName}.unity");
    }

    private readonly struct BenchmarkContext
    {
        public readonly Scene Scene;
        public readonly Transform Root;
        public readonly GameManager Manager;

        public BenchmarkContext(Scene scene, Transform root)
        {
            Scene = scene;
            Root = root;
            Manager = root.GetComponent<GameManager>();
        }
    }
}
