using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

[Serializable]
public sealed class VideoCaptureManager
{
    private const int MaxNumberOfPasses = 32;

    [Tooltip("Total path-tracing samples per pixel accumulated for each output frame.")]
    [Min(1)]
    public int samplesPerFrame = 128;

    [Tooltip("Simulation time advanced between output frames, in seconds.")]
    [Min(0.001f)]
    public float frameTimeStep = 1.0f / 30.0f;

    [Tooltip("Total simulated duration to capture, in seconds.")]
    [Min(0.1f)]
    public float duration = 5.0f;

    [Tooltip("Absolute output folder, or a folder relative to Application.persistentDataPath.")]
    public string outputFolder = "VideoFrames";

    [Tooltip("Encodes the completed PNG sequence as an H.264 MP4 while retaining the lossless source frames.")]
    public bool encodeMp4 = true;

    [Tooltip("Optional ffmpeg executable path. Leave blank to search PATH and common Homebrew locations.")]
    public string ffmpegPath = "";

    private GameManager _gameManager;
    private bool _active;
    private bool _awaitingSimulationStep;
    private int _frameIndex;
    private int _frameCount;
    private int _dispatchesPerFrame;
    private string _directory;
    private bool _previousSingleFrame;
    private float _previousSingleFrameRenderTime;
    private bool _previousFrameAccumulation;
    private bool _previousTemporalDenoising;
    private int _previousNumberOfPasses;
    private int _previousTargetFrameRate;
    private int _previousVSyncCount;
    private float _previousTimeScale;
    private float _previousCaptureDeltaTime;
    private Process _encodingProcess;
    private bool _encodingActive;
    private string _outputPath;

    public bool IsActive => _active;
    public int CompletedFrameCount => _frameIndex;
    public int FrameCount => _active ? _frameCount : CalculateFrameCount(duration, frameTimeStep);
    public string DirectoryPath => _directory;
    public bool IsEncodingActive => _encodingActive;
    public string OutputPath => _outputPath;

    public void Initialize(GameManager gameManager)
    {
        _gameManager = gameManager;
    }

    public void ValidateSettings()
    {
        samplesPerFrame = Mathf.Max(1, samplesPerFrame);
        frameTimeStep = Mathf.Max(0.000001f, frameTimeStep);
        duration = Mathf.Max(0.000001f, duration);
    }

    public void Update()
    {
        UpdateEncoding();
    }

    public bool HandleInput()
    {
        if (!_active)
        {
            return false;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cancel();
        }

        return true;
    }

    public void PrepareRender()
    {
        if (!_active || !_awaitingSimulationStep)
        {
            return;
        }

        _gameManager._singleFrameRenderTime = Time.time;
        Time.timeScale = 0.0f;
        Time.captureDeltaTime = 0.0f;
        _awaitingSimulationStep = false;
    }

    public void CompleteRender()
    {
        if (_active && !_awaitingSimulationStep && _gameManager.AccumulatedFrameCount >= _dispatchesPerFrame)
        {
            SaveFrameAndAdvance();
        }
    }

    public void Start()
    {
        if (_active || _encodingActive || !Application.isPlaying)
        {
            return;
        }

        int frameCount = CalculateFrameCount(duration, frameTimeStep);
        if (frameCount <= 0)
        {
            Debug.LogError("Video capture requires a positive duration and frame time step.", _gameManager);
            return;
        }
        if (_gameManager.debugRenderMode != DebugRenderMode.FinalColor)
        {
            Debug.LogError("Video capture requires the FinalColor debug render mode so frame accumulation is available.", _gameManager);
            return;
        }

        int requestedSamples = Mathf.Max(1, samplesPerFrame);
        int samplesPerDispatch = GetSamplesPerDispatch(requestedSamples, _gameManager.enableCaustics);
        string outputRoot = string.IsNullOrWhiteSpace(outputFolder) ? "VideoFrames" : outputFolder.Trim();
        if (!Path.IsPathRooted(outputRoot))
        {
            outputRoot = Path.Combine(Application.persistentDataPath, outputRoot);
        }

        try
        {
            _directory = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            int suffix = 1;
            while (System.IO.Directory.Exists(_directory))
            {
                _directory = Path.Combine(outputRoot, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{suffix++}");
            }
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception exception)
        {
            Debug.LogError($"Could not create video capture directory: {exception.Message}", _gameManager);
            _directory = null;
            return;
        }

        _previousSingleFrame = _gameManager._singleFrame;
        _previousSingleFrameRenderTime = _gameManager._singleFrameRenderTime;
        _previousFrameAccumulation = _gameManager.enableFrameAccumulation;
        _previousTemporalDenoising = _gameManager.TemporalDenoising.enabled;
        _previousNumberOfPasses = _gameManager.numberOfPasses;
        _previousTargetFrameRate = Application.targetFrameRate;
        _previousVSyncCount = QualitySettings.vSyncCount;
        _previousTimeScale = Time.timeScale;
        _previousCaptureDeltaTime = Time.captureDeltaTime;

        _frameIndex = 0;
        _frameCount = frameCount;
        _dispatchesPerFrame = requestedSamples / samplesPerDispatch;
        _awaitingSimulationStep = false;
        _active = true;
        _gameManager._singleFrame = true;
        _gameManager._previousSingleFrame = true;
        _gameManager._singleFrameRenderTime = Time.time;
        _gameManager.enableFrameAccumulation = true;
        _gameManager.TemporalDenoising.enabled = false;
        _gameManager.numberOfPasses = samplesPerDispatch;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;
        Time.timeScale = 0.0f;
        _gameManager.ResetFrameAccumulation();

        Debug.Log(
            $"Video capture started: {frameCount:N0} frames, {requestedSamples:N0} samples per frame, " +
            $"output '{_directory}'. Press Escape to cancel.",
            _gameManager);
    }

    public void Cancel()
    {
        if (!_active)
        {
            return;
        }

        Debug.Log($"Video capture cancelled after {_frameIndex:N0} of {_frameCount:N0} frames.", _gameManager);
        Finish(false);
    }

    public void Release()
    {
        if (_active)
        {
            Finish(false);
        }

        _encodingProcess?.Dispose();
        _encodingProcess = null;
        _encodingActive = false;
    }

    public bool IsCapturing => _active;

    public static int CalculateFrameCount(float captureDuration, float captureFrameTimeStep)
    {
        if (captureDuration <= 0.0f || captureFrameTimeStep <= 0.0f)
        {
            return 0;
        }

        double frameCount = Math.Ceiling((double)captureDuration / captureFrameTimeStep - 0.0000001);
        return frameCount >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)frameCount);
    }

    public static double EstimateCaptureSeconds(
        int frameCount,
        int requestedSamplesPerFrame,
        int currentSamplesPerFrame,
        float averageFrameMs,
        bool causticsEnabled)
    {
        if (frameCount <= 0 || requestedSamplesPerFrame <= 0 || currentSamplesPerFrame <= 0 || averageFrameMs <= 0.0f)
        {
            return 0.0;
        }

        return causticsEnabled
            ? frameCount * (double)requestedSamplesPerFrame * averageFrameMs / 1000.0
            : frameCount * (double)requestedSamplesPerFrame / currentSamplesPerFrame * averageFrameMs / 1000.0;
    }

    private static int GetSamplesPerDispatch(int requestedSamples, bool causticsEnabled)
    {
        if (causticsEnabled)
        {
            return 1;
        }

        for (int candidate = Mathf.Min(MaxNumberOfPasses, requestedSamples); candidate > 1; candidate--)
        {
            if (requestedSamples % candidate == 0)
            {
                return candidate;
            }
        }

        return 1;
    }

    private void SaveFrameAndAdvance()
    {
        string path = Path.Combine(_directory, $"frame_{_frameIndex:D6}.png");
        try
        {
            File.WriteAllBytes(path, _gameManager.EncodeCurrentOutputPng());
        }
        catch (Exception exception)
        {
            Debug.LogError($"Video capture failed while writing '{path}': {exception.Message}", _gameManager);
            Finish(false);
            return;
        }

        _frameIndex++;
        if (_frameIndex >= _frameCount)
        {
            string completedDirectory = _directory;
            int completedFrameCount = _frameCount;
            Finish(true);
            if (encodeMp4)
            {
                StartEncoding(completedDirectory, frameTimeStep);
            }
            Debug.Log(
                $"Video capture complete: {completedFrameCount:N0} PNG frames written to '{completedDirectory}'." +
                (_encodingActive ? " MP4 encoding started." : string.Empty),
                _gameManager);
            return;
        }

        _gameManager.ResetFrameAccumulation();
        _awaitingSimulationStep = true;
        Time.captureDeltaTime = Mathf.Max(0.000001f, frameTimeStep);
        Time.timeScale = 1.0f;
    }

    private void Finish(bool completed)
    {
        _active = false;
        _awaitingSimulationStep = false;
        _gameManager._singleFrame = _previousSingleFrame;
        _gameManager._previousSingleFrame = _previousSingleFrame;
        _gameManager._singleFrameRenderTime = _previousSingleFrameRenderTime;
        _gameManager.enableFrameAccumulation = _previousFrameAccumulation;
        _gameManager.TemporalDenoising.enabled = _previousTemporalDenoising;
        _gameManager.numberOfPasses = _previousNumberOfPasses;
        Application.targetFrameRate = _previousTargetFrameRate;
        QualitySettings.vSyncCount = _previousVSyncCount;
        Time.captureDeltaTime = _previousCaptureDeltaTime;
        Time.timeScale = _previousTimeScale;
        _gameManager.ResetFrameAccumulation();

        if (!completed)
        {
            _frameCount = 0;
        }
    }

    private void StartEncoding(string frameDirectory, float captureFrameTimeStep)
    {
        string executable = ResolveFfmpegExecutable(ffmpegPath);
        _outputPath = Path.Combine(frameDirectory, "video.mp4");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildEncoderArguments(frameDirectory, _outputPath, captureFrameTimeStep),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _encodingProcess = Process.Start(startInfo);
            _encodingActive = _encodingProcess != null;
            if (!_encodingActive)
            {
                Debug.LogError("ffmpeg did not start. The lossless PNG sequence has been retained.", _gameManager);
            }
        }
        catch (Exception exception)
        {
            _encodingProcess = null;
            _encodingActive = false;
            Debug.LogError(
                $"Could not start ffmpeg at '{executable}': {exception.Message}. " +
                "Set Video Ffmpeg Path or install ffmpeg. The lossless PNG sequence has been retained.",
                _gameManager);
        }
    }

    private void UpdateEncoding()
    {
        if (!_encodingActive || _encodingProcess == null || !_encodingProcess.HasExited)
        {
            return;
        }

        int exitCode = _encodingProcess.ExitCode;
        _encodingProcess.Dispose();
        _encodingProcess = null;
        _encodingActive = false;
        if (exitCode == 0 && File.Exists(_outputPath))
        {
            Debug.Log($"Video encoding complete: '{_outputPath}'.", _gameManager);
        }
        else
        {
            Debug.LogError(
                $"ffmpeg exited with code {exitCode}. The lossless PNG sequence has been retained in '{_directory}'.",
                _gameManager);
        }
    }

    private static string ResolveFfmpegExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        string[] commonPaths = { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" };
        for (int i = 0; i < commonPaths.Length; i++)
        {
            if (File.Exists(commonPaths[i]))
            {
                return commonPaths[i];
            }
        }

        return "ffmpeg";
    }

    private static string BuildEncoderArguments(string frameDirectory, string outputPath, float captureFrameTimeStep)
    {
        double frameRate = 1.0 / Math.Max(0.000001, captureFrameTimeStep);
        double roundedFrameRate = Math.Round(frameRate);
        if (Math.Abs(frameRate - roundedFrameRate) < 0.0001)
        {
            frameRate = roundedFrameRate;
        }

        string frameRateText = frameRate.ToString("0.########", CultureInfo.InvariantCulture);
        string inputPath = Path.Combine(frameDirectory, "frame_%06d.png");
        return $"-y -framerate {frameRateText} -start_number 0 -i {QuoteProcessArgument(inputPath)} " +
            "-vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" " +
            $"-c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart {QuoteProcessArgument(outputPath)}";
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
