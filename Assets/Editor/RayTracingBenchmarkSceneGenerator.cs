using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RayTracingBenchmarkSceneGenerator
{
    private const string BenchmarkSceneFolder = "Assets/Scenes/Benchmarks";
    private const string GeneratedAssetFolder = "Assets/Scenes/Benchmarks/GeneratedAssets";
    private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
    private const string SkyboxPath = "Assets/skyboxOcean.jpg";
    private const string WolfensteinTextureAtlasPath = "Assets/wolf3d_textures.png";
    private const int WolfensteinTextureTileSize = 64;

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
        CreateGlassTransmissionScene();
        CreateSparseScene();
        CreateDynamicScene();
        CreateWaterScene();
        CreateGlassOfWaterPencilScene();
        CreateCornellBoxScene();
        CreateWolfensteinScene();

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
        if (ShouldSkipExistingScene("Benchmark_ManySpheres"))
        {
            return;
        }

        var context = CreateBaseScene("Benchmark_ManySpheres", new Vector3(0.0f, 7.0f, -24.0f), new Vector3(15.0f, 0.0f, 0.0f));
        context.Manager.numberOfPasses = 1;
        context.Manager.enableFrameAccumulation = true;
        context.Manager.numBounces = 6;
        context.Manager.shadowBvhMinObjectCount = 1024;
        context.Manager.shadowQuality = 1;
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
        if (ShouldSkipExistingScene("Benchmark_ShadowBlockers"))
        {
            return;
        }

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
        if (ShouldSkipExistingScene("Benchmark_ManyLights"))
        {
            return;
        }

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
        if (ShouldSkipExistingScene("Benchmark_DenseMesh"))
        {
            return;
        }

        var context = CreateBaseScene("Benchmark_DenseMesh", new Vector3(0.0f, 7.0f, -18.0f), new Vector3(16.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(-3.0f, 12.0f, -5.0f), 1.7f, new Color32(255, 238, 218, 255));
        AddRayMesh(context.Root, "Dense Terrain Mesh", CreateGridMesh("Dense Terrain", 180, 180, 0.11f), new Vector3(-9.9f, 0.15f, -2.0f), Vector3.zero, Vector3.one, new Color32(170, 185, 160, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        Save(context.Scene, "Benchmark_DenseMesh");
    }

    private static void CreateManyMeshesScene()
    {
        if (ShouldSkipExistingScene("Benchmark_ManyMeshes"))
        {
            return;
        }

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
        if (ShouldSkipExistingScene("Benchmark_Glass"))
        {
            return;
        }

        var context = CreateBaseScene("Benchmark_Glass", new Vector3(0.0f, 5.5f, -16.0f), new Vector3(12.0f, 0.0f, 0.0f), passes: 1, bounces: 8, shadowQuality: 1);
        context.Manager.enableFrameAccumulation = true;
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

    private static void CreateGlassTransmissionScene()
    {
        const string sceneName = "Benchmark_GlassTransmission";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(sceneName, new Vector3(0.0f, 5.8f, -13.5f), new Vector3(16.0f, 0.0f, 0.0f), passes: 1, bounces: 10, shadowQuality: 3);
        context.Manager.enableFrameAccumulation = true;
        context.Manager.lightFalloffScale = 0.035f;
        context.Manager._skyboxLightColor = new Color32(18, 18, 22, 255);
        context.Manager.groundSmoothness = 0.12f;
        context.Manager.shadowBvhMinObjectCount = 0;

        AddLight(context.Root, "White Transmission Light", new Vector3(0.0f, 3.5f, -4.9f), 0.65f, Color.white);
        //AddLight(context.Root, "Warm Side Reference Light", new Vector3(-5.6f, 4.8f, -2.2f), 0.38f, new Color32(255, 206, 145, 255));
        //AddLight(context.Root, "Blue Side Reference Light", new Vector3(5.6f, 4.8f, -2.2f), 0.38f, new Color32(95, 145, 255, 255));

        AddPrimitiveMesh(context.Root, "Receiver Back Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 2.0f, 5.2f), Vector3.zero, new Vector3(13.0f, 4.0f, 0.08f), new Color32(230, 230, 225, 255), RayMaterial.MaterialType.Diffuse, 0.05f, 1.0f);
        AddPrimitiveMesh(context.Root, "Receiver Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, 1.6f), Vector3.zero, new Vector3(13.0f, 0.04f, 11.0f), new Color32(215, 213, 205, 255), RayMaterial.MaterialType.Diffuse, 0.08f, 1.0f);

        AddTransmissionFilterStack(context.Root, "Clear Reference", -5.4f, new Color32[0], 0.35f, 1.0f);
        AddTransmissionFilterStack(context.Root, "Blue Single Layer", -3.6f, new[] { new Color32(55, 105, 255, 255) }, 0.35f, 1.0f);
        AddTransmissionFilterStack(context.Root, "Yellow Single Layer", -1.8f, new[] { new Color32(255, 235, 50, 255) }, 0.35f, 1.0f);
        AddTransmissionFilterStack(context.Root, "Blue Then Yellow", 0.0f, new[] { new Color32(55, 105, 255, 255), new Color32(255, 235, 50, 255) }, 0.35f, 1.0f);
        AddTransmissionFilterStack(context.Root, "Yellow Then Blue", 1.8f, new[] { new Color32(255, 235, 50, 255), new Color32(55, 105, 255, 255) }, 0.35f, 1.0f);
        AddTransmissionFilterStack(context.Root, "Red Green Blue Stack", 3.6f, new[] { new Color32(255, 60, 45, 255), new Color32(55, 220, 75, 255), new Color32(55, 105, 255, 255) }, 0.35f, 1.0f);

        AddPrimitiveMesh(context.Root, "Thick Blue Glass Block", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(5.4f, 2.0f, 0.0f), Vector3.zero, new Vector3(1.05f, 2.6f, 1.35f), new Color32(55, 105, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.28f, 1.5f);
        AddPrimitiveMesh(context.Root, "Thin Blue Glass Plate", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(5.4f, 2.0f, -1.35f), Vector3.zero, new Vector3(1.05f, 2.6f, 0.14f), new Color32(55, 105, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.28f, 1.5f);

        AddSphere(context.Root, "Cyan Glass Sphere Shadow Test", new Vector3(-3.85f, 1.05f, 2.15f), 0.75f, new Color32(45, 255, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.32f, 1.5f);
        AddSphere(context.Root, "Yellow Glass Sphere Shadow Test", new Vector3(-2.9f, 1.05f, 2.15f), 0.75f, new Color32(255, 255, 55, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.25f, 1.5f);

        Save(context.Scene, sceneName);
    }

    private static void AddTransmissionFilterStack(Transform parent, string name, float x, Color32[] colors, float opacity, float refraction)
    {
        if (colors.Length == 0)
        {
            AddPrimitiveMesh(parent, name + " Receiver Marker", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(x, 1.0f, 4.95f), Vector3.zero, new Vector3(1.05f, 1.8f, 0.08f), new Color32(245, 245, 240, 255), RayMaterial.MaterialType.Diffuse, 0.02f, 1.0f);
            return;
        }

        for (int i = 0; i < colors.Length; i++)
        {
            float z = -0.55f + i * 0.42f;
            AddPrimitiveMesh(parent, $"{name} Filter {i + 1}", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(x, 2.0f, z), Vector3.zero, new Vector3(1.05f, 2.8f, 0.12f), colors[i], RayMaterial.MaterialType.Glass, 1.0f, opacity, refraction);
        }
    }

    private static void CreateSparseScene()
    {
        if (ShouldSkipExistingScene("Benchmark_Sparse"))
        {
            return;
        }

        var context = CreateBaseScene("Benchmark_Sparse", new Vector3(0.0f, 4.0f, -12.0f), new Vector3(10.0f, 0.0f, 0.0f));
        AddLight(context.Root, "Key Light", new Vector3(-2.0f, 8.0f, -2.0f), 1.0f, new Color32(255, 240, 220, 255));
        AddSphere(context.Root, "Single Sphere", new Vector3(0.0f, 1.0f, 3.0f), 1.0f, new Color32(210, 80, 75, 255), RayMaterial.MaterialType.Metal, 0.9f);
        Save(context.Scene, "Benchmark_Sparse");
    }

    private static void CreateDynamicScene()
    {
        if (ShouldSkipExistingScene("Benchmark_Dynamic"))
        {
            return;
        }

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

    private static void CreateWaterScene()
    {
        const string sceneName = "Benchmark_Water";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(sceneName, new Vector3(0.0f, 0.95f, -14.0f), new Vector3(1.5f, 0.0f, 0.0f), passes: 2, bounces: 5, shadowQuality: 2);
        context.Manager.cameraFocalDistance = 18.0f;
        context.Manager.groundSmoothness = 0.72f;
        context.Manager.lightFalloffScale = 0.045f;
        context.Manager.exposure = 1.15f;
        context.Manager.topLevelBvhMinObjectCount = 0;
        context.Manager.shadowBvhMinObjectCount = 0;
        context.Manager.enableWater = true;
        context.Manager.waterCenter = new Vector3(0.0f, 0.85f, 5.0f);
        context.Manager.waterSize = new Vector2(32.0f, 36.0f);
        context.Manager.waterColor = new Color32(36, 110, 132, 255);
        context.Manager.waterSmoothness = 0.97f;
        context.Manager.waterOpacity = 0.16f;
        context.Manager.waterRefractionIndex = 1.333f;
        context.Manager.waterWaveAmplitude = 0.32f;
        context.Manager.waterWaveScale = 0.7f;
        context.Manager.waterWaveSpeed = 0.85f;
        context.Manager.waterMarchSteps = 36;
        context.Manager.waterRefinementSteps = 6;

        AddLight(context.Root, "Low Sun Reflection Light", new Vector3(-5.0f, 4.0f, -5.5f), 1.2f, new Color32(255, 226, 188, 255));
        AddLight(context.Root, "Cool Sky Fill", new Vector3(8.0f, 7.5f, 8.0f), 1.8f, new Color32(120, 170, 255, 255));

        AddRayMesh(context.Root, "Lake Bed", CreateHorizontalQuadMesh("Lake Bed", 32.0f, 36.0f, 4.0f, 4.0f), new Vector3(0.0f, 0.04f, 5.0f), Vector3.zero, Vector3.one, new Color32(72, 63, 42, 255), RayMaterial.MaterialType.Diffuse, 0.35f);

        for (int i = 0; i < 24; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 24.0f;
            float radiusX = 13.5f + (i % 3) * 0.65f;
            float radiusZ = 15.0f + (i % 4) * 0.55f;
            float x = Mathf.Cos(angle) * radiusX;
            float z = 5.0f + Mathf.Sin(angle) * radiusZ;
            float stoneRadius = 0.25f + 0.18f * Mathf.PerlinNoise(i * 0.37f, 2.1f);
            AddSphere(context.Root, "Shore Rock", new Vector3(x, 0.18f + stoneRadius, z), stoneRadius, new Color32(98, 96, 88, 255), RayMaterial.MaterialType.Diffuse, 0.42f);
        }

        for (int i = 0; i < 18; i++)
        {
            float x = -11.0f + i * 1.3f;
            float z = 0.5f + Mathf.Sin(i * 1.7f) * 5.5f;
            float y = 0.22f + (i % 4) * 0.08f;
            var color = Color.HSVToRGB(0.08f + i * 0.012f, 0.45f, 0.55f);
            AddSphere(context.Root, "Underwater Pebble", new Vector3(x, y, z + 5.0f), 0.25f + (i % 3) * 0.08f, color, RayMaterial.MaterialType.Diffuse, 0.25f);
        }

        AddSphere(context.Root, "Half Submerged Red Marker", new Vector3(-3.2f, 0.95f, 1.2f), 0.62f, new Color32(220, 65, 45, 255), RayMaterial.MaterialType.Metal, 0.72f);
        AddSphere(context.Root, "Underwater Blue Marker", new Vector3(2.7f, 0.42f, 3.2f), 0.58f, new Color32(50, 120, 235, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        AddSphere(context.Root, "Far Reflection Sphere", new Vector3(5.4f, 1.25f, 13.0f), 0.95f, new Color32(230, 222, 196, 255), RayMaterial.MaterialType.Metal, 0.86f);

        AddPrimitiveMesh(context.Root, "Left Dock Post", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-5.2f, 1.2f, -1.5f), Vector3.zero, new Vector3(0.28f, 2.4f, 0.28f), new Color32(96, 62, 34, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Dock Post", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-3.8f, 1.2f, -1.5f), Vector3.zero, new Vector3(0.28f, 2.4f, 0.28f), new Color32(96, 62, 34, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);
        AddPrimitiveMesh(context.Root, "Dock Plank", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-4.5f, 1.55f, -1.5f), Vector3.zero, new Vector3(2.2f, 0.18f, 1.0f), new Color32(120, 78, 42, 255), RayMaterial.MaterialType.Diffuse, 0.45f, 1.0f);

        Save(context.Scene, sceneName);
    }

    private static void CreateGlassOfWaterPencilScene()
    {
        const string sceneName = "Benchmark_GlassWaterPencil";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(sceneName, new Vector3(0.0f, 2.75f, -7.2f), new Vector3(6.0f, 0.0f, 0.0f), passes: 4, bounces: 8, shadowQuality: 4);
        context.Manager.numberOfPasses = 1;
        context.Manager.numBounces = 4;
        context.Manager.shadowQuality = 1;
        context.Manager.cameraFocalDistance = 7.5f;
        context.Manager.groundSmoothness = 0.28f;
        context.Manager.lightFalloffScale = 0.08f;
        context.Manager.exposure = 1.0f;
        context.Manager.topLevelBvhMinObjectCount = 0;
        context.Manager.shadowBvhMinObjectCount = 1024;
        context.Manager.lightSamplingStrategy = GameManager.LightSamplingStrategy.ImportanceSampled;
        context.Manager.lightSampleCount = 1;
        context.Manager._skyboxLightColor = new Color32(72, 76, 82, 255);
        context.Manager.enableWater = false;

        AddLight(context.Root, "Large Softbox", new Vector3(-3.5f, 5.6f, -3.8f), 1.6f, new Color32(255, 250, 236, 255));
        AddLight(context.Root, "Rim Highlight", new Vector3(3.5f, 3.7f, -2.2f), 0.55f, new Color32(210, 230, 255, 255));

        var tumblerRoot = new GameObject("Glass Tumbler");
        tumblerRoot.transform.SetParent(context.Root, false);
        tumblerRoot.transform.localPosition = Vector3.zero;
        tumblerRoot.transform.localRotation = Quaternion.identity;
        tumblerRoot.transform.localScale = Vector3.one;

        AddRayMesh(tumblerRoot.transform, "Glass Wall", CreateOpenCylinderMesh("Glass Wall", 96, 1.36f, 3.05f, 0.055f), new Vector3(0.0f, 1.56f, 0.0f), Vector3.zero, Vector3.one, new Color32(212, 238, 245, 255), RayMaterial.MaterialType.Glass, 0.98f, 0.18f, 1.83f);
        AddRayMesh(tumblerRoot.transform, "Water Volume", CreateCylinderMesh("Water Volume", 96, 1.24f, 1.86f), new Vector3(0.0f, 1.17f, 0.0f), Vector3.zero, Vector3.one, new Color32(190, 226, 238, 255), RayMaterial.MaterialType.Glass, 0.99f, 0.08f, 2.2f);
        AddRayMesh(tumblerRoot.transform, "Top Rim", CreateTorusMesh("Top Rim", 96, 12, 1.36f, 0.055f), new Vector3(0.0f, 3.09f, 0.0f), Vector3.zero, Vector3.one, new Color32(220, 244, 250, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.16f, 1.52f);

        var pencilRoot = new GameObject("Tilted Pencil");
        pencilRoot.transform.SetParent(context.Root, false);
        pencilRoot.transform.localPosition = new Vector3(-2.5f, 1.4f, 0.0f);
        pencilRoot.transform.localEulerAngles = new Vector3(0.17f, 0.0f, -55.0f);
        pencilRoot.transform.localScale = Vector3.one;

        AddRayMesh(pencilRoot.transform, "Red Pencil Cylinder", CreateHorizontalCylinderMesh("Red Pencil Cylinder", 10, 0.14f, 5.7f), new Vector3(0.1f, 2.25f, 0.0f), Vector3.zero, Vector3.one, new Color32(174, 28, 36, 255), RayMaterial.MaterialType.Diffuse, 0.52f);

        Save(context.Scene, sceneName);
    }

    private static void CreateCornellBoxScene()
    {
        const string sceneName = "Benchmark_CornellBox";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(sceneName, new Vector3(0.0f, 2.15f, -6.4f), Vector3.zero, passes: 4, bounces: 6, shadowQuality: 4);
        context.Manager.cameraFocalDistance = 8.5f;
        context.Manager.groundSmoothness = 0.15f;
        context.Manager.lightFalloffScale = 0.02f;
        context.Manager.topLevelBvhMinObjectCount = 0;
        context.Manager.shadowBvhMinObjectCount = 0;
        context.Manager._skyboxLightColor = new Color32(0, 0, 0, 255);

        AddPrimitiveMesh(context.Root, "Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, 3.0f), Vector3.zero, new Vector3(6.0f, 0.04f, 6.0f), new Color32(230, 226, 218, 255), RayMaterial.MaterialType.Diffuse, 0.2f, 1.0f);
        AddPrimitiveMesh(context.Root, "Ceiling", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 4.5f, 3.0f), Vector3.zero, new Vector3(6.0f, 0.04f, 6.0f), new Color32(226, 226, 220, 255), RayMaterial.MaterialType.Diffuse, 0.15f, 1.0f);
        AddPrimitiveMesh(context.Root, "Back Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 2.25f, 6.0f), Vector3.zero, new Vector3(6.0f, 4.5f, 0.04f), new Color32(224, 222, 214, 255), RayMaterial.MaterialType.Diffuse, 0.15f, 1.0f);
        AddPrimitiveMesh(context.Root, "Left Red Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-3.0f, 2.25f, 3.0f), Vector3.zero, new Vector3(0.04f, 4.5f, 6.0f), new Color32(255, 0, 0, 255), RayMaterial.MaterialType.Diffuse, 0.05f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Green Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(3.0f, 2.25f, 3.0f), Vector3.zero, new Vector3(0.04f, 4.5f, 6.0f), new Color32(0, 255, 0, 255), RayMaterial.MaterialType.Diffuse, 0.05f, 1.0f);

        AddRayMesh(context.Root, "Ceiling Light Disc", CreateDiscMesh("Ceiling Light Disc", 72), new Vector3(0.0f, 4.46f, 2.15f), Vector3.zero, new Vector3(1.65f, 1.0f, 0.42f), new Color32(255, 255, 245, 255), RayMaterial.MaterialType.Diffuse, 0.0f);
        AddLight(context.Root, "Ceiling Area Light", new Vector3(0.0f, 4.18f, 2.15f), 0.85f, new Color32(255, 250, 230, 255));

        AddSphere(context.Root, "Reflective Sphere", new Vector3(-1.75f, 0.82f, 2.4f), 0.8f, new Color32(235, 232, 224, 255), RayMaterial.MaterialType.Metal, 0.65f);
        AddPrimitiveMesh(context.Root, "Tall Box", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(1.25f, 1.45f, 3.65f), Vector3.zero, new Vector3(1.45f, 2.9f, 1.45f), new Color32(212, 205, 195, 255), RayMaterial.MaterialType.Diffuse, 0.12f, 1.0f);

        Save(context.Scene, sceneName);
    }

    private static void CreateWolfensteinScene()
    {
        const string sceneName = "Benchmark_Wolfenstein";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var wallTexture = GetOrCreateWolfensteinWallTexture();
        var context = CreateBaseScene(sceneName, new Vector3(0.0f, 1.35f, -4.8f), new Vector3(2.0f, 0.0f, 0.0f), passes: 4, bounces: 4, shadowQuality: 3);
        context.Manager.cameraFocalDistance = 10.0f;
        context.Manager.groundSmoothness = 0.25f;
        context.Manager.lightFalloffScale = 0.035f;
        context.Manager.exposure = 1.25f;
        context.Manager._skyboxLightColor = new Color32(8, 8, 8, 255);
        context.Manager.topLevelBvhMinObjectCount = 0;
        context.Manager.shadowBvhMinObjectCount = 0;

        AddRayMesh(context.Root, "Back Stone Wall", CreateQuadMesh("Back Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(0.0f, 1.5f, 7.0f), Vector3.zero, Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, wallTexture);
        AddRayMesh(context.Root, "Left Stone Wall", CreateQuadMesh("Left Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(-6.0f, 1.5f, 1.0f), new Vector3(0.0f, 90.0f, 0.0f), Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, wallTexture);
        AddRayMesh(context.Root, "Right Stone Wall", CreateQuadMesh("Right Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(6.0f, 1.5f, 1.0f), new Vector3(0.0f, -90.0f, 0.0f), Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, wallTexture);
        AddRayMesh(context.Root, "Floor", CreateHorizontalQuadMesh("Floor", 12.0f, 12.0f, 3.0f, 3.0f), new Vector3(0.0f, 0.002f, 1.0f), Vector3.zero, Vector3.one, new Color32(78, 68, 48, 255), RayMaterial.MaterialType.Diffuse, 0.28f);
        AddRayMesh(context.Root, "Ceiling", CreateHorizontalQuadMesh("Ceiling", 12.0f, 12.0f, 3.0f, 3.0f), new Vector3(0.0f, 3.0f, 1.0f), new Vector3(180.0f, 0.0f, 0.0f), Vector3.one, new Color32(92, 78, 54, 255), RayMaterial.MaterialType.Diffuse, 0.2f);

        AddLight(context.Root, "Bright Wall Light", new Vector3(1.9f, 0.75f, 5.35f), 0.42f, new Color32(255, 245, 190, 255));
        AddLight(context.Root, "Small Warm Light", new Vector3(-0.35f, 1.1f, 5.85f), 0.35f, new Color32(255, 238, 178, 255));
        AddLight(context.Root, "Ceiling Fill", new Vector3(0.0f, 2.75f, 1.5f), 0.7f, new Color32(170, 135, 85, 255));

        AddSphere(context.Root, "Large Center Sphere", new Vector3(-1.2f, 0.85f, 2.85f), 0.85f, new Color32(150, 146, 105, 255), RayMaterial.MaterialType.Diffuse, 0.4f);
        AddSphere(context.Root, "Cyan Sphere", new Vector3(-2.65f, 0.72f, 2.45f), 0.72f, new Color32(32, 128, 135, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        AddSphere(context.Root, "Orange Right Sphere", new Vector3(3.9f, 0.72f, 3.7f), 0.72f, new Color32(215, 93, 0, 255), RayMaterial.MaterialType.Diffuse, 0.3f);
        AddSphere(context.Root, "Blue Left Sphere", new Vector3(-4.15f, 0.65f, 1.15f), 0.65f, new Color32(0, 56, 78, 255), RayMaterial.MaterialType.Diffuse, 0.25f);
        AddSphere(context.Root, "Brown Right Sphere", new Vector3(1.75f, 0.62f, 1.35f), 0.62f, new Color32(116, 80, 34, 255), RayMaterial.MaterialType.Diffuse, 0.25f);
        AddSphere(context.Root, "Foreground Green Sphere", new Vector3(-0.8f, 0.92f, -0.8f), 0.92f, new Color32(26, 68, 56, 255), RayMaterial.MaterialType.Diffuse, 0.25f);
        AddSphere(context.Root, "Foreground Yellow Sphere", new Vector3(4.85f, 1.25f, -1.4f), 1.25f, new Color32(120, 112, 54, 255), RayMaterial.MaterialType.Diffuse, 0.2f);
        AddSphere(context.Root, "Foreground Red Sphere", new Vector3(-5.0f, 1.0f, -2.05f), 1.0f, new Color32(105, 0, 25, 255), RayMaterial.MaterialType.Diffuse, 0.2f);

        Save(context.Scene, sceneName);
    }

    private static GameObject AddSphere(Transform parent, string name, Vector3 position, float radius, Color color, RayMaterial.MaterialType type, float smoothness, float opacity = 1.0f, float refraction = 1.0f)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localRotation = Quaternion.identity;
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
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale = Vector3.one;

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
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localEulerAngles = euler;
        obj.transform.localScale = scale;

        var material = obj.AddComponent<RayMaterial>();

        obj.AddComponent<MeshFilter>();
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<MeshCollider>();
        var primitive = obj.AddComponent<RayMeshPrimitive>();

        // Adding RayMeshPrimitive in the editor can invoke Reset(), which assigns preview defaults
        // to the RayMaterial. Apply benchmark material values after that so the compute renderer
        // receives the intended colors and material settings.
        material.Type = type;
        material.Color = color;
        material.Smoothness = smoothness;
        material.Opacity = opacity;
        material.RefractionIndex = refraction;

        primitive.Type = primitiveType;
        primitive.EnsureMesh();
        return obj;
    }

    private static GameObject AddRayMesh(Transform parent, string name, Mesh mesh, Vector3 position, Vector3 euler, Vector3 scale, Color color, RayMaterial.MaterialType type, float smoothness, float opacity = 1.0f, float refraction = 1.0f, Texture2D albedoTexture = null)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localEulerAngles = euler;
        obj.transform.localScale = scale;

        var material = obj.AddComponent<RayMaterial>();
        material.Type = type;
        material.Color = color;
        material.AlbedoTexture = albedoTexture;
        material.Smoothness = smoothness;
        material.Opacity = opacity;
        material.RefractionIndex = refraction;

        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<RayTracingObject>();
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

    private static Mesh CreateQuadMesh(string name, float width, float height, float uScale, float vScale)
    {
        var mesh = new Mesh
        {
            name = name,
            vertices = new[]
            {
                new Vector3(-width * 0.5f, -height * 0.5f, 0.0f),
                new Vector3(width * 0.5f, -height * 0.5f, 0.0f),
                new Vector3(width * 0.5f, height * 0.5f, 0.0f),
                new Vector3(-width * 0.5f, height * 0.5f, 0.0f)
            },
            uv = new[]
            {
                new Vector2(0.0f, 0.0f),
                new Vector2(uScale, 0.0f),
                new Vector2(uScale, vScale),
                new Vector2(0.0f, vScale)
            },
            triangles = new[] { 0, 2, 1, 0, 3, 2 }
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh CreateHorizontalQuadMesh(string name, float width, float depth, float uScale, float vScale)
    {
        var mesh = new Mesh
        {
            name = name,
            vertices = new[]
            {
                new Vector3(-width * 0.5f, 0.0f, -depth * 0.5f),
                new Vector3(width * 0.5f, 0.0f, -depth * 0.5f),
                new Vector3(width * 0.5f, 0.0f, depth * 0.5f),
                new Vector3(-width * 0.5f, 0.0f, depth * 0.5f)
            },
            uv = new[]
            {
                new Vector2(0.0f, 0.0f),
                new Vector2(uScale, 0.0f),
                new Vector2(uScale, vScale),
                new Vector2(0.0f, vScale)
            },
            triangles = new[] { 0, 1, 2, 0, 2, 3 }
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh CreateDiscMesh(string name, int segments)
    {
        segments = Mathf.Max(3, segments);
        var vertices = new Vector3[segments + 1];
        var triangles = new int[segments * 3];
        vertices[0] = Vector3.zero;

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2.0f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
        }

        for (int i = 0; i < segments; i++)
        {
            int triangleIndex = i * 3;
            triangles[triangleIndex] = 0;
            triangles[triangleIndex + 1] = i + 1;
            triangles[triangleIndex + 2] = ((i + 1) % segments) + 1;
        }

        var mesh = new Mesh
        {
            name = name,
            vertices = vertices,
            triangles = triangles
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh CreateCylinderMesh(string name, int segments, float radius, float height)
    {
        segments = Mathf.Max(3, segments);
        var vertices = new Vector3[segments * 2 + 2];
        var triangles = new int[segments * 12];
        float halfHeight = height * 0.5f;

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2.0f / segments;
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;
            vertices[i] = new Vector3(x, -halfHeight, z);
            vertices[i + segments] = new Vector3(x, halfHeight, z);
        }

        int bottomCenter = segments * 2;
        int topCenter = bottomCenter + 1;
        vertices[bottomCenter] = new Vector3(0.0f, -halfHeight, 0.0f);
        vertices[topCenter] = new Vector3(0.0f, halfHeight, 0.0f);

        int triangleIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            triangles[triangleIndex++] = i;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next + segments;

            triangles[triangleIndex++] = bottomCenter;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = i;

            triangles[triangleIndex++] = topCenter;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next + segments;
        }

        return CreateMesh(name, vertices, triangles);
    }

    private static Mesh CreateHorizontalCylinderMesh(string name, int segments, float radius, float length)
    {
        segments = Mathf.Max(3, segments);
        var vertices = new Vector3[segments * 2 + 2];
        var triangles = new int[segments * 12];
        float halfLength = length * 0.5f;

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2.0f / segments;
            float y = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;
            vertices[i] = new Vector3(-halfLength, y, z);
            vertices[i + segments] = new Vector3(halfLength, y, z);
        }

        int leftCenter = segments * 2;
        int rightCenter = leftCenter + 1;
        vertices[leftCenter] = new Vector3(-halfLength, 0.0f, 0.0f);
        vertices[rightCenter] = new Vector3(halfLength, 0.0f, 0.0f);

        int triangleIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            triangles[triangleIndex++] = i;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next + segments;

            triangles[triangleIndex++] = leftCenter;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = i;

            triangles[triangleIndex++] = rightCenter;
            triangles[triangleIndex++] = i + segments;
            triangles[triangleIndex++] = next + segments;
        }

        return CreateMesh(name, vertices, triangles);
    }

    private static Mesh CreateOpenCylinderMesh(string name, int segments, float radius, float height, float thickness)
    {
        segments = Mathf.Max(3, segments);
        float innerRadius = Mathf.Max(0.01f, radius - Mathf.Max(0.001f, thickness));
        float halfHeight = height * 0.5f;
        var vertices = new Vector3[segments * 4];
        var triangles = new int[segments * 12];

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2.0f / segments;
            var outer = new Vector3(Mathf.Cos(angle) * radius, 0.0f, Mathf.Sin(angle) * radius);
            var inner = new Vector3(Mathf.Cos(angle) * innerRadius, 0.0f, Mathf.Sin(angle) * innerRadius);
            vertices[i] = new Vector3(outer.x, -halfHeight, outer.z);
            vertices[i + segments] = new Vector3(outer.x, halfHeight, outer.z);
            vertices[i + segments * 2] = new Vector3(inner.x, -halfHeight, inner.z);
            vertices[i + segments * 3] = new Vector3(inner.x, halfHeight, inner.z);
        }

        int triangleIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int outerBottom = i;
            int outerTop = i + segments;
            int innerBottom = i + segments * 2;
            int innerTop = i + segments * 3;
            int nextOuterBottom = next;
            int nextOuterTop = next + segments;
            int nextInnerBottom = next + segments * 2;
            int nextInnerTop = next + segments * 3;

            triangles[triangleIndex++] = outerBottom;
            triangles[triangleIndex++] = outerTop;
            triangles[triangleIndex++] = nextOuterBottom;
            triangles[triangleIndex++] = nextOuterBottom;
            triangles[triangleIndex++] = outerTop;
            triangles[triangleIndex++] = nextOuterTop;

            triangles[triangleIndex++] = innerBottom;
            triangles[triangleIndex++] = nextInnerBottom;
            triangles[triangleIndex++] = innerTop;
            triangles[triangleIndex++] = innerTop;
            triangles[triangleIndex++] = nextInnerBottom;
            triangles[triangleIndex++] = nextInnerTop;
        }

        return CreateMesh(name, vertices, triangles);
    }

    private static Mesh CreateConeMesh(string name, int segments, float radius, float height)
    {
        segments = Mathf.Max(3, segments);
        var vertices = new Vector3[segments + 2];
        var triangles = new int[segments * 6];
        float halfHeight = height * 0.5f;
        vertices[segments] = new Vector3(0.0f, halfHeight, 0.0f);
        vertices[segments + 1] = new Vector3(0.0f, -halfHeight, 0.0f);

        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2.0f / segments;
            vertices[i] = new Vector3(Mathf.Cos(angle) * radius, -halfHeight, Mathf.Sin(angle) * radius);
        }

        int triangleIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            triangles[triangleIndex++] = i;
            triangles[triangleIndex++] = segments;
            triangles[triangleIndex++] = next;

            triangles[triangleIndex++] = segments + 1;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = i;
        }

        return CreateMesh(name, vertices, triangles);
    }

    private static Mesh CreateTorusMesh(string name, int majorSegments, int minorSegments, float majorRadius, float minorRadius)
    {
        majorSegments = Mathf.Max(3, majorSegments);
        minorSegments = Mathf.Max(3, minorSegments);
        var vertices = new Vector3[majorSegments * minorSegments];
        var triangles = new int[majorSegments * minorSegments * 6];

        for (int i = 0; i < majorSegments; i++)
        {
            float majorAngle = i * Mathf.PI * 2.0f / majorSegments;
            var radial = new Vector3(Mathf.Cos(majorAngle), 0.0f, Mathf.Sin(majorAngle));
            for (int j = 0; j < minorSegments; j++)
            {
                float minorAngle = j * Mathf.PI * 2.0f / minorSegments;
                float ringRadius = majorRadius + Mathf.Cos(minorAngle) * minorRadius;
                float y = Mathf.Sin(minorAngle) * minorRadius;
                vertices[i * minorSegments + j] = new Vector3(radial.x * ringRadius, y, radial.z * ringRadius);
            }
        }

        int triangleIndex = 0;
        for (int i = 0; i < majorSegments; i++)
        {
            int nextI = (i + 1) % majorSegments;
            for (int j = 0; j < minorSegments; j++)
            {
                int nextJ = (j + 1) % minorSegments;
                int a = i * minorSegments + j;
                int b = nextI * minorSegments + j;
                int c = i * minorSegments + nextJ;
                int d = nextI * minorSegments + nextJ;
                triangles[triangleIndex++] = a;
                triangles[triangleIndex++] = b;
                triangles[triangleIndex++] = c;
                triangles[triangleIndex++] = c;
                triangles[triangleIndex++] = b;
                triangles[triangleIndex++] = d;
            }
        }

        return CreateMesh(name, vertices, triangles);
    }

    private static Mesh CreateMesh(string name, Vector3[] vertices, int[] triangles)
    {
        var mesh = new Mesh
        {
            name = name,
            indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            vertices = vertices,
            triangles = triangles
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Texture2D GetOrCreateWolfensteinWallTexture()
    {
        Directory.CreateDirectory(GeneratedAssetFolder);
        const string texturePath = GeneratedAssetFolder + "/WolfensteinWall.png";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (existing != null)
        {
            return existing;
        }

        var texture = CreateWolfensteinWallTexture();
        File.WriteAllBytes(texturePath, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(texturePath);

        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer != null)
        {
            importer.isReadable = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
    }

    private static Texture2D CreateWolfensteinWallTexture()
    {
        var atlasTexture = LoadReadableTexture(WolfensteinTextureAtlasPath);
        if (atlasTexture != null)
        {
            return ExtractTopLeftTextureTile(atlasTexture, WolfensteinTextureTileSize, "Wolfenstein Wall");
        }

        const int size = 128;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Wolfenstein Wall",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        var darkMortar = new Color32(28, 24, 18, 255);
        var shadow = new Color32(60, 50, 34, 255);
        var mid = new Color32(126, 111, 75, 255);
        var light = new Color32(205, 181, 116, 255);
        var highlight = new Color32(245, 221, 145, 255);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                texture.SetPixel(x, y, mid);
            }
        }

        FillRect(texture, 0, 0, size, size, shadow);
        DrawBrick(texture, 3, 6, 23, 42, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 31, 5, 31, 18, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 69, 6, 24, 42, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 99, 5, 26, 18, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 31, 30, 32, 20, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 98, 30, 27, 20, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 4, 56, 32, 18, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 42, 56, 21, 42, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 70, 56, 32, 18, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 106, 56, 18, 42, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 5, 82, 31, 40, darkMortar, shadow, mid, light, highlight);
        DrawBrick(texture, 70, 82, 32, 40, darkMortar, shadow, mid, light, highlight);

        texture.Apply(false, false);
        return texture;
    }

    private static Texture2D LoadReadableTexture(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        if (!texture.LoadImage(File.ReadAllBytes(path), false))
        {
            Object.DestroyImmediate(texture);
            return null;
        }

        return texture;
    }

    private static Texture2D ExtractTopLeftTextureTile(Texture2D source, int tileSize, string textureName)
    {
        int size = Mathf.Min(tileSize, source.width, source.height);
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = textureName,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        int sourceY = source.height - size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                texture.SetPixel(x, y, source.GetPixel(x, sourceY + y));
            }
        }

        texture.Apply(false, false);
        Object.DestroyImmediate(source);
        return texture;
    }

    private static void DrawBrick(Texture2D texture, int x, int y, int width, int height, Color32 mortar, Color32 shadow, Color32 mid, Color32 light, Color32 highlight)
    {
        FillRect(texture, x - 2, y - 2, width + 4, height + 4, mortar);
        FillRect(texture, x, y, width, height, mid);
        FillRect(texture, x, y, width, 3, shadow);
        FillRect(texture, x, y, 3, height, shadow);
        FillRect(texture, x + width - 3, y + 2, 3, height - 2, light);
        FillRect(texture, x + 2, y + height - 3, width - 2, 3, light);
        FillRect(texture, x + width / 5, y + height / 4, Mathf.Max(3, width / 2), Mathf.Max(3, height / 4), highlight);

        for (int py = y + 4; py < y + height - 4; py += 4)
        {
            for (int px = x + 4; px < x + width - 4; px += 4)
            {
                int hash = (px * 37 + py * 17 + width * 13 + height * 7) & 3;
                Color32 speckle = hash == 0 ? light : hash == 1 ? shadow : mid;
                FillRect(texture, px, py, 2, 2, speckle);
            }
        }
    }

    private static void FillRect(Texture2D texture, int x, int y, int width, int height, Color32 color)
    {
        int minX = Mathf.Clamp(x, 0, texture.width);
        int minY = Mathf.Clamp(y, 0, texture.height);
        int maxX = Mathf.Clamp(x + width, 0, texture.width);
        int maxY = Mathf.Clamp(y + height, 0, texture.height);

        for (int py = minY; py < maxY; py++)
        {
            for (int px = minX; px < maxX; px++)
            {
                texture.SetPixel(px, py, color);
            }
        }
    }

    private static void Save(Scene scene, string sceneName)
    {
        string path = GetScenePath(sceneName);
        if (File.Exists(path))
        {
            Debug.LogWarning($"Skipping save for existing benchmark scene: {path}");
            return;
        }

        EditorSceneManager.SaveScene(scene, path);
    }

    private static bool ShouldSkipExistingScene(string sceneName)
    {
        string path = GetScenePath(sceneName);
        if (!File.Exists(path))
        {
            return false;
        }

        Debug.Log($"Skipping existing benchmark scene: {path}");
        return true;
    }

    private static string GetScenePath(string sceneName)
    {
        return $"{BenchmarkSceneFolder}/{sceneName}.unity";
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
