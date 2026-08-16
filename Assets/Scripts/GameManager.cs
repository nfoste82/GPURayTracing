using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PathTracing;
using PathTracing.AccelerationStructures;
using PathTracing.Camera;
using PathTracing.Caustics;
using PathTracing.Denoising;
using PathTracing.Lighting;
using PathTracing.PathTracedTypes;
using PathTracing.Shapes;
using PathTracing.TemporalDenoising;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Light = PathTracing.Lighting.Light;

[RequireComponent(typeof(TerrainManager))]
[RequireComponent(typeof(CameraManager))]
[RequireComponent(typeof(WaterManager))]
public class GameManager : MonoBehaviour
{
    [SerializeField, HideInInspector] private CameraManager _cameraManager;

    [SerializeField, HideInInspector]
    private bool bakeBvhUponExit = true;

    [Tooltip("Logs phase timings for initial scene buffer construction and the first compute dispatch.")]
    public bool profileStartup = true;

    public ComputeShader shader;

    public CameraManager CameraManager
    {
        get
        {
            if (_cameraManager == null)
            {
                _cameraManager = GetComponent<CameraManager>();
            }

            return _cameraManager;
        }
    }

    public Camera renderTextureCamera
    {
        get => CameraManager.renderTextureCamera;
        set => CameraManager.renderTextureCamera = value;
    }

    [Header("Caustic preservation")]
    [Tooltip("Prevents the denoiser from diffusing isolated HDR caustic candidates into neighboring receiver pixels. Higher values preserve only stronger local outliers.")]
    [Range(1.5f, 32.0f)]
    public float causticPreservationThreshold = 4.0f;

    [Header("Quality settings (Higher quality -> Slower)")]
    [Tooltip("Percentage of the camera viewport traced before bilinear reconstruction to the display resolution. Lower values reduce ray work but soften fine detail.")]
    [Range(25.0f, 100.0f)]
    public float renderResolutionPercent = 100.0f;

    [Range(1, 32)]
    public int numberOfPasses = 1;

    [Range(0.0f, 5.0f)]
    [Tooltip("Width of the random sub-pixel camera filter in pixel units. 1 uses the full pixel footprint and is the correct anti-aliasing default; values above 1 deliberately blur across neighboring pixels.")]
    public float subpixelJitterScale = 1.4f;

    [Tooltip("Progressively averages final-color renders while the camera, scene, and quality settings are unchanged. Debug render modes are not accumulated.")]
    public bool enableFrameAccumulation = true;

    [Range(1, 16)]
    public int numBounces = 8;

    [Range(0, 5)]
    public int shadowQuality = 0;

    [Tooltip("Use flat object loops below this count; set above the scene object count to force flat loops.")]
    [Range(0, 1024)]
    public int topLevelBvhMinObjectCount = 1024;

    [Tooltip("Use flat shadow blocker loops below this count; set above the blocker count to force flat shadow loops.")]
    [Range(0, 1024)]
    public int shadowBvhMinObjectCount = 1024;

    [Header("Sampling and Accumulation")]
    [Range(0f, 1.5f)]
    public float shadowRandomness = 0.65f;

    [Header("Parallax Mapping")]
    [Range(0f, 90f)]
    [Tooltip("View angle from the surface normal where parallax uses its maximum strength. It interpolates each material's parallax strength at this angle or higher, down toward the material's parallax minimum strength at 0 degrees.")]
    public float parallaxMaximumStrengthAngle = 20f;

    [Tooltip("Diagnostic: cap how many lights each shading point samples. 0 = sample all lights (normal). Lower values confirm the per-hit light loop is the bottleneck.")]
    [Range(0, 256)]
    public int maxLightSamples = 0;

    [SerializeField, HideInInspector]
    private LightingManager _lightingManager = new();

    public LightingManager Lighting => _lightingManager ??= new LightingManager();
    public SpatialDenoisingManager SpatialDenoising => _spatialDenoisingManager ??= new SpatialDenoisingManager();
    public TemporalDenoisingManager TemporalDenoising => _temporalDenoisingManager ??= new TemporalDenoisingManager();

    [Tooltip("Builds a photon map for sphere and triangle-light caustics through glass, closed meshes, and the registered water volume. Disabled by default.")]
    public bool enableCaustics = false;

    [Tooltip("Globally enables the registered FogVolume without disabling its component or changing shader resources.")]
    public bool enableVolumetricFog = true;

    [Tooltip("Multiplies the density on the registered FogVolume. Useful for tuning a scene from the GameManager.")]
    [Range(0.0f, 2.0f)]
    public float fogDensityScale = 1.0f;

    [Tooltip("Multiplies the FogVolume scattering albedo. Lower values absorb more light and preserve shaft contrast.")]
    [Range(0.0f, 2.0f)]
    public float fogScatteringScale = 1.0f;

    [Tooltip("Display-oriented multiplier for direct light scattered toward the camera. Raise this to reveal shafts without increasing extinction or washing out surfaces.")]
    [Range(0.0f, 32.0f)]
    public float fogInScatteringIntensity = 8.0f;

    [Tooltip("Allows paths to scatter repeatedly in fog. More physical, but slower, noisier, and more likely to wash out high-contrast light shafts.")]
    public bool enableFogMultipleScattering = false;

    public DebugRenderMode debugRenderMode = DebugRenderMode.FinalColor;

    [Tooltip("Master brightness applied before ACES tone mapping. Acts like a camera exposure dial.")]
    [Range(0.0f, 8.0f)]
    public float exposure = 1.0f;

    [Tooltip("Maximum HDR luminance of one path sample before averaging. Lower positive values clamp fireflies more strongly; 0 disables the clamp.")]
    [Range(0.0f, 20.0f)]
    public float fireflyClamp = 1.0f;

    public bool randomNoise = false;

    public Texture skyboxTexture;

    [HideInInspector]
    public Texture2D defaultMeshAlbedoTexture;

    [HideInInspector]
    public Texture2D defaultMeshMetallicRoughnessTexture;

    [HideInInspector]
    public Texture2D defaultMeshNormalTexture;

    [HideInInspector]
    public bool syncUnitySkyboxToRayTracedSkybox = true;

    [HideInInspector]
    [Range(0.0f, 8.0f)]
    public float unitySkyboxExposure = 1.0f;

    [HideInInspector]
    [Range(0.0f, 360.0f)]
    public float unitySkyboxRotation = 0.0f;

    private Material _unitySkyboxMaterial;

    private RenderTexture _outputTexture;
    private RenderTexture _presentationTexture;
    private RenderTexture _accumulationTexture;
    // Reconstruction-neutral, linear feature outputs. They are allocated now but do not change
    // presentation until a denoiser consumes them in a later milestone.
    private RenderTexture _beautyTexture;
    private RenderTexture _featureNormalTexture;
    private RenderTexture _featureAlbedoTexture;
    private RenderTexture _featureDepthTexture;
    private RenderTexture _featureIdentityTexture;
    private RenderTexture _featureValidityTexture;
    private Vector2Int _textureSize;
    private Vector2Int _displayTextureSize;
    private int _accumulatedFrameCount;
    private long _renderedFrameCount;
    private int _accumulationStateHash;
    private bool _hasAccumulationStateHash;
    private RenderTexture _presentationSource;

    private List<Sphere> _spheres = new ();
    private readonly List<PathTracedSphere> _sphereObjects = new ();
    private ComputeBuffer _sphereBuffer;

    private List<Triangle> _triangles = new ();
    private readonly List<MeshInfo> _meshInfos = new ();
    private readonly List<BvhNode> _bvhNodes = new ();
    private readonly List<PathTracedMesh> _meshObjects = new ();
    private readonly MeshBvhTemplateCache _meshBvhTemplates = new();
    private readonly List<Texture2D> _meshAlbedoTextures = new ();
    private readonly List<Texture2D> _meshMetallicRoughnessTextures = new ();
    private readonly List<Texture2D> _meshNormalTextures = new ();
    private readonly List<Texture2D> _meshParallaxTextures = new ();
    private Texture2DArray _meshAlbedoTextureArray;
    private Texture2DArray _meshMetallicRoughnessTextureArray;
    private Texture2DArray _meshNormalTextureArray;
    private Texture2DArray _meshParallaxTextureArray;
    
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _meshBuffer;
    private ComputeBuffer _bvhNodeBuffer;
    
    [SerializeField]
    private SpatialDenoisingManager _spatialDenoisingManager = new ();
    
    [SerializeField]
    private TemporalDenoisingManager _temporalDenoisingManager = new ();
    
    [SerializeField, HideInInspector]
    private CausticsManager _causticsManager = new();
    
    private readonly SceneBvhManager _sceneBvhs = new();
    
    private TerrainManager _terrainManager;
    
    [SerializeField, HideInInspector]
    private VideoCaptureManager _videoCaptureManager = new();
    public VideoCaptureManager VideoCapture => _videoCaptureManager;
    
    public CausticsManager Caustics => _causticsManager;
    
    private WaterManager _waterManager;
    private WaterManager WaterManager
    {
        get
        {
            if (_waterManager == null)
            {
                _waterManager = GetComponent<WaterManager>();
            }

            return _waterManager;
        }
    }

    // Tracks whether any shadow-casting blocker (regular sphere or mesh triangle) is transparent
    // (opacity < 1). When false, shadow rays in the shader take a cheaper pure-occlusion path that
    // early-outs on the first opaque blocker without the nearest-transparent-blocker bookkeeping.
    // Recomputed each frame in UpdateSpheres()/UpdateTriangles().
    private const float ShadowBlockerOpaqueThreshold = 1.0f;

    [Tooltip("Freezes simulation time and progressively refines the current view. Camera and scene changes reset accumulation and render the updated view.")]
    public bool _singleFrame = false;
    
    internal bool _previousSingleFrame;
    internal float _singleFrameRenderTime;

    // Compute-shader variants compile synchronously on their first Dispatch, which freezes the
    // main thread (the spinning-wheel stall) the first time a debug render mode is selected. We
    // track which debug variants have already been dispatched, and when a new one is requested we
    // show an on-screen overlay for one frame BEFORE running the stalling dispatch, so the user
    // sees a "compiling" message instead of an apparently locked-up app.
    private readonly HashSet<int> _warmedShaderVariants = new ();
    private DebugRenderMode _appliedDebugRenderMode = DebugRenderMode.FinalColor;
    private bool _appliedCausticsEnabled;
    private bool _appliedFogEnabled;
    private bool _pendingVariantWarmup;
    
    public int SphereCount => _spheres.Count;
    public int LightCount => Lighting.LightCount;
    public int MeshCount => _meshInfos.Count;
    public int TriangleCount => _triangles.Count;
    public int TopLevelBvhNodeCount => _sceneBvhs.TopLevelNodeCount;
    public int ShadowBvhNodeCount => _sceneBvhs.ShadowNodeCount;
    public int TopLevelBvhObjectCount => _sceneBvhs.TopLevelObjectCount;
    public int ShadowBvhObjectCount => _sceneBvhs.ShadowObjectCount;
    public bool IsTopLevelBvhActive => _sceneBvhs.IsTopLevelActive;
    public bool IsShadowBvhActive => _sceneBvhs.IsShadowActive;
    
    // TextureSize is the internal ray-tracing resolution; DisplayTextureSize is the camera target size.
    public Vector2Int TextureSize => _textureSize;
    internal RenderTexture OutputTexture => _outputTexture;
    internal RenderTexture BeautyTexture => _beautyTexture;
    internal RenderTexture FeatureNormalTexture => _featureNormalTexture;
    internal RenderTexture FeatureAlbedoTexture => _featureAlbedoTexture;
    internal RenderTexture FeatureDepthTexture => _featureDepthTexture;
    internal RenderTexture FeatureIdentityTexture => _featureIdentityTexture;
    internal RenderTexture FeatureValidityTexture => _featureValidityTexture;
    
    internal void SetPresentationSource(RenderTexture source) => _presentationSource = source;
    public Water WaterInternal => WaterManager.Water;
    public Vector2Int DisplayTextureSize => _displayTextureSize;
    public int AccumulatedFrameCount => _accumulatedFrameCount;
    public int SphereLightCount => Lighting.SphereLightCount;
    
    public int MeshLightCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _meshObjects.Count; i++)
            {
                if (_meshObjects[i].light != null)
                {
                    count++;
                }
            }
            return count;
        }
    }
    
    public int TriangleLightCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _triangles.Count; i++)
            {
                if (_triangles[i].lightIndex >= 0)
                {
                    count++;
                }
            }
            return count;
        }
    }
    public bool HasWaterVolume => WaterManager.HasWaterVolume;
    public bool IsVolumetricFogActive => IsFogEnabled();
    public float EffectiveFogDensity => IsFogEnabled() ? _fogVolume.Density * Mathf.Max(0.0f, fogDensityScale) : 0.0f;
    public Color EffectiveFogScatteringAlbedo => IsFogEnabled()
        ? _fogVolume.ScatteringAlbedo * Mathf.Max(0.0f, fogScatteringScale)
        : Color.black;

    private bool _buffersNeedRebuilding;
    private readonly HashSet<PathTracingObject> _rayTracingObjects = new ();
    
    private static readonly int SkyboxTexture = Shader.PropertyToID("_SkyboxTexture");
    private static readonly int MeshAlbedoTextures = Shader.PropertyToID("_MeshAlbedoTextures");
    private static readonly int MeshMetallicRoughnessTextures = Shader.PropertyToID("_MeshMetallicRoughnessTextures");
    private static readonly int MeshNormalTextures = Shader.PropertyToID("_MeshNormalTextures");
    private static readonly int MeshParallaxTextures = Shader.PropertyToID("_MeshParallaxTextures");
    private static readonly int Seed = Shader.PropertyToID("_Seed");
    private static readonly int NumberOfPasses = Shader.PropertyToID("_NumberOfPasses");
    private static readonly int SubpixelJitterScale = Shader.PropertyToID("_SubpixelJitterScale");
    private static readonly int NumBounces = Shader.PropertyToID("_NumBounces");
    private static readonly int Mode = Shader.PropertyToID("_DebugRenderMode");
    private static readonly int UseFrameAccumulation = Shader.PropertyToID("_UseFrameAccumulation");
    private static readonly int FrameCount = Shader.PropertyToID("_AccumulatedFrameCount");
    private static readonly int SampleOffset = Shader.PropertyToID("_SampleOffset");
    private static readonly int ParallaxMaximumStrengthCosine = Shader.PropertyToID("_ParallaxMaximumStrengthCosine");
    private static readonly int Exposure = Shader.PropertyToID("_Exposure");
    private static readonly int FireflyClamp = Shader.PropertyToID("_FireflyClamp");
    private static readonly int FogEnabled = Shader.PropertyToID("_FogEnabled");
    private static readonly int FogBoundsMin = Shader.PropertyToID("_FogBoundsMin");
    private static readonly int FogBoundsMax = Shader.PropertyToID("_FogBoundsMax");
    private static readonly int FogScatteringAlbedo = Shader.PropertyToID("_FogScatteringAlbedo");
    private static readonly int FogDensity = Shader.PropertyToID("_FogDensity");
    private static readonly int FogInScatteringIntensity = Shader.PropertyToID("_FogInScatteringIntensity");
    private static readonly int FogMultipleScattering = Shader.PropertyToID("_FogMultipleScattering");
    private static readonly int Spheres = Shader.PropertyToID("_Spheres");
    private static readonly int Lights = Shader.PropertyToID("_Lights");
    private static readonly int Triangles = Shader.PropertyToID("_Triangles");
    private static readonly int Meshes = Shader.PropertyToID("_Meshes");
    private static readonly int BvhNodes = Shader.PropertyToID("_BvhNodes");
    private static readonly int MeshLightTriangleCdf = Shader.PropertyToID("_MeshLightTriangleCdf");
    private static readonly int NumSpheres = Shader.PropertyToID("_NumSpheres");
    private static readonly int NumTriangles = Shader.PropertyToID("_NumTriangles");
    private static readonly int NumMeshes = Shader.PropertyToID("_NumMeshes");
    private static readonly int Result = Shader.PropertyToID("Result");
    private static readonly int AccumulationResult = Shader.PropertyToID("AccumulationResult");
    private static readonly int Beauty = Shader.PropertyToID("Beauty");
    private static readonly int FeatureNormal = Shader.PropertyToID("FeatureNormal");
    private static readonly int FeatureAlbedo = Shader.PropertyToID("FeatureAlbedo");
    private static readonly int FeatureDepth = Shader.PropertyToID("FeatureDepth");
    private static readonly int FeatureIdentity = Shader.PropertyToID("FeatureIdentity");
    private static readonly int FeatureValidity = Shader.PropertyToID("FeatureValidity");
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");
    private static readonly int Tint = Shader.PropertyToID("_Tint");
    private static readonly int Rotation = Shader.PropertyToID("_Rotation");

    private const int SphereStride = 92;
    private const int TriangleStride = 260;
    private const int MeshInfoStride = 48;
    private const int BvhNodeStride = 48;
    // The photon transport kernel carries a medium stack and intersection state. A 32-thread
    // group keeps Metal register allocation within its recommended per-group budget.
    private const int CausticTraceThreadCount = 32;
    // CSMain combines path tracing with optional volumetric fog. Keeping its groups at 16 threads
    // avoids Metal's recommended temporary-register budget being exceeded.
    private const int RenderThreadCountX = 4;
    private const int RenderThreadCountY = 4;
    private const int MaxCausticGridCells = 262144;
    private FogVolume _fogVolume;
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private readonly List<string> _startupProfilePhases = new ();
    private double _startupRegistrationMilliseconds;
    private bool _startupProfilePending;
    private bool _startupInitializationPending;
    private string _startupInitializationStatus;
    private bool _loadedBakedMeshBvhs;
    private string _bvhBakeLoadStatus = "not attempted";
    private int _profileBuiltMeshTemplateCount;
    private long _profileBuiltMeshTemplateTicks;
    private long _profileTextureArrayTicks;
    
    public void InitSceneSettings(SceneSettings settings)
    {
        numberOfPasses = settings.NumberOfPasses;
        subpixelJitterScale = settings.SubpixelJitterScale;
        enableFrameAccumulation = settings.EnableFrameAccumulation;
        numBounces = settings.NumBounces;
        shadowQuality = settings.ShadowQuality;
        topLevelBvhMinObjectCount = settings.TopLevelBvhMinObjectCount;
        shadowBvhMinObjectCount = settings.ShadowBvhMinObjectCount;
        shadowRandomness = settings.ShadowRandomness;
        Lighting.LightSamplingStrategy = settings.LightSamplingStrategy;
        Lighting.LightSampleCount = settings.LightSampleCount;
        SpatialDenoising.enabled = settings.EnableSpatialDenoising;
        SpatialDenoising.iterations = settings.DenoiserIterations;
        SpatialDenoising.luminanceSigma = settings.DenoiserLuminanceSigma;
        enableCaustics = settings.EnableCaustics;
        Caustics.PhotonCount = settings.CausticPhotonCount;
        Caustics.GatherRadius = settings.CausticGatherRadius;
        Caustics.Seed = settings.CausticSeed;
        Caustics.Intensity = settings.CausticIntensity;
        fogDensityScale = settings.FogDensityScale;
        fogScatteringScale = settings.FogScatteringScale;
        fogInScatteringIntensity = settings.FogInScatteringIntensity;
        enableFogMultipleScattering = settings.EnableFogMultipleScattering;
        Lighting.LightFalloffScale = settings.LightFalloffScale;
        exposure = settings.Exposure;
        fireflyClamp = settings.FireflyClamp;
        randomNoise = settings.RandomNoise;
        Lighting.SkyboxLightColor = settings.SkyboxLightColor;

        CameraManager.InitSceneSettings(settings);
    }

    internal void OnWaterChanged()
    {
        CameraManager.AutoFocusSceneChanged = true;
        ResetFrameAccumulation();
    }

    private void Start()
    {
        _videoCaptureManager.Initialize(this);
        CameraManager.FocusRequested += QueueFocusQuery;
        _terrainManager = GetComponent<TerrainManager>();
        _terrainManager.TerrainChanged += ResetFrameAccumulation;
        _temporalDenoisingManager.Initialize(this);
        if (!_singleFrame && Time.timeScale == 0.0f)
        {
            Time.timeScale = 1.0f;
        }

        _startupRegistrationMilliseconds = _startupStopwatch.Elapsed.TotalMilliseconds;
        _startupProfilePending = profileStartup;
        EnsureBenchmarkComponents();
        SyncUnitySkyboxPreview();

        var outputTextureStart = Stopwatch.GetTimestamp();
        EnsureOutputTextureSize(Screen.width, Screen.height);
        AddStartupProfilePhase("output textures", outputTextureStart);

        var bakedBvhLoadStart = Stopwatch.GetTimestamp();
        TryLoadBakedMeshBvhs();
        AddStartupProfilePhase($"baked mesh BVH load ({_bvhBakeLoadStatus})", bakedBvhLoadStart);

        _startupInitializationPending = true;
        _startupInitializationStatus = "Preparing ray tracing scene data";
        StartCoroutine(InitializeStartupBuffers());
    }

    private IEnumerator InitializeStartupBuffers()
    {
        // Let the Game view paint the startup notice before synchronous buffer construction starts.
        yield return new WaitForEndOfFrame();
        _startupInitializationStatus = "Building ray tracing buffers";
        RebuildBuffers(_startupProfilePending);
        _startupInitializationPending = false;
    }

    private void EnsureBenchmarkComponents()
    {
        var debugOverlay = GetComponent<RayTracingBenchmarkOverlay>();
        if (debugOverlay == null)
        {
            debugOverlay = gameObject.AddComponent<RayTracingBenchmarkOverlay>();
        }
        debugOverlay.gameManager = this;

        var benchmarkRunner = GetComponent<RayTracingBenchmarkRunner>();
        if (benchmarkRunner == null)
        {
            benchmarkRunner = gameObject.AddComponent<RayTracingBenchmarkRunner>();
        }
        benchmarkRunner.gameManager = this;
    }

    private void OnValidate()
    {
        _spatialDenoisingManager.ValidateSettings();
        subpixelJitterScale = Mathf.Clamp(subpixelJitterScale, 0.0f, 2.0f);
        _temporalDenoisingManager.ValidateSettings();
        CameraManager.cameraOrbitZoom = Mathf.Max(0.1f, CameraManager.cameraOrbitZoom);
        causticPreservationThreshold = Mathf.Clamp(causticPreservationThreshold, 1.5f, 32.0f);
        _videoCaptureManager.ValidateSettings();
        SyncUnitySkyboxPreview();
    }

    private void SyncUnitySkyboxPreview()
    {
        if (!syncUnitySkyboxToRayTracedSkybox || skyboxTexture == null)
        {
            return;
        }

        var skyboxShader = Shader.Find("Skybox/Panoramic");
        if (skyboxShader == null)
        {
            return;
        }

        if (_unitySkyboxMaterial == null || _unitySkyboxMaterial.shader != skyboxShader)
        {
            _unitySkyboxMaterial = new Material(skyboxShader)
            {
                name = "Ray Traced Skybox Preview",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        _unitySkyboxMaterial.SetTexture(MainTex, skyboxTexture);
        _unitySkyboxMaterial.SetColor(Tint, Lighting.SkyboxLightColor);
        _unitySkyboxMaterial.SetFloat(Exposure, unitySkyboxExposure);
        _unitySkyboxMaterial.SetFloat(Rotation, unitySkyboxRotation);
        RenderSettings.skybox = _unitySkyboxMaterial;
    }

    private void CreateOutputTexture(int width, int height)
    {
        _outputTexture?.Release();
        _presentationTexture?.Release();
        _accumulationTexture?.Release();
        _beautyTexture?.Release();
        _featureNormalTexture?.Release();
        _featureAlbedoTexture?.Release();
        _featureDepthTexture?.Release();
        _featureIdentityTexture?.Release();
        _featureValidityTexture?.Release();
        _spatialDenoisingManager.ReleaseResources();
        ReleaseTemporalDenoiserResources();
        _textureSize = new Vector2Int(width, height);
        _outputTexture = new RenderTexture(_textureSize.x, _textureSize.y, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        _outputTexture.Create();

        _presentationTexture = new RenderTexture(_displayTextureSize.x, _displayTextureSize.y, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear
        };
        _presentationTexture.Create();

        _accumulationTexture = new RenderTexture(_textureSize.x, _textureSize.y, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        _accumulationTexture.Create();

        _beautyTexture = CreateFeatureTexture(RenderTextureFormat.ARGBFloat);
        _featureNormalTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _featureAlbedoTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _featureDepthTexture = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _featureIdentityTexture = CreateFeatureTexture(RenderTextureFormat.RFloat);
        _featureValidityTexture = CreateFeatureTexture(RenderTextureFormat.RHalf);
        ResetFrameAccumulation();
        _temporalDenoisingManager.ResetHistory();
    }

    private RenderTexture CreateFeatureTexture(RenderTextureFormat format)
    {
        var texture = new RenderTexture(_textureSize.x, _textureSize.y, 0, format)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        texture.Create();
        return texture;
    }

    internal bool IsFogEnabledInternal() => IsFogEnabled();
    internal float GetCameraApertureRadiusInternal() => CameraManager.GetApertureRadius();

    private bool ShouldRunSpatialDenoiser()
    {
        return _spatialDenoisingManager.ShouldRun(debugRenderMode);
    }

    private bool IsTemporalDebugMode()
    {
        return _temporalDenoisingManager.IsDebugMode(debugRenderMode);
    }

    private bool ShouldRunTemporalDenoiser()
    {
        // Keep temporal history warm while the camera is still. The still-image path remains the
        // presented output, but its stable history is immediately available when motion resumes.
        return _temporalDenoisingManager.ShouldRun(debugRenderMode);
    }

    private bool IsCausticPreservationDebugMode()
    {
        return debugRenderMode == DebugRenderMode.CausticPreservationMask;
    }

    private bool ShouldUseTemporalAccumulation()
    {
        return _temporalDenoisingManager.ShouldUseAccumulation(debugRenderMode);
    }

    private void ReleaseTemporalDenoiserResources()
    {
        _temporalDenoisingManager.ReleaseResources();
    }

    private void Update()
    {
        _videoCaptureManager.Update();

        if (_buffersNeedRebuilding)
        {
            RebuildBuffers();
        }

        if (_singleFrame != _previousSingleFrame)
        {
            SetSingleFrameMode(_singleFrame);
        }

        if (_videoCaptureManager.HandleInput())
        {
            return;
        }

        CameraManager.HandleInput();
        CameraManager.HandleFocusInput();

        if (Input.GetKeyDown(KeyCode.Space))
        {
            SetSingleFrameMode(!_singleFrame);
        }
    }

    private GUIStyle _compileNoticeStyle;

    // Shows a centered notice during the single frame before a debug shader variant's first
    // (blocking) Dispatch. The notice is painted this frame; the actual compile stall happens next
    // frame with this message still on screen, so the user sees an explanation instead of an
    // apparently frozen application.
    private void OnGUI()
    {
        if (!_pendingVariantWarmup && !_startupInitializationPending)
        {
            return;
        }

        if (_compileNoticeStyle == null)
        {
            _compileNoticeStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                padding = new RectOffset(20, 20, 20, 20)
            };
            _compileNoticeStyle.normal.textColor = Color.white;
        }

        const float boxWidth = 520f;
        const float boxHeight = 120f;
        
        var rect = new Rect(
            (Screen.width - boxWidth) * 0.5f,
            (Screen.height - boxHeight) * 0.5f,
            boxWidth,
            boxHeight);

        var message = _startupInitializationPending
            ? _startupInitializationStatus + "\nStartup timings will be logged when initialization completes."
            : "Compiling shader variant, this may take several minutes...";
        
        GUI.Box(rect, message, _compileNoticeStyle);
    }

    private void SetSingleFrameMode(bool enabled)
    {
        _singleFrame = enabled;
        _previousSingleFrame = enabled;
        ResetFrameAccumulation();

        if (enabled)
        {
            _singleFrameRenderTime = Time.time;
            EnableSingleFrameSettings();
        }
        else
        {
            EnableRealtimeSettings();
        }
    }

    private static void EnableSingleFrameSettings()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 15;
        Time.timeScale = 0.0f;
    }

    private static void EnableRealtimeSettings()
    {
        QualitySettings.vSyncCount = 2;
        Application.targetFrameRate = 60;
        Time.timeScale = 1.0f;
    }

    private void OnDestroy()
    {
        if (_cameraManager != null)
        {
            _cameraManager.FocusRequested -= QueueFocusQuery;
        }
        if (_terrainManager != null)
        {
            _terrainManager.TerrainChanged -= ResetFrameAccumulation;
        }
        _videoCaptureManager.Release();
        
        _outputTexture?.Release();
        _presentationTexture?.Release();
        _accumulationTexture?.Release();
        _beautyTexture?.Release();
        _featureNormalTexture?.Release();
        _featureAlbedoTexture?.Release();
        _featureDepthTexture?.Release();
        _featureIdentityTexture?.Release();
        _featureValidityTexture?.Release();
        
        _spatialDenoisingManager.ReleaseResources();
        
        ReleaseTemporalDenoiserResources();
        
        _sphereBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        
        _bvhNodeBuffer?.Release();
        _sceneBvhs.Release();
        
        _lightingManager.ReleaseBuffers();
        
        _causticsManager.ReleaseResources();
        
        DestroyRuntimeTextureArrays();
    }

    private void QueueFocusQuery(Vector2 viewportPosition)
    {
        if (CameraManager.FocusQueryInFlight)
        {
            return;
        }

        CameraManager.PendingFocusQueryUv = viewportPosition;
        CameraManager.FocusQueryPending = true;
    }

    private void DestroyRuntimeTextureArrays()
    {
        DestroyRuntimeTextureArray(_meshAlbedoTextureArray);
        DestroyRuntimeTextureArray(_meshMetallicRoughnessTextureArray);
        DestroyRuntimeTextureArray(_meshNormalTextureArray);
        DestroyRuntimeTextureArray(_meshParallaxTextureArray);
        _meshAlbedoTextureArray = null;
        _meshMetallicRoughnessTextureArray = null;
        _meshNormalTextureArray = null;
        _meshParallaxTextureArray = null;
    }

    private static void DestroyRuntimeTextureArray(Texture2DArray textureArray)
    {
        if (textureArray != null)
        {
            DestroyRuntimeObject(textureArray);
        }
    }

    private static void DestroyRuntimeObject(UnityEngine.Object runtimeObject)
    {
        if (Application.isPlaying)
        {
            Destroy(runtimeObject);
        }
        else
        {
            DestroyImmediate(runtimeObject);
        }
    }

    private void EnsureOutputTextureSize(int width, int height)
    {
        _displayTextureSize = new Vector2Int(width, height);
        
        var internalSize = CalculateInternalRenderSize(width, height, renderResolutionPercent);
        var internalWidth = internalSize.x;
        var internalHeight = internalSize.y;
        
        if (_outputTexture == null || _presentationTexture == null
            || internalWidth != _textureSize.x || internalHeight != _textureSize.y
            || width != _presentationTexture.width || height != _presentationTexture.height)
        {
            CreateOutputTexture(internalWidth, internalHeight);
        }

        CameraManager.SetAspect((float)_displayTextureSize.x / _displayTextureSize.y);
    }

    private static Vector2Int CalculateInternalRenderSize(int displayWidth, int displayHeight, float percent)
    {
        var renderScale = Mathf.Clamp(percent, 25.0f, 100.0f) * 0.01f;
        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(displayWidth * renderScale)),
            Mathf.Max(1, Mathf.RoundToInt(displayHeight * renderScale)));
    }

    private void UpdateTextureFromCompute(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, Result, _outputTexture);
        shader.SetTexture(kernelHandle, AccumulationResult, _accumulationTexture);
        shader.SetTexture(kernelHandle, Beauty, _beautyTexture);
        
        var threadGroupsX = Mathf.CeilToInt(_textureSize.x / (float)RenderThreadCountX);
        var threadGroupsY = Mathf.CeilToInt(_textureSize.y / (float)RenderThreadCountY);
        
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }

    private void PresentFinalColor()
    {
        _spatialDenoisingManager.Present(_presentationSource ?? _beautyTexture, _presentationTexture, exposure);
    }

    private void PresentLinearTexture(RenderTexture source, RenderTexture destination)
    {
        _spatialDenoisingManager.Present(source, destination, exposure);
    }

    private void UpdateFeaturesFromCompute()
    {
        var kernelHandle = shader.FindKernel("CSFeatures");
        SetShaderParameters(kernelHandle);
        
        shader.SetTexture(kernelHandle, FeatureNormal, _featureNormalTexture);
        shader.SetTexture(kernelHandle, FeatureAlbedo, _featureAlbedoTexture);
        shader.SetTexture(kernelHandle, FeatureDepth, _featureDepthTexture);
        shader.SetTexture(kernelHandle, FeatureIdentity, _featureIdentityTexture);
        shader.SetTexture(kernelHandle, FeatureValidity, _featureValidityTexture);
        
        var threadGroupsX = Mathf.CeilToInt(_textureSize.x / (float)RenderThreadCountX);
        var threadGroupsY = Mathf.CeilToInt(_textureSize.y / (float)RenderThreadCountY);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }

    private void PresentFeatureDebugMode()
    {
        _spatialDenoisingManager.PresentFeatureDebug(debugRenderMode, _featureNormalTexture, _featureAlbedoTexture,
            _featureDepthTexture, _featureIdentityTexture, _featureValidityTexture, _outputTexture, _textureSize);
    }

    private bool IsFeatureDebugMode()
    {
        return debugRenderMode >= DebugRenderMode.FeatureNormal && debugRenderMode <= DebugRenderMode.FeatureValidity;
    }

    private void RunSpatialDenoiser(RenderTexture source = null, RenderTexture variance = null)
    {
        _presentationSource = _spatialDenoisingManager.Filter(source ?? _beautyTexture, variance, _featureNormalTexture,
            _featureAlbedoTexture, _featureDepthTexture, _featureIdentityTexture, _featureValidityTexture, debugRenderMode,
            _textureSize, enableCaustics, causticPreservationThreshold);

        if (debugRenderMode == DebugRenderMode.SpatialDenoised || debugRenderMode == DebugRenderMode.AtrousIteration1
                                                               || debugRenderMode == DebugRenderMode.AtrousIteration2 ||
                                                               debugRenderMode == DebugRenderMode.AtrousIteration3)
        {
            PresentLinearTexture(_presentationSource, _outputTexture);
        }
    }

    private void PresentCausticPreservationMask()
    {
        _spatialDenoisingManager.PresentCausticPreservationMask(_beautyTexture, _featureValidityTexture, _outputTexture,
            _textureSize, causticPreservationThreshold, enableCaustics);
    }

    internal void ResetFrameAccumulation()
    {
        _accumulatedFrameCount = 0;
        _hasAccumulationStateHash = false;
    }

    private void BuildCausticSamplingDistribution()
    {
        _causticsManager.BuildSamplingDistribution(Lighting.Lights, _spheres, _meshInfos, _triangles, WaterInternal);
    }
    
    private void CalculateCausticGridLayout()
    {
        var hasBounds = false;
        var boundsMin = Vector3.zero;
        var boundsMax = Vector3.zero;
        
        for (var i = 0; i < _spheres.Count; i++)
        {
            var radius = Vector3.one * _spheres[i].radius;
            CausticsLogic.EncapsulateCausticBounds(_spheres[i].position - radius, _spheres[i].position + radius,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }
        
        for (var i = 0; i < _meshInfos.Count; i++)
        {
            CausticsLogic.EncapsulateCausticBounds(_meshInfos[i].boundsMin, _meshInfos[i].boundsMax,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }
        
        if (WaterManager.TryGetCausticBounds(out Vector3 waterBoundsMin, out Vector3 waterBoundsMax))
        {
            CausticsLogic.EncapsulateCausticBounds(
                waterBoundsMin,
                waterBoundsMax,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }

        if (!hasBounds)
        {
            boundsMin = new Vector3(-10.0f, -1.0f, -10.0f);
            boundsMax = new Vector3(10.0f, 10.0f, 10.0f);
        }

        _causticsManager.ConfigureGrid(boundsMin, boundsMax, Caustics.GatherRadius, MaxCausticGridCells);
    }
    

    private void ReleaseCausticResources()
    {
        _causticsManager.ReleaseResources();
    }

    private void UpdateCausticPhotonMap()
    {
        if (!enableCaustics)
        {
            if (_causticsManager.PreviousEnabled || _causticsManager.HasResources)
            {
                ReleaseCausticResources();
                ResetFrameAccumulation();
            }
            _causticsManager.PreviousEnabled = false;
            return;
        }

        var stateHash = CalculatePhotonStateHash();
        var stateChanged = !_causticsManager.HasPhotonStateHash || stateHash != _causticsManager.PhotonStateHash;
        if (stateChanged)
        {
            _causticsManager.BuildSamplingDistribution(Lighting.Lights, _spheres, _meshInfos, _triangles, WaterInternal);
        }
        
        CalculateCausticGridLayout();
        _causticsManager.EnsureResources(Caustics.PhotonCount);
        if (stateChanged)
        {
            _causticsManager.UploadSamplingDistribution();
            _causticsManager.PhotonStateHash = stateHash;
            _causticsManager.HasPhotonStateHash = true;
            _causticsManager.FrameIndex = 0;
            ResetFrameAccumulation();
        }
        else if (!ShouldUseFrameAccumulation())
        {
            _causticsManager.PreviousEnabled = true;
            return;
        }

        shader.EnableKeyword("CAUSTICS_ENABLED");
        var clearKernel = shader.FindKernel("ClearCausticPhotons");
        var traceKernel = shader.FindKernel("TraceCausticPhotons");
        var clearGridKernel = shader.FindKernel("ClearCausticGrid");
        var buildGridKernel = shader.FindKernel("BuildCausticGrid");
        
        SetPhotonTraceSceneParameters(traceKernel);
        _causticsManager.SetShaderParameters(shader, clearKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, traceKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, clearGridKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, buildGridKernel, numBounces);
        
        SetSceneBuffers(traceKernel);
        
        shader.Dispatch(clearKernel, 1, 1, 1);
        shader.Dispatch(traceKernel, Mathf.CeilToInt(Mathf.Max(1, Caustics.PhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        shader.Dispatch(clearGridKernel, Mathf.Max(1,
            Mathf.CeilToInt(_causticsManager.GridCellCount / (float)CausticTraceThreadCount)), 1, 1);
        shader.Dispatch(buildGridKernel, Mathf.CeilToInt(Mathf.Max(1, Caustics.PhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        
        RequestCausticMetadataReadback();
        _causticsManager.DispatchCountValue++;
        
        if (ShouldUseFrameAccumulation())
        {
            _causticsManager.FrameIndex = _causticsManager.FrameIndex == int.MaxValue ? 0 : _causticsManager.FrameIndex + 1;
        }
        _causticsManager.PreviousEnabled = true;
    }

    private void RequestCausticMetadataReadback()
    {
        _causticsManager.RequestMetadataReadback();
    }

    private void SetPhotonTraceSceneParameters(int traceKernel)
    {
        EnsureMeshTextureArrays();
        
        shader.SetTexture(traceKernel, MeshAlbedoTextures, _meshAlbedoTextureArray);
        shader.SetTexture(traceKernel, MeshMetallicRoughnessTextures, _meshMetallicRoughnessTextureArray);
        shader.SetTexture(traceKernel, MeshNormalTextures, _meshNormalTextureArray);
        shader.SetTexture(traceKernel, MeshParallaxTextures, _meshParallaxTextureArray);
        
        shader.SetInt(NumSpheres, _spheres.Count);
        shader.SetInt(NumTriangles, _triangles.Count);
        shader.SetInt(NumMeshes, _meshInfos.Count);
        
        Lighting.SetShaderLightCount(shader);
        
        _sceneBvhs.SetShaderParameters(shader);
        
        WaterManager.SetShaderParameters(shader, Application.isPlaying ? GetRenderTime() : 0.0f);
        
        SetTerrainShaderParameters(traceKernel);
    }

    private bool ShouldUseFrameAccumulation()
    {
        var animatedWater = WaterManager.IsAnimated && !_singleFrame;
        
        return enableFrameAccumulation && 
               debugRenderMode == DebugRenderMode.FinalColor && 
               !animatedWater && 
               !ShouldUseTemporalAccumulation();
    }

    private float GetRenderTime()
    {
        return _singleFrame ? _singleFrameRenderTime : Time.time;
    }

    public void RenderImage(RenderTexture src, RenderTexture dest)
    {
        EnsureOutputTextureSize(src.width, src.height);
        
        if (TryPresentStartupFrame(src, dest) || TryDeferShaderVariantWarmup(dest, out var frame))
        {
            return;
        }
        
        _videoCaptureManager.PrepareRender();
        PrepareRenderFrame(ref frame);
        DispatchRenderFrame(ref frame);
        FinalizeRenderFrame(ref frame);
        _videoCaptureManager.CompleteRender();
        
        Graphics.Blit(debugRenderMode == DebugRenderMode.FinalColor && !frame.useDedicatedCausticsDebugKernel
            ? _presentationTexture : _outputTexture, dest);
    }

    private struct RenderFrame
    {
        public bool fogEnabled;
        public bool useDedicatedCausticsDebugKernel;
        public int requestedVariant;
        public bool useFrameAccumulation;
        public int kernelHandle;
        public long preparationStart;
    }

    private bool TryPresentStartupFrame(RenderTexture src, RenderTexture dest)
    {
        if (!_startupInitializationPending) return false;
        Graphics.Blit(src, dest);
        return true;
    }

    private bool TryDeferShaderVariantWarmup(RenderTexture dest, out RenderFrame frame)
    {
        frame = new RenderFrame
        {
            fogEnabled = IsFogEnabled(),
            useDedicatedCausticsDebugKernel = enableCaustics && debugRenderMode == DebugRenderMode.Caustics
        };
        frame.requestedVariant = GetShaderVariantKey(debugRenderMode, enableCaustics, frame.fogEnabled);
        
        var changed = debugRenderMode != _appliedDebugRenderMode || enableCaustics != _appliedCausticsEnabled || frame.fogEnabled != _appliedFogEnabled;
        
        if (_pendingVariantWarmup || !changed || _warmedShaderVariants.Contains(frame.requestedVariant)) return false;
        
        _pendingVariantWarmup = true;
        
        Graphics.Blit(_presentationTexture != null ? _presentationTexture : _outputTexture, dest);
        return true;
    }

    private void PrepareRenderFrame(ref RenderFrame frame)
    {
        // Runtime glTF imports can register meshes after Update has processed the normal deferred
        // rebuild. Resize buffers before their first render-frame upload.
        if (_buffersNeedRebuilding)
        {
            RebuildBuffers();
        }

        if (!ShouldRunTemporalDenoiser() && _temporalDenoisingManager.HasResources)
        {
            ReleaseTemporalDenoiserResources(); 
            _temporalDenoisingManager.ResetHistory();
        }
        frame.preparationStart = _startupProfilePending ? Stopwatch.GetTimestamp() : 0;
        
        CameraManager.UpdateTrackedFocusPoint();
        _temporalDenoisingManager.SetDynamicSceneChanged(false);
        UpdateSpheres(); 
        UpdateTriangles(); 
        UpdateSceneBvhs();
        CameraManager.UpdateAutoFocus(numberOfPasses, WaterManager.CalculateAutoFocusStateHash(), GetNearestIntersectionDistanceForAutoFocus);
        CameraManager.AutoFocusSceneChanged = false;
        UpdateCausticPhotonMap();
        
        if (ShouldRunTemporalDenoiser())
        {
            _temporalDenoisingManager.PrepareCameraState();
        }
        
        frame.useFrameAccumulation = ShouldUseFrameAccumulation();
        
        if (frame.useFrameAccumulation)
        {
            var stateHash = CalculateAccumulationStateHash();
            if (!_hasAccumulationStateHash || stateHash != _accumulationStateHash)
            {
                _accumulatedFrameCount = 0; 
                _accumulationStateHash = stateHash; 
                _hasAccumulationStateHash = true;
            }
        }
        else
        {
            ResetFrameAccumulation();
        }
        
        frame.kernelHandle = shader.FindKernel(frame.useDedicatedCausticsDebugKernel ? "CSCausticsDebug" : "CSMain");
    }

    private void DispatchRenderFrame(ref RenderFrame frame)
    {
        SetShaderParameters(frame.kernelHandle);
        CameraManager.DispatchPendingFocusQuery(shader, SetShaderParameters, ResetFrameAccumulation);
        if (_startupProfilePending)
        {
            AddStartupProfilePhase("first-frame CPU preparation", frame.preparationStart);
        }
        
        var dispatchStart = _startupProfilePending ? Stopwatch.GetTimestamp() : 0;
        UpdateTextureFromCompute(frame.kernelHandle);
        _presentationSource = _beautyTexture;
        
        if (!frame.useDedicatedCausticsDebugKernel && (ShouldRunSpatialDenoiser() || ShouldRunTemporalDenoiser() || IsFeatureDebugMode() || IsCausticPreservationDebugMode()))
        {
            UpdateFeaturesFromCompute();
        }
        
        if (!frame.useDedicatedCausticsDebugKernel && IsFeatureDebugMode())
        {
            PresentFeatureDebugMode();
        }

        if (!frame.useDedicatedCausticsDebugKernel && ShouldRunTemporalDenoiser())
        {
            _temporalDenoisingManager.Run(debugRenderMode); 
            _temporalDenoisingManager.CommitCameraState();
        }
        else if (!frame.useDedicatedCausticsDebugKernel && IsCausticPreservationDebugMode())
        {
            PresentCausticPreservationMask();
        }
        
        if (!frame.useDedicatedCausticsDebugKernel && ShouldRunSpatialDenoiser() && !IsTemporalDebugMode() && !ShouldUseTemporalAccumulation())
        {
            RunSpatialDenoiser();
        }
        
        if (!frame.useDedicatedCausticsDebugKernel && debugRenderMode == DebugRenderMode.FinalColor)
        {
            PresentFinalColor();
        }

        if (_startupProfilePending)
        {
            AddStartupProfilePhase("first compute dispatch (includes shader compilation)", dispatchStart); LogStartupProfile();
        }
    }

    private void FinalizeRenderFrame(ref RenderFrame frame)
    {
        _renderedFrameCount++;
        if (frame.useFrameAccumulation && !frame.useDedicatedCausticsDebugKernel)
        {
            _accumulatedFrameCount++;
        }
        _temporalDenoisingManager.CommitRenderedCameraState();
        _warmedShaderVariants.Add(frame.requestedVariant);
        _appliedDebugRenderMode = debugRenderMode;
        _appliedCausticsEnabled = enableCaustics;
        _appliedFogEnabled = frame.fogEnabled;
        _pendingVariantWarmup = false;
    }

    public void ExportCurrentRenderPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An output image path is required.", nameof(path));
        }
        if (_outputTexture == null)
        {
            throw new InvalidOperationException("The ray tracer has not rendered an image yet.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.WriteAllBytes(path, EncodeCurrentOutputPng());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not export the current render to '{path}'.", exception);
        }
    }

    internal byte[] EncodeCurrentOutputPng()
    {
        var width = Mathf.Max(1, _displayTextureSize.x);
        var height = Mathf.Max(1, _displayTextureSize.y);
        var presentation = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        
        try
        {
            var currentOutput = debugRenderMode == DebugRenderMode.FinalColor
                ? _presentationTexture ?? _outputTexture
                : _outputTexture;
            Graphics.Blit(currentOutput, presentation);
            RenderTexture.active = presentation;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            texture.Apply(false, false);
            return texture.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(presentation);
            DestroyRuntimeObject(texture);
        }
    }

    private static int GetShaderVariantKey(DebugRenderMode mode, bool causticsEnabled, bool fogEnabled)
    {
        return ((int)mode << 2) | (causticsEnabled ? 2 : 0) | (fogEnabled ? 1 : 0);
    }

    private bool IsFogEnabled()
    {
        return enableVolumetricFog
            && _fogVolume != null
            && _fogVolume.Density > 0.0f
            && fogDensityScale > 0.0f;
    }

    private void UpdateSpheres()
    {
        var spheresChanged = false;
        var sphereBoundsChanged = false;
        var sphereTexturesChanged = false;
        var lightsChanged = false;
        var lightBoundsChanged = false;
        for (var i = 0; i < _spheres.Count; ++i)
        {
            var sphere = _spheres[i];
            var previousSphere = sphere;
            var sphereObject = _sphereObjects[i];

            var position = sphereObject.transform.TransformPoint(sphereObject.collider.center);
            var radius = GetWorldSphereRadius(sphereObject.collider, sphereObject.transform);
            var boundsChanged = sphere.position != position || !Mathf.Approximately(sphere.radius, radius);
            if (boundsChanged) _temporalDenoisingManager.MarkDynamicSceneChanged();
            sphereBoundsChanged |= boundsChanged;
            sphere.position = position;
            sphere.radius = radius;

            var material = sphereObject.material;
            var previousTextureIndex = sphere.textureIndex;
            var previousNormalTextureIndex = sphere.normalTextureIndex;
            var previousParallaxTextureIndex = sphere.parallaxTextureIndex;
            
            sphere.color = material.Color.ToVector3();
            sphere.refraction = material.RefractionIndex;
            sphere.opacity = material.Opacity;
            sphere.smoothness = material.Smoothness;
            sphere.specular = material.Specular;
            sphere.transmission = material.Transmission;
            sphere.materialType = (int)material.Type;
            sphere.textureIndex = GetMeshTextureIndex(material.AlbedoTexture, _meshAlbedoTextures);
            sphere.normalTextureIndex = GetMeshTextureIndex(material.NormalTexture, _meshNormalTextures);
            sphere.parallaxTextureIndex = GetMeshTextureIndex(material.ParallaxTexture, _meshParallaxTextures);
            sphere.textureUvScale = material.TextureUvScale;
            sphere.parallaxStrength = material.ParallaxStrength;
            sphere.minimumParallaxStrength = Mathf.Min(material.MinimumParallaxStrength, material.ParallaxStrength);
            sphereTexturesChanged |= previousTextureIndex != sphere.textureIndex
                || previousNormalTextureIndex != sphere.normalTextureIndex
                || previousParallaxTextureIndex != sphere.parallaxTextureIndex;
            spheresChanged |= !sphere.Equals(previousSphere);

            _spheres[i] = sphere;
        }
        _lightingManager.SetTransparentSphereBlockers(false);
        for (var i = 0; i < _spheres.Count; i++)
        {
            if (_spheres[i].opacity < 1.0f)
            {
                _lightingManager.SetTransparentSphereBlockers(true);
                break;
            }
        }

        _lightingManager.UpdateSphereLights(out bool sphereLightDataChanged,
            out bool sphereLightBoundsChanged);
        lightsChanged |= sphereLightDataChanged;
        lightBoundsChanged |= sphereLightBoundsChanged;
        if (sphereLightDataChanged || sphereLightBoundsChanged) _temporalDenoisingManager.MarkDynamicSceneChanged();

        bool directionalLightsChanged = _lightingManager.UpdateDirectionalLights(out bool directionalBoundsChanged);
        if (directionalLightsChanged) _temporalDenoisingManager.MarkDynamicSceneChanged();
        lightsChanged |= directionalLightsChanged;
        lightBoundsChanged |= directionalBoundsChanged;

        if (sphereBoundsChanged)
        {
            _sceneBvhs.MarkAllDirty();
        }

        if (spheresChanged)
        {
            if (_sphereBuffer != null && _spheres.Count > 0)
            {
                _sphereBuffer.SetData(_spheres);
            }
        }

        if (sphereTexturesChanged)
        {
            RebuildMeshTextureArrays();
        }

        if (lightsChanged)
        {
            _lightingManager.UploadLightData();
        }

        if (lightBoundsChanged)
        {
            _sceneBvhs.MarkTopLevelDirty();
        }

        CameraManager.AutoFocusSceneChanged |= spheresChanged || lightsChanged;
    }

    private void UpdateTriangles()
    {
        if (_meshObjects.Count == 0)
        {
            _lightingManager.SetTransparentMeshBlockers(false);
            return;
        }

        UpdateMeshChangeCache(out bool geometryChanged, out bool materialChanged);
        if (geometryChanged || materialChanged) _temporalDenoisingManager.MarkDynamicSceneChanged();
        CameraManager.AutoFocusSceneChanged |= geometryChanged || materialChanged;
        if (!geometryChanged && !materialChanged)
        {
            return;
        }

        if (geometryChanged)
        {
            RebuildTriangleData();
            _sceneBvhs.MarkAllDirty();
        }
        else
        {
            RefreshTriangleMaterials();
        }

        if (_triangleBuffer != null && _triangles.Count > 0)
        {
            _triangleBuffer.SetData(_triangles);
        }

        if (geometryChanged && _meshBuffer != null && _meshInfos.Count > 0)
        {
            _meshBuffer.SetData(_meshInfos);
        }

        if (geometryChanged && _bvhNodeBuffer != null && _bvhNodes.Count > 0)
        {
            _bvhNodeBuffer.SetData(_bvhNodes);
        }

        // Mesh emitters share the light buffer with sphere and directional lights. Refresh it
        // after a mesh material edit, which may have changed mesh-light emission.
        if (geometryChanged || materialChanged)
        {
            _lightingManager.UploadLightData();
        }
        if (geometryChanged)
        {
            _lightingManager.UploadMeshLightTriangleCdf();
        }

    }

    private void UpdateSceneBvhs()
    {
        _sceneBvhs.Update(_spheres, Lighting.Lights, _meshInfos, topLevelBvhMinObjectCount, shadowBvhMinObjectCount, SceneBvhManager.StackSize);
    }

    private void UpdateMeshChangeCache(out bool geometryChanged, out bool materialChanged)
    {
        geometryChanged = false;
        materialChanged = false;
        _lightingManager.SetTransparentMeshBlockers(false);

        for (int i = 0; i < _meshObjects.Count; i++)
        {
            var meshObject = _meshObjects[i];
            var localToWorld = meshObject.transform.localToWorldMatrix;
            var snapshot = RayMaterialSnapshot.Create(meshObject.material, meshObject.light);

            if (snapshot.opacity < ShadowBlockerOpaqueThreshold)
            {
                _lightingManager.SetTransparentMeshBlockers(true);
            }

            bool meshGeometryChanged = meshObject.previousLocalToWorld != localToWorld
                || meshObject.previousInterpolateNormals != snapshot.interpolateNormals;
            bool meshMaterialChanged = snapshot.DiffersFrom(meshObject);

            geometryChanged |= meshGeometryChanged;
            materialChanged |= meshMaterialChanged;

            if (!meshGeometryChanged && !meshMaterialChanged)
            {
                continue;
            }

            meshObject.previousLocalToWorld = localToWorld;
            snapshot.StoreIn(ref meshObject);
            _meshObjects[i] = meshObject;
        }
    }

    private void RefreshTriangleMaterials()
    {
        _meshAlbedoTextures.Clear();
        _meshMetallicRoughnessTextures.Clear();
        _meshNormalTextures.Clear();
        _meshParallaxTextures.Clear();

        for (int meshIndex = 0; meshIndex < _meshObjects.Count; meshIndex++)
        {
            var meshObject = _meshObjects[meshIndex];
            var snapshot = RayMaterialSnapshot.Create(meshObject.material, meshObject.light);
            bool isLight = meshObject.light != null;
            int textureIndex = GetMeshAlbedoTextureIndex(snapshot.albedoTexture);
            int metallicRoughnessTextureIndex = GetMeshTextureIndex(snapshot.metallicRoughnessTexture, _meshMetallicRoughnessTextures);
            int normalTextureIndex = GetMeshTextureIndex(snapshot.normalTexture, _meshNormalTextures);
            int parallaxTextureIndex = GetMeshTextureIndex(snapshot.parallaxTexture, _meshParallaxTextures);

            int triangleStart = 0;
            int triangleEnd = 0;
            int lightIndex = -1;
            for (int infoIndex = 0; infoIndex < _meshInfos.Count; infoIndex++)
            {
                if (_meshInfos[infoIndex].meshIndex != meshIndex)
                {
                    continue;
                }

                triangleStart = _meshInfos[infoIndex].triangleStart;
                triangleEnd = triangleStart + _meshInfos[infoIndex].triangleCount;
                lightIndex = _meshInfos[infoIndex].lightIndex;
                break;
            }

            for (int triangleIndex = triangleStart; triangleIndex < triangleEnd; triangleIndex++)
            {
                var triangle = _triangles[triangleIndex];
                snapshot.ApplyTo(ref triangle);
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                triangle.parallaxTextureIndex = parallaxTextureIndex;
                _triangles[triangleIndex] = triangle;

            }

            if (isLight && lightIndex >= 0 && lightIndex < Lighting.LightCount)
            {
                _lightingManager.UpdateMeshLightEmission(lightIndex, snapshot.emission);
            }
        }

        RebuildMeshTextureArrays();
    }

    private void RebuildTriangleData()
    {
        _triangles.Clear();
        _meshInfos.Clear();
        _bvhNodes.Clear();
        _meshAlbedoTextures.Clear();
        _meshMetallicRoughnessTextures.Clear();
        _meshNormalTextures.Clear();
        _meshParallaxTextures.Clear();
        for (int sphereIndex = 0; sphereIndex < _sphereObjects.Count; sphereIndex++)
        {
            var sphereMaterial = _sphereObjects[sphereIndex].material;
            var sphere = _spheres[sphereIndex];
            sphere.textureIndex = sphereMaterial != null
                ? GetMeshTextureIndex(sphereMaterial.AlbedoTexture, _meshAlbedoTextures)
                : -1;
            sphere.normalTextureIndex = sphereMaterial != null
                ? GetMeshTextureIndex(sphereMaterial.NormalTexture, _meshNormalTextures)
                : -1;
            sphere.parallaxTextureIndex = sphereMaterial != null
                ? GetMeshTextureIndex(sphereMaterial.ParallaxTexture, _meshParallaxTextures)
                : -1;
            sphere.textureUvScale = sphereMaterial != null ? sphereMaterial.TextureUvScale : Vector2.one;
            _spheres[sphereIndex] = sphere;
        }
        _lightingManager.RemoveMeshLights();

        for (int meshIndex = 0; meshIndex < _meshObjects.Count; meshIndex++)
        {
            var meshObject = _meshObjects[meshIndex];
            var mesh = meshObject.mesh;
            if (mesh == null)
            {
                continue;
            }

            var localToWorld = meshObject.transform.localToWorldMatrix;
            var normalToWorld = localToWorld.inverse.transpose;
            var snapshot = RayMaterialSnapshot.Create(meshObject.material, meshObject.light);
            bool isLight = meshObject.light != null;
            int textureIndex = GetMeshAlbedoTextureIndex(snapshot.albedoTexture);
            int metallicRoughnessTextureIndex = GetMeshTextureIndex(snapshot.metallicRoughnessTexture, _meshMetallicRoughnessTextures);
            int normalTextureIndex = GetMeshTextureIndex(snapshot.normalTexture, _meshNormalTextures);
            int parallaxTextureIndex = GetMeshTextureIndex(snapshot.parallaxTexture, _meshParallaxTextures);
            bool interpolateNormals = snapshot.interpolateNormals;
            MeshBvhTemplate template = GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
            int triangleStart = _triangles.Count;
            int nodeStart = _bvhNodes.Count;
            int lightIndex = -1;
            float totalLightArea = 0.0f;
            Vector3 areaWeightedLightPosition = Vector3.zero;

            for (int i = 0; i < template.triangles.Count; i++)
            {
                Triangle triangle = TransformTemplateTriangle(template.triangles[i], localToWorld, normalToWorld);
                snapshot.ApplyTo(ref triangle);
                triangle.meshIndex = meshIndex;
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                triangle.parallaxTextureIndex = parallaxTextureIndex;
                triangle.interpolateNormals = interpolateNormals ? 1 : 0;
                triangle.lightIndex = lightIndex;
                _triangles.Add(triangle);

                if (isLight)
                {
                    float area = 0.5f * Vector3.Cross(
                        triangle.vertex1 - triangle.vertex0,
                        triangle.vertex2 - triangle.vertex0).magnitude;
                    totalLightArea += area;
                    areaWeightedLightPosition += (triangle.vertex0 + triangle.vertex1 + triangle.vertex2) * (area / 3.0f);
                }
            }

            if (template.triangles.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < template.nodes.Count; i++)
            {
                BvhNode node = template.nodes[i];
                TransformBounds(node.boundsMin, node.boundsMax, localToWorld, out node.boundsMin, out node.boundsMax);
                if (node.leftChildIndex >= 0) node.leftChildIndex += nodeStart;
                if (node.rightChildIndex >= 0) node.rightChildIndex += nodeStart;
                if (node.triangleStart >= 0) node.triangleStart += triangleStart;
                _bvhNodes.Add(node);
            }

            int rootNodeIndex = nodeStart;
            bool hasMeshLight = isLight && totalLightArea > 0.000001f;
            if (hasMeshLight)
            {
                lightIndex = Lighting.LightCount;
            }
            for (int triangleIndex = triangleStart; triangleIndex < _triangles.Count; triangleIndex++)
            {
                Triangle triangle = _triangles[triangleIndex];
                triangle.lightIndex = hasMeshLight ? lightIndex : -1;
                _triangles[triangleIndex] = triangle;
            }

            _meshInfos.Add(new MeshInfo
            {
                boundsMin = _bvhNodes[rootNodeIndex].boundsMin,
                rootNodeIndex = rootNodeIndex,
                boundsMax = _bvhNodes[rootNodeIndex].boundsMax,
                triangleStart = triangleStart,
                triangleCount = _triangles.Count - triangleStart,
                meshIndex = meshIndex,
                isLight = hasMeshLight ? 1 : 0,
                lightIndex = hasMeshLight ? lightIndex : -1
            });

            if (hasMeshLight)
            {
                _lightingManager.AddMeshLight(_triangles, triangleStart, _triangles.Count - triangleStart,
                    areaWeightedLightPosition / totalLightArea, totalLightArea, snapshot.emission, out _);
            }
        }

        _lightingManager.RebuildMeshLightTriangleCdf(_triangles, _meshInfos);
        RebuildMeshTextureArrays();
    }

    private MeshBvhTemplate GetOrBuildMeshBvhTemplate(Mesh mesh, bool interpolateNormals)
    {
        long start = Stopwatch.GetTimestamp();
        MeshBvhTemplate template = _meshBvhTemplates.GetOrBuild(mesh, interpolateNormals, SceneBvhManager.LeafTriangleCount,
            SceneBvhManager.StackSize, SceneBvhManager.BoundsPadding, out bool wasBuilt);
        if (wasBuilt)
        {
            _profileBuiltMeshTemplateCount++;
            _profileBuiltMeshTemplateTicks += Stopwatch.GetTimestamp() - start;
        }
        return template;
    }

    private void TryLoadBakedMeshBvhs()
    {
        _loadedBakedMeshBvhs = false;

        var bvhBake = GetEditorBvhBake();

        if (bvhBake == null)
        {
            _bvhBakeLoadStatus = "no bake assigned";
            return;
        }
        
        if (bvhBake.formatVersion != SceneBvhManager.BakeFormatVersion)
        {
            _bvhBakeLoadStatus = "format is out-of-date";
            return;
        }
        
        if (string.IsNullOrEmpty(bvhBake.streamingAssetsRelativePath))
        {
            _bvhBakeLoadStatus = "binary path is missing";
            return;
        }

#if UNITY_EDITOR
        if (!IsEditorBvhBakeCurrent())
        {
            return;
        }
#endif

        var path = Path.Combine(Application.streamingAssetsPath, bvhBake.streamingAssetsRelativePath);
        if (!File.Exists(path))
        {
            _bvhBakeLoadStatus = "binary file is missing";
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            
            var loadedTemplates = MeshBvhBakeSerializer.Read(reader, SceneBvhManager.BakeFormatVersion,
                bvhBake.meshes.Count);
            
            if (loadedTemplates == null)
            {
                _bvhBakeLoadStatus = "binary header or mesh count is invalid";
                return;
            }

            for (var meshIndex = 0; meshIndex < loadedTemplates.Count; meshIndex++)
            {
                var entry = bvhBake.meshes[meshIndex];
                var template = loadedTemplates[meshIndex];
                
                if (entry.mesh == null
                    || entry.mesh.vertexCount != entry.vertexCount
                    || GetMeshIndexCount(entry.mesh) != entry.indexCount
                    || template.triangles.Count != entry.triangleCount
                    || template.nodes.Count != entry.nodeCount)
                {
                    _bvhBakeLoadStatus = $"mesh metadata mismatch at entry {meshIndex}";
                    return;
                }
            }

            if (stream.Position != stream.Length)
            {
                _bvhBakeLoadStatus = "binary has unexpected trailing data";
                return;
            }

            _meshBvhTemplates.Clear();
            foreach (var meshObject in _meshObjects)
            {
                var interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
                var bakeIndex = FindBakedMeshEntry(meshObject.mesh, interpolateNormals);
                if (bakeIndex < 0)
                {
                    _meshBvhTemplates.Clear();
                    _bvhBakeLoadStatus = $"bake is out-of-date: no template for {meshObject.mesh.name}";
                    return;
                }

                _meshBvhTemplates.Set(meshObject.mesh, interpolateNormals, loadedTemplates[bakeIndex]);
            }
            _loadedBakedMeshBvhs = true;
            _bvhBakeLoadStatus = $"loaded {loadedTemplates.Count:N0} baked templates for {_meshBvhTemplates.Count:N0} runtime meshes";
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Could not load baked ray tracing BVH data from '{path}': {exception.Message}", this);
            _meshBvhTemplates.Clear();
            _bvhBakeLoadStatus = $"load failed: {exception.GetType().Name}";
        }
    }

    private int FindBakedMeshEntry(Mesh mesh, bool interpolateNormals)
    {
#if UNITY_EDITOR
        var bvhBake = GetEditorBvhBake();
        var identity = GetEditorMeshIdentity(mesh);
#else
        return -1;
#endif
        
        for (var i = 0; i < bvhBake.meshes.Count; i++)
        {
            var entry = bvhBake.meshes[i];
            if (entry.interpolateNormals != interpolateNormals)
            {
                continue;
            }
#if UNITY_EDITOR
            if (entry.meshIdentity == identity)
#else
            if (entry.mesh == mesh)
#endif
            {
                return i;
            }
        }
        return -1;
    }

    private static int GetMeshIndexCount(Mesh mesh)
    {
        var count = 0;
        for (var i = 0; i < mesh.subMeshCount; i++)
        {
            count += checked((int)mesh.GetIndexCount(i));
        }
        return count;
    }

    private static Triangle TransformTemplateTriangle(Triangle triangle, Matrix4x4 localToWorld, Matrix4x4 normalToWorld)
    {
        triangle.vertex0 = localToWorld.MultiplyPoint3x4(triangle.vertex0);
        triangle.vertex1 = localToWorld.MultiplyPoint3x4(triangle.vertex1);
        triangle.vertex2 = localToWorld.MultiplyPoint3x4(triangle.vertex2);
        
        triangle.normal = Vector3.Cross(triangle.vertex1 - triangle.vertex0, triangle.vertex2 - triangle.vertex0).normalized;
        
        if (triangle.interpolateNormals != 0)
        {
            triangle.normal0 = normalToWorld.MultiplyVector(triangle.normal0).normalized;
            triangle.normal1 = normalToWorld.MultiplyVector(triangle.normal1).normalized;
            triangle.normal2 = normalToWorld.MultiplyVector(triangle.normal2).normalized;
        }
        else
        {
            triangle.normal0 = triangle.normal;
            triangle.normal1 = triangle.normal;
            triangle.normal2 = triangle.normal;
        }
        
        triangle.tangent0 = TransformTangent(triangle.tangent0, triangle.normal0, localToWorld);
        triangle.tangent1 = TransformTangent(triangle.tangent1, triangle.normal1, localToWorld);
        triangle.tangent2 = TransformTangent(triangle.tangent2, triangle.normal2, localToWorld);
        return triangle;
    }

    private static void TransformBounds(Vector3 sourceMin, Vector3 sourceMax, Matrix4x4 matrix, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        var center = (sourceMin + sourceMax) * 0.5f;
        var extents = (sourceMax - sourceMin) * 0.5f;
        var worldCenter = matrix.MultiplyPoint3x4(center);
        boundsMin = worldCenter;
        boundsMax = worldCenter;
        
        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var corner = matrix.MultiplyPoint3x4(center + Vector3.Scale(extents, new Vector3(x, y, z)));
                    boundsMin = Vector3.Min(boundsMin, corner);
                    boundsMax = Vector3.Max(boundsMax, corner);
                }
            }
        }

        // glTF nodes often carry a 0.01 unit-conversion scale. Keep a minimum world-space
        // tolerance so floating-point error cannot discard thin or nearly planar BVH leaves.
        Vector3 padding = Vector3.one * SceneBvhManager.BoundsPadding;
        boundsMin -= padding;
        boundsMax += padding;
    }

    private int GetMeshAlbedoTextureIndex(Texture2D texture)
    {
        return GetMeshTextureIndex(texture, _meshAlbedoTextures);
    }

    private static int GetMeshTextureIndex(Texture2D texture, List<Texture2D> textures)
    {
        if (texture == null)
        {
            return -1;
        }

        var existingIndex = textures.IndexOf(texture);
        if (existingIndex >= 0)
        {
            return existingIndex;
        }

        textures.Add(texture);
        return textures.Count - 1;
    }

    private static float GetEffectiveMetallic(RayMaterial material)
    {
        return material.Type == RayMaterial.MaterialType.Metal && Mathf.Approximately(material.Metallic, 0.0f)
            ? 1.0f
            : Mathf.Clamp01(material.Metallic);
    }

    private static Vector4 TransformTangent(Vector4 tangent, Vector3 normal, Matrix4x4 localToWorld)
    {
        var direction = localToWorld.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z));
        direction = Vector3.ProjectOnPlane(direction, normal).normalized;
        return new Vector4(direction.x, direction.y, direction.z, tangent.w);
    }

    private void RebuildMeshTextureArrays()
    {
        var start = Stopwatch.GetTimestamp();
        EnsureDefaultMeshTextures();
        DestroyRuntimeTextureArrays();
        _meshAlbedoTextureArray = BuildMeshTextureArray(_meshAlbedoTextures, defaultMeshAlbedoTexture, "Ray Tracing Mesh Albedo Texture Array", Color.white, false);
        _meshMetallicRoughnessTextureArray = BuildMeshTextureArray(_meshMetallicRoughnessTextures, defaultMeshMetallicRoughnessTexture, "Ray Tracing Mesh Metallic Roughness Texture Array", Color.white, true);
        _meshNormalTextureArray = BuildMeshTextureArray(_meshNormalTextures, defaultMeshNormalTexture, "Ray Tracing Mesh Normal Texture Array", new Color(0.5f, 0.5f, 1.0f, 1.0f), true);
        _meshParallaxTextureArray = BuildMeshTextureArray(_meshParallaxTextures, null, "Ray Tracing Mesh Parallax Texture Array", Color.black, true);
        _profileTextureArrayTicks += Stopwatch.GetTimestamp() - start;
    }

    private void EnsureDefaultMeshTextures()
    {
        defaultMeshAlbedoTexture = EnsureDefaultMeshTexture(defaultMeshAlbedoTexture, "Default Mesh Albedo", Color.white);
        defaultMeshMetallicRoughnessTexture = EnsureDefaultMeshTexture(defaultMeshMetallicRoughnessTexture, "Default Mesh Metallic Roughness", Color.white);
        defaultMeshNormalTexture = EnsureDefaultMeshTexture(defaultMeshNormalTexture, "Default Mesh Normal", new Color(0.5f, 0.5f, 1.0f, 1.0f));
    }

    private static Texture2D EnsureDefaultMeshTexture(Texture2D texture, string textureName, Color color)
    {
        if (texture != null)
        {
            return texture;
        }

        texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
        {
            name = textureName,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        texture.SetPixel(0, 0, color);
        texture.Apply(false, true);
        return texture;
    }

    private static Texture2DArray BuildMeshTextureArray(List<Texture2D> textures, Texture2D fallback, string arrayName, Color fallbackColor, bool linear)
    {
        return MeshTextureArrayBuilder.Build(textures, fallback, arrayName, fallbackColor, linear);
    }

    private static bool IntersectAabb(Ray ray, Vector3 boundsMin, Vector3 boundsMax, float maxDistance)
    {
        var inverseDirection = new Vector3(
            1.0f / GetSafeDirectionComponent(ray.direction.x),
            1.0f / GetSafeDirectionComponent(ray.direction.y),
            1.0f / GetSafeDirectionComponent(ray.direction.z));

        var t0 = Vector3.Scale(boundsMin - ray.origin, inverseDirection);
        var t1 = Vector3.Scale(boundsMax - ray.origin, inverseDirection);
        var tMin3 = Vector3.Min(t0, t1);
        var tMax3 = Vector3.Max(t0, t1);
        var tMin = Mathf.Max(tMin3.x, Mathf.Max(tMin3.y, tMin3.z));
        var tMax = Mathf.Min(tMax3.x, Mathf.Min(tMax3.y, tMax3.z));

        return tMax >= Mathf.Max(0.0f, tMin) && tMin < maxDistance;
    }

    private static float GetSafeDirectionComponent(float value)
    {
        if (Mathf.Abs(value) >= 0.00000001f)
        {
            return value;
        }

        return value < 0.0f ? -0.00000001f : 0.00000001f;
    }

    private float GetNearestMeshIntersectionDistance(Ray ray, MeshInfo meshInfo, float nearestDistance)
    {
        if (meshInfo.triangleCount <= 0 || !IntersectAabb(ray, meshInfo.boundsMin, meshInfo.boundsMax, nearestDistance))
        {
            return nearestDistance;
        }

        var stack = new int[SceneBvhManager.StackSize];
        int stackCount = 0;
        stack[stackCount++] = meshInfo.rootNodeIndex;

        while (stackCount > 0)
        {
            var node = _bvhNodes[stack[--stackCount]];
            if (!IntersectAabb(ray, node.boundsMin, node.boundsMax, nearestDistance))
            {
                continue;
            }

            if (node.triangleCount > 0)
            {
                for (int i = 0; i < node.triangleCount; i++)
                {
                    var triangle = _triangles[node.triangleStart + i];
                    if (ShouldAutoFocusIgnoreObject(triangle.opacity))
                    {
                        continue;
                    }

                    var hitDistance = triangle.Intersect(ray.origin, ray.direction);
                    if (hitDistance >= 0.0f && hitDistance < nearestDistance)
                    {
                        nearestDistance = hitDistance;
                    }
                }

                continue;
            }

            if (node.leftChildIndex >= 0 && stackCount < SceneBvhManager.StackSize)
            {
                stack[stackCount++] = node.leftChildIndex;
            }

            if (node.rightChildIndex >= 0 && stackCount < SceneBvhManager.StackSize)
            {
                stack[stackCount++] = node.rightChildIndex;
            }
        }

        return nearestDistance;
    }

    private bool ShouldAutoFocusIgnoreObject(float opacity)
    {
        return opacity <= CameraManager.autoFocusTransparentOpacityThreshold;
    }

    public void RebuildBuffers(bool startupProfile = false)
    {
        var rebuildStart = Stopwatch.GetTimestamp();
        _profileBuiltMeshTemplateCount = 0;
        _profileBuiltMeshTemplateTicks = 0;
        _profileTextureArrayTicks = 0;
        _buffersNeedRebuilding = false;
        ResetFrameAccumulation();
        _sphereBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
        _sceneBvhs.Release();
        _lightingManager.ReleaseBuffers();
        _sphereBuffer = null;
        _triangleBuffer = null;
        _meshBuffer = null;
        _bvhNodeBuffer = null;

        long phaseStart = Stopwatch.GetTimestamp();
        RebuildTriangleData();
        if (startupProfile)
        {
            AddStartupProfilePhase(
                $"triangle data/per-mesh BVHs ({_triangles.Count:N0} triangles, {_meshInfos.Count:N0} meshes)",
                phaseStart);
            AddStartupProfileElapsedTicks(
                $"  new mesh BVH templates ({_profileBuiltMeshTemplateCount:N0})",
                _profileBuiltMeshTemplateTicks);
            AddStartupProfileElapsedTicks("  texture arrays", _profileTextureArrayTicks);
        }

        phaseStart = Stopwatch.GetTimestamp();
        _sceneBvhs.RebuildTopLevel(_spheres, Lighting.Lights, _meshInfos, topLevelBvhMinObjectCount, SceneBvhManager.StackSize);
        if (startupProfile)
        {
            AddStartupProfilePhase($"top-level BVH ({_sceneBvhs.TopLevelNodeCount:N0} nodes)", phaseStart);
        }

        phaseStart = Stopwatch.GetTimestamp();
        _sceneBvhs.RebuildShadow(_spheres, _meshInfos, shadowBvhMinObjectCount, SceneBvhManager.StackSize);
        if (startupProfile)
        {
            AddStartupProfilePhase($"shadow BVH ({_sceneBvhs.ShadowNodeCount:N0} nodes)", phaseStart);
        }

        shader.SetInt(NumSpheres, _spheres.Count);
        shader.SetInt(NumTriangles, _triangles.Count);
        shader.SetInt(NumMeshes, _meshInfos.Count);
        
        Lighting.SetShaderLightCount(shader);
        
        _sceneBvhs.SetShaderParameters(shader);

        phaseStart = Stopwatch.GetTimestamp();
        _sphereBuffer = CreateComputeBuffer(_spheres, SphereStride);
        _triangleBuffer = CreateComputeBuffer(_triangles, TriangleStride);
        _meshBuffer = CreateComputeBuffer(_meshInfos, MeshInfoStride);
        _bvhNodeBuffer = CreateComputeBuffer(_bvhNodes, BvhNodeStride);
        
        _lightingManager.EnsureBuffers();
        _lightingManager.UploadMeshLightTriangleCdf();
        
        if (startupProfile)
        {
            AddStartupProfilePhase("compute buffer creation/upload", phaseStart);
            AddStartupProfilePhase("buffer rebuild total", rebuildStart);
        }
    }

    private void AddStartupProfilePhase(string name, long startTimestamp)
    {
        AddStartupProfileElapsedTicks(name, Stopwatch.GetTimestamp() - startTimestamp);
    }

    private void AddStartupProfileElapsedTicks(string name, long elapsedTicks)
    {
        if (!_startupProfilePending || elapsedTicks < 0)
        {
            return;
        }

        var milliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        _startupProfilePhases.Add($"  {name}: {milliseconds:N1} ms");
    }

    private void LogStartupProfile()
    {
        _startupStopwatch.Stop();
        var message = new StringBuilder(512);
        message.AppendLine($"Ray tracing startup profile for '{gameObject.scene.name}':");
        message.AppendLine($"  object registration / Unity startup before Start: {_startupRegistrationMilliseconds:N1} ms");
        for (var i = 0; i < _startupProfilePhases.Count; i++)
        {
            message.AppendLine(_startupProfilePhases[i]);
        }
        message.AppendLine($"  total through first dispatch: {_startupStopwatch.Elapsed.TotalMilliseconds:N1} ms");
        message.Append(
            $"  scene totals: {_meshObjects.Count:N0} mesh objects, {_meshBvhTemplates.Count:N0} unique mesh templates, " +
            $"{_triangles.Count:N0} triangles, {Lighting.LightCount:N0} lights");
        Debug.Log(message.ToString(), this);
        _startupProfilePending = false;
    }

    private static ComputeBuffer CreateComputeBuffer<T>(List<T> data, int stride) where T : struct
    {
        var buffer = new ComputeBuffer(Mathf.Max(1, data.Count), stride);
        if (data.Count > 0)
        {
            buffer.SetData(data);
        }
        else
        {
            buffer.SetData(new[] { default(T) });
        }

        return buffer;
    }

    public void RegisterObject(PathTracingObject obj)
    {
        if (_rayTracingObjects.Contains(obj))
        {
            return;
        }

        var material = obj.GetComponent<RayMaterial>();
        var rayLight = obj.GetComponent<RayLight>();
        var sphereCollider = obj.GetComponent<SphereCollider>();

        // RayObjectPreview adds a MeshFilter to collider-backed lights for Scene-view display.
        // Prefer their analytic collider representation so that preview geometry cannot turn one
        // sphere light into an emissive triangle mesh.
        if (rayLight != null && sphereCollider != null)
        {
            MarkObjectRegistered(obj);
            var radius = GetWorldSphereRadius(sphereCollider, obj.transform);
            _lightingManager.RegisterSphereLight(obj, obj.transform, rayLight, sphereCollider, _triangles, radius,
                MarkLightingSceneChanged);
            return;
        }

        if (material != null && sphereCollider != null)
        {
            MarkObjectRegistered(obj);
            var sphere = new Sphere
            {
                position = obj.transform.TransformPoint(sphereCollider.center),
                color = material.Color.ToVector3(),
                smoothness = material.Smoothness,
                radius = GetWorldSphereRadius(sphereCollider, obj.transform),
                opacity = material.Opacity,
                refraction = material.RefractionIndex,
                specular = material.Specular,
                transmission = material.Transmission,
                materialType = (int)material.Type,
                textureIndex = -1,
                normalTextureIndex = -1,
                parallaxTextureIndex = -1,
            };
            _spheres.Add(sphere);
            _sphereObjects.Add(new PathTracedSphere
            {
                obj = obj,
                transform = obj.transform,
                material = material,
                collider = sphereCollider
            });
            return;
        }

        var meshFilter = obj.GetComponent<MeshFilter>();
        if ((material != null || rayLight != null) && meshFilter != null && meshFilter.sharedMesh != null)
        {
            MarkObjectRegistered(obj);
            _meshObjects.Add(new PathTracedMesh
            {
                obj = obj,
                transform = obj.transform,
                material = material,
                light = rayLight,
                mesh = meshFilter.sharedMesh,
                previousLocalToWorld = obj.transform.localToWorldMatrix,
                previousColor = material != null ? material.Color.ToVector3() : Vector3.one,
                previousEmission = rayLight != null ? rayLight.Color.ToVector3() * Mathf.Max(0.0f, rayLight.Intensity) : Vector3.zero,
                previousSmoothness = material != null ? material.Smoothness : 0.0f,
                previousMetallic = material != null ? GetEffectiveMetallic(material) : 0.0f,
                previousOpacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f,
                previousRefraction = material != null ? material.RefractionIndex : 1.0f,
                previousSpecular = material != null ? Mathf.Clamp01(material.Specular) : 0.0f,
                previousTransmission = material != null ? Mathf.Clamp01(material.Transmission) : 1.0f,
                previousMaterialType = rayLight != null ? 3 : (int)material.Type,
                previousAlbedoTexture = material != null ? material.AlbedoTexture : null,
                previousMetallicRoughnessTexture = material != null ? material.MetallicRoughnessTexture : null,
                previousNormalTexture = material != null ? material.NormalTexture : null,
                previousNormalStrength = material != null ? material.NormalStrength : 1.0f,
                previousTextureUvRotation = material != null ? material.TextureUvRotation : 0.0f,
                previousInterpolateNormals = material != null && material.InterpolateNormals
            });
            return;
        }

        Debug.LogWarning($"RayTracingObject '{obj.name}' needs RayMaterial with SphereCollider or MeshFilter, or RayLight with SphereCollider or MeshFilter.", obj);
    }

    private void MarkObjectRegistered(PathTracingObject obj)
    {
        _rayTracingObjects.Add(obj);
        _buffersNeedRebuilding = true;
        CameraManager.AutoFocusSceneChanged = true;
        ResetFrameAccumulation();
    }

    public void RegisterDirectionalLight(RayDirectionalLight directionalLight)
    {
        _lightingManager.RegisterDirectionalLight(directionalLight, _triangles, MarkLightingSceneChanged);
    }

    public void UnregisterDirectionalLight(RayDirectionalLight directionalLight)
    {
        _lightingManager.UnregisterDirectionalLight(directionalLight, _triangles, MarkLightingSceneChanged);
    }

    private void MarkLightingSceneChanged()
    {
        _buffersNeedRebuilding = true;
        CameraManager.AutoFocusSceneChanged = true;
        ResetFrameAccumulation();
    }

    public bool RegisterFogVolume(FogVolume fogVolume)
    {
        if (_fogVolume == fogVolume)
        {
            return true;
        }

        if (_fogVolume != null)
        {
            Debug.LogError(
                $"Only one active FogVolume component is supported by GameManager '{name}'. " +
                $"Disable '{_fogVolume.name}' before enabling '{fogVolume.name}'.",
                fogVolume);
            return false;
        }

        _fogVolume = fogVolume;
        ResetFrameAccumulation();
        return true;
    }

    public void UnregisterFogVolume(FogVolume fogVolume)
    {
        if (_fogVolume != fogVolume)
        {
            return;
        }

        _fogVolume = null;
        ResetFrameAccumulation();
    }
    
    public void UnregisterObject(PathTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        _buffersNeedRebuilding = true;
        CameraManager.AutoFocusSceneChanged = true;
        ResetFrameAccumulation();

        var sphereIndex = _sphereObjects.FindIndex(sphere => sphere.obj == obj);
        if (sphereIndex >= 0)
        {
            _sphereObjects.RemoveAt(sphereIndex);
            _spheres.RemoveAt(sphereIndex);
            return;
        }

        if (_lightingManager.UnregisterSphereLight(obj, _triangles, MarkLightingSceneChanged))
        {
            return;
        }

        var meshIndex = _meshObjects.FindIndex(mesh => mesh.obj == obj);
        if (meshIndex >= 0)
        {
            _meshObjects.RemoveAt(meshIndex);
        }
    }

    private void SetComputeBuffer(int nameId, ComputeBuffer buffer, int kernelHandle)
    {
        if (buffer != null)
        {
            shader.SetBuffer(kernelHandle, nameId, buffer);
        }
    }

    private void SetSceneBuffers(int kernelHandle)
    {
        SetComputeBuffer(Spheres, _sphereBuffer, kernelHandle);
        _lightingManager.SetBuffers(shader, kernelHandle);
        SetComputeBuffer(Triangles, _triangleBuffer, kernelHandle);
        SetComputeBuffer(Meshes, _meshBuffer, kernelHandle);
        SetComputeBuffer(BvhNodes, _bvhNodeBuffer, kernelHandle);
        _sceneBvhs.SetBuffers(shader, kernelHandle);
    }

    private static float GetWorldSphereRadius(SphereCollider sphereCollider, Transform sphereTransform)
    {
        var scale = sphereTransform.lossyScale;
        var largestAxisScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        return sphereCollider.radius * largestAxisScale;
    }

    private float GetNearestIntersectionDistanceForAutoFocus(Ray ray)
    {
        // This is a distance that allows things in the mid-distance to still get sub-pixel jitter, which
        // allows better anti-aliasing. Beyond this distance the focus changes are even more of a sub-pixel
        // and barely noticeable. We increase the jitter a bit if there is more super-sampling (passes) to get
        // more anti-aliasing.
        var nearestDistance = 12 - Math.Min(8.0f, numberOfPasses * 1.75f);

        foreach (var sphere in _spheres)
        {
            if (ShouldAutoFocusIgnoreObject(sphere.opacity))
            {
                continue;
            }

            var hitDistance = sphere.Intersect(ray.origin, ray.direction);

            if (hitDistance >= 0.0f && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
        }

        nearestDistance = _lightingManager.GetNearestSphereLightIntersection(ray, nearestDistance);

        foreach (var meshInfo in _meshInfos)
        {
            nearestDistance = GetNearestMeshIntersectionDistance(ray, meshInfo, nearestDistance);
        }
        
        if (WaterManager.TryGetAutoFocusHit(ray, nearestDistance, out float waterHitDistance))
        {
            nearestDistance = waterHitDistance;
        }

        return nearestDistance;
    }
    
    private void SetShaderParameters(int kernelHandle)
    {
        BindShaderTextures(kernelHandle);
        BindShaderCameraAndRendererSamplingParameters();
        BindShaderKeywordsAndLightingParameters(kernelHandle);
        BindShaderEnvironmentAndSceneParameters(kernelHandle);
    }

    private void BindShaderTextures(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, SkyboxTexture, skyboxTexture);
        EnsureMeshTextureArrays();
        shader.SetTexture(kernelHandle, MeshAlbedoTextures, _meshAlbedoTextureArray);
        shader.SetTexture(kernelHandle, MeshMetallicRoughnessTextures, _meshMetallicRoughnessTextureArray);
        shader.SetTexture(kernelHandle, MeshNormalTextures, _meshNormalTextureArray);
        shader.SetTexture(kernelHandle, MeshParallaxTextures, _meshParallaxTextureArray);
    }

    private void BindShaderCameraAndRendererSamplingParameters()
    {
        CameraManager.SetShaderParameters(shader);
        _temporalDenoisingManager.SetRayTracingShaderParameters(shader, debugRenderMode);

        if (randomNoise)
        {
            shader.SetInt(Seed, UnityEngine.Random.Range(1, int.MaxValue));
        }
        else
        {
            shader.SetInt(Seed, 1);
        }

        shader.SetInt(NumberOfPasses, numberOfPasses);
        shader.SetFloat(SubpixelJitterScale, subpixelJitterScale);
        shader.SetInt(NumBounces, numBounces);
        // Temporal modes are presented by RayTracingSpatialDenoiser after CSMain. Keep CSMain
        // on its normal HDR beauty path so an out-of-range renderer debug value cannot write
        // an untonemapped fallback before the temporal presentation pass runs.
        shader.SetInt(Mode, IsTemporalDebugMode() ? (int)DebugRenderMode.FinalColor : (int)debugRenderMode);
        shader.SetInt(UseFrameAccumulation, ShouldUseFrameAccumulation() ? 1 : 0);
        shader.SetInt(FrameCount, _accumulatedFrameCount);
        shader.SetInt(SampleOffset, CalculateSampleOffset());
    }

    private void BindShaderKeywordsAndLightingParameters(int kernelHandle)
    {
        // The shader splits its debug render path behind the DEBUG_RENDER keyword so the default
        // final-color variant compiles without any debug intersection/scatter code (a large shader
        // compile-time saving). Only enable the debug variant when a debug mode is actually active.
        if (debugRenderMode == DebugRenderMode.FinalColor || debugRenderMode == DebugRenderMode.Caustics
            || (debugRenderMode >= DebugRenderMode.RawBeauty && debugRenderMode != DebugRenderMode.TerrainCells))
        {
            shader.DisableKeyword("DEBUG_RENDER");
        }
        else
        {
            shader.EnableKeyword("DEBUG_RENDER");
        }
        if (enableCaustics)
        {
            shader.EnableKeyword("CAUSTICS_ENABLED");
            _causticsManager.SetShaderParameters(shader, kernelHandle, numBounces);
        }
        else
        {
            shader.DisableKeyword("CAUSTICS_ENABLED");
        }
        if (IsFogEnabled())
        {
            shader.EnableKeyword("FOG_ENABLED");
        }
        else
        {
            shader.DisableKeyword("FOG_ENABLED");
        }
        Lighting.SetShaderParameters(shader);
        Lighting.SetShaderSamplingParameters(shader, maxLightSamples, shadowQuality, shadowRandomness);

        _lightingManager.WarnIfImportanceLightLimitExceeded();
        shader.SetFloat(ParallaxMaximumStrengthCosine, Mathf.Cos(Mathf.Clamp(parallaxMaximumStrengthAngle, 0.0f, 90.0f) * Mathf.Deg2Rad));
    }

    private void BindShaderEnvironmentAndSceneParameters(int kernelHandle)
    {
        shader.SetFloat(Exposure, exposure);
        shader.SetFloat(FireflyClamp, Mathf.Max(0.0f, fireflyClamp));
        
        WaterManager.SetShaderParameters(shader, Application.isPlaying ? GetRenderTime() : 0.0f);
        
        SetTerrainShaderParameters(kernelHandle);
        
        var fogEnabled = IsFogEnabled();
        var fogCenter = fogEnabled ? _fogVolume.Center : Vector3.zero;
        var fogSize = fogEnabled ? _fogVolume.Size : Vector3.one;
        var fogAlbedo = fogEnabled ? _fogVolume.ScatteringAlbedo : Color.black;
        shader.SetInt(FogEnabled, fogEnabled ? 1 : 0);
        var fogBoundsMin = fogCenter - fogSize * 0.5f;
        var fogBoundsMax = fogCenter + fogSize * 0.5f;
        shader.SetVector(FogBoundsMin, new Vector4(fogBoundsMin.x, fogBoundsMin.y, fogBoundsMin.z, 0.0f));
        shader.SetVector(FogBoundsMax, new Vector4(fogBoundsMax.x, fogBoundsMax.y, fogBoundsMax.z, 0.0f));
        shader.SetVector(FogScatteringAlbedo, new Vector4(
            Mathf.Clamp01(fogAlbedo.r * fogScatteringScale),
            Mathf.Clamp01(fogAlbedo.g * fogScatteringScale),
            Mathf.Clamp01(fogAlbedo.b * fogScatteringScale),
            0.0f));
        shader.SetFloat(FogDensity, fogEnabled ? EffectiveFogDensity : 0.0f);
        shader.SetFloat(FogInScatteringIntensity, Mathf.Max(0.0f, fogInScatteringIntensity));
        shader.SetInt(FogMultipleScattering, enableFogMultipleScattering ? 1 : 0);
        
        Lighting.SetShaderLightCount(shader);
        
        _sceneBvhs.SetShaderParameters(shader);

        // When no shadow-casting blocker is transparent, the shader can use a cheaper
        // pure-occlusion shadow path that early-outs on the first opaque blocker.
        Lighting.SetShaderTransparentShadowBlockers(shader, Lighting.HasTransparentShadowBlockers);
        SetSceneBuffers(kernelHandle);
    }

    private void SetTerrainShaderParameters(int kernelHandle)
    {
        _terrainManager ??= GetComponent<TerrainManager>();
        _terrainManager.SetShaderParameters(shader, kernelHandle);
    }

    private void EnsureMeshTextureArrays()
    {
        if (_meshAlbedoTextureArray == null
            || _meshMetallicRoughnessTextureArray == null
            || _meshNormalTextureArray == null
            || _meshParallaxTextureArray == null)
        {
            RebuildMeshTextureArrays();
        }
    }

    private int CalculateSampleOffset()
    {
        var frameIndex = ShouldUseFrameAccumulation() ? _accumulatedFrameCount : _renderedFrameCount;
        var sampleOffset = frameIndex * Mathf.Max(1, numberOfPasses);
        return (int)Math.Min(int.MaxValue, sampleOffset);
    }

    private int CalculateAccumulationStateHash()
    {
        unchecked
        {
            var hash = 17;
            hash = AddHash(hash, _textureSize.x);
            hash = AddHash(hash, _textureSize.y);
            hash = AddHash(hash, numberOfPasses);
            hash = AddHash(hash, subpixelJitterScale);
            hash = AddHash(hash, numBounces);
            hash = AddHash(hash, shadowQuality);
            hash = AddHash(hash, topLevelBvhMinObjectCount);
            hash = AddHash(hash, shadowBvhMinObjectCount);
            hash = AddHash(hash, maxLightSamples);
            hash = AddHash(hash, (int)Lighting.LightSamplingStrategy);
            hash = AddHash(hash, Lighting.LightSampleCount);
            hash = AddHash(hash, shadowRandomness);
            hash = AddHash(hash, parallaxMaximumStrengthAngle);
            hash = AddHash(hash, Lighting.LightFalloffScale);
            hash = CameraManager.AddAccumulationStateHash(hash, CameraManager.IsTrackedFocusPointOutsideFrustum());
            hash = AddHash(hash, fireflyClamp);
            hash = _causticsManager.AddAccumulationStateHash(hash, enableCaustics);
            hash = WaterManager.AddAccumulationStateHash(hash);
            hash = AddHash(hash, enableVolumetricFog ? 1 : 0);
            hash = AddHash(hash, fogDensityScale);
            hash = AddHash(hash, fogScatteringScale);
            hash = AddHash(hash, fogInScatteringIntensity);
            hash = AddHash(hash, enableFogMultipleScattering ? 1 : 0);
            if (_fogVolume != null)
            {
                hash = _fogVolume.AddAccumulationStateHash(hash);
            }
            hash = AddHash(hash, randomNoise ? 1 : 0);
            hash = AddHash(hash, skyboxTexture != null ? skyboxTexture.GetInstanceID() : 0);
            hash = AddHash(hash, Lighting.SkyboxLightColor.r);
            hash = AddHash(hash, Lighting.SkyboxLightColor.g);
            hash = AddHash(hash, Lighting.SkyboxLightColor.b);
            hash = AddHash(hash, _spheres.Count);
            for (var i = 0; i < _spheres.Count; i++)
            {
                hash = _spheres[i].AddHash(hash);
            }

            hash = Lighting.AddLightStateHash(hash);

            hash = AddHash(hash, _triangles.Count);
            hash = AddHash(hash, _meshInfos.Count);
            for (var i = 0; i < _meshObjects.Count; i++)
            {
                hash = _meshObjects[i].AddHash(hash);
            }

            return hash;
        }
    }

    private int CalculatePhotonStateHash()
    {
        unchecked
        {
            var hash = _causticsManager.CalculatePhotonStateHash(17);
            hash = AddHash(hash, numBounces);
            hash = AddHash(hash, _spheres.Count);
            for (var i = 0; i < _spheres.Count; i++)
            {
                hash = _spheres[i].AddHash(hash);
            }

            hash = Lighting.AddLightStateHash(hash);

            hash = AddHash(hash, _triangles.Count);
            hash = AddHash(hash, _meshInfos.Count);
            for (var i = 0; i < _meshObjects.Count; i++)
            {
                hash = _meshObjects[i].AddHash(hash);
            }

            return WaterManager.AddCausticPhotonStateHash(hash, _singleFrame, GetRenderTime());
        }
    }

    internal static int AddHash(int hash, int value)
    {
        unchecked
        {
            return hash * 31 + value;
        }
    }

    internal static int AddHash(int hash, float value)
    {
        return AddHash(hash, value.GetHashCode());
    }

    internal static int AddHash(int hash, Vector3 value)
    {
        hash = AddHash(hash, value.x);
        hash = AddHash(hash, value.y);
        return AddHash(hash, value.z);
    }

    internal static int AddHash(int hash, Vector2 value)
    {
        hash = AddHash(hash, value.x);
        return AddHash(hash, value.y);
    }

    internal static int AddHash(int hash, Matrix4x4 value)
    {
        for (var i = 0; i < 16; i++)
        {
            hash = AddHash(hash, value[i]);
        }

        return hash;
    }

#if UNITY_EDITOR
    public RayTracingBvhBakeAsset EditorBvhBake => GetEditorBvhBake();

    public bool EditorBakeBvhUponExit
    {
        get => bakeBvhUponExit;
        set => bakeBvhUponExit = value;
    }

    public bool EditorLoadedBakedMeshBvhs => _loadedBakedMeshBvhs;

    public void EditorBuildMeshBvhTemplates()
    {
        foreach (var meshObject in _meshObjects)
        {
            var interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
            GetOrBuildMeshBvhTemplate(meshObject.mesh, interpolateNormals);
        }
    }

    public void EditorBuildMeshBvhTemplate(Mesh mesh, bool interpolateNormals)
    {
        GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
    }

    public void EditorGetMeshBvhTemplateCounts(Mesh mesh, bool interpolateNormals, out int triangleCount, out int nodeCount)
    {
        var template = GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
        triangleCount = template.triangles.Count;
        nodeCount = template.nodes.Count;
    }

    public void EditorWriteMeshBvhBake(string path, RayTracingBvhBakeAsset asset)
    {
        MeshBvhBakeSerializer.Write(path, asset, _meshBvhTemplates, SceneBvhManager.LeafTriangleCount, SceneBvhManager.StackSize,
            SceneBvhManager.BoundsPadding);
    }

    private bool IsEditorBvhBakeCurrent()
    {
        var bvhBake = GetEditorBvhBake();
        if (bvhBake == null)
        {
            _bvhBakeLoadStatus = "no local bake found";
            return false;
        }

        var expectedKeys = new HashSet<string>();
        foreach (var meshObject in _meshObjects)
        {
            if (meshObject.mesh == null)
            {
                _bvhBakeLoadStatus = "bake is out-of-date: a runtime mesh is missing";
                return false;
            }

            var interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
            expectedKeys.Add(GetEditorMeshIdentity(meshObject.mesh) + (interpolateNormals ? ":smooth" : ":flat"));
        }

        if (expectedKeys.Count != bvhBake.meshes.Count)
        {
            _bvhBakeLoadStatus = $"bake is out-of-date: expected {expectedKeys.Count:N0} templates, bake has {bvhBake.meshes.Count:N0}";
            return false;
        }

        foreach (var entry in bvhBake.meshes)
        {
            if (entry.mesh == null)
            {
                _bvhBakeLoadStatus = "bake is out-of-date: a baked mesh is missing";
                return false;
            }

            var key = entry.meshIdentity + (entry.interpolateNormals ? ":smooth" : ":flat");
            var path = UnityEditor.AssetDatabase.GetAssetPath(entry.mesh);
            var dependencyHash = string.IsNullOrEmpty(path)
                ? $"scene:{entry.mesh.vertexCount}:{GetMeshIndexCount(entry.mesh)}"
                : UnityEditor.AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (!expectedKeys.Remove(key)
                || entry.mesh.vertexCount != entry.vertexCount
                || GetMeshIndexCount(entry.mesh) != entry.indexCount
                || dependencyHash != entry.dependencyHash)
            {
                _bvhBakeLoadStatus = $"bake is out-of-date: mesh entry {entry.mesh?.name ?? "missing"} changed";
                return false;
            }
        }

        return expectedKeys.Count == 0;
    }

    private static string GetEditorMeshIdentity(Mesh mesh)
    {
        if (UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
        {
            return guid + ":" + localId;
        }

        return "scene:" + mesh.name + ":" + mesh.vertexCount + ":" + GetMeshIndexCount(mesh);
    }
#endif
    
    private RayTracingBvhBakeAsset GetEditorBvhBake()
    {
#if !UNITY_EDITOR
        return null;
#else
        if (string.IsNullOrEmpty(gameObject.scene.path))
        {
            return null;
        }

        var sceneGuid = UnityEditor.AssetDatabase.AssetPathToGUID(gameObject.scene.path);
        var managerId = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(this).targetObjectId.ToString();
        var path = $"Assets/Generated/RayTracingBvhBakes/{sceneGuid}_{managerId}.asset";
        return UnityEditor.AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(path);
#endif
    }
}

/// <summary>
/// Scene-local onboarding overlay. Attach this only to the Getting Started scene so other scenes
/// remain free of instructional UI.
/// </summary>
public sealed class GettingStartedControlsOverlay : MonoBehaviour
{
    private const float PanelLeft = 16.0f;
    private const float PanelTop = 16.0f;
    private const float PanelWidth = 390.0f;
    private const float PanelHeight = 205.0f;
    private const float PanelSpacing = 12.0f;

    private bool _visible = true;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.H))
        {
            _visible = !_visible;
        }
    }

    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(PanelLeft, GetPanelTop(), PanelWidth, PanelHeight), GUIContent.none, GUI.skin.window);
        GUILayout.Label("Getting Started", GUI.skin.label);
        GUILayout.Space(4.0f);
        GUILayout.Label("WASD: move    Arrow keys: look");
        GUILayout.Label("Click: focus at cursor");
        GUILayout.Label("Space: toggle paused refinement");
        GUILayout.Label("Z: performance diagnostics");
        GUILayout.Label("H: hide or show this help");
        GUILayout.Space(6.0f);
        GUILayout.Label("Open the Ray Tracing Controls tab: Window > Ray Tracing > Quick Controls.", GUI.skin.label);
        GUILayout.Label("Open the Ray Tracing Gallery tab: Window > Ray Tracing > Scene Gallery.", GUI.skin.label);
        GUILayout.EndArea();
    }

    private float GetPanelTop()
    {
        var benchmarkOverlay = GetComponent<RayTracingBenchmarkOverlay>();
        return benchmarkOverlay != null && benchmarkOverlay.showOverlay
            ? RayTracingBenchmarkOverlay.PanelTop + RayTracingBenchmarkOverlay.PanelHeight + PanelSpacing
            : PanelTop;
    }
}
