using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class RayTracingSceneCapture
{
    private const int DefaultSamplesPerScene = 200;
    private const int DefaultCaptureWidth = 512;
    private const int DefaultCaptureHeight = 512;
    private const string DefaultOutputFolder = "RayTracingSceneCaptures";
    private const string SessionPrefix = "GPURayTracing.SceneCapture.";
    private static RenderTexture _captureTarget;

    static RayTracingSceneCapture()
    {
        EditorApplication.update += Update;
    }

    // Invoke with -executeMethod RayTracingSceneCapture.CaptureFromCommandLine.
    public static void CaptureFromCommandLine()
    {
        string sceneArgument = GetCommandLineArgument("-rayTracingScenes");
        string outputArgument = GetCommandLineArgument("-rayTracingOutput");
        bool generateScenes = HasCommandLineArgument("-rayTracingGenerateScenes");
        string label = GetCommandLineArgument("-rayTracingCaptureLabel") ?? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        DebugRenderMode debugRenderMode = GetDebugRenderMode();
        if (!TryGetCaptureSettings(out int samplesPerScene, out int captureWidth, out int captureHeight))
        {
            ExitBatchMode(1);
            return;
        }
        if (string.IsNullOrWhiteSpace(sceneArgument))
        {
            Debug.LogError("Scene capture requires -rayTracingScenes with semicolon-separated scene asset paths.");
            ExitBatchMode(1);
            return;
        }

        string[] scenes = sceneArgument.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (generateScenes)
        {
            RayTracingSceneGenerator.GenerateScenes(scenes, true);
        }
        string outputRoot = string.IsNullOrWhiteSpace(outputArgument) ? GetDefaultOutputRoot() : outputArgument;
        if (Application.isBatchMode)
        {
            CaptureInBatchMode(label, scenes, outputRoot, samplesPerScene, captureWidth, captureHeight, debugRenderMode);
            return;
        }
        StartCapture(label, scenes, outputRoot, samplesPerScene, captureWidth, captureHeight);
    }

    private static void CaptureInBatchMode(
        string label,
        IReadOnlyList<string> scenePaths,
        string outputRoot,
        int samplesPerScene,
        int captureWidth,
        int captureHeight,
        DebugRenderMode debugRenderMode)
    {
        try
        {
            foreach (string scenePath in scenePaths)
            {
                string trimmedPath = scenePath.Trim();
                if (!File.Exists(trimmedPath))
                {
                    throw new FileNotFoundException("Ray tracing scene capture could not find its scene.", trimmedPath);
                }

                EditorSceneManager.OpenScene(trimmedPath);
                GameManager manager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                if (manager == null || manager.renderTextureCamera == null || manager.shader == null)
                {
                    throw new InvalidOperationException($"Scene capture requires a configured GameManager, render camera, and compute shader: {trimmedPath}");
                }

                manager.randomNoise = false;
                manager.enableFrameAccumulation = true;
                manager.enableTemporalDenoising = false;
                manager.debugRenderMode = debugRenderMode;
                manager.numberOfPasses = 1;
                manager._singleFrame = true;
                RayTracingTerrain terrain = UnityEngine.Object.FindFirstObjectByType<RayTracingTerrain>();
                if (terrain != null && !manager.RegisterTerrain(terrain))
                {
                    throw new InvalidOperationException($"Scene capture could not register its terrain: {trimmedPath}");
                }
                InitializeBatchRenderer(manager, captureWidth, captureHeight);

                _captureTarget = new RenderTexture(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Ray Tracing Scene Capture",
                    enableRandomWrite = true
                };
                _captureTarget.Create();
                manager.renderTextureCamera.targetTexture = _captureTarget;
                var source = new RenderTexture(captureWidth, captureHeight, 0, RenderTextureFormat.ARGB32);
                source.Create();

                try
                {
                    // RenderImage normally runs from a Game View callback. Invoke it directly because
                    // batch-mode editor sessions do not repaint a Game View.
                    for (int sample = 0; sample <= samplesPerScene; sample++)
                    {
                        manager.RenderImage(source, _captureTarget);
                    }

                    string sceneName = Path.GetFileNameWithoutExtension(trimmedPath);
                    string outputPath = Path.Combine(outputRoot, SanitizePathSegment(label), sceneName + ".png");
                    manager.ExportCurrentRenderPng(outputPath);
                    Debug.Log($"Ray tracing scene capture wrote '{outputPath}'.");
                }
                finally
                {
                    source.Release();
                    UnityEngine.Object.DestroyImmediate(source);
                }
                ReleaseCaptureTarget(manager.renderTextureCamera);
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

    private static void InitializeBatchRenderer(GameManager manager, int width, int height)
    {
        MethodInfo createOutputTexture = typeof(GameManager).GetMethod(
            "CreateOutputTexture",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (createOutputTexture == null)
        {
            throw new MissingMethodException(typeof(GameManager).FullName, "CreateOutputTexture");
        }

        createOutputTexture.Invoke(manager, new object[] { width, height });
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
        Debug.Log($"Ray tracing scene capture wrote '{outputPath}'.");
        ReleaseCaptureTarget(manager.renderTextureCamera);
        SessionState.SetBool(SessionPrefix + "SceneFinished", true);
        EditorApplication.isPlaying = false;
    }

    private static void ConfigureCapture(GameManager manager)
    {
        manager.randomNoise = false;
        manager.enableFrameAccumulation = true;
        manager.enableTemporalDenoising = false;
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

    private static bool TryGetCaptureSettings(out int samplesPerScene, out int captureWidth, out int captureHeight)
    {
        samplesPerScene = DefaultSamplesPerScene;
        captureWidth = DefaultCaptureWidth;
        captureHeight = DefaultCaptureHeight;

        if (!TryGetPositiveIntegerArgument("-rayTracingSamples", ref samplesPerScene)
            || !TryGetPositiveIntegerArgument("-rayTracingWidth", ref captureWidth)
            || !TryGetPositiveIntegerArgument("-rayTracingHeight", ref captureHeight))
        {
            return false;
        }

        return true;
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
