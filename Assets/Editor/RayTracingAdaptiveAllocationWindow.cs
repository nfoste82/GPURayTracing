using System;
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
    private const string HeatmapFolderName = "TestCaptures/Heatmaps";
    private const int AdaptiveAllocationBlockSize = 8;
    private const int HeatmapPreviewScale = 4;
    private string _folder;
    private string _latestPath;
    private string _latestMetadataPath;
    private Texture2D _latestTexture;
    private Texture2D _differenceTexture;
    private string _differenceStatus;
    private string _metadata;
    private double _nextPoll;
    private bool _liveGeneration;
    private bool _showHeatmap;
    private bool _showDifference;
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

        if (!_showHeatmap)
        {
            EditorGUILayout.HelpBox("Heatmap display and generation are disabled. Allocation statistics above continue to update while live monitoring is enabled.", MessageType.Info);
        }
        else if (_latestTexture == null)
        {
            EditorGUILayout.HelpBox("Start an adaptive capture or select a completed heatmap folder to show the heatmap.", MessageType.Info);
        }

        if (_showHeatmap && _latestTexture != null)
        {
            Rect imageRect = GUILayoutUtility.GetRect(_latestTexture.width * HeatmapPreviewScale,
                _latestTexture.height * HeatmapPreviewScale, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
            GUI.DrawTexture(imageRect, _latestTexture, ScaleMode.ScaleToFit, false);
        }

        if (!_showDifference) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Current Render vs Reference", EditorStyles.boldLabel);
        if (_differenceTexture == null)
        {
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(_differenceStatus)
                ? "No reference difference is available for this frame."
                : _differenceStatus, MessageType.Info);
            return;
        }

        Rect differenceRect = GUILayoutUtility.GetAspectRect(_differenceTexture.width / (float)_differenceTexture.height,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUI.DrawTexture(differenceRect, _differenceTexture, ScaleMode.ScaleToFit, false);
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
            allocation = _liveManager.ReadAdaptiveAllocationForCapture();
            string path = Path.Combine(_folder, $"frame_{frame:000000}.png");
            WriteHeatmap(path, allocation);
        }
        if (_showDifference)
        {
            string differencePath = Path.Combine(_folder, $"difference_{frame:000000}.png");
            if (!RayTracingSceneCapture.TryWriteCurrentReferenceDifference(_liveManager, SceneManager.GetActiveScene().path,
                    differencePath, out _differenceStatus) && File.Exists(differencePath))
            {
                File.Delete(differencePath);
            }
        }
        else
        {
            DestroyDifferenceTexture();
        }
        File.WriteAllText(metadataPath,
            $"Adaptive allocation heatmap\nFrame: {frame}\n" +
            $"Active work items: {allocation.activeWorkItems}\n" +
            $"Current scheduled paths: {allocation.assignedPaths}\n" +
            FormatBucketGroupCounts(allocation.bucketGroupCounts) +
            "Colors rank pixels by their current scheduled-path count.\n");
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
            string differencePath = Path.Combine(_folder,
                Path.GetFileName(latest).Replace("frame_", "difference_"));
            LoadDifferenceTexture(differencePath);
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
