using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

public class RayTracingBenchmarkRunner : MonoBehaviour
{
    public GameManager gameManager;
    public bool showOverlay = false;
    public KeyCode toggleKey = KeyCode.X;
    public KeyCode runKey = KeyCode.B;
    public int warmupFrames = 30;
    public int measurementFrames = 120;
    public int trialsPerConfiguration = 3;
    [Tooltip("Benchmarks exponentially increasing caustic photon counts, then measures caustics disabled.")]
    public bool sweepCausticPhotonCounts = false;
    public float impracticalOverheadPercent = 25.0f;
    public int targetFrameRate = 60;
    [Min(64)] public int sweepStartPhotonCount = 1 << 10;
    [Min(2)] public int sweepPhotonCountMultiplier = 2;
    [Min(1)] public int sweepFramesPerPhotonCount = 10;
    [Min(1)] public int sweepExtendedFrameCount = 30;
    [Min(0.0f)] public float cooldownSeconds = 5.0f;
    [Min(64)] public int sweepMaximumPhotonCount = 1 << 21;

    private readonly List<Result> _results = new List<Result>();
    private readonly List<Summary> _summaries = new List<Summary>();
    private Coroutine _benchmarkCoroutine;
    private string _status = "Press B to benchmark this scene";
    private string _lastCsvPath;
    private string _benchmarkMetadata;
    private GUIStyle _style;
    private bool _hasMeasuredConfiguration;

    private void Awake()
    {
        if (gameManager == null)
        {
            gameManager = FindFirstObjectByType<GameManager>();
        }

        if (gameManager != null && gameObject != gameManager.gameObject)
        {
            showOverlay = false;
            enabled = false;
            Destroy(this);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showOverlay = !showOverlay;
        }

        if (showOverlay && Input.GetKeyDown(runKey) && _benchmarkCoroutine == null && gameManager != null)
        {
            _benchmarkCoroutine = StartCoroutine(RunBenchmark());
        }
    }

    private IEnumerator RunBenchmark()
    {
        bool originalCausticsEnabled = gameManager.enableCaustics;
        int originalPhotonCount = gameManager.Caustics.PhotonCount;
        int originalTargetFrameRate = Application.targetFrameRate;
        int originalVSyncCount = QualitySettings.vSyncCount;

        _results.Clear();
        _summaries.Clear();
        _lastCsvPath = null;
        _hasMeasuredConfiguration = false;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        _benchmarkMetadata = BuildMetadata();

        try
        {
            if (sweepCausticPhotonCounts)
            {
                int startPhotonCount = Mathf.Clamp(sweepStartPhotonCount, 64, sweepMaximumPhotonCount);
                int photonCount = startPhotonCount;
                float firstCausticsMs = 0.0f;
                var testedPhotonCounts = new List<int>();
                while (true)
                {
                    yield return MeasureConfiguration(true, photonCount, Mathf.Max(1, sweepFramesPerPhotonCount));
                    testedPhotonCounts.Add(photonCount);
                    Summary summary = _summaries[_summaries.Count - 1];
                    if (photonCount == startPhotonCount)
                    {
                        firstCausticsMs = summary.MedianFrameMs;
                    }
                    else if (firstCausticsMs > 0.0f
                        && summary.MedianFrameMs >= firstCausticsMs * (1.0f + impracticalOverheadPercent / 100.0f))
                    {
                        break;
                    }

                    if (photonCount >= sweepMaximumPhotonCount)
                    {
                        break;
                    }

                    photonCount = Mathf.Min(sweepMaximumPhotonCount,
                        Mathf.Max(photonCount + 1, photonCount * Mathf.Max(2, sweepPhotonCountMultiplier)));
                }

                int extendedStart = Mathf.Max(0, testedPhotonCounts.Count - 2);
                for (int i = extendedStart; i < testedPhotonCounts.Count; i++)
                {
                    yield return MeasureConfiguration(true, testedPhotonCounts[i], Mathf.Max(1, sweepExtendedFrameCount));
                }

                yield return MeasureConfiguration(false, 0, Mathf.Max(1, sweepExtendedFrameCount));
            }
            else
            {
                yield return MeasureConfiguration(originalCausticsEnabled, originalPhotonCount, measurementFrames);
            }

            _lastCsvPath = WriteCsv();
            _status = $"Complete: {_results.Count} configurations";
            Debug.Log($"Scene benchmark complete. Results written to {_lastCsvPath}");
        }
        finally
        {
            gameManager.SetRenderingPaused(false);
            gameManager.enableCaustics = originalCausticsEnabled;
            gameManager.Caustics.PhotonCount = originalPhotonCount;
            Application.targetFrameRate = originalTargetFrameRate;
            QualitySettings.vSyncCount = originalVSyncCount;
            _benchmarkCoroutine = null;
            _hasMeasuredConfiguration = false;
        }
    }

    private IEnumerator MeasureConfiguration(bool causticsEnabled, int photonCount, int frameCount)
    {
        yield return CooldownIfNeeded();
        gameManager.enableCaustics = causticsEnabled;
        if (causticsEnabled)
        {
            gameManager.Caustics.PhotonCount = photonCount;
        }
        gameManager.ResetFrameAccumulation();

        string label = sweepCausticPhotonCounts
            ? (causticsEnabled ? $"{photonCount} photons" : "caustics disabled")
            : SceneManager.GetActiveScene().name;
        gameManager.SetRenderingPaused(false);
        int warmupCount = Mathf.Max(1, warmupFrames);
        for (int i = 0; i < warmupCount; i++)
        {
            _status = $"Warming {label}: {i + 1}/{warmupCount}";
            yield return null;
        }

        int trialCount = Mathf.Max(1, trialsPerConfiguration);
        int sampleCount = Mathf.Max(1, frameCount);
        var trialAverages = new float[trialCount];
        for (int trial = 0; trial < trialCount; trial++)
        {
            double sumMs = 0.0;
            float minMs = float.MaxValue;
            float maxMs = 0.0f;
            for (int i = 0; i < sampleCount; i++)
            {
                yield return null;
                float frameMs = Time.unscaledDeltaTime * 1000.0f;
                sumMs += frameMs;
                minMs = Mathf.Min(minMs, frameMs);
                maxMs = Mathf.Max(maxMs, frameMs);
                _status = $"Measuring {label}, trial {trial + 1}/{trialCount}: {i + 1}/{sampleCount}";
            }

            float averageMs = (float)(sumMs / sampleCount);
            trialAverages[trial] = averageMs;
            _results.Add(new Result(causticsEnabled, photonCount, trial + 1, sampleCount, averageMs, minMs, maxMs));
        }

        Array.Sort(trialAverages);
        _summaries.Add(new Summary(causticsEnabled, photonCount, Median(trialAverages), sampleCount));
    }

    private IEnumerator CooldownIfNeeded()
    {
        if (!_hasMeasuredConfiguration)
        {
            _hasMeasuredConfiguration = true;
            yield break;
        }

        float duration = Mathf.Max(0.0f, cooldownSeconds);
        if (duration <= 0.0f)
        {
            yield break;
        }

        gameManager.SetRenderingPaused(true);
        _status = $"Cooling down for {duration:0.0} seconds";
        yield return new WaitForSecondsRealtime(duration);
    }

    private static float Median(float[] sortedValues)
    {
        int middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) * 0.5f
            : sortedValues[middle];
    }

    private string WriteCsv()
    {
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestCaptures", "CausticsBenchmarks"));
        Directory.CreateDirectory(directory);
        string sceneName = SanitizeFileName(SceneManager.GetActiveScene().name);
        string path = Path.Combine(directory, $"{sceneName}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var builder = new StringBuilder();
        builder.Append(_benchmarkMetadata);
        builder.AppendLine();
        builder.AppendLine("caustics_enabled,photon_count,trial,frames,average_frame_ms,min_frame_ms,max_frame_ms");
        for (int i = 0; i < _results.Count; i++)
        {
            Result result = _results[i];
            builder.Append(result.CausticsEnabled ? "true" : "false").Append(',')
                .Append(result.PhotonCount).Append(',')
                .Append(result.Trial).Append(',')
                .Append(result.Frames).Append(',')
                .Append(result.AverageFrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(result.MinFrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(result.MaxFrameMs.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine();
        }


        builder.AppendLine();
        builder.AppendLine("caustics_enabled,photon_count,frames,median_average_frame_ms,overhead_vs_first_caustics_percent,overhead_vs_disabled_percent,over_target_budget,impractical");
        int firstCausticsIndex = _summaries.FindIndex(summary => summary.CausticsEnabled);
        int disabledIndex = _summaries.FindIndex(summary => !summary.CausticsEnabled);
        float baselineMs = firstCausticsIndex >= 0 ? _summaries[firstCausticsIndex].MedianFrameMs : 0.0f;
        float disabledMs = disabledIndex >= 0 ? _summaries[disabledIndex].MedianFrameMs : 0.0f;
        float targetFrameMs = 1000.0f / Mathf.Max(1, targetFrameRate);
        for (int i = 0; i < _summaries.Count; i++)
        {
            Summary summary = _summaries[i];
            float overheadPercent = baselineMs > 0.0f ? (summary.MedianFrameMs / baselineMs - 1.0f) * 100.0f : 0.0f;
            float disabledOverheadPercent = disabledMs > 0.0f ? (summary.MedianFrameMs / disabledMs - 1.0f) * 100.0f : 0.0f;
            bool overTarget = summary.MedianFrameMs > targetFrameMs;
            bool impractical = sweepCausticPhotonCounts && summary.CausticsEnabled
                && (overheadPercent > impracticalOverheadPercent || overTarget);
            builder.Append(summary.CausticsEnabled ? "true" : "false").Append(',')
                .Append(summary.PhotonCount).Append(',')
                .Append(summary.Frames).Append(',')
                .Append(summary.MedianFrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(overheadPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(disabledOverheadPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(overTarget ? "true" : "false").Append(',')
                .Append(impractical ? "true" : "false").AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private string BuildMetadata()
    {
        var builder = new StringBuilder(1024);
        builder.AppendLine("setting,value");
        AppendSetting(builder, "scene", SceneManager.GetActiveScene().name);
        AppendSetting(builder, "unity_version", Application.unityVersion);
        AppendSetting(builder, "platform", Application.platform.ToString());
        AppendSetting(builder, "graphics_device", SystemInfo.graphicsDeviceName);
        AppendSetting(builder, "graphics_api", SystemInfo.graphicsDeviceType.ToString());
        AppendSetting(builder, "internal_resolution", $"{gameManager.TextureSize.x}x{gameManager.TextureSize.y}");
        AppendSetting(builder, "display_resolution", $"{gameManager.DisplayTextureSize.x}x{gameManager.DisplayTextureSize.y}");
        AppendSetting(builder, "render_resolution_percent", gameManager.renderResolutionPercent);
        AppendSetting(builder, "number_of_passes", gameManager.numberOfPasses);
        AppendSetting(builder, "num_bounces", gameManager.numBounces);
        AppendSetting(builder, "shadow_quality", gameManager.shadowQuality);
        AppendSetting(builder, "shadow_randomness", gameManager.shadowRandomness);
        AppendSetting(builder, "light_sampling_strategy", gameManager.Lighting.LightSamplingStrategy);
        AppendSetting(builder, "light_sample_count", gameManager.Lighting.LightSampleCount);
        AppendSetting(builder, "max_light_samples", gameManager.maxLightSamples);
        AppendSetting(builder, "frame_accumulation", gameManager.enableFrameAccumulation);
        AppendSetting(builder, "debug_render_mode", gameManager.debugRenderMode);
        AppendSetting(builder, "random_noise", gameManager.randomNoise);
        AppendSetting(builder, "camera_autofocus", gameManager.CameraManager.cameraAutoFocus);
        AppendSetting(builder, "camera_focal_distance", gameManager.CameraManager.cameraFocalDistance);
        AppendSetting(builder, "light_falloff_scale", gameManager.Lighting.LightFalloffScale);
        AppendSetting(builder, "exposure", gameManager.exposure);
        AppendSetting(builder, "firefly_clamp", gameManager.fireflyClamp);
        AppendSetting(builder, "environment_lighting", gameManager.enableEnvironmentLighting);
        AppendSetting(builder, "environment_light_sample_count", gameManager.environmentLightSampleCount);
        AppendSetting(builder, "environment_highlight_threshold", gameManager.environmentHighlightThreshold);
        AppendSetting(builder, "environment_highlight_soft_knee", gameManager.environmentHighlightSoftKnee);
        AppendSetting(builder, "environment_highlight_intensity", gameManager.environmentHighlightIntensity);
        AppendSetting(builder, "environment_distribution_resolution", $"{gameManager.EnvironmentDistributionWidth}x{gameManager.EnvironmentDistributionHeight}");
        AppendSetting(builder, "caustics_enabled", gameManager.enableCaustics);
        AppendSetting(builder, "caustic_photon_count", gameManager.enableCaustics ? gameManager.Caustics.PhotonCount : 0);
        AppendSetting(builder, "caustic_gather_radius", gameManager.Caustics.GatherRadius);
        AppendSetting(builder, "caustic_intensity", gameManager.Caustics.Intensity);
        AppendSetting(builder, "caustic_sweep_start_photons", sweepStartPhotonCount);
        AppendSetting(builder, "caustic_sweep_multiplier", sweepPhotonCountMultiplier);
        AppendSetting(builder, "caustic_sweep_frames_per_count", sweepFramesPerPhotonCount);
        AppendSetting(builder, "caustic_sweep_extended_frames", sweepExtendedFrameCount);
        AppendSetting(builder, "caustic_sweep_cooldown_seconds", cooldownSeconds);
        AppendSetting(builder, "caustic_sweep_max_photons", sweepMaximumPhotonCount);
        AppendSetting(builder, "fog_enabled", gameManager.IsVolumetricFogActive);
        AppendSetting(builder, "fog_density", gameManager.EffectiveFogDensity);
        AppendSetting(builder, "fog_density_scale", gameManager.fogDensityScale);
        AppendSetting(builder, "fog_scattering_scale", gameManager.fogScatteringScale);
        AppendSetting(builder, "fog_in_scattering_intensity", gameManager.fogInScatteringIntensity);
        AppendSetting(builder, "fog_multiple_scattering", gameManager.enableFogMultipleScattering);
        AppendSetting(builder, "water_present", gameManager.HasWaterVolume);
        AppendSetting(builder, "sphere_count", gameManager.SphereCount);
        AppendSetting(builder, "mesh_count", gameManager.MeshCount);
        AppendSetting(builder, "triangle_count", gameManager.TriangleCount);
        AppendSetting(builder, "light_count", gameManager.LightCount);
        AppendSetting(builder, "sphere_light_count", gameManager.SphereLightCount);
        AppendSetting(builder, "mesh_light_count", gameManager.MeshLightCount);
        AppendSetting(builder, "triangle_light_count", gameManager.TriangleLightCount);
        AppendSetting(builder, "top_level_bvh_active", gameManager.IsTopLevelBvhActive);
        AppendSetting(builder, "top_level_bvh_nodes", gameManager.TopLevelBvhNodeCount);
        AppendSetting(builder, "top_level_bvh_threshold", gameManager.topLevelBvhMinObjectCount);
        AppendSetting(builder, "shadow_bvh_active", gameManager.IsShadowBvhActive);
        AppendSetting(builder, "shadow_bvh_nodes", gameManager.ShadowBvhNodeCount);
        AppendSetting(builder, "shadow_bvh_threshold", gameManager.shadowBvhMinObjectCount);
        return builder.ToString();
    }

    private static void AppendSetting(StringBuilder builder, string name, object value)
    {
        string text = value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;
        builder.Append(name).Append(',').Append('"').Append(text.Replace("\"", "\"\"")).AppendLine("\"");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '-');
        }
        return string.IsNullOrWhiteSpace(value) ? "scene-benchmark" : value;
    }

    private void OnGUI()
    {
        if (!showOverlay || gameManager == null)
        {
            return;
        }

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                padding = new RectOffset(10, 10, 8, 8)
            };
            _style.normal.textColor = Color.white;
        }

        var builder = new StringBuilder(512);
        builder.Append("Scene benchmark (B)\n").AppendLine(_status);
        int firstCausticsIndex = _summaries.FindIndex(summary => summary.CausticsEnabled);
        float baselineMs = firstCausticsIndex >= 0 ? _summaries[firstCausticsIndex].MedianFrameMs : 0.0f;
        float targetFrameMs = 1000.0f / Mathf.Max(1, targetFrameRate);
        int practicalLimit = -1;
        for (int i = 0; i < _summaries.Count; i++)
        {
            Summary summary = _summaries[i];
            float overheadPercent = baselineMs > 0.0f ? (summary.MedianFrameMs / baselineMs - 1.0f) * 100.0f : 0.0f;
            bool impractical = sweepCausticPhotonCounts && summary.CausticsEnabled
                && (overheadPercent > impracticalOverheadPercent || summary.MedianFrameMs > targetFrameMs);
            builder.Append(sweepCausticPhotonCounts
                    ? (summary.CausticsEnabled ? summary.PhotonCount.ToString() : "Disabled")
                    : SceneManager.GetActiveScene().name)
                .Append(": ").Append(summary.MedianFrameMs.ToString("0.00")).Append(" ms median, ")
                .Append(summary.Frames).Append(" frames")
                .Append(sweepCausticPhotonCounts && summary.CausticsEnabled ? $" ({overheadPercent:+0.0;-0.0;0.0}%)" : string.Empty)
                .AppendLine(impractical ? "  LIMIT" : string.Empty);
            if (impractical && practicalLimit < 0)
            {
                practicalLimit = summary.PhotonCount;
            }
        }

        if (sweepCausticPhotonCounts && _summaries.Count > 0)
        {
            builder.Append("First impractical count: ").AppendLine(practicalLimit < 0 ? "not reached" : practicalLimit.ToString());
        }

        if (!string.IsNullOrEmpty(_lastCsvPath))
        {
            builder.Append("CSV: ").Append(_lastCsvPath);
        }

        builder.Append("\nToggle: ").Append(toggleKey);
        GUI.Box(new Rect(12, 350, 620, 210), builder.ToString(), _style);
    }

    private readonly struct Result
    {
        public readonly bool CausticsEnabled;
        public readonly int PhotonCount;
        public readonly int Trial;
        public readonly int Frames;
        public readonly float AverageFrameMs;
        public readonly float MinFrameMs;
        public readonly float MaxFrameMs;

        public Result(bool causticsEnabled, int photonCount, int trial, int frames, float averageFrameMs, float minFrameMs, float maxFrameMs)
        {
            CausticsEnabled = causticsEnabled;
            PhotonCount = photonCount;
            Trial = trial;
            Frames = frames;
            AverageFrameMs = averageFrameMs;
            MinFrameMs = minFrameMs;
            MaxFrameMs = maxFrameMs;
        }
    }

    private readonly struct Summary
    {
        public readonly bool CausticsEnabled;
        public readonly int PhotonCount;
        public readonly float MedianFrameMs;
        public readonly int Frames;

        public Summary(bool causticsEnabled, int photonCount, float medianFrameMs, int frames)
        {
            CausticsEnabled = causticsEnabled;
            PhotonCount = photonCount;
            MedianFrameMs = medianFrameMs;
            Frames = frames;
        }
    }
}
