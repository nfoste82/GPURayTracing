using System.IO;
using UnityEditor;
using UnityEngine;

internal static class TerrainSceneGenerator
{
    private const string GeneratedAssetFolder = "Assets/Scenes/Generated/GeneratedAssets";
    private const string TerrainPreviewMaterialPath = GeneratedAssetFolder + "/TerrainPreview.mat";
    private static readonly string[] TerrainTexturePaths =
    {
        "Assets/Textures/Terrain/dirt_floor_diff_2k.jpg",
        "Assets/Textures/Terrain/sparse_grass_diff_2k.jpg",
        "Assets/Textures/Terrain/rock_05_diff_2k.jpg",
        "Assets/Textures/Terrain/rock_boulder_dry_diff_2k.jpg"
    };
    private static readonly string[] TerrainNormalTexturePaths =
    {
        "Assets/Textures/Terrain/dirt_floor_nor_gl_2k.png",
        "Assets/Textures/Terrain/sparse_grass_nor_gl_2k.png",
        "Assets/Textures/Terrain/rock_05_nor_gl_2k.png",
        "Assets/Textures/Terrain/rock_boulder_dry_nor_gl_2k.png"
    };
    private static readonly string[] TerrainMaskTexturePaths =
    {
        "Assets/Textures/Terrain/dirt_floor_mask_2k.png",
        "Assets/Textures/Terrain/sparse_grass_mask_2k.png",
        "Assets/Textures/Terrain/rock_05_mask_2k.png",
        "Assets/Textures/Terrain/rock_boulder_dry_mask_2k.png"
    };
    private static readonly float[] TerrainTextureTileSizes = { 7.0f, 9.0f, 6.0f, 8.0f };

    public static void CreateTerrainScene()
    {
        const string sceneName = "Terrain";
        const int seed = 481516;
        if (RayTracingSceneGenerator.ShouldSkipExistingScene(sceneName))
        {
            return;
        }

        Directory.CreateDirectory(GeneratedAssetFolder);
        var terrainData = CreateSeededTerrainData(seed);
        var context = RayTracingSceneGenerator.CreateBaseScene(new SceneSettings
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
            DirectionalLightIntensity = 5.0f,
            SkyboxLightColor = new Color32(227, 206, 206, 255)
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

        RayTracingSceneGenerator.Save(context.Scene, sceneName);
    }

    private static TerrainData CreateSeededTerrainData(int seed)
    {
        EditorUtility.DisplayProgressBar("Generating Terrain", "Building 1 km heightfield", 0.05f);
        TerrainLayer[] layers = new TerrainLayer[4];
        try
        {
            const string path = GeneratedAssetFolder + "/SeededTerrain.asset";
            var terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (terrainData == null)
            {
                terrainData = new TerrainData();
                AssetDatabase.CreateAsset(terrainData, path);
            }

            const int resolution = 257;
            terrainData.heightmapResolution = resolution;
            terrainData.size = new Vector3(1000.0f, 100.0f, 1000.0f);
            var heights = new float[resolution, resolution];
            var random = new System.Random(seed);
            var offsetX = (float)random.NextDouble() * 64.0f;
            var offsetZ = (float)random.NextDouble() * 64.0f;
            var massifA = new Vector2(0.20f, 0.68f);
            var massifB = new Vector2(0.77f, 0.72f);
            var massifC = new Vector2(0.62f, 0.30f);
            var valleyOffset = Mathf.Lerp(-0.08f, 0.08f, (float)random.NextDouble());
            for (var z = 0; z < resolution; z++)
            {
                if ((z & 15) == 0)
                {
                    EditorUtility.DisplayProgressBar("Generating Terrain", "Building natural landforms", 0.05f + 0.60f * z / (resolution - 1));
                }
                for (var x = 0; x < resolution; x++)
                {
                    var nx = (float)x / (resolution - 1);
                    var nz = (float)z / (resolution - 1);
                    var warpX = FractalNoise(offsetX + nx * 2.0f, offsetZ + nz * 2.0f, 3, 2.0f, 0.5f) - 0.5f;
                    var warpZ = FractalNoise(offsetX + 17.0f + nx * 2.0f, offsetZ + 31.0f + nz * 2.0f, 3, 2.0f, 0.5f) - 0.5f;
                    var warpedX = nx + warpX * 0.16f;
                    var warpedZ = nz + warpZ * 0.16f;
                    var continental = FractalNoise(offsetX + warpedX * 1.15f, offsetZ + warpedZ * 1.15f, 4, 2.0f, 0.52f);
                    var foothills = FractalNoise(offsetX + 13.0f + warpedX * 4.0f, offsetZ + 7.0f + warpedZ * 4.0f, 4, 2.05f, 0.5f);
                    var detail = FractalNoise(offsetX + 29.0f + warpedX * 13.0f, offsetZ + 41.0f + warpedZ * 13.0f, 3, 2.2f, 0.48f);
                    var ridges = RidgedNoise(offsetX + 47.0f + warpedX * 4.8f, offsetZ + 19.0f + warpedZ * 4.8f, 4);
                    var mountainMask = Mathf.Clamp01((continental - 0.43f) / 0.36f);
                    var massifs = Gaussian(warpedX, warpedZ, massifA, 0.13f, 0.34f)
                                  + Gaussian(warpedX, warpedZ, massifB, 0.16f, 0.29f)
                                  + Gaussian(warpedX, warpedZ, massifC, 0.11f, 0.20f);
                    var valleyCenter = 0.48f + valleyOffset + Mathf.Sin(warpedZ * Mathf.PI * 1.3f) * 0.14f + warpX * 0.08f;
                    var valleyDistance = Mathf.Abs(warpedX - valleyCenter);
                    var valley = (1.0f - Mathf.SmoothStep(0.035f, 0.20f, valleyDistance)) * Mathf.SmoothStep(0.02f, 0.15f, warpedZ);
                    var openBasin = Gaussian(warpedX, warpedZ, new Vector2(0.30f, 0.24f), 0.20f, 1.0f);
                    var edgeDistance = Mathf.Min(Mathf.Min(nx, 1.0f - nx), Mathf.Min(nz, 1.0f - nz));
                    var edge = Mathf.SmoothStep(0.0f, 0.08f, edgeDistance);
                    var height = 0.075f + continental * 0.11f + foothills * 0.055f + detail * 0.018f;
                    height += massifs + ridges * mountainMask * 0.16f;
                    height -= valley * 0.19f + openBasin * 0.055f;
                    heights[z, x] = Mathf.Clamp01(Mathf.Lerp(0.035f, height, edge));
                }
            }
            NormalizeTerrainHeights(heights, 0.02f, 0.98f);
            terrainData.SetHeights(0, 0, heights);

            EditorUtility.DisplayProgressBar("Generating Terrain", "Assigning terrain textures", 0.70f);
            Color[] colors = { new(0.20f, 0.29f, 0.11f), new(0.42f, 0.55f, 0.18f), new(0.48f, 0.34f, 0.17f), new(0.80f, 0.78f, 0.68f) };
            for (var i = 0; i < layers.Length; i++)
            {
                ConfigureTerrainTexture(TerrainTexturePaths[i]);
                ConfigureTerrainDataTexture(TerrainNormalTexturePaths[i]);
                ConfigureTerrainDataTexture(TerrainMaskTexturePaths[i]);
                layers[i] = CreateTerrainLayer(i, colors[i], TerrainTexturePaths[i], TerrainNormalTexturePaths[i], TerrainMaskTexturePaths[i], TerrainTextureTileSizes[i]);
            }
            terrainData.terrainLayers = layers;

            EditorUtility.DisplayProgressBar("Generating Terrain", "Painting terrain materials", 0.75f);
            terrainData.alphamapResolution = 128;
            var rules = new[]
            {
                new TerrainLayerPainter.LayerRule { LayerIndex = 0, Name = "dirt_floor", MinElevationRank = 0.00f, MaxElevationRank = 0.34f, ElevationFeatherRank = 0.10f },
                new TerrainLayerPainter.LayerRule { LayerIndex = 1, Name = "sparse_grass", MinElevationRank = 0.28f, MaxElevationRank = 0.72f, MaxSlopeDegrees = 26.0f, SlopeFeatherDegrees = 8.0f, ElevationFeatherRank = 0.10f, NoiseInfluence = 0.25f },
                new TerrainLayerPainter.LayerRule { LayerIndex = 2, Name = "rock_05", MinElevationRank = 0.62f, MaxElevationRank = 0.94f, ElevationFeatherRank = 0.10f },
                new TerrainLayerPainter.LayerRule { LayerIndex = 3, Name = "rock_boulder_dry", MinElevationRank = 0.88f, MaxElevationRank = 1.00f, ElevationFeatherRank = 0.08f }
            };
            var paint = TerrainLayerPainter.Paint(heights, terrainData.size, terrainData.alphamapResolution, rules, (u, v) => FractalNoise(offsetX + u * 5.0f, offsetZ + v * 5.0f, 3, 2.0f, 0.5f));
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
        var sum = 0.0f;
        var amplitude = 1.0f;
        var frequency = 1.0f;
        var amplitudeSum = 0.0f;
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
        var noise = FractalNoise(x, z, octaves, 2.0f, 0.52f);
        return Mathf.Pow(1.0f - Mathf.Abs(noise * 2.0f - 1.0f), 2.0f);
    }

    private static float Gaussian(float x, float z, Vector2 center, float radius, float amplitude)
    {
        var deltaX = x - center.x;
        var deltaZ = z - center.y;
        return amplitude * Mathf.Exp(-(deltaX * deltaX + deltaZ * deltaZ) / (2.0f * radius * radius));
    }

    private static void NormalizeTerrainHeights(float[,] heights, float minimum, float maximum)
    {
        var sourceMinimum = float.PositiveInfinity;
        var sourceMaximum = float.NegativeInfinity;
        var resolution = heights.GetLength(0);
        for (var z = 0; z < resolution; z++)
        {
            for (var x = 0; x < resolution; x++)
            {
                sourceMinimum = Mathf.Min(sourceMinimum, heights[z, x]);
                sourceMaximum = Mathf.Max(sourceMaximum, heights[z, x]);
            }
        }
        var sourceRange = Mathf.Max(0.0001f, sourceMaximum - sourceMinimum);
        for (var z = 0; z < resolution; z++)
        {
            for (var x = 0; x < resolution; x++)
            {
                var normalized = (heights[z, x] - sourceMinimum) / sourceRange;
                heights[z, x] = Mathf.Lerp(minimum, maximum, normalized);
            }
        }
    }

    private static TerrainLayer CreateTerrainLayer(int index, Color fallbackColor, string texturePath, string normalPath, string maskPath, float tileSize)
    {
        var layerPath = GeneratedAssetFolder + $"/SeededTerrainLayer{index}.terrainlayer";
        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, layerPath);
        }
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogWarning($"Terrain texture is missing at {texturePath}; using the generated fallback swatch.");
            texture = CreateTerrainColorTexture(index, fallbackColor);
        }
        layer.diffuseTexture = texture;
        layer.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
        layer.maskMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
        layer.metallic = 1.0f;
        layer.smoothness = 1.0f;
        layer.normalScale = 1.0f;
        layer.tileSize = Vector2.one * tileSize;
        EditorUtility.SetDirty(layer);
        return layer;
    }

    private static Material GetOrCreateTerrainPreviewMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainPreviewMaterialPath);
        var shader = Shader.Find("Nature/Terrain/RayTracingPreview");
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
        if (importer == null) return;
        var changed = false;
        if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; changed = true; }
        if (importer.textureCompression != TextureImporterCompression.CompressedHQ) { importer.textureCompression = TextureImporterCompression.CompressedHQ; changed = true; }
        if (changed) importer.SaveAndReimport();
    }

    private static void ConfigureTerrainDataTexture(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        var changed = false;
        if (importer.sRGBTexture) { importer.sRGBTexture = false; changed = true; }
        if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; changed = true; }
        if (importer.textureCompression != TextureImporterCompression.CompressedHQ) { importer.textureCompression = TextureImporterCompression.CompressedHQ; changed = true; }
        if (changed) importer.SaveAndReimport();
    }

    private static Texture2D CreateTerrainColorTexture(int index, Color color)
    {
        string texturePath = GeneratedAssetFolder + $"/SeededTerrainLayer{index}.asset";
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture != null) AssetDatabase.DeleteAsset(texturePath);
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = $"Seeded Terrain Layer {index}" };
        texture.SetPixels(new[] { color, color, color, color });
        texture.Apply(false, false);
        AssetDatabase.CreateAsset(texture, texturePath);
        return texture;
    }
}
