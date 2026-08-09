using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class RayTracingTerrain : MonoBehaviour
{
    [Tooltip("Unity Terrain used for the heightmap and TerrainLayer splat data.")]
    public Terrain Terrain;

    [Range(4, 64)]
    [Tooltip("Coarse min/max height cells uploaded to the GPU before heightfield refinement.")]
    public int AccelerationResolution = 32;

    [Range(4, 16)]
    public int MarchSteps = 12;

    [Range(2, 8)]
    public int RefinementSteps = 5;

    [Range(1, 123456789)]
    [Tooltip("Seed used by the generated terrain scene. This component does not regenerate TerrainData at runtime.")]
    public int Seed = 481516;

    public TerrainData Data => Terrain != null ? Terrain.terrainData : null;

    public int HeightmapResolution => Data != null ? Data.heightmapResolution : 1;

    private void OnEnable()
    {
        if (Terrain == null)
        {
            Terrain = GetComponent<Terrain>();
        }

        if (Terrain != null && !Application.isPlaying)
        {
            Terrain.drawHeightmap = true;
        }

        GameManager manager = GetComponentInParent<GameManager>();
        if (manager == null)
        {
            Debug.LogError($"RayTracingTerrain '{name}' must be a child of a GameManager.", this);
            enabled = false;
            return;
        }

        if (!manager.RegisterTerrain(this))
        {
            enabled = false;
        }
    }

    private void OnDisable()
    {
        GameManager manager = GetComponentInParent<GameManager>();
        if (manager != null)
        {
            manager.UnregisterTerrain(this);
        }
    }

    public TerrainCell[] BuildCells()
    {
        TerrainData data = Data;
        int resolution = Mathf.Clamp(AccelerationResolution, 4, 64);
        var cells = new TerrainCell[resolution * resolution];
        if (data == null)
        {
            return cells;
        }

        float[,] heights = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
        int heightResolution = data.heightmapResolution;
        for (int z = 0; z < resolution; z++)
        {
            int minZ = z * (heightResolution - 1) / resolution;
            int maxZ = (z + 1) * (heightResolution - 1) / resolution;
            for (int x = 0; x < resolution; x++)
            {
                int minX = x * (heightResolution - 1) / resolution;
                int maxX = (x + 1) * (heightResolution - 1) / resolution;
                float minimum = 1.0f;
                float maximum = 0.0f;
                for (int sampleZ = minZ; sampleZ <= maxZ; sampleZ++)
                {
                    for (int sampleX = minX; sampleX <= maxX; sampleX++)
                    {
                        float height = heights[sampleZ, sampleX];
                        minimum = Mathf.Min(minimum, height);
                        maximum = Mathf.Max(maximum, height);
                    }
                }

                cells[z * resolution + x] = new TerrainCell { minHeight = minimum, maxHeight = maximum };
            }
        }

        return cells;
    }

    public float[] BuildHeights()
    {
        TerrainData data = Data;
        if (data == null)
        {
            return Array.Empty<float>();
        }

        int resolution = data.heightmapResolution;
        float[,] source = data.GetHeights(0, 0, resolution, resolution);
        var heights = new float[resolution * resolution];
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                heights[z * resolution + x] = source[z, x];
            }
        }
        return heights;
    }

    public Color[] BuildAlphamap()
    {
        TerrainData data = Data;
        if (data == null)
        {
            return Array.Empty<Color>();
        }

        int width = data.alphamapWidth;
        int height = data.alphamapHeight;
        int layerCount = Mathf.Min(4, data.alphamapLayers);
        float[,,] source = data.GetAlphamaps(0, 0, width, height);
        var pixels = new Color[width * height];
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                var weights = Color.clear;
                if (layerCount > 0) weights.r = source[z, x, 0];
                if (layerCount > 1) weights.g = source[z, x, 1];
                if (layerCount > 2) weights.b = source[z, x, 2];
                if (layerCount > 3) weights.a = source[z, x, 3];
                pixels[z * width + x] = weights;
            }
        }
        return pixels;
    }

    public Vector4 GetAverageLayerWeights()
    {
        Color[] pixels = BuildAlphamap();
        if (pixels.Length == 0)
        {
            return Vector4.zero;
        }

        Vector4 sum = Vector4.zero;
        for (int i = 0; i < pixels.Length; i++)
        {
            sum += new Vector4(pixels[i].r, pixels[i].g, pixels[i].b, pixels[i].a);
        }
        return sum / pixels.Length;
    }

    [Serializable]
    public struct TerrainCell
    {
        public float minHeight;
        public float maxHeight;
    }
}
