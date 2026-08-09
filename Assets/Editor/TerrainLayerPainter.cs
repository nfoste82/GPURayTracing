using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Declarative terrain layer weight painting.
///
/// WHY THIS EXISTS
/// ---------------
/// Terrain heightmaps in this project are produced by a long chain of fractal noise, Gaussian
/// massifs, ridged noise, valley subtraction, and edge falloff, and are then min/max normalized.
/// The resulting elevation distribution is NOT uniform on 0..1 and cannot be predicted by reading
/// the generator code. Writing raw thresholds such as "rock above 0.72" therefore fails silently:
/// the threshold may sit above the 99th percentile and paint almost nothing.
///
/// Rules here are instead expressed in two self-calibrating spaces:
///
///   * Elevation RANK (0..1), a percentile of the actual heightmap. Rank 0.9 always means
///     "the highest 10% of this terrain", whatever the noise chain produced.
///   * Slope DEGREES, a real world angle. 30 degrees always means 30 degrees, independent of
///     heightmap resolution and terrain size.
///
/// Both survive any change to the height generator. Call <see cref="Paint"/> to produce weights
/// and <see cref="BuildReport"/> to see what was actually painted.
/// </summary>
public static class TerrainLayerPainter
{
    /// <summary>Maximum layers the ray tracer reads (RGBA control texture).</summary>
    public const int MaxLayers = 4;

    /// <summary>
    /// One layer's placement rule. Elevation is given as a rank band, slope as a degree band.
    /// Both bands are optional; omitting one means "this layer does not care about it".
    ///
    /// Bands are soft: <see cref="Feather"/> controls how far outside the band the weight decays
    /// to zero, so adjacent layers blend instead of forming hard seams.
    /// </summary>
    public sealed class LayerRule
    {
        /// <summary>Layer index 0..3, matching TerrainData.terrainLayers order.</summary>
        public int LayerIndex;

        /// <summary>Human readable name; used only for reports.</summary>
        public string Name = string.Empty;

        /// <summary>
        /// Elevation percentile band this layer occupies, in 0..1 rank space.
        /// (0.0, 0.35) = "the lowest 35% of the terrain by area".
        /// </summary>
        public float MinElevationRank;
        public float MaxElevationRank = 1.0f;

        /// <summary>Real world slope band in degrees. Default accepts any slope.</summary>
        public float MinSlopeDegrees;
        public float MaxSlopeDegrees = 90.0f;

        /// <summary>
        /// Softness of the band edges. Elevation feather is in rank units, slope feather in
        /// degrees. Larger values produce wider, more natural transitions.
        /// </summary>
        public float ElevationFeatherRank = 0.08f;
        public float SlopeFeatherDegrees = 6.0f;

        /// <summary>
        /// Multiplied into the final weight. Use it to bias a layer that overlaps others without
        /// moving its bands. 1 is neutral.
        /// </summary>
        public float Strength = 1.0f;

        /// <summary>
        /// Optional 0..1 noise break-up amount. 0 keeps bands clean; higher values let a
        /// caller-supplied noise field mottle the boundary.
        /// </summary>
        public float NoiseInfluence;
    }

    /// <summary>Resolved numbers behind a rule, for reporting and tests.</summary>
    public readonly struct ResolvedBand
    {
        public readonly int LayerIndex;
        public readonly string Name;
        public readonly float MinElevation;
        public readonly float MaxElevation;
        public readonly float MinSlopeDegrees;
        public readonly float MaxSlopeDegrees;

        public ResolvedBand(int layerIndex, string name, float minElevation, float maxElevation,
            float minSlopeDegrees, float maxSlopeDegrees)
        {
            LayerIndex = layerIndex;
            Name = name;
            MinElevation = minElevation;
            MaxElevation = maxElevation;
            MinSlopeDegrees = minSlopeDegrees;
            MaxSlopeDegrees = maxSlopeDegrees;
        }
    }

    /// <summary>Everything needed to verify a paint pass without opening the Scene view.</summary>
    public sealed class PaintResult
    {
        /// <summary>[z, x, layer] normalized weights, ready for TerrainData.SetAlphamaps.</summary>
        public float[,,] Weights;

        /// <summary>Mean weight per layer over the whole alphamap.</summary>
        public float[] AverageWeight = new float[MaxLayers];

        /// <summary>Fraction 0..1 of texels where each layer is the single strongest.</summary>
        public float[] DominantFraction = new float[MaxLayers];

        /// <summary>
        /// Fraction 0..1 of texels no rule claimed, which fell back to the nearest band.
        /// Anything above a few percent means the rules leave gaps in the elevation range.
        /// </summary>
        public float FallbackFraction;

        /// <summary>
        /// Dominance counting ONLY texels a rule actually claimed. Coverage validation uses this
        /// so fallback fills cannot disguise a band that sits outside the real distribution.
        /// </summary>
        public float[] ClaimedDominantFraction = new float[MaxLayers];

        /// <summary>Elevation percentiles actually measured, keyed by the ranks in PercentileRanks.</summary>
        public float[] ElevationPercentiles;
        public float[] SlopePercentiles;
        public float[] PercentileRanks;

        /// <summary>Concrete height/slope values each rule resolved to.</summary>
        public ResolvedBand[] ResolvedBands;

        /// <summary>Layers whose dominance fell below the requested minimum, if any.</summary>
        public List<string> Warnings = new List<string>();
    }

    private static readonly float[] DefaultPercentileRanks =
        { 0.0f, 0.05f, 0.25f, 0.5f, 0.75f, 0.9f, 0.95f, 0.99f, 1.0f };

    /// <summary>
    /// Paints layer weights from declarative rules.
    /// </summary>
    /// <param name="heights">Normalized heightmap indexed [z, x], as produced by GetHeights.</param>
    /// <param name="terrainSize">Terrain size in metres; needed to convert slope to degrees.</param>
    /// <param name="alphamapResolution">Output alphamap resolution.</param>
    /// <param name="rules">One rule per layer, up to four.</param>
    /// <param name="noise">
    /// Optional noise sampler in normalized 0..1 terrain space returning 0..1, used by any rule
    /// with a non-zero NoiseInfluence. Pass null to disable break-up.
    /// </param>
    public static PaintResult Paint(
        float[,] heights,
        Vector3 terrainSize,
        int alphamapResolution,
        IReadOnlyList<LayerRule> rules,
        Func<float, float, float> noise = null)
    {
        if (heights == null) throw new ArgumentNullException(nameof(heights));
        if (rules == null || rules.Count == 0) throw new ArgumentException("At least one layer rule is required.", nameof(rules));
        if (rules.Count > MaxLayers) throw new ArgumentException($"At most {MaxLayers} layer rules are supported.", nameof(rules));
        if (alphamapResolution < 2) throw new ArgumentException("Alphamap resolution must be at least 2.", nameof(alphamapResolution));

        int heightResolution = heights.GetLength(0);
        var result = new PaintResult
        {
            Weights = new float[alphamapResolution, alphamapResolution, MaxLayers],
            PercentileRanks = DefaultPercentileRanks
        };

        // Measure the real distributions before resolving any rule.
        var sortedHeights = FlattenSorted(heights);
        var slopeField = BuildSlopeDegreeField(heights, terrainSize);
        var sortedSlopes = FlattenSorted(slopeField);

        result.ElevationPercentiles = Percentiles(sortedHeights, DefaultPercentileRanks);
        result.SlopePercentiles = Percentiles(sortedSlopes, DefaultPercentileRanks);

        // Resolve rank space into concrete heights once, so every texel uses the same thresholds.
        var bands = new ResolvedBand[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            LayerRule rule = rules[i];
            bands[i] = new ResolvedBand(
                rule.LayerIndex,
                string.IsNullOrEmpty(rule.Name) ? $"layer{rule.LayerIndex}" : rule.Name,
                QuantileOf(sortedHeights, rule.MinElevationRank),
                QuantileOf(sortedHeights, rule.MaxElevationRank),
                rule.MinSlopeDegrees,
                rule.MaxSlopeDegrees);
        }
        result.ResolvedBands = bands;

        // Feather widths are given in rank units, so convert them to height units using local
        // CDF spacing. This keeps transitions comparable in area regardless of distribution shape.
        var elevationFeather = new float[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            elevationFeather[i] = RankFeatherToHeight(sortedHeights, rules[i].ElevationFeatherRank);
        }

        var dominantCounts = new int[MaxLayers];
        var claimedCounts = new int[MaxLayers];
        var weightSums = new double[MaxLayers];
        var scratch = new float[MaxLayers];
        int fallbackCount = 0;

        for (int z = 0; z < alphamapResolution; z++)
        {
            for (int x = 0; x < alphamapResolution; x++)
            {
                // Sample height/slope at the alphamap texel centre in normalized terrain space.
                float u = alphamapResolution == 1 ? 0.0f : x / (float)(alphamapResolution - 1);
                float v = alphamapResolution == 1 ? 0.0f : z / (float)(alphamapResolution - 1);
                int hx = Mathf.Clamp(Mathf.RoundToInt(u * (heightResolution - 1)), 0, heightResolution - 1);
                int hz = Mathf.Clamp(Mathf.RoundToInt(v * (heightResolution - 1)), 0, heightResolution - 1);

                float height = heights[hz, hx];
                float slopeDegrees = slopeField[hz, hx];
                float noiseValue = noise?.Invoke(u, v) ?? 0.5f;

                for (int c = 0; c < MaxLayers; c++) scratch[c] = 0.0f;

                float total = 0.0f;
                for (int i = 0; i < rules.Count; i++)
                {
                    LayerRule rule = rules[i];
                    ResolvedBand band = bands[i];

                    float weight = SoftBand(height, band.MinElevation, band.MaxElevation, elevationFeather[i])
                        * SoftBand(slopeDegrees, band.MinSlopeDegrees, band.MaxSlopeDegrees, rule.SlopeFeatherDegrees)
                        * Mathf.Max(0.0f, rule.Strength);

                    if (rule.NoiseInfluence > 0.0f)
                    {
                        float influence = Mathf.Clamp01(rule.NoiseInfluence);
                        weight *= Mathf.Lerp(1.0f, Mathf.Clamp01(noiseValue), influence);
                    }

                    int index = Mathf.Clamp(rule.LayerIndex, 0, MaxLayers - 1);
                    scratch[index] += weight;
                    total += weight;
                }

                // Every texel must sum to 1. If no rule claimed this texel, fall back to the
                // layer whose elevation band centre is nearest, so terrain is never unpainted.
                // Fallback texels are counted separately: they indicate rule gaps, and they must
                // not be mistaken for genuine coverage by ValidateCoverage.
                bool claimed = total > 1e-6f;
                if (!claimed)
                {
                    int fallback = NearestBandLayer(bands, height);
                    scratch[fallback] = 1.0f;
                    total = 1.0f;
                    fallbackCount++;
                }

                int dominant = 0;
                float dominantWeight = -1.0f;
                for (int c = 0; c < MaxLayers; c++)
                {
                    float normalized = scratch[c] / total;
                    result.Weights[z, x, c] = normalized;
                    weightSums[c] += normalized;
                    if (normalized > dominantWeight)
                    {
                        dominantWeight = normalized;
                        dominant = c;
                    }
                }
                dominantCounts[dominant]++;
                if (claimed)
                {
                    claimedCounts[dominant]++;
                }
            }
        }

        float texelCount = alphamapResolution * (float)alphamapResolution;
        for (int c = 0; c < MaxLayers; c++)
        {
            result.AverageWeight[c] = (float)(weightSums[c] / texelCount);
            result.DominantFraction[c] = dominantCounts[c] / texelCount;
            result.ClaimedDominantFraction[c] = claimedCounts[c] / texelCount;
        }
        result.FallbackFraction = fallbackCount / texelCount;

        return result;
    }

    /// <summary>
    /// Flags layers that ended up effectively invisible. Call after Paint and log the warnings:
    /// this is the check that catches a band sitting outside the real distribution.
    /// </summary>
    public static void ValidateCoverage(PaintResult result, float minimumDominantFraction = 0.02f)
    {
        if (result?.ResolvedBands == null) return;

        // Gaps in the rule set are their own defect: fallback texels are painted, but by proximity
        // rather than intent, so they should not be read as a layer working correctly.
        if (result.FallbackFraction > 0.02f)
        {
            result.Warnings.Add(
                $"{result.FallbackFraction * 100.0f:F2}% of texels matched no rule and fell back to the " +
                "nearest elevation band. Widen the rule bands or their feather so the full elevation " +
                "range is covered.");
        }

        foreach (ResolvedBand band in result.ResolvedBands)
        {
            int index = Mathf.Clamp(band.LayerIndex, 0, MaxLayers - 1);
            float dominant = result.ClaimedDominantFraction[index];
            if (dominant < minimumDominantFraction)
            {
                result.Warnings.Add(
                    $"Layer {index} ({band.Name}) is genuinely dominant on only {dominant * 100.0f:F2}% of the " +
                    $"terrain (wanted at least {minimumDominantFraction * 100.0f:F2}%). Its elevation band resolved " +
                    $"to {band.MinElevation:F3}..{band.MaxElevation:F3} and slope band to " +
                    $"{band.MinSlopeDegrees:F1}..{band.MaxSlopeDegrees:F1} degrees. Adjust its elevation ranks " +
                    "or slope degrees.");
            }
        }
    }

    /// <summary>
    /// Human and LLM readable summary: measured distributions, resolved thresholds, per layer
    /// coverage, and a coarse dominance map. Reading this is much faster than inspecting pixels.
    /// </summary>
    public static string BuildReport(PaintResult result, string title = "Terrain Layer Coverage")
    {
        if (result == null) return string.Empty;

        var text = new StringBuilder();
        text.AppendLine($"=== {title} ===");

        text.AppendLine();
        text.AppendLine("Measured distributions (percentile: elevation / slope degrees)");
        for (int i = 0; i < result.PercentileRanks.Length; i++)
        {
            text.AppendLine(
                $"  p{result.PercentileRanks[i] * 100.0f,-5:0.#} " +
                $"elevation {result.ElevationPercentiles[i]:F4}   " +
                $"slope {result.SlopePercentiles[i]:F2} deg");
        }

        text.AppendLine();
        text.AppendLine("Resolved bands and coverage");
        foreach (ResolvedBand band in result.ResolvedBands)
        {
            int index = Mathf.Clamp(band.LayerIndex, 0, MaxLayers - 1);
            text.AppendLine(
                $"  layer {index} {band.Name,-18} " +
                $"elevation {band.MinElevation:F3}..{band.MaxElevation:F3}  " +
                $"slope {band.MinSlopeDegrees:F0}..{band.MaxSlopeDegrees:F0} deg  " +
                $"avg {result.AverageWeight[index]:F4}  dominant {result.DominantFraction[index] * 100.0f:F1}%");
        }
        text.AppendLine($"  unclaimed texels filled by nearest band: {result.FallbackFraction * 100.0f:F2}%");

        text.AppendLine();
        text.AppendLine("Dominant layer map (digit = layer index, north/+Z at top)");
        text.Append(BuildDominanceMap(result.Weights, 32));

        if (result.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("WARNINGS");
            foreach (string warning in result.Warnings)
            {
                text.AppendLine($"  - {warning}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// ASCII dominance map. Deliberately low resolution: a glance shows whether a layer is
    /// missing or whether the whole terrain collapsed to one texture.
    /// </summary>
    public static string BuildDominanceMap(float[,,] weights, int size)
    {
        if (weights == null) return string.Empty;

        int resolution = weights.GetLength(0);
        int layers = Mathf.Min(MaxLayers, weights.GetLength(2));
        int step = Mathf.Max(1, resolution / Mathf.Max(1, size));

        var text = new StringBuilder();
        for (int z = resolution - step; z >= 0; z -= step)
        {
            text.Append("  ");
            for (int x = 0; x < resolution; x += step)
            {
                int dominant = 0;
                float best = -1.0f;
                for (int c = 0; c < layers; c++)
                {
                    if (weights[z, x, c] > best)
                    {
                        best = weights[z, x, c];
                        dominant = c;
                    }
                }
                text.Append(dominant);
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    /// <summary>
    /// Per-layer dominance restricted to a rectangle of normalized terrain space. Use it to check
    /// what the camera actually sees: whole-terrain averages can look healthy while the visible
    /// region is a single layer.
    /// </summary>
    public static float[] MeasureRegionDominance(float[,,] weights, Rect normalizedRegion)
    {
        var fractions = new float[MaxLayers];
        if (weights == null) return fractions;

        int resolution = weights.GetLength(0);
        int layers = Mathf.Min(MaxLayers, weights.GetLength(2));
        int minX = Mathf.Clamp(Mathf.FloorToInt(normalizedRegion.xMin * (resolution - 1)), 0, resolution - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(normalizedRegion.xMax * (resolution - 1)), 0, resolution - 1);
        int minZ = Mathf.Clamp(Mathf.FloorToInt(normalizedRegion.yMin * (resolution - 1)), 0, resolution - 1);
        int maxZ = Mathf.Clamp(Mathf.CeilToInt(normalizedRegion.yMax * (resolution - 1)), 0, resolution - 1);

        var counts = new int[MaxLayers];
        int total = 0;
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int dominant = 0;
                float best = -1.0f;
                for (int c = 0; c < layers; c++)
                {
                    if (weights[z, x, c] > best)
                    {
                        best = weights[z, x, c];
                        dominant = c;
                    }
                }
                counts[dominant]++;
                total++;
            }
        }

        if (total > 0)
        {
            for (int c = 0; c < MaxLayers; c++) fractions[c] = counts[c] / (float)total;
        }
        return fractions;
    }

    /// <summary>
    /// Slope in degrees per heightmap sample, using centred differences over real metres.
    /// </summary>
    public static float[,] BuildSlopeDegreeField(float[,] heights, Vector3 terrainSize)
    {
        int resolution = heights.GetLength(0);
        int maxIndex = resolution - 1;
        var slopes = new float[resolution, resolution];
        float runX = 2.0f * (terrainSize.x / Mathf.Max(1, maxIndex));
        float runZ = 2.0f * (terrainSize.z / Mathf.Max(1, maxIndex));

        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float dx = (heights[z, Mathf.Min(maxIndex, x + 1)] - heights[z, Mathf.Max(0, x - 1)]) * terrainSize.y;
                float dz = (heights[Mathf.Min(maxIndex, z + 1), x] - heights[Mathf.Max(0, z - 1), x]) * terrainSize.y;
                float gradientX = dx / runX;
                float gradientZ = dz / runZ;
                float gradient = Mathf.Sqrt(gradientX * gradientX + gradientZ * gradientZ);
                slopes[z, x] = Mathf.Atan(gradient) * Mathf.Rad2Deg;
            }
        }
        return slopes;
    }

    /// <summary>Concrete height at an elevation percentile. Rank 0.9 = highest 10% boundary.</summary>
    public static float ElevationAtRank(float[,] heights, float rank)
    {
        return QuantileOf(FlattenSorted(heights), rank);
    }

    private static float[] FlattenSorted(float[,] field)
    {
        int resolution = field.GetLength(0);
        int width = field.GetLength(1);
        var values = new float[resolution * width];
        int i = 0;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < width; x++)
            {
                values[i++] = field[z, x];
            }
        }
        Array.Sort(values);
        return values;
    }

    private static float[] Percentiles(float[] sorted, float[] ranks)
    {
        var values = new float[ranks.Length];
        for (int i = 0; i < ranks.Length; i++)
        {
            values[i] = QuantileOf(sorted, ranks[i]);
        }
        return values;
    }

    private static float QuantileOf(float[] sorted, float rank)
    {
        if (sorted.Length == 0) return 0.0f;
        float clamped = Mathf.Clamp01(rank);
        int index = Mathf.RoundToInt(clamped * (sorted.Length - 1));
        return sorted[Mathf.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Converts a feather expressed in rank units into height units, using the median CDF slope
    /// so the blend covers roughly the intended fraction of surface area.
    /// </summary>
    private static float RankFeatherToHeight(float[] sorted, float featherRank)
    {
        if (featherRank <= 0.0f || sorted.Length < 2) return 0.0f;
        float low = QuantileOf(sorted, Mathf.Clamp01(0.5f - featherRank * 0.5f));
        float high = QuantileOf(sorted, Mathf.Clamp01(0.5f + featherRank * 0.5f));
        return Mathf.Max(1e-5f, high - low);
    }

    /// <summary>
    /// 1 inside [min, max], smoothly decaying to 0 across <paramref name="feather"/> outside it.
    /// </summary>
    private static float SoftBand(float value, float min, float max, float feather)
    {
        if (max < min) return 0.0f;

        float width = Mathf.Max(0.0f, feather);
        if (width <= 0.0f) return value >= min && value <= max ? 1.0f : 0.0f;

        float rising = min <= 0.0f && value <= min ? 1.0f : Mathf.SmoothStep(min - width, min, value);
        float falling = 1.0f - Mathf.SmoothStep(max, max + width, value);
        return Mathf.Clamp01(Mathf.Min(rising, falling));
    }

    private static int NearestBandLayer(ResolvedBand[] bands, float height)
    {
        int nearest = Mathf.Clamp(bands[0].LayerIndex, 0, MaxLayers - 1);
        float bestDistance = float.PositiveInfinity;
        foreach (ResolvedBand band in bands)
        {
            float centre = (band.MinElevation + band.MaxElevation) * 0.5f;
            float distance = Mathf.Abs(height - centre);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = Mathf.Clamp(band.LayerIndex, 0, MaxLayers - 1);
            }
        }
        return nearest;
    }
}
