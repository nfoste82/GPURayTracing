using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PathTracing;
using PathTracing.AccelerationStructures;
using PathTracing.Camera;
using PathTracing.Caustics;
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
    public void InitSceneSettings(SceneSettings settings)
    {
        numberOfPasses = settings.NumberOfPasses;
        enableFrameAccumulation = settings.EnableFrameAccumulation;
        numBounces = settings.NumBounces;
        shadowQuality = settings.ShadowQuality;
        topLevelBvhMinObjectCount = settings.TopLevelBvhMinObjectCount;
        shadowBvhMinObjectCount = settings.ShadowBvhMinObjectCount;
        shadowRandomness = settings.ShadowRandomness;
        lightSamplingStrategy = settings.LightSamplingStrategy;
        lightSampleCount = settings.LightSampleCount;
        enableCaustics = settings.EnableCaustics;
        causticPhotonCount = settings.CausticPhotonCount;
        causticGatherRadius = settings.CausticGatherRadius;
        causticSeed = settings.CausticSeed;
        causticIntensity = settings.CausticIntensity;
        fogDensityScale = settings.FogDensityScale;
        fogScatteringScale = settings.FogScatteringScale;
        fogInScatteringIntensity = settings.FogInScatteringIntensity;
        enableFogMultipleScattering = settings.EnableFogMultipleScattering;
        lightFalloffScale = settings.LightFalloffScale;
        exposure = settings.Exposure;
        fireflyClamp = settings.FireflyClamp;
        randomNoise = settings.RandomNoise;
        _skyboxLightColor = settings.SkyboxLightColor;

        CameraManager.InitSceneSettings(settings);
    }

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

    [Header("Spatial denoising")]
    [Tooltip("Applies an edge-aware spatial A-trous filter to linear HDR beauty. This does not use temporal history.")]
    public bool enableSpatialDenoising = true;

    [Range(1, 5)]
    [Tooltip("A-trous passes use increasing pixel steps: 1, 2, 4, 8, and 16.")]
    public int spatialDenoiserIterations = 1;

    [Range(0.01f, 4.0f)]
    [Tooltip("How quickly filtering stops across depth discontinuities. Lower values preserve sharper depth edges.")]
    public float spatialDenoiserDepthSigma = 0.25f;

    [Range(1.0f, 256.0f)]
    [Tooltip("How strongly filtering stops across shading-normal changes.")]
    public float spatialDenoiserNormalPower = 64.0f;

    [Range(0.01f, 4.0f)]
    [Tooltip("How quickly filtering stops across albedo changes.")]
    public float spatialDenoiserAlbedoSigma = 0.25f;

    [Range(0.01f, 4.0f)]
    [Tooltip("How quickly filtering stops across HDR luminance changes.")]
    public float spatialDenoiserLuminanceSigma = 0.08f;

    [Header("Temporal denoising")]
    [Tooltip("Uses camera-only temporal reprojection and bounded HDR accumulation while the camera moves, then allows progressive still accumulation when it stops.")]
    public bool enableTemporalDenoising = false;

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
    public LightSamplingStrategy lightSamplingStrategy
    {
        get => _lightingManager.LightSamplingStrategy;
        set => _lightingManager.LightSamplingStrategy = value;
    }
    public int lightSampleCount
    {
        get => _lightingManager.LightSampleCount;
        set => _lightingManager.LightSampleCount = value;
    }

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

    // Must match MaxImportanceLights in RayTracingCompute.compute. Lights beyond this count
    // are ignored by the ImportanceSampled strategy.
    private const int MaxImportanceLights = 128;
    private bool _warnedImportanceLightOverflow = false;

    public DebugRenderMode debugRenderMode = DebugRenderMode.FinalColor;

    public float lightFalloffScale
    {
        get => _lightingManager.LightFalloffScale;
        set => _lightingManager.LightFalloffScale = value;
    }

    [Tooltip("Master brightness applied before ACES tone mapping. Acts like a camera exposure dial.")]
    [Range(0.0f, 8.0f)]
    public float exposure = 1.0f;

    [Tooltip("Maximum HDR luminance of one path sample before averaging. Lower positive values clamp fireflies more strongly; 0 disables the clamp.")]
    [Range(0.0f, 8.0f)]
    public float fireflyClamp = 1.0f;

    public bool randomNoise = false;

    public Color32 _skyboxLightColor
    {
        get => _lightingManager.SkyboxLightColor;
        set => _lightingManager.SkyboxLightColor = value;
    }

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

    private Vector4 _skyboxLightColorAsVector;
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
    private RenderTexture _denoiserPingTexture;
    private RenderTexture _denoiserPongTexture;
    private RenderTexture _denoiserIteration1Texture;
    private RenderTexture _denoiserIteration2Texture;
    private RenderTexture _denoiserIteration3Texture;
    private RenderTexture _causticPreservationMaskTexture;
    private ComputeShader _spatialDenoiserShader;
    [SerializeField]
    private TemporalDenoisingManager _temporalDenoisingManager = new TemporalDenoisingManager();
    private bool _temporalDynamicSceneChanged;
    private bool _hasRenderedCameraState;
    private Vector3 _lastRenderedCameraPosition;
    private Quaternion _lastRenderedCameraRotation;
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

    private List<Light> _lights => _lightingManager.MutableLights;
    private List<PathTracedLight> _lightObjects => _lightingManager.MutableLightObjects;
    private List<RayDirectionalLight> _directionalLights => _lightingManager.MutableDirectionalLights;
    private ComputeBuffer _lightBuffer;

    private List<Triangle> _triangles = new ();
    private readonly List<MeshInfo> _meshInfos = new ();
    private readonly List<BvhNode> _bvhNodes = new ();
    private readonly List<TopLevelBvhNode> _topLevelBvhNodes = new ();
    private readonly List<TopLevelBvhNode> _shadowBvhNodes = new ();
    private readonly List<TopLevelBvhBuildItem> _topLevelBvhBuildItems = new ();
    private readonly List<TopLevelBvhBuildItem> _shadowBvhBuildItems = new();
    private readonly List<float> _meshLightTriangleCdf = new();
    private readonly TopLevelBvhBuildItemComparer _topLevelBvhBuildItemComparer = new ();
    private readonly List<PathTracedMesh> _meshObjects = new ();
    private readonly Dictionary<long, MeshBvhTemplate> _meshBvhTemplates = new ();
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
    private ComputeBuffer _topLevelBvhNodeBuffer;
    private ComputeBuffer _shadowBvhNodeBuffer;
    private ComputeBuffer _meshLightTriangleCdfBuffer;
    
    [SerializeField, HideInInspector]
    private CausticsManager _causticsManager = new();

    // Acceleration structures only need rebuilding when object bounds, membership, or their
    // activation thresholds change. Material-only edits keep the existing bounds valid.
    private bool _topLevelBvhDirty;
    private bool _shadowBvhDirty;
    private int _lastTopLevelBvhMinObjectCount = int.MinValue;
    private int _lastShadowBvhMinObjectCount = int.MinValue;

    // Tracks whether any shadow-casting blocker (regular sphere or mesh triangle) is transparent
    // (opacity < 1). When false, shadow rays in the shader take a cheaper pure-occlusion path that
    // early-outs on the first opaque blocker without the nearest-transparent-blocker bookkeeping.
    // Recomputed each frame in UpdateSpheres()/UpdateTriangles().
    private bool _hasTransparentSphereBlockers;
    private bool _hasTransparentMeshBlockers;
    private const float ShadowBlockerOpaqueThreshold = 1.0f;

    // Reusable suffix surface-area scratch for the SAH BVH split sweep, grown on demand so each
    // build does not allocate per node.
    private float[] _sahSuffixArea = Array.Empty<float>();
    
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

    private TerrainManager _terrainManager;
    [SerializeField, HideInInspector]
    private VideoCaptureManager _videoCaptureManager = new();
    public VideoCaptureManager VideoCapture => _videoCaptureManager;
    public CausticsManager Caustics => _causticsManager;
    public int causticPhotonCount { get => _causticsManager.PhotonCount; set => _causticsManager.PhotonCount = value; }
    public float causticGatherRadius { get => _causticsManager.GatherRadius; set => _causticsManager.GatherRadius = value; }
    public int causticSeed { get => _causticsManager.Seed; set => _causticsManager.Seed = value; }
    public float causticIntensity { get => _causticsManager.Intensity; set => _causticsManager.Intensity = value; }

    // True for the single frame where the "Compiling shader variant" overlay should be shown
    // before the blocking compile happens. Read by RayTracingBenchmarkOverlay.
    public bool IsCompilingShaderVariant => _pendingVariantWarmup;
    public int SphereCount => _spheres.Count;
    public int LightCount => _lights.Count;
    public int MeshCount => _meshInfos.Count;
    public int TriangleCount => _triangles.Count;
    public int TopLevelBvhNodeCount => _topLevelBvhNodes.Count;
    public int ShadowBvhNodeCount => _shadowBvhNodes.Count;
    public int TopLevelBvhObjectCount => _topLevelBvhBuildItems.Count;
    public int ShadowBvhObjectCount => _shadowBvhBuildItems.Count;
    public bool IsTopLevelBvhActive => _topLevelBvhNodes.Count > 0;
    public bool IsShadowBvhActive => _shadowBvhNodes.Count > 0;
    // TextureSize is the internal ray-tracing resolution; DisplayTextureSize is the camera target size.
    public Vector2Int TextureSize => _textureSize;
    internal ComputeShader SpatialDenoiserShader => _spatialDenoiserShader;
    internal RenderTexture OutputTexture => _outputTexture;
    internal RenderTexture BeautyTexture => _beautyTexture;
    internal RenderTexture FeatureNormalTexture => _featureNormalTexture;
    internal RenderTexture FeatureDepthTexture => _featureDepthTexture;
    internal RenderTexture FeatureIdentityTexture => _featureIdentityTexture;
    internal RenderTexture FeatureValidityTexture => _featureValidityTexture;
    internal RenderTexture CausticPreservationMaskTexture => _causticPreservationMaskTexture;
    public Water WaterInternal => WaterManager.Water;
    public Vector2Int DisplayTextureSize => _displayTextureSize;
    public int AccumulatedFrameCount => _accumulatedFrameCount;
    public int SphereLightCount => _lightObjects.Count;
    
    public int MeshLightCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _meshObjects.Count; i++)
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
            int count = 0;
            for (int i = 0; i < _triangles.Count; i++)
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

    private static bool _buffersNeedRebuilding = false;
    private static readonly List<PathTracingObject> _rayTracingObjects = new ();
    private static readonly int SkyboxTexture = Shader.PropertyToID("_SkyboxTexture");
    private static readonly int MeshAlbedoTextures = Shader.PropertyToID("_MeshAlbedoTextures");
    private static readonly int MeshMetallicRoughnessTextures = Shader.PropertyToID("_MeshMetallicRoughnessTextures");
    private static readonly int MeshNormalTextures = Shader.PropertyToID("_MeshNormalTextures");
    private static readonly int MeshParallaxTextures = Shader.PropertyToID("_MeshParallaxTextures");
    private static readonly int CameraToWorld = Shader.PropertyToID("_CameraToWorld");
    private static readonly int CameraInverseProjection = Shader.PropertyToID("_CameraInverseProjection");
    private static readonly int FrameJitterNdc = Shader.PropertyToID("_FrameJitterNdc");
    private static readonly int UseTemporalJitter = Shader.PropertyToID("_UseTemporalJitter");
    private static readonly int SkyboxLight = Shader.PropertyToID("_SkyboxLight");
    private static readonly int Seed = Shader.PropertyToID("_Seed");
    private static readonly int NumberOfPasses = Shader.PropertyToID("_NumberOfPasses");
    private static readonly int SubpixelJitterScale = Shader.PropertyToID("_SubpixelJitterScale");
    private static readonly int NumBounces = Shader.PropertyToID("_NumBounces");
    private static readonly int Mode = Shader.PropertyToID("_DebugRenderMode");
    private static readonly int UseFrameAccumulation = Shader.PropertyToID("_UseFrameAccumulation");
    private static readonly int FrameCount = Shader.PropertyToID("_AccumulatedFrameCount");
    private static readonly int SampleOffset = Shader.PropertyToID("_SampleOffset");
    private static readonly int MaxLightSamples = Shader.PropertyToID("_MaxLightSamples");
    private static readonly int SamplingStrategy = Shader.PropertyToID("_LightSamplingStrategy");
    private static readonly int LightSampleCount = Shader.PropertyToID("_LightSampleCount");
    private static readonly int Quality = Shader.PropertyToID("_ShadowQuality");
    private static readonly int ShadowRandomness = Shader.PropertyToID("_ShadowRandomness");
    private static readonly int ParallaxMaximumStrengthCosine = Shader.PropertyToID("_ParallaxMaximumStrengthCosine");
    private static readonly int LightFalloffScale = Shader.PropertyToID("_LightFalloffScale");
    private static readonly int FocalDistance = Shader.PropertyToID("_FocalDistance");
    private static readonly int ApertureRadius = Shader.PropertyToID("_ApertureRadius");
    private static readonly int ApertureBladeCount = Shader.PropertyToID("_ApertureBladeCount");
    private static readonly int ApertureBladeRotation = Shader.PropertyToID("_ApertureBladeRotation");
    private static readonly int AnamorphicRatio = Shader.PropertyToID("_AnamorphicRatio");
    private static readonly int Exposure = Shader.PropertyToID("_Exposure");
    private static readonly int FireflyClamp = Shader.PropertyToID("_FireflyClamp");
    private static readonly int FogEnabled = Shader.PropertyToID("_FogEnabled");
    private static readonly int FogBoundsMin = Shader.PropertyToID("_FogBoundsMin");
    private static readonly int FogBoundsMax = Shader.PropertyToID("_FogBoundsMax");
    private static readonly int FogScatteringAlbedo = Shader.PropertyToID("_FogScatteringAlbedo");
    private static readonly int FogDensity = Shader.PropertyToID("_FogDensity");
    private static readonly int FogInScatteringIntensity = Shader.PropertyToID("_FogInScatteringIntensity");
    private static readonly int FogMultipleScattering = Shader.PropertyToID("_FogMultipleScattering");
    private static readonly int NumLights = Shader.PropertyToID("_NumLights");
    private static readonly int NumTopLevelBvhNodes = Shader.PropertyToID("_NumTopLevelBvhNodes");
    private static readonly int NumShadowBvhNodes = Shader.PropertyToID("_NumShadowBvhNodes");
    private static readonly int HasTransparentShadowBlockers = Shader.PropertyToID("_HasTransparentShadowBlockers");
    private static readonly int FocusQueryResult = Shader.PropertyToID("_FocusQueryResult");
    private static readonly int Spheres = Shader.PropertyToID("_Spheres");
    private static readonly int Lights = Shader.PropertyToID("_Lights");
    private static readonly int Triangles = Shader.PropertyToID("_Triangles");
    private static readonly int Meshes = Shader.PropertyToID("_Meshes");
    private static readonly int BvhNodes = Shader.PropertyToID("_BvhNodes");
    private static readonly int TopLevelBvhNodes = Shader.PropertyToID("_TopLevelBvhNodes");
    private static readonly int ShadowBvhNodes = Shader.PropertyToID("_ShadowBvhNodes");
    private static readonly int MeshLightTriangleCdf = Shader.PropertyToID("_MeshLightTriangleCdf");
    private static readonly int NumSpheres = Shader.PropertyToID("_NumSpheres");
    private static readonly int NumTriangles = Shader.PropertyToID("_NumTriangles");
    private static readonly int NumMeshes = Shader.PropertyToID("_NumMeshes");

    private const int SphereStride = 92;
    private const int LightStride = 88;
    private const int MaxNumberOfPasses = 32;
    private const int TriangleStride = 252;
    private const int MeshInfoStride = 48;
    private const int BvhNodeStride = 48;
    private const int TopLevelBvhNodeStride = 48;
    private const int MeshLightTriangleCdfStride = 4;
    // The photon transport kernel carries a medium stack and intersection state. A 32-thread
    // group keeps Metal register allocation within its recommended per-group budget.
    private const int CausticTraceThreadCount = 32;
    // CSMain combines path tracing with optional volumetric fog. Keeping its groups at 16 threads
    // avoids Metal's recommended temporary-register budget being exceeded.
    private const int RenderThreadCountX = 4;
    private const int RenderThreadCountY = 4;
    private const int MaxCausticGridCells = 262144;
    private const int BvhLeafTriangleCount = 4;
    private const int BvhStackSize = 32;
    public const int BvhBakeFormatVersion = 2;
    private const int BvhBakeMagic = 0x48564252;
    private const float BvhBoundsPadding = 0.0001f;
    private const int TopLevelObjectTypeInternal = -1;
    private const int TopLevelObjectTypeSphere = 0;
    private const int TopLevelObjectTypeLight = 1;
    private const int TopLevelObjectTypeMesh = 2;
    
    private WaterManager _waterManager;
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

    public WaterManager WaterManager
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
        spatialDenoiserIterations = Mathf.Clamp(spatialDenoiserIterations, 1, 5);
        spatialDenoiserDepthSigma = Mathf.Max(0.01f, spatialDenoiserDepthSigma);
        spatialDenoiserNormalPower = Mathf.Max(1.0f, spatialDenoiserNormalPower);
        spatialDenoiserAlbedoSigma = Mathf.Max(0.01f, spatialDenoiserAlbedoSigma);
        spatialDenoiserLuminanceSigma = Mathf.Max(0.01f, spatialDenoiserLuminanceSigma);
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

        _unitySkyboxMaterial.SetTexture("_MainTex", skyboxTexture);
        _unitySkyboxMaterial.SetColor("_Tint", _skyboxLightColor);
        _unitySkyboxMaterial.SetFloat("_Exposure", unitySkyboxExposure);
        _unitySkyboxMaterial.SetFloat("_Rotation", unitySkyboxRotation);
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
        ReleaseSpatialDenoiserResources();
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

    private void EnsureSpatialDenoiserResources()
    {
        EnsureSpatialDenoiserShader();
        if (_spatialDenoiserShader == null) return;

        if (_denoiserPingTexture != null)
        {
            return;
        }

        _denoiserPingTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _denoiserPongTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _causticPreservationMaskTexture = CreateFeatureTexture(RenderTextureFormat.RHalf);
    }

    internal void EnsureSpatialDenoiserResourcesInternal() => EnsureSpatialDenoiserResources();
    internal RenderTexture CreateFeatureTextureInternal(RenderTextureFormat format) => CreateFeatureTexture(format);
    internal void GenerateCausticPreservationMaskInternal(int threadGroupsX, int threadGroupsY) => GenerateCausticPreservationMask(threadGroupsX, threadGroupsY);
    internal void RunSpatialDenoiserInternal(RenderTexture source, RenderTexture variance) => RunSpatialDenoiser(source, variance);
    internal bool IsFogEnabledInternal() => IsFogEnabled();
    internal float GetCameraApertureRadiusInternal() => CameraManager.GetApertureRadius();

    private void EnsureSpatialDenoiserShader()
    {
        if (_spatialDenoiserShader == null)
        {
            _spatialDenoiserShader = Resources.Load<ComputeShader>("RayTracingSpatialDenoiser");
        }

        if (_spatialDenoiserShader == null)
        {
            Debug.LogError("Spatial denoiser shader was not found at Resources/RayTracingSpatialDenoiser.", this);
        }
    }

    private RenderTexture GetDenoiserIterationTexture(int iteration)
    {
        switch (iteration)
        {
            case 1:
                if (_denoiserIteration1Texture == null)
                {
                    _denoiserIteration1Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
                }
                return _denoiserIteration1Texture;
            case 2:
                if (_denoiserIteration2Texture == null)
                {
                    _denoiserIteration2Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
                }
                return _denoiserIteration2Texture;
            case 3:
                if (_denoiserIteration3Texture == null)
                {
                    _denoiserIteration3Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
                }
                return _denoiserIteration3Texture;
            default:
                return null;
        }
    }

    private void ReleaseSpatialDenoiserResources()
    {
        ReleaseRenderTexture(_denoiserPingTexture);
        ReleaseRenderTexture(_denoiserPongTexture);
        ReleaseRenderTexture(_denoiserIteration1Texture);
        ReleaseRenderTexture(_denoiserIteration2Texture);
        ReleaseRenderTexture(_denoiserIteration3Texture);
        ReleaseRenderTexture(_causticPreservationMaskTexture);
        _denoiserPingTexture = null;
        _denoiserPongTexture = null;
        _denoiserIteration1Texture = null;
        _denoiserIteration2Texture = null;
        _denoiserIteration3Texture = null;
        _causticPreservationMaskTexture = null;
    }

    private static void ReleaseRenderTexture(RenderTexture texture)
    {
        if (texture != null)
        {
            texture.Release();
        }
    }

    private bool ShouldRunSpatialDenoiser()
    {
        return enableSpatialDenoising
            || debugRenderMode == DebugRenderMode.SpatialDenoised
            || debugRenderMode == DebugRenderMode.AtrousIteration1
            || debugRenderMode == DebugRenderMode.AtrousIteration2
            || debugRenderMode == DebugRenderMode.AtrousIteration3;
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

        if (Input.GetKeyDown(KeyCode.T))
        {
            SetSingleFrameMode(!_singleFrame);
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            SetSingleFrameMode(false);
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

        string message = _startupInitializationPending
            ? _startupInitializationStatus + "\nStartup timings will be logged when initialization completes."
            : "Compiling shader variant, this may take a minute...";
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
        Application.targetFrameRate = 10;
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
        ReleaseSpatialDenoiserResources();
        ReleaseTemporalDenoiserResources();
        _sphereBuffer?.Release();
        _lightBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
        _topLevelBvhNodeBuffer?.Release();
        _shadowBvhNodeBuffer?.Release();
        _meshLightTriangleCdfBuffer?.Release();
        if (CameraManager.FocusQueryInFlight)
        {
            CameraManager.FocusReadbackRequest.WaitForCompletion();
        }
        CameraManager.FocusQueryGeneration++;
        CameraManager.FocusQueryBuffer?.Release();
        CameraManager.FocusQueryBuffer = null;
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
        Vector2Int internalSize = CalculateInternalRenderSize(width, height, renderResolutionPercent);
        int internalWidth = internalSize.x;
        int internalHeight = internalSize.y;
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
        float renderScale = Mathf.Clamp(percent, 25.0f, 100.0f) * 0.01f;
        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(displayWidth * renderScale)),
            Mathf.Max(1, Mathf.RoundToInt(displayHeight * renderScale)));
    }

    private void UpdateTextureFromCompute(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "Result", _outputTexture);
        shader.SetTexture(kernelHandle, "AccumulationResult", _accumulationTexture);
        shader.SetTexture(kernelHandle, "Beauty", _beautyTexture);
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / (float)RenderThreadCountX);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / (float)RenderThreadCountY);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }

    private void PresentFinalColor()
    {
        EnsureSpatialDenoiserShader();
        if (_spatialDenoiserShader == null || _presentationTexture == null)
        {
            return;
        }

        PresentLinearTexture(_presentationSource ?? _beautyTexture, _presentationTexture);
    }

    private void PresentLinearTexture(RenderTexture source, RenderTexture destination)
    {
        int kernel = _spatialDenoiserShader.FindKernel("CSPresent");
        _spatialDenoiserShader.SetTexture(kernel, "InputBeauty", source);
        _spatialDenoiserShader.SetTexture(kernel, "PresentationResult", destination);
        _spatialDenoiserShader.SetFloat("_Exposure", exposure);
        _spatialDenoiserShader.Dispatch(kernel,
            Mathf.CeilToInt(destination.width / 8.0f),
            Mathf.CeilToInt(destination.height / 8.0f), 1);
    }

    private void UpdateFeaturesFromCompute()
    {
        int kernelHandle = shader.FindKernel("CSFeatures");
        SetShaderParameters(kernelHandle);
        shader.SetTexture(kernelHandle, "FeatureNormal", _featureNormalTexture);
        shader.SetTexture(kernelHandle, "FeatureAlbedo", _featureAlbedoTexture);
        shader.SetTexture(kernelHandle, "FeatureDepth", _featureDepthTexture);
        shader.SetTexture(kernelHandle, "FeatureIdentity", _featureIdentityTexture);
        shader.SetTexture(kernelHandle, "FeatureValidity", _featureValidityTexture);
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / (float)RenderThreadCountX);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / (float)RenderThreadCountY);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }

    private void PresentFeatureDebugMode()
    {
        EnsureSpatialDenoiserResources();
        int kernel = _spatialDenoiserShader.FindKernel("CSVisualizeFeature");
        _spatialDenoiserShader.SetTexture(kernel, "FeatureNormal", _featureNormalTexture);
        _spatialDenoiserShader.SetTexture(kernel, "FeatureAlbedo", _featureAlbedoTexture);
        _spatialDenoiserShader.SetTexture(kernel, "FeatureDepth", _featureDepthTexture);
        _spatialDenoiserShader.SetTexture(kernel, "FeatureIdentity", _featureIdentityTexture);
        _spatialDenoiserShader.SetTexture(kernel, "FeatureValidity", _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(kernel, "PresentationResult", _outputTexture);
        _spatialDenoiserShader.SetInt("_FeatureDebugMode", (int)debugRenderMode - (int)DebugRenderMode.FeatureNormal + 1);
        _spatialDenoiserShader.Dispatch(kernel,
            Mathf.CeilToInt(_textureSize.x / 8.0f), Mathf.CeilToInt(_textureSize.y / 8.0f), 1);
    }

    private bool IsFeatureDebugMode()
    {
        return debugRenderMode >= DebugRenderMode.FeatureNormal && debugRenderMode <= DebugRenderMode.FeatureValidity;
    }

    private void GenerateCausticPreservationMask(int threadGroupsX, int threadGroupsY)
    {
        int kernel = _spatialDenoiserShader.FindKernel("CSGeneratePreservationMask");
        _spatialDenoiserShader.SetTexture(kernel, "Beauty", _beautyTexture);
        _spatialDenoiserShader.SetTexture(kernel, "FeatureValidity", _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(kernel, "GeneratedPreservationMask", _causticPreservationMaskTexture);
        _spatialDenoiserShader.SetFloat("_CausticPreservationThreshold", causticPreservationThreshold);
        _spatialDenoiserShader.SetInt("_EnableCausticPreservation", enableCaustics ? 1 : 0);
        _spatialDenoiserShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    private void RunSpatialDenoiser(RenderTexture source = null, RenderTexture variance = null)
    {
        EnsureSpatialDenoiserResources();
        if (_spatialDenoiserShader == null || _denoiserPingTexture == null)
        {
            return;
        }

        int atrousKernel = _spatialDenoiserShader.FindKernel("CSAtrous");
        _spatialDenoiserShader.SetTexture(atrousKernel, "FeatureNormal", _featureNormalTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "FeatureAlbedo", _featureAlbedoTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "FeatureDepth", _featureDepthTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "FeatureIdentity", _featureIdentityTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "FeatureValidity", _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "Variance", variance ?? _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(atrousKernel, "PreservationMask", _causticPreservationMaskTexture);
        _spatialDenoiserShader.SetFloat("_DepthSigma", spatialDenoiserDepthSigma);
        _spatialDenoiserShader.SetFloat("_NormalPower", spatialDenoiserNormalPower);
        _spatialDenoiserShader.SetFloat("_AlbedoSigma", spatialDenoiserAlbedoSigma);
        _spatialDenoiserShader.SetFloat("_LuminanceSigma", spatialDenoiserLuminanceSigma);
        _spatialDenoiserShader.SetInt("_UseVarianceGuidance", variance != null ? 1 : 0);
        _spatialDenoiserShader.SetInt("_EnableCausticPreservation", enableCaustics ? 1 : 0);

        RenderTexture input = source ?? _beautyTexture;
        RenderTexture output = _denoiserPingTexture;
        int requiredDebugIterations = debugRenderMode == DebugRenderMode.AtrousIteration3 ? 3
            : debugRenderMode == DebugRenderMode.AtrousIteration2 ? 2
            : 1;
        int iterations = Mathf.Clamp(Mathf.Max(spatialDenoiserIterations, requiredDebugIterations), 1, 5);
        bool captureDebugIteration = debugRenderMode == DebugRenderMode.AtrousIteration1
            || debugRenderMode == DebugRenderMode.AtrousIteration2
            || debugRenderMode == DebugRenderMode.AtrousIteration3;
        RenderTexture debugIterationTexture = captureDebugIteration
            ? GetDenoiserIterationTexture(requiredDebugIterations) : null;
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        if (enableCaustics)
        {
            GenerateCausticPreservationMask(threadGroupsX, threadGroupsY);
        }
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            _spatialDenoiserShader.SetTexture(atrousKernel, "InputBeauty", input);
            _spatialDenoiserShader.SetTexture(atrousKernel, "FilteredBeauty", output);
            _spatialDenoiserShader.SetInt("_StepWidth", 1 << iteration);
            _spatialDenoiserShader.Dispatch(atrousKernel, threadGroupsX, threadGroupsY, 1);

            if (captureDebugIteration && iteration == requiredDebugIterations - 1)
            {
                Graphics.CopyTexture(output, debugIterationTexture);
            }

            input = output;
            output = output == _denoiserPingTexture ? _denoiserPongTexture : _denoiserPingTexture;
        }

        RenderTexture presentationInput = input;
        if (captureDebugIteration)
        {
            presentationInput = debugIterationTexture;
        }

        _presentationSource = presentationInput;
        if (debugRenderMode == DebugRenderMode.SpatialDenoised || captureDebugIteration)
        {
            PresentLinearTexture(presentationInput, _outputTexture);
        }
    }

    private void RunTemporalDenoiser()
    {
        _temporalDenoisingManager.SetDynamicSceneChanged(_temporalDynamicSceneChanged);
        _temporalDenoisingManager.Run(debugRenderMode);
    }

    #if false
    private void RunTemporalDenoiserLegacy()
    {
        EnsureTemporalDenoiserResources();
        if (_spatialDenoiserShader == null || _motionVectorTexture == null)
        {
            return;
        }

        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        int motionKernel = _spatialDenoiserShader.FindKernel("CSGenerateCameraMotion");
        _spatialDenoiserShader.SetTexture(motionKernel, "FeatureDepth", _featureDepthTexture);
        _spatialDenoiserShader.SetTexture(motionKernel, "FeatureValidity", _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(motionKernel, "GeneratedMotionVectors", _motionVectorTexture);
        _spatialDenoiserShader.SetMatrix("_CurrentUnjitteredViewProjection", _currentUnjitteredViewProjection);
        _spatialDenoiserShader.SetMatrix("_PreviousUnjitteredViewProjection", _previousUnjitteredViewProjection);
        _spatialDenoiserShader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        _spatialDenoiserShader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);
        _spatialDenoiserShader.Dispatch(motionKernel, threadGroupsX, threadGroupsY, 1);

        RenderTexture previousRadiance = _temporalHistoryReadIsA ? _temporalRadianceHistoryA : _temporalRadianceHistoryB;
        RenderTexture nextRadiance = _temporalHistoryReadIsA ? _temporalRadianceHistoryB : _temporalRadianceHistoryA;
        RenderTexture previousNormal = _temporalHistoryReadIsA ? _temporalNormalHistoryA : _temporalNormalHistoryB;
        RenderTexture nextNormal = _temporalHistoryReadIsA ? _temporalNormalHistoryB : _temporalNormalHistoryA;
        RenderTexture previousDepth = _temporalHistoryReadIsA ? _temporalDepthHistoryA : _temporalDepthHistoryB;
        RenderTexture nextDepth = _temporalHistoryReadIsA ? _temporalDepthHistoryB : _temporalDepthHistoryA;
        RenderTexture previousIdentity = _temporalHistoryReadIsA ? _temporalIdentityHistoryA : _temporalIdentityHistoryB;
        RenderTexture nextIdentity = _temporalHistoryReadIsA ? _temporalIdentityHistoryB : _temporalIdentityHistoryA;
        RenderTexture previousValidity = _temporalHistoryReadIsA ? _temporalValidityHistoryA : _temporalValidityHistoryB;
        RenderTexture nextValidity = _temporalHistoryReadIsA ? _temporalValidityHistoryB : _temporalValidityHistoryA;
        RenderTexture previousHistoryLength = _temporalHistoryReadIsA ? _temporalHistoryLengthA : _temporalHistoryLengthB;
        RenderTexture nextHistoryLength = _temporalHistoryReadIsA ? _temporalHistoryLengthB : _temporalHistoryLengthA;
        RenderTexture previousMoments = _temporalHistoryReadIsA ? _temporalMomentsA : _temporalMomentsB;
        RenderTexture nextMoments = _temporalHistoryReadIsA ? _temporalMomentsB : _temporalMomentsA;
        int validationKernel = _spatialDenoiserShader.FindKernel("CSTemporalReprojectValidate");
        _spatialDenoiserShader.SetTexture(validationKernel, "Beauty", _beautyTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "FeatureNormal", _featureNormalTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "FeatureDepth", _featureDepthTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "FeatureIdentity", _featureIdentityTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "FeatureValidity", _featureValidityTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "MotionVectors", _motionVectorTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousRadiance", previousRadiance);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousNormal", previousNormal);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousDepth", previousDepth);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousIdentity", previousIdentity);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousValidity", previousValidity);
        _spatialDenoiserShader.SetTexture(validationKernel, "PreviousHistoryLength", previousHistoryLength);
        _spatialDenoiserShader.SetTexture(validationKernel, "ReprojectedRadiance", _temporalReprojectedRadianceTexture);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextRadiance", nextRadiance);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextNormal", nextNormal);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextDepth", nextDepth);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextIdentity", nextIdentity);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextValidity", nextValidity);
        _spatialDenoiserShader.SetTexture(validationKernel, "NextHistoryLength", nextHistoryLength);
        _spatialDenoiserShader.SetTexture(validationKernel, "TemporalDiagnostics", _temporalDiagnosticsTexture);
        _spatialDenoiserShader.SetInt("_TemporalHistoryValid", _temporalHistoryValid ? 1 : 0);
        _spatialDenoiserShader.SetInt("_TemporalUnsupported", IsTemporalPathUnsupported() ? 1 : 0);
        _spatialDenoiserShader.SetFloat("_TemporalDepthThreshold", _temporalDenoisingManager.temporalDepthThreshold);
        _spatialDenoiserShader.SetFloat("_TemporalNormalThreshold", _temporalDenoisingManager.temporalNormalThreshold);
        _spatialDenoiserShader.SetInt("_TemporalMaxHistoryLength", _temporalDenoisingManager.temporalMaxHistoryLength);
        _spatialDenoiserShader.SetFloat("_TemporalCameraRotationDelta", _hasRenderedCameraState
            ? Quaternion.Angle(renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
        _spatialDenoiserShader.Dispatch(validationKernel, threadGroupsX, threadGroupsY, 1);

        int momentsKernel = _spatialDenoiserShader.FindKernel("CSUpdateTemporalMoments");
        _spatialDenoiserShader.SetTexture(momentsKernel, "Beauty", _beautyTexture);
        _spatialDenoiserShader.SetTexture(momentsKernel, "MotionVectors", _motionVectorTexture);
        _spatialDenoiserShader.SetTexture(momentsKernel, "HistoryLength", nextHistoryLength);
        _spatialDenoiserShader.SetTexture(momentsKernel, "TemporalDiagnostics", _temporalDiagnosticsTexture);
        _spatialDenoiserShader.SetTexture(momentsKernel, "PreviousMoments", previousMoments);
        _spatialDenoiserShader.SetTexture(momentsKernel, "NextMoments", nextMoments);
        _spatialDenoiserShader.SetTexture(momentsKernel, "NextVariance", _temporalVarianceTexture);
        _spatialDenoiserShader.SetFloat("_TemporalCameraRotationDelta", _hasRenderedCameraState
            ? Quaternion.Angle(renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
        _spatialDenoiserShader.Dispatch(momentsKernel, threadGroupsX, threadGroupsY, 1);

        if (enableCaustics || IsCausticPreservationDebugMode())
        {
            GenerateCausticPreservationMask(threadGroupsX, threadGroupsY);
        }

        if (IsTemporalDebugMode())
        {
            int debugMode = debugRenderMode == DebugRenderMode.MotionVectors ? 1
                : debugRenderMode == DebugRenderMode.TemporalReprojectedRadiance ? 2
                : debugRenderMode == DebugRenderMode.TemporalHistoryAcceptance ? 3
                : debugRenderMode == DebugRenderMode.TemporalRejectionReason ? 4
                : debugRenderMode == DebugRenderMode.TemporalDenoised ? 5
                : debugRenderMode == DebugRenderMode.TemporalHistoryLength ? 6
                : debugRenderMode == DebugRenderMode.TemporalDenoisedTint ? 7
                : debugRenderMode == DebugRenderMode.TemporalVariance ? 8 : 9;
            int visualizeKernel = _spatialDenoiserShader.FindKernel("CSVisualizeTemporal");
            _spatialDenoiserShader.SetTexture(visualizeKernel, "MotionVectors", _motionVectorTexture);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "ReprojectedRadiance", _temporalReprojectedRadianceTexture);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "NextRadiance", nextRadiance);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "TemporalDiagnostics", _temporalDiagnosticsTexture);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "HistoryLength", nextHistoryLength);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "Variance", _temporalVarianceTexture);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "PreservationMask", _causticPreservationMaskTexture);
            _spatialDenoiserShader.SetTexture(visualizeKernel, "PresentationResult", _outputTexture);
            _spatialDenoiserShader.SetInt("_TemporalDebugMode", debugMode);
            _spatialDenoiserShader.SetFloat("_Exposure", exposure);
            _spatialDenoiserShader.SetInt("_TemporalMaxHistoryLength", _temporalDenoisingManager.temporalMaxHistoryLength);
            _spatialDenoiserShader.Dispatch(visualizeKernel, threadGroupsX, threadGroupsY, 1);
        }
        else if (ShouldUseTemporalAccumulation())
        {
            if (_temporalDenoisingManager.temporalVarianceGuidedFiltering)
            {
                RunSpatialDenoiser(nextRadiance, _temporalVarianceTexture);
            }
            else
            {
                int presentKernel = _spatialDenoiserShader.FindKernel("CSPresent");
                _spatialDenoiserShader.SetTexture(presentKernel, "InputBeauty", nextRadiance);
                _spatialDenoiserShader.SetTexture(presentKernel, "PresentationResult", _outputTexture);
                _spatialDenoiserShader.SetFloat("_Exposure", exposure);
                _spatialDenoiserShader.Dispatch(presentKernel, threadGroupsX, threadGroupsY, 1);
            }
        }

        _temporalHistoryReadIsA = !_temporalHistoryReadIsA;
        _temporalHistoryValid = true;
    }

    #endif

    private void PresentCausticPreservationMask()
    {
        EnsureSpatialDenoiserResources();
        if (_spatialDenoiserShader == null || _causticPreservationMaskTexture == null)
        {
            return;
        }

        var threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        var threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        GenerateCausticPreservationMask(threadGroupsX, threadGroupsY);
        var kernel = _spatialDenoiserShader.FindKernel("CSVisualizeTemporal");
        _spatialDenoiserShader.SetTexture(kernel, "PreservationMask", _causticPreservationMaskTexture);
        _spatialDenoiserShader.SetTexture(kernel, "PresentationResult", _outputTexture);
        _spatialDenoiserShader.SetInt("_TemporalDebugMode", 9);
        _spatialDenoiserShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    internal void ResetFrameAccumulation()
    {
        _accumulatedFrameCount = 0;
        _hasAccumulationStateHash = false;
    }

    private void BuildCausticSamplingDistribution()
    {
        _causticsManager.TargetPairs.Clear();
        _causticsManager.TargetTriangles.Clear();
        var meshTriangleRanges = new Dictionary<int, Vector2Int>();

        for (var meshIndex = 0; meshIndex < _meshInfos.Count; meshIndex++)
        {
            var mesh = _meshInfos[meshIndex];
            if (!CausticsLogic.IsCausticRefractor(mesh, _triangles))
            {
                continue;
            }

            var triangleStart = _causticsManager.TargetTriangles.Count;
            var totalArea = 0.0f;
            for (var triangleOffset = 0; triangleOffset < mesh.triangleCount; triangleOffset++)
            {
                var triangle = _triangles[mesh.triangleStart + triangleOffset];
                totalArea += 0.5f * Vector3.Cross(
                    triangle.vertex1 - triangle.vertex0,
                    triangle.vertex2 - triangle.vertex0).magnitude;
                _causticsManager.TargetTriangles.Add(new CausticTargetTriangle
                {
                    triangleIndex = mesh.triangleStart + triangleOffset,
                    cumulativeProbability = totalArea
                });
            }

            if (totalArea <= 1e-8f)
            {
                _causticsManager.TargetTriangles.RemoveRange(triangleStart, _causticsManager.TargetTriangles.Count - triangleStart);
                continue;
            }

            // cumulativeProbability currently holds the running *unnormalized* area sum. Normalize it
            // in place while tracking the previous already-normalized value locally. Reading the
            // previous element back out of the list here would divide it by totalArea a second time
            // (it was normalized on the prior iteration), which corrupts every per-triangle
            // probability and therefore every photon's power.
            var lastTriangleIndex = _causticsManager.TargetTriangles.Count - 1;
            var previousCdf = 0.0f;
            for (var triangleIndex = triangleStart; triangleIndex < _causticsManager.TargetTriangles.Count; triangleIndex++)
            {
                var target = _causticsManager.TargetTriangles[triangleIndex];
                var normalizedCdf = target.cumulativeProbability / totalArea;
                target.selectionProbability = normalizedCdf - previousCdf;
                
                // Guard the last entry against float rounding leaving the CDF just below any sample.
                target.cumulativeProbability = triangleIndex == lastTriangleIndex ? 1.0f : normalizedCdf;
                
                previousCdf = normalizedCdf;
                _causticsManager.TargetTriangles[triangleIndex] = target;
            }
            meshTriangleRanges.Add(meshIndex, new Vector2Int(triangleStart, _causticsManager.TargetTriangles.Count - triangleStart));
        }

        var pairWeights = new List<float>();
        var maximumWeight = 0.0f;
        for (var lightIndex = 0; lightIndex < _lights.Count; lightIndex++)
        {
            var light = _lights[lightIndex];
            if (!CausticsLogic.IsCausticLight(light))
            {
                continue;
            }

            for (var sphereIndex = 0; sphereIndex < _spheres.Count; sphereIndex++)
            {
                Sphere sphere = _spheres[sphereIndex];
                if (CausticsLogic.IsCausticRefractor(sphere))
                {
                    AddCausticTargetPair(lightIndex, 0, sphereIndex, 0, 0,
                        CausticsLogic.GetCausticPairWeight(light, sphere.position, sphere.radius), pairWeights, ref maximumWeight);
                }
            }

            foreach (KeyValuePair<int, Vector2Int> meshRange in meshTriangleRanges)
            {
                MeshInfo mesh = _meshInfos[meshRange.Key];
                AddCausticTargetPair(lightIndex, 1, meshRange.Key, meshRange.Value.x, meshRange.Value.y,
                    CausticsLogic.GetCausticPairWeight(light, (mesh.boundsMin + mesh.boundsMax) * 0.5f,
                        (mesh.boundsMax - mesh.boundsMin).magnitude * 0.5f),
                    pairWeights, ref maximumWeight);
            }

            if (CausticsLogic.IsCausticRefractor(WaterInternal))
            {
                var waterSize = WaterInternal.Size;
                AddCausticTargetPair(lightIndex, 2, -1, 0, 0,
                    CausticsLogic.GetCausticPairWeight(light, WaterInternal.TopCenter,
                        new Vector3(waterSize.x, 0.0f, waterSize.y).magnitude * 0.5f),
                    pairWeights, ref maximumWeight);
            }
        }

        if (_causticsManager.TargetPairs.Count == 0)
        {
            return;
        }

        var totalWeight = 0.0f;
        var minimumWeight = Mathf.Max(1e-8f, maximumWeight * 1e-4f);
        for (var i = 0; i < pairWeights.Count; i++)
        {
            pairWeights[i] = Mathf.Max(minimumWeight, pairWeights[i]);
            totalWeight += pairWeights[i];
        }

        var cumulativeProbability = 0.0f;
        for (var i = 0; i < _causticsManager.TargetPairs.Count; i++)
        {
            var pair = _causticsManager.TargetPairs[i];
            pair.selectionProbability = pairWeights[i] / totalWeight;
            cumulativeProbability += pair.selectionProbability;
            pair.cumulativeProbability = i == _causticsManager.TargetPairs.Count - 1 ? 1.0f : cumulativeProbability;
            _causticsManager.TargetPairs[i] = pair;
        }
    }

    private void AddCausticTargetPair(int lightIndex, int refractorType, int refractorIndex,
        int triangleStart, int triangleCount, float weight, List<float> weights, ref float maximumWeight)
    {
        _causticsManager.TargetPairs.Add(new CausticTargetPair
        {
            lightIndex = lightIndex,
            refractorType = refractorType,
            refractorIndex = refractorIndex,
            triangleStart = triangleStart,
            triangleCount = triangleCount
        });
        weights.Add(weight);
        maximumWeight = Mathf.Max(maximumWeight, weight);
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

        var padding = Mathf.Max(0.01f, causticGatherRadius);
        boundsMin -= Vector3.one * padding;
        boundsMax += Vector3.one * padding;
        
        var size = Vector3.Max(boundsMax - boundsMin, Vector3.one * padding);
        var cellSize = padding;
        var dimensions = CausticsLogic.CalculateCausticGridDimensions(size, cellSize);
        
        while ((long)dimensions.x * dimensions.y * dimensions.z > MaxCausticGridCells)
        {
            cellSize *= 1.25f;
            dimensions = CausticsLogic.CalculateCausticGridDimensions(size, cellSize);
        }

        _causticsManager.GridMin = boundsMin;
        _causticsManager.GridCellSize = cellSize;
        _causticsManager.GridDimensions = dimensions;
        long gridCellCount = (long)dimensions.x * dimensions.y * dimensions.z;
        _causticsManager.GridCellCountValue = Mathf.Max(1, (int)Mathf.Min(int.MaxValue, gridCellCount));
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

        int stateHash = CalculatePhotonStateHash();
        bool stateChanged = !_causticsManager.HasPhotonStateHash || stateHash != _causticsManager.PhotonStateHash;
        if (stateChanged)
        {
            BuildCausticSamplingDistribution();
        }
        CalculateCausticGridLayout();
        _causticsManager.EnsureResources(causticPhotonCount);
        if (stateChanged)
        {
            _causticsManager.TargetPairBuffer.SetData(_causticsManager.TargetPairs.Count > 0
                ? _causticsManager.TargetPairs
                : new List<CausticTargetPair> { default });
            _causticsManager.TargetTriangleBuffer.SetData(_causticsManager.TargetTriangles.Count > 0
                ? _causticsManager.TargetTriangles
                : new List<CausticTargetTriangle> { default });
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
        int clearKernel = shader.FindKernel("ClearCausticPhotons");
        int traceKernel = shader.FindKernel("TraceCausticPhotons");
        int clearGridKernel = shader.FindKernel("ClearCausticGrid");
        int buildGridKernel = shader.FindKernel("BuildCausticGrid");
        SetPhotonTraceSceneParameters(traceKernel);
        _causticsManager.SetShaderParameters(shader, clearKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, traceKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, clearGridKernel, numBounces);
        _causticsManager.SetShaderParameters(shader, buildGridKernel, numBounces);
        SetSceneBuffers(traceKernel);
        shader.Dispatch(clearKernel, 1, 1, 1);
        shader.Dispatch(traceKernel, Mathf.CeilToInt(Mathf.Max(1, causticPhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        shader.Dispatch(clearGridKernel, Mathf.Max(1,
            Mathf.CeilToInt(_causticsManager.GridCellCount / (float)CausticTraceThreadCount)), 1, 1);
        shader.Dispatch(buildGridKernel, Mathf.CeilToInt(Mathf.Max(1, causticPhotonCount) / (float)CausticTraceThreadCount), 1, 1);
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
        if (_causticsManager.MetadataReadbackInFlight || _causticsManager.PhotonMetadataBuffer == null)
        {
            return;
        }

        _causticsManager.MetadataReadbackInFlight = true;
        int generation = _causticsManager.MetadataReadbackGeneration;
        AsyncGPUReadback.Request(_causticsManager.PhotonMetadataBuffer,
            request => CompleteCausticMetadataReadback(request, generation));
    }

    private void CompleteCausticMetadataReadback(AsyncGPUReadbackRequest request, int generation)
    {
        if (generation != _causticsManager.MetadataReadbackGeneration)
        {
            return;
        }

        _causticsManager.MetadataReadbackInFlight = false;
        if (request.hasError)
        {
            Debug.LogWarning("Caustic metadata GPU readback failed.", this);
            return;
        }

        var metadata = request.GetData<uint>();
        if (metadata.Length >= CausticsManager.MetadataCount)
        {
            _causticsManager.GridOutOfBoundsCountValue = (int)metadata[4];
            _causticsManager.GridPhotonCountValue = (int)metadata[5];
        }
    }

    private void SetPhotonTraceSceneParameters(int traceKernel)
    {
        EnsureMeshTextureArrays();
        shader.SetTexture(traceKernel, "_MeshAlbedoTextures", _meshAlbedoTextureArray);
        shader.SetTexture(traceKernel, "_MeshMetallicRoughnessTextures", _meshMetallicRoughnessTextureArray);
        shader.SetTexture(traceKernel, "_MeshNormalTextures", _meshNormalTextureArray);
        shader.SetTexture(traceKernel, "_MeshParallaxTextures", _meshParallaxTextureArray);
        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);
        WaterManager.SetShaderParameters(shader, Application.isPlaying ? GetRenderTime() : 0.0f);
        SetTerrainShaderParameters(traceKernel);
    }

    private bool ShouldUseFrameAccumulation()
    {
        bool animatedWater = WaterManager.IsAnimated && !_singleFrame;
        return enableFrameAccumulation && debugRenderMode == DebugRenderMode.FinalColor && !animatedWater
            && !ShouldUseTemporalAccumulation();
    }

    private float GetRenderTime()
    {
        return _singleFrame ? _singleFrameRenderTime : Time.time;
    }

    private int GetActiveLightCountForSampling()
    {
        int activeLightCount = _lights.Count;
        if (maxLightSamples > 0)
        {
            activeLightCount = Mathf.Min(activeLightCount, maxLightSamples);
        }

        return Mathf.Max(0, activeLightCount);
    }

    public void RenderImage(RenderTexture src, RenderTexture dest)
    {
        EnsureOutputTextureSize(src.width, src.height);
        if (_startupInitializationPending)
        {
            Graphics.Blit(src, dest);
            return;
        }
        _videoCaptureManager.PrepareRender();
        if (!ShouldRunTemporalDenoiser() && _temporalDenoisingManager.HasResources)
        {
            ReleaseTemporalDenoiserResources();
            _temporalDenoisingManager.ResetHistory();
        }

        // Detect a switch to a debug variant that has not been compiled yet. The first Dispatch of
        // a new variant blocks the main thread while the GPU backend compiles it. To avoid an
        // apparently frozen app, we defer that blocking dispatch by one frame: this frame we set the
        // overlay flag and re-show the previous output (no heavy dispatch), so OnGUI can paint the
        // "Compiling shader variant" message; next frame we run the stalling dispatch with that
        // message already on screen.
        bool fogEnabled = IsFogEnabled();
        bool useDedicatedCausticsDebugKernel = enableCaustics && debugRenderMode == DebugRenderMode.Caustics;
        int requestedVariant = GetShaderVariantKey(debugRenderMode, enableCaustics, fogEnabled);
        bool shaderVariantChanged = debugRenderMode != _appliedDebugRenderMode
            || enableCaustics != _appliedCausticsEnabled
            || fogEnabled != _appliedFogEnabled;
        if (!_pendingVariantWarmup
            && shaderVariantChanged
            && !_warmedShaderVariants.Contains(requestedVariant))
        {
            _pendingVariantWarmup = true;
            Graphics.Blit(_presentationTexture != null ? _presentationTexture : _outputTexture, dest);
            return;
        }

        long firstFramePreparationStart = _startupProfilePending ? Stopwatch.GetTimestamp() : 0;
        CameraManager.UpdateTrackedFocusPoint();
        _temporalDynamicSceneChanged = false;
        _temporalDenoisingManager.SetDynamicSceneChanged(false);
        UpdateSpheres();
        UpdateTriangles();
        UpdateTopLevelBvh();
        UpdateShadowBvh();
        CameraManager.UpdateAutoFocus(
            numberOfPasses,
            WaterManager.CalculateAutoFocusStateHash(),
            GetNearestIntersectionDistanceForAutoFocus);
        CameraManager.AutoFocusSceneChanged = false;
        UpdateCausticPhotonMap();

        if (ShouldRunTemporalDenoiser())
        {
            _temporalDenoisingManager.PrepareCameraState();
        }

        bool useFrameAccumulation = ShouldUseFrameAccumulation();
        if (useFrameAccumulation)
        {
            int stateHash = CalculateAccumulationStateHash();
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

        var kernelHandle = shader.FindKernel(useDedicatedCausticsDebugKernel ? "CSCausticsDebug" : "CSMain");

        SetShaderParameters(kernelHandle);
        DispatchPendingFocusQuery();
        if (_startupProfilePending)
        {
            AddStartupProfilePhase("first-frame CPU preparation", firstFramePreparationStart);
        }
        long dispatchStart = _startupProfilePending ? Stopwatch.GetTimestamp() : 0;
        UpdateTextureFromCompute(kernelHandle);
        _presentationSource = _beautyTexture;
        if (!useDedicatedCausticsDebugKernel && (ShouldRunSpatialDenoiser() || ShouldRunTemporalDenoiser() || IsFeatureDebugMode() || IsCausticPreservationDebugMode()))
        {
            UpdateFeaturesFromCompute();
        }
        if (!useDedicatedCausticsDebugKernel && IsFeatureDebugMode())
        {
            PresentFeatureDebugMode();
        }
        if (!useDedicatedCausticsDebugKernel && ShouldRunTemporalDenoiser())
        {
            RunTemporalDenoiser();
            _temporalDenoisingManager.CommitCameraState();
        }
        else if (!useDedicatedCausticsDebugKernel && IsCausticPreservationDebugMode())
        {
            PresentCausticPreservationMask();
        }
        if (!useDedicatedCausticsDebugKernel && ShouldRunSpatialDenoiser() && !IsTemporalDebugMode()
            && !ShouldUseTemporalAccumulation())
        {
            RunSpatialDenoiser();
        }
        if (!useDedicatedCausticsDebugKernel && debugRenderMode == DebugRenderMode.FinalColor)
        {
            PresentFinalColor();
        }
        if (_startupProfilePending)
        {
            AddStartupProfilePhase("first compute dispatch (includes shader compilation)", dispatchStart);
            LogStartupProfile();
        }
        _renderedFrameCount++;

        if (useFrameAccumulation && !useDedicatedCausticsDebugKernel)
        {
            _accumulatedFrameCount++;
        }
        _temporalDenoisingManager.CommitRenderedCameraState();

        // The dispatch above triggered (and blocked on) any first-time variant compile. Record
        // that this debug mode is now warm so future switches to it are instant, and clear the
        // overlay flag.
        _warmedShaderVariants.Add(requestedVariant);
        _appliedDebugRenderMode = debugRenderMode;
        _appliedCausticsEnabled = enableCaustics;
        _appliedFogEnabled = fogEnabled;
        _pendingVariantWarmup = false;

        _videoCaptureManager.CompleteRender();

        Graphics.Blit(debugRenderMode == DebugRenderMode.FinalColor && !useDedicatedCausticsDebugKernel
            ? _presentationTexture : _outputTexture, dest);
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

        string directory = Path.GetDirectoryName(path);
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
        int width = Mathf.Max(1, _displayTextureSize.x);
        int height = Mathf.Max(1, _displayTextureSize.y);
        RenderTexture presentation = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture currentOutput = debugRenderMode == DebugRenderMode.FinalColor
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

    private void DispatchPendingFocusQuery()
    {
        if (!CameraManager.FocusQueryPending || CameraManager.FocusQueryInFlight)
        {
            return;
        }

        if (CameraManager.FocusQueryBuffer == null)
        {
            CameraManager.FocusQueryBuffer = new ComputeBuffer(1, sizeof(float) * 4);
        }

        int kernel = shader.FindKernel("CSFocusQuery");
        SetShaderParameters(kernel);
        shader.SetVector("_FocusQueryUv", CameraManager.PendingFocusQueryUv);
        shader.SetBuffer(kernel, FocusQueryResult, CameraManager.FocusQueryBuffer);
        shader.Dispatch(kernel, 1, 1, 1);

        CameraManager.FocusQueryPending = false;
        CameraManager.FocusQueryInFlight = true;
        CameraManager.FocusQueryCameraPosition = renderTextureCamera.transform.position;
        CameraManager.FocusQueryCameraForward = renderTextureCamera.transform.forward;
        int generation = CameraManager.FocusQueryGeneration;
        CameraManager.FocusReadbackRequest = AsyncGPUReadback.Request(
            CameraManager.FocusQueryBuffer,
            request => CompleteFocusQuery(request, generation));
    }

    private void CompleteFocusQuery(AsyncGPUReadbackRequest request, int generation)
    {
        if (generation != CameraManager.FocusQueryGeneration)
        {
            return;
        }

        CameraManager.FocusQueryInFlight = false;
        if (request.hasError)
        {
            Debug.LogWarning("GPU click-to-focus readback failed.", this);
            return;
        }

        Vector4 result = request.GetData<Vector4>()[0];
        if (result.w < 0.5f)
        {
            return;
        }

        Vector3 hitPosition = new Vector3(result.x, result.y, result.z);
        float focusDistance = Vector3.Dot(hitPosition - CameraManager.FocusQueryCameraPosition, CameraManager.FocusQueryCameraForward);
        if (focusDistance <= 0.0f)
        {
            return;
        }

        CameraManager.cameraAutoFocus = false;
        if (CameraManager.cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            CameraManager.SetOrbitFocus(hitPosition);
        }
        CameraManager.cameraFocalDistance = CameraManager.cameraBehavior == CameraBehavior.OrbitFocusPoint
            ? CameraManager.DefaultOrbitZoom
            : Mathf.Max(0.1f, focusDistance);
        CameraManager.PreviousFocalDistance = CameraManager.cameraFocalDistance;
        CameraManager.ClickedFocusPoint = hitPosition;
        CameraManager.HasClickedFocusPoint = CameraManager.enableClickToFocus && CameraManager.trackClickedFocusPoint;
        CameraManager.UpdateTrackedFocusPoint();
        ResetFrameAccumulation();
    }

    private void UpdateSpheres()
    {
        _hasTransparentSphereBlockers = false;
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
            _temporalDynamicSceneChanged |= boundsChanged;
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

            if (sphere.opacity < ShadowBlockerOpaqueThreshold)
            {
                _hasTransparentSphereBlockers = true;
            }

            _spheres[i] = sphere;
        }
        
        for (var i = 0; i < _lights.Count; ++i)
        {
            if (i >= _lightObjects.Count)
            {
                break;
            }

            var lightData = _lights[i];
            var previousLightData = lightData;
            var lightObject = _lightObjects[i];

            var position = lightObject.transform.TransformPoint(lightObject.collider.center);
            var radius = GetWorldSphereRadius(lightObject.collider, lightObject.transform);
            var boundsChanged = lightData.position != position || !Mathf.Approximately(lightData.radius, radius);
            _temporalDynamicSceneChanged |= boundsChanged;
            lightBoundsChanged |= boundsChanged;
            lightData.position = position;
            lightData.radius = radius;
            lightData.area = Mathf.PI * lightData.radius * lightData.radius;
            lightData.type = (int)PathTracedLightType.Sphere;

            var light = lightObject.light;
            lightData.emission = light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity);
            lightsChanged |= !lightData.Equals(previousLightData);
            
            _lights[i] = lightData;
        }

        bool directionalLightsChanged = _lightingManager.UpdateDirectionalLights(out bool directionalBoundsChanged);
        _temporalDynamicSceneChanged |= directionalLightsChanged;
        lightsChanged |= directionalLightsChanged;
        lightBoundsChanged |= directionalBoundsChanged;

        if (sphereBoundsChanged)
        {
            _topLevelBvhDirty = true;
            _shadowBvhDirty = true;
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

        var requiredLightBufferCount = Mathf.Max(1, _lights.Count);
        if (_lightBuffer == null || _lightBuffer.count < requiredLightBufferCount)
        {
            _lightBuffer?.Release();
            _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        }
        else if (lightsChanged && _lights.Count > 0)
        {
            _lightBuffer.SetData(_lights);
        }

        if (lightBoundsChanged)
        {
            _topLevelBvhDirty = true;
        }

        CameraManager.AutoFocusSceneChanged |= spheresChanged || lightsChanged;
    }

    private void UpdateTriangles()
    {
        if (_meshObjects.Count == 0)
        {
            _hasTransparentMeshBlockers = false;
            return;
        }

        UpdateMeshChangeCache(out bool geometryChanged, out bool materialChanged);
        _temporalDynamicSceneChanged |= geometryChanged || materialChanged;
        CameraManager.AutoFocusSceneChanged |= geometryChanged || materialChanged;
        if (!geometryChanged && !materialChanged)
        {
            return;
        }

        if (geometryChanged)
        {
            RebuildTriangleData();
            _topLevelBvhDirty = true;
            _shadowBvhDirty = true;
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
        if ((geometryChanged || materialChanged) && _lightBuffer != null && _lights.Count > 0)
        {
            _lightBuffer.SetData(_lights);
        }
        if (geometryChanged && _meshLightTriangleCdfBuffer != null)
        {
            _meshLightTriangleCdfBuffer.SetData(_meshLightTriangleCdf.Count > 0
                ? _meshLightTriangleCdf
                : new List<float> { 0.0f });
        }

    }

    private void UpdateTopLevelBvh()
    {
        if (!_topLevelBvhDirty && _lastTopLevelBvhMinObjectCount == topLevelBvhMinObjectCount)
        {
            return;
        }

        RebuildTopLevelBvh();
        _topLevelBvhDirty = false;
        _lastTopLevelBvhMinObjectCount = topLevelBvhMinObjectCount;

        int requiredBufferCount = Mathf.Max(1, _topLevelBvhNodes.Count);
        if (_topLevelBvhNodeBuffer == null || _topLevelBvhNodeBuffer.count < requiredBufferCount)
        {
            _topLevelBvhNodeBuffer?.Release();
            _topLevelBvhNodeBuffer = CreateComputeBuffer(_topLevelBvhNodes, TopLevelBvhNodeStride);
        }
        else if (_topLevelBvhNodes.Count > 0)
        {
            _topLevelBvhNodeBuffer.SetData(_topLevelBvhNodes);
        }
    }

    private void UpdateShadowBvh()
    {
        if (!_shadowBvhDirty && _lastShadowBvhMinObjectCount == shadowBvhMinObjectCount)
        {
            return;
        }

        RebuildShadowBvh();
        _shadowBvhDirty = false;
        _lastShadowBvhMinObjectCount = shadowBvhMinObjectCount;

        int requiredBufferCount = Mathf.Max(1, _shadowBvhNodes.Count);
        if (_shadowBvhNodeBuffer == null || _shadowBvhNodeBuffer.count < requiredBufferCount)
        {
            _shadowBvhNodeBuffer?.Release();
            _shadowBvhNodeBuffer = CreateComputeBuffer(_shadowBvhNodes, TopLevelBvhNodeStride);
        }
        else if (_shadowBvhNodes.Count > 0)
        {
            _shadowBvhNodeBuffer.SetData(_shadowBvhNodes);
        }
    }

    private void UpdateMeshChangeCache(out bool geometryChanged, out bool materialChanged)
    {
        geometryChanged = false;
        materialChanged = false;
        _hasTransparentMeshBlockers = false;

        for (int i = 0; i < _meshObjects.Count; i++)
        {
            var meshObject = _meshObjects[i];
            var material = meshObject.material;
            var light = meshObject.light;
            var localToWorld = meshObject.transform.localToWorldMatrix;
            var color = material != null ? material.Color.ToVector3() : Vector3.one;
            var emission = light != null ? light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity) : Vector3.zero;
            var smoothness = material != null ? material.Smoothness : 0.0f;
            var metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            var specular = material != null ? Mathf.Clamp01(material.Specular) : 0.0f;
            var transmission = material != null ? Mathf.Clamp01(material.Transmission) : 1.0f;
            var materialType = light != null ? 3 : (int)material.Type;
            var albedoTexture = material != null ? material.AlbedoTexture : null;
            var metallicRoughnessTexture = material != null ? material.MetallicRoughnessTexture : null;
            var normalTexture = material != null ? material.NormalTexture : null;
            var parallaxTexture = material != null ? material.ParallaxTexture : null;
            var textureUvScale = material != null ? material.TextureUvScale : Vector2.one;
            float parallaxStrength = material != null ? material.ParallaxStrength : 0.0f;
            float minimumParallaxStrength = material != null ? Mathf.Min(material.MinimumParallaxStrength, parallaxStrength) : 0.0f;
            bool interpolateNormals = material != null && material.InterpolateNormals;

            if (opacity < ShadowBlockerOpaqueThreshold)
            {
                _hasTransparentMeshBlockers = true;
            }

            bool meshGeometryChanged = meshObject.previousLocalToWorld != localToWorld
                || meshObject.previousInterpolateNormals != interpolateNormals;
            bool meshMaterialChanged = meshObject.previousColor != color
                || meshObject.previousEmission != emission
                || !Mathf.Approximately(meshObject.previousSmoothness, smoothness)
                || !Mathf.Approximately(meshObject.previousMetallic, metallic)
                || !Mathf.Approximately(meshObject.previousOpacity, opacity)
                || !Mathf.Approximately(meshObject.previousRefraction, refraction)
                || !Mathf.Approximately(meshObject.previousSpecular, specular)
                || !Mathf.Approximately(meshObject.previousTransmission, transmission)
                || meshObject.previousMaterialType != materialType
                || meshObject.previousAlbedoTexture != albedoTexture
                || meshObject.previousMetallicRoughnessTexture != metallicRoughnessTexture
                || meshObject.previousNormalTexture != normalTexture
                || meshObject.previousParallaxTexture != parallaxTexture
                || meshObject.previousTextureUvScale != textureUvScale
                || !Mathf.Approximately(meshObject.previousParallaxStrength, parallaxStrength)
                || !Mathf.Approximately(meshObject.previousMinimumParallaxStrength, minimumParallaxStrength);

            geometryChanged |= meshGeometryChanged;
            materialChanged |= meshMaterialChanged;

            if (!meshGeometryChanged && !meshMaterialChanged)
            {
                continue;
            }

            meshObject.previousLocalToWorld = localToWorld;
            meshObject.previousColor = color;
            meshObject.previousEmission = emission;
            meshObject.previousSmoothness = smoothness;
            meshObject.previousMetallic = metallic;
            meshObject.previousOpacity = opacity;
            meshObject.previousRefraction = refraction;
            meshObject.previousSpecular = specular;
            meshObject.previousTransmission = transmission;
            meshObject.previousMaterialType = materialType;
            meshObject.previousAlbedoTexture = albedoTexture;
            meshObject.previousMetallicRoughnessTexture = metallicRoughnessTexture;
            meshObject.previousNormalTexture = normalTexture;
            meshObject.previousParallaxTexture = parallaxTexture;
            meshObject.previousTextureUvScale = textureUvScale;
            meshObject.previousParallaxStrength = parallaxStrength;
            meshObject.previousMinimumParallaxStrength = minimumParallaxStrength;
            meshObject.previousInterpolateNormals = interpolateNormals;
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
            var material = meshObject.material;
            var light = meshObject.light;
            bool isLight = light != null;
            var color = material != null ? material.Color.ToVector3() : Vector3.one;
            var emission = isLight ? light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity) : Vector3.zero;
            float smoothness = material != null ? material.Smoothness : 0.0f;
            float metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            float opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            float refraction = material != null ? material.RefractionIndex : 1.0f;
            float specular = material != null ? Mathf.Clamp01(material.Specular) : 0.0f;
            float transmission = material != null ? Mathf.Clamp01(material.Transmission) : 1.0f;
            int materialType = isLight ? 3 : (int)material.Type;
            int textureIndex = material != null ? GetMeshAlbedoTextureIndex(material.AlbedoTexture) : -1;
            int metallicRoughnessTextureIndex = material != null ? GetMeshTextureIndex(material.MetallicRoughnessTexture, _meshMetallicRoughnessTextures) : -1;
            int normalTextureIndex = material != null ? GetMeshTextureIndex(material.NormalTexture, _meshNormalTextures) : -1;
            int parallaxTextureIndex = material != null ? GetMeshTextureIndex(material.ParallaxTexture, _meshParallaxTextures) : -1;

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
                triangle.color = color;
                triangle.emission = emission;
                triangle.smoothness = smoothness;
                triangle.metallic = metallic;
                triangle.opacity = opacity;
                triangle.refraction = refraction;
                triangle.specular = specular;
                triangle.transmission = transmission;
                triangle.materialType = materialType;
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                triangle.parallaxTextureIndex = parallaxTextureIndex;
                triangle.textureUvScale = material != null ? material.TextureUvScale : Vector2.one;
                triangle.parallaxStrength = material != null ? material.ParallaxStrength : 0.0f;
                triangle.minimumParallaxStrength = material != null ? Mathf.Min(material.MinimumParallaxStrength, material.ParallaxStrength) : 0.0f;
                _triangles[triangleIndex] = triangle;

            }

            if (isLight && lightIndex >= 0 && lightIndex < _lights.Count)
            {
                var meshLight = _lights[lightIndex];
                meshLight.emission = emission;
                _lights[lightIndex] = meshLight;
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
        int analyticLightCount = _lightObjects.Count + _directionalLights.Count * 2;
        if (_lights.Count > analyticLightCount)
        {
            _lights.RemoveRange(analyticLightCount, _lights.Count - analyticLightCount);
        }

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
            var material = meshObject.material;
            var light = meshObject.light;
            bool isLight = light != null;
            var color = material != null ? material.Color.ToVector3() : Vector3.one;
            var emission = isLight ? light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity) : Vector3.zero;
            var smoothness = material != null ? material.Smoothness : 0.0f;
            var metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            var specular = material != null ? Mathf.Clamp01(material.Specular) : 0.0f;
            var transmission = material != null ? Mathf.Clamp01(material.Transmission) : 1.0f;
            int materialType = isLight ? 3 : (int)material.Type;
            int textureIndex = material != null ? GetMeshAlbedoTextureIndex(material.AlbedoTexture) : -1;
            int metallicRoughnessTextureIndex = material != null ? GetMeshTextureIndex(material.MetallicRoughnessTexture, _meshMetallicRoughnessTextures) : -1;
            int normalTextureIndex = material != null ? GetMeshTextureIndex(material.NormalTexture, _meshNormalTextures) : -1;
            int parallaxTextureIndex = material != null ? GetMeshTextureIndex(material.ParallaxTexture, _meshParallaxTextures) : -1;
            bool interpolateNormals = material != null && material.InterpolateNormals;
            MeshBvhTemplate template = GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
            int triangleStart = _triangles.Count;
            int nodeStart = _bvhNodes.Count;
            int lightIndex = -1;
            float totalLightArea = 0.0f;
            Vector3 areaWeightedLightPosition = Vector3.zero;

            for (int i = 0; i < template.triangles.Count; i++)
            {
                Triangle triangle = TransformTemplateTriangle(template.triangles[i], localToWorld, normalToWorld);
                triangle.color = color;
                triangle.emission = emission;
                triangle.smoothness = smoothness;
                triangle.metallic = metallic;
                triangle.opacity = opacity;
                triangle.refraction = refraction;
                triangle.specular = specular;
                triangle.transmission = transmission;
                triangle.materialType = materialType;
                triangle.meshIndex = meshIndex;
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                triangle.parallaxTextureIndex = parallaxTextureIndex;
                triangle.textureUvScale = material != null ? material.TextureUvScale : Vector2.one;
                triangle.parallaxStrength = material != null ? material.ParallaxStrength : 0.0f;
                triangle.minimumParallaxStrength = material != null ? Mathf.Min(material.MinimumParallaxStrength, material.ParallaxStrength) : 0.0f;
                triangle.interpolateNormals = interpolateNormals ? 1 : 0;
                triangle.lightIndex = lightIndex;
                _triangles.Add(triangle);

                if (isLight)
                {
                    float area = GetTriangleArea(triangle.vertex0, triangle.vertex1, triangle.vertex2);
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
                lightIndex = _lights.Count;
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
                _lights.Add(new Light
                {
                    position = areaWeightedLightPosition / totalLightArea,
                    emission = emission,
                    type = (int)PathTracedLightType.Mesh,
                    triangleStart = triangleStart,
                    triangleCount = _triangles.Count - triangleStart,
                    totalArea = totalLightArea
                });
            }
        }

        RebuildMeshLightTriangleCdf();
        RebuildMeshTextureArrays();
    }

    private void RebuildMeshLightTriangleCdf()
    {
        _meshLightTriangleCdf.Clear();
        for (int i = 0; i < _triangles.Count; i++)
        {
            _meshLightTriangleCdf.Add(0.0f);
        }

        for (int meshIndex = 0; meshIndex < _meshInfos.Count; meshIndex++)
        {
            MeshInfo mesh = _meshInfos[meshIndex];
            if (mesh.lightIndex < 0 || mesh.lightIndex >= _lights.Count)
            {
                continue;
            }

            float cumulativeArea = 0.0f;
            for (int triangleIndex = mesh.triangleStart; triangleIndex < mesh.triangleStart + mesh.triangleCount; triangleIndex++)
            {
                Triangle triangle = _triangles[triangleIndex];
                cumulativeArea += GetTriangleArea(triangle.vertex0, triangle.vertex1, triangle.vertex2);
                _meshLightTriangleCdf[triangleIndex] = cumulativeArea;
            }

            if (cumulativeArea <= 0.000001f)
            {
                continue;
            }

            for (int triangleIndex = mesh.triangleStart; triangleIndex < mesh.triangleStart + mesh.triangleCount; triangleIndex++)
            {
                _meshLightTriangleCdf[triangleIndex] /= cumulativeArea;
            }
            _meshLightTriangleCdf[mesh.triangleStart + mesh.triangleCount - 1] = 1.0f;
        }
    }

    private MeshBvhTemplate GetOrBuildMeshBvhTemplate(Mesh mesh, bool interpolateNormals)
    {
        long key = ((long)mesh.GetInstanceID() << 1) | (interpolateNormals ? 1L : 0L);
        if (_meshBvhTemplates.TryGetValue(key, out MeshBvhTemplate template))
        {
            return template;
        }

        long start = Stopwatch.GetTimestamp();
        var vertices = mesh.vertices;
        var indices = mesh.triangles;
        var uvs = mesh.uv;
        var normals = mesh.normals;
        var tangents = mesh.tangents;
        bool useInterpolatedNormals = interpolateNormals && normals.Length == vertices.Length;
        bool hasTangents = tangents.Length == vertices.Length;
        var sourceTriangles = new List<Triangle>(indices.Length / 3);

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int index0 = indices[i];
            int index1 = indices[i + 1];
            int index2 = indices[i + 2];
            Vector3 vertex0 = vertices[index0];
            Vector3 vertex1 = vertices[index1];
            Vector3 vertex2 = vertices[index2];
            Vector3 normal = Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0).normalized;
            Vector3 normal0 = useInterpolatedNormals ? normals[index0].normalized : normal;
            Vector3 normal1 = useInterpolatedNormals ? normals[index1].normalized : normal;
            Vector3 normal2 = useInterpolatedNormals ? normals[index2].normalized : normal;
            sourceTriangles.Add(new Triangle
            {
                vertex0 = vertex0,
                vertex1 = vertex1,
                vertex2 = vertex2,
                normal = normal,
                normal0 = normal0,
                normal1 = normal1,
                normal2 = normal2,
                tangent0 = GetLocalTangent(tangents, index0, normal0, hasTangents),
                tangent1 = GetLocalTangent(tangents, index1, normal1, hasTangents),
                tangent2 = GetLocalTangent(tangents, index2, normal2, hasTangents),
                uv0 = GetMeshUv(uvs, index0),
                uv1 = GetMeshUv(uvs, index1),
                uv2 = GetMeshUv(uvs, index2),
                interpolateNormals = useInterpolatedNormals ? 1 : 0
            });
        }

        template = new MeshBvhTemplate();
        if (sourceTriangles.Count > 0)
        {
            BuildBvhNode(sourceTriangles, template.triangles, template.nodes, 0, sourceTriangles.Count);
        }
        _meshBvhTemplates.Add(key, template);
        _profileBuiltMeshTemplateCount++;
        _profileBuiltMeshTemplateTicks += Stopwatch.GetTimestamp() - start;
        return template;
    }

    private void TryLoadBakedMeshBvhs()
    {
        _loadedBakedMeshBvhs = false;
#if UNITY_EDITOR
        RayTracingBvhBakeAsset bvhBake = FindEditorBvhBake();
#else
        RayTracingBvhBakeAsset bvhBake = null;
#endif
        if (bvhBake == null)
        {
            _bvhBakeLoadStatus = "no bake assigned";
            return;
        }
        if (bvhBake.formatVersion != BvhBakeFormatVersion)
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
        if (!EditorIsBvhBakeCurrent())
        {
            return;
        }
#endif

        string path = Path.Combine(Application.streamingAssetsPath, bvhBake.streamingAssetsRelativePath);
        if (!File.Exists(path))
        {
            _bvhBakeLoadStatus = "binary file is missing";
            return;
        }

        try
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != BvhBakeMagic || reader.ReadInt32() != BvhBakeFormatVersion)
                {
                    _bvhBakeLoadStatus = "binary header is invalid";
                    return;
                }

                int meshCount = reader.ReadInt32();
                if (meshCount != bvhBake.meshes.Count)
                {
                    _bvhBakeLoadStatus = "binary mesh count does not match metadata";
                    return;
                }

                var loadedTemplates = new List<MeshBvhTemplate>(meshCount);
                for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
                {
                    var entry = bvhBake.meshes[meshIndex];
                    int triangleCount = reader.ReadInt32();
                    int nodeCount = reader.ReadInt32();
                    if (entry.mesh == null
                        || entry.mesh.vertexCount != entry.vertexCount
                        || GetMeshIndexCount(entry.mesh) != entry.indexCount
                        || triangleCount != entry.triangleCount
                        || nodeCount != entry.nodeCount)
                    {
                        _bvhBakeLoadStatus = $"mesh metadata mismatch at entry {meshIndex}";
                        return;
                    }

                    var template = new MeshBvhTemplate();
                    for (int i = 0; i < triangleCount; i++)
                    {
                        template.triangles.Add(ReadBakedTriangle(reader));
                    }
                    for (int i = 0; i < nodeCount; i++)
                    {
                        template.nodes.Add(ReadBakedBvhNode(reader));
                    }

                    loadedTemplates.Add(template);
                }

                if (stream.Position != stream.Length)
                {
                    _bvhBakeLoadStatus = "binary has unexpected trailing data";
                    return;
                }

                _meshBvhTemplates.Clear();
                foreach (var meshObject in _meshObjects)
                {
                    bool interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
                    int bakeIndex = FindBakedMeshEntry(meshObject.mesh, interpolateNormals);
                    if (bakeIndex < 0)
                    {
                        _meshBvhTemplates.Clear();
                        _bvhBakeLoadStatus = $"bake is out-of-date: no template for {meshObject.mesh.name}";
                        return;
                    }

                    long runtimeKey = ((long)meshObject.mesh.GetInstanceID() << 1) | (interpolateNormals ? 1L : 0L);
                    _meshBvhTemplates[runtimeKey] = loadedTemplates[bakeIndex];
                }
                _loadedBakedMeshBvhs = true;
                _bvhBakeLoadStatus = $"loaded {meshCount:N0} baked templates for {_meshBvhTemplates.Count:N0} runtime meshes";
            }
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
        RayTracingBvhBakeAsset bvhBake = FindEditorBvhBake();
        if (bvhBake == null)
        {
            return -1;
        }

        string identity = GetEditorMeshIdentity(mesh);
#else
        return -1;
#endif
        for (int i = 0; i < bvhBake.meshes.Count; i++)
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

#if UNITY_EDITOR
    private bool EditorIsBvhBakeCurrent()
    {
        RayTracingBvhBakeAsset bvhBake = FindEditorBvhBake();
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

            bool interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
            string key = GetEditorMeshIdentity(meshObject.mesh) + (interpolateNormals ? ":smooth" : ":flat");
            expectedKeys.Add(key);
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

            string key = entry.meshIdentity + (entry.interpolateNormals ? ":smooth" : ":flat");
            string path = UnityEditor.AssetDatabase.GetAssetPath(entry.mesh);
            string dependencyHash = string.IsNullOrEmpty(path)
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

    private RayTracingBvhBakeAsset FindEditorBvhBake()
    {
        if (string.IsNullOrEmpty(gameObject.scene.path))
        {
            return null;
        }

        string sceneGuid = UnityEditor.AssetDatabase.AssetPathToGUID(gameObject.scene.path);
        string managerId = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(this).targetObjectId.ToString();
        string path = $"Assets/Generated/RayTracingBvhBakes/{sceneGuid}_{managerId}.asset";
        return UnityEditor.AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(path);
    }
#endif

    private static Triangle ReadBakedTriangle(BinaryReader reader)
    {
        return new Triangle
        {
            vertex0 = ReadVector3(reader),
            vertex1 = ReadVector3(reader),
            vertex2 = ReadVector3(reader),
            normal = ReadVector3(reader),
            normal0 = ReadVector3(reader),
            normal1 = ReadVector3(reader),
            normal2 = ReadVector3(reader),
            tangent0 = ReadVector4(reader),
            tangent1 = ReadVector4(reader),
            tangent2 = ReadVector4(reader),
            uv0 = ReadVector2(reader),
            uv1 = ReadVector2(reader),
            uv2 = ReadVector2(reader),
            interpolateNormals = reader.ReadInt32()
        };
    }

    private static BvhNode ReadBakedBvhNode(BinaryReader reader)
    {
        return new BvhNode
        {
            boundsMin = ReadVector3(reader),
            leftChildIndex = reader.ReadInt32(),
            boundsMax = ReadVector3(reader),
            rightChildIndex = reader.ReadInt32(),
            triangleStart = reader.ReadInt32(),
            triangleCount = reader.ReadInt32()
        };
    }

    private static Vector2 ReadVector2(BinaryReader reader)
    {
        return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static int GetMeshIndexCount(Mesh mesh)
    {
        int count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            count += checked((int)mesh.GetIndexCount(i));
        }
        return count;
    }

#if UNITY_EDITOR
    public RayTracingBvhBakeAsset EditorBvhBake
    {
        get => FindEditorBvhBake();
    }

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
            bool interpolateNormals = meshObject.material != null && meshObject.material.InterpolateNormals;
            GetOrBuildMeshBvhTemplate(meshObject.mesh, interpolateNormals);
        }
    }

    public void EditorBuildMeshBvhTemplate(Mesh mesh, bool interpolateNormals)
    {
        GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
    }

    public void EditorGetMeshBvhTemplateCounts(Mesh mesh, bool interpolateNormals, out int triangleCount, out int nodeCount)
    {
        MeshBvhTemplate template = GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
        triangleCount = template.triangles.Count;
        nodeCount = template.nodes.Count;
    }

    public void EditorWriteMeshBvhBake(string path, RayTracingBvhBakeAsset asset)
    {
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(BvhBakeMagic);
            writer.Write(BvhBakeFormatVersion);
            writer.Write(asset.meshes.Count);
            for (int meshIndex = 0; meshIndex < asset.meshes.Count; meshIndex++)
            {
                var entry = asset.meshes[meshIndex];
                MeshBvhTemplate template = GetOrBuildMeshBvhTemplate(entry.mesh, entry.interpolateNormals);
                writer.Write(template.triangles.Count);
                writer.Write(template.nodes.Count);
                for (int i = 0; i < template.triangles.Count; i++)
                {
                    WriteBakedTriangle(writer, template.triangles[i]);
                }
                for (int i = 0; i < template.nodes.Count; i++)
                {
                    WriteBakedBvhNode(writer, template.nodes[i]);
                }
            }
        }
    }

    private static void WriteBakedTriangle(BinaryWriter writer, Triangle triangle)
    {
        WriteVector3(writer, triangle.vertex0);
        WriteVector3(writer, triangle.vertex1);
        WriteVector3(writer, triangle.vertex2);
        WriteVector3(writer, triangle.normal);
        WriteVector3(writer, triangle.normal0);
        WriteVector3(writer, triangle.normal1);
        WriteVector3(writer, triangle.normal2);
        WriteVector4(writer, triangle.tangent0);
        WriteVector4(writer, triangle.tangent1);
        WriteVector4(writer, triangle.tangent2);
        WriteVector2(writer, triangle.uv0);
        WriteVector2(writer, triangle.uv1);
        WriteVector2(writer, triangle.uv2);
        writer.Write(triangle.interpolateNormals);
    }

    private static void WriteBakedBvhNode(BinaryWriter writer, BvhNode node)
    {
        WriteVector3(writer, node.boundsMin);
        writer.Write(node.leftChildIndex);
        WriteVector3(writer, node.boundsMax);
        writer.Write(node.rightChildIndex);
        writer.Write(node.triangleStart);
        writer.Write(node.triangleCount);
    }

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
        writer.Write(value.w);
    }
#endif

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
        Vector3 center = (sourceMin + sourceMax) * 0.5f;
        Vector3 extents = (sourceMax - sourceMin) * 0.5f;
        Vector3 worldCenter = matrix.MultiplyPoint3x4(center);
        boundsMin = worldCenter;
        boundsMax = worldCenter;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 corner = matrix.MultiplyPoint3x4(center + Vector3.Scale(extents, new Vector3(x, y, z)));
            boundsMin = Vector3.Min(boundsMin, corner);
            boundsMax = Vector3.Max(boundsMax, corner);
        }
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

        int existingIndex = textures.IndexOf(texture);
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

    private static Vector4 GetLocalTangent(Vector4[] tangents, int index, Vector3 normal, bool hasTangents)
    {
        if (!hasTangents)
        {
            return Vector4.zero;
        }

        Vector4 source = tangents[index];
        Vector3 tangent = new Vector3(source.x, source.y, source.z);
        tangent = Vector3.ProjectOnPlane(tangent, normal).normalized;
        return new Vector4(tangent.x, tangent.y, tangent.z, source.w < 0.0f ? -1.0f : 1.0f);
    }

    private static Vector4 TransformTangent(Vector4 tangent, Vector3 normal, Matrix4x4 localToWorld)
    {
        Vector3 direction = localToWorld.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z));
        direction = Vector3.ProjectOnPlane(direction, normal).normalized;
        return new Vector4(direction.x, direction.y, direction.z, tangent.w);
    }

    private static float GetTriangleArea(Vector3 vertex0, Vector3 vertex1, Vector3 vertex2)
    {
        return Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0).magnitude * 0.5f;
    }

    private static Vector2 GetMeshUv(Vector2[] uvs, int vertexIndex)
    {
        return uvs != null && vertexIndex >= 0 && vertexIndex < uvs.Length ? uvs[vertexIndex] : Vector2.zero;
    }

    private void RebuildMeshTextureArrays()
    {
        long start = Stopwatch.GetTimestamp();
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
        int textureCount = Mathf.Max(1, textures.Count);
        int width = 1;
        int height = 1;
        for (int i = 0; i < textures.Count; i++)
        {
            if (textures[i] == null)
            {
                continue;
            }

            width = Mathf.Max(width, textures[i].width);
            height = Mathf.Max(height, textures[i].height);
        }

        var result = new Texture2DArray(width, height, textureCount, TextureFormat.RGBA32, false, linear)
        {
            name = arrayName,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };

        for (int i = 0; i < textureCount; i++)
        {
            Texture2D source = i < textures.Count ? textures[i] : fallback;
            CopyTextureToArraySlice(source, result, i, fallbackColor);
        }

        result.Apply(false, false);
        return result;
    }

    private static void CopyTextureToArraySlice(Texture2D source, Texture2DArray destination, int slice, Color fallbackColor)
    {
        int width = destination.width;
        int height = destination.height;
        var pixels = new Color32[width * height];
        if (source == null)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fallbackColor;
            }
        }
        else if (source.isReadable && source.width == width && source.height == height)
        {
            pixels = source.GetPixels32();
        }
        else if (source.isReadable)
        {
            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    pixels[y * width + x] = source.GetPixelBilinear(u, v);
                }
            }
        }
        else
        {
            RenderTexture previousRenderTexture = RenderTexture.active;
            RenderTexture temporaryRenderTexture = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                destination.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var readableTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, !destination.isDataSRGB);
            try
            {
                Graphics.Blit(source, temporaryRenderTexture);
                RenderTexture.active = temporaryRenderTexture;
                readableTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readableTexture.Apply(false, false);
                pixels = readableTexture.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previousRenderTexture;
                RenderTexture.ReleaseTemporary(temporaryRenderTexture);
                DestroyRuntimeObject(readableTexture);
            }
        }

        destination.SetPixels32(pixels, slice);
    }

    private void RebuildTopLevelBvh()
    {
        _topLevelBvhNodes.Clear();
        _topLevelBvhBuildItems.Clear();

        for (int i = 0; i < _spheres.Count; i++)
        {
            AddSphereTopLevelBvhItem(_topLevelBvhBuildItems, _spheres[i], TopLevelObjectTypeSphere, i);
        }

        for (int i = 0; i < _lights.Count; i++)
        {
            if (_lights[i].type != (int)PathTracedLightType.Sphere)
            {
                continue;
            }

            AddSphereTopLevelBvhItem(_topLevelBvhBuildItems, _lights[i], TopLevelObjectTypeLight, i);
        }

        for (int i = 0; i < _meshInfos.Count; i++)
        {
            _topLevelBvhBuildItems.Add(new TopLevelBvhBuildItem
            {
                boundsMin = _meshInfos[i].boundsMin,
                boundsMax = _meshInfos[i].boundsMax,
                objectType = TopLevelObjectTypeMesh,
                objectIndex = i
            });
        }

        if (_topLevelBvhBuildItems.Count < topLevelBvhMinObjectCount)
        {
            return;
        }

        if (_topLevelBvhBuildItems.Count > 0)
        {
            BuildTopLevelBvhNode(_topLevelBvhBuildItems, _topLevelBvhNodes, 0, _topLevelBvhBuildItems.Count, 1);
        }
    }

    private void RebuildShadowBvh()
    {
        _shadowBvhNodes.Clear();
        _shadowBvhBuildItems.Clear();

        for (int i = 0; i < _spheres.Count; i++)
        {
            AddSphereTopLevelBvhItem(_shadowBvhBuildItems, _spheres[i], TopLevelObjectTypeSphere, i);
        }

        for (int i = 0; i < _meshInfos.Count; i++)
        {
            if (_meshInfos[i].isLight != 0)
            {
                continue;
            }

            _shadowBvhBuildItems.Add(new TopLevelBvhBuildItem
            {
                boundsMin = _meshInfos[i].boundsMin,
                boundsMax = _meshInfos[i].boundsMax,
                objectType = TopLevelObjectTypeMesh,
                objectIndex = i
            });
        }

        if (_shadowBvhBuildItems.Count < shadowBvhMinObjectCount)
        {
            return;
        }

        if (_shadowBvhBuildItems.Count > 0)
        {
            BuildTopLevelBvhNode(_shadowBvhBuildItems, _shadowBvhNodes, 0, _shadowBvhBuildItems.Count, 1);
        }
    }

    private static void AddSphereTopLevelBvhItem(List<TopLevelBvhBuildItem> items, Sphere sphere, int objectType, int objectIndex)
    {
        var radius = Vector3.one * (sphere.radius + BvhBoundsPadding);
        items.Add(new TopLevelBvhBuildItem
        {
            boundsMin = sphere.position - radius,
            boundsMax = sphere.position + radius,
            objectType = objectType,
            objectIndex = objectIndex
        });
    }

    private static void AddSphereTopLevelBvhItem(List<TopLevelBvhBuildItem> items, Light light, int objectType, int objectIndex)
    {
        var radius = Vector3.one * (light.radius + BvhBoundsPadding);
        items.Add(new TopLevelBvhBuildItem
        {
            boundsMin = light.position - radius,
            boundsMax = light.position + radius,
            objectType = objectType,
            objectIndex = objectIndex
        });
    }

    private int BuildTopLevelBvhNode(List<TopLevelBvhBuildItem> items, List<TopLevelBvhNode> nodes, int start, int count, int depth)
    {
        if (depth > BvhStackSize)
        {
            throw new InvalidOperationException($"Top-level BVH depth {depth} exceeds traversal stack capacity {BvhStackSize}.");
        }

        var nodeIndex = nodes.Count;
        var boundsMin = items[start].boundsMin;
        var boundsMax = items[start].boundsMax;

        for (int i = start + 1; i < start + count; i++)
        {
            boundsMin = Vector3.Min(boundsMin, items[i].boundsMin);
            boundsMax = Vector3.Max(boundsMax, items[i].boundsMax);
        }

        nodes.Add(new TopLevelBvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = -1,
            rightChildIndex = -1,
            objectType = TopLevelObjectTypeInternal,
            objectIndex = -1
        });

        if (count == 1)
        {
            nodes[nodeIndex] = new TopLevelBvhNode
            {
                boundsMin = boundsMin,
                boundsMax = boundsMax,
                leftChildIndex = -1,
                rightChildIndex = -1,
                objectType = items[start].objectType,
                objectIndex = items[start].objectIndex
            };
            return nodeIndex;
        }

        _topLevelBvhBuildItemComparer.Axis = GetLongestAxis(boundsMax - boundsMin);
        int leftCount = ClampBvhSplitToDepth(FindTopLevelSahSplit(items, start, count), count, depth);

        int rightCount = count - leftCount;
        int leftChildIndex = BuildTopLevelBvhNode(items, nodes, start, leftCount, depth + 1);
        int rightChildIndex = BuildTopLevelBvhNode(items, nodes, start + leftCount, rightCount, depth + 1);

        nodes[nodeIndex] = new TopLevelBvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = leftChildIndex,
            rightChildIndex = rightChildIndex,
            objectType = TopLevelObjectTypeInternal,
            objectIndex = -1
        };

        return nodeIndex;
    }

    // Scores candidate top-level splits across all three axes by SAH and leaves items sorted on
    // the winning axis so the chosen split is contiguous. Falls back to a longest-axis median
    // split if no positive-area split is found.
    private int FindTopLevelSahSplit(List<TopLevelBvhBuildItem> items, int start, int count)
    {
        int bestAxis = -1;
        int bestSplit = count / 2;
        float bestCost = float.MaxValue;

        EnsureSahScratch(count);

        for (int axis = 0; axis < 3; axis++)
        {
            _topLevelBvhBuildItemComparer.Axis = axis;
            items.Sort(start, count, _topLevelBvhBuildItemComparer);

            var suffixMin = items[start + count - 1].boundsMin;
            var suffixMax = items[start + count - 1].boundsMax;
            _sahSuffixArea[count - 1] = HalfSurfaceArea(suffixMax - suffixMin);
            for (int i = count - 2; i >= 0; i--)
            {
                suffixMin = Vector3.Min(suffixMin, items[start + i].boundsMin);
                suffixMax = Vector3.Max(suffixMax, items[start + i].boundsMax);
                _sahSuffixArea[i] = HalfSurfaceArea(suffixMax - suffixMin);
            }

            var prefixMin = items[start].boundsMin;
            var prefixMax = items[start].boundsMax;
            for (int leftCount = 1; leftCount < count; leftCount++)
            {
                float leftArea = HalfSurfaceArea(prefixMax - prefixMin);
                float rightArea = _sahSuffixArea[leftCount];
                float cost = leftArea * leftCount + rightArea * (count - leftCount);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAxis = axis;
                    bestSplit = leftCount;
                }

                prefixMin = Vector3.Min(prefixMin, items[start + leftCount].boundsMin);
                prefixMax = Vector3.Max(prefixMax, items[start + leftCount].boundsMax);
            }
        }

        if (bestAxis < 0)
        {
            bestAxis = GetLongestAxis(items[start].boundsMax - items[start].boundsMin);
            bestSplit = count / 2;
        }

        _topLevelBvhBuildItemComparer.Axis = bestAxis;
        items.Sort(start, count, _topLevelBvhBuildItemComparer);

        return Mathf.Clamp(bestSplit, 1, count - 1);
    }

    private int BuildBvhNode(
        List<Triangle> meshTriangles,
        List<Triangle> outputTriangles,
        List<BvhNode> outputNodes,
        int start,
        int count,
        int depth = 1)
    {
        if (depth > BvhStackSize)
        {
            throw new InvalidOperationException($"Mesh BVH depth {depth} exceeds traversal stack capacity {BvhStackSize}.");
        }

        var nodeIndex = outputNodes.Count;
        var boundsMin = GetTriangleBoundsMin(meshTriangles[start]);
        var boundsMax = GetTriangleBoundsMax(meshTriangles[start]);

        for (int i = start + 1; i < start + count; i++)
        {
            Encapsulate(meshTriangles[i], ref boundsMin, ref boundsMax);
        }

        var padding = Vector3.one * BvhBoundsPadding;
        boundsMin -= padding;
        boundsMax += padding;

        outputNodes.Add(new BvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = -1,
            rightChildIndex = -1,
            triangleStart = -1,
            triangleCount = 0
        });

        if (count <= BvhLeafTriangleCount)
        {
            var triangleStart = outputTriangles.Count;
            for (int i = start; i < start + count; i++)
            {
                outputTriangles.Add(meshTriangles[i]);
            }

            outputNodes[nodeIndex] = new BvhNode
            {
                boundsMin = boundsMin,
                boundsMax = boundsMax,
                leftChildIndex = -1,
                rightChildIndex = -1,
                triangleStart = triangleStart,
                triangleCount = count
            };
            return nodeIndex;
        }

        int leftCount = FindTriangleMedianSplit(meshTriangles, start, count, boundsMin, boundsMax);

        int rightCount = count - leftCount;
        int leftChildIndex = BuildBvhNode(meshTriangles, outputTriangles, outputNodes, start, leftCount, depth + 1);
        int rightChildIndex = BuildBvhNode(meshTriangles, outputTriangles, outputNodes, start + leftCount, rightCount, depth + 1);

        outputNodes[nodeIndex] = new BvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = leftChildIndex,
            rightChildIndex = rightChildIndex,
            triangleStart = -1,
            triangleCount = 0
        };

        return nodeIndex;
    }

    private static int FindTriangleMedianSplit(
        List<Triangle> meshTriangles,
        int start,
        int count,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        int axis = GetLongestAxis(boundsMax - boundsMin);
        meshTriangles.Sort(start, count, Comparer<Triangle>.Create((a, b) =>
            GetTriangleCentroid(a)[axis].CompareTo(GetTriangleCentroid(b)[axis])));
        return count / 2;
    }

    private static Vector3 GetTriangleCentroid(Triangle triangle)
    {
        return (triangle.vertex0 + triangle.vertex1 + triangle.vertex2) / 3.0f;
    }

    private static Vector3 GetTriangleBoundsMin(Triangle triangle)
    {
        return Vector3.Min(triangle.vertex0, Vector3.Min(triangle.vertex1, triangle.vertex2));
    }

    private static Vector3 GetTriangleBoundsMax(Triangle triangle)
    {
        return Vector3.Max(triangle.vertex0, Vector3.Max(triangle.vertex1, triangle.vertex2));
    }

    private static void Encapsulate(Triangle triangle, ref Vector3 boundsMin, ref Vector3 boundsMax)
    {
        boundsMin = Vector3.Min(boundsMin, GetTriangleBoundsMin(triangle));
        boundsMax = Vector3.Max(boundsMax, GetTriangleBoundsMax(triangle));
    }

    private static int GetLongestAxis(Vector3 size)
    {
        if (size.x >= size.y && size.x >= size.z)
        {
            return 0;
        }

        return size.y >= size.z ? 1 : 2;
    }

    private static int ClampBvhSplitToDepth(int leftCount, int count, int depth)
    {
        // Bound each child's population by what the remaining binary-tree depth can hold.
        var remainingDepth = BvhStackSize - depth - 1;
        var maxChildCount = remainingDepth >= 30 ? int.MaxValue : 1 << remainingDepth;
        return Mathf.Clamp(leftCount, Mathf.Max(1, count - maxChildCount), Mathf.Min(count - 1, maxChildCount));
    }

    // Half the surface area of an AABB (the SA term used in the surface area heuristic). Half is
    // fine because the SAH compares ratios, so the constant factor cancels. Returns 0 for empty
    // or inverted bounds so degenerate nodes do not dominate the cost.
    private static float HalfSurfaceArea(Vector3 size)
    {
        if (size.x <= 0f && size.y <= 0f && size.z <= 0f)
        {
            return 0f;
        }

        var x = Mathf.Max(0f, size.x);
        var y = Mathf.Max(0f, size.y);
        var z = Mathf.Max(0f, size.z);
        return x * y + y * z + z * x;
    }

    private void EnsureSahScratch(int count)
    {
        if (_sahSuffixArea.Length < count)
        {
            _sahSuffixArea = new float[count];
        }
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

        var stack = new int[BvhStackSize];
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

            if (node.leftChildIndex >= 0 && stackCount < BvhStackSize)
            {
                stack[stackCount++] = node.leftChildIndex;
            }

            if (node.rightChildIndex >= 0 && stackCount < BvhStackSize)
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
        _lightBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
        _topLevelBvhNodeBuffer?.Release();
        _shadowBvhNodeBuffer?.Release();
        _meshLightTriangleCdfBuffer?.Release();
        _sphereBuffer = null;
        _lightBuffer = null;
        _triangleBuffer = null;
        _meshBuffer = null;
        _bvhNodeBuffer = null;
        _topLevelBvhNodeBuffer = null;
        _shadowBvhNodeBuffer = null;
        _meshLightTriangleCdfBuffer = null;

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
        RebuildTopLevelBvh();
        _topLevelBvhDirty = false;
        _lastTopLevelBvhMinObjectCount = topLevelBvhMinObjectCount;
        if (startupProfile)
        {
            AddStartupProfilePhase($"top-level BVH ({_topLevelBvhNodes.Count:N0} nodes)", phaseStart);
        }

        phaseStart = Stopwatch.GetTimestamp();
        RebuildShadowBvh();
        _shadowBvhDirty = false;
        _lastShadowBvhMinObjectCount = shadowBvhMinObjectCount;
        if (startupProfile)
        {
            AddStartupProfilePhase($"shadow BVH ({_shadowBvhNodes.Count:N0} nodes)", phaseStart);
        }

        shader.SetInt(NumSpheres, _spheres.Count);
        shader.SetInt(NumLights, _lights.Count);
        shader.SetInt(NumTriangles, _triangles.Count);
        shader.SetInt(NumMeshes, _meshInfos.Count);
        shader.SetInt(NumTopLevelBvhNodes, _topLevelBvhNodes.Count);
        shader.SetInt(NumShadowBvhNodes, _shadowBvhNodes.Count);

        phaseStart = Stopwatch.GetTimestamp();
        _sphereBuffer = CreateComputeBuffer(_spheres, SphereStride);
        _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        _triangleBuffer = CreateComputeBuffer(_triangles, TriangleStride);
        _meshBuffer = CreateComputeBuffer(_meshInfos, MeshInfoStride);
        _bvhNodeBuffer = CreateComputeBuffer(_bvhNodes, BvhNodeStride);
        _topLevelBvhNodeBuffer = CreateComputeBuffer(_topLevelBvhNodes, TopLevelBvhNodeStride);
        _shadowBvhNodeBuffer = CreateComputeBuffer(_shadowBvhNodes, TopLevelBvhNodeStride);
        _meshLightTriangleCdfBuffer = CreateComputeBuffer(_meshLightTriangleCdf, MeshLightTriangleCdfStride);
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
            $"{_triangles.Count:N0} triangles, {_lights.Count:N0} lights");
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

        _rayTracingObjects.Add(obj);
        _buffersNeedRebuilding = true;
        CameraManager.AutoFocusSceneChanged = true;
        ResetFrameAccumulation();

        var material = obj.GetComponent<RayMaterial>();
        var rayLight = obj.GetComponent<RayLight>();
        var sphereCollider = obj.GetComponent<SphereCollider>();

        if (material != null && sphereCollider != null)
        {
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

        // RayObjectPreview adds a MeshFilter to collider-backed lights for Scene-view display.
        // Prefer their analytic collider representation so that preview geometry cannot turn one
        // sphere light into an emissive triangle mesh.
        if (rayLight != null && sphereCollider != null)
        {
            var radius = GetWorldSphereRadius(sphereCollider, obj.transform);
            var lightData = new Light
            {
                position = obj.transform.TransformPoint(sphereCollider.center),
                radius = radius,
                area = Mathf.PI * radius * radius,
                emission = rayLight.Color.ToVector3() * Mathf.Max(0.0f, rayLight.Intensity),
                type = (int)PathTracedLightType.Sphere
            };
            int insertionIndex = _lightObjects.Count;
            _lights.Insert(insertionIndex, lightData);
            for (int i = 0; i < _triangles.Count; i++)
            {
                Triangle triangle = _triangles[i];
                if (triangle.lightIndex >= insertionIndex)
                {
                    triangle.lightIndex++;
                    _triangles[i] = triangle;
                }
            }
            _lightObjects.Add(new PathTracedLight
            {
                obj = obj,
                transform = obj.transform,
                light = rayLight,
                collider = sphereCollider
            });
            return;
        }

        var meshFilter = obj.GetComponent<MeshFilter>();
        if ((material != null || rayLight != null) && meshFilter != null && meshFilter.sharedMesh != null)
        {
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
                previousInterpolateNormals = material != null && material.InterpolateNormals
            });
            return;
        }

        Debug.LogWarning($"RayTracingObject '{obj.name}' needs RayMaterial with SphereCollider or MeshFilter, or RayLight with SphereCollider or MeshFilter.", obj);
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

        var lightIndex = _lightObjects.FindIndex(light => light.obj == obj);
        if (lightIndex >= 0)
        {
            _lightObjects.RemoveAt(lightIndex);
            _lights.RemoveAt(lightIndex);
            for (var i = 0; i < _triangles.Count; i++)
            {
                var triangle = _triangles[i];
                if (triangle.lightIndex > lightIndex)
                {
                    triangle.lightIndex--;
                    _triangles[i] = triangle;
                }
            }
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
        SetComputeBuffer(Lights, _lightBuffer, kernelHandle);
        SetComputeBuffer(Triangles, _triangleBuffer, kernelHandle);
        SetComputeBuffer(Meshes, _meshBuffer, kernelHandle);
        SetComputeBuffer(BvhNodes, _bvhNodeBuffer, kernelHandle);
        SetComputeBuffer(TopLevelBvhNodes, _topLevelBvhNodeBuffer, kernelHandle);
        SetComputeBuffer(ShadowBvhNodes, _shadowBvhNodeBuffer, kernelHandle);
        SetComputeBuffer(MeshLightTriangleCdf, _meshLightTriangleCdfBuffer, kernelHandle);
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

        foreach (var sphere in _lights)
        {
            if (sphere.type != (int)PathTracedLightType.Sphere)
            {
                continue;
            }

            var hitDistance = sphere.Intersect(ray.origin, ray.direction);

            if (hitDistance >= 0.0f && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
        }

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
        shader.SetTexture(kernelHandle, SkyboxTexture, skyboxTexture);
        EnsureMeshTextureArrays();
        shader.SetTexture(kernelHandle, MeshAlbedoTextures, _meshAlbedoTextureArray);
        shader.SetTexture(kernelHandle, MeshMetallicRoughnessTextures, _meshMetallicRoughnessTextureArray);
        shader.SetTexture(kernelHandle, MeshNormalTextures, _meshNormalTextureArray);
        shader.SetTexture(kernelHandle, MeshParallaxTextures, _meshParallaxTextureArray);

        shader.SetMatrix(CameraToWorld, renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix(CameraInverseProjection, renderTextureCamera.projectionMatrix.inverse);
        var temporalJitter = _temporalDenoisingManager.CurrentJitterNdc;
        shader.SetVector(FrameJitterNdc, new Vector4(temporalJitter.x, temporalJitter.y, 0.0f, 0.0f));
        shader.SetInt(UseTemporalJitter, ShouldRunTemporalDenoiser() ? 1 : 0);

        _skyboxLightColorAsVector = new Vector4(_skyboxLightColor.r / 255f, _skyboxLightColor.g / 255f, _skyboxLightColor.b / 255f, 1.0f);
        shader.SetVector(SkyboxLight, _skyboxLightColorAsVector);

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
        shader.SetInt(MaxLightSamples, maxLightSamples);
        shader.SetInt(SamplingStrategy, (int)lightSamplingStrategy);
        shader.SetInt(LightSampleCount, lightSampleCount);

        // Importance sampling can only weight up to MaxImportanceLights; warn once when the
        // scene exceeds that so the dropped lights are not a silent surprise.
        if (lightSamplingStrategy == LightSamplingStrategy.ImportanceSampled
            && _lights.Count > MaxImportanceLights)
        {
            if (!_warnedImportanceLightOverflow)
            {
                Debug.LogWarning(
                    $"ImportanceSampled light strategy supports up to {MaxImportanceLights} lights, " +
                    $"but the scene has {_lights.Count}. Lights beyond {MaxImportanceLights} are ignored " +
                    "for importance weighting. Raise MaxImportanceLights in RayTracingCompute.compute " +
                    "(and the matching constant in GameManager) or use a different light sampling strategy.");
                _warnedImportanceLightOverflow = true;
            }
        }
        else
        {
            _warnedImportanceLightOverflow = false;
        }
        shader.SetInt(Quality, shadowQuality);
        shader.SetFloat(ShadowRandomness, shadowRandomness);
        shader.SetFloat(ParallaxMaximumStrengthCosine, Mathf.Cos(Mathf.Clamp(parallaxMaximumStrengthAngle, 0.0f, 90.0f) * Mathf.Deg2Rad));
        shader.SetFloat(LightFalloffScale, lightFalloffScale);
        shader.SetFloat(FocalDistance, CameraManager.cameraFocalDistance);
        shader.SetFloat(ApertureRadius, CameraManager.GetApertureRadius());
        shader.SetInt(ApertureBladeCount, CameraManager.cameraApertureBladeCount >= 3 ? CameraManager.cameraApertureBladeCount : 0);
        shader.SetFloat(ApertureBladeRotation, CameraManager.cameraApertureBladeRotation * Mathf.Deg2Rad);
        shader.SetFloat(AnamorphicRatio, Mathf.Clamp(CameraManager.cameraAnamorphicRatio, 0.25f, 4.0f));
        shader.SetFloat(Exposure, exposure);
        shader.SetFloat(FireflyClamp, Mathf.Max(0.0f, fireflyClamp));
        WaterManager.SetShaderParameters(shader, Application.isPlaying ? GetRenderTime() : 0.0f);
        SetTerrainShaderParameters(kernelHandle);
        bool fogEnabled = IsFogEnabled();
        Vector3 fogCenter = fogEnabled ? _fogVolume.Center : Vector3.zero;
        Vector3 fogSize = fogEnabled ? _fogVolume.Size : Vector3.one;
        Color fogAlbedo = fogEnabled ? _fogVolume.ScatteringAlbedo : Color.black;
        shader.SetInt(FogEnabled, fogEnabled ? 1 : 0);
        Vector3 fogBoundsMin = fogCenter - fogSize * 0.5f;
        Vector3 fogBoundsMax = fogCenter + fogSize * 0.5f;
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
        shader.SetInt(NumLights, _lights.Count);
        shader.SetInt(NumTopLevelBvhNodes, _topLevelBvhNodes.Count);
        shader.SetInt(NumShadowBvhNodes, _shadowBvhNodes.Count);

        // When no shadow-casting blocker is transparent, the shader can use a cheaper
        // pure-occlusion shadow path that early-outs on the first opaque blocker.
        var hasTransparentShadowBlockers = _hasTransparentSphereBlockers || _hasTransparentMeshBlockers;
        shader.SetInt(HasTransparentShadowBlockers, hasTransparentShadowBlockers ? 1 : 0);
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
            hash = AddHash(hash, (int)lightSamplingStrategy);
            hash = AddHash(hash, lightSampleCount);
            hash = AddHash(hash, shadowRandomness);
            hash = AddHash(hash, parallaxMaximumStrengthAngle);
            hash = AddHash(hash, lightFalloffScale);
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
            hash = AddHash(hash, _skyboxLightColor.r);
            hash = AddHash(hash, _skyboxLightColor.g);
            hash = AddHash(hash, _skyboxLightColor.b);
            hash = AddHash(hash, _spheres.Count);
            for (var i = 0; i < _spheres.Count; i++)
            {
                hash = _spheres[i].AddHash(hash);
            }

            hash = AddHash(hash, _lights.Count);
            for (var i = 0; i < _lights.Count; i++)
            {
                hash = _lights[i].AddHash(hash);
            }

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

            hash = AddHash(hash, _lights.Count);
            for (var i = 0; i < _lights.Count; i++)
            {
                hash = _lights[i].AddHash(hash);
            }

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

}
