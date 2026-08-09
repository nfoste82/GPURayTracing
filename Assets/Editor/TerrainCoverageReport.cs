using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Reports what is ACTUALLY painted on a terrain, read back from TerrainData.
///
/// This closes the feedback loop for terrain layer work. Without it, verifying a paint pass means
/// eyeballing the Scene view or parsing Unity's binary TerrainData serialization; both are slow and
/// neither produces numbers you can reason about or diff. Run this after any change to terrain
/// painting or height generation and read the table.
///
/// Symptoms and what they mean:
///   * one layer dominant on ~100% of texels  -> bands sit outside the real elevation distribution
///   * all layers with similar nonzero weight -> bands overlap everywhere, producing uniform mush
///   * healthy whole-terrain split but a single-layer visible region -> camera is looking at a
///     flat area such as the edge falloff band
/// </summary>
public static class TerrainCoverageReport
{
    private const string TerrainScenePath = "Assets/Scenes/Generated/Terrain.unity";

    [MenuItem("Tools/Ray Tracing/Report Terrain Coverage")]
    public static void ReportActiveSceneTerrain()
    {
        Terrain terrain = Object.FindFirstObjectByType<Terrain>();
        if (terrain == null)
        {
            Scene active = SceneManager.GetActiveScene();
            if (active.path != TerrainScenePath)
            {
                if (!EditorUtility.DisplayDialog(
                        "No Terrain In Active Scene",
                        $"Open {TerrainScenePath} and report its terrain coverage?",
                        "Open Scene",
                        "Cancel"))
                {
                    return;
                }
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }
                EditorSceneManager.OpenScene(TerrainScenePath, OpenSceneMode.Single);
                terrain = Object.FindFirstObjectByType<Terrain>();
            }
        }

        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogWarning("No Terrain with TerrainData found; open a scene containing one.");
            return;
        }

        Debug.Log(BuildReport(terrain));
    }

    /// <summary>
    /// Builds the report from live TerrainData. Safe to call from tests.
    /// </summary>
    public static string BuildReport(Terrain terrain)
    {
        TerrainData data = terrain.terrainData;
        int resolution = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, resolution, resolution);
        float[,,] weights = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);

        // Reuse the painter's reporting by rebuilding an equivalent result from real data.
        var result = new TerrainLayerPainter.PaintResult
        {
            Weights = weights,
            PercentileRanks = new[] { 0.0f, 0.05f, 0.25f, 0.5f, 0.75f, 0.9f, 0.95f, 0.99f, 1.0f }
        };

        float[,] slopes = TerrainLayerPainter.BuildSlopeDegreeField(heights, data.size);
        result.ElevationPercentiles = PercentilesOf(heights, result.PercentileRanks);
        result.SlopePercentiles = PercentilesOf(slopes, result.PercentileRanks);

        int layers = Mathf.Min(TerrainLayerPainter.MaxLayers, data.alphamapLayers);
        var counts = new int[TerrainLayerPainter.MaxLayers];
        var sums = new double[TerrainLayerPainter.MaxLayers];
        int texels = data.alphamapWidth * data.alphamapHeight;

        for (int z = 0; z < data.alphamapHeight; z++)
        {
            for (int x = 0; x < data.alphamapWidth; x++)
            {
                int dominant = 0;
                float best = -1.0f;
                for (int c = 0; c < layers; c++)
                {
                    float weight = weights[z, x, c];
                    sums[c] += weight;
                    if (weight > best)
                    {
                        best = weight;
                        dominant = c;
                    }
                }
                counts[dominant]++;
            }
        }

        var bands = new TerrainLayerPainter.ResolvedBand[layers];
        TerrainLayer[] terrainLayers = data.terrainLayers;
        for (int c = 0; c < layers; c++)
        {
            result.AverageWeight[c] = (float)(sums[c] / texels);
            result.DominantFraction[c] = counts[c] / (float)texels;
            // Reading back finished data, there is no notion of an unclaimed texel: every texel
            // already has weights. Claimed dominance therefore equals observed dominance, which
            // lets ValidateCoverage flag layers that are effectively invisible.
            result.ClaimedDominantFraction[c] = counts[c] / (float)texels;

            string name = c < terrainLayers.Length && terrainLayers[c] != null && terrainLayers[c].diffuseTexture != null
                ? terrainLayers[c].diffuseTexture.name
                : $"layer{c}";
            // Bands are unknown when reading back, so report the observed elevation span instead.
            bands[c] = new TerrainLayerPainter.ResolvedBand(c, name, 0.0f, 0.0f, 0.0f, 0.0f);
        }
        result.ResolvedBands = bands;
        TerrainLayerPainter.ValidateCoverage(result);

        string report = TerrainLayerPainter.BuildReport(result, $"Painted Coverage: {terrain.name}");

        // The camera matters: a healthy global split can still render as one texture.
        Camera camera = Camera.main;
        if (camera != null)
        {
            Rect region = EstimateVisibleRegion(camera, terrain);
            float[] visible = TerrainLayerPainter.MeasureRegionDominance(weights, region);
            report += $"\nVisible-region dominance (normalized terrain rect " +
                      $"x {region.xMin:F2}..{region.xMax:F2}, z {region.yMin:F2}..{region.yMax:F2})\n";
            for (int c = 0; c < layers; c++)
            {
                report += $"  layer {c} {bands[c].Name,-18} {visible[c] * 100.0f:F1}%\n";
            }
        }

        return report;
    }

    /// <summary>
    /// Coarse axis-aligned estimate of the terrain area in front of the camera, in normalized
    /// terrain space. Intended for coverage sanity checks, not exact culling.
    /// </summary>
    private static Rect EstimateVisibleRegion(Camera camera, Terrain terrain)
    {
        TerrainData data = terrain.terrainData;
        Vector3 origin = terrain.transform.position;
        Vector3 size = data.size;

        Vector3 cameraPosition = camera.transform.position;
        Vector3 forward = camera.transform.forward;
        const float viewDistance = 400.0f;
        Vector3 target = cameraPosition + forward * viewDistance;

        float minX = Mathf.Min(cameraPosition.x, target.x) - 60.0f;
        float maxX = Mathf.Max(cameraPosition.x, target.x) + 60.0f;
        float minZ = Mathf.Min(cameraPosition.z, target.z);
        float maxZ = Mathf.Max(cameraPosition.z, target.z);

        float u0 = Mathf.Clamp01((minX - origin.x) / size.x);
        float u1 = Mathf.Clamp01((maxX - origin.x) / size.x);
        float v0 = Mathf.Clamp01((minZ - origin.z) / size.z);
        float v1 = Mathf.Clamp01((maxZ - origin.z) / size.z);
        return Rect.MinMaxRect(Mathf.Min(u0, u1), Mathf.Min(v0, v1), Mathf.Max(u0, u1), Mathf.Max(v0, v1));
    }

    private static float[] PercentilesOf(float[,] field, float[] ranks)
    {
        int height = field.GetLength(0);
        int width = field.GetLength(1);
        var values = new float[height * width];
        int i = 0;
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                values[i++] = field[z, x];
            }
        }
        System.Array.Sort(values);

        var output = new float[ranks.Length];
        for (int r = 0; r < ranks.Length; r++)
        {
            int index = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(ranks[r]) * (values.Length - 1)), 0, values.Length - 1);
            output[r] = values[index];
        }
        return output;
    }
}
