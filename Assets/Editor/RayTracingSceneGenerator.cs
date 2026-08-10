using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class RayTracingSceneGenerator
{
    private const string GeneratedSceneFolder = "Assets/Scenes/Generated";
    private const string GeneratedAssetFolder = "Assets/Scenes/Generated/GeneratedAssets";
    private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
    private const string SkyboxPath = "Assets/Textures/Skyboxes/skyboxOcean.jpg";
    private const string StanfordDragonModelPath = "Assets/Models/Dragon/stanford-dragon-pbr.fbx";
    private const string WolfensteinTextureAtlasPath = "Assets/wolf3d_textures.png";
    private const string TeapotBaseModelPath = "Assets/Models/Teapot/Mesh000.obj";
    private const string TeapotBodyModelPath = "Assets/Models/Teapot/Mesh001.obj";
    private const string DefaultCheckerGrayTexturePath = "Default-Checker-Gray.png";
    private const string RenderManTextureFolder = "Assets/Textures/RenderManSwatch";
    private const string TerrainPreviewMaterialPath = GeneratedAssetFolder + "/TerrainPreview.mat";
    private static readonly string[] TerrainTexturePaths =
    {
        "Assets/Textures/Terrain/dirt_floor_diff_2k.jpg",
        "Assets/Textures/Terrain/sparse_grass_diff_2k.jpg",
        "Assets/Textures/Terrain/rock_05_diff_2k.jpg",
        "Assets/Textures/Terrain/rock_boulder_dry_diff_2k.jpg"
    };
    private static readonly float[] TerrainTextureTileSizes = { 7.0f, 9.0f, 6.0f, 8.0f };
    private const int WolfensteinTextureTileSize = 64;
    private static HashSet<string> _requestedScenePaths;
    private static bool _overwriteExistingScenes;

    [MenuItem("Tools/Ray Tracing/Generate Scenes")]
    public static void GenerateScenes()
    {
        GenerateScenes(null, false);
    }

    /// <summary>
    /// Regenerates every generated scene, overwriting whatever is already on disk.
    /// Use this after changing generator code: plain "Generate Scenes" skips scenes that already
    /// exist, so edits appear to have no effect until the scene file is deleted by hand.
    /// </summary>
    [MenuItem("Tools/Ray Tracing/Regenerate Scenes (Overwrite All)")]
    public static void RegenerateScenes()
    {
        bool confirmed = EditorUtility.DisplayDialog(
            "Regenerate All Generated Scenes",
            $"This overwrites every scene in {GeneratedSceneFolder} and its generated assets.\n\n" +
            "Hand edits to those scenes will be lost. Continue?",
            "Regenerate",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        GenerateScenes(null, true);
        Debug.Log($"Regenerated all scenes in {GeneratedSceneFolder}.");
    }

    [MenuItem("Tools/Ray Tracing/Regenerate Terrain Scene")]
    public static void RegenerateTerrainScene()
    {
        GenerateScenes(new[] { GetScenePath("Terrain") }, true);
    }

    public static void GenerateScenes(IReadOnlyList<string> scenePaths, bool overwriteExistingScenes)
    {
        Directory.CreateDirectory(GeneratedSceneFolder);
        _requestedScenePaths = scenePaths == null ? null : new HashSet<string>(scenePaths, StringComparer.OrdinalIgnoreCase);
        _overwriteExistingScenes = overwriteExistingScenes;

        try
        {
            CreateManySpheresScene();
            CreateShadowBlockersScene();
            CreateManyLightsScene();
            CreateManyMeshesScene();
            CreateGlassScene();
            CreateGlassTransmissionScene();
            CreateCausticsScene();
            CreateTriangleLightCausticsScene();
            CreateDynamicScene();
            CreateWaterScene();
            CreateGlassOfWaterPencilScene();
            CreateCornellBoxScene();
            CreateDemofoxGlossyReflectionsScene();
            CreateDemofoxRefractionIndexScene();
            CreateDemofoxRoughRefractionScene();
            CreateDemofoxAbsorptionScene();
            CreateDragonCornellBoxScene();
            CreateWolfensteinScene();
            CreateVolumetricFogScene();
            CreateTerrainScene();
            CreateTeapotMaterialScene();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            _requestedScenePaths = null;
            _overwriteExistingScenes = false;
        }
    }

    [MenuItem("Tools/Ray Tracing/Generate Teapot Material Scene")]
    public static void CreateTeapotMaterialScene()
    {
        const string sceneName = "Benchmark_TeapotMaterials";
        Directory.CreateDirectory(GeneratedSceneFolder);
        Directory.CreateDirectory(GeneratedAssetFolder);
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        EnsureReadableModel(TeapotBaseModelPath);
        EnsureReadableModel(TeapotBodyModelPath);
        
        var baseMesh = LoadFirstMeshFromAsset(TeapotBaseModelPath);
        var bodyMesh = LoadFirstMeshFromAsset(TeapotBodyModelPath);
        
        if (baseMesh == null || bodyMesh == null)
        {
            Debug.LogError("Teapot material scene requires both Assets/Models/Teapot OBJ files and Assets/checkerboard.png.");
            return;
        }

        var tilesAlbedo = LoadRenderManTexture("tiles_base.png", false);
        var tilesMetalRough = LoadRenderManTexture("tiles_mat_rgh.png", true);
        var tilesNormal = LoadRenderManTexture("tiles_normal.png", true);
        var marbleAlbedo = LoadRenderManTexture("marble_base.png", false);
        var marbleMetalRough = LoadRenderManTexture("marble_mat_rgh.png", true);
        var scratchesAlbedo = LoadRenderManTexture("scratches_base.png", false);
        var scratchesMetalRough = LoadRenderManTexture("scratches_mat_rgh.png", true);
        var scratchesNormal = LoadRenderManTexture("scratches_normal.png", true);
        var stripedAlbedo = LoadRenderManTexture("striped_base.png", false);
        var stripedMetalRough = LoadRenderManTexture("striped_mat_rgh.png", true);
        var stripedNormal = LoadRenderManTexture("striped_normal.png", true);
        var goldAlbedo = LoadRenderManTexture("gold_base.png", false);
        var goldMetalRough = LoadRenderManTexture("gold_mat_rgh.png", true);
        var goldNormal = LoadRenderManTexture("gold_normal.png", true);

        if (tilesAlbedo == null || tilesMetalRough == null || tilesNormal == null
            || marbleAlbedo == null || marbleMetalRough == null
            || scratchesAlbedo == null || scratchesMetalRough == null || scratchesNormal == null
            || stripedAlbedo == null || stripedMetalRough == null || stripedNormal == null
            || goldAlbedo == null || goldMetalRough == null || goldNormal == null)
        {
            Debug.LogError($"Teapot material scene requires the RenderMan swatch textures under {RenderManTextureFolder}.");
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName,
            CameraPosition = new Vector3(-0.1f, 15f, -36.17f),
            CameraEuler = new Vector3(20.8f, 0.0f, 0.0f),
            NumBounces = 12,
            ShadowQuality = 0,
            CameraApertureMode = GameManager.CameraApertureMode.Pinhole,
            Exposure = 2.5f,
            LightFalloffScale = 0.03f,
            SkyboxLightColor = new Color32(18, 18, 18, 255),
            TopLevelBvhMinObjectCount = 0,
            ShadowBvhMinObjectCount = 0,
            FieldOfView = 10.7f,
            DirectionalLightIntensity = 0.4f,
            DirectionalLightAngularRadius = 8.43f,
        });
        
        var defaultCheckerGray = AssetDatabase.GetBuiltinExtraResource<Texture2D>(DefaultCheckerGrayTexturePath);

        AddRayMesh(context.Root, "Checkerboard Floor", 
            CreateHorizontalQuadMesh("Teapot Checkerboard Floor", 22.0f, 20.0f, 6.0f, 6.0f), 
            new Vector3(0f, 0f, 3.35f), Vector3.zero, new Vector3(1f, 1f, 1.5f), 
            Color.white, RayMaterial.MaterialType.Diffuse, 
            0.2f, 1.0f, 1.0f, albedoTexture: defaultCheckerGray);
        
        AddRayMesh(context.Root, "Checkerboard Back Wall", 
            CreateHorizontalQuadMesh("Teapot Checkerboard Floor", 22.0f, 20.0f, 6.0f, 6.0f), 
            new Vector3(0f, 0f, 30f), new Vector3(90.0f, 0.0f, 0.0f), new Vector3(5.0f, 5.0f, 5.0f), 
            Color.white, RayMaterial.MaterialType.Diffuse, 
            0.2f, 1.0f, 1.0f, albedoTexture: defaultCheckerGray);
        
        AddLight(context.Root, "Front Left Fill", new Vector3(-23.25f, 17.2f, -8.7f), 3f, new Color32(225, 247, 255, 255), 2f);
        AddLight(context.Root, "Back Light", new Vector3(0.73f, 3.47f, 23.4f), 1.5f, new Color32(255, 250, 235, 255), 2f);
        AddLight(context.Root, "Front Right Fill", new Vector3(12.75f, 10f, -14.54f), 2.5f, new Color32(220, 234, 235, 255), 1.5f);

        AddTeapot(context.Root, "Blue Tiles", bodyMesh, baseMesh, new Vector3(-4.2f, 0.02f, 4.8f), Color.white, RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f, tilesAlbedo, tilesMetalRough, tilesNormal);
        AddTeapot(context.Root, "Marble", bodyMesh, baseMesh, new Vector3(0.0f, 0.02f, 4.8f), Color.white, RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f, marbleAlbedo, marbleMetalRough);
        AddTeapot(context.Root, "Blue Scratched", bodyMesh, baseMesh, new Vector3(4.2f, 0.02f, 4.8f), Color.white, RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f, scratchesAlbedo, scratchesMetalRough, scratchesNormal);
        AddTeapot(context.Root, "Striped Chrome", bodyMesh, baseMesh, new Vector3(-4.2f, 0.02f, -3.63f), Color.white, RayMaterial.MaterialType.Metal, 0.0f, 1.0f, stripedAlbedo, stripedMetalRough, stripedNormal);
        AddTeapot(context.Root, "Teal Glass", bodyMesh, baseMesh, new Vector3(0.0f, 0.02f, -3.63f), new Color32(0, 221, 159, 255), RayMaterial.MaterialType.Glass, 0.854f, 0.0f, null, null, null, 0.25f, 1.5f);
        AddTeapot(context.Root, "Gold Circles", bodyMesh, baseMesh, new Vector3(4.2f, 0.02f, -3.63f), new Color32(209, 136, 3, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f, goldAlbedo, goldMetalRough, goldNormal);

        Save(context.Scene, sceneName);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static BenchmarkContext CreateBaseScene(SceneSettings settings)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = settings.SceneName;

        var cameraObject = new GameObject("Ray Tracing Camera");
        
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = settings.FieldOfView;
        camera.clearFlags = CameraClearFlags.Skybox;
        
        cameraObject.transform.position = settings.CameraPosition;
        cameraObject.transform.eulerAngles = settings.CameraEuler;
        
        cameraObject.AddComponent<AudioListener>();

        var managerObject = new GameObject("Game Manager");
        var manager = managerObject.AddComponent<GameManager>();
        manager.shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
        manager.renderTextureCamera = camera;
        manager.InitSceneSettings(settings);
        manager.skyboxTexture = AssetDatabase.LoadAssetAtPath<Texture>(SkyboxPath);

        var renderer = cameraObject.AddComponent<RayTracingCameraRenderer>();
        renderer.GameManager = manager;

        AddDirectionalLight(managerObject.transform, "Directional Light", settings.DirectionalLightRotation, new Color32(255, 244, 222, 255), settings.DirectionalLightIntensity, settings.DirectionalLightAngularRadius);

        return new BenchmarkContext(scene, managerObject.transform);
    }

    private static void CreateVolumetricFogScene()
    {
        const string sceneName = "Benchmark_VolumetricFog";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(0.0f, 3.17f, -11.27f), CameraEuler = new Vector3(8.23f, 0.0f, 0.0f),
            LightSamplingStrategy = GameManager.LightSamplingStrategy.AllLights,
            LightFalloffScale = 0.041f, ShadowRandomness = 0.6f, Exposure = 0.75f,
            FogDensityScale = 0.74f, FogScatteringScale = 0.466f, FogInScatteringIntensity = 12.77f,
            SkyboxLightColor = new Color32(0, 0, 0, 255), CameraFocalDistance = 15.0f, FireflyClamp = 8f
        });

        const int slatCount = 9;
        const float slatSpacing = 1.45f;
        const float slatWidth = 0.72f;
        const float slatDepth = 17.25f;

        AddFloor(context.Root, new Vector2(0.0f, 2.5f), new Vector2(35.0f, 500.0f), 0.12f, "Matte Floor");
        
        AddMeshLight(
            context.Root,
            "Rectangular Ceiling Light",
            CreateHorizontalQuadMesh("Rectangular Ceiling Light", (slatCount - 1) * slatSpacing + slatWidth, slatDepth, 1.0f, 1.0f),
            new Vector3(0.0f, 20.7f, 3.5f),
            Vector3.zero,
            new Vector3(0.15f, 1.0f, 1.0f),
            new Color32(255, 255, 255, 255));

        for (int i = 0; i < slatCount; i++)
        {
            float x = (i - (slatCount - 1) * 0.5f) * slatSpacing;
            AddPrimitiveMesh(
                context.Root,
                $"Ceiling Slat {i + 1}",
                RayMeshPrimitive.PrimitiveType.Cube,
                new Vector3(x, 8.75f, 3.5f),
                Vector3.zero,
                new Vector3(slatWidth, 0.55f, slatDepth),
                Color.black,
                RayMaterial.MaterialType.Diffuse,
                0.56f,
                1.0f);
        }
        
        // Add left wall
        AddPrimitiveMesh(context.Root, $"Left Wall",
            RayMeshPrimitive.PrimitiveType.Cube,
            new Vector3(-12.0f, 8.75f, 3.5f), Vector3.zero, new Vector3(1f, 40f, 100f),
            Color.black,
            RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f);
        
        // Add right wall
        AddPrimitiveMesh(context.Root, $"Right Wall",
            RayMeshPrimitive.PrimitiveType.Cube,
            new Vector3(12.0f, 8.75f, 3.5f), Vector3.zero, new Vector3(1f, 40f, 100f),
            Color.black,
            RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f);

        var fogObject = new GameObject("Homogeneous Fog Volume");
        fogObject.transform.SetParent(context.Root, false);
        
        var fog = fogObject.AddComponent<FogVolume>();
        fog.Density = 0.029f;
        fog.ScatteringAlbedo = Color.white;
        
        fogObject.transform.localPosition = new Vector3(0.0f, 4.6f, 3.5f);
        fogObject.transform.localScale = new Vector3(14.0f, 30.0f, 22.0f);

        Save(context.Scene, sceneName);
    }

    private static void CreateManySpheresScene()
    {
        if (ShouldSkipExistingScene("Benchmark_ManySpheres"))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_ManySpheres", CameraPosition = new Vector3(0.0f, 7.0f, -24.0f), CameraEuler = new Vector3(15.0f, 0.0f, 0.0f),
            NumBounces = 6, ShadowBvhMinObjectCount = 1024, ShadowQuality = 1
        });
        AddLight(context.Root, "Key Light", new Vector3(0.0f, 13.0f, -4.0f), 1.8f, new Color32(255, 235, 210, 255));
        AddFloor(context.Root, new Vector2(0.0f, 6.0f), new Vector2(32.0f, 28.0f), 0.5f);

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

    private static void CreateTerrainScene()
    {
        const string sceneName = "Terrain";
        const int seed = 481516;
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        Directory.CreateDirectory(GeneratedAssetFolder);
        TerrainData terrainData = CreateSeededTerrainData(seed);
        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName,
            CameraPosition = new Vector3(0.0f, 13.3f, -0.42f),
            CameraEuler = new Vector3(10.85f, 0.0f, 0.0f),
            NumBounces = 5,
            ShadowQuality = 0,
            LightFalloffScale = 0.02f,
            Exposure = 1.0f,
            TopLevelBvhMinObjectCount = 0,
            ShadowBvhMinObjectCount = 0,
            CameraMovementSpeed = 3.0f,
            SkyboxLightColor = new Color32(220, 225, 235, 255)
        });

        var terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        terrainObject.name = "Seeded Ray Tracing Terrain";
        terrainObject.transform.SetParent(context.Root, false);
        terrainObject.transform.localPosition = new Vector3(-500.0f, 0.0f, -80.0f);
        
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.drawHeightmap = true;
        terrain.materialTemplate = GetOrCreateTerrainPreviewMaterial();
        terrain.GetComponent<TerrainCollider>().enabled = false;
        
        var rayTracingTerrain = terrainObject.AddComponent<RayTracingTerrain>();
        rayTracingTerrain.Terrain = terrain;
        rayTracingTerrain.Seed = seed;
        rayTracingTerrain.AccelerationResolution = 32;
        rayTracingTerrain.MarchSteps = 12;
        rayTracingTerrain.RefinementSteps = 5;
        
        Save(context.Scene, sceneName);
    }

    private static TerrainData CreateSeededTerrainData(int seed)
    {
        EditorUtility.DisplayProgressBar("Generating Terrain", "Building 1 km heightfield", 0.05f);
        try
        {
        const string path = GeneratedAssetFolder + "/SeededTerrain.asset";
        TerrainData terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
        if (terrainData == null)
        {
            terrainData = new TerrainData();
            AssetDatabase.CreateAsset(terrainData, path);
        }

        const int resolution = 257;
        terrainData.heightmapResolution = resolution;
        // One terrain unit represents one metre: the heightmap covers one square kilometre
        // and uses a 100 m vertical range, representative of a rolling mountain valley.
        terrainData.size = new Vector3(1000.0f, 100.0f, 1000.0f);
        var heights = new float[resolution, resolution];
        var random = new System.Random(seed);
        float offsetX = (float)random.NextDouble() * 64.0f;
        float offsetZ = (float)random.NextDouble() * 64.0f;
        Vector2 massifA = new Vector2(0.20f, 0.68f);
        Vector2 massifB = new Vector2(0.77f, 0.72f);
        Vector2 massifC = new Vector2(0.62f, 0.30f);
        float valleyOffset = Mathf.Lerp(-0.08f, 0.08f, (float)random.NextDouble());
        for (int z = 0; z < resolution; z++)
        {
            if ((z & 15) == 0)
            {
                EditorUtility.DisplayProgressBar("Generating Terrain", "Building natural landforms", 0.05f + 0.60f * z / (resolution - 1));
            }
            for (int x = 0; x < resolution; x++)
            {
                float nx = (float)x / (resolution - 1);
                float nz = (float)z / (resolution - 1);
                float warpX = FractalNoise(offsetX + nx * 2.0f, offsetZ + nz * 2.0f, 3, 2.0f, 0.5f) - 0.5f;
                float warpZ = FractalNoise(offsetX + 17.0f + nx * 2.0f, offsetZ + 31.0f + nz * 2.0f, 3, 2.0f, 0.5f) - 0.5f;
                float warpedX = nx + warpX * 0.16f;
                float warpedZ = nz + warpZ * 0.16f;
                float continental = FractalNoise(offsetX + warpedX * 1.15f, offsetZ + warpedZ * 1.15f, 4, 2.0f, 0.52f);
                float foothills = FractalNoise(offsetX + 13.0f + warpedX * 4.0f, offsetZ + 7.0f + warpedZ * 4.0f, 4, 2.05f, 0.5f);
                float detail = FractalNoise(offsetX + 29.0f + warpedX * 13.0f, offsetZ + 41.0f + warpedZ * 13.0f, 3, 2.2f, 0.48f);
                float ridges = RidgedNoise(offsetX + 47.0f + warpedX * 4.8f, offsetZ + 19.0f + warpedZ * 4.8f, 4);
                float mountainMask = Mathf.Clamp01((continental - 0.43f) / 0.36f);
                float massifs = Gaussian(warpedX, warpedZ, massifA, 0.13f, 0.34f)
                    + Gaussian(warpedX, warpedZ, massifB, 0.16f, 0.29f)
                    + Gaussian(warpedX, warpedZ, massifC, 0.11f, 0.20f);

                // A gently meandering lowland joins open terrain instead of terminating in a circular depression.
                float valleyCenter = 0.48f + valleyOffset + Mathf.Sin(warpedZ * Mathf.PI * 1.3f) * 0.14f + warpX * 0.08f;
                float valleyDistance = Mathf.Abs(warpedX - valleyCenter);
                float valley = (1.0f - Mathf.SmoothStep(0.035f, 0.20f, valleyDistance)) * Mathf.SmoothStep(0.02f, 0.15f, warpedZ);
                float openBasin = Gaussian(warpedX, warpedZ, new Vector2(0.30f, 0.24f), 0.20f, 1.0f);
                float edgeDistance = Mathf.Min(Mathf.Min(nx, 1.0f - nx), Mathf.Min(nz, 1.0f - nz));
                float edge = Mathf.SmoothStep(0.0f, 0.08f, edgeDistance);
                float height = 0.075f + continental * 0.11f + foothills * 0.055f + detail * 0.018f;
                height += massifs + ridges * mountainMask * 0.16f;
                height -= valley * 0.19f + openBasin * 0.055f;
                heights[z, x] = Mathf.Clamp01(Mathf.Lerp(0.035f, height, edge));
            }
        }
        NormalizeTerrainHeights(heights, 0.02f, 0.98f);
        terrainData.SetHeights(0, 0, heights);

        EditorUtility.DisplayProgressBar("Generating Terrain", "Assigning terrain textures", 0.70f);
        TerrainLayer[] layers = new TerrainLayer[4];
        Color[] colors = { new Color(0.20f, 0.29f, 0.11f), new Color(0.42f, 0.55f, 0.18f), new Color(0.48f, 0.34f, 0.17f), new Color(0.80f, 0.78f, 0.68f) };
        for (int i = 0; i < layers.Length; i++)
        {
            ConfigureTerrainTexture(TerrainTexturePaths[i]);
            layers[i] = CreateTerrainLayer(i, colors[i], TerrainTexturePaths[i], TerrainTextureTileSizes[i]);
        }
        terrainData.terrainLayers = layers;

        EditorUtility.DisplayProgressBar("Generating Terrain", "Painting terrain materials", 0.75f);
        terrainData.alphamapResolution = 128;

        // Layer placement is declared in self-calibrating units, not raw heightmap values:
        //   * elevation as a PERCENTILE RANK of this heightmap (0.9 = highest 10% by area)
        //   * slope in real DEGREES
        // The noise chain above plus NormalizeTerrainHeights produces a strongly bottom-weighted
        // distribution (median elevation near 0.11, not 0.5), so absolute thresholds silently
        // paint almost nothing. Rank space stays correct if the height generator changes.
        // To rebalance the terrain, move these ranks; do not hand-tune height constants.
        var rules = new[]
        {
            new TerrainLayerPainter.LayerRule
            {
                LayerIndex = 0, Name = "dirt_floor",
                MinElevationRank = 0.00f, MaxElevationRank = 0.34f,
                ElevationFeatherRank = 0.10f
            },
            new TerrainLayerPainter.LayerRule
            {
                LayerIndex = 1, Name = "sparse_grass",
                MinElevationRank = 0.28f, MaxElevationRank = 0.72f,
                // Grass only holds on ground shallow enough to keep soil.
                MaxSlopeDegrees = 26.0f, SlopeFeatherDegrees = 8.0f,
                ElevationFeatherRank = 0.10f,
                NoiseInfluence = 0.25f
            },
            new TerrainLayerPainter.LayerRule
            {
                LayerIndex = 2, Name = "rock_05",
                MinElevationRank = 0.62f, MaxElevationRank = 0.94f,
                ElevationFeatherRank = 0.10f
            },
            new TerrainLayerPainter.LayerRule
            {
                LayerIndex = 3, Name = "rock_boulder_dry",
                MinElevationRank = 0.88f, MaxElevationRank = 1.00f,
                ElevationFeatherRank = 0.08f
            }
        };

        TerrainLayerPainter.PaintResult paint = TerrainLayerPainter.Paint(
            heights,
            terrainData.size,
            terrainData.alphamapResolution,
            rules,
            (u, v) => FractalNoise(offsetX + u * 5.0f, offsetZ + v * 5.0f, 3, 2.0f, 0.5f));

        TerrainLayerPainter.ValidateCoverage(paint);
        terrainData.SetAlphamaps(0, 0, paint.Weights);
        Debug.Log(TerrainLayerPainter.BuildReport(paint, "Generated Terrain Layer Coverage"));
        foreach (string warning in paint.Warnings)
        {
            Debug.LogWarning($"Terrain painting: {warning}");
        }

        EditorUtility.SetDirty(terrainData);
        return terrainData;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static float FractalNoise(float x, float z, int octaves, float lacunarity, float persistence)
    {
        float sum = 0.0f;
        float amplitude = 1.0f;
        float frequency = 1.0f;
        float amplitudeSum = 0.0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += Mathf.PerlinNoise(x * frequency, z * frequency) * amplitude;
            amplitudeSum += amplitude;
            frequency *= lacunarity;
            amplitude *= persistence;
        }
        return sum / amplitudeSum;
    }

    private static float RidgedNoise(float x, float z, int octaves)
    {
        float noise = FractalNoise(x, z, octaves, 2.0f, 0.52f);
        return Mathf.Pow(1.0f - Mathf.Abs(noise * 2.0f - 1.0f), 2.0f);
    }

    private static float Gaussian(float x, float z, Vector2 center, float radius, float amplitude)
    {
        float deltaX = x - center.x;
        float deltaZ = z - center.y;
        return amplitude * Mathf.Exp(-(deltaX * deltaX + deltaZ * deltaZ) / (2.0f * radius * radius));
    }

    private static void NormalizeTerrainHeights(float[,] heights, float minimum, float maximum)
    {
        float sourceMinimum = float.PositiveInfinity;
        float sourceMaximum = float.NegativeInfinity;
        int resolution = heights.GetLength(0);
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                sourceMinimum = Mathf.Min(sourceMinimum, heights[z, x]);
                sourceMaximum = Mathf.Max(sourceMaximum, heights[z, x]);
            }
        }

        float sourceRange = Mathf.Max(0.0001f, sourceMaximum - sourceMinimum);
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float normalized = (heights[z, x] - sourceMinimum) / sourceRange;
                heights[z, x] = Mathf.Lerp(minimum, maximum, normalized);
            }
        }
    }


    private static TerrainLayer CreateTerrainLayer(int index, Color fallbackColor, string texturePath, float tileSize)
    {
        string layerPath = GeneratedAssetFolder + $"/SeededTerrainLayer{index}.terrainlayer";
        TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, layerPath);
        }
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogWarning($"Terrain texture is missing at {texturePath}; using the generated fallback swatch.");
            texture = CreateTerrainColorTexture(index, fallbackColor);
        }
        layer.diffuseTexture = texture;
        layer.tileSize = Vector2.one * tileSize;
        EditorUtility.SetDirty(layer);
        return layer;
    }

    private static Material GetOrCreateTerrainPreviewMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(TerrainPreviewMaterialPath);
        Shader shader = Shader.Find("Nature/Terrain/RayTracingPreview");
        if (shader == null)
        {
            Debug.LogWarning("Terrain preview shader was not found; Unity Scene view will use its default terrain material.");
            return null;
        }
        if (material == null)
        {
            material = new Material(shader) { name = "Ray Tracing Terrain Preview" };
            AssetDatabase.CreateAsset(material, TerrainPreviewMaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
            EditorUtility.SetDirty(material);
        }
        return material;
    }

    private static void ConfigureTerrainTexture(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        bool changed = false;
        if (importer.wrapMode != TextureWrapMode.Repeat)
        {
            importer.wrapMode = TextureWrapMode.Repeat;
            changed = true;
        }
        if (importer.textureCompression != TextureImporterCompression.CompressedHQ)
        {
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            changed = true;
        }
        if (changed)
        {
            importer.SaveAndReimport();
        }
    }

    private static Texture2D CreateTerrainColorTexture(int index, Color color)
    {
        string texturePath = GeneratedAssetFolder + $"/SeededTerrainLayer{index}.asset";
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture != null)
        {
            AssetDatabase.DeleteAsset(texturePath);
        }
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = $"Seeded Terrain Layer {index}" };
        texture.SetPixels(new[] { color, color, color, color });
        texture.Apply(false, false);
        AssetDatabase.CreateAsset(texture, texturePath);
        return texture;
    }

    private static void CreateShadowBlockersScene()
    {
        if (ShouldSkipExistingScene("Benchmark_ShadowBlockers"))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_ShadowBlockers", CameraPosition = new Vector3(0.0f, 8.0f, -22.0f), CameraEuler = new Vector3(18.0f, 0.0f, 0.0f),
            NumBounces = 4, ShadowQuality = 0,
            ShadowBvhMinObjectCount = 1024, LightFalloffScale = 0.027f
        });
        AddLight(context.Root, "Wide Light", new Vector3(0.0f, 12.0f, -6.0f), 2.4f, new Color32(255, 240, 220, 255));
        AddFloor(context.Root, new Vector2(0.0f, 5.0f), new Vector2(26.0f, 18.0f), 0.5f);

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

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_ManyLights", 
            CameraPosition = new Vector3(-0.474f, 11.4f, -18.97f), CameraEuler = new Vector3(25.0f, 0.0f, 0.0f),
            ShadowBvhMinObjectCount = 1024, 
            LightFalloffScale = 0.12f,
            DirectionalLightIntensity = 0.0f,
            FieldOfView = 24.5f
        });
        AddFloor(context.Root, new Vector2(0.0f, 5.0f), new Vector2(24.0f, 24.0f), 0.955f);

        for (var i = 0; i < 72; i++)
        {
            var angle = i * Mathf.PI * 2.0f / 72.0f;
            var radius = 7.0f + (i % 3) * 1.6f;
            var color = Color.HSVToRGB(i / 72.0f, 0.45f, 1.0f);
            AddLight(context.Root, "Light", new Vector3(Mathf.Cos(angle) * radius, 4.0f + (i % 5) * 0.7f, Mathf.Sin(angle) * radius + 5.0f), 0.28f, color);
        }

        for (var i = 0; i < 40; i++)
        {
            var angle = i * Mathf.PI * 2.0f / 40.0f;
            AddSphere(context.Root, "Receiver Sphere", new Vector3(Mathf.Cos(angle) * 4.2f, 0.6f, Mathf.Sin(angle) * 4.2f + 5.0f), 0.55f, new Color32(185, 185, 190, 255), RayMaterial.MaterialType.Diffuse, 0.1f);
        }

        Save(context.Scene, "Benchmark_ManyLights");
    }

    private static void CreateManyMeshesScene()
    {
        if (ShouldSkipExistingScene("Benchmark_ManyMeshes"))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_ManyMeshes", CameraPosition = new Vector3(0.0f, 8.0f, -22.0f), CameraEuler = new Vector3(18.0f, 0.0f, 0.0f),
            TopLevelBvhMinObjectCount = 0
        });
        AddLight(context.Root, "Key Light", new Vector3(0.0f, 14.0f, -5.0f), 2.0f, new Color32(255, 238, 218, 255));
        AddFloor(context.Root, new Vector2(0.0f, 6.0f), new Vector2(24.0f, 18.0f), 0.5f);

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

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_Glass", 
            CameraPosition = new Vector3(0.0f, 4.5f, -5.11f), 
            CameraEuler = new Vector3(36.0f, 0.0f, 0.0f),
            NumBounces = 8, ShadowQuality = 1
        });
        
        AddFloor(context.Root, new Vector2(0.0f, 3.0f), new Vector2(16.0f, 16.0f), 0.5f);
        AddLight(context.Root, "Key Light", new Vector3(-3.0f, 9.0f, -4.0f), 1.5f, new Color32(255, 235, 220, 255), 4f);
        AddLight(context.Root, "Blue Light", new Vector3(4.0f, 5.5f, 4.0f), 0.8f, new Color32(110, 165, 255, 255), 2f);

        for (int i = 0; i < 28; i++)
        {
            float angle = i * Mathf.PI * 2.0f / 28.0f;
            float radius = 4.0f + (i % 4) * 0.45f;
            var color = Color.HSVToRGB(i / 28.0f, 0.32f, 1.0f);
            AddSphere(context.Root, "Glass Sphere", new Vector3(Mathf.Cos(angle) * radius, 0.95f, Mathf.Sin(angle) * radius + 3.0f), 0.8f, color, RayMaterial.MaterialType.Glass, 1.0f, 0.78f, 1.5f, 0.15f);
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

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(-6.65f, 4.86f, -1.99f), CameraEuler = new Vector3(37.42f, 54.6f, 0.0f),
            NumBounces = 16, 
            ShadowQuality = 0,
            LightFalloffScale = 0.015f, 
            Exposure = 1.29f,
            SkyboxLightColor = new Color32(18, 18, 22, 255),
            TopLevelBvhMinObjectCount = 1024, 
            ShadowBvhMinObjectCount = 1024,
            DirectionalLightAngularRadius = 1.61f,
            EnableCaustics = true,
            CausticIntensity = 0.16f,
        });


        AddPrimitiveMesh(context.Root, "Receiver Back Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 2.0f, 5.2f), Vector3.zero, new Vector3(13.0f, 4.0f, 0.08f), new Color32(230, 230, 225, 255), RayMaterial.MaterialType.Diffuse, 0.05f, 1.0f);
        AddPrimitiveMesh(context.Root, "Receiver Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, 1.6f), Vector3.zero, new Vector3(13.0f, 0.04f, 11.0f), new Color32(215, 213, 205, 255), RayMaterial.MaterialType.Diffuse, 0.08f, 1.0f);

        AddTransmissionFilterStack(context.Root, "Clear Reference", -5.4f, Array.Empty<Color32>());
        AddTransmissionFilterStack(context.Root, "Blue Single Layer", -3.6f, new[] { new Color32(55, 105, 255, 255) });
        AddTransmissionFilterStack(context.Root, "Yellow Single Layer", -1.8f, new[] { new Color32(255, 235, 50, 255) });
        AddTransmissionFilterStack(context.Root, "Blue Then Yellow", 0.0f, new[] { new Color32(55, 105, 255, 255), new Color32(255, 235, 50, 255) });
        AddTransmissionFilterStack(context.Root, "Yellow Then Blue", 1.8f, new[] { new Color32(255, 235, 50, 255), new Color32(55, 105, 255, 255) });
        AddTransmissionFilterStack(context.Root, "Red Green Blue Stack", 3.6f, new[] { new Color32(255, 60, 45, 255), new Color32(55, 220, 75, 255), new Color32(55, 105, 255, 255) });

        // Keep these side by side so their receiver-wall shadows directly expose distance-based
        // mesh absorption instead of overlapping along the light direction.
        AddPrimitiveMesh(context.Root, "Thick Blue Glass Block", RayMeshPrimitive.PrimitiveType.Cube, 
            new Vector3(4.75f, 2.0f, -0.3f), Vector3.zero, new Vector3(0.8f, 2.6f, 1.35f),
            new Color32(55, 105, 255, 255), RayMaterial.MaterialType.Glass, 
            1.0f, 0.521f, 1.5f, 0.068f, 0.876f);
        
        AddPrimitiveMesh(context.Root, "Thin Blue Glass Plate", RayMeshPrimitive.PrimitiveType.Cube, 
            new Vector3(5.75f, 2.0f, -0.3f), Vector3.zero, new Vector3(0.8f, 2.6f, 0.14f),
            new Color32(55, 105, 255, 255), RayMaterial.MaterialType.Glass, 
            1.0f, 0.521f, 1.5f, 0.068f, 0.876f);

        AddSphere(context.Root, "Blue Glass Sphere", new Vector3(-3.82f, 1.51f, 3.34f), 0.75f, new Color32(0, 73, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.32f, 1.5f, 0.0f, 0.83f);
        AddSphere(context.Root, "Yellow Glass Sphere", new Vector3(-3.27f, 2.57f, 1.8f), 0.75f, new Color32(255, 255, 55, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.25f, 1.5f, 0.0f, 0.83f);

        Save(context.Scene, sceneName);
    }

    private static void AddTransmissionFilterStack(Transform parent, string name, float x, Color32[] colors, float opacity = 0.477f, float refraction = 1.5f, float specular = 0.0f, float transmission = 0.82f)
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

    private static void CreateCausticsScene()
    {
        const string sceneName = "Benchmark_Caustics";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        EnsureReadableModel(StanfordDragonModelPath);
        var dragonMesh = LoadFirstMeshFromAsset(StanfordDragonModelPath);
        if (dragonMesh == null)
        {
            Debug.LogWarning($"Skipping {sceneName}: no mesh found at {StanfordDragonModelPath}.");
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(-12.6f, 8.0f, 1.8f), CameraEuler = new Vector3(32.2f, 90.0f, 0.0f),
            NumBounces = 16, 
            ShadowQuality = 0,
            CameraFocalDistance = 12.0f, 
            FireflyClamp = 0.0f,
            EnableCaustics = true, 
            CausticGatherRadius = 0.01f, 
            CausticIntensity = 1.3f,
            TopLevelBvhMinObjectCount = 1024, 
            ShadowBvhMinObjectCount = 1024,
            LightSamplingStrategy = GameManager.LightSamplingStrategy.ImportanceSampled, 
            SkyboxLightColor = new Color32(111, 109, 98, 255),
            FieldOfView = 29.6f
        });

        var texturedPlane = RayMeshAssetGenerator.GetOrCreateTexturedPlaneMesh();
        var defaultCheckerGray = AssetDatabase.GetBuiltinExtraResource<Texture2D>(DefaultCheckerGrayTexturePath);
        
        AddRayMesh(context.Root, "Caustic Receiver", texturedPlane, 
            new Vector3(0.0f, 0.02f, 2.2f), Vector3.zero, new Vector3(10.0f, 0.4f, 9.0f), 
            Color.white, RayMaterial.MaterialType.Diffuse,
            0.5f, 1.0f, 1.0f, albedoTexture: defaultCheckerGray);

        AddPrimitiveMesh(context.Root, "Glass Dodecahedron", RayMeshPrimitive.PrimitiveType.Dodecahedron, 
            new Vector3(0.3f, 1.13f, 3.55f), Vector3.zero, Vector3.one, 
            new Color32(57, 255, 83, 255), RayMaterial.MaterialType.Glass, 
            1.0f, 0.816f, 1.5f, 0.1f, 0.7f);

        var dragon = AddRayMesh(context.Root, "Stanford Dragon", dragonMesh, 
            Vector3.zero, new Vector3(0.0f, 148.0f, 0.0f), Vector3.one * 3.0f,
            new Color32(0, 65, 255, 255), RayMaterial.MaterialType.Glass,
            1.0f, 0.62f, 1.5f, 0.1f, 0.7f);
        dragon.GetComponent<RayMaterial>().InterpolateNormals = true;
        
        Save(context.Scene, sceneName);
    }

    private static void CreateTriangleLightCausticsScene()
    {
        const string sceneName = "Benchmark_CausticsTriangleLight";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(0.0f, 5.4f, -10.5f), CameraEuler = new Vector3(19.0f, 0.0f, 0.0f),
            NumBounces = 10, ShadowQuality = 0,
            CameraFocalDistance = 12.0f, LightFalloffScale = 0.012f, FireflyClamp = 0.0f,
            EnableCaustics = true, CausticPhotonCount = 2048, CausticGatherRadius = 0.28f,
            TopLevelBvhMinObjectCount = 1024, ShadowBvhMinObjectCount = 1024,
            LightSamplingStrategy = GameManager.LightSamplingStrategy.AllLights, SkyboxLightColor = new Color32(2, 2, 3, 255)
        });

        AddPrimitiveMesh(context.Root, "Matte Caustic Receiver", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, 2.2f), Vector3.zero, new Vector3(10.0f, 0.04f, 9.0f), new Color32(225, 225, 218, 255), RayMaterial.MaterialType.Diffuse, 0.02f, 1.0f);
        AddMeshLight(context.Root, "Triangle Caustic Light", CreateHorizontalTriangleMesh("Triangle Caustic Light", 2.8f, 1.8f), new Vector3(0.0f, 6.8f, 2.5f), Vector3.zero, Vector3.one, new Color32(255, 244, 218, 255));
        AddSphere(context.Root, "Clear Glass Sphere", new Vector3(0.0f, 1.32f, 2.5f), 1.3f, new Color32(238, 248, 255, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.04f, 1.52f);
        AddSphere(context.Root, "Diffuse Scale Reference", new Vector3(2.6f, 0.45f, 6.2f), 0.45f, new Color32(185, 78, 52, 255), RayMaterial.MaterialType.Diffuse, 0.08f);
        Save(context.Scene, sceneName);
    }

    private static void CreateDynamicScene()
    {
        if (ShouldSkipExistingScene("Benchmark_Dynamic"))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = "Benchmark_Dynamic", CameraPosition = new Vector3(0.0f, 7.0f, -22.0f), CameraEuler = new Vector3(15.0f, 0.0f, 0.0f)
        });
        AddFloor(context.Root, new Vector2(0.0f, 5.0f), new Vector2(22.0f, 22.0f), 0.5f);
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

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(-8.8f, 7.75f, 12.37f), CameraEuler = new Vector3(36.35f, 81.9f, 0.0f),
            NumBounces = 8, 
            ShadowQuality = 0,
            LightFalloffScale = 0.021f, 
            Exposure = 1.6f,
            DirectionalLightIntensity = 1.5f, DirectionalLightAngularRadius = 2.74f, DirectionalLightRotation = new Vector3(90.0f, -30.0f, 0.0f),
            TopLevelBvhMinObjectCount = 0, 
            ShadowBvhMinObjectCount = 0, 
            SkyboxLightColor = new Color32(221, 221, 221, 255),
        });
        
        var waterObject = new GameObject("Water Volume");
        waterObject.transform.SetParent(context.Root, false);
        var water = waterObject.AddComponent<Water>();
        waterObject.transform.position = new Vector3(-2.0f, 5.0f, 3.0f);
        waterObject.transform.localScale = new Vector3(40.0f, 5.0f, 40.0f);
        water.Color = new Color32(215, 255, 255, 255);
        water.Smoothness = 0.97f;
        water.Opacity = 0.08f;
        water.AbsorptionStrength = 0.55f;
        water.RefractionIndex = 1.33f;
        water.WaveAmplitude = 0.32f;
        water.WaveScale = 0.7f;
        water.WaveSpeed = 0.85f;
        water.MarchSteps = 36;
        water.RefinementSteps = 6;

        //AddLight(context.Root, "Low Sun Reflection Light", new Vector3(-5.0f, 4.0f, -5.5f), 1.2f, new Color32(255, 226, 188, 255));
        AddLight(context.Root, "Cool Sky Fill", new Vector3(8.0f, 15f, 8.0f), 1.8f, new Color32(255, 253, 155, 255));

        AddPrimitiveMesh(context.Root, "Ground Plane", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-2.0f, 0.07f, 3.0f), Vector3.zero, new Vector3(600.0f, 0.04f, 600.0f), new Color32(88, 78, 48, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);

        AddPrimitiveMesh(context.Root, "Raised Bed Inside Water", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(10.0f, 0.43f, 10.0f), new Vector3(0.0f, 0.0f, 25.0f), new Vector3(15.0f, 5.0f, 40.0f), new Color32(88, 78, 48, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);

        for (var i = 0; i < 24; i++)
        {
            var angle = i * Mathf.PI * 2.0f / 24.0f;
            var radiusX = 13.5f + (i % 3) * 0.65f;
            var radiusZ = 15.0f + (i % 4) * 0.55f;
            var x = Mathf.Cos(angle) * radiusX;
            var z = 5.0f + Mathf.Sin(angle) * radiusZ;
            var stoneRadius = 0.25f + 0.18f * Mathf.PerlinNoise(i * 0.37f, 2.1f);
            AddSphere(context.Root, "Shore Rock", new Vector3(x, 0.18f + stoneRadius, z), stoneRadius, new Color32(98, 96, 88, 255), RayMaterial.MaterialType.Diffuse, 0.42f);
        }

        for (var i = 0; i < 18; i++)
        {
            var x = -11.0f + i * 1.3f;
            var localZ = -3.0f + i * 1.1f + Mathf.Sin(i * 1.7f) * 1.8f;
            var y = 0.7f + (i % 4) * 0.04f;
            var color = Color.HSVToRGB(0.08f + i * 0.012f, 0.65f, 0.55f);
            AddSphere(context.Root, "Depth Gradient Pebble", new Vector3(x, y, localZ + 5.0f), 0.25f + (i % 3) * 0.08f, color, RayMaterial.MaterialType.Glass, 1.0f, 0.4f, 1.5f, specular: 0.2f, transmission: 0.89f);
        }

        AddSphere(context.Root, "Half Submerged Red Marker", new Vector3(-3.2f, 0.95f, 1.2f), 0.62f, new Color32(220, 65, 45, 255), RayMaterial.MaterialType.Metal, 0.72f);
        AddSphere(context.Root, "Shallow Blue Marker", new Vector3(2.7f, 0.86f, 1.0f), 0.58f, new Color32(50, 120, 235, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        AddSphere(context.Root, "Water Only Yellow Marker", new Vector3(-5.5f, 0.55f, 10.5f), 0.22f, new Color32(245, 210, 70, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        AddSphere(context.Root, "Far Reflection Sphere", new Vector3(5.4f, 1.25f, 13.0f), 0.95f, new Color32(230, 222, 196, 255), RayMaterial.MaterialType.Metal, 0.86f);

        AddPrimitiveMesh(context.Root, "Left Dock Post", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-5.2f, 1.2f, 14.5f), Vector3.zero, new Vector3(0.28f, 10.0f, 0.28f), new Color32(96, 62, 34, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Dock Post", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-3.8f, 1.2f, 14.5f), Vector3.zero, new Vector3(0.28f, 10.0f, 0.28f), new Color32(96, 62, 34, 255), RayMaterial.MaterialType.Diffuse, 0.38f, 1.0f);
        AddPrimitiveMesh(context.Root, "Dock Plank", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-4.5f, 5.0f, 14.5f), Vector3.zero, new Vector3(2.2f, 0.18f, 1.0f), new Color32(120, 78, 42, 255), RayMaterial.MaterialType.Diffuse, 0.45f, 1.0f);

        Save(context.Scene, sceneName);
    }

    private static void CreateGlassOfWaterPencilScene()
    {
        const string sceneName = "Benchmark_GlassWaterPencil";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(0.0f, 3.9277854f, -6.0574317f), CameraEuler = new Vector3(14.3395891f, 358.70932f, 0.0f),
            NumBounces = 10, ShadowQuality = 0,
            CameraFocalDistance = 7.5f, LightFalloffScale = 0.003f,
            TopLevelBvhMinObjectCount = 0, ShadowBvhMinObjectCount = 1024,
            SkyboxLightColor = new Color32(140, 149, 164, 255)
        });

        AddLight(context.Root, "Large Softbox", new Vector3(-3.5f, 5.6f, -3.8f), 1.6f, Color.white);
        AddFloor(context.Root, Vector2.zero, new Vector2(12.0f, 10.0f), 0.28f, "Tabletop");
        var rimHighlight = AddLight(context.Root, "Rim Highlight", new Vector3(3.5f, 3.7f, -2.2f), 0.55f, new Color32(210, 230, 255, 255));
        rimHighlight.transform.localScale = Vector3.one * 0.1f;

        var tumblerRoot = new GameObject("Glass Tumbler");
        tumblerRoot.transform.SetParent(context.Root, false);
        tumblerRoot.transform.localPosition = Vector3.zero;
        tumblerRoot.transform.localRotation = Quaternion.identity;
        tumblerRoot.transform.localScale = Vector3.one;

        var glassWall = AddRayMesh(tumblerRoot.transform, "Glass Wall", CreateOpenCylinderMesh("Glass Wall", 96, 1.36f, 3.05f, 0.055f), new Vector3(0.0f, 1.56f, 0.0f), Vector3.zero, Vector3.one, new Color32(212, 238, 245, 255), RayMaterial.MaterialType.Glass, 0.98f, 0.146f, 1.83f);
        glassWall.GetComponent<RayMaterial>().InterpolateNormals = true;
        var waterVolume = AddRayMesh(tumblerRoot.transform, "Water Volume", CreateCylinderMesh("Water Volume", 96, 1.24f, 1.86f), new Vector3(0.0f, 1.17f, 0.0f), Vector3.zero, Vector3.one, new Color32(190, 226, 238, 255), RayMaterial.MaterialType.Glass, 0.99f, 0.08f, 2.2f);
        waterVolume.GetComponent<RayMaterial>().InterpolateNormals = true;
        var topRim = AddRayMesh(tumblerRoot.transform, "Top Rim", CreateTorusMesh("Top Rim", 96, 12, 1.36f, 0.055f), new Vector3(0.0f, 3.09f, 0.0f), Vector3.zero, Vector3.one, new Color32(220, 244, 250, 255), RayMaterial.MaterialType.Glass, 1.0f, 0.16f, 1.52f);
        topRim.GetComponent<RayMaterial>().InterpolateNormals = true;

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

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(0.0f, 2.05f, -4.85f), CameraEuler = new Vector3(0.0f, 0.0f, 0.0f),
            NumBounces = 14, 
            ShadowQuality = 0,
            CameraFocalDistance = 9.5f, 
            LightFalloffScale = 0.075f,
            TopLevelBvhMinObjectCount = 0, 
            ShadowBvhMinObjectCount = 0, 
            SkyboxLightColor = new Color32(0, 0, 0, 255),
            DirectionalLightIntensity = 0.0f
        });

        const float roomWidth = 6.0f;
        const float roomHeight = 4.5f;
        const float roomDepth = 12.0f;
        const float roomCenterZ = 1.0f;
        float backZ = roomCenterZ + roomDepth * 0.5f;
        float frontZ = roomCenterZ - roomDepth * 0.5f;

        AddPrimitiveMesh(context.Root, "Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, roomCenterZ), Vector3.zero, new Vector3(roomWidth, 0.04f, roomDepth), new Color32(230, 226, 212, 255), RayMaterial.MaterialType.Diffuse, 0.22f, 1.0f);
        AddPrimitiveMesh(context.Root, "Ceiling", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight, roomCenterZ), Vector3.zero, new Vector3(roomWidth, 0.04f, roomDepth), new Color32(226, 224, 212, 255), RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f);
        AddPrimitiveMesh(context.Root, "Left Green Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(0.04f, roomHeight, roomDepth), new Color32(34, 178, 58, 255), RayMaterial.MaterialType.Diffuse, 0.06f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Red Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(0.04f, roomHeight, roomDepth), new Color32(226, 20, 20, 255), RayMaterial.MaterialType.Diffuse, 0.06f, 1.0f);
        AddPrimitiveMesh(context.Root, "Far Mirror Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight * 0.5f, backZ), Vector3.zero, new Vector3(roomWidth, roomHeight, 0.04f), Color.white, RayMaterial.MaterialType.Metal, 1.0f, 1.0f);
        AddPrimitiveMesh(context.Root, "Camera-Side Mirror Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight * 0.5f, frontZ), Vector3.zero, new Vector3(roomWidth, roomHeight, 0.04f), Color.white, RayMaterial.MaterialType.Metal, 1.0f, 1.0f);
        
        AddMeshLight(context.Root, "Middle Rectangular Ceiling Light", CreateHorizontalQuadMesh("Middle Rectangular Ceiling Light", 1.35f, 0.46f, 1.0f, 1.0f), new Vector3(0.0f, roomHeight - 0.04f, 0.65f), Vector3.zero, new Vector3(2.0f, 1.0f, 1.0f), new Color32(255, 248, 220, 255));

        AddPrimitiveMesh(context.Root, "Near Left Block", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-1.85f, 0.82f, -1.85f), Vector3.zero, new Vector3(1.45f, 1.6f, 1.15f), new Color32(218, 212, 196, 255), RayMaterial.MaterialType.Diffuse, 0.15f, 1.0f);
        AddPrimitiveMesh(context.Root, "Tall Center Block", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-0.55f, 1.55f, 0.8f), Vector3.zero, new Vector3(1.0f, 3.1f, 1.0f), new Color32(220, 216, 202, 255), RayMaterial.MaterialType.Diffuse, 0.12f, 1.0f);
        
        AddPrimitiveMesh(context.Root, "Glass Box", RayMeshPrimitive.PrimitiveType.Cube,
            new Vector3(1.45f, 0.81f, 1.9f), new Vector3(0.0f, -8.0f, 0.0f), new Vector3(1.2f, 1.48f, 1.0f),
            new Color32(232, 232, 226, 255), RayMaterial.MaterialType.Glass, 
            1.0f, 0.32f, 1.85f, 0.086f, 0.85f);
        
        AddPrimitiveMesh(context.Root, "Glass Pyramid", RayMeshPrimitive.PrimitiveType.Pyramid, 
            new Vector3(0.0f, 1.05f, -0.7f), new Vector3(0.0f, 22.0f, 0.0f), Vector3.one * 1.8f,
            new Color32(210, 235, 255, 255), RayMaterial.MaterialType.Glass,
            1.0f, 0.32f, 1.85f, 0.086f, 0.85f);
        
        AddSphere(context.Root, "Chrome Sphere", new Vector3(2.05f, 0.82f, -2.25f), 0.82f, new Color32(236, 233, 226, 255), RayMaterial.MaterialType.Metal, 1.0f);

        Save(context.Scene, sceneName);
    }

    private static void CreateDemofoxGlossyReflectionsScene()
    {
        const string sceneName = "Benchmark_DemofoxGlossyReflections";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        // Recreates the material arrangement from https://www.shadertoy.com/view/WsBBR3.
        // The project skybox provides the environment visible through the open front.
        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName,
            CameraPosition = new Vector3(0.0f, 3.24f, -6.24f),
            CameraEuler = Vector3.zero,
            NumBounces = 8,
            CameraFocalDistance = 13.5f,
            LightFalloffScale = 0.03f,
            TopLevelBvhMinObjectCount = 0,
            ShadowBvhMinObjectCount = 0,
            SkyboxLightColor = new Color32(110, 110, 120, 255),
            CameraApertureMode = GameManager.CameraApertureMode.Pinhole
        });

        const float roomWidth = 7.0f;
        const float roomHeight = 6.5f;
        const float roomDepth = 2.75f;
        const float roomCenterZ = roomDepth * 0.5f;
        const float wallThickness = 0.04f;

        AddPrimitiveMesh(context.Root, "Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, roomCenterZ), Vector3.zero, new Vector3(roomWidth, wallThickness, roomDepth), new Color32(198, 196, 180, 255), RayMaterial.MaterialType.Diffuse, 0.15f, 1.0f);
        AddPrimitiveMesh(context.Root, "Ceiling", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight, roomCenterZ), Vector3.zero, new Vector3(roomWidth, wallThickness, roomDepth), new Color32(198, 196, 180, 255), RayMaterial.MaterialType.Diffuse, 0.15f, 1.0f);
        AddPrimitiveMesh(context.Root, "Back Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight * 0.5f, roomCenterZ + roomDepth * 0.5f), Vector3.zero, new Vector3(roomWidth, roomHeight, wallThickness), new Color32(198, 196, 180, 255), RayMaterial.MaterialType.Diffuse, 0.12f, 1.0f);
        AddPrimitiveMesh(context.Root, "Left Red Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(wallThickness, roomHeight, roomDepth), new Color32(220, 42, 32, 255), RayMaterial.MaterialType.Diffuse, 0.08f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Green Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(wallThickness, roomHeight, roomDepth), new Color32(45, 205, 42, 255), RayMaterial.MaterialType.Diffuse, 0.08f, 1.0f);
        AddMeshLight(context.Root, "Ceiling Area Light", CreateHorizontalQuadMesh("Demofox Ceiling Area Light", 2.8f, roomDepth * 0.5f, 1.0f, 1.0f), new Vector3(0.0f, roomHeight - wallThickness + 0.012f, roomDepth * 0.5f), Vector3.zero, Vector3.one, Color.white);

        float[] roughnessSteps = { 1.0f, 0.75f, 0.5f, 0.25f, 0.0f };
        var smallSphereRadius = 0.48f;
        for (int i = 0; i < roughnessSteps.Length; i++)
        {
            AddSphere(context.Root, $"Green Metal Smoothness {1.0f - roughnessSteps[i]:0.00}", new Vector3(-2.85f + i * 1.4f, 3.35f, roomDepth - smallSphereRadius), smallSphereRadius, new Color(0.3f, 1.0f, 0.3f, 1.0f), RayMaterial.MaterialType.Metal, roughnessSteps[i]);
        }

        var sphereRadius = 0.85f;
        var sidePadding = 0.17f;
        var yellowDielectric = AddSphere(context.Root, "Yellow Dielectric", new Vector3(-(roomWidth * 0.5f) + sphereRadius + sidePadding, sphereRadius, roomDepth - sphereRadius), sphereRadius, new Color32(180, 170, 50, 255), RayMaterial.MaterialType.Diffuse, 0.8f);
        yellowDielectric.GetComponent<RayMaterial>().Metallic = 0.1f;
        var pinkDielectric = AddSphere(context.Root, "Pink Dielectric", new Vector3(0.0f, sphereRadius, roomDepth - sphereRadius), sphereRadius, new Color32(220, 115, 172, 255), RayMaterial.MaterialType.Diffuse, 0.8f);
        pinkDielectric.GetComponent<RayMaterial>().Metallic = 0.3f;
        AddSphere(context.Root, "Blue Magenta Metal", new Vector3((roomWidth * 0.5f) - sphereRadius - sidePadding, sphereRadius, roomDepth - sphereRadius), sphereRadius, new Color32(42, 36, 210, 255), RayMaterial.MaterialType.Metal, 0.5f);

        Save(context.Scene, sceneName);
    }

    private static void CreateDemofoxRoughRefractionScene()
    {
        const string sceneName = "Benchmark_DemofoxRoughRefraction";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateDemofoxOrbGradientTestScene(sceneName);
        const int sphereCount = 7;
        for (int i = 0; i < sphereCount; i++)
        {
            float smoothness = 1.0f - i * (0.5f / (sphereCount - 1));
            AddSphere(
                context.Root,
                $"Glass Smoothness {smoothness:0.00}",
                GetDemofoxRefractionSpherePosition(i, sphereCount),
                1.1f,
                new Color32(245, 248, 250, 255),
                RayMaterial.MaterialType.Glass,
                smoothness,
                0.04f,
                1.14f,
                0.02f);
        }

        Save(context.Scene, sceneName);
    }

    private static void CreateDemofoxRefractionIndexScene()
    {
        const string sceneName = "Benchmark_DemofoxRefractionIndex";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateDemofoxOrbGradientTestScene(sceneName);
        const int sphereCount = 7;
        for (int i = 0; i < sphereCount; i++)
        {
            float refractionIndex = Mathf.Lerp(1.0f, 1.5f, (float)i / (sphereCount - 1));
            AddSphere(
                context.Root,
                $"Glass IOR {refractionIndex:0.00}",
                GetDemofoxRefractionSpherePosition(i, sphereCount),
                1.1f,
                new Color32(245, 248, 250, 255),
                RayMaterial.MaterialType.Glass,
                1.0f,
                0.04f,
                refractionIndex,
                0.04f,
                0.95f);
        }

        Save(context.Scene, sceneName);
    }

    private static void CreateDemofoxAbsorptionScene()
    {
        const string sceneName = "Benchmark_DemofoxAbsorption";
        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var context = CreateDemofoxOrbGradientTestScene(sceneName);
        
        const int sphereCount = 7;
        var absorption = new []{0f, 0.23f, 0.42f, 0.6f, 0.75f, 0.83f, 1f };
        for (var i = 0; i < sphereCount; i++)
        {
            Color filter = new Color32(31, 5, 0, 255);
            AddSphere(
                context.Root,
                $"Glass Absorption {absorption[i]:0.00}",
                GetDemofoxRefractionSpherePosition(i, sphereCount),
                1.1f,
                filter,
                RayMaterial.MaterialType.Glass,
                1.0f,
                absorption[i],
                1.08f,
                0.105f);
        }

        Save(context.Scene, sceneName);
    }

    private static BenchmarkContext CreateDemofoxOrbGradientTestScene(string sceneName)
    {
        // Layout based on the SCENE 6 (roughness) and SCENE 3 (absorption) fixtures from
        // https://www.shadertoy.com/view/ttfyzN.
        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, 
            CameraPosition = new Vector3(0.0f, 4.15f, -11.87f), 
            CameraEuler = new Vector3(3.0f, 0.0f, 0.0f),
            NumBounces = 10, 
            ShadowQuality = 0,
            CameraFocalDistance = 16.0f, 
            LightFalloffScale = 0.175f,
            // Preserve the HDR contrast between the emissive panel and the environment in the
            // source fixture. Per-sample clamping would reduce both to the same luminance.
            FireflyClamp = 0.0f,
            TopLevelBvhMinObjectCount = 0, 
            ShadowBvhMinObjectCount = 0,
            SkyboxLightColor = new Color32(255, 245, 223, 255), 
            CameraApertureMode = GameManager.CameraApertureMode.Pinhole,
            FieldOfView = 33.6f,
            DirectionalLightIntensity = 0.0f,
        });

        const float stageWidth = 19.0f;
        AddPrimitiveMesh(context.Root, "White Receiver", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, 4.8f), Vector3.zero, new Vector3(stageWidth, 0.04f, 7.0f), new Color32(140, 140, 140, 255), RayMaterial.MaterialType.Diffuse, 0.08f, 1.0f);
        const float lightWidth = 4.0f;
        const float lightDepth = 2.0f;
        const float lightHeight = 7.1f;
        const float lightZ = 4.8f;
        const float lightBorder = 0.5f;
        AddPrimitiveMesh(context.Root, "Overhead Light Frame", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, lightHeight + 0.03f, lightZ), new Vector3(10.8f, 0f, 0f), new Vector3(lightWidth + lightBorder * 2.0f, 0.06f, lightDepth + lightBorder * 2.0f), new Color32(54, 50, 42, 255), RayMaterial.MaterialType.Diffuse, 0.2f, 1.0f);
        AddMeshLight(context.Root, "Overhead Area Light", CreateHorizontalQuadMesh("Demofox Refraction Area Light", lightWidth, lightDepth, 1.0f, 1.0f), new Vector3(0.0f, lightHeight - 0.1f, lightZ), new Vector3(10.8f, 0f, 0f), Vector3.one, new Color32(255, 246, 223, 255), 9.0f);

        const int stripeCount = 100;
        const float stripeWidth = stageWidth / stripeCount;
        var stripeParent = new GameObject("Backdrop Stripes").transform;
        stripeParent.SetParent(context.Root, false);
        for (var i = 0; i < stripeCount; i++)
        {
            var x = (i - (stripeCount - 1) * 0.5f) * stripeWidth;
            var stripeColor = (i & 1) == 0 ? Color.black : Color.white;
            AddPrimitiveMesh(stripeParent, $"Backdrop Stripe {i + 1}", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(x, 2.45f, 7.8f), Vector3.zero, new Vector3(stripeWidth, 3.3f, 0.04f), stripeColor, RayMaterial.MaterialType.Diffuse, 0.0f, 1.0f);
        }

        return context;
    }

    private static Vector3 GetDemofoxRefractionSpherePosition(int index, int sphereCount)
    {
        const float radius = 1.1f;
        const float gap = 0.08f;
        float spacing = radius * 2.0f + gap;
        return new Vector3((index - (sphereCount - 1) * 0.5f) * spacing, radius * 1.5f, 4.8f);
    }

    private static void CreateDragonCornellBoxScene()
    {
        const string sceneName = "Benchmark_DragonCornellBox";
        EnsureReadableModel(StanfordDragonModelPath);

        if (ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        var dragonMesh = LoadFirstMeshFromAsset(StanfordDragonModelPath);
        if (dragonMesh == null)
        {
            Debug.LogWarning($"Skipping {sceneName}: no mesh found at {StanfordDragonModelPath}.");
            return;
        }

        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(0.0f, 2.2f, -5.2f), CameraEuler = new Vector3(2.0f, 0.0f, 0.0f),
            NumBounces = 5, 
            ShadowQuality = 0,
            CameraFocalDistance = 6.5f, 
            LightFalloffScale = 0.02f,
            TopLevelBvhMinObjectCount = 0, 
            ShadowBvhMinObjectCount = 0, 
            SkyboxLightColor = new Color32(0, 0, 0, 255),
            DirectionalLightIntensity = 0.0f,
        });

        const float roomWidth = 5.6f;
        const float roomHeight = 4.2f;
        const float roomDepth = 8.2f;
        const float roomCenterZ = 0.5f;
        float backZ = roomCenterZ + roomDepth * 0.5f;

        AddPrimitiveMesh(context.Root, "Floor", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, 0.02f, roomCenterZ), Vector3.zero, new Vector3(roomWidth, 0.04f, roomDepth), new Color32(230, 226, 214, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f);
        AddPrimitiveMesh(context.Root, "Ceiling", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight, roomCenterZ), Vector3.zero, new Vector3(roomWidth, 0.04f, roomDepth), new Color32(226, 224, 214, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f);
        AddPrimitiveMesh(context.Root, "Left Green Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(-roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(0.04f, roomHeight, roomDepth), new Color32(34, 178, 58, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f);
        AddPrimitiveMesh(context.Root, "Right Red Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(roomWidth * 0.5f, roomHeight * 0.5f, roomCenterZ), Vector3.zero, new Vector3(0.04f, roomHeight, roomDepth), new Color32(230, 38, 20, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f);
        AddPrimitiveMesh(context.Root, "Back Wall", RayMeshPrimitive.PrimitiveType.Cube, new Vector3(0.0f, roomHeight * 0.5f, backZ), Vector3.zero, new Vector3(roomWidth, roomHeight, 0.04f), new Color32(232, 230, 220, 255), RayMaterial.MaterialType.Diffuse, 0.5f, 1.0f);

        AddMeshLight(context.Root, "Rectangular Ceiling Light", CreateHorizontalQuadMesh("Rectangular Ceiling Light", 1.25f, 0.72f, 1.0f, 1.0f), new Vector3(0.0f, roomHeight - 0.021f, 0.7f), Vector3.zero, new Vector3(1.25f, 1.25f, 1.25f), new Color32(255, 255, 255, 255));

        var dragon = AddRayMesh(context.Root, "Stanford Dragon", dragonMesh, new Vector3(0.0f, 0.0f, 0.0f), new Vector3(0.0f, 148.0f, 0.0f), new Vector3(3.0f, 3.0f, 3.0f), Color.white, RayMaterial.MaterialType.Diffuse, 0.75f, 1.0f, 1.0f);
        dragon.GetComponent<RayMaterial>().InterpolateNormals = true;
        //FitObjectToBox(dragon.transform, dragonMesh.bounds, new Vector3(0.0f, 0.04f, 0.15f), new Vector3(2.45f, 2.35f, 2.45f));

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
        var context = CreateBaseScene(new SceneSettings
        {
            SceneName = sceneName, CameraPosition = new Vector3(5.4f, 1.29f, 0.99f), CameraEuler = new Vector3(2.0f, -60.55f, 0.0f),
            NumberOfPasses = 2, NumBounces = 6, ShadowQuality = 1,
            CameraFocalDistance = 10.0f, LightFalloffScale = 0.035f, Exposure = 1.25f,
            SkyboxLightColor = new Color32(8, 8, 8, 255),
            TopLevelBvhMinObjectCount = 1024, ShadowBvhMinObjectCount = 1024
        });

        AddRayMesh(context.Root, "Back Stone Wall", CreateQuadMesh("Back Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(0.0f, 1.5f, 7.0f), Vector3.zero, Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, albedoTexture: wallTexture);
        AddRayMesh(context.Root, "Left Stone Wall", CreateQuadMesh("Left Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(-6.0f, 1.5f, 1.0f), new Vector3(0.0f, 90.0f, 0.0f), Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, albedoTexture: wallTexture);
        AddRayMesh(context.Root, "Right Stone Wall", CreateQuadMesh("Right Stone Wall", 12.0f, 3.0f, 6.0f, 1.5f), new Vector3(6.0f, 1.5f, 1.0f), new Vector3(0.0f, -90.0f, 0.0f), Vector3.one, Color.white, RayMaterial.MaterialType.Diffuse, 0.18f, 1.0f, 1.0f, albedoTexture: wallTexture);
        AddRayMesh(context.Root, "Floor", CreateHorizontalQuadMesh("Floor", 12.0f, 12.0f, 3.0f, 3.0f), new Vector3(0.0f, 0.002f, 1.0f), Vector3.zero, Vector3.one, new Color32(78, 68, 48, 255), RayMaterial.MaterialType.Diffuse, 0.28f);
        AddRayMesh(context.Root, "Ceiling", CreateHorizontalQuadMesh("Ceiling", 12.0f, 12.0f, 3.0f, 3.0f), new Vector3(0.0f, 2.0f, 1.0f), new Vector3(180.0f, 0.0f, 0.0f), Vector3.one, new Color32(92, 78, 54, 255), RayMaterial.MaterialType.Diffuse, 0.2f);

        //AddLight(context.Root, "Bright Wall Light", new Vector3(1.9f, 0.75f, 5.35f), 0.42f, new Color32(255, 245, 190, 255));
        AddLight(context.Root, "Small Warm Light", new Vector3(-0.35f, 1.1f, 5.85f), 0.35f, new Color32(255, 238, 178, 255));
        AddLight(context.Root, "Ceiling Fill", new Vector3(0.47f, 2f, -1.62f), 0.7f, new Color32(170, 135, 85, 255));

        AddSphere(context.Root, "Large Center Sphere", new Vector3(-1.2f, 0.85f, 2.85f), 0.85f, new Color32(150, 146, 105, 255), RayMaterial.MaterialType.Diffuse, 0.4f);
        AddSphere(context.Root, "Cyan Sphere", new Vector3(-2.65f, 0.72f, 2.45f), 0.72f, new Color32(32, 128, 135, 255), RayMaterial.MaterialType.Diffuse, 0.35f);
        AddSphere(context.Root, "Orange Right Sphere", new Vector3(3.9f, 0.72f, 3.7f), 0.72f, new Color32(215, 93, 0, 255), RayMaterial.MaterialType.Metal, 1.0f);
        AddSphere(context.Root, "Blue Left Sphere", new Vector3(-4.15f, 0.65f, 1.15f), 0.65f, new Color32(0, 56, 78, 255), RayMaterial.MaterialType.Diffuse, 0.25f);
        AddSphere(context.Root, "Brown Right Sphere", new Vector3(1.75f, 0.62f, 1.35f), 0.62f, new Color32(116, 80, 34, 255), RayMaterial.MaterialType.Metal, 1.0f);
        AddSphere(context.Root, "Foreground Green Sphere", new Vector3(-0.8f, 0.92f, -0.8f), 0.92f, new Color32(26, 68, 56, 255), RayMaterial.MaterialType.Diffuse, 0.25f);
        AddSphere(context.Root, "Foreground Yellow Sphere", new Vector3(4.85f, 1.25f, -1.4f), 1.25f, new Color32(120, 112, 54, 255), RayMaterial.MaterialType.Diffuse, 0.2f);
        AddSphere(context.Root, "Foreground Red Sphere", new Vector3(-5.0f, 1.0f, -2.05f), 1.0f, new Color32(105, 0, 25, 255), RayMaterial.MaterialType.Diffuse, 0.2f);

        Save(context.Scene, sceneName);
    }

    private static GameObject AddSphere(Transform parent, string name, Vector3 position, float radius, Color color, RayMaterial.MaterialType type, float smoothness, float opacity = 1.0f, float refraction = 1.0f, float specular = 0.0f, float transmission = 1.0f)
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
        material.Specular = specular;
        material.Transmission = transmission;

        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static GameObject AddFloor(Transform parent, Vector2 center, Vector2 size, float smoothness, string name = "Floor")
    {
        return AddPrimitiveMesh(
            parent,
            name,
            RayMeshPrimitive.PrimitiveType.Cube,
            new Vector3(center.x, -0.02f, center.y),
            Vector3.zero,
            new Vector3(size.x, 0.04f, size.y),
            new Color32(204, 204, 204, 255),
            RayMaterial.MaterialType.Diffuse,
            smoothness,
            1.0f);
    }

    private static GameObject AddLight(Transform parent, string name, Vector3 position, float radius, Color color, float intensity = 1.0f)
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
        light.Intensity = intensity;

        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static GameObject AddDirectionalLight(Transform parent, string name, Vector3 euler, Color color, float intensity, float angularRadius)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = Vector3.zero;
        obj.transform.localEulerAngles = euler;
        obj.transform.localScale = Vector3.one;

        var light = obj.AddComponent<RayDirectionalLight>();
        light.Color = color;
        light.Intensity = intensity;
        light.AngularRadius = angularRadius;
        return obj;
    }

    private static GameObject AddMeshLight(Transform parent, string name, Mesh mesh, Vector3 position, Vector3 euler, Vector3 scale, Color color, float intensity = 1.0f)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localEulerAngles = euler;
        obj.transform.localScale = scale;

        var light = obj.AddComponent<RayLight>();
        light.Color = color;
        light.Intensity = intensity;

        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static GameObject AddPrimitiveMesh(
        Transform parent, string name, RayMeshPrimitive.PrimitiveType primitiveType, 
        Vector3 position, Vector3 euler, Vector3 scale, 
        Color color, RayMaterial.MaterialType type, 
        float smoothness, 
        float opacity, 
        float refraction = 1.0f, 
        float specular = 0.0f, 
        float transmission = 1.0f)
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
        material.Specular = specular;
        material.Transmission = transmission;

        primitive.Type = primitiveType;
        primitive.EnsureMesh();
        return obj;
    }

    private static GameObject AddRayMesh(
        Transform parent, string name, Mesh mesh, 
        Vector3 position, Vector3 euler, Vector3 scale, 
        Color color, 
        RayMaterial.MaterialType type, 
        float smoothness, 
        float opacity = 1.0f,
        float refraction = 1.0f, 
        float specular = 0.0f,
        float transmission = 1.0f,
        Texture2D albedoTexture = null)
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
        material.Specular = specular;
        material.Transmission = transmission;

        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>();
        obj.AddComponent<RayTracingObject>();
        return obj;
    }

    private static void AddTeapot(
        Transform parent,
        string name,
        Mesh bodyMesh,
        Mesh baseMesh,
        Vector3 position,
        Color color,
        RayMaterial.MaterialType type,
        float smoothness,
        float metallic,
        Texture2D albedoTexture = null,
        Texture2D metallicRoughnessTexture = null,
        Texture2D normalTexture = null,
        float opacity = 1.0f,
        float refraction = 1.0f)
    {
        var root = new GameObject(name);
        root.transform.SetParent(parent, false);
        root.transform.localPosition = position;
        root.transform.localEulerAngles = new Vector3(0.0f, -91.05f, 0.0f);
        root.transform.localScale = Vector3.one * 25.0f;

        ConfigureTeapotPart(
            AddRayMesh(root.transform, "Body", bodyMesh, Vector3.zero, Vector3.zero, Vector3.one, color, type, smoothness, opacity, refraction, albedoTexture: albedoTexture)
            , metallic, metallicRoughnessTexture, normalTexture);
        
        ConfigureTeapotPart(
            AddRayMesh(root.transform, "Base", baseMesh, Vector3.zero, Vector3.zero, Vector3.one, 
                new Color32(25, 25, 25, 255), 
                RayMaterial.MaterialType.Diffuse, 
                0.4f, 
                1.0f, 
                0.0f, 
                albedoTexture: albedoTexture), 
            0.0f, 
            metallicRoughnessTexture, 
            normalTexture);
    }

    private static void ConfigureTeapotPart(GameObject part, float metallic, Texture2D metallicRoughnessTexture, Texture2D normalTexture)
    {
        var material = part.GetComponent<RayMaterial>();
        material.Metallic = metallic;
        material.MetallicRoughnessTexture = metallicRoughnessTexture;
        material.NormalTexture = normalTexture;
        material.InterpolateNormals = true;
    }

    private static Mesh LoadFirstMeshFromAsset(string path)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh != null)
        {
            return mesh;
        }

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (asset is Mesh candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void EnsureReadableModel(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null || importer.isReadable)
        {
            return;
        }

        importer.isReadable = true;
        importer.SaveAndReimport();
    }

    private static void EnsureReadableTexture(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        bool changed = false;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            changed = true;
        }
        if (importer.wrapMode != TextureWrapMode.Repeat)
        {
            importer.wrapMode = TextureWrapMode.Repeat;
            changed = true;
        }
        if (changed)
        {
            importer.SaveAndReimport();
        }
    }

    private static Texture2D LoadRenderManTexture(string fileName, bool linear)
    {
        string path = $"{RenderManTextureFolder}/{fileName}";
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return null;
        }

        bool changed = false;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            changed = true;
        }
        if (importer.wrapMode != TextureWrapMode.Repeat)
        {
            importer.wrapMode = TextureWrapMode.Repeat;
            changed = true;
        }
        if (importer.sRGBTexture == linear)
        {
            importer.sRGBTexture = !linear;
            changed = true;
        }
        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            changed = true;
        }
        if (changed)
        {
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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

    private static Mesh CreateHorizontalTriangleMesh(string name, float width, float depth)
    {
        var mesh = new Mesh
        {
            name = name,
            vertices = new[]
            {
                new Vector3(-width * 0.5f, 0.0f, -depth * 0.5f),
                new Vector3(0.0f, 0.0f, depth * 0.5f),
                new Vector3(width * 0.5f, 0.0f, -depth * 0.5f)
            },
            triangles = new[] { 0, 2, 1 }
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
        if (File.Exists(path) && !_overwriteExistingScenes)
        {
            Debug.LogWarning($"Skipping save for existing generated scene: {path}");
            return;
        }

        EditorSceneManager.SaveScene(scene, path);
    }

    private static bool ShouldSkipExistingScene(string sceneName)
    {
        string path = GetScenePath(sceneName);
        if (_requestedScenePaths != null && !_requestedScenePaths.Contains(path))
        {
            return true;
        }
        if (!File.Exists(path))
        {
            return false;
        }

        if (_overwriteExistingScenes)
        {
            return false;
        }

        Debug.LogWarning(
            $"Skipping existing generated scene: {path}. Generator changes will NOT appear until it is " +
            "regenerated. Use Tools > Ray Tracing > Regenerate Scenes (Overwrite All), or " +
            "Regenerate Terrain Scene for terrain only.");
        return true;
    }

    private static string GetScenePath(string sceneName)
    {
        const string benchmarkPrefix = "Benchmark_";
        string generatedName = sceneName.StartsWith(benchmarkPrefix, StringComparison.Ordinal)
            ? sceneName.Substring(benchmarkPrefix.Length)
            : sceneName;
        return $"{GeneratedSceneFolder}/{generatedName}.unity";
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
