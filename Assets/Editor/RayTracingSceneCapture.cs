using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const string DefaultOutputFolder = "TestCaptures";
    private const string GalleryThumbnailOutputFolder = "Assets/Editor/RayTracingSceneGalleryThumbnails";
    private const string GalleryThumbnailLabel = "Current";
    private const string DefaultReferenceRoot = "Assets/Editor/RayTracingSceneReferences";
    private const int ReferenceWidth = 1024;
    private const int ReferenceHeight = 1024;
    private const double ReferenceDurationSeconds = 240.0;
    private const double MaximumTimedCaptureSeconds = 120.0;
    private const int ThermalCooldownMilliseconds = 10000;
    private const float DifferenceHeatmapRedPercentile = 0.99f;
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
        public readonly ulong retiredPaths;
        public readonly GameManager.AdaptiveDiagnosticsData adaptiveDiagnostics;
        public CaptureResult(string imagePath, int measuredFrames, double totalMilliseconds, ulong retiredPaths)
        {
            this.imagePath = imagePath;
            this.measuredFrames = measuredFrames;
            this.totalMilliseconds = totalMilliseconds;
            this.retiredPaths = retiredPaths;
            adaptiveDiagnostics = null;
        }
        public CaptureResult(string imagePath, int measuredFrames, double totalMilliseconds,
            ulong retiredPaths, GameManager.AdaptiveDiagnosticsData adaptiveDiagnostics)
            : this(imagePath, measuredFrames, totalMilliseconds, retiredPaths)
        {
            this.adaptiveDiagnostics = adaptiveDiagnostics;
        }
    }

    private readonly struct AdaptiveCaptureFrame
    {
        public readonly int frame;
        public readonly double milliseconds;
        public readonly GameManager.AdaptiveFrameTelemetry telemetry;

        public AdaptiveCaptureFrame(int frame, double milliseconds, GameManager.AdaptiveFrameTelemetry telemetry)
        {
            this.frame = frame;
            this.milliseconds = milliseconds;
            this.telemetry = telemetry;
        }
    }

    static RayTracingSceneCapture()
    {
        EditorApplication.update += Update;
    }

    // Invoke with -executeMethod RayTracingSceneCapture.CaptureFromCommandLine.
    public static void CaptureFromCommandLine()
    {
        var differenceImageA = GetCommandLineArgument("-rayTracingDifferenceImageA");
        var differenceImageB = GetCommandLineArgument("-rayTracingDifferenceImageB");
        var differenceOutput = GetCommandLineArgument("-rayTracingDifferenceOutput");
        if (differenceImageA != null || differenceImageB != null || differenceOutput != null)
        {
            if (string.IsNullOrWhiteSpace(differenceImageA) || string.IsNullOrWhiteSpace(differenceImageB)
                || string.IsNullOrWhiteSpace(differenceOutput))
            {
                ReportCommandLineError("Difference image generation requires -rayTracingDifferenceImageA, -rayTracingDifferenceImageB, and -rayTracingDifferenceOutput.");
                ExitBatchMode(1);
                return;
            }

            try
            {
                GenerateDifferenceImage(differenceImageA, differenceImageB, differenceOutput);
                Debug.Log($"Difference image written to '{differenceOutput}'.");
                ExitBatchMode(0);
            }
            catch (Exception exception)
            {
                ReportCommandLineError("Difference image generation failed.", exception);
                ExitBatchMode(1);
            }
            return;
        }

        var sceneArgument = GetCommandLineArgument("-rayTracingScenes");
        var outputArgument = GetCommandLineArgument("-rayTracingOutput");
        var generateScenes = HasCommandLineArgument("-rayTracingGenerateScenes");
        var compareAdaptiveSampling = HasCommandLineArgument("-rayTracingCompareAdaptiveSampling");
        var skipAdaptiveOff = HasCommandLineArgument("-rayTracingSkipAdaptiveOff");
        var referenceMetrics = HasCommandLineArgument("-rayTracingReferenceMetrics");
        var refreshReferences = HasCommandLineArgument("-rayTracingRefreshReferences");
        var requireExistingReferences = HasCommandLineArgument("-rayTracingRequireExistingReferences");
        var referenceRoot = GetCommandLineArgument("-rayTracingReferenceRoot") ?? DefaultReferenceRoot;
        float? brightnessPriority;
        float? directLightPriority;
        float? roughnessPriority;
        if (!TryGetSignedPriorityArgument("-rayTracingAdaptiveBrightnessPriority", out brightnessPriority)
            || !TryGetSignedPriorityArgument("-rayTracingAdaptiveDirectLightPriority", out directLightPriority)
            || !TryGetSignedPriorityArgument("-rayTracingAdaptiveRoughnessPriority", out roughnessPriority))
        {
            ExitBatchMode(1);
            return;
        }

        if (!TryGetCaptureSettings(out var samplesPerScene, out var captureWidth, out var captureHeight,
                out var durationSeconds))
        {
            ReportCommandLineError("Invalid scene capture settings.");
            ExitBatchMode(1);
            return;
        }

        // The candidate comparison is capped independently from the longer reference capture.
        if (compareAdaptiveSampling && durationSeconds > MaximumTimedCaptureSeconds)
        {
            ReportCommandLineError($"Timed captures are capped at {MaximumTimedCaptureSeconds:0} seconds to prevent adaptive regressions from stalling the editor.");
            ExitBatchMode(1);
            return;
        }

        var label = GetCommandLineArgument("-rayTracingCaptureLabel") ?? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var debugRenderMode = GetDebugRenderMode();

        if (referenceMetrics && (!compareAdaptiveSampling || captureWidth != ReferenceWidth
            || captureHeight != ReferenceHeight || debugRenderMode != DebugRenderMode.FinalColor))
        {
            ReportCommandLineError("-rayTracingReferenceMetrics requires -rayTracingCompareAdaptiveSampling, FinalColor, and 1024x1024 capture dimensions.");
            ExitBatchMode(1);
            return;
        }

        if ((refreshReferences || requireExistingReferences) && !referenceMetrics)
        {
            ReportCommandLineError("-rayTracingRefreshReferences and -rayTracingRequireExistingReferences require -rayTracingReferenceMetrics.");
            ExitBatchMode(1);
            return;
        }

        if (skipAdaptiveOff && !compareAdaptiveSampling)
        {
            ReportCommandLineError("-rayTracingSkipAdaptiveOff requires -rayTracingCompareAdaptiveSampling.");
            ExitBatchMode(1);
            return;
        }

        if (string.IsNullOrWhiteSpace(sceneArgument))
        {
            ReportCommandLineError("Scene capture requires -rayTracingScenes with semicolon-separated scene asset paths.");
            ExitBatchMode(1);
            return;
        }

        var scenes = sceneArgument.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (generateScenes)
        {
            RayTracingSceneGenerator.GenerateScenes(scenes, true);
        }
        
        var outputRoot = string.IsNullOrWhiteSpace(outputArgument) ? GetDefaultOutputRoot() : outputArgument;
        label = GetAvailableCaptureLabel(outputRoot, label);
        if (Application.isBatchMode)
        {
            CaptureInBatchMode(label, scenes, outputRoot, samplesPerScene, captureWidth, captureHeight,
                durationSeconds, debugRenderMode, compareAdaptiveSampling, skipAdaptiveOff, referenceMetrics,
                refreshReferences, requireExistingReferences, referenceRoot, brightnessPriority, directLightPriority, roughnessPriority);
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
        bool skipAdaptiveOff,
        bool referenceMetrics,
        bool refreshReferences,
        bool requireExistingReferences,
        string referenceRoot,
        float? brightnessPriority,
         float? directLightPriority,
         float? roughnessPriority)
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
                if (brightnessPriority.HasValue)
                {
                    manager.adaptiveGuidanceBrightnessPriority = brightnessPriority.Value;
                }
                if (directLightPriority.HasValue)
                {
                    manager.adaptiveGuidanceDirectLightPriority = directLightPriority.Value;
                }
                if (roughnessPriority.HasValue)
                {
                    manager.adaptiveGuidanceRoughnessPriority = roughnessPriority.Value;
                }
                if (compareAdaptiveSampling)
                {
                    CaptureAdaptiveComparison(manager, sceneName, outputRoot, label, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, trimmedPath, skipAdaptiveOff, referenceMetrics,
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
            string failureLogPath = TryWriteCaptureLog(Path.Combine(outputRoot, SanitizePathSegment(label)), label);
            string logDetails = failureLogPath == null
                ? $"Unity console log: '{Application.consoleLogPath}'."
                : $"Unity console log: '{Application.consoleLogPath}'. Capture log: '{failureLogPath}'.";
            ReportCommandLineError("Ray tracing scene capture failed. " + logDetails, exception);
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
        bool skipAdaptiveOff,
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

        CaptureResult adaptiveOff = default;
        if (!skipAdaptiveOff)
        {
            adaptiveOff = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_off", samplesPerScene, captureWidth,
                captureHeight, durationSeconds, debugRenderMode, false, true);
            CoolDownBetweenTimedCaptures(durationSeconds);
        }
        CaptureResult adaptiveOn = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_on", samplesPerScene, captureWidth,
            captureHeight, durationSeconds, debugRenderMode, true, true);
        if (referenceMetrics)
        {
            if (!skipAdaptiveOff)
            {
                WriteReferenceMetrics(comparisonRoot, "adaptive_off", adaptiveOff, referenceImagePath, reference);
            }
            WriteReferenceMetrics(comparisonRoot, "adaptive_on", adaptiveOn, referenceImagePath, reference);
        }
        if (!skipAdaptiveOff)
        {
            GenerateDifferenceImage(adaptiveOff.imagePath, adaptiveOn.imagePath,
                Path.Combine(comparisonRoot, "adaptive_off_vs_on_difference.png"));
        }
        if (referenceMetrics)
        {
            GenerateDifferenceImage(adaptiveOn.imagePath, referenceImagePath,
                Path.Combine(comparisonRoot, "adaptive_on_vs_reference_difference.png"));
            if (!skipAdaptiveOff)
            {
                GenerateDifferenceImage(adaptiveOff.imagePath, referenceImagePath,
                    Path.Combine(comparisonRoot, "adaptive_off_vs_reference_difference.png"));
                GenerateReferenceComparisonImage(adaptiveOff.imagePath, adaptiveOn.imagePath, referenceImagePath,
                    Path.Combine(comparisonRoot, "adaptive_on_red_off_green_vs_reference.png"));
            }
        }
        WriteCaptureLog(comparisonRoot, label);
    }

    private static void WriteCaptureLog(string outputRoot, string label)
    {
        TryWriteCaptureLog(outputRoot, label);
    }

    private static string TryWriteCaptureLog(string outputRoot, string label)
    {
        string consoleLogPath = Application.consoleLogPath;
        if (string.IsNullOrWhiteSpace(consoleLogPath) || !File.Exists(consoleLogPath))
        {
            Debug.LogWarning("Could not copy the Unity console log because its source path is unavailable.");
            return consoleLogPath;
        }

        try
        {
            Directory.CreateDirectory(outputRoot);
            string destination = Path.Combine(outputRoot, SanitizePathSegment(label) + ".log");
            File.Copy(consoleLogPath, destination, true);
            return destination;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Could not copy the Unity console log: {exception.Message}");
            return null;
        }
    }

    private static void ReportCommandLineError(string message, Exception exception = null)
    {
        string details = exception == null ? message : message + Environment.NewLine + exception;
        Debug.LogError(details);
        Console.Error.WriteLine(details);
        Console.Error.Flush();
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
        manager.SetAdaptiveCaptureDiagnostics(adaptiveSampling);
        // Capture both variants with the same fixed path budget: one path per pixel per frame.
        manager.adaptiveSamplingMinSamples = Mathf.Max(1, manager.adaptiveSamplingMinSamples);
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
        var adaptiveFrames = adaptiveSampling ? new List<AdaptiveCaptureFrame>() : null;
        var stopwatch = Stopwatch.StartNew();
        while ((durationSeconds > 0.0 && stopwatch.Elapsed.TotalSeconds < durationSeconds)
               || (durationSeconds <= 0.0 && measuredFrames < samplesPerScene))
        {
            var frameStopwatch = Stopwatch.StartNew();
            manager.RenderImage(_captureSource, _captureTarget);
            measuredFrames++;
            if (durationSeconds > 0.0 || adaptiveSampling)
            {
                // RenderImage can submit work faster than Metal retires it. Synchronizing each
                // duration frame makes the wall-clock budget measure completed rendering rather
                // than CPU submission followed by an unreported queue drain at the end.
                SynchronizeDurationCaptureGpu();
            }
            frameStopwatch.Stop();
            if (adaptiveSampling)
            {
                adaptiveFrames.Add(new AdaptiveCaptureFrame(measuredFrames, frameStopwatch.Elapsed.TotalMilliseconds,
                    manager.ReadAdaptiveFrameTelemetryForCapture()));
            }
        }
        stopwatch.Stop();
        SynchronizeDurationCaptureGpu();
        var adaptiveDiagnostics = adaptiveSampling ? manager.ReadAdaptiveDiagnosticsForCapture() : null;
        ulong retiredPaths = adaptiveSampling
            ? SumPathCounts(adaptiveDiagnostics.pathCounts) + SumGuidancePaths(adaptiveFrames)
            : (ulong)captureWidth * (ulong)captureHeight * (ulong)measuredFrames;

        Directory.CreateDirectory(outputRoot);
        string outputPath = Path.Combine(outputRoot, label + ".png");
        manager.ExportCurrentRenderPng(outputPath);
        if (writeTimingReport)
        {
            WriteTimingReport(outputRoot, label, sceneName, adaptiveSampling, measuredFrames, durationSeconds,
                captureWidth, captureHeight, stopwatch.Elapsed.TotalMilliseconds, retiredPaths);
            if (adaptiveDiagnostics != null) WriteAdaptiveDiagnostics(outputRoot, adaptiveDiagnostics, adaptiveFrames);
            if (adaptiveDiagnostics != null)
            {
                WriteAdaptiveFrameTelemetry(outputRoot, adaptiveFrames);
                WriteAdaptiveAllocationHeatmap(outputRoot, adaptiveDiagnostics);
            }
        }
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}' ({stopwatch.Elapsed.TotalMilliseconds:0.00} ms).");
        ReleaseCaptureTarget(manager.renderTextureCamera);
        return new CaptureResult(outputPath, measuredFrames, stopwatch.Elapsed.TotalMilliseconds, retiredPaths, adaptiveDiagnostics);
    }

    private static void WriteAdaptiveDiagnostics(string outputRoot, GameManager.AdaptiveDiagnosticsData diagnostics,
        List<AdaptiveCaptureFrame> frames)
    {
        uint[] metadata = diagnostics.metadata;
        var pathCounts = (float[])diagnostics.pathCounts.Clone();
        var uncertainties = (float[])diagnostics.uncertainties.Clone();
        Array.Sort(pathCounts);
        Array.Sort(uncertainties);
        int pixels = pathCounts.Length;
        if (pixels == 0 || metadata.Length < GameManager.AdaptiveDiagnosticsMetadataCount)
        {
            throw new InvalidOperationException("Adaptive diagnostics readback returned incomplete data.");
        }
        uint requestedPaths = metadata[GameManager.AdaptiveMetadataRequestedPaths];
        uint assignedPaths = metadata[GameManager.AdaptiveMetadataAssignedPaths];
        uint fullResolutionRetiredPaths = metadata[GameManager.AdaptiveMetadataRetiredPaths];
        uint finalFrameGuidancePaths = metadata[GameManager.AdaptiveMetadataGuidancePaths];
        uint finalFrameRetiredPaths = fullResolutionRetiredPaths + finalFrameGuidancePaths;
        ulong cumulativeGuidancePaths = SumGuidancePaths(frames);
        ulong cumulativeFullResolutionPaths = SumFullResolutionPaths(frames);
        ulong cumulativeRetiredPaths = cumulativeFullResolutionPaths + cumulativeGuidancePaths;
        uint activeWorkItems = metadata[GameManager.AdaptiveMetadataWorkItemCount];
        uint overflow = metadata[GameManager.AdaptiveMetadataWorkListOverflow];
        ulong workItemPaths = 0;
        var assignedPixels = new bool[pixels];
        for (int index = 0; index < diagnostics.workItemPathCounts.Length; index++)
        {
            uint pixel = diagnostics.workItemPixels[index];
            if (pixel >= pixels || assignedPixels[pixel])
            {
                throw new InvalidOperationException($"Adaptive work list contains an invalid or duplicate pixel index: {pixel}.");
            }
            assignedPixels[pixel] = true;
            workItemPaths += diagnostics.workItemPathCounts[index];
        }
        if (requestedPaths != assignedPaths || requestedPaths != fullResolutionRetiredPaths + finalFrameGuidancePaths
            || overflow != 0u || activeWorkItems > pixels || workItemPaths != fullResolutionRetiredPaths)
        {
            throw new InvalidOperationException(
                $"Adaptive allocation invariant failed: requested={requestedPaths}, assigned={assignedPaths}, guidance={finalFrameGuidancePaths}, " +
                $"workItemPaths={workItemPaths}, fullResolutionRetired={fullResolutionRetiredPaths}, workItems={activeWorkItems}, " +
                $"pixels={pixels}, overflow={overflow}.");
        }
        string report = "{\n" +
            $"  \"width\": {diagnostics.width},\n" +
            $"  \"height\": {diagnostics.height},\n" +
            $"  \"requestedRootPaths\": {requestedPaths},\n" +
            $"  \"assignedPaths\": {assignedPaths},\n" +
            $"  \"retiredPaths\": {finalFrameRetiredPaths},\n" +
            $"  \"fullResolutionRetiredPaths\": {fullResolutionRetiredPaths},\n" +
            $"  \"guidanceRetiredPaths\": {finalFrameGuidancePaths},\n" +
            $"  \"totalRetiredPaths\": {finalFrameRetiredPaths},\n" +
            $"  \"captureCumulativeFullResolutionRetiredPaths\": {cumulativeFullResolutionPaths},\n" +
            $"  \"captureCumulativeGuidanceRetiredPaths\": {cumulativeGuidancePaths},\n" +
            $"  \"captureCumulativeTotalRetiredPaths\": {cumulativeRetiredPaths},\n" +
            $"  \"coarseGroups\": {metadata[GameManager.AdaptiveMetadataCoarseGroups]},\n" +
            $"  \"activeWorkItems\": {activeWorkItems},\n" +
            $"  \"workItemPaths\": {workItemPaths},\n" +
            $"  \"workListOverflow\": {overflow},\n" +
            $"  \"pathCountMin\": {pathCounts[0]:R},\n" +
            $"  \"pathCountMean\": {Mean(pathCounts):R},\n" +
            $"  \"pathCountMax\": {pathCounts[pixels - 1]:R},\n" +
            $"  \"pathCountP50\": {Percentile(pathCounts, 0.50f):R},\n" +
            $"  \"pathCountP95\": {Percentile(pathCounts, 0.95f):R},\n" +
            $"  \"pathCountP99\": {Percentile(pathCounts, 0.99f):R},\n" +
            $"  \"uncertaintyMean\": {Mean(uncertainties):R},\n" +
            $"  \"uncertaintyMax\": {uncertainties[pixels - 1]:R},\n" +
            $"  \"uncertaintyP50\": {Percentile(uncertainties, 0.50f):R},\n" +
            $"  \"uncertaintyP95\": {Percentile(uncertainties, 0.95f):R},\n" +
            $"  \"uncertaintyP99\": {Percentile(uncertainties, 0.99f):R},\n" +
            $"  \"prioritySum\": {metadata[GameManager.AdaptiveMetadataPrioritySum]},\n" +
            $"  \"bootstrapPixels\": {metadata[GameManager.AdaptiveMetadataBootstrapPixels]},\n" +
            $"  \"bootstrapPaths\": {metadata[GameManager.AdaptiveMetadataBootstrapPaths]},\n" +
            "  \"priorityBucketPopulations\": [" + JoinMetadata(metadata, GameManager.AdaptiveMetadataBucketPopulationStart, 16) + "],\n" +
            "  \"priorityBucketAdmittedPaths\": [" + JoinMetadata(metadata, GameManager.AdaptiveMetadataBucketAdmittedPathsStart, 16) + "],\n" +
            "  \"priorityBucketBudgets\": [" + JoinMetadata(metadata, GameManager.AdaptiveMetadataBucketBudgetStart, 16) + "]\n" +
            "}\n";
        File.WriteAllText(Path.Combine(outputRoot, "adaptive_diagnostics.json"), report);
    }

    private static double Mean(float[] values)
    {
        double sum = 0.0;
        foreach (float value in values) sum += value;
        return values.Length == 0 ? 0.0 : sum / values.Length;
    }

    private static ulong SumPathCounts(float[] pathCounts)
    {
        ulong total = 0;
        foreach (float count in pathCounts)
        {
            total += (ulong)Mathf.Max(0.0f, count);
        }
        return total;
    }

    private static ulong SumGuidancePaths(List<AdaptiveCaptureFrame> frames)
    {
        ulong total = 0;
        if (frames == null) return total;
        foreach (AdaptiveCaptureFrame frame in frames) total += frame.telemetry.guidancePaths;
        return total;
    }

    private static ulong SumFullResolutionPaths(List<AdaptiveCaptureFrame> frames)
    {
        ulong total = 0;
        if (frames == null) return total;
        foreach (AdaptiveCaptureFrame frame in frames) total += frame.telemetry.retiredPaths;
        return total;
    }

    private static float Percentile(float[] sortedValues, float percentile)
    {
        if (sortedValues.Length == 0) return 0.0f;
        int index = Mathf.Clamp(Mathf.FloorToInt((sortedValues.Length - 1) * percentile), 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static string JoinMetadata(uint[] values, int start, int count)
    {
        var parts = new string[count];
        for (int i = 0; i < count; i++) parts[i] = values[start + i].ToString();
        return string.Join(", ", parts);
    }

    private static void WriteAdaptiveFrameTelemetry(string outputRoot, List<AdaptiveCaptureFrame> frames)
    {
        if (frames == null || frames.Count == 0) return;

        var lines = new List<string>(frames.Count + 1)
        {
            "frame,accumulated_frame,classified,frame_ms,active_work_items,full_resolution_paths,retired_paths,guidance_paths,total_retired_paths"
        };
        foreach (AdaptiveCaptureFrame frame in frames)
        {
            lines.Add($"{frame.frame},{frame.telemetry.accumulatedFrameCount},{frame.telemetry.reclassified}," +
                $"{frame.milliseconds:R},{frame.telemetry.activeWorkItems},{frame.telemetry.assignedPaths},{frame.telemetry.retiredPaths},{frame.telemetry.guidancePaths},{frame.telemetry.retiredPaths + frame.telemetry.guidancePaths}");
        }
        File.WriteAllLines(Path.Combine(outputRoot, "adaptive_frame_telemetry.csv"), lines);

        double firstHalf = 0.0;
        double secondHalf = 0.0;
        int split = Math.Max(1, frames.Count / 2);
        int reclassificationFrames = 0;
        double reclassificationMilliseconds = 0.0;
        double reuseMilliseconds = 0.0;
        int reuseFrames = 0;
        for (int index = 0; index < frames.Count; index++)
        {
            AdaptiveCaptureFrame frame = frames[index];
            if (index < split) firstHalf += frame.milliseconds;
            else secondHalf += frame.milliseconds;
            if (frame.telemetry.reclassified)
            {
                reclassificationFrames++;
                reclassificationMilliseconds += frame.milliseconds;
            }
            else
            {
                reuseFrames++;
                reuseMilliseconds += frame.milliseconds;
            }
        }
        string summary = "Adaptive Frame Telemetry\n" +
            $"Frames: {frames.Count}\n" +
            $"First-half average: {firstHalf / split:0.000} ms\n" +
            $"Second-half average: {secondHalf / Math.Max(1, frames.Count - split):0.000} ms\n" +
            $"Reclassification frames: {reclassificationFrames}, average {reclassificationMilliseconds / Math.Max(1, reclassificationFrames):0.000} ms\n" +
            $"Reuse frames: {reuseFrames}, average {reuseMilliseconds / Math.Max(1, reuseFrames):0.000} ms\n" +
            "See adaptive_frame_telemetry.csv for the synchronized per-frame sequence.\n";
        File.WriteAllText(Path.Combine(outputRoot, "adaptive_frame_telemetry.txt"), summary);
    }

    private static void WriteAdaptiveAllocationHeatmap(string outputRoot, GameManager.AdaptiveDiagnosticsData diagnostics)
    {
        int width = diagnostics.width;
        int height = diagnostics.height;
        var cumulativePaths = new uint[width * height];
        var nonZeroPaths = new List<uint>(cumulativePaths.Length);
        uint maxPaths = 0;
        for (int index = 0; index < cumulativePaths.Length; index++)
        {
            uint paths = (uint)Mathf.Max(0.0f, diagnostics.pathCounts[index]);
            cumulativePaths[index] = paths;
            if (paths == 0u) continue;
            nonZeroPaths.Add(paths);
            maxPaths = Math.Max(maxPaths, paths);
        }

        var quantileByPathCount = new Dictionary<uint, float>();
        nonZeroPaths.Sort();
        for (int start = 0; start < nonZeroPaths.Count;)
        {
            int end = start + 1;
            while (end < nonZeroPaths.Count && nonZeroPaths[end] == nonZeroPaths[start]) end++;
            // Map ties to their CDF midpoint. This reveals meaningful variation when the final
            // allocation is uniform but the cumulative path counts differ by only a few samples.
            quantileByPathCount[nonZeroPaths[start]] = (start + (end - start) * 0.5f) / nonZeroPaths.Count;
            start = end;
        }

        var texture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
        var pixels = new Color[cumulativePaths.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint paths = cumulativePaths[x + y * width];
                float quantile = paths == 0u ? 0.0f : quantileByPathCount[paths];
                pixels[x + y * width] = HeatmapColor(paths, quantile);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        File.WriteAllBytes(Path.Combine(outputRoot, "adaptive_allocation_heatmap.png"), texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        File.WriteAllText(Path.Combine(outputRoot, "adaptive_allocation_heatmap.txt"),
            "Adaptive allocation heatmap\n" +
            "Black pixels have no full-resolution path. Other colors show cumulative full-resolution paths ranked among selected pixels.\n" +
            $"Maximum cumulative paths per pixel: {maxPaths}\n" +
            "Bands: >80th percentile red, >60th yellow, >40th cyan, >20th cyan-blue, >10th blue, >5th dark-blue, otherwise navy.\n");
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
        double totalMilliseconds,
        ulong retiredPaths)
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
                $"Average measured FPS: {(averageMilliseconds > 0.0 ? 1000.0 / averageMilliseconds : 0.0):0.000}\n" +
                $"Cumulative retired paths: {retiredPaths}\n" +
                $"Adaptive policy: fixed global root budget, no automatic stopping\n" +
                $"Adaptive bootstrap paths per pixel: {(adaptiveSampling ? "configured minimum" : "uniform")}" + "\n");
    }

    private static string EnsureReference(GameManager manager, string scenePath, string referenceRoot, bool refresh,
        bool requireExisting, out ReferenceMetadata metadata)
    {
        var imagePath = GetReferenceImagePath(scenePath, referenceRoot);
        var metadataPath = Path.ChangeExtension(imagePath, ".json");
        var imageExists = File.Exists(imagePath);
        var metadataExists = File.Exists(metadataPath);
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

        Debug.Log($"Generating {ReferenceDurationSeconds:0}-second adaptive-off reference for '{scenePath}'.");
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
            throw new InvalidOperationException($"Reference '{imagePath}' does not match the required {ReferenceDurationSeconds:0}-second 1024x1024 contract. Use -rayTracingRefreshReferences after review.");
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
            $"  \"cumulativeRetiredPaths\": {result.retiredPaths},\n" +
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

    private static void GenerateDifferenceImage(string firstPath, string secondPath, string outputPath)
    {
        var first = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var second = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D difference = null;
        try
        {
            if (!first.LoadImage(File.ReadAllBytes(firstPath), false)
                || !second.LoadImage(File.ReadAllBytes(secondPath), false)
                || first.width != second.width || first.height != second.height)
            {
                throw new InvalidOperationException($"Difference images must be readable and have matching dimensions: '{firstPath}', '{secondPath}'.");
            }

            Color[] firstPixels = first.GetPixels();
            Color[] secondPixels = second.GetPixels();
            float[] differences = new float[firstPixels.Length];
            for (int i = 0; i < differences.Length; i++)
            {
                Color a = firstPixels[i].linear;
                Color b = secondPixels[i].linear;
                float dr = a.r - b.r;
                float dg = a.g - b.g;
                float db = a.b - b.b;
                float magnitude = Mathf.Sqrt((dr * dr + dg * dg + db * db) / 3.0f);
                differences[i] = magnitude;
            }

            float[] sortedDifferences = (float[])differences.Clone();
            Array.Sort(sortedDifferences);
            int redIndex = Mathf.Min(sortedDifferences.Length - 1,
                Mathf.FloorToInt((sortedDifferences.Length - 1) * DifferenceHeatmapRedPercentile));
            float redDifference = sortedDifferences[redIndex];

            difference = new Texture2D(first.width, first.height, TextureFormat.RGB24, false, true);
            Color[] outputPixels = new Color[differences.Length];
            for (int i = 0; i < differences.Length; i++)
            {
                float amount = redDifference > 0.0f ? Mathf.Clamp01(differences[i] / redDifference) : 0.0f;
                outputPixels[i] = Color.Lerp(Color.blue, Color.red, amount);
            }
            difference.SetPixels(outputPixels);
            difference.Apply(false, false);

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(outputPath, difference.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(first);
            UnityEngine.Object.DestroyImmediate(second);
            if (difference != null)
            {
                UnityEngine.Object.DestroyImmediate(difference);
            }
        }
    }

    private static void GenerateReferenceComparisonImage(string offPath, string onPath, string referencePath,
        string outputPath)
    {
        var off = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var on = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var reference = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D comparison = null;
        try
        {
            if (!off.LoadImage(File.ReadAllBytes(offPath), false)
                || !on.LoadImage(File.ReadAllBytes(onPath), false)
                || !reference.LoadImage(File.ReadAllBytes(referencePath), false)
                || off.width != on.width || off.width != reference.width
                || off.height != on.height || off.height != reference.height)
            {
                throw new InvalidOperationException(
                    $"Reference comparison images must be readable and have matching dimensions: '{offPath}', '{onPath}', '{referencePath}'.");
            }

            Color[] offPixels = off.GetPixels();
            Color[] onPixels = on.GetPixels();
            Color[] referencePixels = reference.GetPixels();
            var offDifferences = new float[offPixels.Length];
            var onDifferences = new float[offPixels.Length];
            var allDifferences = new float[offPixels.Length * 2];
            for (int i = 0; i < offPixels.Length; i++)
            {
                offDifferences[i] = DifferenceMagnitude(offPixels[i], referencePixels[i]);
                onDifferences[i] = DifferenceMagnitude(onPixels[i], referencePixels[i]);
                allDifferences[i * 2] = offDifferences[i];
                allDifferences[i * 2 + 1] = onDifferences[i];
            }

            Array.Sort(allDifferences);
            int redIndex = Mathf.Min(allDifferences.Length - 1,
                Mathf.FloorToInt((allDifferences.Length - 1) * DifferenceHeatmapRedPercentile));
            float redDifference = allDifferences[redIndex];
            comparison = new Texture2D(off.width, off.height, TextureFormat.RGB24, false, true);
            Color[] outputPixels = new Color[offPixels.Length];
            for (int i = 0; i < outputPixels.Length; i++)
            {
                float greenAmount = redDifference > 0.0f ? Mathf.Clamp01(offDifferences[i] / redDifference) : 0.0f;
                float redAmount = redDifference > 0.0f ? Mathf.Clamp01(onDifferences[i] / redDifference) : 0.0f;
                outputPixels[i] = new Color(redAmount, greenAmount, 0.0f, 1.0f);
            }
            comparison.SetPixels(outputPixels);
            comparison.Apply(false, false);

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(outputPath, comparison.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(off);
            UnityEngine.Object.DestroyImmediate(on);
            UnityEngine.Object.DestroyImmediate(reference);
            if (comparison != null) UnityEngine.Object.DestroyImmediate(comparison);
        }
    }

    private static float DifferenceMagnitude(Color first, Color second)
    {
        Color a = first.linear;
        Color b = second.linear;
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        return Mathf.Sqrt((dr * dr + dg * dg + db * db) / 3.0f);
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
        if (camera == null && _captureTarget != null)
        {
            foreach (Camera candidate in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.targetTexture == _captureTarget)
                {
                    candidate.targetTexture = null;
                }
            }
        }
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
        return Path.Combine(Application.dataPath, "..", DefaultOutputFolder);
    }

    private static string GetAvailableCaptureLabel(string outputRoot, string label)
    {
        string sanitizedLabel = SanitizePathSegment(label);
        string fullOutputRoot = Path.GetFullPath(outputRoot);
        string availableLabel = sanitizedLabel;
        int suffix = 2;
        while (Directory.Exists(Path.Combine(fullOutputRoot, availableLabel)))
        {
            availableLabel = sanitizedLabel + "_" + suffix++;
        }

        return availableLabel;
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
            ReportCommandLineError($"Scene capture argument -rayTracingDurationSeconds must be positive; received '{durationArgument}'.");
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

        ReportCommandLineError($"Scene capture argument {name} must be a positive integer; received '{argument}'.");
        return false;
    }

    private static bool TryGetSignedPriorityArgument(string name, out float? value)
    {
        string argument = GetCommandLineArgument(name);
        value = null;
        if (argument == null)
        {
            return true;
        }
        if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue)
            && parsedValue >= -2.0f && parsedValue <= 2.0f)
        {
            value = parsedValue;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {name} must be a number from -2 through 2; received '{argument}'.");
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
