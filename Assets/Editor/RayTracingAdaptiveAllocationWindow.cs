using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RayTracingAdaptiveAllocationWindow : EditorWindow
{
    private const string WindowTitle = "Adaptive Allocation Monitor";
    private const string FolderPreference = "RayTracing.AdaptiveAllocationHeatmapFolder";
    private const string LiveGenerationPreference = "RayTracing.AdaptiveAllocationLiveGeneration";
    private const string HeatmapPreference = "RayTracing.AdaptiveAllocationShowHeatmap";
    private const string DifferencePreference = "RayTracing.AdaptiveAllocationShowDifference";
    private const string RollingHeatmapPreference = "RayTracing.AdaptiveAllocationShowRollingHeatmap";
    private const string RollingHeatmapFramesPreference = "RayTracing.AdaptiveAllocationRollingHeatmapFrames";
    private const string HeatmapFolderName = "TestCaptures/Heatmaps";
    private const int AdaptiveAllocationBlockSize = 8;
    private const int HeatmapPreviewScale = 4;
    private const float MaxImageDisplaySize = 512.0f;
    private const float ImagePanelMinimumHeight = 180.0f;
    private const float ImagePanelBottomMargin = 24.0f;
    private string _folder;
    private string _latestPath;
    private string _latestMetadataPath;
    private Texture2D _latestTexture;
    private Texture2D _differenceTexture;
    private string _differenceStatus;
    private string _referenceMetricStatus;
    private double _referencePsnrDb;
    private double _referenceRmse;
    private bool _hasReferenceMetrics;
    private string _metadata;
    private double _nextPoll;
    private bool _liveGeneration;
    private bool _showHeatmap;
    private bool _showDifference;
    private bool _showRollingHeatmap;
    private int _rollingHeatmapFrames;
    private readonly Queue<uint[]> _rollingGroupHistory = new Queue<uint[]>();
    private int _rollingHistoryWidth = -1;
    private int _rollingHistoryHeight = -1;
    private Vector2 _windowScrollPosition;
    private Vector2 _imageScrollPosition;
    private GameManager _liveManager;
    private bool _previousAdaptiveSampling;
    private bool _previousFrameAccumulation;
    private int _lastLiveFrame = -1;
    private bool _liveSettingsApplied;

    [MenuItem("Window/Ray Tracing/Adaptive Allocation Monitor")]
    public static void Open()
    {
        GetWindow<RayTracingAdaptiveAllocationWindow>(WindowTitle);
    }

    private void OnEnable()
    {
        titleContent = new GUIContent(WindowTitle);
        minSize = new Vector2(360.0f, 500.0f);
        _liveGeneration = SessionState.GetBool(LiveGenerationPreference, false);
        _showHeatmap = SessionState.GetBool(HeatmapPreference, true);
        _showDifference = SessionState.GetBool(DifferencePreference, false);
        _showRollingHeatmap = SessionState.GetBool(RollingHeatmapPreference, false);
        _rollingHeatmapFrames = Mathf.Clamp(SessionState.GetInt(RollingHeatmapFramesPreference, 30), 1, 10000);
        _folder = _liveGeneration ? GetLiveFolder() : ResolveDefaultFolder();
        if (string.IsNullOrEmpty(_folder))
        {
            _folder = EditorPrefs.GetString(FolderPreference, string.Empty);
        }
        EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.pauseStateChanged += OnPauseStateChanged;
        EditorApplication.update += PollForLatestFrame;
        PollForLatestFrame();
    }

    private void OnDisable()
    {
        EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.pauseStateChanged -= OnPauseStateChanged;
        EditorApplication.update -= PollForLatestFrame;
        DestroyLatestTexture();
        DestroyDifferenceTexture();
    }

    private void OnActiveSceneChanged(Scene _, Scene __)
    {
        if (_liveGeneration)
        {
            StopLiveGeneration();
            StartLiveGeneration();
            return;
        }
        string sceneFolder = ResolveDefaultFolder();
        if (string.IsNullOrEmpty(sceneFolder)) return;
        SetFolder(sceneFolder);
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && _liveGeneration)
        {
            ApplyLiveGenerationSettings();
        }
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            RestoreLiveGenerationSettings();
        }
    }

    private void OnPauseStateChanged(PauseState state)
    {
        if (_liveManager != null && _liveSettingsApplied)
        {
            _liveManager.SetAdaptiveCaptureDiagnostics(state != PauseState.Paused);
        }
    }

    private void OnGUI()
    {
        _windowScrollPosition = EditorGUILayout.BeginScrollView(_windowScrollPosition);
        EditorGUILayout.LabelField("Adaptive Allocation Monitor", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Live monitoring reports adaptive allocation statistics while playing. Heatmap and reference images are optional diagnostics and can be disabled to avoid their readback and file-generation cost. Monitoring pauses its diagnostics while Play mode is paused.",
            MessageType.Info);

        bool liveGeneration = EditorGUILayout.ToggleLeft("Monitor allocation while playing", _liveGeneration);
        if (liveGeneration != _liveGeneration)
        {
            if (liveGeneration) StartLiveGeneration();
            else StopLiveGeneration();
        }

        DrawAdaptiveSamplingControls();

        using (new EditorGUI.DisabledScope(_liveGeneration))
        using (new EditorGUILayout.HorizontalScope())
        {
            _folder = EditorGUILayout.TextField("Heatmap Folder", _folder);
            if (GUILayout.Button("Browse", GUILayout.Width(70.0f)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select Adaptive Heatmap Folder", _folder, string.Empty);
                if (!string.IsNullOrEmpty(selected)) SetFolder(selected);
            }
        }

        if (GUILayout.Button("Refresh")) PollForLatestFrame(true);
        EditorGUILayout.LabelField("Latest", string.IsNullOrEmpty(_latestMetadataPath) ? "No frame found" : Path.GetFileNameWithoutExtension(_latestMetadataPath));
        if (!string.IsNullOrEmpty(_metadata))
        {
            EditorGUILayout.LabelField(_metadata, EditorStyles.wordWrappedMiniLabel);
        }

        EditorGUILayout.Space();
        bool showHeatmap = EditorGUILayout.ToggleLeft("Show allocation heatmap (generate while playing)", _showHeatmap);
        if (showHeatmap != _showHeatmap)
        {
            _showHeatmap = showHeatmap;
            SessionState.SetBool(HeatmapPreference, _showHeatmap);
            _latestMetadataPath = null;
            DestroyLatestTexture();
            DestroyDifferenceTexture();
            PollForLatestFrame(true);
        }

        bool showDifference = EditorGUILayout.ToggleLeft("Show Current Render vs Reference", _showDifference);
        if (showDifference != _showDifference)
        {
            _showDifference = showDifference;
            SessionState.SetBool(DifferencePreference, _showDifference);
            _latestMetadataPath = null;
            DestroyDifferenceTexture();
            PollForLatestFrame(true);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            bool showRollingHeatmap = EditorGUILayout.ToggleLeft("Show last", _showRollingHeatmap,
                GUILayout.Width(110.0f));
            int rollingHeatmapFrames = EditorGUILayout.IntField(_rollingHeatmapFrames, GUILayout.Width(50.0f));
            EditorGUILayout.LabelField("frames of allocation", GUILayout.Width(120.0f));
            rollingHeatmapFrames = Mathf.Clamp(rollingHeatmapFrames, 1, 10000);
            if (showRollingHeatmap != _showRollingHeatmap)
            {
                _showRollingHeatmap = showRollingHeatmap;
                SessionState.SetBool(RollingHeatmapPreference, _showRollingHeatmap);
                ResetRollingHeatmapHistory();
                Repaint();
            }
            if (rollingHeatmapFrames != _rollingHeatmapFrames)
            {
                _rollingHeatmapFrames = rollingHeatmapFrames;
                SessionState.SetInt(RollingHeatmapFramesPreference, _rollingHeatmapFrames);
                ResetRollingHeatmapHistory();
                Repaint();
            }
        }

        if (!_showHeatmap)
        {
            EditorGUILayout.HelpBox("Heatmap display and generation are disabled. Allocation statistics above continue to update while live monitoring is enabled.", MessageType.Info);
        }
        else if (_latestTexture == null)
        {
            EditorGUILayout.HelpBox("Start an adaptive capture or select a completed heatmap folder to show the heatmap.", MessageType.Info);
        }

        bool hasHeatmap = _showHeatmap && _latestTexture != null;
        bool hasDifference = _showDifference && _differenceTexture != null;
        if (!hasDifference && _showDifference)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current Render vs Reference", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(_differenceStatus)
                ? "No reference difference is available for this frame."
                : _differenceStatus, MessageType.Info);
        }

        if (_showDifference && _hasReferenceMetrics)
        {
            EditorGUILayout.LabelField("Current RGB PSNR", double.IsPositiveInfinity(_referencePsnrDb)
                ? "Infinity dB (identical)"
                : $"{_referencePsnrDb:0.00} dB");
            EditorGUILayout.LabelField("Current RGB RMSE", $"{_referenceRmse:0.00000000}");
        }
        else if (_showDifference && !string.IsNullOrEmpty(_referenceMetricStatus))
        {
            EditorGUILayout.HelpBox(_referenceMetricStatus, MessageType.Info);
        }

        if (!hasHeatmap && !hasDifference)
        {
            EditorGUILayout.EndScrollView();
            return;
        }

        float imageWidth = Mathf.Min(MaxImageDisplaySize, Mathf.Max(1.0f, position.width - 24.0f));
        float imagePanelHeight = Mathf.Max(ImagePanelMinimumHeight, position.height - GUILayoutUtility.GetLastRect().yMax
            - ImagePanelBottomMargin);
        imagePanelHeight = Mathf.Min(imagePanelHeight, position.height - ImagePanelBottomMargin);
        _imageScrollPosition = EditorGUILayout.BeginScrollView(_imageScrollPosition, false, true,
            GUILayout.Height(imagePanelHeight));
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(imageWidth), GUILayout.ExpandHeight(false)))
        {
            if (hasHeatmap)
            {
                DrawImage(_latestTexture, Mathf.Min(imageWidth, _latestTexture.width * HeatmapPreviewScale));
            }

            if (hasDifference)
            {
                if (hasHeatmap) EditorGUILayout.Space();
                EditorGUILayout.LabelField("Current Render vs Reference", EditorStyles.boldLabel);
                DrawImage(_differenceTexture, imageWidth);
            }
        }
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndScrollView();
    }

    private static void DrawImage(Texture2D texture, float width)
    {
        float height = width * texture.height / texture.width;
        Rect imageRect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height),
            GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
        GUI.DrawTexture(imageRect, texture, ScaleMode.ScaleToFit, false);
    }

    private static void DrawAdaptiveSamplingControls()
    {
        GameManager manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
        if (manager == null)
        {
            EditorGUILayout.HelpBox("Add a GameManager to the active scene to edit adaptive sampling settings.",
                MessageType.Info);
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Adaptive Sampling", EditorStyles.boldLabel);
        var serializedManager = new SerializedObject(manager);
        serializedManager.Update();

        SerializedProperty enabled = serializedManager.FindProperty("enableAdaptiveSampling");
        EditorGUILayout.PropertyField(enabled, new GUIContent("Adaptive Sampling (Experimental)"));
        if (enabled.boolValue)
        {
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveBootstrapFrames"),
                new GUIContent("Low-Resolution Bootstrap Frames"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveBootstrapResolutionScale"),
                new GUIContent("Bootstrap Resolution Scale"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveGuidanceHistoryFrames"),
                new GUIContent("Coarse History Passed to Fine",
                    "Approximate fine accumulation samples initialized from the upscaled bootstrap image."));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveSamplingMinSamples"),
                new GUIContent("Minimum Fine Samples"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveBootstrapGroupDivisor"),
                new GUIContent("Fine Bootstrap Group Batches"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptivePriorityMode"),
                new GUIContent("Priority Mode"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveNormalizePriorityByLuminance"),
                new GUIContent("Luminance Priority Normalization Strength"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveReclassificationInterval"),
                new GUIContent("Reclassification Interval"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveHighestBucketSampleRate"),
                new GUIContent("Highest Bucket Sample Rate"));
            EditorGUILayout.PropertyField(serializedManager.FindProperty("adaptiveMaxPathsPerPixel"),
                new GUIContent("Max Paths Per Pixel"));
        }

        serializedManager.ApplyModifiedProperties();
    }

    private void SetFolder(string folder)
    {
        _folder = folder;
        EditorPrefs.SetString(FolderPreference, folder);
        _latestPath = null;
        _latestMetadataPath = null;
        DestroyLatestTexture();
        DestroyDifferenceTexture();
        _hasReferenceMetrics = false;
        _referenceMetricStatus = null;
        ResetRollingHeatmapHistory();
        PollForLatestFrame(true);
    }

    private void StartLiveGeneration()
    {
        _liveGeneration = true;
        SessionState.SetBool(LiveGenerationPreference, true);
        _folder = GetLiveFolder();
        Directory.CreateDirectory(_folder);
        _latestPath = null;
        _latestMetadataPath = null;
        DestroyLatestTexture();
        DestroyDifferenceTexture();
        _hasReferenceMetrics = false;
        _referenceMetricStatus = null;
        ResetRollingHeatmapHistory();

        if (!EditorApplication.isPlaying)
        {
            Repaint();
            return;
        }

        ApplyLiveGenerationSettings();
    }

    private void ApplyLiveGenerationSettings()
    {
        GameManager manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
        if (manager == null)
        {
            ShowNotification(new GUIContent("No GameManager found in the playing scene."));
            return;
        }

        if (_liveSettingsApplied && _liveManager == manager) return;
        _liveGeneration = true;
        _folder = GetLiveFolder();
        Directory.CreateDirectory(_folder);
        _latestPath = null;
        _latestMetadataPath = null;
        DestroyLatestTexture();
        DestroyDifferenceTexture();
        _liveManager = manager;
        _previousAdaptiveSampling = manager.enableAdaptiveSampling;
        _previousFrameAccumulation = manager.enableFrameAccumulation;
        manager.enableAdaptiveSampling = true;
        manager.enableFrameAccumulation = true;
        manager.SetAdaptiveCaptureDiagnostics(true);
        _liveSettingsApplied = true;
        _lastLiveFrame = -1;
        PollForLatestFrame(true);
    }

    private void StopLiveGeneration()
    {
        RestoreLiveGenerationSettings();
        _liveGeneration = false;
        SessionState.SetBool(LiveGenerationPreference, false);
    }

    private void RestoreLiveGenerationSettings()
    {
        if (_liveManager != null && _liveSettingsApplied)
        {
            _liveManager.SetAdaptiveCaptureDiagnostics(false);
            _liveManager.enableAdaptiveSampling = _previousAdaptiveSampling;
            _liveManager.enableFrameAccumulation = _previousFrameAccumulation;
        }
        _liveManager = null;
        _liveSettingsApplied = false;
        ResetRollingHeatmapHistory();
    }

    private void CaptureLiveFrame()
    {
        if (!_liveGeneration || !EditorApplication.isPlaying || EditorApplication.isPaused || _liveManager == null
            || !_liveManager.enableAdaptiveSampling || _liveManager.AccumulatedFrameCount <= 0
            || _liveManager.AccumulatedFrameCount == _lastLiveFrame)
        {
            return;
        }

        GameManager.AdaptiveAllocationFrameData allocation =
            _liveManager.ReadAdaptiveAllocationStatsForCapture();
        int frame = _liveManager.AccumulatedFrameCount;
        string metadataPath = Path.Combine(_folder, $"frame_{frame:000000}.txt");
        if (_showHeatmap)
        {
            // The compact work list describes this scheduler epoch only. The monitor's heatmap is
            // a convergence view, so visualize the persistent per-pixel retired-path totals.
            allocation = _liveManager.ReadAdaptiveCumulativeAllocationForCapture();
            if (_showRollingHeatmap)
            {
                allocation = BuildRollingHeatmapAllocation(allocation);
            }
            string path = Path.Combine(_folder, $"frame_{frame:000000}.png");
            WriteHeatmap(path, allocation);
        }
        if (_showDifference)
        {
            Texture2D difference;
            _hasReferenceMetrics = RayTracingSceneCapture.TryCompareCurrentRenderToReference(_liveManager,
                SceneManager.GetActiveScene().path, out difference, out _referencePsnrDb, out _referenceRmse,
                out _referenceMetricStatus);
            if (_hasReferenceMetrics)
            {
                DestroyDifferenceTexture();
                _differenceTexture = difference;
                _differenceTexture.filterMode = FilterMode.Point;
                _differenceStatus = string.Empty;
            }
            else
            {
                if (difference != null) UnityEngine.Object.DestroyImmediate(difference);
                DestroyDifferenceTexture();
                _differenceStatus = _referenceMetricStatus;
            }
        }
        else
        {
            DestroyDifferenceTexture();
            _hasReferenceMetrics = false;
            _referenceMetricStatus = null;
        }
        File.WriteAllText(metadataPath,
            $"Adaptive allocation heatmap\nFrame: {frame}\n" +
            $"Active work items: {allocation.activeWorkItems}\n" +
            (_showRollingHeatmap
                ? $"Rolling retired paths: {allocation.assignedPaths}\n"
                : $"Cumulative retired paths: {allocation.assignedPaths}\n") +
            FormatBucketGroupCounts(allocation.bucketGroupCounts) +
            (_showRollingHeatmap
                ? $"Colors rank pixels by retired paths from the last {_rollingHeatmapFrames} frames.\n"
                : "Colors rank pixels by their cumulative retired-path count.\n"));
        _lastLiveFrame = frame;
        PollForLatestFrame(true);
    }

    private static string GetLiveFolder()
    {
        string sceneName = Path.GetFileNameWithoutExtension(SceneManager.GetActiveScene().path);
        if (string.IsNullOrEmpty(sceneName)) sceneName = "UnsavedScene";
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", HeatmapFolderName, sceneName));
    }

    private static void WriteHeatmap(string path, GameManager.AdaptiveAllocationFrameData allocation)
    {
        var selected = new System.Collections.Generic.List<uint>();
        foreach (uint paths in allocation.pixels) if (paths > 0u) selected.Add(paths);
        selected.Sort();
        var quantiles = new System.Collections.Generic.Dictionary<uint, float>();
        for (int start = 0; start < selected.Count;)
        {
            int end = start + 1;
            while (end < selected.Count && selected[end] == selected[start]) end++;
            quantiles[selected[start]] = (start + (end - start) * 0.5f) / Math.Max(1, selected.Count);
            start = end;
        }
        int width = Mathf.CeilToInt(allocation.width / (float)AdaptiveAllocationBlockSize);
        int height = Mathf.CeilToInt(allocation.height / (float)AdaptiveAllocationBlockSize);
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
        texture.filterMode = FilterMode.Point;
        var colors = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint paths = allocation.pixels[x * AdaptiveAllocationBlockSize
                    + y * AdaptiveAllocationBlockSize * allocation.width];
                colors[x + y * width] = paths == 0u ? Color.black : HeatmapColor(paths, quantiles[paths]);
            }
        }
        texture.SetPixels(colors);
        texture.Apply(false, false);
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);
    }

    private GameManager.AdaptiveAllocationFrameData BuildRollingHeatmapAllocation(
        GameManager.AdaptiveAllocationFrameData cumulative)
    {
        EnsureRollingHistoryCapacity(cumulative.width, cumulative.height);
        uint[] current = DownsampleGroupCounts(cumulative);
        _rollingGroupHistory.Enqueue(current);
        while (_rollingGroupHistory.Count > _rollingHeatmapFrames + 1)
        {
            _rollingGroupHistory.Dequeue();
        }

        uint[] oldest = _rollingGroupHistory.Count > _rollingHeatmapFrames
            ? _rollingGroupHistory.Peek()
            : null;
        uint[] rolling = new uint[current.Length];
        ulong assignedPaths = 0;
        uint activeWorkItems = 0;
        for (int i = 0; i < rolling.Length; i++)
        {
            uint previous = oldest == null ? 0u : oldest[i];
            rolling[i] = current[i] >= previous ? current[i] - previous : 0u;
            assignedPaths += rolling[i];
            if (rolling[i] > 0u) activeWorkItems++;
        }

        return new GameManager.AdaptiveAllocationFrameData
        {
            pixels = ExpandGroupCounts(rolling, cumulative.width, cumulative.height),
            bucketGroupCounts = cumulative.bucketGroupCounts,
            assignedPaths = assignedPaths > uint.MaxValue ? uint.MaxValue : (uint)assignedPaths,
            activeWorkItems = activeWorkItems,
            width = cumulative.width,
            height = cumulative.height
        };
    }

    private void EnsureRollingHistoryCapacity(int width, int height)
    {
        if (_rollingHistoryWidth == width && _rollingHistoryHeight == height) return;
        ResetRollingHeatmapHistory();
        _rollingHistoryWidth = width;
        _rollingHistoryHeight = height;
    }

    private void ResetRollingHeatmapHistory()
    {
        _rollingGroupHistory.Clear();
        _rollingHistoryWidth = -1;
        _rollingHistoryHeight = -1;
    }

    private static uint[] DownsampleGroupCounts(GameManager.AdaptiveAllocationFrameData allocation)
    {
        int groupWidth = Mathf.CeilToInt(allocation.width / (float)AdaptiveAllocationBlockSize);
        int groupHeight = Mathf.CeilToInt(allocation.height / (float)AdaptiveAllocationBlockSize);
        var groups = new uint[groupWidth * groupHeight];
        for (int y = 0; y < groupHeight; y++)
        {
            for (int x = 0; x < groupWidth; x++)
            {
                int startX = x * AdaptiveAllocationBlockSize;
                int startY = y * AdaptiveAllocationBlockSize;
                int endX = Mathf.Min(startX + AdaptiveAllocationBlockSize, allocation.width);
                int endY = Mathf.Min(startY + AdaptiveAllocationBlockSize, allocation.height);
                ulong total = 0;
                for (int pixelY = startY; pixelY < endY; pixelY++)
                {
                    for (int pixelX = startX; pixelX < endX; pixelX++)
                    {
                        total += allocation.pixels[pixelX + pixelY * allocation.width];
                    }
                }
                groups[x + y * groupWidth] = total > uint.MaxValue ? uint.MaxValue : (uint)total;
            }
        }
        return groups;
    }

    private static uint[] ExpandGroupCounts(uint[] groups, int width, int height)
    {
        int groupWidth = Mathf.CeilToInt(width / (float)AdaptiveAllocationBlockSize);
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[x + y * width] = groups[(x / AdaptiveAllocationBlockSize)
                    + (y / AdaptiveAllocationBlockSize) * groupWidth];
            }
        }
        return pixels;
    }

    private static Color HeatmapColor(uint paths, float quantile)
    {
        if (paths == 0u) return Color.black;
        if (quantile > 0.80f) return Color.red;
        if (quantile > 0.60f) return Color.yellow;
        if (quantile > 0.40f) return Color.cyan;
        if (quantile > 0.20f) return new Color(0.0f, 0.45f, 1.0f);
        if (quantile > 0.10f) return Color.blue;
        if (quantile > 0.05f) return new Color(0.0f, 0.0f, 0.55f);
        return new Color(0.0f, 0.0f, 0.25f);
    }

    internal static string FormatBucketGroupCounts(uint[] bucketGroupCounts)
    {
        if (bucketGroupCounts == null || bucketGroupCounts.Length == 0) return string.Empty;

        var counts = new string[bucketGroupCounts.Length];
        for (int bucket = 0; bucket < bucketGroupCounts.Length; bucket++)
        {
            counts[bucket] = $"{bucket}: {bucketGroupCounts[bucket]}";
        }
        return "Pixel groups per bucket (8x8): " + string.Join(", ", counts) + "\n";
    }

    private static string ResolveDefaultFolder()
    {
        string sceneName = Path.GetFileNameWithoutExtension(EditorSceneManager.GetActiveScene().path);
        if (string.IsNullOrEmpty(sceneName)) return string.Empty;

        string captureRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestCaptures"));
        if (!Directory.Exists(captureRoot)) return string.Empty;

        string heatmapFolder = Path.Combine(captureRoot, "Heatmaps", sceneName);
        if (Directory.Exists(heatmapFolder) && Directory.GetFiles(heatmapFolder, "frame_*.png").Length > 0)
        {
            return heatmapFolder;
        }

        string newestFolder = null;
        DateTime newestWrite = DateTime.MinValue;
        foreach (string folder in Directory.GetDirectories(captureRoot, "adaptive_allocation_heatmaps",
                     SearchOption.AllDirectories))
        {
            DirectoryInfo parent = Directory.GetParent(folder);
            if (parent == null || !string.Equals(parent.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                continue;

            DateTime folderWrite = DateTime.MinValue;
            foreach (string imagePath in Directory.GetFiles(folder, "frame_*.png"))
            {
                DateTime imageWrite = File.GetLastWriteTimeUtc(imagePath);
                if (imageWrite > folderWrite) folderWrite = imageWrite;
            }
            if (folderWrite > newestWrite)
            {
                newestFolder = folder;
                newestWrite = folderWrite;
            }
        }
        return newestFolder ?? string.Empty;
    }

    private void PollForLatestFrame()
    {
        if (EditorApplication.timeSinceStartup < _nextPoll) return;
        _nextPoll = EditorApplication.timeSinceStartup + 0.25;
        CaptureLiveFrame();
        PollForLatestFrame(false);
    }

    private void PollForLatestFrame(bool force)
    {
        if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder))
        {
            string sceneFolder = ResolveDefaultFolder();
            if (!string.IsNullOrEmpty(sceneFolder))
            {
                SetFolder(sceneFolder);
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder))
        {
            if (force) Repaint();
            return;
        }

        string latestMetadata = null;
        DateTime latestWrite = DateTime.MinValue;
        foreach (string path in Directory.GetFiles(_folder, "frame_*.txt"))
        {
            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime > latestWrite)
            {
                latestMetadata = path;
                latestWrite = writeTime;
            }
        }
        if (latestMetadata == null || string.Equals(latestMetadata, _latestMetadataPath, StringComparison.Ordinal))
        {
            if (force) Repaint();
            return;
        }

        _latestMetadataPath = latestMetadata;
        _metadata = File.ReadAllText(latestMetadata);
        string latest = Path.ChangeExtension(latestMetadata, ".png");
        if (_showHeatmap && File.Exists(latest))
        {
            try
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!texture.LoadImage(File.ReadAllBytes(latest), false))
                {
                    DestroyImmediate(texture);
                    return;
                }
                texture.filterMode = FilterMode.Point;
                DestroyLatestTexture();
                _latestTexture = texture;
                _latestPath = latest;
            }
            catch (IOException)
            {
                return;
            }
        }
        else
        {
            DestroyLatestTexture();
            _latestPath = null;
        }
        if (_showDifference)
        {
            // Live comparison creates the display texture directly and does not write a PNG.
            // Completed capture folders still load their persisted difference image here.
            if (!_liveGeneration || _differenceTexture == null)
            {
                string differencePath = Path.Combine(_folder,
                    Path.GetFileName(latest).Replace("frame_", "difference_"));
                LoadDifferenceTexture(differencePath);
            }
        }
        Repaint();
    }

    private void DestroyLatestTexture()
    {
        if (_latestTexture != null)
        {
            DestroyImmediate(_latestTexture);
            _latestTexture = null;
        }
    }

    private void LoadDifferenceTexture(string path)
    {
        DestroyDifferenceTexture();
        if (!File.Exists(path))
        {
            if (string.IsNullOrEmpty(_differenceStatus))
            {
                _differenceStatus = "No same-resolution reference image is available for this scene.";
            }
            return;
        }

        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!texture.LoadImage(File.ReadAllBytes(path), false))
            {
                DestroyImmediate(texture);
                _differenceStatus = "Could not read the reference difference image.";
                return;
            }
            _differenceTexture = texture;
            _differenceStatus = string.Empty;
        }
        catch (IOException)
        {
            _differenceStatus = "Reference difference image is still being written.";
        }
    }

    private void DestroyDifferenceTexture()
    {
        if (_differenceTexture != null)
        {
            DestroyImmediate(_differenceTexture);
            _differenceTexture = null;
        }
    }
}
