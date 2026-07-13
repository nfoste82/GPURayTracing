using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class CausticsBenchmarkRunner : MonoBehaviour
{
    public GameManager gameManager;
    public KeyCode runKey = KeyCode.F4;
    public int warmupFrames = 30;
    public int measurementFrames = 120;
    public int trialsPerConfiguration = 3;
    public float impracticalOverheadPercent = 25.0f;
    public int targetFrameRate = 60;
    public int[] photonCounts = { 64, 256, 1024, 2048, 4096, 16384 };

    private readonly List<Result> _results = new List<Result>();
    private readonly List<Summary> _summaries = new List<Summary>();
    private Coroutine _benchmarkCoroutine;
    private string _status = "Press F4 to benchmark caustics";
    private string _lastCsvPath;
    private GUIStyle _style;

    private void Awake()
    {
        if (gameManager == null)
        {
            gameManager = FindObjectOfType<GameManager>();
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(runKey) && _benchmarkCoroutine == null && gameManager != null)
        {
            _benchmarkCoroutine = StartCoroutine(RunBenchmark());
        }
    }

    private IEnumerator RunBenchmark()
    {
        bool originalCausticsEnabled = gameManager.enableCaustics;
        bool originalDynamicQuality = gameManager.enableDynamicQuality;
        int originalPhotonCount = gameManager.causticPhotonCount;
        int originalTargetFrameRate = Application.targetFrameRate;
        int originalVSyncCount = QualitySettings.vSyncCount;

        _results.Clear();
        _summaries.Clear();
        _lastCsvPath = null;
        gameManager.enableDynamicQuality = false;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        try
        {
            yield return MeasureConfiguration(false, 0);

            if (photonCounts != null)
            {
                for (int i = 0; i < photonCounts.Length; i++)
                {
                    yield return MeasureConfiguration(true, Mathf.Max(64, photonCounts[i]));
                }
            }

            _lastCsvPath = WriteCsv();
            _status = $"Complete: {_results.Count} configurations";
            Debug.Log($"Caustics benchmark complete. Results written to {_lastCsvPath}");
        }
        finally
        {
            gameManager.enableCaustics = originalCausticsEnabled;
            gameManager.causticPhotonCount = originalPhotonCount;
            gameManager.enableDynamicQuality = originalDynamicQuality;
            Application.targetFrameRate = originalTargetFrameRate;
            QualitySettings.vSyncCount = originalVSyncCount;
            _benchmarkCoroutine = null;
        }
    }

    private IEnumerator MeasureConfiguration(bool causticsEnabled, int photonCount)
    {
        gameManager.enableCaustics = causticsEnabled;
        if (causticsEnabled)
        {
            gameManager.causticPhotonCount = photonCount;
        }

        string label = causticsEnabled ? $"{photonCount} photons" : "disabled";
        int warmupCount = Mathf.Max(1, warmupFrames);
        for (int i = 0; i < warmupCount; i++)
        {
            _status = $"Warming {label}: {i + 1}/{warmupCount}";
            yield return null;
        }

        int trialCount = Mathf.Max(1, trialsPerConfiguration);
        int sampleCount = Mathf.Max(1, measurementFrames);
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
        _summaries.Add(new Summary(causticsEnabled, photonCount, Median(trialAverages)));
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
        string directory = Path.Combine(Application.persistentDataPath, "Benchmarks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"caustics-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var builder = new StringBuilder();
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
        builder.AppendLine("caustics_enabled,photon_count,median_average_frame_ms,overhead_percent,over_target_budget,impractical");
        float baselineMs = _summaries.Count > 0 ? _summaries[0].MedianFrameMs : 0.0f;
        float targetFrameMs = 1000.0f / Mathf.Max(1, targetFrameRate);
        for (int i = 0; i < _summaries.Count; i++)
        {
            Summary summary = _summaries[i];
            float overheadPercent = baselineMs > 0.0f ? (summary.MedianFrameMs / baselineMs - 1.0f) * 100.0f : 0.0f;
            bool overTarget = summary.MedianFrameMs > targetFrameMs;
            bool impractical = summary.CausticsEnabled && (overheadPercent > impracticalOverheadPercent || overTarget);
            builder.Append(summary.CausticsEnabled ? "true" : "false").Append(',')
                .Append(summary.PhotonCount).Append(',')
                .Append(summary.MedianFrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(overheadPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(overTarget ? "true" : "false").Append(',')
                .Append(impractical ? "true" : "false").AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private void OnGUI()
    {
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
        builder.Append("Caustics benchmark (F4)\n").AppendLine(_status);
        float baselineMs = _summaries.Count > 0 ? _summaries[0].MedianFrameMs : 0.0f;
        float targetFrameMs = 1000.0f / Mathf.Max(1, targetFrameRate);
        int practicalLimit = -1;
        for (int i = 0; i < _summaries.Count; i++)
        {
            Summary summary = _summaries[i];
            float overheadPercent = baselineMs > 0.0f ? (summary.MedianFrameMs / baselineMs - 1.0f) * 100.0f : 0.0f;
            bool impractical = summary.CausticsEnabled
                && (overheadPercent > impracticalOverheadPercent || summary.MedianFrameMs > targetFrameMs);
            builder.Append(summary.CausticsEnabled ? summary.PhotonCount.ToString() : "Disabled")
                .Append(": ").Append(summary.MedianFrameMs.ToString("0.00")).Append(" ms median")
                .Append(summary.CausticsEnabled ? $" ({overheadPercent:+0.0;-0.0;0.0}%)" : string.Empty)
                .AppendLine(impractical ? "  LIMIT" : string.Empty);
            if (impractical && practicalLimit < 0)
            {
                practicalLimit = summary.PhotonCount;
            }
        }

        if (_summaries.Count > 0)
        {
            builder.Append("First impractical count: ").AppendLine(practicalLimit < 0 ? "not reached" : practicalLimit.ToString());
        }

        if (!string.IsNullOrEmpty(_lastCsvPath))
        {
            builder.Append("CSV: ").Append(_lastCsvPath);
        }

        GUI.Box(new Rect(12, 290, 560, 190), builder.ToString(), _style);
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

        public Summary(bool causticsEnabled, int photonCount, float medianFrameMs)
        {
            CausticsEnabled = causticsEnabled;
            PhotonCount = photonCount;
            MedianFrameMs = medianFrameMs;
        }
    }
}
