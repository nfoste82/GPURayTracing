using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class RayTracingSceneCapture
{
    private const int DefaultSamplesPerScene = 300;
    private const int DefaultCaptureWidth = 512;
    private const int DefaultCaptureHeight = 512;
    private const string DefaultOutputFolder = "RayTracingSceneCaptures";
    private const string GalleryThumbnailOutputFolder = "Assets/Editor/RayTracingSceneGalleryThumbnails";
    private const string GalleryThumbnailLabel = "Current";
    private const string SessionPrefix = "GPURayTracing.SceneCapture.";
    private const int DurationCaptureGpuSyncInterval = 128;
    private static RenderTexture _captureTarget;
    private static RenderTexture _captureSource;

    static RayTracingSceneCapture()
    {
        EditorApplication.update += Update;
    }

    // Invoke with -executeMethod RayTracingSceneCapture.CaptureFromCommandLine.
    public static void CaptureFromCommandLine()
    {
        var sceneArgument = GetCommandLineArgument("-rayTracingScenes");
        var outputArgument = GetCommandLineArgument("-rayTracingOutput");
        var generateScenes = HasCommandLineArgument("-rayTracingGenerateScenes");
        var compareAdaptiveSampling = HasCommandLineArgument("-rayTracingCompareAdaptiveSampling");
        var requestedPreset = GetAdaptiveSamplingPreset();
        if (!TryGetCaptureSettings(out int samplesPerScene, out int captureWidth, out int captureHeight,
                out double durationSeconds))
        {
            ExitBatchMode(1);
            return;
        }
        var label = GetCommandLineArgument("-rayTracingCaptureLabel") ?? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var debugRenderMode = GetDebugRenderMode();
        if (string.IsNullOrWhiteSpace(sceneArgument))
        {
            Debug.LogError("Scene capture requires -rayTracingScenes with semicolon-separated scene asset paths.");
            ExitBatchMode(1);
            return;
        }

        var scenes = sceneArgument.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (generateScenes)
        {
            RayTracingSceneGenerator.GenerateScenes(scenes, true);
        }
        
        var outputRoot = string.IsNullOrWhiteSpace(outputArgument) ? GetDefaultOutputRoot() : outputArgument;
        if (Application.isBatchMode)
        {
            CaptureInBatchMode(label, scenes, outputRoot, samplesPerScene, captureWidth, captureHeight,
                durationSeconds, debugRenderMode, compareAdaptiveSampling, requestedPreset);
            return;
        }
        StartCapture(label, scenes, outputRoot, samplesPerScene, captureWidth, captureHeight);
    }

    [MenuItem("Tools/Ray Tracing/Generate Scene Gallery Thumbnails")]
    public static void CaptureGalleryThumbnails()
    {
        StartCapture(
            GalleryThumbnailLabel,
            RayTracingSceneGalleryWindow.GetThumbnailScenePaths(),
            GalleryThumbnailOutputFolder,
            DefaultSamplesPerScene,
            DefaultCaptureWidth,
            DefaultCaptureHeight);
    }

    public static void CaptureGalleryThumbnail(string scenePath)
    {
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            Debug.LogError("Scene gallery thumbnail capture requires a scene path.");
            return;
        }

        StartCapture(
            GalleryThumbnailLabel,
            new[] { scenePath },
            GalleryThumbnailOutputFolder,
            DefaultSamplesPerScene,
            DefaultCaptureWidth,
            DefaultCaptureHeight);
    }

    private static void CaptureInBatchMode(
        string label,
        IReadOnlyList<string> scenePaths,
        string outputRoot,
        int samplesPerScene,
        int captureWidth,
        int captureHeight,
        double durationSeconds,
        DebugRenderMode debugRenderMode,
        bool compareAdaptiveSampling,
        AdaptiveSamplingPreset requestedPreset)
    {
        try
        {
            foreach (var scenePath in scenePaths)
            {
                var trimmedPath = scenePath.Trim();
                EditorSceneManager.OpenScene(trimmedPath);
                var manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                if (manager == null || manager.renderTextureCamera == null || manager.shader == null)
                {
                    throw new InvalidOperationException($"Scene capture requires a configured GameManager, render camera, and compute shader: {trimmedPath}");
                }

                foreach (var setup in UnityEngine.Object.FindObjectsByType<MaterialBallRoomRuntimeSetup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    setup.PrepareForRendering();
                }

                var sceneName = Path.GetFileNameWithoutExtension(trimmedPath);
                if (compareAdaptiveSampling)
                {
                    CaptureAdaptiveComparison(manager, sceneName, outputRoot, label, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode);
                }
                else
                {
                    CaptureVariant(manager, sceneName, Path.Combine(outputRoot, SanitizePathSegment(label)), sceneName, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, requestedPreset, false, false);
                }
            }

            Debug.Log($"Ray tracing scene capture complete: '{Path.Combine(outputRoot, SanitizePathSegment(label))}'.");
            ExitBatchMode(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ReleaseCaptureTarget(null);
            ExitBatchMode(1);
        }
    }

    private static void CaptureAdaptiveComparison(
        GameManager manager,
        string sceneName,
        string outputRoot,
        string label,
        int samplesPerScene,
        int captureWidth,
        int captureHeight,
        double durationSeconds,
        DebugRenderMode debugRenderMode)
    {
        var comparisonRoot = Path.Combine(outputRoot, SanitizePathSegment(label), sceneName);
        CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_off", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, AdaptiveSamplingPreset.Custom, false, true);
        CaptureVariant(manager, sceneName, comparisonRoot, "quality", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, AdaptiveSamplingPreset.Quality, true, true);
        CaptureVariant(manager, sceneName, comparisonRoot, "performance", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, AdaptiveSamplingPreset.Performance, true, true);
        CaptureVariant(manager, sceneName, comparisonRoot, "ultra_performance", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, AdaptiveSamplingPreset.UltraPerformance, true, true);
    }

    private static void CaptureVariant(
        GameManager manager,
        string sceneName,
        string outputRoot,
        string label,
        int samplesPerScene,
        int captureWidth,
        int captureHeight,
        double durationSeconds,
        DebugRenderMode debugRenderMode,
        AdaptiveSamplingPreset preset,
        bool adaptiveSampling,
        bool writeTimingReport)
    {
        manager.randomNoise = false;
        manager.enableFrameAccumulation = true;
        if (preset != AdaptiveSamplingPreset.Custom)
        {
            manager.ApplyAdaptiveSamplingPreset(preset);
        }
        else
        {
            manager.enableAdaptiveSampling = false;
        }
        manager.TemporalDenoising.enabled = false;
        manager.debugRenderMode = debugRenderMode;
        manager.numberOfPasses = 1;
        manager._singleFrame = true;
        
        ResetAccumulation(manager);
        InitializeBatchRenderer(manager, captureWidth, captureHeight);
        ResetAccumulation(manager);

        _captureTarget = new RenderTexture(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32)
        {
            name = "Ray Tracing Scene Capture",
            enableRandomWrite = true
        };
        _captureTarget.Create();
        manager.renderTextureCamera.targetTexture = _captureTarget;
        _captureSource = new RenderTexture(captureWidth, captureHeight, 0, RenderTextureFormat.ARGB32);
        _captureSource.Create();

        // Warm until the render actually completes. The first call can intentionally defer a cold
        // shader variant, and the next call may include its synchronous multi-minute compilation.
        for (int warmup = 0; warmup < 4 && manager.AccumulatedFrameCount == 0; warmup++)
        {
            manager.RenderImage(_captureSource, _captureTarget);
        }
        ResetAccumulation(manager);

        var measuredFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        var nextYield = stopwatch.Elapsed.TotalSeconds + 0.05;
        while ((durationSeconds > 0.0 && stopwatch.Elapsed.TotalSeconds < durationSeconds)
               || (durationSeconds <= 0.0 && measuredFrames < samplesPerScene))
        {
            manager.RenderImage(_captureSource, _captureTarget);
            measuredFrames++;
            if (durationSeconds > 0.0 && measuredFrames % DurationCaptureGpuSyncInterval == 0)
            {
                SynchronizeDurationCaptureGpu();
            }
            if (Application.isBatchMode && stopwatch.Elapsed.TotalSeconds >= nextYield)
            {
                // Return briefly to Unity's editor loop in long captures so Metal can submit
                // completed command buffers instead of retaining every synchronous dispatch.
                System.Threading.Thread.Sleep(1);
                nextYield += 0.05;
            }
        }
        stopwatch.Stop();
        SynchronizeDurationCaptureGpu();

        Directory.CreateDirectory(outputRoot);
        string outputPath = Path.Combine(outputRoot, label + ".png");
        manager.ExportCurrentRenderPng(outputPath);
        if (writeTimingReport)
        {
            WriteTimingReport(outputRoot, label, sceneName, preset, adaptiveSampling, measuredFrames,
                durationSeconds,
                captureWidth, captureHeight, stopwatch.Elapsed.TotalMilliseconds);
        }
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}' ({stopwatch.Elapsed.TotalMilliseconds:0.00} ms).");
        ReleaseCaptureTarget(manager.renderTextureCamera);
    }

    private static void SynchronizeDurationCaptureGpu()
    {
        // A synchronous RenderImage call can still leave Metal command buffers queued. A
        // readback request establishes a dependency on the presented capture target, and waiting
        // for it forces the backend to retire those commands before more duration frames arrive.
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(_captureTarget);
        request.WaitForCompletion();
        if (request.hasError)
        {
            throw new InvalidOperationException("GPU readback failed while synchronizing a duration capture.");
        }
    }

    private static void ResetAccumulation(GameManager manager)
    {
        MethodInfo reset = typeof(GameManager).GetMethod("ResetFrameAccumulation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        reset?.Invoke(manager, null);
    }

    private static void WriteTimingReport(
        string outputRoot,
        string label,
        string sceneName,
        AdaptiveSamplingPreset preset,
        bool adaptiveSampling,
        int measuredFrames,
        double requestedDurationSeconds,
        int width,
        int height,
        double totalMilliseconds)
    {
        Directory.CreateDirectory(outputRoot);
        string path = Path.Combine(outputRoot, label + ".txt");
        double averageMilliseconds = totalMilliseconds / Math.Max(1, measuredFrames);
        File.WriteAllText(path,
            $"Scene: {sceneName}\n" +
            $"Adaptive sampling: {adaptiveSampling}\n" +
            $"Adaptive sampling preset: {preset}\n" +
            $"Measured frames: {measuredFrames}\n" +
            $"Requested duration: {(requestedDurationSeconds > 0.0 ? requestedDurationSeconds.ToString("0.###") + " seconds" : "fixed sample count")}\n" +
            $"Resolution: {width}x{height}\n" +
            $"Total measured render time: {totalMilliseconds:0.000} ms\n" +
            $"Average render time per frame: {averageMilliseconds:0.000} ms\n" +
            $"Average measured FPS: {(averageMilliseconds > 0.0 ? 1000.0 / averageMilliseconds : 0.0):0.000}\n");
    }

    private static void InitializeBatchRenderer(GameManager manager, int width, int height)
    {
        manager.TemporalDenoising.Initialize(manager);
        foreach (PathTracingObject pathTracingObject in UnityEngine.Object.FindObjectsByType<PathTracingObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            manager.RegisterObject(pathTracingObject);
        }
        foreach (RayDirectionalLight directionalLight in UnityEngine.Object.FindObjectsByType<RayDirectionalLight>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            manager.RegisterDirectionalLight(directionalLight);
        }

        MethodInfo ensureOutputTextureSize = typeof(GameManager).GetMethod("EnsureOutputTextureSize", BindingFlags.Instance | BindingFlags.NonPublic);
        if (ensureOutputTextureSize == null)
        {
            throw new MissingMethodException(typeof(GameManager).FullName, "EnsureOutputTextureSize");
        }
        ensureOutputTextureSize.Invoke(manager, new object[] { width, height });
        manager.RebuildBuffers(false);
    }

    private static DebugRenderMode GetDebugRenderMode()
    {
        string argument = GetCommandLineArgument("-rayTracingDebugRenderMode");
        if (argument == null)
        {
            return DebugRenderMode.FinalColor;
        }

        if (Enum.TryParse(argument, true, out DebugRenderMode mode)
            && Enum.IsDefined(typeof(DebugRenderMode), mode))
        {
            return mode;
        }

        throw new ArgumentException($"Unknown ray tracing debug render mode '{argument}'.");
    }

    private static AdaptiveSamplingPreset GetAdaptiveSamplingPreset()
    {
        string argument = GetCommandLineArgument("-rayTracingAdaptiveSamplingPreset");
        if (string.IsNullOrWhiteSpace(argument))
        {
            return AdaptiveSamplingPreset.Custom;
        }
        if (Enum.TryParse(argument, true, out AdaptiveSamplingPreset preset)
            && preset != AdaptiveSamplingPreset.Custom)
        {
            return preset;
        }
        throw new ArgumentException($"Unknown adaptive sampling preset '{argument}'. Use quality, performance, or ultraPerformance.");
    }

    private static void StartCapture(string label, IReadOnlyList<string> scenePaths, string outputRoot, int samplesPerScene, int captureWidth, int captureHeight)
    {
        if (IsActive())
        {
            Debug.LogError("A ray tracing scene capture is already active.");
            return;
        }
        if (scenePaths == null || scenePaths.Count == 0)
        {
            Debug.LogError("Ray tracing scene capture requires at least one scene.");
            ExitBatchMode(1);
            return;
        }
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        var paths = new List<string>();
        foreach (string scenePath in scenePaths)
        {
            string trimmedPath = scenePath.Trim();
            if (!File.Exists(trimmedPath))
            {
                Debug.LogError($"Ray tracing scene capture could not find '{trimmedPath}'.");
                ExitBatchMode(1);
                return;
            }
            paths.Add(trimmedPath);
        }

        SessionState.SetBool(SessionPrefix + "Active", true);
        SessionState.SetString(SessionPrefix + "Paths", string.Join("\n", paths));
        SessionState.SetString(SessionPrefix + "Output", Path.GetFullPath(outputRoot));
        SessionState.SetString(SessionPrefix + "Label", SanitizePathSegment(label));
        SessionState.SetInt(SessionPrefix + "Samples", samplesPerScene);
        SessionState.SetInt(SessionPrefix + "Width", captureWidth);
        SessionState.SetInt(SessionPrefix + "Height", captureHeight);
        SessionState.SetInt(SessionPrefix + "Index", 0);
        SessionState.SetBool(SessionPrefix + "WaitingForPlay", false);
        SessionState.SetBool(SessionPrefix + "SceneFinished", false);
        SessionState.SetBool(SessionPrefix + "Configured", false);
        SessionState.SetInt(SessionPrefix + "WarmupFrames", 0);

        Debug.Log($"Ray tracing scene capture started: {paths.Count} scene(s), {samplesPerScene} samples each at {captureWidth}x{captureHeight}.");
    }

    private static void Update()
    {
        if (!IsActive())
        {
            return;
        }

        if (!EditorApplication.isPlaying)
        {
            UpdateEditorState();
            return;
        }

        UpdatePlayModeCapture();
    }

    private static void UpdateEditorState()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (SessionState.GetBool(SessionPrefix + "SceneFinished", false))
        {
            SessionState.SetInt(SessionPrefix + "Index", SessionState.GetInt(SessionPrefix + "Index", 0) + 1);
            SessionState.SetBool(SessionPrefix + "SceneFinished", false);
            SessionState.SetBool(SessionPrefix + "WaitingForPlay", false);
        }

        string[] paths = GetScenePaths();
        int index = SessionState.GetInt(SessionPrefix + "Index", 0);
        if (index >= paths.Length)
        {
            CompleteCapture();
            return;
        }
        if (SessionState.GetBool(SessionPrefix + "WaitingForPlay", false))
        {
            return;
        }

        EditorSceneManager.OpenScene(paths[index]);
        SessionState.SetBool(SessionPrefix + "WaitingForPlay", true);
        SessionState.SetBool(SessionPrefix + "Configured", false);
        EditorApplication.delayCall += EnterPlayMode;
    }

    private static void EnterPlayMode()
    {
        if (IsActive() && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = true;
        }
    }

    private static void UpdatePlayModeCapture()
    {
        GameManager manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
        if (manager == null || manager.renderTextureCamera == null || manager.shader == null)
        {
            FailCurrentScene("requires a configured GameManager, render camera, and compute shader");
            return;
        }

        if (!SessionState.GetBool(SessionPrefix + "Configured", false))
        {
            ConfigureCapture(manager);
            SessionState.SetBool(SessionPrefix + "Configured", true);
            return;
        }

        int warmupFrames = SessionState.GetInt(SessionPrefix + "WarmupFrames", 0);
        if (warmupFrames > 0)
        {
            SessionState.SetInt(SessionPrefix + "WarmupFrames", warmupFrames - 1);
            return;
        }

        // Batch-mode Unity does not supply a Game View callback. The scene has still completed
        // normal Play-mode initialization; now drive the same render entry point explicitly.
        manager.RenderImage(_captureSource, _captureTarget);

        if (manager.AccumulatedFrameCount < SessionState.GetInt(SessionPrefix + "Samples", DefaultSamplesPerScene))
        {
            return;
        }

        string sceneName = Path.GetFileNameWithoutExtension(GetScenePaths()[SessionState.GetInt(SessionPrefix + "Index", 0)]);
        string outputPath = Path.Combine(
            SessionState.GetString(SessionPrefix + "Output", GetDefaultOutputRoot()),
            SessionState.GetString(SessionPrefix + "Label", "capture"),
            sceneName + ".png");
        manager.ExportCurrentRenderPng(outputPath);
        AssetDatabase.Refresh();
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}'.");
        ReleaseCaptureTarget(manager.renderTextureCamera);
        SessionState.SetBool(SessionPrefix + "SceneFinished", true);
        EditorApplication.isPlaying = false;
    }

    private static void ConfigureCapture(GameManager manager)
    {
        manager.randomNoise = false;
        manager.enableFrameAccumulation = true;
        manager.TemporalDenoising.enabled = false;
        manager.debugRenderMode = DebugRenderMode.FinalColor;
        manager.numberOfPasses = 1;
        manager._singleFrame = true;

        _captureTarget = new RenderTexture(
            SessionState.GetInt(SessionPrefix + "Width", DefaultCaptureWidth),
            SessionState.GetInt(SessionPrefix + "Height", DefaultCaptureHeight),
            24,
            RenderTextureFormat.ARGB32)
        {
            name = "Ray Tracing Scene Capture",
            enableRandomWrite = true
        };
        _captureTarget.Create();
        manager.renderTextureCamera.targetTexture = _captureTarget;
        _captureSource = new RenderTexture(_captureTarget.width, _captureTarget.height, 0, RenderTextureFormat.ARGB32)
        {
            name = "Ray Tracing Scene Capture Source"
        };
        _captureSource.Create();
        // Give Start() and coroutine-based scene setup one additional Play-mode frame.
        SessionState.SetInt(SessionPrefix + "WarmupFrames", 2);
    }

    private static void FailCurrentScene(string reason)
    {
        Debug.LogError($"Ray tracing scene capture failed: '{GetCurrentScenePath()}' {reason}.");
        ReleaseCaptureTarget(null);
        ClearSession();
        ExitBatchMode(1);
    }

    private static void CompleteCapture()
    {
        string outputDirectory = Path.Combine(
            SessionState.GetString(SessionPrefix + "Output", GetDefaultOutputRoot()),
            SessionState.GetString(SessionPrefix + "Label", "capture"));
        Debug.Log($"Ray tracing scene capture complete: '{outputDirectory}'.");
        AssetDatabase.Refresh();
        ReleaseCaptureTarget(null);
        ClearSession();
        ExitBatchMode(0);
    }

    private static void ReleaseCaptureTarget(Camera camera)
    {
        if (camera != null && camera.targetTexture == _captureTarget)
        {
            camera.targetTexture = null;
        }
        if (_captureTarget != null)
        {
            if (RenderTexture.active == _captureTarget)
            {
                RenderTexture.active = null;
            }
            _captureTarget.Release();
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_captureTarget);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_captureTarget);
            }
            _captureTarget = null;
        }
        if (_captureSource != null)
        {
            _captureSource.Release();
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_captureSource);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_captureSource);
            }
            _captureSource = null;
        }
    }

    private static bool IsActive()
    {
        return SessionState.GetBool(SessionPrefix + "Active", false);
    }

    private static string[] GetScenePaths()
    {
        return SessionState.GetString(SessionPrefix + "Paths", string.Empty)
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string GetCurrentScenePath()
    {
        string[] paths = GetScenePaths();
        int index = SessionState.GetInt(SessionPrefix + "Index", 0);
        return index >= 0 && index < paths.Length ? paths[index] : "<unknown scene>";
    }

    private static void ClearSession()
    {
        SessionState.EraseBool(SessionPrefix + "Active");
        SessionState.EraseString(SessionPrefix + "Paths");
        SessionState.EraseString(SessionPrefix + "Output");
        SessionState.EraseString(SessionPrefix + "Label");
        SessionState.EraseInt(SessionPrefix + "Samples");
        SessionState.EraseInt(SessionPrefix + "Width");
        SessionState.EraseInt(SessionPrefix + "Height");
        SessionState.EraseInt(SessionPrefix + "Index");
        SessionState.EraseBool(SessionPrefix + "WaitingForPlay");
        SessionState.EraseBool(SessionPrefix + "SceneFinished");
        SessionState.EraseBool(SessionPrefix + "Configured");
        SessionState.EraseInt(SessionPrefix + "WarmupFrames");
    }


    private static string GetDefaultOutputRoot()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), DefaultOutputFolder);
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }
        return string.IsNullOrWhiteSpace(value) ? "capture" : value;
    }

    private static string GetCommandLineArgument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length - 1; i++)
        {
            if (arguments[i] == name)
            {
                return arguments[i + 1];
            }
        }
        return null;
    }

    private static bool HasCommandLineArgument(string name)
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument == name)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCaptureSettings(out int samplesPerScene, out int captureWidth, out int captureHeight,
        out double durationSeconds)
    {
        samplesPerScene = DefaultSamplesPerScene;
        captureWidth = DefaultCaptureWidth;
        captureHeight = DefaultCaptureHeight;
        durationSeconds = 0.0;

        if (!TryGetPositiveIntegerArgument("-rayTracingSamples", ref samplesPerScene)
            || !TryGetPositiveIntegerArgument("-rayTracingWidth", ref captureWidth)
            || !TryGetPositiveIntegerArgument("-rayTracingHeight", ref captureHeight))
        {
            return false;
        }

        string durationArgument = GetCommandLineArgument("-rayTracingDurationSeconds");
        if (durationArgument != null && (!double.TryParse(durationArgument, out durationSeconds) || durationSeconds <= 0.0))
        {
            Debug.LogError($"Scene capture argument -rayTracingDurationSeconds must be positive; received '{durationArgument}'.");
            return false;
        }
        return durationArgument == null || !HasCommandLineArgument("-rayTracingSamples") || durationSeconds > 0.0;
    }

    private static bool TryGetPositiveIntegerArgument(string name, ref int value)
    {
        string argument = GetCommandLineArgument(name);
        if (argument == null)
        {
            return true;
        }
        if (int.TryParse(argument, out int parsedValue) && parsedValue > 0)
        {
            value = parsedValue;
            return true;
        }

        Debug.LogError($"Scene capture argument {name} must be a positive integer; received '{argument}'.");
        return false;
    }

    private static void ExitBatchMode(int exitCode)
    {
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(exitCode);
        }
    }
}
