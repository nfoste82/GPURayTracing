using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using PathTracing.Lighting;
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
    private const double MaximumTimedCaptureSeconds = 600.0;
    private const string GenerateReferenceArgument = "-rayTracingGenerateReference";
    private const string ExperimentArgument = "-rayTracingExperiment";
    private const string RecordEditorRunArgument = "-rayTracingRecordEditorRun";
    private const double DefaultCooldownSeconds = 10.0;
    private const float DifferenceHeatmapRedPercentile = 0.99f;
    private const int AdaptiveAllocationBlockSize = 8;
    private const string SessionPrefix = "GPURayTracing.SceneCapture.";
    private static RenderTexture _captureTarget;
    private static RenderTexture _captureSource;

    private readonly struct AdaptiveSamplingOverrides
    {
        public readonly int? minSamples;
        public readonly float? guidanceChangeThreshold;
        public readonly int? guidanceMaxUpdates;
        public readonly GameManager.AdaptivePriorityMode? priorityMode;
        public readonly float? normalizePriorityByLuminance;
        public readonly int? reclassificationInterval;
        public readonly float? highestBucketSampleRate;
        public readonly int? maxPathsPerPixel;
        public readonly int? bootstrapFrames;
        public readonly float? bootstrapResolutionScale;
        public readonly int? guidanceHistoryFrames;
        public readonly int? bootstrapGroupDivisor;

        public AdaptiveSamplingOverrides(int? minSamples, float? guidanceChangeThreshold, int? guidanceMaxUpdates,
            GameManager.AdaptivePriorityMode? priorityMode,
            float? normalizePriorityByLuminance,
            int? reclassificationInterval, float? highestBucketSampleRate,
            int? maxPathsPerPixel, int? bootstrapFrames, float? bootstrapResolutionScale,
            int? guidanceHistoryFrames, int? bootstrapGroupDivisor)
        {
            this.minSamples = minSamples;
            this.guidanceChangeThreshold = guidanceChangeThreshold;
            this.guidanceMaxUpdates = guidanceMaxUpdates;
            this.priorityMode = priorityMode;
            this.normalizePriorityByLuminance = normalizePriorityByLuminance;
            this.reclassificationInterval = reclassificationInterval;
            this.highestBucketSampleRate = highestBucketSampleRate;
            this.maxPathsPerPixel = maxPathsPerPixel;
            this.bootstrapFrames = bootstrapFrames;
            this.bootstrapResolutionScale = bootstrapResolutionScale;
            this.guidanceHistoryFrames = guidanceHistoryFrames;
            this.bootstrapGroupDivisor = bootstrapGroupDivisor;
        }
    }

    [Serializable]
    private sealed class CaptureExperiment
    {
        public int schemaVersion = 1;
        public string label;
        public string[] scenes;
        public int width = DefaultCaptureWidth;
        public int height = DefaultCaptureHeight;
        public int samples = DefaultSamplesPerScene;
        public double durationSeconds;
        public double cooldownSeconds = DefaultCooldownSeconds;
        public string referenceRoot = DefaultReferenceRoot;
        public int[] temporalRisWarmupFrames;
        public int temporalRisTrialsPerWarmup;
        public int temporalRisFirstSeed = 1;
        public ExperimentVariant[] variants;
    }

    [Serializable]
    private sealed class ExperimentVariant
    {
        public string name;
        public ExperimentOverride[] overrides;
    }

    [Serializable]
    private sealed class ExperimentOverride
    {
        public string path;
        public string value;
    }

    [Serializable]
    private class ReferenceMetadata
    {
        public int schemaVersion = 2;
        public string scenePath;
        public string imageSha256;
        public string sceneSha256;
        public string shaderSha256;
        public string sourceRevision;
        public string settingsSha256;
        public ReferenceRenderSettings settings;
        public int width;
        public int height;
        public double durationSeconds;
        public int measuredFrames;
        public string unityVersion;
        public string graphicsDeviceType;
        public string generatedUtc;
    }

    [Serializable]
    private class ReferenceRenderSettings
    {
        public bool temporalRisEnabled;
        public int initialRisCandidateCount;
        public int temporalRisHistoryMCap;
        public bool spatialRisEnabled;
        public int spatialRisNeighborCount;
        public string lightSamplingStrategy;
        public int lightSampleCount;
        public int maxLightSamples;
        public int numberOfPasses;
        public int numBounces;
        public int shadowQuality;
        public float shadowRandomness;
        public float lightFalloffScale;
        public bool environmentLighting;
        public int environmentLightSampleCount;
        public float fireflyClamp;
        public bool frameAccumulation;
        public bool randomNoise;
        public int captureSeed;
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

    private readonly struct TemporalRisTrialResult
    {
        public readonly int warmupFrames;
        public readonly int trial;
        public readonly int seed;
        public readonly string variant;
        public readonly double milliseconds;
        public readonly double rgbRmse;
        public readonly double meanLuminance;

        public TemporalRisTrialResult(int warmupFrames, int trial, int seed, string variant,
            double milliseconds, double rgbRmse, double meanLuminance)
        {
            this.warmupFrames = warmupFrames;
            this.trial = trial;
            this.seed = seed;
            this.variant = variant;
            this.milliseconds = milliseconds;
            this.rgbRmse = rgbRmse;
            this.meanLuminance = meanLuminance;
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

    private readonly struct AdaptiveAllocationSnapshot
    {
        public readonly int frame;
        public readonly bool reclassified;
        public readonly GameManager.AdaptiveAllocationFrameData allocation;

        public AdaptiveAllocationSnapshot(int frame, bool reclassified,
            GameManager.AdaptiveAllocationFrameData allocation)
        {
            this.frame = frame;
            this.reclassified = reclassified;
            this.allocation = allocation;
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
        var experimentArgument = GetCommandLineArgument(ExperimentArgument);
        if (!string.IsNullOrWhiteSpace(experimentArgument))
        {
            RunExperimentFromCommandLine(experimentArgument, outputArgument);
            return;
        }
        var generateScenes = HasCommandLineArgument("-rayTracingGenerateScenes");
        var generateReference = HasCommandLineArgument(GenerateReferenceArgument);
        var adaptiveSampling = HasCommandLineArgument("-rayTracingAdaptiveSampling");
        var compareAdaptiveSampling = HasCommandLineArgument("-rayTracingCompareAdaptiveSampling");
        var recordEditorRun = HasCommandLineArgument(RecordEditorRunArgument);
        var disableAdaptiveInstrumentation = HasCommandLineArgument("-rayTracingDisableAdaptiveInstrumentation");
        var skipAdaptiveOff = HasCommandLineArgument("-rayTracingSkipAdaptiveOff");
        if (!TryGetAdaptiveComparisonType(out GameManager.AdaptivePriorityMode? adaptiveComparisonType))
        {
            ExitBatchMode(1);
            return;
        }
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
        if (!TryGetAdaptiveSamplingOverrides(out var adaptiveSamplingOverrides))
        {
            ExitBatchMode(1);
            return;
        }

        if (!TryGetCaptureSettings(out var samplesPerScene, out var captureWidth, out var captureHeight,
                out var durationSeconds, out var cooldownSeconds))
        {
            ReportCommandLineError("Invalid scene capture settings.");
            ExitBatchMode(1);
            return;
        }

        if (generateReference && durationSeconds <= 0.0)
        {
            ReportCommandLineError($"{GenerateReferenceArgument} requires -rayTracingDurationSeconds with a positive duration.");
            ExitBatchMode(1);
            return;
        }
        if (generateReference && (compareAdaptiveSampling || referenceMetrics || skipAdaptiveOff))
        {
            ReportCommandLineError($"{GenerateReferenceArgument} cannot be combined with adaptive comparison or reference-metrics options.");
            ExitBatchMode(1);
            return;
        }

        // The candidate comparison is capped independently from the longer reference capture.
        if (compareAdaptiveSampling && GetCommandLineArgument("-rayTracingDurationSeconds") != null
            && durationSeconds > MaximumTimedCaptureSeconds)
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

        if ((refreshReferences || requireExistingReferences) && !referenceMetrics && !generateReference)
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
        if (disableAdaptiveInstrumentation && !compareAdaptiveSampling)
        {
            ReportCommandLineError("-rayTracingDisableAdaptiveInstrumentation requires -rayTracingCompareAdaptiveSampling.");
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
                durationSeconds, debugRenderMode, generateReference, compareAdaptiveSampling, skipAdaptiveOff, referenceMetrics,
                refreshReferences, requireExistingReferences, referenceRoot, brightnessPriority, directLightPriority, roughnessPriority,
                adaptiveSamplingOverrides, adaptiveSampling, adaptiveComparisonType, cooldownSeconds, disableAdaptiveInstrumentation,
                recordEditorRun);
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
        bool generateReference,
        bool compareAdaptiveSampling,
        bool skipAdaptiveOff,
        bool referenceMetrics,
        bool refreshReferences,
        bool requireExistingReferences,
        string referenceRoot,
        float? brightnessPriority,
        float? directLightPriority,
        float? roughnessPriority,
        AdaptiveSamplingOverrides adaptiveSamplingOverrides,
        bool adaptiveSampling,
        GameManager.AdaptivePriorityMode? adaptiveComparisonType,
        double cooldownSeconds,
        bool disableAdaptiveInstrumentation,
        bool recordEditorRun)
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
                ApplyAdaptiveSamplingOverrides(manager, adaptiveSamplingOverrides);
                if (generateReference)
                {
                    GenerateReference(manager, trimmedPath, referenceRoot, captureWidth, captureHeight,
                        durationSeconds, refreshReferences, requireExistingReferences);
                }
                else if (compareAdaptiveSampling)
                {
                    CaptureAdaptiveComparison(manager, sceneName, outputRoot, label, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, trimmedPath, skipAdaptiveOff, referenceMetrics,
                        refreshReferences, requireExistingReferences, referenceRoot, adaptiveSamplingOverrides, adaptiveComparisonType,
                        cooldownSeconds, !disableAdaptiveInstrumentation, recordEditorRun);
                }
                else
                {
                    CaptureVariant(manager, sceneName, Path.Combine(outputRoot, SanitizePathSegment(label)), sceneName, samplesPerScene,
                        captureWidth, captureHeight, durationSeconds, debugRenderMode, adaptiveSampling,
                        adaptiveSamplingOverrides.priorityMode ?? manager.adaptivePriorityMode, false, false, recordEditorRun, null);
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

    private static void RunExperimentFromCommandLine(string experimentPath, string outputArgument)
    {
        string outputRoot = string.IsNullOrWhiteSpace(outputArgument) ? GetDefaultOutputRoot() : outputArgument;
        string label = null;
        try
        {
            CaptureExperiment experiment = JsonUtility.FromJson<CaptureExperiment>(File.ReadAllText(experimentPath));
            ValidateExperiment(experiment, experimentPath);
            label = GetAvailableCaptureLabel(outputRoot, experiment.label);
            string experimentRoot = Path.Combine(outputRoot, SanitizePathSegment(label));
            Directory.CreateDirectory(experimentRoot);

            foreach (string scenePath in experiment.scenes)
            {
                string trimmedPath = scenePath.Trim();
                EditorSceneManager.OpenScene(trimmedPath);
                var manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                if (manager == null || manager.renderTextureCamera == null || manager.shader == null)
                {
                    throw new InvalidOperationException($"Experiment requires a configured GameManager, render camera, and compute shader: {trimmedPath}");
                }
                foreach (var setup in UnityEngine.Object.FindObjectsByType<MaterialBallRoomRuntimeSetup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    setup.PrepareForRendering();
                }

                string sceneName = Path.GetFileNameWithoutExtension(trimmedPath);
                string referencePath = FindLongestReference(trimmedPath, experiment.referenceRoot,
                    experiment.width, experiment.height, out ReferenceMetadata reference);
                if (referencePath == null)
                {
                    throw new InvalidOperationException(
                        $"Experiment reference is missing for '{trimmedPath}' at {experiment.width}x{experiment.height}. " +
                        "Generate it separately with -rayTracingGenerateReference; experiments never generate references automatically.");
                }

                string sceneRoot = Path.Combine(experimentRoot, SanitizePathSegment(sceneName));
                Directory.CreateDirectory(sceneRoot);
                if (experiment.temporalRisWarmupFrames != null && experiment.temporalRisWarmupFrames.Length > 0)
                {
                    RunTemporalRisOneFrameTrials(manager, experiment, sceneRoot, referencePath);
                    File.WriteAllText(Path.Combine(sceneRoot, "experiment.json"), JsonUtility.ToJson(experiment, true));
                    WriteCaptureLog(sceneRoot, label);
                    continue;
                }
                var results = new List<ExperimentVariantResult>();
                for (int index = 0; index < experiment.variants.Length; index++)
                {
                    ExperimentVariant variant = experiment.variants[index];
                    if (index > 0) CoolDownBetweenTimedCaptures(experiment.durationSeconds, experiment.cooldownSeconds);
                    ApplyExperimentOverrides(manager, variant.overrides);
                    string variantName = SanitizePathSegment(variant.name);
                    CaptureResult result = CaptureVariant(manager, sceneName, sceneRoot, variantName,
                        experiment.samples, experiment.width, experiment.height, experiment.durationSeconds,
                        DebugRenderMode.FinalColor, manager.enableAdaptiveSampling, manager.adaptivePriorityMode,
                        true, manager.enableAdaptiveSampling, false, referencePath, true);
                    WriteReferenceMetrics(sceneRoot, variantName, result, referencePath, reference);
                    if (manager.enableAdaptiveSampling)
                        WriteGroupDiagnostics(sceneRoot, variantName, result, manager.adaptivePriorityMode, referencePath);
                    results.Add(new ExperimentVariantResult(variantName, result));
                }

                WriteExperimentComparison(sceneRoot, results, referencePath, reference);
                File.WriteAllText(Path.Combine(sceneRoot, "experiment.json"), JsonUtility.ToJson(experiment, true));
                for (int first = 0; first < results.Count; first++)
                {
                    for (int second = first + 1; second < results.Count; second++)
                    {
                        GenerateDifferenceImage(results[first].result.imagePath, results[second].result.imagePath,
                            Path.Combine(sceneRoot, $"{results[first].name}_vs_{results[second].name}_difference.png"));
                    }
                    GenerateDifferenceImage(results[first].result.imagePath, referencePath,
                        Path.Combine(sceneRoot, $"{results[first].name}_vs_reference_difference.png"));
                }
                WriteCaptureLog(sceneRoot, label);
            }
            Debug.Log($"Ray tracing experiment complete: '{Path.Combine(outputRoot, SanitizePathSegment(label))}'.");
            ExitBatchMode(0);
        }
        catch (Exception exception)
        {
            string failureRoot = label == null ? outputRoot : Path.Combine(outputRoot, SanitizePathSegment(label));
            string failureLogPath = TryWriteCaptureLog(failureRoot, label ?? Path.GetFileNameWithoutExtension(experimentPath));
            ReportCommandLineError("Ray tracing experiment failed. " +
                (failureLogPath == null ? $"Unity console log: '{Application.consoleLogPath}'." :
                    $"Unity console log: '{Application.consoleLogPath}'. Experiment log: '{failureLogPath}'."), exception);
            ReleaseCaptureTarget(null);
            ExitBatchMode(1);
        }
    }

    private readonly struct ExperimentVariantResult
    {
        public readonly string name;
        public readonly CaptureResult result;
        public ExperimentVariantResult(string name, CaptureResult result)
        {
            this.name = name;
            this.result = result;
        }
    }

    private static void ValidateExperiment(CaptureExperiment experiment, string path)
    {
        if (experiment == null || experiment.schemaVersion != 1)
            throw new InvalidOperationException($"Experiment '{path}' must use schemaVersion 1.");
        if (string.IsNullOrWhiteSpace(experiment.label) || experiment.scenes == null || experiment.scenes.Length == 0
            || experiment.variants == null || experiment.variants.Length < 2)
            throw new InvalidOperationException("An experiment requires a label, at least one scene, and at least two variants.");
        if (experiment.width <= 0 || experiment.height <= 0 || experiment.samples < 0
            || experiment.durationSeconds < 0.0 || experiment.cooldownSeconds < 0.0
            || (experiment.samples <= 0 && experiment.durationSeconds <= 0.0))
            throw new InvalidOperationException("Experiment dimensions and cooldown must be valid, with either a positive sample count or duration.");
        if (experiment.temporalRisWarmupFrames != null && experiment.temporalRisWarmupFrames.Length > 0)
        {
            if (experiment.temporalRisTrialsPerWarmup <= 0)
                throw new InvalidOperationException("Temporal RIS one-frame trials require a positive temporalRisTrialsPerWarmup.");
            foreach (int warmupFrames in experiment.temporalRisWarmupFrames)
            {
                if (warmupFrames < 0)
                    throw new InvalidOperationException("Temporal RIS warm-up frame counts must be non-negative.");
            }
        }
        if (string.IsNullOrWhiteSpace(experiment.referenceRoot)) experiment.referenceRoot = DefaultReferenceRoot;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExperimentVariant variant in experiment.variants)
        {
            if (variant == null || string.IsNullOrWhiteSpace(variant.name) || !names.Add(variant.name))
                throw new InvalidOperationException("Experiment variant names must be non-empty and unique.");
        }
    }

    private static void ApplyExperimentOverrides(GameManager manager, ExperimentOverride[] overrides)
    {
        if (overrides == null) return;
        foreach (ExperimentOverride item in overrides)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.path))
                throw new InvalidOperationException("Experiment overrides require a property path.");
            object target = manager;
            string[] segments = item.path.Split('.');
            for (int index = 0; index < segments.Length - 1; index++)
            {
                FieldInfo field = target.GetType().GetField(segments[index], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo property = target.GetType().GetProperty(segments[index], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                target = field != null ? field.GetValue(target) : property?.GetValue(target);
                if (target == null) throw new InvalidOperationException($"Experiment override path '{item.path}' could not be resolved.");
            }
            string memberName = segments[segments.Length - 1];
            FieldInfo targetField = target.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo targetProperty = target.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type valueType = targetField != null ? targetField.FieldType : targetProperty?.PropertyType;
            if (valueType == null || (targetProperty != null && !targetProperty.CanWrite))
                throw new InvalidOperationException($"Experiment override path '{item.path}' is not writable.");
            object value;
            if (valueType == typeof(bool) && bool.TryParse(item.value, out bool boolValue)) value = boolValue;
            else if (valueType == typeof(int) && int.TryParse(item.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue)) value = intValue;
            else if (valueType == typeof(float) && float.TryParse(item.value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue)) value = floatValue;
            else if (valueType.IsEnum && Enum.TryParse(valueType, item.value, true, out object enumValue)) value = enumValue;
            else throw new InvalidOperationException($"Experiment override '{item.path}' has invalid value '{item.value}' for {valueType.Name}.");
            if (targetField != null) targetField.SetValue(target, value); else targetProperty.SetValue(target, value);
        }
    }

    private static void WriteExperimentComparison(string outputRoot, List<ExperimentVariantResult> results,
        string referencePath, ReferenceMetadata reference)
    {
        var lines = new List<string> { "variant,measured_frames,total_render_ms,average_render_ms,average_fps,retired_paths,rgb_mae,rgb_rmse,rgb_psnr_db,luminance_mae,luminance_rmse,luminance_relative_absolute_error,luminance_fraction_above_0_01" };
        foreach (ExperimentVariantResult item in results)
        {
            double average = item.result.totalMilliseconds / Math.Max(1, item.result.measuredFrames);
            VariantComparisonMetrics metrics = CalculateReferenceMetrics(item.result.imagePath, referencePath);
            lines.Add(string.Join(",", item.name, item.result.measuredFrames.ToString(CultureInfo.InvariantCulture),
                item.result.totalMilliseconds.ToString("R", CultureInfo.InvariantCulture), average.ToString("R", CultureInfo.InvariantCulture),
                (average > 0.0 ? 1000.0 / average : 0.0).ToString("R", CultureInfo.InvariantCulture), item.result.retiredPaths,
                CsvNumber(metrics.rgbMeanAbsoluteError), CsvNumber(metrics.rgbRootMeanSquaredError), CsvNumber(metrics.rgbPsnrDb),
                CsvNumber(metrics.luminanceMeanAbsoluteError), CsvNumber(metrics.luminanceRootMeanSquaredError),
                CsvNumber(metrics.luminanceMeanRelativeAbsoluteError), CsvNumber(metrics.luminanceFractionAbove0_01)));
        }
        File.WriteAllLines(Path.Combine(outputRoot, "variant_comparison.csv"), lines);
    }

    private static void RunTemporalRisOneFrameTrials(GameManager manager, CaptureExperiment experiment,
        string outputRoot, string referencePath)
    {
        var results = new List<TemporalRisTrialResult>();
        try
        {
            InitializeBatchRenderer(manager, experiment.width, experiment.height);
            _captureTarget = new RenderTexture(experiment.width, experiment.height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Temporal RIS One-Frame Trials",
                enableRandomWrite = true
            };
            _captureTarget.Create();
            _captureSource = new RenderTexture(experiment.width, experiment.height, 0, RenderTextureFormat.ARGB32);
            _captureSource.Create();
            manager.renderTextureCamera.targetTexture = _captureTarget;
            manager.randomNoise = false;
            manager.enableFrameAccumulation = true;
            manager.enableAdaptiveSampling = false;
            manager.TemporalDenoising.enabled = false;
            manager.debugRenderMode = DebugRenderMode.FinalColor;
            manager.numberOfPasses = 1;
            manager._singleFrame = true;

            // Compile/defer the production variant before counting trial warm-up frames.
            ResetAccumulation(manager);
            for (int warmup = 0; warmup < 4 && manager.AccumulatedFrameCount == 0; warmup++)
            {
                manager.RenderImage(_captureSource, _captureTarget);
            }
            ResetAccumulation(manager);

            Color[] referencePixels = LoadCachedReferencePixels(referencePath);
            foreach (int warmupFrames in experiment.temporalRisWarmupFrames)
            {
                for (int trial = 0; trial < experiment.temporalRisTrialsPerWarmup; trial++)
                {
                    int seed = experiment.temporalRisFirstSeed + trial;
                    foreach (ExperimentVariant variant in experiment.variants)
                    {
                        ApplyExperimentOverrides(manager, variant.overrides);
                        manager.CaptureRandomSeed = seed;
                        ResetAccumulation(manager);
                        manager.ResetCaptureSampleSequence();
                        manager.ClearTemporalRisDiagnostics();
                        for (int frame = 0; frame < warmupFrames; frame++)
                        {
                            manager.RenderImage(_captureSource, _captureTarget);
                            SynchronizeDurationCaptureGpu();
                        }

                        manager.enableFrameAccumulation = false;
                        if (manager.Lighting.TemporalRisEnabled)
                        {
                            manager.PreserveTemporalRisHistoryForNextNonAccumulatedFrame();
                        }
                        var stopwatch = Stopwatch.StartNew();
                        manager.RenderImage(_captureSource, _captureTarget);
                        SynchronizeDurationCaptureGpu();
                        stopwatch.Stop();
                        Color[] pixels = manager.ReadCurrentFinalColorPixels();
                        VariantComparisonMetrics metrics = CalculateReferenceMetrics(pixels, referencePixels, true);
                        results.Add(new TemporalRisTrialResult(warmupFrames, trial + 1, seed, variant.name,
                            stopwatch.Elapsed.TotalMilliseconds, metrics.rgbRootMeanSquaredError, MeanLuminance(pixels)));
                        manager.enableFrameAccumulation = true;
                    }
                }
            }
        }
        finally
        {
            manager.CaptureRandomSeed = 0;
            manager.enableFrameAccumulation = true;
            ResetAccumulation(manager);
            ReleaseCaptureTarget(manager.renderTextureCamera);
        }

        WriteTemporalRisOneFrameTrialReports(outputRoot, results);
    }

    private static float MeanLuminance(Color[] pixels)
    {
        double total = 0.0;
        foreach (Color pixel in pixels)
        {
            total += 0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b;
        }
        return (float)(total / Math.Max(1, pixels.Length));
    }

    private static void WriteTemporalRisOneFrameTrialReports(string outputRoot, List<TemporalRisTrialResult> results)
    {
        var lines = new List<string>
        {
            "warmup_frames,trial,seed,variant,measured_render_ms,rgb_rmse,mean_linear_luminance"
        };
        foreach (TemporalRisTrialResult result in results)
        {
            lines.Add(string.Join(",", result.warmupFrames.ToString(CultureInfo.InvariantCulture),
                result.trial.ToString(CultureInfo.InvariantCulture), result.seed.ToString(CultureInfo.InvariantCulture),
                result.variant, result.milliseconds.ToString("R", CultureInfo.InvariantCulture),
                result.rgbRmse.ToString("R", CultureInfo.InvariantCulture),
                result.meanLuminance.ToString("R", CultureInfo.InvariantCulture)));
        }
        File.WriteAllLines(Path.Combine(outputRoot, "temporal_ris_one_frame_trials.csv"), lines);

        var summary = new List<string> { "warmup_frames,variant,trials,mean_render_ms,render_ms_variance,mean_rgb_rmse,rgb_rmse_variance,mean_linear_luminance,luminance_variance" };
        var warmupFramesSet = new HashSet<int>();
        var variants = new HashSet<string>(StringComparer.Ordinal);
        foreach (TemporalRisTrialResult result in results)
        {
            warmupFramesSet.Add(result.warmupFrames);
            variants.Add(result.variant);
        }
        foreach (int warmupFrames in warmupFramesSet)
        {
            foreach (string variant in variants)
            {
                List<TemporalRisTrialResult> group = results.FindAll(result => result.warmupFrames == warmupFrames && result.variant == variant);
                var milliseconds = group.ConvertAll(result => result.milliseconds);
                var rmse = group.ConvertAll(result => result.rgbRmse);
                var luminance = group.ConvertAll(result => (double)result.meanLuminance);
                summary.Add(string.Join(",", warmupFrames.ToString(CultureInfo.InvariantCulture), variant,
                    group.Count.ToString(CultureInfo.InvariantCulture), Mean(group.ConvertAll(result => result.milliseconds)).ToString("R", CultureInfo.InvariantCulture),
                    Variance(milliseconds).ToString("R", CultureInfo.InvariantCulture),
                    Mean(rmse).ToString("R", CultureInfo.InvariantCulture),
                    Variance(rmse).ToString("R", CultureInfo.InvariantCulture),
                    Mean(luminance).ToString("R", CultureInfo.InvariantCulture),
                    Variance(luminance).ToString("R", CultureInfo.InvariantCulture)));
            }
        }
        File.WriteAllLines(Path.Combine(outputRoot, "temporal_ris_one_frame_summary.csv"), summary);
    }

    private static double Variance(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double mean = Mean(values);
        double sum = 0.0;
        foreach (double value in values)
        {
            double delta = value - mean;
            sum += delta * delta;
        }
        return sum / (values.Count - 1);
    }

    private static double Mean(List<double> values)
    {
        double sum = 0.0;
        foreach (double value in values) sum += value;
        return values.Count == 0 ? 0.0 : sum / values.Count;
    }

    private static void GenerateReference(GameManager manager, string scenePath, string referenceRoot,
        int width, int height, double durationSeconds, bool refresh, bool requireExisting)
    {
        string directory = GetReferenceDirectory(scenePath, referenceRoot);
        string imagePath = GetReferenceImagePath(scenePath, referenceRoot, width, height, durationSeconds);
        string metadataPath = Path.ChangeExtension(imagePath, ".json");
        if (!refresh && (File.Exists(imagePath) || File.Exists(metadataPath)))
        {
            throw new InvalidOperationException(
                $"Reference for '{scenePath}' at {width}x{height} already exists. Use -rayTracingRefreshReferences to replace it.");
        }
        if (requireExisting)
        {
            throw new InvalidOperationException($"Reference generation cannot use -rayTracingRequireExistingReferences.");
        }

        Debug.Log($"Generating {durationSeconds:0.###}-second reference at {width}x{height} for '{scenePath}'.");
        // A reference must not contain the experimental temporal reservoir estimator it evaluates.
        manager.Lighting.TemporalRisEnabled = false;
        manager.Lighting.SpatialRisEnabled = false;
        manager.InvalidateTemporalRisHistory();
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        CaptureResult result = CaptureVariant(manager, sceneName, directory, Path.GetFileNameWithoutExtension(imagePath),
            0, width, height, durationSeconds, DebugRenderMode.FinalColor, false,
            GameManager.AdaptivePriorityMode.WelfordStandardError, false, false);
        if (result.imagePath != imagePath)
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
            File.Move(result.imagePath, imagePath);
        }

        WriteReferenceMetadata(metadataPath, manager, scenePath, imagePath, width, height, durationSeconds, result.measuredFrames);
        AssetDatabase.Refresh();
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
        string referenceRoot,
        AdaptiveSamplingOverrides adaptiveSamplingOverrides,
        GameManager.AdaptivePriorityMode? adaptiveComparisonType,
        double cooldownSeconds,
        bool adaptiveInstrumentation,
        bool recordEditorRun)
    {
        var comparisonRoot = Path.Combine(outputRoot, SanitizePathSegment(label), sceneName);
        ReferenceMetadata reference = null;
        string referenceImagePath = null;
        if (referenceMetrics)
        {
            referenceImagePath = EnsureReference(manager, scenePath, referenceRoot, refreshReferences, requireExistingReferences,
                out reference);
        }

        ApplyAdaptiveSamplingOverrides(manager, adaptiveSamplingOverrides);

        bool runWelford = !adaptiveComparisonType.HasValue
            || adaptiveComparisonType.Value == GameManager.AdaptivePriorityMode.WelfordStandardError;
        bool runDammertz = !adaptiveComparisonType.HasValue
            || adaptiveComparisonType.Value == GameManager.AdaptivePriorityMode.DammertzSplitEstimator;
        CaptureResult adaptiveOff = default;
        CaptureResult adaptiveWelford = default;
        CaptureResult adaptiveDammertz = default;
        if (runWelford)
        {
            adaptiveWelford = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_welford", samplesPerScene, captureWidth,
                captureHeight, durationSeconds, debugRenderMode, true, GameManager.AdaptivePriorityMode.WelfordStandardError, true,
                adaptiveInstrumentation, recordEditorRun, referenceImagePath);
        }
        if (runDammertz)
        {
            if (runWelford) CoolDownBetweenTimedCaptures(durationSeconds, cooldownSeconds);
            adaptiveDammertz = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_dammertz", samplesPerScene, captureWidth,
                captureHeight, durationSeconds, debugRenderMode, true, GameManager.AdaptivePriorityMode.DammertzSplitEstimator, true,
                adaptiveInstrumentation, recordEditorRun, referenceImagePath);
        }
        if (!skipAdaptiveOff)
        {
            CoolDownBetweenTimedCaptures(durationSeconds, cooldownSeconds);
            adaptiveOff = CaptureVariant(manager, sceneName, comparisonRoot, "adaptive_off", samplesPerScene, captureWidth,
                captureHeight, durationSeconds, debugRenderMode, false, GameManager.AdaptivePriorityMode.WelfordStandardError, true,
                false, recordEditorRun, referenceImagePath);
        }
        if (referenceMetrics)
        {
            if (!skipAdaptiveOff)
            {
                WriteReferenceMetrics(comparisonRoot, "adaptive_off", adaptiveOff, referenceImagePath, reference);
            }
            if (runWelford) WriteReferenceMetrics(comparisonRoot, "adaptive_welford", adaptiveWelford, referenceImagePath, reference);
            if (runDammertz) WriteReferenceMetrics(comparisonRoot, "adaptive_dammertz", adaptiveDammertz, referenceImagePath, reference);
        }
        if (!skipAdaptiveOff)
        {
            if (runWelford) GenerateDifferenceImage(adaptiveOff.imagePath, adaptiveWelford.imagePath,
                Path.Combine(comparisonRoot, "adaptive_off_vs_welford_difference.png"));
            if (runDammertz) GenerateDifferenceImage(adaptiveOff.imagePath, adaptiveDammertz.imagePath,
                Path.Combine(comparisonRoot, "adaptive_off_vs_dammertz_difference.png"));
        }
        if (runWelford && runDammertz) GenerateDifferenceImage(adaptiveWelford.imagePath, adaptiveDammertz.imagePath,
            Path.Combine(comparisonRoot, "adaptive_welford_vs_dammertz_difference.png"));
        if (referenceMetrics)
        {
            if (runWelford) GenerateDifferenceImage(adaptiveWelford.imagePath, referenceImagePath,
                Path.Combine(comparisonRoot, "adaptive_welford_vs_reference_difference.png"));
            if (runDammertz) GenerateDifferenceImage(adaptiveDammertz.imagePath, referenceImagePath,
                Path.Combine(comparisonRoot, "adaptive_dammertz_vs_reference_difference.png"));
            if (!skipAdaptiveOff)
            {
                GenerateDifferenceImage(adaptiveOff.imagePath, referenceImagePath,
                    Path.Combine(comparisonRoot, "adaptive_off_vs_reference_difference.png"));
                if (runWelford) GenerateReferenceComparisonImage(adaptiveOff.imagePath, adaptiveWelford.imagePath, referenceImagePath,
                    Path.Combine(comparisonRoot, "adaptive_welford_red_off_green_vs_reference.png"));
                if (runDammertz) GenerateReferenceComparisonImage(adaptiveOff.imagePath, adaptiveDammertz.imagePath, referenceImagePath,
                    Path.Combine(comparisonRoot, "adaptive_dammertz_red_off_green_vs_reference.png"));
            }
            if (runWelford && runDammertz) WriteGroupDiagnostics(comparisonRoot, adaptiveWelford, adaptiveDammertz, referenceImagePath);
        }
        WriteThreeWayComparisonSummary(comparisonRoot, adaptiveOff, adaptiveWelford, adaptiveDammertz, skipAdaptiveOff, runWelford, runDammertz);
        WriteVariantComparisonCsv(comparisonRoot, adaptiveOff, adaptiveWelford, adaptiveDammertz,
            runWelford, runDammertz,
            skipAdaptiveOff, referenceMetrics ? referenceImagePath : null, reference);
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

    private static void CoolDownBetweenTimedCaptures(double durationSeconds, double cooldownSeconds)
    {
        if (durationSeconds > 0.0 && cooldownSeconds > 0.0)
        {
            Debug.Log($"Cooling down for {cooldownSeconds:0.###} seconds before the next timed capture.");
            Thread.Sleep(TimeSpan.FromSeconds(cooldownSeconds));
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
        GameManager.AdaptivePriorityMode adaptivePriorityMode,
        bool writeTimingReport,
        bool adaptiveInstrumentation,
        bool recordEditorRun = false,
        string referencePath = null,
        bool writeConvergenceMetrics = false)
    {
        manager.enableFrameAccumulation = true;
        manager.enableAdaptiveSampling = adaptiveSampling;
        manager.adaptivePriorityMode = adaptivePriorityMode;
        manager.SetAdaptiveCaptureDiagnostics(adaptiveSampling && adaptiveInstrumentation);
        // Capture both variants with the same fixed path budget: one path per pixel per frame.
        manager.adaptiveSamplingMinSamples = Mathf.Max(1, manager.adaptiveSamplingMinSamples);
        manager.TemporalDenoising.enabled = false;
        manager.debugRenderMode = debugRenderMode;
        manager.numberOfPasses = 1;
        manager._singleFrame = true;

        // Keep candidate-specific diagnostics separate during three-way captures. The rendered
        // candidate image remains in outputRoot, while reports and heatmaps live below this folder.
        string diagnosticsRoot = Path.Combine(outputRoot, label);
        Directory.CreateDirectory(diagnosticsRoot);
        
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
        manager.ClearTemporalRisDiagnostics();

        var measuredFrames = 0;
        string editorRunFolder = recordEditorRun
            ? CreateCommandLineEditorRunFolder(outputRoot, adaptiveSampling)
            : null;
        StreamWriter editorRunWriter = recordEditorRun
            ? CreateCommandLineEditorRunWriter(editorRunFolder, manager, sceneName, referencePath)
            : null;
        StreamWriter convergenceWriter = writeConvergenceMetrics
            ? CreateConvergenceWriter(diagnosticsRoot)
            : null;
        Color[] referencePixels = string.IsNullOrEmpty(referencePath) ? null : LoadCachedReferencePixels(referencePath);
        double editorRunStart = EditorApplication.timeSinceStartup;
        double previousPsnr = 0.0;
        double previousRmse = 0.0;
        bool hasPreviousMetrics = false;
        var adaptiveFrames = adaptiveInstrumentation ? new List<AdaptiveCaptureFrame>() : null;
        string heatmapFolder = adaptiveInstrumentation && writeTimingReport
            ? Path.GetFullPath(Path.Combine(DefaultOutputFolder, "Heatmaps", sceneName, label))
            : null;
        if (heatmapFolder != null) Directory.CreateDirectory(heatmapFolder);
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
            if (editorRunWriter != null)
            {
                WriteCommandLineEditorRunFrame(editorRunWriter, manager, measuredFrames, editorRunStart,
                    referencePixels, ref previousPsnr, ref previousRmse, ref hasPreviousMetrics);
            }
            if (convergenceWriter != null)
            {
                WriteCommandLineEditorRunFrame(convergenceWriter, manager, measuredFrames, editorRunStart,
                    referencePixels, ref previousPsnr, ref previousRmse, ref hasPreviousMetrics);
            }
            if (adaptiveInstrumentation)
            {
                GameManager.AdaptiveFrameTelemetry telemetry = manager.ReadAdaptiveFrameTelemetryForCapture();
                adaptiveFrames.Add(new AdaptiveCaptureFrame(measuredFrames, frameStopwatch.Elapsed.TotalMilliseconds, telemetry));
                if (heatmapFolder != null)
                {
                    GameManager.AdaptiveAllocationFrameData allocation = manager.ReadAdaptiveAllocationForCapture();
                    WriteAdaptiveAllocationHeatmapFrame(heatmapFolder,
                        new AdaptiveAllocationSnapshot(measuredFrames, telemetry.reclassified, allocation));
                }
            }
        }
        stopwatch.Stop();
        SynchronizeDurationCaptureGpu();
        var adaptiveDiagnostics = adaptiveInstrumentation ? manager.ReadAdaptiveDiagnosticsForCapture() : null;
        uint[] temporalRisDiagnostics = manager.ReadTemporalRisDiagnosticsForCapture();
        ulong retiredPaths = adaptiveInstrumentation
            ? SumPathCounts(adaptiveDiagnostics.pathCounts) + SumGuidancePaths(adaptiveFrames)
            : (ulong)captureWidth * (ulong)captureHeight * (ulong)measuredFrames;

        Directory.CreateDirectory(outputRoot);
        string outputPath = Path.Combine(outputRoot, label + ".png");
        manager.ExportCurrentRenderPng(outputPath);
        if (editorRunWriter != null)
        {
            editorRunWriter.Flush();
            editorRunWriter.Dispose();
            manager.ExportCurrentRenderPng(Path.Combine(editorRunFolder, "final_color.png"));
            File.WriteAllText(Path.Combine(editorRunFolder, "run_complete.txt"),
                $"recordedFrames={measuredFrames}\n");
        }
        if (convergenceWriter != null)
        {
            convergenceWriter.Flush();
            convergenceWriter.Dispose();
        }
        if (writeTimingReport)
        {
            WriteTimingReport(diagnosticsRoot, label, sceneName, adaptiveSampling, measuredFrames, durationSeconds,
                captureWidth, captureHeight, stopwatch.Elapsed.TotalMilliseconds, retiredPaths);
            if (adaptiveDiagnostics != null) WriteAdaptiveDiagnostics(diagnosticsRoot, adaptiveDiagnostics, adaptiveFrames);
            WriteRisReuseDiagnostics(diagnosticsRoot, manager, temporalRisDiagnostics);
            if (adaptiveDiagnostics != null)
            {
                WriteAdaptiveFrameTelemetry(diagnosticsRoot, adaptiveFrames);
                WriteAdaptiveAllocationHeatmap(diagnosticsRoot, adaptiveDiagnostics);
            }
        }
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}' ({stopwatch.Elapsed.TotalMilliseconds:0.00} ms).");
        ReleaseCaptureTarget(manager.renderTextureCamera);
        return new CaptureResult(outputPath, measuredFrames, stopwatch.Elapsed.TotalMilliseconds, retiredPaths, adaptiveDiagnostics);
    }

    private static void WriteRisReuseDiagnostics(string outputRoot, GameManager manager, uint[] diagnostics)
    {
        bool temporalEnabled = manager.Lighting.TemporalRisEnabled;
        bool spatialEnabled = manager.Lighting.SpatialRisEnabled;
        if ((!temporalEnabled && !spatialEnabled) || diagnostics.Length < TemporalRisManager.DiagnosticsCount) return;
        ulong eligible = diagnostics[TemporalRisManager.EligibleCount];
        int configuredReuseCandidateCount = spatialEnabled
            ? manager.Lighting.SpatialRisNeighborCount
            : manager.Lighting.TemporalRisHistoryMCap;
        ulong reuseOpportunities = spatialEnabled
            ? eligible * (ulong)configuredReuseCandidateCount
            : eligible;
        ulong accepted = diagnostics[TemporalRisManager.HistoryAcceptedCount];
        ulong merged = diagnostics[TemporalRisManager.HistoryMergedCount];
        string report = "{\n" +
            $"  \"reuseMode\": \"{(spatialEnabled ? "spatial" : "temporal")}\",\n" +
            $"  \"configuredLocalCandidateCount\": {manager.Lighting.InitialRisCandidateCount},\n" +
            $"  \"configuredReuseCandidateCount\": {configuredReuseCandidateCount},\n" +
            $"  \"eligiblePrimaryHits\": {eligible},\n" +
            $"  \"reuseOpportunities\": {reuseOpportunities},\n" +
            $"  \"reuseAccepted\": {accepted},\n" +
            $"  \"reuseAcceptanceRate\": {(reuseOpportunities > 0 ? (double)accepted / reuseOpportunities : 0.0):R},\n" +
            $"  \"reuseRejectedOutOfBounds\": {diagnostics[TemporalRisManager.HistoryOutOfBoundsCount]},\n" +
            $"  \"reuseRejectedFeatures\": {diagnostics[TemporalRisManager.HistoryFeatureRejectedCount]},\n" +
            $"  \"reuseRejectedReservoir\": {diagnostics[TemporalRisManager.HistoryReservoirRejectedCount]},\n" +
            $"  \"reuseRejectedZeroTarget\": {diagnostics[TemporalRisManager.HistoryZeroTargetCount]},\n" +
            $"  \"reuseMerged\": {merged},\n" +
            $"  \"reuseMergeRate\": {(reuseOpportunities > 0 ? (double)merged / reuseOpportunities : 0.0):R},\n" +
            $"  \"reuseSelected\": {diagnostics[TemporalRisManager.HistorySelectedCount]},\n" +
            $"  \"reuseSelectionRate\": {(merged > 0 ? (double)diagnostics[TemporalRisManager.HistorySelectedCount] / merged : 0.0):R},\n" +
            $"  \"meanRetainedReuseM\": {(merged > 0 ? (double)diagnostics[TemporalRisManager.RetainedMTotal] / merged : 0.0):R},\n" +
            $"  \"meanEffectiveReservoirM\": {(eligible > 0 ? (double)diagnostics[TemporalRisManager.EffectiveMTotal] / eligible : 0.0):R}\n" +
            "}\n";
        File.WriteAllText(Path.Combine(outputRoot, spatialEnabled ? "spatial_ris_diagnostics.json" : "temporal_ris_diagnostics.json"), report);
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
        if (requestedPaths != assignedPaths || assignedPaths != fullResolutionRetiredPaths
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

    private static string CreateCommandLineEditorRunFolder(string outputRoot, bool adaptiveSampling)
    {
        string prefix = adaptiveSampling ? "adaptive_run" : "run";
        string folder = Path.Combine(outputRoot, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static StreamWriter CreateCommandLineEditorRunWriter(string folder, GameManager manager,
        string sceneName, string referencePath)
    {
        string settings =
            $"scene={sceneName}\n" +
            $"enableAdaptiveSampling={manager.enableAdaptiveSampling}\n" +
            $"enableFrameAccumulation={manager.enableFrameAccumulation}\n" +
            $"displayWidth={manager.DisplayTextureSize.x}\n" +
            $"displayHeight={manager.DisplayTextureSize.y}\n" +
            $"referenceImage={referencePath ?? string.Empty}\n";
        File.WriteAllText(Path.Combine(folder, "settings.txt"), settings);
        var writer = new StreamWriter(Path.Combine(folder, "metrics.csv"), false);
        writer.WriteLine("frame,elapsed_seconds,rgb_psnr_db,rgb_rmse,psnr_db_improvement,rmse_improvement");
        writer.Flush();
        return writer;
    }

    private static StreamWriter CreateConvergenceWriter(string folder)
    {
        Directory.CreateDirectory(folder);
        var writer = new StreamWriter(Path.Combine(folder, "metrics.csv"), false);
        writer.WriteLine("frame,elapsed_seconds,rgb_psnr_db,rgb_rmse,psnr_db_improvement,rmse_improvement");
        writer.Flush();
        return writer;
    }

    private static void WriteCommandLineEditorRunFrame(StreamWriter writer, GameManager manager, int frame,
        double startTime, Color[] referencePixels, ref double previousPsnr, ref double previousRmse,
        ref bool hasPreviousMetrics)
    {
        bool available = referencePixels != null;
        double psnr = double.NaN;
        double rmse = double.NaN;
        if (available)
        {
            VariantComparisonMetrics metrics = CalculateReferenceMetrics(manager.ReadCurrentFinalColorPixels(), referencePixels, true);
            psnr = metrics.rgbPsnrDb;
            rmse = metrics.rgbRootMeanSquaredError;
        }
        string psnrValue = double.IsNaN(psnr) ? "NaN" : (double.IsPositiveInfinity(psnr) ? "Infinity" : psnr.ToString("R", CultureInfo.InvariantCulture));
        string rmseValue = double.IsNaN(rmse) ? "NaN" : rmse.ToString("R", CultureInfo.InvariantCulture);
        string psnrImprovement = "NaN";
        string rmseImprovement = "NaN";
        if (!double.IsNaN(psnr) && hasPreviousMetrics)
        {
            psnrImprovement = CalculatePsnrImprovement(psnr, previousPsnr);
            rmseImprovement = (previousRmse - rmse).ToString("R", CultureInfo.InvariantCulture);
        }
        writer.WriteLine(string.Join(",", frame,
            (EditorApplication.timeSinceStartup - startTime).ToString("R", CultureInfo.InvariantCulture),
            psnrValue, rmseValue, psnrImprovement, rmseImprovement));
        if (!double.IsNaN(psnr))
        {
            previousPsnr = psnr;
            previousRmse = rmse;
            hasPreviousMetrics = true;
        }
        writer.Flush();
    }

    private static string CalculatePsnrImprovement(double current, double previous)
    {
        if (double.IsPositiveInfinity(current) && double.IsPositiveInfinity(previous)) return "0";
        if (double.IsPositiveInfinity(current)) return "Infinity";
        if (double.IsPositiveInfinity(previous)) return "-Infinity";
        return (current - previous).ToString("R", CultureInfo.InvariantCulture);
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
            "frame,accumulated_frame,classified,frame_ms,scheduler_fence_ms,trace_fence_ms,resolve_fence_ms,active_work_items,full_resolution_paths,retired_paths,guidance_paths,total_retired_paths"
        };
        foreach (AdaptiveCaptureFrame frame in frames)
        {
            lines.Add($"{frame.frame},{frame.telemetry.accumulatedFrameCount},{frame.telemetry.reclassified}," +
                $"{frame.milliseconds:R},{frame.telemetry.schedulerMilliseconds:R},{frame.telemetry.traceMilliseconds:R},{frame.telemetry.resolveMilliseconds:R}," +
                $"{frame.telemetry.activeWorkItems},{frame.telemetry.assignedPaths},{frame.telemetry.retiredPaths},{frame.telemetry.guidancePaths},{frame.telemetry.retiredPaths + frame.telemetry.guidancePaths}");
        }
        File.WriteAllLines(Path.Combine(outputRoot, "adaptive_frame_telemetry.csv"), lines);

        double firstHalf = 0.0;
        double secondHalf = 0.0;
        int split = Math.Max(1, frames.Count / 2);
        int reclassificationFrames = 0;
        double reclassificationMilliseconds = 0.0;
        double reuseMilliseconds = 0.0;
        double schedulerMilliseconds = 0.0;
        double traceMilliseconds = 0.0;
        double resolveMilliseconds = 0.0;
        int reuseFrames = 0;
        for (int index = 0; index < frames.Count; index++)
        {
            AdaptiveCaptureFrame frame = frames[index];
            if (index < split) firstHalf += frame.milliseconds;
            else secondHalf += frame.milliseconds;
            schedulerMilliseconds += frame.telemetry.schedulerMilliseconds;
            traceMilliseconds += frame.telemetry.traceMilliseconds;
            resolveMilliseconds += frame.telemetry.resolveMilliseconds;
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
            "Phase fence timing (capture-only; includes GPU synchronization/readback overhead):\n" +
            $"Scheduler average: {schedulerMilliseconds / frames.Count:0.000} ms\n" +
            $"Root trace average: {traceMilliseconds / frames.Count:0.000} ms\n" +
            $"Resolve average: {resolveMilliseconds / frames.Count:0.000} ms\n" +
            "See adaptive_frame_telemetry.csv for the synchronized per-frame sequence.\n";
        File.WriteAllText(Path.Combine(outputRoot, "adaptive_frame_telemetry.txt"), summary);
    }

    private static void WriteAdaptiveAllocationHeatmap(string outputRoot, GameManager.AdaptiveDiagnosticsData diagnostics)
    {
        int width = diagnostics.width;
        int height = diagnostics.height;
        bool finalScheduleUniform = diagnostics.workItemPixels.Length == width * height
            && diagnostics.workItemPathCounts.Length == width * height
            && diagnostics.workItemPathCounts.Length > 0;
        uint uniformPaths = finalScheduleUniform ? diagnostics.workItemPathCounts[0] : 0u;
        if (uniformPaths == 0u) finalScheduleUniform = false;
        for (int index = 1; finalScheduleUniform && index < diagnostics.workItemPathCounts.Length; index++)
            finalScheduleUniform = diagnostics.workItemPathCounts[index] == uniformPaths;
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
                // A uniform final schedule can retain harmless bootstrap-cohort count offsets.
                // Show its actual current state instead of presenting those historical offsets as allocation bias.
                pixels[x + y * width] = finalScheduleUniform ? Color.green : HeatmapColor(paths, quantile);
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
            (finalScheduleUniform
                ? $"Final schedule is uniform ({uniformPaths} path per pixel); green suppresses historical bootstrap-cohort count offsets.\n"
                : "Final schedule is non-uniform; colors show cumulative allocation differences.\n") +
            "Bands: >80th percentile red, >60th yellow, >40th cyan, >20th cyan-blue, >10th blue, >5th dark-blue, otherwise navy.\n");
    }

    private static void WriteAdaptiveAllocationHeatmapFrame(string outputRoot, AdaptiveAllocationSnapshot snapshot)
    {
        GameManager.AdaptiveAllocationFrameData allocation = snapshot.allocation;
        uint maxPaths = 0u;
        var nonZeroPaths = new List<uint>();
        foreach (uint paths in allocation.pixels)
        {
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
            quantileByPathCount[nonZeroPaths[start]] = (start + (end - start) * 0.5f) / Math.Max(1, nonZeroPaths.Count);
            start = end;
        }

        int width = Mathf.CeilToInt(allocation.width / (float)AdaptiveAllocationBlockSize);
        int height = Mathf.CeilToInt(allocation.height / (float)AdaptiveAllocationBlockSize);
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
        var colors = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint paths = allocation.pixels[x * AdaptiveAllocationBlockSize
                    + y * AdaptiveAllocationBlockSize * allocation.width];
                float quantile = paths == 0u ? 0.0f : quantileByPathCount[paths];
                colors[x + y * width] = HeatmapColor(paths, quantile);
            }
        }
        texture.SetPixels(colors);
        texture.Apply(false, false);
        string imagePath = Path.Combine(outputRoot, $"frame_{snapshot.frame:000000}.png");
        File.WriteAllBytes(imagePath, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        File.WriteAllText(Path.Combine(outputRoot, $"frame_{snapshot.frame:000000}.txt"),
            $"Adaptive allocation heatmap\nFrame: {snapshot.frame}\n" +
            $"Reclassified: {snapshot.reclassified}\n" +
            $"Active work items: {allocation.activeWorkItems}\n" +
            $"Assigned paths: {allocation.assignedPaths}\n" +
            $"Maximum paths per pixel: {maxPaths}\n" +
            RayTracingAdaptiveAllocationWindow.FormatBucketGroupCounts(allocation.bucketGroupCounts) +
            "This 1/8-resolution image represents 8x8 allocation blocks and should be displayed at 8x scale.\n" +
            "Black blocks received no full-resolution path this frame. Other colors are ranked among selected blocks.\n");
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
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
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

    private sealed class GroupDiagnostic
    {
        public float welfordScore;
        public float dammertzScore;
        public float assignedPaths;
        public float cumulativeFinePaths;
        public float referenceError;
        public float meanLuminance;
    }

    private readonly struct GroupDiagnosticSummary
    {
        public readonly int servedGroups;
        public readonly int totalGroups;
        public readonly double scoreErrorSpearman;
        public readonly double assignedErrorSpearman;
        public readonly double cumulativeFinePathErrorSpearman;
        public readonly double servedMeanLuminance;
        public readonly double unservedMeanLuminance;
        public readonly double servedMeanReferenceError;
        public readonly double unservedMeanReferenceError;

        public GroupDiagnosticSummary(int servedGroups, int totalGroups, double scoreErrorSpearman,
            double assignedErrorSpearman, double cumulativeFinePathErrorSpearman, double servedMeanLuminance,
            double unservedMeanLuminance, double servedMeanReferenceError, double unservedMeanReferenceError)
        {
            this.servedGroups = servedGroups;
            this.totalGroups = totalGroups;
            this.scoreErrorSpearman = scoreErrorSpearman;
            this.assignedErrorSpearman = assignedErrorSpearman;
            this.cumulativeFinePathErrorSpearman = cumulativeFinePathErrorSpearman;
            this.servedMeanLuminance = servedMeanLuminance;
            this.unservedMeanLuminance = unservedMeanLuminance;
            this.servedMeanReferenceError = servedMeanReferenceError;
            this.unservedMeanReferenceError = unservedMeanReferenceError;
        }
    }

    private static void WriteGroupDiagnostics(string outputRoot, string variantName, CaptureResult result,
        GameManager.AdaptivePriorityMode priorityMode, string referenceImagePath)
    {
        if (result.adaptiveDiagnostics == null) return;
        var reference = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!reference.LoadImage(File.ReadAllBytes(referenceImagePath), false))
                throw new InvalidOperationException($"Could not read reference image '{referenceImagePath}'.");
            GroupDiagnostic[] groups = BuildGroupDiagnostics(result, LoadImagePixels(result.imagePath), reference.GetPixels());
            WriteGroupDiagnosticReport(Path.Combine(outputRoot, $"{variantName}_group_diagnostics.json"), groups,
                priorityMode == GameManager.AdaptivePriorityMode.WelfordStandardError ? "welfordScore" : "dammertzScore");
            WriteGroupDiagnosticCsv(Path.Combine(outputRoot, $"{variantName}_group_diagnostics.csv"), variantName,
                groups, result.adaptiveDiagnostics.width, result.adaptiveDiagnostics.height);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(reference);
        }
    }

    private static void WriteGroupDiagnostics(string outputRoot, CaptureResult welford, CaptureResult dammertz,
        string referenceImagePath)
    {
        if (welford.adaptiveDiagnostics == null || dammertz.adaptiveDiagnostics == null) return;
        var reference = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!reference.LoadImage(File.ReadAllBytes(referenceImagePath), false))
                throw new InvalidOperationException($"Could not read reference image '{referenceImagePath}'.");
            Color[] referencePixels = reference.GetPixels();
            Color[] welfordPixels = LoadImagePixels(welford.imagePath);
            Color[] dammertzPixels = LoadImagePixels(dammertz.imagePath);
            GroupDiagnostic[] welfordGroups = BuildGroupDiagnostics(welford, welfordPixels, referencePixels);
            GroupDiagnostic[] dammertzGroups = BuildGroupDiagnostics(dammertz, dammertzPixels, referencePixels);
            WriteGroupDiagnosticReport(Path.Combine(outputRoot, "adaptive_welford_group_diagnostics.json"), welfordGroups,
                "welfordScore");
            WriteGroupDiagnosticReport(Path.Combine(outputRoot, "adaptive_dammertz_group_diagnostics.json"), dammertzGroups,
                "dammertzScore");
            WriteGroupDiagnosticCsv(Path.Combine(outputRoot, "adaptive_group_diagnostics.csv"), welfordGroups,
                dammertzGroups, welford.adaptiveDiagnostics.width, welford.adaptiveDiagnostics.height);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(reference);
        }
    }

    private static GroupDiagnostic[] BuildGroupDiagnostics(CaptureResult result, Color[] candidatePixels, Color[] referencePixels)
    {
        GameManager.AdaptiveDiagnosticsData data = result.adaptiveDiagnostics;
        int groupWidth = Mathf.CeilToInt(data.width / 8.0f);
        int groupHeight = Mathf.CeilToInt(data.height / 8.0f);
        var groups = new GroupDiagnostic[groupWidth * groupHeight];
        var assigned = new uint[data.width * data.height];
        for (int i = 0; i < data.workItemPixels.Length; i++) assigned[data.workItemPixels[i]] = data.workItemPathCounts[i];
        for (int groupY = 0; groupY < groupHeight; groupY++)
        for (int groupX = 0; groupX < groupWidth; groupX++)
        {
            int groupIndex = groupX + groupY * groupWidth;
            double welford = 0.0, dammertz = 0.0, errorSquared = 0.0, paths = 0.0, cumulativeFinePaths = 0.0, luminance = 0.0;
            int count = 0;
            for (int y = groupY * 8; y < Math.Min(data.height, groupY * 8 + 8); y++)
            for (int x = groupX * 8; x < Math.Min(data.width, groupX * 8 + 8); x++)
            {
                int pixel = x + y * data.width;
                Vector4 state = data.samplingState[pixel];
                Vector4 m2 = data.samplingM2[pixel];
                Vector4 alternating = data.alternatingState[pixel];
                Vector4 accumulation = data.accumulation[pixel];
                if (IsFinite(accumulation.x) && IsFinite(accumulation.y) && IsFinite(accumulation.z))
                    luminance += 0.2126 * accumulation.x + 0.7152 * accumulation.y + 0.0722 * accumulation.z;
                if (IsFinite(state.x) && state.x > 1.0f && IsFinite(m2.x) && IsFinite(m2.y) && IsFinite(m2.z))
                    welford += Math.Sqrt(Math.Max(0.0, (m2.x + m2.y + m2.z) / (state.x * (state.x - 1.0f))));
                if (alternating.w >= 2.0f && IsFinite(accumulation.x) && IsFinite(accumulation.y) && IsFinite(accumulation.z)
                    && IsFinite(alternating.x) && IsFinite(alternating.y) && IsFinite(alternating.z))
                {
                    double dr = accumulation.x - alternating.x, dg = accumulation.y - alternating.y, db = accumulation.z - alternating.z;
                    dammertz += Math.Sqrt((dr * dr + dg * dg + db * db) / 3.0);
                }
                Color candidate = candidatePixels[pixel].linear;
                Color expected = referencePixels[pixel].linear;
                float er = candidate.r - expected.r, eg = candidate.g - expected.g, eb = candidate.b - expected.b;
                errorSquared += (er * er + eg * eg + eb * eb) / 3.0;
                paths += assigned[pixel];
                cumulativeFinePaths += Math.Max(0.0f, state.x);
                count++;
            }
            groups[groupIndex] = new GroupDiagnostic
            {
                welfordScore = (float)(welford / Math.Max(1, count)),
                dammertzScore = (float)(dammertz / Math.Max(1, count)),
                assignedPaths = (float)paths,
                cumulativeFinePaths = (float)(cumulativeFinePaths / Math.Max(1, count)),
                referenceError = (float)Math.Sqrt(errorSquared / Math.Max(1, count)),
                meanLuminance = (float)(luminance / Math.Max(1, count))
            };
        }
        return groups;
    }

    private static Color[] LoadImagePixels(string imagePath)
    {
        // Group diagnostics run only at capture time; image loading stays outside interactive scheduling.
        var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!image.LoadImage(File.ReadAllBytes(imagePath), false))
                throw new InvalidOperationException($"Could not read candidate image '{imagePath}'.");
            return image.GetPixels();
        }
        finally { UnityEngine.Object.DestroyImmediate(image); }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void WriteGroupDiagnosticReport(string path, GroupDiagnostic[] groups, string scoreName)
    {
        var score = new float[groups.Length];
        var error = new float[groups.Length];
        var paths = new float[groups.Length];
        var cumulativeFinePaths = new float[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            score[i] = scoreName == "welfordScore" ? groups[i].welfordScore : groups[i].dammertzScore;
            error[i] = groups[i].referenceError;
            paths[i] = groups[i].assignedPaths;
            cumulativeFinePaths[i] = groups[i].cumulativeFinePaths;
        }
        GroupDiagnosticSummary summary = SummarizeGroupDiagnostics(groups, score, error, paths, cumulativeFinePaths);
        Array.Sort(score); Array.Sort(error); Array.Sort(paths);
        File.WriteAllText(path, "{\n" +
            $"  \"score\": \"{scoreName}\",\n" +
            $"  \"scoreP50\": {Percentile(score, 0.50f):R},\n" +
            $"  \"scoreP95\": {Percentile(score, 0.95f):R},\n" +
            $"  \"errorP50\": {Percentile(error, 0.50f):R},\n" +
            $"  \"errorP95\": {Percentile(error, 0.95f):R},\n" +
            $"  \"assignedPathsP50\": {Percentile(paths, 0.50f):R},\n" +
            $"  \"assignedPathsP95\": {Percentile(paths, 0.95f):R},\n" +
            $"  \"servedGroups\": {summary.servedGroups},\n" +
            $"  \"totalGroups\": {summary.totalGroups},\n" +
            $"  \"servedGroupFraction\": {(summary.totalGroups > 0 ? (double)summary.servedGroups / summary.totalGroups : 0.0):R},\n" +
            $"  \"scoreErrorSpearman\": {summary.scoreErrorSpearman:R},\n" +
            $"  \"assignedPathErrorSpearman\": {summary.assignedErrorSpearman:R},\n" +
            $"  \"cumulativeFinePathErrorSpearman\": {summary.cumulativeFinePathErrorSpearman:R},\n" +
            $"  \"servedMeanLuminance\": {summary.servedMeanLuminance:R},\n" +
            $"  \"unservedMeanLuminance\": {summary.unservedMeanLuminance:R},\n" +
            $"  \"servedMeanReferenceError\": {summary.servedMeanReferenceError:R},\n" +
            $"  \"unservedMeanReferenceError\": {summary.unservedMeanReferenceError:R}\n" + "}\n");
    }

    private static GroupDiagnosticSummary SummarizeGroupDiagnostics(GroupDiagnostic[] groups, float[] score, float[] error,
        float[] paths, float[] cumulativeFinePaths)
    {
        int servedGroups = 0, unservedGroups = 0;
        double servedLuminance = 0.0, unservedLuminance = 0.0, servedError = 0.0, unservedError = 0.0;
        for (int i = 0; i < groups.Length; i++)
        {
            if (paths[i] > 0.0f)
            {
                servedGroups++;
                servedLuminance += groups[i].meanLuminance;
                servedError += error[i];
            }
            else
            {
                unservedGroups++;
                unservedLuminance += groups[i].meanLuminance;
                unservedError += error[i];
            }
        }
        return new GroupDiagnosticSummary(servedGroups, groups.Length, Spearman(score, error), Spearman(paths, error),
            Spearman(cumulativeFinePaths, error), servedLuminance / Math.Max(1, servedGroups),
            unservedLuminance / Math.Max(1, unservedGroups), servedError / Math.Max(1, servedGroups),
            unservedError / Math.Max(1, unservedGroups));
    }

    private static void WriteGroupDiagnosticCsv(string path, GroupDiagnostic[] welfordGroups,
        GroupDiagnostic[] dammertzGroups, int width, int height)
    {
        if (welfordGroups.Length != dammertzGroups.Length)
            throw new InvalidOperationException("Adaptive group diagnostics have mismatched group counts.");

        int groupWidth = Mathf.CeilToInt(width / 8.0f);
        var lines = new List<string>(welfordGroups.Length * 2 + 1)
        {
            "variant,group_x,group_y,valid_pixel_count,welford_score,dammertz_score,current_assigned_paths,cumulative_fine_paths,mean_linear_luminance,reference_rgb_rmse"
        };
        for (int groupIndex = 0; groupIndex < welfordGroups.Length; groupIndex++)
        {
            int groupX = groupIndex % groupWidth;
            int groupY = groupIndex / groupWidth;
            int validPixels = Math.Min(8, width - groupX * 8) * Math.Min(8, height - groupY * 8);
            AppendGroupDiagnosticCsvRow(lines, "adaptive_welford", groupX, groupY, validPixels, welfordGroups[groupIndex]);
            AppendGroupDiagnosticCsvRow(lines, "adaptive_dammertz", groupX, groupY, validPixels, dammertzGroups[groupIndex]);
        }
        File.WriteAllLines(path, lines);
    }

    private static void WriteGroupDiagnosticCsv(string path, string variantName, GroupDiagnostic[] groups, int width, int height)
    {
        int groupWidth = Mathf.CeilToInt(width / 8.0f);
        var lines = new List<string>(groups.Length + 1)
        {
            "variant,group_x,group_y,valid_pixel_count,welford_score,dammertz_score,current_assigned_paths,cumulative_fine_paths,mean_linear_luminance,reference_rgb_rmse"
        };
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            int groupX = groupIndex % groupWidth;
            int groupY = groupIndex / groupWidth;
            int validPixels = Math.Min(8, width - groupX * 8) * Math.Min(8, height - groupY * 8);
            AppendGroupDiagnosticCsvRow(lines, variantName, groupX, groupY, validPixels, groups[groupIndex]);
        }
        File.WriteAllLines(path, lines);
    }

    private static void AppendGroupDiagnosticCsvRow(List<string> lines, string variant, int groupX, int groupY,
        int validPixels, GroupDiagnostic group)
    {
        lines.Add(string.Join(",", new[]
        {
            variant,
            groupX.ToString(CultureInfo.InvariantCulture),
            groupY.ToString(CultureInfo.InvariantCulture),
            validPixels.ToString(CultureInfo.InvariantCulture),
            group.welfordScore.ToString("R", CultureInfo.InvariantCulture),
            group.dammertzScore.ToString("R", CultureInfo.InvariantCulture),
            group.assignedPaths.ToString("R", CultureInfo.InvariantCulture),
            group.cumulativeFinePaths.ToString("R", CultureInfo.InvariantCulture),
            group.meanLuminance.ToString("R", CultureInfo.InvariantCulture),
            group.referenceError.ToString("R", CultureInfo.InvariantCulture)
        }));
    }

    private static double Spearman(GroupDiagnostic[] groups, string scoreName)
    {
        int count = groups.Length;
        if (count < 2) return 0.0;
        var score = new float[count]; var error = new float[count];
        for (int i = 0; i < count; i++) { score[i] = scoreName == "welfordScore" ? groups[i].welfordScore : groups[i].dammertzScore; error[i] = groups[i].referenceError; }
        float[] scoreRanks = Ranks(score); float[] errorRanks = Ranks(error);
        double meanScore = Mean(scoreRanks), meanError = Mean(errorRanks), numerator = 0.0, scoreVariance = 0.0, errorVariance = 0.0;
        for (int i = 0; i < count; i++) { double a = scoreRanks[i] - meanScore, b = errorRanks[i] - meanError; numerator += a * b; scoreVariance += a * a; errorVariance += b * b; }
        return scoreVariance > 0.0 && errorVariance > 0.0 ? numerator / Math.Sqrt(scoreVariance * errorVariance) : 0.0;
    }

    private static double Spearman(float[] first, float[] second)
    {
        if (first.Length != second.Length || first.Length < 2) return 0.0;
        float[] firstRanks = Ranks(first);
        float[] secondRanks = Ranks(second);
        double firstMean = Mean(firstRanks), secondMean = Mean(secondRanks), numerator = 0.0, firstVariance = 0.0, secondVariance = 0.0;
        for (int i = 0; i < first.Length; i++)
        {
            double a = firstRanks[i] - firstMean, b = secondRanks[i] - secondMean;
            numerator += a * b;
            firstVariance += a * a;
            secondVariance += b * b;
        }
        return firstVariance > 0.0 && secondVariance > 0.0 ? numerator / Math.Sqrt(firstVariance * secondVariance) : 0.0;
    }

    private static float[] Ranks(float[] values)
    {
        var order = new int[values.Length]; for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));
        var ranks = new float[values.Length];
        for (int start = 0; start < order.Length;)
        {
            int end = start + 1; while (end < order.Length && values[order[end]].Equals(values[order[start]])) end++;
            float rank = (start + end - 1) * 0.5f + 1.0f; for (int i = start; i < end; i++) ranks[order[i]] = rank; start = end;
        }
        return ranks;
    }

    private static void WriteThreeWayComparisonSummary(string outputRoot, CaptureResult off, CaptureResult welford,
        CaptureResult dammertz, bool skippedOff, bool runWelford, bool runDammertz)
    {
        File.WriteAllText(Path.Combine(outputRoot, "adaptive_three_way_comparison.json"), "{\n" +
            $"  \"adaptiveOffRetiredPaths\": {(skippedOff ? "null" : off.retiredPaths.ToString())},\n" +
            $"  \"adaptiveWelfordRetiredPaths\": {(runWelford ? welford.retiredPaths.ToString() : "null")},\n" +
            $"  \"adaptiveDammertzRetiredPaths\": {(runDammertz ? dammertz.retiredPaths.ToString() : "null")},\n" +
            $"  \"adaptiveOffMilliseconds\": {(skippedOff ? "null" : off.totalMilliseconds.ToString("R"))},\n" +
            $"  \"adaptiveWelfordMilliseconds\": {(runWelford ? welford.totalMilliseconds.ToString("R", CultureInfo.InvariantCulture) : "null")},\n" +
            $"  \"adaptiveDammertzMilliseconds\": {(runDammertz ? dammertz.totalMilliseconds.ToString("R", CultureInfo.InvariantCulture) : "null")}\n" + "}\n");
    }

    private readonly struct VariantComparisonMetrics
    {
        public readonly bool available;
        public readonly double rgbMeanAbsoluteError;
        public readonly double rgbRootMeanSquaredError;
        public readonly double rgbPsnrDb;
        public readonly double luminanceMeanAbsoluteError;
        public readonly double luminanceRootMeanSquaredError;
        public readonly double luminanceMeanRelativeAbsoluteError;
        public readonly double luminanceFractionAbove0_01;

        public VariantComparisonMetrics(bool available, double rgbMeanAbsoluteError, double rgbRootMeanSquaredError,
            double rgbPsnrDb, double luminanceMeanAbsoluteError, double luminanceRootMeanSquaredError,
            double luminanceMeanRelativeAbsoluteError, double luminanceFractionAbove0_01)
        {
            this.available = available;
            this.rgbMeanAbsoluteError = rgbMeanAbsoluteError;
            this.rgbRootMeanSquaredError = rgbRootMeanSquaredError;
            this.rgbPsnrDb = rgbPsnrDb;
            this.luminanceMeanAbsoluteError = luminanceMeanAbsoluteError;
            this.luminanceRootMeanSquaredError = luminanceRootMeanSquaredError;
            this.luminanceMeanRelativeAbsoluteError = luminanceMeanRelativeAbsoluteError;
            this.luminanceFractionAbove0_01 = luminanceFractionAbove0_01;
        }
    }

    private static void WriteVariantComparisonCsv(string outputRoot, CaptureResult adaptiveOff,
        CaptureResult adaptiveWelford, CaptureResult adaptiveDammertz, bool runWelford, bool runDammertz,
        bool skippedOff, string referencePath,
        ReferenceMetadata reference)
    {
        Directory.CreateDirectory(outputRoot);
        string[] header =
        {
            "variant", "adaptive_priority_mode", "measured_frames", "total_render_ms", "average_render_ms",
            "average_fps", "retired_paths", "reference_metrics_available", "rgb_mean_absolute_error",
            "rgb_rmse", "rgb_psnr_db", "luminance_mean_absolute_error", "luminance_rmse",
            "luminance_relative_absolute_error", "luminance_fraction_above_0_01"
        };
        var lines = new List<string> { string.Join(",", header) };
        if (!skippedOff)
        {
            lines.Add(BuildVariantComparisonCsvRow("adaptive_off", "off", adaptiveOff, referencePath, reference));
        }
        if (runWelford) lines.Add(BuildVariantComparisonCsvRow("adaptive_welford", "WelfordStandardError", adaptiveWelford,
            referencePath, reference));
        if (runDammertz) lines.Add(BuildVariantComparisonCsvRow("adaptive_dammertz", "DammertzSplitEstimator", adaptiveDammertz,
            referencePath, reference));
        File.WriteAllLines(Path.Combine(outputRoot, "adaptive_variant_comparison.csv"), lines);
    }

    private static string BuildVariantComparisonCsvRow(string variant, string priorityMode, CaptureResult result,
        string referencePath, ReferenceMetadata reference)
    {
        double averageMilliseconds = result.totalMilliseconds / Math.Max(1, result.measuredFrames);
        double fps = averageMilliseconds > 0.0 ? 1000.0 / averageMilliseconds : 0.0;
        VariantComparisonMetrics metrics = string.IsNullOrEmpty(referencePath)
            ? default
            : CalculateReferenceMetrics(result.imagePath, referencePath);
        string[] fields =
        {
            variant,
            priorityMode,
            result.measuredFrames.ToString(CultureInfo.InvariantCulture),
            result.totalMilliseconds.ToString("R", CultureInfo.InvariantCulture),
            averageMilliseconds.ToString("R", CultureInfo.InvariantCulture),
            fps.ToString("R", CultureInfo.InvariantCulture),
            result.retiredPaths.ToString(CultureInfo.InvariantCulture),
            metrics.available ? "true" : "false",
            CsvNumber(metrics.available ? metrics.rgbMeanAbsoluteError : double.NaN),
            CsvNumber(metrics.available ? metrics.rgbRootMeanSquaredError : double.NaN),
            CsvNumber(metrics.available ? metrics.rgbPsnrDb : double.NaN),
            CsvNumber(metrics.available ? metrics.luminanceMeanAbsoluteError : double.NaN),
            CsvNumber(metrics.available ? metrics.luminanceRootMeanSquaredError : double.NaN),
            CsvNumber(metrics.available ? metrics.luminanceMeanRelativeAbsoluteError : double.NaN),
            CsvNumber(metrics.available ? metrics.luminanceFractionAbove0_01 : double.NaN)
        };
        return string.Join(",", fields);
    }

    private static string CsvNumber(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? string.Empty
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string EnsureReference(GameManager manager, string scenePath, string referenceRoot, bool refresh,
        bool requireExisting, out ReferenceMetadata metadata)
    {
        string imagePath = FindLongestReference(scenePath, referenceRoot, ReferenceWidth, ReferenceHeight, out metadata);
        if (!refresh && imagePath != null)
        {
            return imagePath;
        }
        if (!refresh && requireExisting)
        {
            throw new InvalidOperationException($"Reference for '{scenePath}' at {ReferenceWidth}x{ReferenceHeight} is missing.");
        }

        Debug.Log($"Generating {ReferenceDurationSeconds:0}-second adaptive-off reference for '{scenePath}'.");
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        imagePath = GetReferenceImagePath(scenePath, referenceRoot, ReferenceWidth, ReferenceHeight, ReferenceDurationSeconds);
        CaptureResult result = CaptureVariant(manager, sceneName, Path.GetDirectoryName(imagePath), Path.GetFileNameWithoutExtension(imagePath),
            0, ReferenceWidth, ReferenceHeight, ReferenceDurationSeconds, DebugRenderMode.FinalColor,
            false, GameManager.AdaptivePriorityMode.WelfordStandardError, false, false);
        if (result.imagePath != imagePath)
        {
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }
            File.Move(result.imagePath, imagePath);
        }
        manager.Lighting.TemporalRisEnabled = false;
        manager.Lighting.SpatialRisEnabled = false;
        manager.InvalidateTemporalRisHistory();
        metadata = WriteReferenceMetadata(Path.ChangeExtension(imagePath, ".json"), manager, scenePath, imagePath, ReferenceWidth, ReferenceHeight,
            ReferenceDurationSeconds, result.measuredFrames);
        AssetDatabase.Refresh();
        return imagePath;
    }

    private static ReferenceMetadata WriteReferenceMetadata(string metadataPath, GameManager manager, string scenePath, string imagePath,
        int width, int height, double durationSeconds, int measuredFrames)
    {
        var settings = new ReferenceRenderSettings
        {
            temporalRisEnabled = manager.Lighting.TemporalRisEnabled,
            initialRisCandidateCount = manager.Lighting.InitialRisCandidateCount,
            temporalRisHistoryMCap = manager.Lighting.TemporalRisHistoryMCap,
            spatialRisEnabled = manager.Lighting.SpatialRisEnabled,
            spatialRisNeighborCount = manager.Lighting.SpatialRisNeighborCount,
            lightSamplingStrategy = manager.Lighting.LightSamplingStrategy.ToString(),
            lightSampleCount = manager.Lighting.LightSampleCount,
            maxLightSamples = manager.maxLightSamples,
            numberOfPasses = manager.numberOfPasses,
            numBounces = manager.numBounces,
            shadowQuality = manager.shadowQuality,
            shadowRandomness = manager.shadowRandomness,
            lightFalloffScale = manager.Lighting.LightFalloffScale,
            environmentLighting = manager.enableEnvironmentLighting,
            environmentLightSampleCount = manager.environmentLightSampleCount,
            fireflyClamp = manager.fireflyClamp,
            frameAccumulation = manager.enableFrameAccumulation,
            randomNoise = manager.randomNoise,
            // CaptureVariant fixes the render RNG by setting randomNoise false.
            captureSeed = 0
        };
        string sceneAbsolutePath = Path.GetFullPath(scenePath);
        string shaderPath = AssetDatabase.GetAssetPath(manager.shader);
        var metadata = new ReferenceMetadata
        {
            scenePath = scenePath,
            imageSha256 = ComputeSha256(imagePath),
            sceneSha256 = File.Exists(sceneAbsolutePath) ? ComputeSha256(sceneAbsolutePath) : null,
            shaderSha256 = !string.IsNullOrEmpty(shaderPath) && File.Exists(shaderPath) ? ComputeSha256(shaderPath) : null,
            sourceRevision = GetSourceRevision(),
            settingsSha256 = ComputeTextSha256(JsonUtility.ToJson(settings)),
            settings = settings,
            width = width,
            height = height,
            durationSeconds = durationSeconds,
            measuredFrames = measuredFrames,
            unityVersion = Application.unityVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            generatedUtc = DateTime.UtcNow.ToString("O")
        };
        File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
        return metadata;
    }

    private static string GetReferenceDirectory(string scenePath, string referenceRoot)
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
        return directory;
    }

    private static string GetReferenceImagePath(string scenePath, string referenceRoot, int width, int height,
        double durationSeconds)
    {
        return Path.Combine(GetReferenceDirectory(scenePath, referenceRoot),
            SanitizePathSegment(Path.GetFileNameWithoutExtension(scenePath)) +
            $"_{width}x{height}_{durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s.png");
    }

    private static string FindLongestReference(string scenePath, string referenceRoot, int width, int height,
        out ReferenceMetadata selectedMetadata)
    {
        selectedMetadata = null;
        string directory = GetReferenceDirectory(scenePath, referenceRoot);
        if (!Directory.Exists(directory)) return null;

        string selectedPath = null;
        foreach (string imagePath in Directory.GetFiles(directory, "*.png"))
        {
            string metadataPath = Path.ChangeExtension(imagePath, ".json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                ReferenceMetadata candidate = ValidateReference(scenePath, imagePath, metadataPath, width, height);
                if (selectedMetadata == null || candidate.durationSeconds > selectedMetadata.durationSeconds)
                {
                    selectedPath = imagePath;
                    selectedMetadata = candidate;
                }
            }
            catch (InvalidOperationException)
            {
                // Ignore references for another resolution or an incomplete capture.
            }
        }
        return selectedPath;
    }

    private static ReferenceMetadata ValidateReference(string scenePath, string imagePath, string metadataPath,
        int expectedWidth, int expectedHeight)
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
        if (metadata == null || metadata.schemaVersion != 2 || metadata.scenePath != scenePath
            || metadata.width != expectedWidth || metadata.height != expectedHeight
            || metadata.durationSeconds <= 0.0 || metadata.imageSha256 != ComputeSha256(imagePath))
        {
            throw new InvalidOperationException($"Reference '{imagePath}' does not match the requested {expectedWidth}x{expectedHeight} reference contract.");
        }
        var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!image.LoadImage(File.ReadAllBytes(imagePath), false) || image.width != expectedWidth || image.height != expectedHeight)
            {
                throw new InvalidOperationException($"Reference image '{imagePath}' is not a readable {expectedWidth}x{expectedHeight} PNG.");
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

    private static string ComputeTextSha256(string text)
    {
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty);
    }

    private static string GetSourceRevision()
    {
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process == null) return "unknown";
            string revision = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrEmpty(revision) ? revision : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void WriteReferenceMetrics(string outputRoot, string label, CaptureResult result, string referencePath,
        ReferenceMetadata reference)
    {
        VariantComparisonMetrics metrics = CalculateReferenceMetrics(result.imagePath, referencePath);
        string report = "{\n" +
            $"  \"referenceImage\": \"{referencePath}\",\n" +
            $"  \"referenceSha256\": \"{reference.imageSha256}\",\n" +
            "  \"comparisonColorSpace\": \"linear-srgb\",\n" +
            $"  \"rgbMeanAbsoluteError\": {metrics.rgbMeanAbsoluteError:R},\n" +
            $"  \"rgbRootMeanSquaredError\": {metrics.rgbRootMeanSquaredError:R},\n" +
            $"  \"rgbPsnrDb\": {(double.IsPositiveInfinity(metrics.rgbPsnrDb) ? "null" : metrics.rgbPsnrDb.ToString("R", CultureInfo.InvariantCulture))},\n" +
            $"  \"luminanceMeanAbsoluteError\": {metrics.luminanceMeanAbsoluteError:R},\n" +
            $"  \"luminanceRootMeanSquaredError\": {metrics.luminanceRootMeanSquaredError:R},\n" +
            $"  \"luminanceMeanRelativeAbsoluteError\": {metrics.luminanceMeanRelativeAbsoluteError:R},\n" +
            $"  \"luminanceFractionAbove0_01\": {metrics.luminanceFractionAbove0_01:R},\n" +
            $"  \"measuredFrames\": {result.measuredFrames},\n" +
            $"  \"cumulativeRetiredPaths\": {result.retiredPaths},\n" +
            $"  \"totalMeasuredRenderMilliseconds\": {result.totalMilliseconds:R}\n" +
            "}\n";
        File.WriteAllText(Path.Combine(outputRoot, label + ".metrics.json"), report);
    }

    private static VariantComparisonMetrics CalculateReferenceMetrics(string candidatePath, string referencePath)
    {
        var candidate = new Texture2D(2, 2, TextureFormat.RGB24, false);
        var referenceImage = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!candidate.LoadImage(File.ReadAllBytes(candidatePath), false)
                || !referenceImage.LoadImage(File.ReadAllBytes(referencePath), false)
                || candidate.width != referenceImage.width || candidate.height != referenceImage.height)
            {
                throw new InvalidOperationException($"Could not compare '{candidatePath}' to reference '{referencePath}'.");
            }
            return CalculateReferenceMetrics(candidate.GetPixels(), referenceImage.GetPixels());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(candidate);
            UnityEngine.Object.DestroyImmediate(referenceImage);
        }
    }

    private static VariantComparisonMetrics CalculateReferenceMetrics(Color[] actual, Color[] expected)
    {
        return CalculateReferenceMetrics(actual, expected, false);
    }

    private static VariantComparisonMetrics CalculateReferenceMetrics(Color[] actual, Color[] expected,
        bool expectedIsLinear)
    {
        return CalculateReferenceMetrics(actual, expected, expectedIsLinear, null);
    }

    private static VariantComparisonMetrics CalculateReferenceMetrics(Color[] actual, Color[] expected,
        bool expectedIsLinear, float[] differences)
    {
        if (actual == null || expected == null || actual.Length != expected.Length || actual.Length == 0)
        {
            throw new InvalidOperationException("Reference comparison requires matching non-empty pixel arrays.");
        }
        if (differences != null && differences.Length != actual.Length)
        {
            throw new InvalidOperationException("Reference difference storage must match the pixel array length.");
        }

        double rgbAbsolute = 0.0, rgbSquared = 0.0, luminanceAbsolute = 0.0, luminanceSquared = 0.0, relative = 0.0;
        int aboveThreshold = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            Color a = actual[i].linear;
            Color b = expectedIsLinear ? expected[i] : expected[i].linear;
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            rgbAbsolute += Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
            rgbSquared += dr * dr + dg * dg + db * db;
            if (differences != null)
            {
                differences[i] = Mathf.Sqrt((dr * dr + dg * dg + db * db) / 3.0f);
            }
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
        return new VariantComparisonMetrics(true, rgbAbsolute / (pixels * 3.0), rgbRmse, psnr,
            luminanceAbsolute / pixels, Math.Sqrt(luminanceSquared / pixels), relative / pixels,
            (double)aboveThreshold / pixels);
    }

    private static string _cachedReferencePath;
    private static long _cachedReferenceWriteTicks;
    private static int _cachedReferenceWidth;
    private static int _cachedReferenceHeight;
    private static Color[] _cachedReferencePixels;

    private static Color[] LoadCachedReferencePixels(string referencePath)
    {
        DateTime writeTime = File.GetLastWriteTimeUtc(referencePath);
        if (_cachedReferencePixels != null
            && string.Equals(_cachedReferencePath, referencePath, StringComparison.Ordinal)
            && _cachedReferenceWriteTicks == writeTime.Ticks)
        {
            return _cachedReferencePixels;
        }

        var referenceImage = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!referenceImage.LoadImage(File.ReadAllBytes(referencePath), false))
            {
                throw new InvalidOperationException($"Could not load reference image '{referencePath}'.");
            }

            Color[] pixels = referenceImage.GetPixels();
            _cachedReferencePixels = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) _cachedReferencePixels[i] = pixels[i].linear;
            _cachedReferencePath = referencePath;
            _cachedReferenceWriteTicks = writeTime.Ticks;
            _cachedReferenceWidth = referenceImage.width;
            _cachedReferenceHeight = referenceImage.height;
            return _cachedReferencePixels;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(referenceImage);
        }
    }

    public static bool TryCompareCurrentRenderToReference(GameManager manager, string scenePath,
        out Texture2D difference, out double rgbPsnrDb, out double rgbRootMeanSquaredError, out string status)
    {
        difference = null;
        rgbPsnrDb = 0.0;
        rgbRootMeanSquaredError = 0.0;
        status = string.Empty;
        if (manager == null || string.IsNullOrEmpty(scenePath))
        {
            status = "No saved scene is available for reference comparison.";
            return false;
        }

        Vector2Int size = manager.DisplayTextureSize;
        string referencePath = FindLongestReference(scenePath, DefaultReferenceRoot, size.x, size.y, out _);
        if (string.IsNullOrEmpty(referencePath))
        {
            status = $"No {size.x}x{size.y} reference image is available for this scene.";
            return false;
        }

        try
        {
            Color[] actual = manager.ReadCurrentFinalColorPixels();
            Color[] reference = LoadCachedReferencePixels(referencePath);
            if (_cachedReferenceWidth != size.x || _cachedReferenceHeight != size.y)
            {
                status = $"Reference image dimensions do not match the current {size.x}x{size.y} render.";
                return false;
            }

            float[] differences = new float[actual.Length];
            VariantComparisonMetrics metrics = CalculateReferenceMetrics(actual, reference, true, differences);

            float[] sortedDifferences = (float[])differences.Clone();
            Array.Sort(sortedDifferences);
            int redIndex = Mathf.Min(sortedDifferences.Length - 1,
                Mathf.FloorToInt((sortedDifferences.Length - 1) * DifferenceHeatmapRedPercentile));
            float redDifference = sortedDifferences[redIndex];
            difference = new Texture2D(size.x, size.y, TextureFormat.RGB24, false, true);
            var outputPixels = new Color[differences.Length];
            for (int i = 0; i < differences.Length; i++)
            {
                float amount = redDifference > 0.0f ? Mathf.Clamp01(differences[i] / redDifference) : 0.0f;
                outputPixels[i] = Color.Lerp(Color.blue, Color.red, amount);
            }
            difference.SetPixels(outputPixels);
            difference.Apply(false, false);
            rgbPsnrDb = metrics.rgbPsnrDb;
            rgbRootMeanSquaredError = metrics.rgbRootMeanSquaredError;
            return true;
        }
        catch (Exception exception)
        {
            status = $"Could not calculate reference metrics: {exception.Message}";
            return false;
        }
    }

    public static bool TryCalculateCurrentReferenceMetrics(GameManager manager, string scenePath,
        out double rgbPsnrDb, out double rgbRootMeanSquaredError, out string status)
    {
        Texture2D difference;
        bool success = TryCompareCurrentRenderToReference(manager, scenePath, out difference,
            out rgbPsnrDb, out rgbRootMeanSquaredError, out status);
        if (difference != null) UnityEngine.Object.DestroyImmediate(difference);
        return success;
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

    public static bool TryWriteCurrentReferenceDifference(GameManager manager, string scenePath, string outputPath,
        out string status)
    {
        status = string.Empty;
        if (manager == null || string.IsNullOrEmpty(scenePath))
        {
            status = "No saved scene is available for reference comparison.";
            return false;
        }

        if (!scenePath.StartsWith("Assets/Scenes/", StringComparison.Ordinal))
        {
            status = "The active scene must be saved below Assets/Scenes to use a reference image.";
            return false;
        }

        Vector2Int size = manager.DisplayTextureSize;
        string referencePath = FindLongestReference(scenePath, DefaultReferenceRoot, size.x, size.y, out _);
        if (string.IsNullOrEmpty(referencePath))
        {
            status = $"No {size.x}x{size.y} reference image is available for this scene.";
            return false;
        }

        string currentPath = outputPath + ".current.png";
        try
        {
            manager.ExportCurrentRenderPng(currentPath);
            GenerateDifferenceImage(currentPath, referencePath, outputPath);
            return true;
        }
        catch (Exception exception)
        {
            status = $"Could not generate reference difference: {exception.Message}";
            return false;
        }
        finally
        {
            if (File.Exists(currentPath)) File.Delete(currentPath);
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
        // Command-line capture opens a scene and renders it in the same editor update, before
        // Unity invokes MonoBehaviour.Start. Run the renderer's normal initialization explicitly
        // so the split compute assets and feature buffers are available to the capture.
        MethodInfo start = typeof(GameManager).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic);
        if (start == null)
        {
            throw new MissingMethodException(typeof(GameManager).FullName, "Start");
        }
        start.Invoke(manager, null);
        FieldInfo startupInitializationPending = typeof(GameManager).GetField("_startupInitializationPending",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (startupInitializationPending == null)
        {
            throw new MissingFieldException(typeof(GameManager).FullName, "_startupInitializationPending");
        }
        startupInitializationPending.SetValue(manager, false);
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
        out double durationSeconds, out double cooldownSeconds)
    {
        samplesPerScene = DefaultSamplesPerScene;
        captureWidth = DefaultCaptureWidth;
        captureHeight = DefaultCaptureHeight;
        durationSeconds = 0.0;
        cooldownSeconds = DefaultCooldownSeconds;

        if (!TryGetPositiveIntegerArgument("-rayTracingSamples", ref samplesPerScene)
            || !TryGetPositiveIntegerArgument("-rayTracingWidth", ref captureWidth)
            || !TryGetPositiveIntegerArgument("-rayTracingHeight", ref captureHeight))
        {
            return false;
        }

        string durationArgument = GetCommandLineArgument("-rayTracingDurationSeconds");
        if (durationArgument != null && (!double.TryParse(durationArgument, NumberStyles.Float,
                CultureInfo.InvariantCulture, out durationSeconds) || durationSeconds <= 0.0))
        {
            ReportCommandLineError($"Scene capture argument -rayTracingDurationSeconds must be positive; received '{durationArgument}'.");
            return false;
        }

        string cooldownArgument = GetCommandLineArgument("-rayTracingCooldownSeconds");
        if (cooldownArgument != null && (!double.TryParse(cooldownArgument, NumberStyles.Float,
                CultureInfo.InvariantCulture, out cooldownSeconds) || cooldownSeconds < 0.0))
        {
            ReportCommandLineError($"Scene capture argument -rayTracingCooldownSeconds must be non-negative; received '{cooldownArgument}'.");
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

    private static bool TryGetAdaptiveSamplingOverrides(out AdaptiveSamplingOverrides overrides)
    {
        overrides = default;
        if (!TryGetOptionalIntegerArgument("-rayTracingAdaptiveSamplingMinSamples", 1, 64, out int? minSamples)
            || !TryGetOptionalFloatArgument("-rayTracingAdaptiveGuidanceChangeThreshold", 0.0f, 1.0f,
                out float? guidanceChangeThreshold)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveGuidanceMaxUpdates", 1, 8,
                out int? guidanceMaxUpdates)
            || !TryGetAdaptivePriorityModeArgument(out GameManager.AdaptivePriorityMode? priorityMode)
            || !TryGetOptionalFloatArgument("-rayTracingAdaptiveNormalizePriorityByLuminance", 0.0f, 1.0f, out float? normalizePriorityByLuminance)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveReclassificationInterval", 1, 8, out int? reclassificationInterval)
            || !TryGetOptionalFloatArgument("-rayTracingAdaptiveHighestBucketSampleRate", 1.0f, 8.0f, out float? highestBucketSampleRate)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveMaxPathsPerPixel", 1, 16, out int? maxPathsPerPixel)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveBootstrapFrames", 1, 512, out int? bootstrapFrames)
            || !TryGetOptionalFloatArgument("-rayTracingAdaptiveBootstrapResolutionScale", 0.10f, 0.75f, out float? bootstrapResolutionScale)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveGuidanceHistoryFrames", 0, 64, out int? guidanceHistoryFrames)
            || !TryGetOptionalIntegerArgument("-rayTracingAdaptiveBootstrapGroupDivisor", 4, 16, out int? bootstrapGroupDivisor))
        {
            return false;
        }

        overrides = new AdaptiveSamplingOverrides(minSamples, guidanceChangeThreshold, guidanceMaxUpdates,
            priorityMode, normalizePriorityByLuminance, reclassificationInterval, highestBucketSampleRate,
            maxPathsPerPixel, bootstrapFrames, bootstrapResolutionScale, guidanceHistoryFrames,
            bootstrapGroupDivisor);
        return true;
    }

    private static bool TryGetAdaptivePriorityModeArgument(out GameManager.AdaptivePriorityMode? value)
    {
        const string argumentName = "-rayTracingAdaptivePriorityMode";
        string argument = GetCommandLineArgument(argumentName);
        value = null;
        if (argument == null)
        {
            return true;
        }

        if (Enum.TryParse(argument, true, out GameManager.AdaptivePriorityMode parsedValue)
            && Enum.IsDefined(typeof(GameManager.AdaptivePriorityMode), parsedValue))
        {
            value = parsedValue;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {argumentName} must be WelfordStandardError or DammertzSplitEstimator; received '{argument}'.");
        return false;
    }

    private static bool TryGetAdaptiveComparisonType(out GameManager.AdaptivePriorityMode? value)
    {
        const string argumentName = "-rayTracingAdaptiveType";
        string argument = GetCommandLineArgument(argumentName);
        value = null;
        if (argument == null)
        {
            return true;
        }

        if (string.Equals(argument, "welford", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "WelfordStandardError", StringComparison.OrdinalIgnoreCase))
        {
            value = GameManager.AdaptivePriorityMode.WelfordStandardError;
            return true;
        }
        if (string.Equals(argument, "dammertz", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "DammertzSplitEstimator", StringComparison.OrdinalIgnoreCase))
        {
            value = GameManager.AdaptivePriorityMode.DammertzSplitEstimator;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {argumentName} must be welford or dammertz; received '{argument}'.");
        return false;
    }

    private static bool TryGetOptionalIntegerArgument(string name, int minimum, int maximum, out int? value)
    {
        string argument = GetCommandLineArgument(name);
        value = null;
        if (argument == null)
        {
            return true;
        }
        if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            && parsedValue >= minimum && parsedValue <= maximum)
        {
            value = parsedValue;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {name} must be an integer from {minimum} through {maximum}; received '{argument}'.");
        return false;
    }

    private static bool TryGetOptionalBooleanArgument(string name, out bool? value)
    {
        string argument = GetCommandLineArgument(name);
        value = null;
        if (argument == null)
        {
            return true;
        }
        if (bool.TryParse(argument, out bool parsedValue))
        {
            value = parsedValue;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {name} must be true or false; received '{argument}'.");
        return false;
    }

    private static bool TryGetOptionalFloatArgument(string name, float minimum, float maximum, out float? value)
    {
        string argument = GetCommandLineArgument(name);
        value = null;
        if (argument == null)
        {
            return true;
        }
        if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue)
            && parsedValue >= minimum && parsedValue <= maximum)
        {
            value = parsedValue;
            return true;
        }

        ReportCommandLineError($"Scene capture argument {name} must be a number from {minimum:0.###} through {maximum:0.###}; received '{argument}'.");
        return false;
    }

    private static void ApplyAdaptiveSamplingOverrides(GameManager manager, AdaptiveSamplingOverrides overrides)
    {
        if (overrides.minSamples.HasValue) manager.adaptiveSamplingMinSamples = overrides.minSamples.Value;
        if (overrides.guidanceChangeThreshold.HasValue)
            manager.adaptiveGuidanceChangeThreshold = overrides.guidanceChangeThreshold.Value;
        if (overrides.guidanceMaxUpdates.HasValue)
            manager.adaptiveGuidanceMaxUpdates = overrides.guidanceMaxUpdates.Value;
        if (overrides.priorityMode.HasValue) manager.adaptivePriorityMode = overrides.priorityMode.Value;
        if (overrides.normalizePriorityByLuminance.HasValue)
            manager.adaptiveNormalizePriorityByLuminance = overrides.normalizePriorityByLuminance.Value;
        if (overrides.reclassificationInterval.HasValue) manager.adaptiveReclassificationInterval = overrides.reclassificationInterval.Value;
        if (overrides.highestBucketSampleRate.HasValue) manager.adaptiveHighestBucketSampleRate = overrides.highestBucketSampleRate.Value;
        if (overrides.maxPathsPerPixel.HasValue) manager.adaptiveMaxPathsPerPixel = overrides.maxPathsPerPixel.Value;
        if (overrides.bootstrapFrames.HasValue) manager.adaptiveBootstrapFrames = overrides.bootstrapFrames.Value;
        if (overrides.bootstrapResolutionScale.HasValue)
            manager.adaptiveBootstrapResolutionScale = overrides.bootstrapResolutionScale.Value;
        if (overrides.guidanceHistoryFrames.HasValue)
            manager.adaptiveGuidanceHistoryFrames = overrides.guidanceHistoryFrames.Value;
        if (overrides.bootstrapGroupDivisor.HasValue)
            manager.adaptiveBootstrapGroupDivisor = overrides.bootstrapGroupDivisor.Value;
    }

    private static void ExitBatchMode(int exitCode)
    {
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(exitCode);
        }
    }
}
