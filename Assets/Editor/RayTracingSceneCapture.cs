using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
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
    private const string DefaultReferenceRoot = "Assets/Editor/RayTracingSceneReferences";
    private const int ReferenceWidth = 1024;
    private const int ReferenceHeight = 1024;
    private const double ReferenceDurationSeconds = 120.0;
    private const int ThermalCooldownMilliseconds = 10000;
    private const string SessionPrefix = "GPURayTracing.SceneCapture.";
    private static RenderTexture _captureTarget;
    private static RenderTexture _captureSource;

    [Serializable]
    private class ReferenceMetadata
    {
        public int schemaVersion = 1;
        public string scenePath;
        public string imageSha256;
        public int width;
        public int height;
        public double durationSeconds;
        public int measuredFrames;
        public string unityVersion;
        public string graphicsDeviceType;
        public string generatedUtc;
    }

    private readonly struct CaptureResult
    {
        public readonly string imagePath;
        public readonly int measuredFrames;
        public readonly double totalMilliseconds;
        public CaptureResult(string imagePath, int measuredFrames, double totalMilliseconds)
        {
            this.imagePath = imagePath;
            this.measuredFrames = measuredFrames;
            this.totalMilliseconds = totalMilliseconds;
        }
    }

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
        var referenceMetrics = HasCommandLineArgument("-rayTracingReferenceMetrics");
        var refreshReferences = HasCommandLineArgument("-rayTracingRefreshReferences");
        var requireExistingReferences = HasCommandLineArgument("-rayTracingRequireExistingReferences");
        var referenceRoot = GetCommandLineArgument("-rayTracingReferenceRoot") ?? DefaultReferenceRoot;
        if (!TryGetCaptureSettings(out int samplesPerScene, out int captureWidth, out int captureHeight,
                out double durationSeconds))
        {
            ExitBatchMode(1);
            return;
        }
        var label = GetCommandLineArgument("-rayTracingCaptureLabel") ?? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var debugRenderMode = GetDebugRenderMode();
        if (referenceMetrics && (!compareAdaptiveSampling || durationSeconds <= 0.0 || captureWidth != ReferenceWidth
            || captureHeight != ReferenceHeight || debugRenderMode != DebugRenderMode.FinalColor))
        {
            Debug.LogError("-rayTracingReferenceMetrics requires -rayTracingCompareAdaptiveSampling, a positive duration, FinalColor, and 1024x1024 capture dimensions.");
            ExitBatchMode(1);
            return;
        }
        if ((refreshReferences || requireExistingReferences) && !referenceMetrics)
        {
            Debug.LogError("-rayTracingRefreshReferences and -rayTracingRequireExistingReferences require -rayTracingReferenceMetrics.");
            ExitBatchMode(1);
            return;
        }
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
                durationSeconds, debugRenderMode, compareAdaptiveSampling, referenceMetrics,
                refreshReferences, requireExistingReferences, referenceRoot);
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
        bool referenceMetrics,
        bool refreshReferences,
        bool requireExistingReferences,
        string referenceRoot)
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
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, trimmedPath, referenceMetrics,
                        refreshReferences, requireExistingReferences, referenceRoot);
                }
                else
                {
                    CaptureVariant(manager, sceneName, Path.Combine(outputRoot, SanitizePathSegment(label)), sceneName, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, false, false);
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
        DebugRenderMode debugRenderMode,
        string scenePath,
        bool referenceMetrics,
        bool refreshReferences,
        bool requireExistingReferences,
        string referenceRoot)
    {
        var comparisonRoot = Path.Combine(outputRoot, SanitizePathSegment(label), sceneName);
        ReferenceMetadata reference = null;
        string referenceImagePath = null;
        if (referenceMetrics)
        {
            referenceImagePath = EnsureReference(manager, scenePath, referenceRoot, refreshReferences, requireExistingReferences,
                out reference);
        }

        CaptureResult adaptiveOff = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_off", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, false, true);
        CoolDownBetweenTimedCaptures(durationSeconds);
        CaptureResult adaptiveOn = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_on", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, true, true);
        if (referenceMetrics)
        {
            WriteReferenceMetrics(comparisonRoot, "adaptive_off", adaptiveOff, referenceImagePath, reference);
            WriteReferenceMetrics(comparisonRoot, "adaptive_on", adaptiveOn, referenceImagePath, reference);
        }
    }

    private static void CoolDownBetweenTimedCaptures(double durationSeconds)
    {
        if (durationSeconds > 0.0)
        {
            Debug.Log($"Cooling down for {ThermalCooldownMilliseconds / 1000} seconds before the next timed capture.");
            Thread.Sleep(ThermalCooldownMilliseconds);
        }
    }

    private static CaptureResult CaptureVariant(
        GameManager manager,
        string sceneName,
        string outputRoot,
        string label,
        int samplesPerScene,
        int captureWidth,
        int captureHeight,
        double durationSeconds,
        DebugRenderMode debugRenderMode,
        bool adaptiveSampling,
        bool writeTimingReport)
    {
        manager.randomNoise = false;
        manager.enableFrameAccumulation = true;
        manager.enableAdaptiveSampling = adaptiveSampling;
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
        while ((durationSeconds > 0.0 && stopwatch.Elapsed.TotalSeconds < durationSeconds)
               || (durationSeconds <= 0.0 && measuredFrames < samplesPerScene))
        {
            manager.RenderImage(_captureSource, _captureTarget);
            measuredFrames++;
            if (durationSeconds > 0.0)
            {
                // RenderImage can submit work faster than Metal retires it. Synchronizing each
                // duration frame makes the wall-clock budget measure completed rendering rather
                // than CPU submission followed by an unreported queue drain at the end.
                SynchronizeDurationCaptureGpu();
            }
        }
        stopwatch.Stop();
        SynchronizeDurationCaptureGpu();

        Directory.CreateDirectory(outputRoot);
        string outputPath = Path.Combine(outputRoot, label + ".png");
        manager.ExportCurrentRenderPng(outputPath);
        if (writeTimingReport)
        {
            WriteTimingReport(outputRoot, label, sceneName, adaptiveSampling, measuredFrames, durationSeconds,
                captureWidth, captureHeight, stopwatch.Elapsed.TotalMilliseconds);
        }
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}' ({stopwatch.Elapsed.TotalMilliseconds:0.00} ms).");
        ReleaseCaptureTarget(manager.renderTextureCamera);
        return new CaptureResult(outputPath, measuredFrames, stopwatch.Elapsed.TotalMilliseconds);
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
            $"Measured frames: {measuredFrames}\n" +
            $"Requested duration: {(requestedDurationSeconds > 0.0 ? requestedDurationSeconds.ToString("0.###") + " seconds" : "fixed sample count")}\n" +
                $"Resolution: {width}x{height}\n" +
                $"Total measured render time: {totalMilliseconds:0.000} ms\n" +
                $"Average render time per frame: {averageMilliseconds:0.000} ms\n" +
                $"Average measured FPS: {(averageMilliseconds > 0.0 ? 1000.0 / averageMilliseconds : 0.0):0.000}\n");
    }

    private static string EnsureReference(GameManager manager, string scenePath, string referenceRoot, bool refresh,
        bool requireExisting, out ReferenceMetadata metadata)
    {
        string imagePath = GetReferenceImagePath(scenePath, referenceRoot);
        string metadataPath = Path.ChangeExtension(imagePath, ".json");
        bool imageExists = File.Exists(imagePath);
        bool metadataExists = File.Exists(metadataPath);
        if (!refresh && imageExists && metadataExists)
        {
            metadata = ValidateReference(scenePath, imagePath, metadataPath);
            return imagePath;
        }
        if (!refresh && (imageExists || metadataExists))
        {
            throw new InvalidOperationException($"Reference for '{scenePath}' is incomplete. Review it or use -rayTracingRefreshReferences.");
        }
        if (!refresh && requireExisting)
        {
            throw new InvalidOperationException($"Reference for '{scenePath}' is missing at '{imagePath}'.");
        }

        Debug.Log($"Generating 120-second adaptive-off reference for '{scenePath}'.");
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        CaptureResult result = CaptureVariant(manager, sceneName, Path.GetDirectoryName(imagePath), sceneName,
            0, ReferenceWidth, ReferenceHeight, ReferenceDurationSeconds, DebugRenderMode.FinalColor,
            false, false);
        if (result.imagePath != imagePath)
        {
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }
            File.Move(result.imagePath, imagePath);
        }
        metadata = new ReferenceMetadata
        {
            scenePath = scenePath,
            imageSha256 = ComputeSha256(imagePath),
            width = ReferenceWidth,
            height = ReferenceHeight,
            durationSeconds = ReferenceDurationSeconds,
            measuredFrames = result.measuredFrames,
            unityVersion = Application.unityVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            generatedUtc = DateTime.UtcNow.ToString("O")
        };
        File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
        AssetDatabase.Refresh();
        return imagePath;
    }

    private static string GetReferenceImagePath(string scenePath, string referenceRoot)
    {
        const string sceneRoot = "Assets/Scenes/";
        if (!scenePath.StartsWith(sceneRoot, StringComparison.Ordinal) || !referenceRoot.StartsWith("Assets/Editor/", StringComparison.Ordinal))
        {
            throw new ArgumentException("References require scenes below Assets/Scenes and a root below Assets/Editor.");
        }
        string relativeScene = Path.ChangeExtension(scenePath.Substring(sceneRoot.Length), null);
        string[] segments = relativeScene.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        string directory = referenceRoot;
        foreach (string segment in segments)
        {
            directory = Path.Combine(directory, SanitizePathSegment(segment));
        }
        return Path.Combine(directory, SanitizePathSegment(Path.GetFileNameWithoutExtension(scenePath)) + ".png");
    }

    private static ReferenceMetadata ValidateReference(string scenePath, string imagePath, string metadataPath)
    {
        ReferenceMetadata metadata;
        try
        {
            metadata = JsonUtility.FromJson<ReferenceMetadata>(File.ReadAllText(metadataPath));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not read reference metadata '{metadataPath}'.", exception);
        }
        if (metadata == null || metadata.schemaVersion != 1 || metadata.scenePath != scenePath
            || metadata.width != ReferenceWidth || metadata.height != ReferenceHeight
            || metadata.durationSeconds != ReferenceDurationSeconds || metadata.imageSha256 != ComputeSha256(imagePath))
        {
            throw new InvalidOperationException($"Reference '{imagePath}' does not match the required 120-second 1024x1024 contract. Use -rayTracingRefreshReferences after review.");
        }
        var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!image.LoadImage(File.ReadAllBytes(imagePath), false) || image.width != ReferenceWidth || image.height != ReferenceHeight)
            {
                throw new InvalidOperationException($"Reference image '{imagePath}' is not a readable 1024x1024 PNG.");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(image);
        }
        return metadata;
    }

    private static string ComputeSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
    }

    private static void WriteReferenceMetrics(string outputRoot, string label, CaptureResult result, string referencePath,
        ReferenceMetadata reference)
    {
        var candidate = new Texture2D(2, 2, TextureFormat.RGB24, false);
        var referenceImage = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!candidate.LoadImage(File.ReadAllBytes(result.imagePath), false)
                || !referenceImage.LoadImage(File.ReadAllBytes(referencePath), false)
                || candidate.width != referenceImage.width || candidate.height != referenceImage.height)
            {
                throw new InvalidOperationException($"Could not compare '{result.imagePath}' to reference '{referencePath}'.");
            }
            Color[] actual = candidate.GetPixels();
            Color[] expected = referenceImage.GetPixels();
            double rgbAbsolute = 0.0, rgbSquared = 0.0, luminanceAbsolute = 0.0, luminanceSquared = 0.0, relative = 0.0;
            int aboveThreshold = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                Color a = actual[i].linear;
                Color b = expected[i].linear;
                float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
                rgbAbsolute += Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
                rgbSquared += dr * dr + dg * dg + db * db;
                float ay = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
                float by = 0.2126f * b.r + 0.7152f * b.g + 0.0722f * b.b;
                float difference = Math.Abs(ay - by);
                luminanceAbsolute += difference;
                luminanceSquared += difference * difference;
                relative += difference / Math.Max(by, 0.01f);
                if (difference > 0.01f) aboveThreshold++;
            }
            int pixels = actual.Length;
            double rgbRmse = Math.Sqrt(rgbSquared / (pixels * 3.0));
            double psnr = rgbRmse == 0.0 ? double.PositiveInfinity : 20.0 * Math.Log10(1.0 / rgbRmse);
            string report = "{\n" +
            $"  \"referenceImage\": \"{referencePath}\",\n" +
            $"  \"referenceSha256\": \"{reference.imageSha256}\",\n" +
            "  \"comparisonColorSpace\": \"linear-srgb\",\n" +
            $"  \"rgbMeanAbsoluteError\": {rgbAbsolute / (pixels * 3.0):R},\n" +
            $"  \"rgbRootMeanSquaredError\": {rgbRmse:R},\n" +
            $"  \"rgbPsnrDb\": {(double.IsPositiveInfinity(psnr) ? "null" : psnr.ToString("R"))},\n" +
            $"  \"luminanceMeanAbsoluteError\": {luminanceAbsolute / pixels:R},\n" +
            $"  \"luminanceRootMeanSquaredError\": {Math.Sqrt(luminanceSquared / pixels):R},\n" +
            $"  \"luminanceMeanRelativeAbsoluteError\": {relative / pixels:R},\n" +
            $"  \"luminanceFractionAbove0_01\": {(double)aboveThreshold / pixels:R},\n" +
            $"  \"measuredFrames\": {result.measuredFrames},\n" +
            $"  \"totalMeasuredRenderMilliseconds\": {result.totalMilliseconds:R}\n" +
                "}\n";
            File.WriteAllText(Path.Combine(outputRoot, label + ".metrics.json"), report);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(candidate);
            UnityEngine.Object.DestroyImmediate(referenceImage);
        }
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
