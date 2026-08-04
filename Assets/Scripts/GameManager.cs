using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using DefaultNamespace;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

public class GameManager : MonoBehaviour
{
    private const float MaxCameraPitch = 89.0f;

    [SerializeField, HideInInspector]
    private RayTracingBvhBakeAsset bvhBake;

    [SerializeField, HideInInspector]
    private bool bakeBvhUponExit = true;

    [Header("Diagnostics")]
    [Tooltip("Logs phase timings for initial scene buffer construction and the first compute dispatch.")]
    public bool profileStartup = true;

    public ComputeShader shader;
    public Camera renderTextureCamera;

    [Header("Spatial denoising")]
    [Tooltip("Applies an edge-aware spatial A-trous filter to linear HDR beauty. This does not use temporal history.")]
    public bool enableSpatialDenoising = false;

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
    public float spatialDenoiserLuminanceSigma = 0.03f;

    [Header("Temporal denoising")]
    [Tooltip("Uses camera-only temporal reprojection and bounded HDR accumulation while the camera moves, then allows progressive still accumulation when it stops.")]
    public bool enableTemporalDenoising = false;

    [Range(1, 64)]
    [Tooltip("Maximum effective temporal samples per pixel. Higher values reduce noise but respond more slowly to valid lighting changes.")]
    public int temporalMaxHistoryLength = 16;

    [Tooltip("Camera translation at or above this per-frame distance uses temporal accumulation instead of still-frame accumulation.")]
    [Min(0.00001f)]
    public float temporalMotionDistance = 0.0001f;

    [Tooltip("Camera rotation at or above this per-frame angle uses temporal accumulation instead of still-frame accumulation.")]
    [Min(0.0001f)]
    public float temporalMotionAngle = 0.01f;

    [Range(0.01f, 1.0f)]
    [Tooltip("Relative primary-hit depth difference allowed when validating reprojected history.")]
    public float temporalDepthThreshold = 0.05f;

    [Range(-1.0f, 1.0f)]
    [Tooltip("Minimum primary-hit normal dot product allowed when validating reprojected history.")]
    public float temporalNormalThreshold = 0.9f;

    [Tooltip("Camera translation at or above this distance is treated as a cut and resets temporal history.")]
    [Min(0.01f)]
    public float temporalCameraCutDistance = 5.0f;

    [Tooltip("Camera rotation at or above this angle is treated as a cut and resets temporal history.")]
    [Range(1.0f, 180.0f)]
    public float temporalCameraCutAngle = 45.0f;

    [Tooltip("Applies the spatial A-Trous passes to temporally accumulated radiance, using temporal luminance variance to relax filtering only where noise remains.")]
    public bool temporalVarianceGuidedFiltering = true;

    [Header("Caustic preservation")]
    [Tooltip("Prevents the denoiser from diffusing isolated HDR caustic candidates into neighboring receiver pixels. Higher values preserve only stronger local outliers.")]
    [Range(1.5f, 32.0f)]
    public float causticPreservationThreshold = 4.0f;
    
    [Header("Quality settings (Higher quality -> Slower)")]
    [Range(1, 32)]
    public int numberOfPasses = 1;

    [Tooltip("Progressively averages final-color renders while the camera, scene, and quality settings are unchanged. Debug render modes are not accumulated.")]
    public bool enableFrameAccumulation = true;

    [Range(1, 16)]
    public int numBounces = 3;

    [Range(0, 5)]
    public int shadowQuality = 2;

    [Tooltip("Use flat object loops below this count; set above the scene object count to force flat loops.")]
    [Range(0, 1024)]
    public int topLevelBvhMinObjectCount = 1024;

    [Tooltip("Use flat shadow blocker loops below this count; set above the blocker count to force flat shadow loops.")]
    [Range(0, 1024)]
    public int shadowBvhMinObjectCount = 1024;

    [Range(0f, 1.5f)]
    public float shadowRandomness = 0.3f;

    [Tooltip("Diagnostic: cap how many lights each shading point samples. 0 = sample all lights (normal). Lower values confirm the per-hit light loop is the bottleneck.")]
    [Range(0, 256)]
    public int maxLightSamples = 0;

    public enum LightSamplingStrategy
    {
        // Sample every light at each shading point. Most accurate per frame, cost scales with light count.
        AllLights = 0,
        // Pick one light at random per shading point, weighted by light count. O(1) lights per hit, noisier per frame.
        UniformRandom = 1,
        // Pick lights weighted by a cheap power/distance estimate, then divide by selection probability.
        // Unbiased like UniformRandom but concentrates samples on lights that matter, so much less noise per sample.
        ImportanceSampled = 2
    }

    [Tooltip("How direct lighting samples scene lights. AllLights is accurate but scales with light count; UniformRandom is much faster in many-light scenes but noisy; ImportanceSampled favors bright/nearby lights for much less noise per sample.")]
    public LightSamplingStrategy lightSamplingStrategy = LightSamplingStrategy.ImportanceSampled;

    [Tooltip("UniformRandom/ImportanceSampled only: how many lights each shading point samples per pass. 1 is fastest/noisiest; higher values reduce noise toward AllLights quality at proportional cost.")]
    [Range(1, 64)]
    public int lightSampleCount = 1;

    [Header("Caustics prototype")]
    [Tooltip("Builds a photon map for sphere and triangle-light caustics through glass, closed meshes, and the registered water volume. Disabled by default.")]
    public bool enableCaustics = false;

    [Range(64, 4194200)]
    [Tooltip("Photon attempts traced for each rendered frame. Independent batches are averaged by final-color frame accumulation.")]
    public int causticPhotonCount = 65536;

    [Range(0.01f, 2.0f)]
    public float causticGatherRadius = 0.2f;

    public int causticSeed = 1;

    [Range(0.0f, 10.0f)]
    public float causticIntensity = 1.0f;

    [Header("Volumetric fog")]
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
    
    [Header("Dynamic quality")]
    [Tooltip("Dynamically adjusts passes, light sampling, shadow quality, and bounces to approach the target frame rate. BVH thresholds are never changed.")]
    public bool enableDynamicQuality = false;

    [Tooltip("Target frame rate used as the dynamic-quality frame-time budget.")]
    [Range(15, 240)]
    public int dynamicQualityTargetFrameRate = 60;

    [Tooltip("Allowed over-budget frame-time error before dynamic quality reduces a setting.")]
    [Range(0.05f, 0.5f)]
    public float dynamicQualityTolerance = 0.15f;

    [Tooltip("Required under-budget headroom before dynamic quality increases a setting. Larger values reduce oscillation.")]
    [Range(0.1f, 0.75f)]
    public float dynamicQualityIncreaseHeadroom = 0.25f;

    // Must match MaxImportanceLights in RayTracingCompute.compute. Lights beyond this count
    // are ignored by the ImportanceSampled strategy.
    private const int MaxImportanceLights = 128;
    private bool _warnedImportanceLightOverflow = false;

    public enum DebugRenderMode
    {
        FinalColor = 0,
        Normals = 1,
        Albedo = 2,
        Emission = 3,
        DirectLight = 4,
        Throughput = 5,
        BounceCount = 6,
        HitDistance = 7,
        AccelerationStructures = 8,
        GlassScatter = 9,
        Caustics = 10,
        RawBeauty = 11,
        FeatureNormal = 12,
        FeatureAlbedo = 13,
        FeatureDepth = 14,
        FeatureIdentity = 15,
        FeatureValidity = 16,
        SpatialDenoised = 17,
        AtrousIteration1 = 18,
        AtrousIteration2 = 19,
        AtrousIteration3 = 20,
        MotionVectors = 21,
        TemporalReprojectedRadiance = 22,
        TemporalHistoryAcceptance = 23,
        TemporalRejectionReason = 24,
        TemporalDenoised = 25,
        TemporalHistoryLength = 26,
        TemporalDenoisedTint = 27,
        TemporalVariance = 28,
        CausticPreservationMask = 29
    }

    [Header("Debug render modes")]
    public DebugRenderMode debugRenderMode = DebugRenderMode.FinalColor;

    [Header("Camera focus and lens")]
    [Tooltip("Continuously focuses the center of the image. A successful click-to-focus selection disables this so the selected distance remains active.")]
    public bool cameraAutoFocus = true;

    [Tooltip("Left-click the rendered Game view to focus on the first qualifying ray-traced surface under the pointer.")]
    public bool enableClickToFocus = true;

    [Tooltip("Keeps a successful click-to-focus world point in focus as the camera moves. While the point is outside the camera frustum, depth of field temporarily uses a pinhole aperture without changing the selected aperture mode.")]
    public bool trackClickedFocusPoint = true;

    [Tooltip("Autofocus ignores ray-traced objects with opacity at or below this value, allowing focus through mostly transparent glass.")]
    [Range(0.0f, 1.0f)]
    public float autoFocusTransparentOpacityThreshold = 0.5f;
    
    [Min(0.1f)]
    public float cameraFocalDistance = 100f;

    public enum CameraApertureMode
    {
        Pinhole = 0,
        LensRadius = 1,
        FStop = 2
    }

    [Tooltip("Pinhole disables depth-of-field blur. Lens Radius gives direct artistic control. F-Stop derives aperture size from the Unity camera focal length.")]
    public CameraApertureMode cameraApertureMode = CameraApertureMode.LensRadius;

    [Tooltip("World-space aperture radius used in Lens Radius mode.")]
    [Range(0.0f, 0.1f)]
    public float cameraApertureRadius = 0.005f;

    [Tooltip("Photographic f-number used in F-Stop mode. Lower values create shallower depth of field.")]
    [Range(0.7f, 32.0f)]
    public float cameraFStop = 2.8f;

    [Tooltip("Scales the physical aperture derived from focal length and f-stop. A value of 1 assumes one world unit is one meter.")]
    [Range(0.01f, 100.0f)]
    public float cameraApertureScale = 1.0f;

    [Tooltip("0 uses a circular aperture. Values from 3 to 16 produce polygonal bokeh.")]
    [Range(0, 16)]
    public int cameraApertureBladeCount = 0;

    [Tooltip("Rotates polygonal aperture blades and their bokeh shape, in degrees.")]
    [Range(0.0f, 360.0f)]
    public float cameraApertureBladeRotation = 0.0f;

    [Tooltip("Stretches bokeh horizontally above 1 and vertically below 1 while preserving aperture area.")]
    [Range(0.25f, 4.0f)]
    public float cameraAnamorphicRatio = 1.0f;

    [Header("Misc settings")]

    [Tooltip("Higher values make direct light fall off faster with distance.")]
    [Range(0.001f, 1.0f)]
    public float lightFalloffScale = 0.16f;

    [Tooltip("Master brightness applied before ACES tone mapping. Acts like a camera exposure dial.")]
    [Range(0.0f, 8.0f)]
    public float exposure = 1.0f;

    [Tooltip("Maximum HDR luminance of one path sample before averaging. Lower positive values clamp fireflies more strongly; 0 disables the clamp.")]
    [Range(0.0f, 8.0f)]
    public float fireflyClamp = 1.0f;

    private float previousFocalDistance = 100f;
    private float timeSincePreviousFocusDistance = 1f;

    public bool randomNoise = false;

    public Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);

    public Texture skyboxTexture;

    [Tooltip("Fallback texture used when no mesh albedo textures are active. Created automatically at runtime if unset.")]
    public Texture2D defaultMeshAlbedoTexture;

    [Tooltip("Fallback texture used when no mesh metallic/roughness textures are active.")]
    public Texture2D defaultMeshMetallicRoughnessTexture;

    [Tooltip("Fallback texture used when no mesh normal textures are active.")]
    public Texture2D defaultMeshNormalTexture;

    [Header("Scene preview")]
    public bool syncUnitySkyboxToRayTracedSkybox = true;

    [Range(0.0f, 8.0f)]
    public float unitySkyboxExposure = 1.0f;

    [Range(0.0f, 360.0f)]
    public float unitySkyboxRotation = 0.0f;

    private Vector4 _skyboxLightColorAsVector;
    private Material _unitySkyboxMaterial;

    private RenderTexture _outputTexture;
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
    private RenderTexture _motionVectorTexture;
    private RenderTexture _temporalRadianceHistoryA;
    private RenderTexture _temporalRadianceHistoryB;
    private RenderTexture _temporalNormalHistoryA;
    private RenderTexture _temporalNormalHistoryB;
    private RenderTexture _temporalDepthHistoryA;
    private RenderTexture _temporalDepthHistoryB;
    private RenderTexture _temporalIdentityHistoryA;
    private RenderTexture _temporalIdentityHistoryB;
    private RenderTexture _temporalValidityHistoryA;
    private RenderTexture _temporalValidityHistoryB;
    private RenderTexture _temporalHistoryLengthA;
    private RenderTexture _temporalHistoryLengthB;
    private RenderTexture _temporalMomentsA;
    private RenderTexture _temporalMomentsB;
    private RenderTexture _temporalVarianceTexture;
    private RenderTexture _temporalReprojectedRadianceTexture;
    private RenderTexture _temporalDiagnosticsTexture;
    private bool _temporalHistoryReadIsA = true;
    private bool _temporalHistoryValid;
    private bool _temporalDynamicSceneChanged;
    private bool _hasTemporalStateHash;
    private int _temporalStateHash;
    private Matrix4x4 _currentUnjitteredViewProjection;
    private Matrix4x4 _previousUnjitteredViewProjection;
    private Vector2 _currentTemporalJitterNdc;
    private Vector2 _previousTemporalJitterNdc;
    private Vector3 _previousTemporalCameraPosition;
    private Quaternion _previousTemporalCameraRotation;
    private uint _temporalFrameIndex;
    private bool _hasRenderedCameraState;
    private Vector3 _lastRenderedCameraPosition;
    private Quaternion _lastRenderedCameraRotation;
    private Vector2Int _textureSize;
    private int _accumulatedFrameCount;
    private long _renderedFrameCount;
    private int _accumulationStateHash;
    private bool _hasAccumulationStateHash;
    private float _dynamicQualityAverageFrameMs;
    private float _dynamicQualityTimeSinceAdjustment;
    private bool _previousDynamicQualityEnabled;

    private List<Sphere> _spheres = new List<Sphere>();
    private readonly List<RayTracedSphere> _sphereObjects = new List<RayTracedSphere>();
    private ComputeBuffer _sphereBuffer;

    private List<Light> _lights = new List<Light>();
    private readonly List<RayTracedLight> _lightObjects = new List<RayTracedLight>();
    private ComputeBuffer _lightBuffer;

    private List<Triangle> _triangles = new List<Triangle>();
    private readonly List<MeshInfo> _meshInfos = new List<MeshInfo>();
    private readonly List<BvhNode> _bvhNodes = new List<BvhNode>();
    private readonly List<TopLevelBvhNode> _topLevelBvhNodes = new List<TopLevelBvhNode>();
    private readonly List<TopLevelBvhNode> _shadowBvhNodes = new List<TopLevelBvhNode>();
    private readonly List<TopLevelBvhBuildItem> _topLevelBvhBuildItems = new List<TopLevelBvhBuildItem>();
    private readonly List<TopLevelBvhBuildItem> _shadowBvhBuildItems = new List<TopLevelBvhBuildItem>();
    private readonly TopLevelBvhBuildItemComparer _topLevelBvhBuildItemComparer = new TopLevelBvhBuildItemComparer();
    private readonly List<RayTracedMesh> _meshObjects = new List<RayTracedMesh>();
    private readonly Dictionary<long, MeshBvhTemplate> _meshBvhTemplates = new Dictionary<long, MeshBvhTemplate>();
    private readonly List<Texture2D> _meshAlbedoTextures = new List<Texture2D>();
    private readonly List<Texture2D> _meshMetallicRoughnessTextures = new List<Texture2D>();
    private readonly List<Texture2D> _meshNormalTextures = new List<Texture2D>();
    private Texture2DArray _meshAlbedoTextureArray;
    private Texture2DArray _meshMetallicRoughnessTextureArray;
    private Texture2DArray _meshNormalTextureArray;
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _meshBuffer;
    private ComputeBuffer _bvhNodeBuffer;
    private ComputeBuffer _topLevelBvhNodeBuffer;
    private ComputeBuffer _shadowBvhNodeBuffer;
    private ComputeBuffer _focusQueryBuffer;
    private bool _focusQueryPending;
    private bool _focusQueryInFlight;
    private Vector2 _pendingFocusQueryUv;
    private Vector3 _focusQueryCameraPosition;
    private Vector3 _focusQueryCameraForward;
    private Vector3 _clickedFocusPoint;
    private bool _hasClickedFocusPoint;
    private bool _clickedFocusPointInFrustum;
    private int _focusQueryGeneration;
    private AsyncGPUReadbackRequest _focusReadbackRequest;
    private ComputeBuffer _causticPhotonBuffer;
    private ComputeBuffer _causticPhotonMetadataBuffer;
    private ComputeBuffer _causticGridCellHeadBuffer;
    private ComputeBuffer _causticPhotonNextBuffer;
    private ComputeBuffer _causticTargetPairBuffer;
    private ComputeBuffer _causticTargetTriangleBuffer;
    private readonly List<CausticTargetPair> _causticTargetPairs = new List<CausticTargetPair>();
    private readonly List<CausticTargetTriangle> _causticTargetTriangles = new List<CausticTargetTriangle>();
    private Vector3 _causticGridMin;
    private Vector3Int _causticGridDimensions;
    private float _causticGridCellSize;
    private int _causticGridCellCount;
    private int _causticGridOutOfBoundsCount;
    private int _causticGridPhotonCount;
    private int _causticPhotonStateHash;
    private bool _hasCausticPhotonStateHash;
    private int _causticFrameIndex;
    private bool _previousCausticsEnabled;
    private int _causticDispatchCount;

    // Tracks whether any shadow-casting blocker (regular sphere or mesh triangle) is transparent
    // (opacity < 1). When false, shadow rays in the shader take a cheaper pure-occlusion path that
    // early-outs on the first opaque blocker without the nearest-transparent-blocker bookkeeping.
    // Recomputed each frame in UpdateSpheres()/UpdateTriangles().
    private bool _hasTransparentSphereBlockers;
    private bool _hasTransparentMeshBlockers;
    private const float ShadowBlockerOpaqueThreshold = 1.0f;

    // Reusable suffix surface-area scratch for the SAH BVH split sweep, grown on demand so each
    // build does not allocate per node.
    private float[] _sahSuffixArea = new float[0];
    
    [Header("Render single frame")]
    [Tooltip("Freezes simulation time and progressively refines the current view. Camera and scene changes reset accumulation and render the updated view.")]
    public bool _singleFrame = false;

    private bool _previousSingleFrame;
    private float _singleFrameRenderTime;

    // Compute-shader variants compile synchronously on their first Dispatch, which freezes the
    // main thread (the spinning-wheel stall) the first time a debug render mode is selected. We
    // track which debug variants have already been dispatched, and when a new one is requested we
    // show an on-screen overlay for one frame BEFORE running the stalling dispatch, so the user
    // sees a "compiling" message instead of an apparently locked-up app.
    private readonly HashSet<int> _warmedShaderVariants = new HashSet<int>();
    private DebugRenderMode _appliedDebugRenderMode = DebugRenderMode.FinalColor;
    private bool _appliedCausticsEnabled;
    private bool _appliedFogEnabled;
    private bool _pendingVariantWarmup;

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
    public Vector2Int TextureSize => _textureSize;
    public int AccumulatedFrameCount => _accumulatedFrameCount;
    public float DynamicQualityAverageFrameMs => _dynamicQualityAverageFrameMs;
    public bool HasCausticResources => _causticPhotonBuffer != null && _causticPhotonMetadataBuffer != null
        && _causticGridCellHeadBuffer != null && _causticPhotonNextBuffer != null
        && _causticTargetPairBuffer != null && _causticTargetTriangleBuffer != null;
    public int CausticDispatchCount => _causticDispatchCount;
    public int CausticGridCellCount => _causticGridCellCount;
    public int CausticGridPhotonCount => _causticGridPhotonCount;
    public int CausticGridOutOfBoundsCount => _causticGridOutOfBoundsCount;
    public int CausticTargetPairCount => _causticTargetPairs.Count;
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
    public int TriangleLightCount => Mathf.Max(0, _lights.Count - _lightObjects.Count);
    public bool HasWaterVolume => _water != null;
    public bool IsVolumetricFogActive => IsFogEnabled();
    public float EffectiveFogDensity => IsFogEnabled() ? _fogVolume.Density * Mathf.Max(0.0f, fogDensityScale) : 0.0f;
    public Color EffectiveFogScatteringAlbedo => IsFogEnabled()
        ? _fogVolume.ScatteringAlbedo * Mathf.Max(0.0f, fogScatteringScale)
        : Color.black;

    private static bool _buffersNeedRebuilding = false;
    private static readonly List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();

    private const int SphereStride = 56;
    private const int LightStride = 72;
    private const int MinNumberOfPasses = 1;
    private const int MaxNumberOfPasses = 32;
    private const int MinNumBounces = 1;
    private const int MaxNumBounces = 16;
    private const int MinShadowQuality = 0;
    private const int MaxShadowQuality = 5;
    private const int MinDynamicLightSampleCount = 1;
    private const int MaxDynamicLightSampleCount = 64;
    private const int DynamicLightSampleDivisor = 10;
    private const float DynamicQualitySmoothing = 0.08f;
    private const float DynamicQualityAdjustmentInterval = 0.75f;
    private const int TriangleStride = 224;
    private const int MeshInfoStride = 48;
    private const int BvhNodeStride = 48;
    private const int TopLevelBvhNodeStride = 48;
    private const int CausticPhotonStride = 36;
    private const int CausticTargetPairStride = 32;
    private const int CausticTargetTriangleStride = 12;
    private const int CausticMetadataCount = 6;
    private const int CausticTraceThreadCount = 64;
    private const int RenderThreadCountX = 8;
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
    private const int LightTypeSphere = 0;
    private const int LightTypeTriangle = 1;

    private struct Sphere
    {
        public Vector3 position;
        public Vector3 color;
        public Vector3 emission;
        public float radius;
        public float smoothness;
        public float opacity;
        public float refraction;
        public int materialType;
        
        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var diffToSphere = position - origin;
            var b = Vector3.Dot(diffToSphere, direction);

            // ray is pointing away from sphere (b < 0)
            if (b < 0f)
            {
                return -1.0f;
            }
            
            var c = diffToSphere.sqrMagnitude - radius * radius;

            var discriminant = (b * b) - c; 

            // A negative discriminant corresponds to ray missing sphere 
            if (discriminant < 0.0f)
            {
                return -1.0f;
            } 

            // Ray now found to intersect sphere, compute smallest t value of intersection
            var hitDistance = b - Mathf.Sqrt(discriminant) - 0.001f;

            // If hit distance is negative, ray started inside sphere so clamp it to zero
            if (hitDistance < 0.0f)
            {
                hitDistance = 0.0f;
            }

            return hitDistance;
        }
    }

    private struct RayTracedSphere
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public SphereCollider collider;
    }

    private struct RayTracedLight
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayLight light;
        public SphereCollider collider;
    }

    private struct Light
    {
        public Vector3 position;
        public Vector3 emission;
        public Vector3 u;
        public float radius;
        public Vector3 v;
        public float area;
        public Vector3 normal;
        public int type;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var diffToSphere = position - origin;
            var b = Vector3.Dot(diffToSphere, direction);
            if (b < 0f)
            {
                return -1.0f;
            }

            var c = diffToSphere.sqrMagnitude - radius * radius;
            var discriminant = (b * b) - c;
            if (discriminant < 0.0f)
            {
                return -1.0f;
            }

            var hitDistance = b - Mathf.Sqrt(discriminant) - 0.001f;
            return hitDistance < 0.0f ? 0.0f : hitDistance;
        }
    }

    private struct Triangle
    {
        public Vector3 vertex0;
        public Vector3 vertex1;
        public Vector3 vertex2;
        public Vector3 normal;
        public Vector3 normal0;
        public Vector3 normal1;
        public Vector3 normal2;
        public Vector4 tangent0;
        public Vector4 tangent1;
        public Vector4 tangent2;
        public Vector3 color;
        public float smoothness;
        public float metallic;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector2 uv2;
        public float opacity;
        public Vector3 emission;
        public float refraction;
        public int materialType;
        public int meshIndex;
        public int textureIndex;
        public int metallicRoughnessTextureIndex;
        public int normalTextureIndex;
        public int interpolateNormals;
        public int lightIndex;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var edge1 = vertex1 - vertex0;
            var edge2 = vertex2 - vertex0;
            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);
            var determinantScale = edge1.magnitude * p.magnitude;

            if (determinantScale <= 0.0f || Mathf.Abs(determinant) <= 0.000001f * determinantScale)
            {
                return -1.0f;
            }

            var inverseDeterminant = 1.0f / determinant;
            var t = origin - vertex0;
            var u = Vector3.Dot(t, p) * inverseDeterminant;

            if (u < 0.0f || u > 1.0f)
            {
                return -1.0f;
            }

            var q = Vector3.Cross(t, edge1);
            var v = Vector3.Dot(direction, q) * inverseDeterminant;

            if (v < 0.0f || u + v > 1.0f)
            {
                return -1.0f;
            }

            var hitDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
            return hitDistance > 0.001f ? hitDistance : -1.0f;
        }
    }

    private struct MeshInfo
    {
        public Vector3 boundsMin;
        public int rootNodeIndex;
        public Vector3 boundsMax;
        public int triangleStart;
        public int triangleCount;
        public int meshIndex;
        public int isLight;
        public int padding1;
    }

    private struct CausticTargetPair
    {
        public int lightIndex;
        public int refractorType;
        public int refractorIndex;
        public int triangleStart;
        public int triangleCount;
        public float cumulativeProbability;
        public float selectionProbability;
        public float padding;
    }

    private struct CausticTargetTriangle
    {
        public int triangleIndex;
        public float cumulativeProbability;
        public float selectionProbability;
    }

    private struct BvhNode
    {
        public Vector3 boundsMin;
        public int leftChildIndex;
        public Vector3 boundsMax;
        public int rightChildIndex;
        public int triangleStart;
        public int triangleCount;
        public int padding0;
        public int padding1;
    }

    private struct TopLevelBvhNode
    {
        public Vector3 boundsMin;
        public int leftChildIndex;
        public Vector3 boundsMax;
        public int rightChildIndex;
        public int objectType;
        public int objectIndex;
        public int padding0;
        public int padding1;
    }

    private struct TopLevelBvhBuildItem
    {
        public Vector3 boundsMin;
        public Vector3 boundsMax;
        public int objectType;
        public int objectIndex;
    }

    private class TopLevelBvhBuildItemComparer : IComparer<TopLevelBvhBuildItem>
    {
        public int axis;

        public int Compare(TopLevelBvhBuildItem x, TopLevelBvhBuildItem y)
        {
            return GetTopLevelBvhItemCentroid(x)[axis].CompareTo(GetTopLevelBvhItemCentroid(y)[axis]);
        }
    }

    private struct RayTracedMesh
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public RayLight light;
        public Mesh mesh;
        public Matrix4x4 previousLocalToWorld;
        public Vector3 previousColor;
        public Vector3 previousEmission;
        public float previousSmoothness;
        public float previousMetallic;
        public float previousOpacity;
        public float previousRefraction;
        public int previousMaterialType;
        public Texture2D previousAlbedoTexture;
        public Texture2D previousMetallicRoughnessTexture;
        public Texture2D previousNormalTexture;
        public bool previousInterpolateNormals;
    }

    private sealed class MeshBvhTemplate
    {
        public readonly List<Triangle> triangles = new List<Triangle>();
        public readonly List<BvhNode> nodes = new List<BvhNode>();
    }

    private Water _water;
    private FogVolume _fogVolume;
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private readonly List<string> _startupProfilePhases = new List<string>();
    private double _startupRegistrationMilliseconds;
    private bool _startupProfilePending;
    private bool _loadedBakedMeshBvhs;
    private string _bvhBakeLoadStatus = "not attempted";
    private int _profileBuiltMeshTemplateCount;
    private long _profileBuiltMeshTemplateTicks;
    private long _profileTextureArrayTicks;

    private void Start()
    {
        _startupRegistrationMilliseconds = _startupStopwatch.Elapsed.TotalMilliseconds;
        _startupProfilePending = profileStartup;
        EnsureBenchmarkComponents();
        SyncUnitySkyboxPreview();
        long outputTextureStart = Stopwatch.GetTimestamp();
        CreateOutputTexture(Screen.width, Screen.height);
        AddStartupProfilePhase("output textures", outputTextureStart);
        long bakedBvhLoadStart = Stopwatch.GetTimestamp();
        TryLoadBakedMeshBvhs();
        AddStartupProfilePhase($"baked mesh BVH load ({_bvhBakeLoadStatus})", bakedBvhLoadStart);
        RebuildBuffers(_startupProfilePending);
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
        temporalDepthThreshold = Mathf.Max(0.01f, temporalDepthThreshold);
        temporalNormalThreshold = Mathf.Clamp(temporalNormalThreshold, -1.0f, 1.0f);
        temporalCameraCutDistance = Mathf.Max(0.01f, temporalCameraCutDistance);
        temporalCameraCutAngle = Mathf.Clamp(temporalCameraCutAngle, 1.0f, 180.0f);
        temporalMaxHistoryLength = Mathf.Clamp(temporalMaxHistoryLength, 1, 64);
        temporalMotionDistance = Mathf.Max(0.00001f, temporalMotionDistance);
        temporalMotionAngle = Mathf.Max(0.0001f, temporalMotionAngle);
        causticPreservationThreshold = Mathf.Clamp(causticPreservationThreshold, 1.5f, 32.0f);
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
        _outputTexture = new RenderTexture(_textureSize.x, _textureSize.y, 24)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        _outputTexture.Create();

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
        ResetTemporalHistory();
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
        if (_spatialDenoiserShader == null)
        {
            _spatialDenoiserShader = Resources.Load<ComputeShader>("RayTracingSpatialDenoiser");
        }

        if (_spatialDenoiserShader == null)
        {
            Debug.LogError("Spatial denoiser shader was not found at Resources/RayTracingSpatialDenoiser.", this);
            return;
        }

        if (_denoiserPingTexture != null)
        {
            return;
        }

        _denoiserPingTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _denoiserPongTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _denoiserIteration1Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _denoiserIteration2Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _denoiserIteration3Texture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _causticPreservationMaskTexture = CreateFeatureTexture(RenderTextureFormat.RHalf);
    }

    private void ReleaseSpatialDenoiserResources()
    {
        _denoiserPingTexture?.Release();
        _denoiserPongTexture?.Release();
        _denoiserIteration1Texture?.Release();
        _denoiserIteration2Texture?.Release();
        _denoiserIteration3Texture?.Release();
        _causticPreservationMaskTexture?.Release();
        _denoiserPingTexture = null;
        _denoiserPongTexture = null;
        _denoiserIteration1Texture = null;
        _denoiserIteration2Texture = null;
        _denoiserIteration3Texture = null;
        _causticPreservationMaskTexture = null;
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
        return debugRenderMode == DebugRenderMode.MotionVectors
            || debugRenderMode == DebugRenderMode.TemporalReprojectedRadiance
            || debugRenderMode == DebugRenderMode.TemporalHistoryAcceptance
            || debugRenderMode == DebugRenderMode.TemporalRejectionReason
            || debugRenderMode == DebugRenderMode.TemporalDenoised
            || debugRenderMode == DebugRenderMode.TemporalHistoryLength
            || debugRenderMode == DebugRenderMode.TemporalDenoisedTint
            || debugRenderMode == DebugRenderMode.TemporalVariance;
    }

    private bool ShouldRunTemporalDenoiser()
    {
        // Keep temporal history warm while the camera is still. The still-image path remains the
        // presented output, but its stable history is immediately available when motion resumes.
        return enableTemporalDenoising || IsTemporalDebugMode();
    }

    private bool IsCausticPreservationDebugMode()
    {
        return debugRenderMode == DebugRenderMode.CausticPreservationMask;
    }

    private bool ShouldUseTemporalAccumulation()
    {
        return enableTemporalDenoising && debugRenderMode == DebugRenderMode.FinalColor && IsCameraMoving();
    }

    private bool IsCameraMoving()
    {
        return _hasRenderedCameraState
            && (Vector3.Distance(renderTextureCamera.transform.position, _lastRenderedCameraPosition) >= temporalMotionDistance
                || Quaternion.Angle(renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) >= temporalMotionAngle);
    }

    private void EnsureTemporalDenoiserResources()
    {
        EnsureSpatialDenoiserResources();
        if (_spatialDenoiserShader == null || _temporalRadianceHistoryA != null)
        {
            return;
        }

        _motionVectorTexture = CreateFeatureTexture(RenderTextureFormat.RGHalf);
        _temporalRadianceHistoryA = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _temporalRadianceHistoryB = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _temporalNormalHistoryA = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _temporalNormalHistoryB = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _temporalDepthHistoryA = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalDepthHistoryB = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalIdentityHistoryA = CreateFeatureTexture(RenderTextureFormat.RFloat);
        _temporalIdentityHistoryB = CreateFeatureTexture(RenderTextureFormat.RFloat);
        _temporalValidityHistoryA = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalValidityHistoryB = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalHistoryLengthA = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalHistoryLengthB = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalMomentsA = CreateFeatureTexture(RenderTextureFormat.RGHalf);
        _temporalMomentsB = CreateFeatureTexture(RenderTextureFormat.RGHalf);
        _temporalVarianceTexture = CreateFeatureTexture(RenderTextureFormat.RHalf);
        _temporalReprojectedRadianceTexture = CreateFeatureTexture(RenderTextureFormat.ARGBHalf);
        _temporalDiagnosticsTexture = CreateFeatureTexture(RenderTextureFormat.RGHalf);
    }

    private void ReleaseTemporalDenoiserResources()
    {
        _motionVectorTexture?.Release();
        _temporalRadianceHistoryA?.Release();
        _temporalRadianceHistoryB?.Release();
        _temporalNormalHistoryA?.Release();
        _temporalNormalHistoryB?.Release();
        _temporalDepthHistoryA?.Release();
        _temporalDepthHistoryB?.Release();
        _temporalIdentityHistoryA?.Release();
        _temporalIdentityHistoryB?.Release();
        _temporalValidityHistoryA?.Release();
        _temporalValidityHistoryB?.Release();
        _temporalHistoryLengthA?.Release();
        _temporalHistoryLengthB?.Release();
        _temporalMomentsA?.Release();
        _temporalMomentsB?.Release();
        _temporalVarianceTexture?.Release();
        _temporalReprojectedRadianceTexture?.Release();
        _temporalDiagnosticsTexture?.Release();
        _motionVectorTexture = null;
        _temporalRadianceHistoryA = null;
        _temporalRadianceHistoryB = null;
        _temporalNormalHistoryA = null;
        _temporalNormalHistoryB = null;
        _temporalDepthHistoryA = null;
        _temporalDepthHistoryB = null;
        _temporalIdentityHistoryA = null;
        _temporalIdentityHistoryB = null;
        _temporalValidityHistoryA = null;
        _temporalValidityHistoryB = null;
        _temporalHistoryLengthA = null;
        _temporalHistoryLengthB = null;
        _temporalMomentsA = null;
        _temporalMomentsB = null;
        _temporalVarianceTexture = null;
        _temporalReprojectedRadianceTexture = null;
        _temporalDiagnosticsTexture = null;
        _temporalHistoryReadIsA = true;
    }

    private void Update()
    {
        if (_buffersNeedRebuilding)
        {
            RebuildBuffers();
        }

        if (_singleFrame != _previousSingleFrame)
        {
            SetSingleFrameMode(_singleFrame);
        }

        if (enableDynamicQuality != _previousDynamicQualityEnabled)
        {
            ResetDynamicQualityState();
            _previousDynamicQualityEnabled = enableDynamicQuality;
        }

        UpdateDynamicQuality();

        HandleInputForCamera(renderTextureCamera);
        HandleClickToFocusInput();

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
        if (!_pendingVariantWarmup)
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

        GUI.Box(rect, "Compiling shader variant, this may take a minute...", _compileNoticeStyle);
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

    private void EnableSingleFrameSettings()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 10;
        Time.timeScale = 0.0f;
    }

    private void EnableRealtimeSettings()
    {
        QualitySettings.vSyncCount = 2;
        Application.targetFrameRate = 60;
        Time.timeScale = 1.0f;
    }
    
    private void HandleInputForCamera(Camera camera)
    {
        float movementDelta = Time.unscaledDeltaTime;

        if (Input.GetKey(KeyCode.W))
        {
            camera.transform.position += camera.transform.forward * movementDelta * 3f;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            camera.transform.position -= camera.transform.forward * movementDelta * 3f;
        }
        
        if (Input.GetKey(KeyCode.A))
        {
            camera.transform.position -= camera.transform.right * movementDelta * 3f;
        }
        else if (Input.GetKey(KeyCode.D))
        {
            camera.transform.position += camera.transform.right * movementDelta * 3f;
        }
        
        float yawDelta = 0.0f;
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            yawDelta = -movementDelta * 50.0f;
        }
        else if (Input.GetKey(KeyCode.RightArrow))
        {
            yawDelta = movementDelta * 50.0f;
        }

        float pitchDelta = 0.0f;
        if (Input.GetKey(KeyCode.UpArrow))
        {
            pitchDelta = movementDelta * 50.0f;
        }
        else if (Input.GetKey(KeyCode.DownArrow))
        {
            pitchDelta = -movementDelta * 50.0f;
        }

        if (yawDelta != 0.0f || pitchDelta != 0.0f)
        {
            RotateCamera(camera.transform, yawDelta, pitchDelta);
        }
    }

    private static void RotateCamera(Transform cameraTransform, float yawDelta, float pitchDelta)
    {
        Vector3 eulerAngles = cameraTransform.eulerAngles;
        float pitch = Mathf.DeltaAngle(0.0f, eulerAngles.x);
        eulerAngles.x = Mathf.Clamp(pitch + pitchDelta, -MaxCameraPitch, MaxCameraPitch);
        eulerAngles.y += yawDelta;
        cameraTransform.eulerAngles = eulerAngles;
    }

    private void HandleClickToFocusInput()
    {
        if (!enableClickToFocus || _focusQueryInFlight || !Input.GetMouseButtonDown(0) || renderTextureCamera == null)
        {
            return;
        }

        Rect pixelRect = renderTextureCamera.pixelRect;
        Vector2 mousePosition = Input.mousePosition;
        if (!pixelRect.Contains(mousePosition) || pixelRect.width <= 0.0f || pixelRect.height <= 0.0f)
        {
            return;
        }

        Vector2 viewportPosition = new Vector2(
            (mousePosition.x - pixelRect.x) / pixelRect.width,
            (mousePosition.y - pixelRect.y) / pixelRect.height);
        _pendingFocusQueryUv = viewportPosition * 2.0f - Vector2.one;
        _focusQueryPending = true;
    }
    
    private void OnDestroy()
    {
        _outputTexture?.Release();
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
        if (_focusQueryInFlight)
        {
            _focusReadbackRequest.WaitForCompletion();
        }
        _focusQueryGeneration++;
        _focusQueryBuffer?.Release();
        _focusQueryBuffer = null;
        ReleaseCausticResources();
        DestroyRuntimeTextureArrays();
    }

    private void DestroyRuntimeTextureArrays()
    {
        DestroyRuntimeTextureArray(_meshAlbedoTextureArray);
        DestroyRuntimeTextureArray(_meshMetallicRoughnessTextureArray);
        DestroyRuntimeTextureArray(_meshNormalTextureArray);
        _meshAlbedoTextureArray = null;
        _meshMetallicRoughnessTextureArray = null;
        _meshNormalTextureArray = null;
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
        if (_outputTexture == null || width != _textureSize.x || height != _textureSize.y)
        {
            CreateOutputTexture(width, height);
        }

        renderTextureCamera.aspect = (float)_textureSize.x / _textureSize.y;
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

            if (iteration == 0)
            {
                Graphics.CopyTexture(output, _denoiserIteration1Texture);
            }
            else if (iteration == 1)
            {
                Graphics.CopyTexture(output, _denoiserIteration2Texture);
            }
            else if (iteration == 2)
            {
                Graphics.CopyTexture(output, _denoiserIteration3Texture);
            }

            input = output;
            output = output == _denoiserPingTexture ? _denoiserPongTexture : _denoiserPingTexture;
        }

        RenderTexture presentationInput = input;
        if (debugRenderMode == DebugRenderMode.AtrousIteration1)
        {
            presentationInput = _denoiserIteration1Texture;
        }
        else if (debugRenderMode == DebugRenderMode.AtrousIteration2 && iterations >= 2)
        {
            presentationInput = _denoiserIteration2Texture;
        }
        else if (debugRenderMode == DebugRenderMode.AtrousIteration3 && iterations >= 3)
        {
            presentationInput = _denoiserIteration3Texture;
        }

        int presentKernel = _spatialDenoiserShader.FindKernel("CSPresent");
        _spatialDenoiserShader.SetTexture(presentKernel, "InputBeauty", presentationInput);
        _spatialDenoiserShader.SetTexture(presentKernel, "PresentationResult", _outputTexture);
        _spatialDenoiserShader.SetFloat("_Exposure", exposure);
        _spatialDenoiserShader.Dispatch(presentKernel, threadGroupsX, threadGroupsY, 1);
    }

    private void RunTemporalDenoiser()
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
        _spatialDenoiserShader.SetFloat("_TemporalDepthThreshold", temporalDepthThreshold);
        _spatialDenoiserShader.SetFloat("_TemporalNormalThreshold", temporalNormalThreshold);
        _spatialDenoiserShader.SetInt("_TemporalMaxHistoryLength", temporalMaxHistoryLength);
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
            _spatialDenoiserShader.SetInt("_TemporalMaxHistoryLength", temporalMaxHistoryLength);
            _spatialDenoiserShader.Dispatch(visualizeKernel, threadGroupsX, threadGroupsY, 1);
        }
        else if (ShouldUseTemporalAccumulation())
        {
            if (temporalVarianceGuidedFiltering)
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

    private void PresentCausticPreservationMask()
    {
        EnsureSpatialDenoiserResources();
        if (_spatialDenoiserShader == null || _causticPreservationMaskTexture == null)
        {
            return;
        }

        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        GenerateCausticPreservationMask(threadGroupsX, threadGroupsY);
        int kernel = _spatialDenoiserShader.FindKernel("CSVisualizeTemporal");
        _spatialDenoiserShader.SetTexture(kernel, "PreservationMask", _causticPreservationMaskTexture);
        _spatialDenoiserShader.SetTexture(kernel, "PresentationResult", _outputTexture);
        _spatialDenoiserShader.SetInt("_TemporalDebugMode", 9);
        _spatialDenoiserShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    private void ResetFrameAccumulation()
    {
        _accumulatedFrameCount = 0;
        _hasAccumulationStateHash = false;
    }

    private void ResetTemporalHistory()
    {
        _temporalHistoryValid = false;
        _hasTemporalStateHash = false;
        _temporalHistoryReadIsA = true;
    }

    private bool IsTemporalPathUnsupported()
    {
        // Pinhole primary features still provide conservative surface motion under depth of field.
        // Difficult primary materials are classified per pixel through FeatureNormal.a. Static
        // water and transmission use capped history; animated water still lacks wave motion.
        return IsFogEnabled()
            || _temporalDynamicSceneChanged
            || (_water != null && _water.WaveSpeed > 0.0f && _water.WaveAmplitude > 0.0f);
    }

    private void PrepareTemporalCameraState()
    {
        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(renderTextureCamera.projectionMatrix, true);
        _currentUnjitteredViewProjection = gpuProjection * renderTextureCamera.worldToCameraMatrix;
        _currentTemporalJitterNdc = GetTemporalJitterNdc(_temporalFrameIndex, _textureSize);
        int stateHash = CalculateTemporalStateHash();
        bool cameraCut = _temporalHistoryValid && (Vector3.Distance(renderTextureCamera.transform.position, _previousTemporalCameraPosition) >= temporalCameraCutDistance
            || Quaternion.Angle(renderTextureCamera.transform.rotation, _previousTemporalCameraRotation) >= temporalCameraCutAngle);
        if (!_hasTemporalStateHash || stateHash != _temporalStateHash || cameraCut)
        {
            ResetTemporalHistory();
            _temporalStateHash = stateHash;
            _hasTemporalStateHash = true;
        }

        if (!_temporalHistoryValid)
        {
            _previousUnjitteredViewProjection = _currentUnjitteredViewProjection;
            _previousTemporalJitterNdc = _currentTemporalJitterNdc;
        }
    }

    private void CommitTemporalCameraState()
    {
        _previousUnjitteredViewProjection = _currentUnjitteredViewProjection;
        _previousTemporalJitterNdc = _currentTemporalJitterNdc;
        _previousTemporalCameraPosition = renderTextureCamera.transform.position;
        _previousTemporalCameraRotation = renderTextureCamera.transform.rotation;
        _temporalFrameIndex++;
    }

    private void CommitRenderedCameraState()
    {
        _lastRenderedCameraPosition = renderTextureCamera.transform.position;
        _lastRenderedCameraRotation = renderTextureCamera.transform.rotation;
        _hasRenderedCameraState = true;
    }

    private static Vector2 GetTemporalJitterNdc(uint frameIndex, Vector2Int size)
    {
        float Halton(uint index, uint radix)
        {
            float result = 0.0f;
            float fraction = 1.0f / radix;
            while (index > 0)
            {
                result += fraction * (index % radix);
                index /= radix;
                fraction /= radix;
            }
            return result;
        }

        return new Vector2(
            (Halton(frameIndex + 1, 2) - 0.5f) * 2.0f / Mathf.Max(1, size.x),
            (Halton(frameIndex + 1, 3) - 0.5f) * 2.0f / Mathf.Max(1, size.y));
    }

    private int CalculateTemporalStateHash()
    {
        unchecked
        {
            int hash = 17;
            hash = AddHash(hash, _textureSize.x);
            hash = AddHash(hash, _textureSize.y);
            hash = AddHash(hash, numberOfPasses);
            hash = AddHash(hash, numBounces);
            hash = AddHash(hash, shadowQuality);
            hash = AddHash(hash, lightSamplingStrategy.GetHashCode());
            hash = AddHash(hash, lightSampleCount);
            hash = AddHash(hash, maxLightSamples);
            hash = AddHash(hash, GetCameraApertureRadius());
            hash = AddHash(hash, enableCaustics ? 1 : 0);
            hash = AddHash(hash, IsFogEnabled() ? 1 : 0);
            hash = AddHash(hash, _water != null ? _water.GetInstanceID() : 0);
            hash = AddHash(hash, skyboxTexture != null ? skyboxTexture.GetInstanceID() : 0);
            hash = AddHash(hash, _spheres.Count);
            hash = AddHash(hash, _lights.Count);
            hash = AddHash(hash, _meshObjects.Count);
            return hash;
        }
    }

    private void EnsureCausticResources()
    {
        int photonCapacity = Mathf.Max(1, causticPhotonCount);
        CalculateCausticGridLayout();
        if (_causticPhotonBuffer != null && _causticPhotonBuffer.count == photonCapacity
            && _causticPhotonMetadataBuffer != null
            && _causticPhotonNextBuffer != null && _causticPhotonNextBuffer.count == photonCapacity
            && _causticGridCellHeadBuffer != null && _causticGridCellHeadBuffer.count == _causticGridCellCount
            && _causticTargetPairBuffer != null && _causticTargetPairBuffer.count == Mathf.Max(1, _causticTargetPairs.Count)
            && _causticTargetTriangleBuffer != null && _causticTargetTriangleBuffer.count == Mathf.Max(1, _causticTargetTriangles.Count))
        {
            return;
        }

        ReleaseCausticResources();
        CalculateCausticGridLayout();
        _causticPhotonBuffer = new ComputeBuffer(photonCapacity, CausticPhotonStride);
        _causticPhotonMetadataBuffer = new ComputeBuffer(CausticMetadataCount, sizeof(uint));
        _causticPhotonNextBuffer = new ComputeBuffer(photonCapacity, sizeof(int));
        _causticGridCellHeadBuffer = new ComputeBuffer(_causticGridCellCount, sizeof(int));
        _causticTargetPairBuffer = CreateComputeBuffer(_causticTargetPairs, CausticTargetPairStride);
        _causticTargetTriangleBuffer = CreateComputeBuffer(_causticTargetTriangles, CausticTargetTriangleStride);
        _hasCausticPhotonStateHash = false;
    }

    private void BuildCausticSamplingDistribution()
    {
        _causticTargetPairs.Clear();
        _causticTargetTriangles.Clear();
        var meshTriangleRanges = new Dictionary<int, Vector2Int>();

        for (int meshIndex = 0; meshIndex < _meshInfos.Count; meshIndex++)
        {
            MeshInfo mesh = _meshInfos[meshIndex];
            if (!IsCausticRefractor(mesh))
            {
                continue;
            }

            int triangleStart = _causticTargetTriangles.Count;
            float totalArea = 0.0f;
            for (int triangleOffset = 0; triangleOffset < mesh.triangleCount; triangleOffset++)
            {
                Triangle triangle = _triangles[mesh.triangleStart + triangleOffset];
                totalArea += 0.5f * Vector3.Cross(
                    triangle.vertex1 - triangle.vertex0,
                    triangle.vertex2 - triangle.vertex0).magnitude;
                _causticTargetTriangles.Add(new CausticTargetTriangle
                {
                    triangleIndex = mesh.triangleStart + triangleOffset,
                    cumulativeProbability = totalArea
                });
            }

            if (totalArea <= 1e-8f)
            {
                _causticTargetTriangles.RemoveRange(triangleStart, _causticTargetTriangles.Count - triangleStart);
                continue;
            }

            // cumulativeProbability currently holds the running *unnormalized* area sum. Normalize it
            // in place while tracking the previous already-normalized value locally. Reading the
            // previous element back out of the list here would divide it by totalArea a second time
            // (it was normalized on the prior iteration), which corrupts every per-triangle
            // probability and therefore every photon's power.
            int lastTriangleIndex = _causticTargetTriangles.Count - 1;
            float previousCdf = 0.0f;
            for (int triangleIndex = triangleStart; triangleIndex < _causticTargetTriangles.Count; triangleIndex++)
            {
                CausticTargetTriangle target = _causticTargetTriangles[triangleIndex];
                float normalizedCdf = target.cumulativeProbability / totalArea;
                target.selectionProbability = normalizedCdf - previousCdf;
                // Guard the last entry against float rounding leaving the CDF just below any sample.
                target.cumulativeProbability = triangleIndex == lastTriangleIndex ? 1.0f : normalizedCdf;
                previousCdf = normalizedCdf;
                _causticTargetTriangles[triangleIndex] = target;
            }
            meshTriangleRanges.Add(meshIndex, new Vector2Int(triangleStart, _causticTargetTriangles.Count - triangleStart));
        }

        var pairWeights = new List<float>();
        float maximumWeight = 0.0f;
        for (int lightIndex = 0; lightIndex < _lights.Count; lightIndex++)
        {
            Light light = _lights[lightIndex];
            if (!IsCausticLight(light))
            {
                continue;
            }

            for (int sphereIndex = 0; sphereIndex < _spheres.Count; sphereIndex++)
            {
                Sphere sphere = _spheres[sphereIndex];
                if (IsCausticRefractor(sphere))
                {
                    AddCausticTargetPair(lightIndex, 0, sphereIndex, 0, 0,
                        GetCausticPairWeight(light, sphere.position, sphere.radius), pairWeights, ref maximumWeight);
                }
            }

            foreach (KeyValuePair<int, Vector2Int> meshRange in meshTriangleRanges)
            {
                MeshInfo mesh = _meshInfos[meshRange.Key];
                AddCausticTargetPair(lightIndex, 1, meshRange.Key, meshRange.Value.x, meshRange.Value.y,
                    GetCausticPairWeight(light, (mesh.boundsMin + mesh.boundsMax) * 0.5f,
                        (mesh.boundsMax - mesh.boundsMin).magnitude * 0.5f),
                    pairWeights, ref maximumWeight);
            }

            if (IsCausticRefractor(_water))
            {
                Vector2 waterSize = _water.Size;
                AddCausticTargetPair(lightIndex, 2, -1, 0, 0,
                    GetCausticPairWeight(light, _water.TopCenter,
                        new Vector3(waterSize.x, 0.0f, waterSize.y).magnitude * 0.5f),
                    pairWeights, ref maximumWeight);
            }
        }

        if (_causticTargetPairs.Count == 0)
        {
            return;
        }

        float totalWeight = 0.0f;
        float minimumWeight = Mathf.Max(1e-8f, maximumWeight * 1e-4f);
        for (int i = 0; i < pairWeights.Count; i++)
        {
            pairWeights[i] = Mathf.Max(minimumWeight, pairWeights[i]);
            totalWeight += pairWeights[i];
        }

        float cumulativeProbability = 0.0f;
        for (int i = 0; i < _causticTargetPairs.Count; i++)
        {
            CausticTargetPair pair = _causticTargetPairs[i];
            pair.selectionProbability = pairWeights[i] / totalWeight;
            cumulativeProbability += pair.selectionProbability;
            pair.cumulativeProbability = i == _causticTargetPairs.Count - 1 ? 1.0f : cumulativeProbability;
            _causticTargetPairs[i] = pair;
        }
    }

    private void AddCausticTargetPair(int lightIndex, int refractorType, int refractorIndex,
        int triangleStart, int triangleCount, float weight, List<float> weights, ref float maximumWeight)
    {
        _causticTargetPairs.Add(new CausticTargetPair
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

    private static bool IsCausticLight(Light light)
    {
        return light.type == LightTypeSphere || (light.type == LightTypeTriangle && light.area > 1e-6f);
    }

    private static bool IsCausticRefractor(Sphere sphere)
    {
        return (sphere.materialType == 2 || sphere.opacity < 1.0f) && sphere.opacity < 1.0f;
    }

    private bool IsCausticRefractor(MeshInfo mesh)
    {
        if (mesh.isLight != 0 || mesh.triangleCount <= 0)
        {
            return false;
        }
        Triangle material = _triangles[mesh.triangleStart];
        return (material.materialType == 2 || material.opacity < 1.0f) && material.opacity < 1.0f;
    }

    private static bool IsCausticRefractor(Water water)
    {
        return water != null && water.Opacity < 1.0f;
    }

    private static float GetCausticPairWeight(Light light, Vector3 targetPosition, float targetRadius)
    {
        Vector3 lightPosition = light.type == LightTypeTriangle
            ? light.position + (light.u + light.v) / 3.0f
            : light.position;
        float distanceSquared = Mathf.Max(1e-6f, (targetPosition - lightPosition).sqrMagnitude);
        float projectedTarget = Mathf.Min(4.0f * Mathf.PI, Mathf.PI * targetRadius * targetRadius / distanceSquared);
        float luminance = Vector3.Dot(light.emission, new Vector3(0.2126f, 0.7152f, 0.0722f));
        float emitterScale = light.type == LightTypeTriangle ? Mathf.Max(1e-6f, light.area) : 1.0f;
        float facing = light.type == LightTypeTriangle
            ? Mathf.Max(0.0f, Vector3.Dot(light.normal, (targetPosition - lightPosition).normalized))
            : 1.0f;
        return Mathf.Max(0.0f, luminance) * emitterScale * facing * Mathf.Max(1e-8f, projectedTarget);
    }

    private void CalculateCausticGridLayout()
    {
        bool hasBounds = false;
        Vector3 boundsMin = Vector3.zero;
        Vector3 boundsMax = Vector3.zero;
        for (int i = 0; i < _spheres.Count; i++)
        {
            Vector3 radius = Vector3.one * _spheres[i].radius;
            EncapsulateCausticBounds(_spheres[i].position - radius, _spheres[i].position + radius,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }
        for (int i = 0; i < _meshInfos.Count; i++)
        {
            EncapsulateCausticBounds(_meshInfos[i].boundsMin, _meshInfos[i].boundsMax,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }
        if (_water != null)
        {
            Vector2 waterSize = _water.Size;
            float waveHeight = Mathf.Max(0.001f, _water.WaveAmplitude);
            Vector3 halfSize = new Vector3(waterSize.x * 0.5f, 0.0f, waterSize.y * 0.5f);
            EncapsulateCausticBounds(
                _water.TopCenter - halfSize + Vector3.down * (_water.Depth + waveHeight),
                _water.TopCenter + halfSize + Vector3.up * waveHeight,
                ref hasBounds, ref boundsMin, ref boundsMax);
        }

        if (!hasBounds)
        {
            boundsMin = new Vector3(-10.0f, -1.0f, -10.0f);
            boundsMax = new Vector3(10.0f, 10.0f, 10.0f);
        }

        float padding = Mathf.Max(0.01f, causticGatherRadius);
        boundsMin -= Vector3.one * padding;
        boundsMax += Vector3.one * padding;
        Vector3 size = Vector3.Max(boundsMax - boundsMin, Vector3.one * padding);
        float cellSize = padding;
        Vector3Int dimensions = CalculateCausticGridDimensions(size, cellSize);
        while ((long)dimensions.x * dimensions.y * dimensions.z > MaxCausticGridCells)
        {
            cellSize *= 1.25f;
            dimensions = CalculateCausticGridDimensions(size, cellSize);
        }

        _causticGridMin = boundsMin;
        _causticGridCellSize = cellSize;
        _causticGridDimensions = dimensions;
        _causticGridCellCount = dimensions.x * dimensions.y * dimensions.z;
    }

    private static Vector3Int CalculateCausticGridDimensions(Vector3 size, float cellSize)
    {
        return new Vector3Int(
            Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize)),
            Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize)),
            Mathf.Max(1, Mathf.CeilToInt(size.z / cellSize)));
    }

    private static void EncapsulateCausticBounds(Vector3 min, Vector3 max, ref bool hasBounds, ref Vector3 boundsMin, ref Vector3 boundsMax)
    {
        if (!hasBounds)
        {
            boundsMin = min;
            boundsMax = max;
            hasBounds = true;
            return;
        }

        boundsMin = Vector3.Min(boundsMin, min);
        boundsMax = Vector3.Max(boundsMax, max);
    }

    private void ReleaseCausticResources()
    {
        _causticPhotonBuffer?.Release();
        _causticPhotonMetadataBuffer?.Release();
        _causticGridCellHeadBuffer?.Release();
        _causticPhotonNextBuffer?.Release();
        _causticTargetPairBuffer?.Release();
        _causticTargetTriangleBuffer?.Release();
        _causticPhotonBuffer = null;
        _causticPhotonMetadataBuffer = null;
        _causticGridCellHeadBuffer = null;
        _causticPhotonNextBuffer = null;
        _causticTargetPairBuffer = null;
        _causticTargetTriangleBuffer = null;
        _causticGridCellCount = 0;
        _causticGridPhotonCount = 0;
        _causticGridOutOfBoundsCount = 0;
        _hasCausticPhotonStateHash = false;
        _causticFrameIndex = 0;
    }

    private void UpdateCausticPhotonMap()
    {
        if (!enableCaustics)
        {
            if (_previousCausticsEnabled || HasCausticResources)
            {
                ReleaseCausticResources();
                ResetFrameAccumulation();
            }
            _previousCausticsEnabled = false;
            return;
        }

        int stateHash = CalculateCausticPhotonStateHash();
        bool stateChanged = !_hasCausticPhotonStateHash || stateHash != _causticPhotonStateHash;
        if (stateChanged)
        {
            BuildCausticSamplingDistribution();
        }
        EnsureCausticResources();
        if (stateChanged)
        {
            _causticTargetPairBuffer.SetData(_causticTargetPairs.Count > 0
                ? _causticTargetPairs
                : new List<CausticTargetPair> { default });
            _causticTargetTriangleBuffer.SetData(_causticTargetTriangles.Count > 0
                ? _causticTargetTriangles
                : new List<CausticTargetTriangle> { default });
            _causticPhotonStateHash = stateHash;
            _hasCausticPhotonStateHash = true;
            _causticFrameIndex = 0;
            ResetFrameAccumulation();
        }
        else if (!ShouldUseFrameAccumulation())
        {
            _previousCausticsEnabled = true;
            return;
        }

        shader.EnableKeyword("CAUSTICS_ENABLED");
        int clearKernel = shader.FindKernel("ClearCausticPhotons");
        int traceKernel = shader.FindKernel("TraceCausticPhotons");
        int clearGridKernel = shader.FindKernel("ClearCausticGrid");
        int buildGridKernel = shader.FindKernel("BuildCausticGrid");
        SetPhotonTraceSceneParameters(traceKernel);
        SetCausticShaderParameters(clearKernel);
        SetCausticShaderParameters(traceKernel);
        SetCausticShaderParameters(clearGridKernel);
        SetCausticShaderParameters(buildGridKernel);
        SetSceneBuffers(traceKernel);
        shader.Dispatch(clearKernel, 1, 1, 1);
        shader.Dispatch(traceKernel, Mathf.CeilToInt(Mathf.Max(1, causticPhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        shader.Dispatch(clearGridKernel, Mathf.CeilToInt(_causticGridCellCount / (float)CausticTraceThreadCount), 1, 1);
        shader.Dispatch(buildGridKernel, Mathf.CeilToInt(Mathf.Max(1, causticPhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        var metadata = new uint[CausticMetadataCount];
        _causticPhotonMetadataBuffer.GetData(metadata);
        _causticGridOutOfBoundsCount = (int)metadata[4];
        _causticGridPhotonCount = (int)metadata[5];
        _causticDispatchCount++;
        if (ShouldUseFrameAccumulation())
        {
            _causticFrameIndex = _causticFrameIndex == int.MaxValue ? 0 : _causticFrameIndex + 1;
        }
        _previousCausticsEnabled = true;
    }

    private void SetPhotonTraceSceneParameters(int traceKernel)
    {
        EnsureMeshTextureArrays();
        shader.SetTexture(traceKernel, "_MeshAlbedoTextures", _meshAlbedoTextureArray);
        shader.SetTexture(traceKernel, "_MeshMetallicRoughnessTextures", _meshMetallicRoughnessTextureArray);
        shader.SetTexture(traceKernel, "_MeshNormalTextures", _meshNormalTextureArray);
        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);
        SetWaterShaderParameters();
    }

    private bool ShouldUseFrameAccumulation()
    {
        bool animatedWater = _water != null && _water.WaveAmplitude > 0.0f && _water.WaveSpeed > 0.0f && !_singleFrame;
        return enableFrameAccumulation && debugRenderMode == DebugRenderMode.FinalColor && !animatedWater
            && !ShouldUseTemporalAccumulation();
    }

    private float GetRenderTime()
    {
        return _singleFrame ? _singleFrameRenderTime : Time.time;
    }

    private void ResetDynamicQualityState()
    {
        _dynamicQualityAverageFrameMs = Time.unscaledDeltaTime > 0.0f
            ? Time.unscaledDeltaTime * 1000.0f
            : GetDynamicQualityTargetFrameMs();
        _dynamicQualityTimeSinceAdjustment = 0.0f;
    }

    private void UpdateDynamicQuality()
    {
        if (!enableDynamicQuality || _singleFrame)
        {
            return;
        }

        float frameMs = Time.unscaledDeltaTime * 1000.0f;
        if (frameMs <= 0.0f)
        {
            return;
        }

        if (_dynamicQualityAverageFrameMs <= 0.0f)
        {
            _dynamicQualityAverageFrameMs = frameMs;
        }
        else
        {
            _dynamicQualityAverageFrameMs = Mathf.Lerp(
                _dynamicQualityAverageFrameMs,
                frameMs,
                DynamicQualitySmoothing);
        }

        _dynamicQualityTimeSinceAdjustment += Time.unscaledDeltaTime;
        if (_dynamicQualityTimeSinceAdjustment < DynamicQualityAdjustmentInterval)
        {
            return;
        }

        float targetFrameMs = GetDynamicQualityTargetFrameMs();
        float tolerance = Mathf.Clamp(dynamicQualityTolerance, 0.01f, 1.0f);
        float increaseHeadroom = Mathf.Clamp(dynamicQualityIncreaseHeadroom, tolerance, 0.95f);
        float slowThresholdMs = targetFrameMs * (1.0f + tolerance);
        float fastThresholdMs = targetFrameMs * (1.0f - increaseHeadroom);
        bool changed = false;

        if (_dynamicQualityAverageFrameMs > slowThresholdMs)
        {
            changed = DecreaseDynamicQuality(_dynamicQualityAverageFrameMs / targetFrameMs);
        }
        else if (_dynamicQualityAverageFrameMs < fastThresholdMs)
        {
            changed = IncreaseDynamicQuality();
        }

        if (changed)
        {
            ResetFrameAccumulation();
            _dynamicQualityTimeSinceAdjustment = 0.0f;
        }
    }

    private float GetDynamicQualityTargetFrameMs()
    {
        return 1000.0f / Mathf.Max(1, dynamicQualityTargetFrameRate);
    }

    private bool DecreaseDynamicQuality(float costRatio)
    {
        if (numberOfPasses > MinNumberOfPasses)
        {
            int targetPasses = Mathf.Max(
                MinNumberOfPasses,
                Mathf.FloorToInt(numberOfPasses / Mathf.Max(1.0f, costRatio)));
            numberOfPasses = Mathf.Min(numberOfPasses - 1, targetPasses);
            return true;
        }

        if (TryDecreaseDynamicLightSampling())
        {
            return true;
        }

        if (shadowQuality > MinShadowQuality)
        {
            shadowQuality--;
            return true;
        }

        if (numBounces > MinNumBounces)
        {
            numBounces--;
            return true;
        }

        return false;
    }

    private bool TryDecreaseDynamicLightSampling()
    {
        if (_lights.Count <= 1)
        {
            return false;
        }

        int targetLightSamples = GetDynamicInitialLightSampleCount();
        if (lightSamplingStrategy == LightSamplingStrategy.AllLights)
        {
            lightSamplingStrategy = LightSamplingStrategy.ImportanceSampled;
            lightSampleCount = targetLightSamples;
            return true;
        }

        if (lightSamplingStrategy == LightSamplingStrategy.ImportanceSampled && lightSampleCount > MinDynamicLightSampleCount)
        {
            lightSampleCount--;
            return true;
        }

        if (lightSamplingStrategy == LightSamplingStrategy.UniformRandom && lightSampleCount > MinDynamicLightSampleCount)
        {
            lightSampleCount--;
            return true;
        }

        return false;
    }

    private bool IncreaseDynamicQuality()
    {
        if (numberOfPasses < MaxNumberOfPasses)
        {
            numberOfPasses++;
            return true;
        }

        if (TryIncreaseDynamicLightSampling())
        {
            return true;
        }

        if (shadowQuality < MaxShadowQuality)
        {
            shadowQuality++;
            return true;
        }

        if (numBounces < MaxNumBounces)
        {
            numBounces++;
            return true;
        }

        return false;
    }

    private bool TryIncreaseDynamicLightSampling()
    {
        if (_lights.Count <= 1 || lightSamplingStrategy == LightSamplingStrategy.AllLights)
        {
            return false;
        }

        int targetLightSamples = GetDynamicInitialLightSampleCount();
        int activeLightCount = GetActiveLightCountForSampling();
        if (lightSampleCount < targetLightSamples)
        {
            lightSampleCount++;
            return true;
        }

        if (lightSampleCount < activeLightCount)
        {
            lightSampleCount++;
            return true;
        }

        lightSamplingStrategy = LightSamplingStrategy.AllLights;
        lightSampleCount = Mathf.Max(MinDynamicLightSampleCount, targetLightSamples);
        return true;
    }

    private int GetDynamicInitialLightSampleCount()
    {
        return Mathf.Clamp(
            Mathf.CeilToInt(GetActiveLightCountForSampling() / (float)DynamicLightSampleDivisor),
            MinDynamicLightSampleCount,
            MaxDynamicLightSampleCount);
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
        if (!ShouldRunTemporalDenoiser() && _temporalRadianceHistoryA != null)
        {
            ReleaseTemporalDenoiserResources();
            ResetTemporalHistory();
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
            Graphics.Blit(_outputTexture, dest);
            return;
        }

        long firstFramePreparationStart = _startupProfilePending ? Stopwatch.GetTimestamp() : 0;
        UpdateTrackedFocusPoint();
        var autoFocusDistance = (cameraAutoFocus)
            ? GetNearestIntersectionDistanceForAutoFocus(new Ray(renderTextureCamera.transform.position,
                renderTextureCamera.transform.forward))
            : cameraFocalDistance;

        if (cameraAutoFocus && autoFocusDistance < 1.0f)
        {
            var modifier = Mathf.Lerp(1.75f, 1.0f, autoFocusDistance);
            autoFocusDistance *= modifier;

            autoFocusDistance = Mathf.Max(autoFocusDistance, 0.1f);
            float targetFocusDistance = autoFocusDistance;

            autoFocusDistance = Mathf.Lerp(previousFocalDistance, autoFocusDistance,
                Mathf.SmoothStep(0.0f, 1.0f, timeSincePreviousFocusDistance));

            if (Mathf.Abs(autoFocusDistance - targetFocusDistance) < 0.05f)
            {
                previousFocalDistance = autoFocusDistance;
                timeSincePreviousFocusDistance = 0.0f;
            }
            else
            {
                timeSincePreviousFocusDistance += Time.unscaledDeltaTime;
            }
        }

        cameraFocalDistance = autoFocusDistance;

        _temporalDynamicSceneChanged = false;
        UpdateSpheres();
        UpdateTriangles();
        UpdateTopLevelBvh();
        UpdateShadowBvh();
        UpdateCausticPhotonMap();

        if (ShouldRunTemporalDenoiser())
        {
            PrepareTemporalCameraState();
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
            CommitTemporalCameraState();
        }
        else if (!useDedicatedCausticsDebugKernel && IsCausticPreservationDebugMode())
        {
            PresentCausticPreservationMask();
        }
        if (!useDedicatedCausticsDebugKernel && ShouldRunSpatialDenoiser() && !IsTemporalDebugMode())
        {
            RunSpatialDenoiser();
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
        CommitRenderedCameraState();

        // The dispatch above triggered (and blocked on) any first-time variant compile. Record
        // that this debug mode is now warm so future switches to it are instant, and clear the
        // overlay flag.
        _warmedShaderVariants.Add(requestedVariant);
        _appliedDebugRenderMode = debugRenderMode;
        _appliedCausticsEnabled = enableCaustics;
        _appliedFogEnabled = fogEnabled;
        _pendingVariantWarmup = false;

        Graphics.Blit(_outputTexture, dest);
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
        if (!_focusQueryPending || _focusQueryInFlight)
        {
            return;
        }

        if (_focusQueryBuffer == null)
        {
            _focusQueryBuffer = new ComputeBuffer(1, sizeof(float) * 4);
        }

        int kernel = shader.FindKernel("CSFocusQuery");
        SetShaderParameters(kernel);
        shader.SetVector("_FocusQueryUv", _pendingFocusQueryUv);
        shader.SetBuffer(kernel, "_FocusQueryResult", _focusQueryBuffer);
        shader.Dispatch(kernel, 1, 1, 1);

        _focusQueryPending = false;
        _focusQueryInFlight = true;
        _focusQueryCameraPosition = renderTextureCamera.transform.position;
        _focusQueryCameraForward = renderTextureCamera.transform.forward;
        int generation = _focusQueryGeneration;
        _focusReadbackRequest = AsyncGPUReadback.Request(
            _focusQueryBuffer,
            request => CompleteFocusQuery(request, generation));
    }

    private void CompleteFocusQuery(AsyncGPUReadbackRequest request, int generation)
    {
        if (generation != _focusQueryGeneration)
        {
            return;
        }

        _focusQueryInFlight = false;
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
        float focusDistance = Vector3.Dot(hitPosition - _focusQueryCameraPosition, _focusQueryCameraForward);
        if (focusDistance <= 0.0f)
        {
            return;
        }

        cameraAutoFocus = false;
        cameraFocalDistance = Mathf.Max(0.1f, focusDistance);
        previousFocalDistance = cameraFocalDistance;
        _clickedFocusPoint = hitPosition;
        _hasClickedFocusPoint = enableClickToFocus && trackClickedFocusPoint;
        UpdateTrackedFocusPoint();
        ResetFrameAccumulation();
    }

    private void UpdateTrackedFocusPoint()
    {
        if (!enableClickToFocus || !trackClickedFocusPoint || !_hasClickedFocusPoint || renderTextureCamera == null)
        {
            _hasClickedFocusPoint = false;
            _clickedFocusPointInFrustum = false;
            return;
        }

        Vector3 viewportPoint = renderTextureCamera.WorldToViewportPoint(_clickedFocusPoint);
        _clickedFocusPointInFrustum = viewportPoint.z >= renderTextureCamera.nearClipPlane
            && viewportPoint.z <= renderTextureCamera.farClipPlane
            && viewportPoint.x >= 0.0f && viewportPoint.x <= 1.0f
            && viewportPoint.y >= 0.0f && viewportPoint.y <= 1.0f;

        if (_clickedFocusPointInFrustum)
        {
            cameraFocalDistance = Mathf.Max(
                0.1f,
                Vector3.Dot(
                    _clickedFocusPoint - renderTextureCamera.transform.position,
                    renderTextureCamera.transform.forward));
            previousFocalDistance = cameraFocalDistance;
        }
    }

    private void UpdateSpheres()
    {
        _hasTransparentSphereBlockers = false;
        for (int i = 0; i < _spheres.Count; ++i)
        {
            var sphere = _spheres[i];
            var sphereObject = _sphereObjects[i];

            Vector3 position = sphereObject.transform.TransformPoint(sphereObject.collider.center);
            float radius = GetWorldSphereRadius(sphereObject.collider, sphereObject.transform);
            _temporalDynamicSceneChanged |= sphere.position != position || !Mathf.Approximately(sphere.radius, radius);
            sphere.position = position;
            sphere.radius = radius;

            var material = sphereObject.material;
            sphere.color = material.Color.ToVector3();
            sphere.refraction = material.RefractionIndex;
            sphere.opacity = material.Opacity;
            sphere.smoothness = material.Smoothness;
            sphere.materialType = (int)material.Type;

            if (sphere.opacity < ShadowBlockerOpaqueThreshold)
            {
                _hasTransparentSphereBlockers = true;
            }

            _spheres[i] = sphere;
        }
        
        for (int i = 0; i < _lights.Count; ++i)
        {
            if (i >= _lightObjects.Count)
            {
                break;
            }

            var lightData = _lights[i];
            var lightObject = _lightObjects[i];

            Vector3 position = lightObject.transform.TransformPoint(lightObject.collider.center);
            float radius = GetWorldSphereRadius(lightObject.collider, lightObject.transform);
            _temporalDynamicSceneChanged |= lightData.position != position || !Mathf.Approximately(lightData.radius, radius);
            lightData.position = position;
            lightData.radius = radius;
            lightData.area = Mathf.PI * lightData.radius * lightData.radius;
            lightData.type = LightTypeSphere;

            var light = lightObject.light;
            lightData.emission = light.Color.ToVector3();
            
            _lights[i] = lightData;
        }

        if (_sphereBuffer != null && _spheres.Count > 0)
        {
            _sphereBuffer.SetData(_spheres);
        }

        int requiredLightBufferCount = Mathf.Max(1, _lights.Count);
        if (_lightBuffer == null || _lightBuffer.count < requiredLightBufferCount)
        {
            _lightBuffer?.Release();
            _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        }
        else if (_lights.Count > 0)
        {
            _lightBuffer.SetData(_lights);
        }
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
        if (!geometryChanged && !materialChanged)
        {
            return;
        }

        if (geometryChanged)
        {
            RebuildTriangleData();
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

        int requiredLightBufferCount = Mathf.Max(1, _lights.Count);
        if (_lightBuffer == null || _lightBuffer.count < requiredLightBufferCount)
        {
            _lightBuffer?.Release();
            _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        }
        else if (_lights.Count > 0)
        {
            _lightBuffer.SetData(_lights);
        }
    }

    private void UpdateTopLevelBvh()
    {
        RebuildTopLevelBvh();

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
        RebuildShadowBvh();

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
            var emission = light != null ? light.Color.ToVector3() : Vector3.zero;
            var smoothness = material != null ? material.Smoothness : 0.0f;
            var metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            var materialType = light != null ? 3 : (int)material.Type;
            var albedoTexture = material != null ? material.AlbedoTexture : null;
            var metallicRoughnessTexture = material != null ? material.MetallicRoughnessTexture : null;
            var normalTexture = material != null ? material.NormalTexture : null;
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
                || meshObject.previousMaterialType != materialType
                || meshObject.previousAlbedoTexture != albedoTexture
                || meshObject.previousMetallicRoughnessTexture != metallicRoughnessTexture
                || meshObject.previousNormalTexture != normalTexture;

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
            meshObject.previousMaterialType = materialType;
            meshObject.previousAlbedoTexture = albedoTexture;
            meshObject.previousMetallicRoughnessTexture = metallicRoughnessTexture;
            meshObject.previousNormalTexture = normalTexture;
            meshObject.previousInterpolateNormals = interpolateNormals;
            _meshObjects[i] = meshObject;
        }
    }

    private void RefreshTriangleMaterials()
    {
        _meshAlbedoTextures.Clear();
        _meshMetallicRoughnessTextures.Clear();
        _meshNormalTextures.Clear();

        for (int meshIndex = 0; meshIndex < _meshObjects.Count; meshIndex++)
        {
            var meshObject = _meshObjects[meshIndex];
            var material = meshObject.material;
            var light = meshObject.light;
            bool isLight = light != null;
            var color = material != null ? material.Color.ToVector3() : Vector3.one;
            var emission = isLight ? light.Color.ToVector3() : Vector3.zero;
            float smoothness = material != null ? material.Smoothness : 0.0f;
            float metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            float opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            float refraction = material != null ? material.RefractionIndex : 1.0f;
            int materialType = isLight ? 3 : (int)material.Type;
            int textureIndex = material != null ? GetMeshAlbedoTextureIndex(material.AlbedoTexture) : -1;
            int metallicRoughnessTextureIndex = material != null ? GetMeshTextureIndex(material.MetallicRoughnessTexture, _meshMetallicRoughnessTextures) : -1;
            int normalTextureIndex = material != null ? GetMeshTextureIndex(material.NormalTexture, _meshNormalTextures) : -1;

            int triangleStart = 0;
            int triangleEnd = 0;
            for (int infoIndex = 0; infoIndex < _meshInfos.Count; infoIndex++)
            {
                if (_meshInfos[infoIndex].meshIndex != meshIndex)
                {
                    continue;
                }

                triangleStart = _meshInfos[infoIndex].triangleStart;
                triangleEnd = triangleStart + _meshInfos[infoIndex].triangleCount;
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
                triangle.materialType = materialType;
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                _triangles[triangleIndex] = triangle;

                if (isLight && triangle.lightIndex >= 0 && triangle.lightIndex < _lights.Count)
                {
                    var triangleLight = _lights[triangle.lightIndex];
                    triangleLight.emission = emission;
                    _lights[triangle.lightIndex] = triangleLight;
                }
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
        int sphereLightCount = _lightObjects.Count;
        if (_lights.Count > sphereLightCount)
        {
            _lights.RemoveRange(sphereLightCount, _lights.Count - sphereLightCount);
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
            var emission = isLight ? light.Color.ToVector3() : Vector3.zero;
            var smoothness = material != null ? material.Smoothness : 0.0f;
            var metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            int materialType = isLight ? 3 : (int)material.Type;
            int textureIndex = material != null ? GetMeshAlbedoTextureIndex(material.AlbedoTexture) : -1;
            int metallicRoughnessTextureIndex = material != null ? GetMeshTextureIndex(material.MetallicRoughnessTexture, _meshMetallicRoughnessTextures) : -1;
            int normalTextureIndex = material != null ? GetMeshTextureIndex(material.NormalTexture, _meshNormalTextures) : -1;
            bool interpolateNormals = material != null && material.InterpolateNormals;
            MeshBvhTemplate template = GetOrBuildMeshBvhTemplate(mesh, interpolateNormals);
            int triangleStart = _triangles.Count;
            int nodeStart = _bvhNodes.Count;

            for (int i = 0; i < template.triangles.Count; i++)
            {
                Triangle triangle = TransformTemplateTriangle(template.triangles[i], localToWorld, normalToWorld);
                int lightIndex = isLight ? _lights.Count : -1;
                triangle.color = color;
                triangle.emission = emission;
                triangle.smoothness = smoothness;
                triangle.metallic = metallic;
                triangle.opacity = opacity;
                triangle.refraction = refraction;
                triangle.materialType = materialType;
                triangle.meshIndex = meshIndex;
                triangle.textureIndex = textureIndex;
                triangle.metallicRoughnessTextureIndex = metallicRoughnessTextureIndex;
                triangle.normalTextureIndex = normalTextureIndex;
                triangle.interpolateNormals = interpolateNormals ? 1 : 0;
                triangle.lightIndex = lightIndex;
                _triangles.Add(triangle);

                if (isLight)
                {
                    AddTriangleLight(triangle.vertex0, triangle.vertex1, triangle.vertex2, triangle.normal, emission);
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
            _meshInfos.Add(new MeshInfo
            {
                boundsMin = _bvhNodes[rootNodeIndex].boundsMin,
                rootNodeIndex = rootNodeIndex,
                boundsMax = _bvhNodes[rootNodeIndex].boundsMax,
                triangleStart = triangleStart,
                triangleCount = _triangles.Count - triangleStart,
                meshIndex = meshIndex,
                isLight = isLight ? 1 : 0
            });
        }

        RebuildMeshTextureArrays();
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
        string identity = GetEditorMeshIdentity(mesh);
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
        var expectedKeys = new HashSet<string>();
        foreach (var meshObject in _meshObjects)
        {
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
            string key = entry.meshIdentity + (entry.interpolateNormals ? ":smooth" : ":flat");
            string path = entry.mesh == null ? string.Empty : UnityEditor.AssetDatabase.GetAssetPath(entry.mesh);
            string dependencyHash = string.IsNullOrEmpty(path)
                ? $"scene:{entry.mesh?.vertexCount ?? 0}:{(entry.mesh == null ? 0 : GetMeshIndexCount(entry.mesh))}"
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
        get => bvhBake;
        set => bvhBake = value;
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
        long key = ((long)mesh.GetInstanceID() << 1) | (interpolateNormals ? 1L : 0L);
        MeshBvhTemplate template = _meshBvhTemplates[key];
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
                long key = ((long)entry.mesh.GetInstanceID() << 1) | (entry.interpolateNormals ? 1L : 0L);
                MeshBvhTemplate template = _meshBvhTemplates[key];
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

    private void AddTriangleLight(Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, Vector3 normal, Vector3 emission)
    {
        var u = vertex1 - vertex0;
        var v = vertex2 - vertex0;
        float area = Vector3.Cross(u, v).magnitude * 0.5f;
        if (area <= 0.000001f)
        {
            return;
        }

        _lights.Add(new Light
        {
            position = vertex0,
            emission = emission,
            u = u,
            v = v,
            normal = normal,
            area = area,
            radius = Mathf.Sqrt(area / Mathf.PI),
            type = LightTypeTriangle
        });
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
            if (_lights[i].type != LightTypeSphere)
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

        _topLevelBvhBuildItemComparer.axis = GetLongestAxis(boundsMax - boundsMin);
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

    private static Vector3 GetTopLevelBvhItemCentroid(TopLevelBvhBuildItem item)
    {
        return (item.boundsMin + item.boundsMax) * 0.5f;
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
            _topLevelBvhBuildItemComparer.axis = axis;
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

        _topLevelBvhBuildItemComparer.axis = bestAxis;
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
        int remainingDepth = BvhStackSize - depth - 1;
        int maxChildCount = remainingDepth >= 30 ? int.MaxValue : 1 << remainingDepth;
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

        float x = Mathf.Max(0f, size.x);
        float y = Mathf.Max(0f, size.y);
        float z = Mathf.Max(0f, size.z);
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
        float tMin = Mathf.Max(tMin3.x, Mathf.Max(tMin3.y, tMin3.z));
        float tMax = Mathf.Min(tMax3.x, Mathf.Min(tMax3.y, tMax3.z));

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
        return opacity <= autoFocusTransparentOpacityThreshold;
    }

    public void RebuildBuffers(bool startupProfile = false)
    {
        long rebuildStart = Stopwatch.GetTimestamp();
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
        _sphereBuffer = null;
        _lightBuffer = null;
        _triangleBuffer = null;
        _meshBuffer = null;
        _bvhNodeBuffer = null;
        _topLevelBvhNodeBuffer = null;
        _shadowBvhNodeBuffer = null;

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
        if (startupProfile)
        {
            AddStartupProfilePhase($"top-level BVH ({_topLevelBvhNodes.Count:N0} nodes)", phaseStart);
        }

        phaseStart = Stopwatch.GetTimestamp();
        RebuildShadowBvh();
        if (startupProfile)
        {
            AddStartupProfilePhase($"shadow BVH ({_shadowBvhNodes.Count:N0} nodes)", phaseStart);
        }

        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);

        phaseStart = Stopwatch.GetTimestamp();
        _sphereBuffer = CreateComputeBuffer(_spheres, SphereStride);
        _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        _triangleBuffer = CreateComputeBuffer(_triangles, TriangleStride);
        _meshBuffer = CreateComputeBuffer(_meshInfos, MeshInfoStride);
        _bvhNodeBuffer = CreateComputeBuffer(_bvhNodes, BvhNodeStride);
        _topLevelBvhNodeBuffer = CreateComputeBuffer(_topLevelBvhNodes, TopLevelBvhNodeStride);
        _shadowBvhNodeBuffer = CreateComputeBuffer(_shadowBvhNodes, TopLevelBvhNodeStride);
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

        double milliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        _startupProfilePhases.Add($"  {name}: {milliseconds:N1} ms");
    }

    private void LogStartupProfile()
    {
        _startupStopwatch.Stop();
        var message = new StringBuilder(512);
        message.AppendLine($"Ray tracing startup profile for '{gameObject.scene.name}':");
        message.AppendLine($"  object registration / Unity startup before Start: {_startupRegistrationMilliseconds:N1} ms");
        for (int i = 0; i < _startupProfilePhases.Count; i++)
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

    public void RegisterObject(RayTracingObject obj)
    {
        if (_rayTracingObjects.Contains(obj))
        {
            return;
        }

        _rayTracingObjects.Add(obj);
        _buffersNeedRebuilding = true;
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
                materialType = (int)material.Type,
            };
            _spheres.Add(sphere);
            _sphereObjects.Add(new RayTracedSphere
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
            _meshObjects.Add(new RayTracedMesh
            {
                obj = obj,
                transform = obj.transform,
                material = material,
                light = rayLight,
                mesh = meshFilter.sharedMesh,
                previousLocalToWorld = obj.transform.localToWorldMatrix,
                previousColor = material != null ? material.Color.ToVector3() : Vector3.one,
                previousEmission = rayLight != null ? rayLight.Color.ToVector3() : Vector3.zero,
                previousSmoothness = material != null ? material.Smoothness : 0.0f,
                previousMetallic = material != null ? GetEffectiveMetallic(material) : 0.0f,
                previousOpacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f,
                previousRefraction = material != null ? material.RefractionIndex : 1.0f,
                previousMaterialType = rayLight != null ? 3 : (int)material.Type,
                previousAlbedoTexture = material != null ? material.AlbedoTexture : null,
                previousMetallicRoughnessTexture = material != null ? material.MetallicRoughnessTexture : null,
                previousNormalTexture = material != null ? material.NormalTexture : null,
                previousInterpolateNormals = material != null && material.InterpolateNormals
            });
            return;
        }

        if (rayLight != null && sphereCollider != null)
        {
            var radius = GetWorldSphereRadius(sphereCollider, obj.transform);
            var lightData = new Light
            {
                position = obj.transform.TransformPoint(sphereCollider.center),
                radius = radius,
                area = Mathf.PI * radius * radius,
                emission = rayLight.Color.ToVector3(),
                type = LightTypeSphere
            };
            _lights.Insert(_lightObjects.Count, lightData);
            _lightObjects.Add(new RayTracedLight
            {
                obj = obj,
                transform = obj.transform,
                light = rayLight,
                collider = sphereCollider
            });
            return;
        }

        Debug.LogWarning($"RayTracingObject '{obj.name}' needs RayMaterial with SphereCollider or MeshFilter, or RayLight with SphereCollider or MeshFilter.", obj);
    }

    public bool RegisterWater(Water water)
    {
        if (_water == water)
        {
            return true;
        }

        if (_water != null)
        {
            Debug.LogError(
                $"Only one active Water component is supported by GameManager '{name}'. " +
                $"Disable '{_water.name}' before enabling '{water.name}'.",
                water);
            return false;
        }

        _water = water;
        ResetFrameAccumulation();
        return true;
    }

    public void UnregisterWater(Water water)
    {
        if (_water != water)
        {
            return;
        }

        _water = null;
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
    
    public void UnregisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        _buffersNeedRebuilding = true;
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
            return;
        }

        var meshIndex = _meshObjects.FindIndex(mesh => mesh.obj == obj);
        if (meshIndex >= 0)
        {
            _meshObjects.RemoveAt(meshIndex);
        }
    }

    private void SetComputeBuffer(string name, ComputeBuffer buffer, int kernelHandle)
    {
        if (buffer != null)
        {
            shader.SetBuffer(kernelHandle, name, buffer);
        }
    }

    private void SetSceneBuffers(int kernelHandle)
    {
        SetComputeBuffer("_Spheres", _sphereBuffer, kernelHandle);
        SetComputeBuffer("_Lights", _lightBuffer, kernelHandle);
        SetComputeBuffer("_Triangles", _triangleBuffer, kernelHandle);
        SetComputeBuffer("_Meshes", _meshBuffer, kernelHandle);
        SetComputeBuffer("_BvhNodes", _bvhNodeBuffer, kernelHandle);
        SetComputeBuffer("_TopLevelBvhNodes", _topLevelBvhNodeBuffer, kernelHandle);
        SetComputeBuffer("_ShadowBvhNodes", _shadowBvhNodeBuffer, kernelHandle);
    }

    private void SetCausticShaderParameters(int kernelHandle)
    {
        shader.SetInt("_CausticPhotonCapacity", Mathf.Max(1, causticPhotonCount));
        shader.SetInt("_CausticPhotonAttemptCount", Mathf.Max(1, causticPhotonCount));
        shader.SetInt("_CausticMaxBounces", Mathf.Clamp(numBounces, MinNumBounces, MaxNumBounces));
        shader.SetInt("_CausticSeed", causticSeed);
        shader.SetInt("_CausticFrameIndex", _causticFrameIndex);
        shader.SetFloat("_CausticGatherRadius", Mathf.Max(0.001f, causticGatherRadius));
        shader.SetFloat("_CausticIntensity", Mathf.Max(0.0f, causticIntensity));
        shader.SetVector("_CausticGridMin", _causticGridMin);
        shader.SetFloat("_CausticGridCellSize", _causticGridCellSize);
        shader.SetInts("_CausticGridDimensions", _causticGridDimensions.x, _causticGridDimensions.y, _causticGridDimensions.z);
        shader.SetInt("_CausticGridCellCount", _causticGridCellCount);
        shader.SetInt("_NumCausticTargetPairs", _causticTargetPairs.Count);
        SetComputeBuffer("_CausticPhotons", _causticPhotonBuffer, kernelHandle);
        SetComputeBuffer("_CausticPhotonMetadata", _causticPhotonMetadataBuffer, kernelHandle);
        SetComputeBuffer("_CausticGridCellHeads", _causticGridCellHeadBuffer, kernelHandle);
        SetComputeBuffer("_CausticPhotonNext", _causticPhotonNextBuffer, kernelHandle);
        SetComputeBuffer("_CausticTargetPairs", _causticTargetPairBuffer, kernelHandle);
        SetComputeBuffer("_CausticTargetTriangles", _causticTargetTriangleBuffer, kernelHandle);
    }

    private static float GetWorldSphereRadius(SphereCollider sphereCollider, Transform sphereTransform)
    {
        var scale = sphereTransform.lossyScale;
        float largestAxisScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        return sphereCollider.radius * largestAxisScale;
    }
    
    private float GetNearestIntersectionDistanceForAutoFocus(Ray ray)
    {
        // This is a distance that allows things in the mid-distance to still get sub-pixel jitter, which
        // allows better anti-aliasing. Beyond this distance the focus changes are even more of a sub-pixel
        // and barely noticeable. We increase the jitter a bit if there is more super-sampling (passes) to get
        // more anti-aliasing.
        float nearestDistance = 12 - Math.Min(8.0f, numberOfPasses * 1.75f);

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
            if (sphere.type != LightTypeSphere)
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
        
        if (_water != null && !ShouldAutoFocusIgnoreObject(_water.Opacity) && Mathf.Abs(ray.direction.y) > 0.000001f)
        {
            Vector3 waterCenter = _water.TopCenter;
            Vector2 waterSize = _water.Size;
            float hitDistance = (waterCenter.y - ray.origin.y) / ray.direction.y;
            var hitPoint = ray.origin + ray.direction * hitDistance;
            var halfSize = waterSize * 0.5f;
            if (hitDistance > 0.0f
                && hitDistance < nearestDistance
                && hitPoint.x >= waterCenter.x - halfSize.x
                && hitPoint.x <= waterCenter.x + halfSize.x
                && hitPoint.z >= waterCenter.z - halfSize.y
                && hitPoint.z <= waterCenter.z + halfSize.y)
            {
                nearestDistance = hitDistance;
            }
        }

        return nearestDistance;
    }
    
    private void SetShaderParameters(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "_SkyboxTexture", skyboxTexture);
        EnsureMeshTextureArrays();
        shader.SetTexture(kernelHandle, "_MeshAlbedoTextures", _meshAlbedoTextureArray);
        shader.SetTexture(kernelHandle, "_MeshMetallicRoughnessTextures", _meshMetallicRoughnessTextureArray);
        shader.SetTexture(kernelHandle, "_MeshNormalTextures", _meshNormalTextureArray);

        shader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);
        shader.SetVector("_FrameJitterNdc", new Vector4(_currentTemporalJitterNdc.x, _currentTemporalJitterNdc.y, 0.0f, 0.0f));
        shader.SetInt("_UseTemporalJitter", ShouldRunTemporalDenoiser() ? 1 : 0);

        _skyboxLightColorAsVector = new Vector4(_skyboxLightColor.r / 255f, _skyboxLightColor.g / 255f, _skyboxLightColor.b / 255f, 1.0f);
        shader.SetVector("_SkyboxLight", _skyboxLightColorAsVector);

        if (randomNoise)
        {
            shader.SetInt("_Seed", UnityEngine.Random.Range(1, int.MaxValue));
        }
        else
        {
            shader.SetInt("_Seed", 1);
        }

        shader.SetInt("_NumberOfPasses", numberOfPasses);
        shader.SetInt("_NumBounces", numBounces);
        // Temporal modes are presented by RayTracingSpatialDenoiser after CSMain. Keep CSMain
        // on its normal HDR beauty path so an out-of-range renderer debug value cannot write
        // an untonemapped fallback before the temporal presentation pass runs.
        shader.SetInt("_DebugRenderMode", IsTemporalDebugMode() ? (int)DebugRenderMode.FinalColor : (int)debugRenderMode);
        shader.SetInt("_UseFrameAccumulation", ShouldUseFrameAccumulation() ? 1 : 0);
        shader.SetInt("_AccumulatedFrameCount", _accumulatedFrameCount);
        shader.SetInt("_SampleOffset", CalculateSampleOffset());

        // The shader splits its debug render path behind the DEBUG_RENDER keyword so the default
        // final-color variant compiles without any debug intersection/scatter code (a large shader
        // compile-time saving). Only enable the debug variant when a debug mode is actually active.
        if (debugRenderMode == DebugRenderMode.FinalColor || debugRenderMode == DebugRenderMode.Caustics
            || debugRenderMode >= DebugRenderMode.RawBeauty)
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
            SetCausticShaderParameters(kernelHandle);
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
        shader.SetInt("_MaxLightSamples", maxLightSamples);
        shader.SetInt("_LightSamplingStrategy", (int)lightSamplingStrategy);
        shader.SetInt("_LightSampleCount", lightSampleCount);

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
        shader.SetInt("_ShadowQuality", shadowQuality);
        shader.SetFloat("_ShadowRandomness", shadowRandomness);
        shader.SetFloat("_LightFalloffScale", lightFalloffScale);
        shader.SetFloat("_FocalDistance", cameraFocalDistance);
        shader.SetFloat("_ApertureRadius", GetCameraApertureRadius());
        shader.SetInt("_ApertureBladeCount", cameraApertureBladeCount >= 3 ? cameraApertureBladeCount : 0);
        shader.SetFloat("_ApertureBladeRotation", cameraApertureBladeRotation * Mathf.Deg2Rad);
        shader.SetFloat("_AnamorphicRatio", Mathf.Clamp(cameraAnamorphicRatio, 0.25f, 4.0f));
        shader.SetFloat("_Exposure", exposure);
        shader.SetFloat("_FireflyClamp", Mathf.Max(0.0f, fireflyClamp));
        SetWaterShaderParameters();
        bool fogEnabled = IsFogEnabled();
        Vector3 fogCenter = fogEnabled ? _fogVolume.Center : Vector3.zero;
        Vector3 fogSize = fogEnabled ? _fogVolume.Size : Vector3.one;
        Color fogAlbedo = fogEnabled ? _fogVolume.ScatteringAlbedo : Color.black;
        shader.SetInt("_FogEnabled", fogEnabled ? 1 : 0);
        Vector3 fogBoundsMin = fogCenter - fogSize * 0.5f;
        Vector3 fogBoundsMax = fogCenter + fogSize * 0.5f;
        shader.SetVector("_FogBoundsMin", new Vector4(fogBoundsMin.x, fogBoundsMin.y, fogBoundsMin.z, 0.0f));
        shader.SetVector("_FogBoundsMax", new Vector4(fogBoundsMax.x, fogBoundsMax.y, fogBoundsMax.z, 0.0f));
        shader.SetVector("_FogScatteringAlbedo", new Vector4(
            Mathf.Clamp01(fogAlbedo.r * fogScatteringScale),
            Mathf.Clamp01(fogAlbedo.g * fogScatteringScale),
            Mathf.Clamp01(fogAlbedo.b * fogScatteringScale),
            0.0f));
        shader.SetFloat("_FogDensity", fogEnabled ? EffectiveFogDensity : 0.0f);
        shader.SetFloat("_FogInScatteringIntensity", Mathf.Max(0.0f, fogInScatteringIntensity));
        shader.SetInt("_FogMultipleScattering", enableFogMultipleScattering ? 1 : 0);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);

        // When no shadow-casting blocker is transparent, the shader can use a cheaper
        // pure-occlusion shadow path that early-outs on the first opaque blocker.
        bool hasTransparentShadowBlockers = _hasTransparentSphereBlockers || _hasTransparentMeshBlockers;
        shader.SetInt("_HasTransparentShadowBlockers", hasTransparentShadowBlockers ? 1 : 0);
        SetSceneBuffers(kernelHandle);
    }

    private void SetWaterShaderParameters()
    {
        bool waterEnabled = _water != null;
        Vector3 waterCenter = waterEnabled ? _water.TopCenter : Vector3.zero;
        Vector2 waterSize = waterEnabled ? _water.Size : Vector2.one;
        Color32 waterColor = waterEnabled ? _water.Color : new Color32(255, 255, 255, 255);
        shader.SetInt("_WaterEnabled", waterEnabled ? 1 : 0);
        shader.SetVector("_WaterCenter", new Vector4(waterCenter.x, waterCenter.y, waterCenter.z, 0.0f));
        shader.SetVector("_WaterSize", new Vector4(waterSize.x, waterSize.y, 0.0f, 0.0f));
        shader.SetFloat("_WaterDepth", waterEnabled ? _water.Depth : 1.0f);
        Vector3 waterColorVector = waterColor.ToVector3();
        shader.SetVector("_WaterColor", new Vector4(waterColorVector.x, waterColorVector.y, waterColorVector.z, 0.0f));
        shader.SetFloat("_WaterSmoothness", waterEnabled ? _water.Smoothness : 0.0f);
        shader.SetFloat("_WaterOpacity", waterEnabled ? Mathf.Clamp01(_water.Opacity) : 0.0f);
        shader.SetFloat("_WaterAbsorptionStrength", waterEnabled ? Mathf.Max(0.0f, _water.AbsorptionStrength) : 0.0f);
        shader.SetFloat("_WaterRefraction", waterEnabled ? _water.RefractionIndex : 1.0f);
        shader.SetFloat("_WaterWaveAmplitude", waterEnabled ? Mathf.Max(0.0f, _water.WaveAmplitude) : 0.0f);
        shader.SetFloat("_WaterWaveScale", waterEnabled ? Mathf.Max(0.001f, _water.WaveScale) : 1.0f);
        shader.SetFloat("_WaterWaveSpeed", waterEnabled ? Mathf.Max(0.0f, _water.WaveSpeed) : 0.0f);
        shader.SetFloat("_WaterTime", Application.isPlaying ? GetRenderTime() : 0.0f);
        shader.SetInt("_WaterMarchSteps", waterEnabled ? Mathf.Clamp(_water.MarchSteps, 8, 64) : 8);
        shader.SetInt("_WaterRefinementSteps", waterEnabled ? Mathf.Clamp(_water.RefinementSteps, 2, 8) : 2);
    }

    private void EnsureMeshTextureArrays()
    {
        if (_meshAlbedoTextureArray == null
            || _meshMetallicRoughnessTextureArray == null
            || _meshNormalTextureArray == null)
        {
            RebuildMeshTextureArrays();
        }
    }

    private int CalculateSampleOffset()
    {
        long frameIndex = ShouldUseFrameAccumulation() ? _accumulatedFrameCount : _renderedFrameCount;
        long sampleOffset = frameIndex * Mathf.Max(1, numberOfPasses);
        return (int)Math.Min(int.MaxValue, sampleOffset);
    }

    private float GetCameraApertureRadius()
    {
        if (cameraApertureMode == CameraApertureMode.Pinhole || IsTrackedFocusPointOutsideFrustum())
        {
            return 0.0f;
        }

        if (cameraApertureMode == CameraApertureMode.LensRadius)
        {
            return Mathf.Max(0.0f, cameraApertureRadius);
        }

        float focalLengthInWorldUnits = Mathf.Max(0.0f, renderTextureCamera.focalLength) * 0.001f;
        return focalLengthInWorldUnits / (2.0f * Mathf.Max(0.1f, cameraFStop))
            * Mathf.Max(0.0f, cameraApertureScale);
    }

    private bool IsTrackedFocusPointOutsideFrustum()
    {
        return enableClickToFocus
            && trackClickedFocusPoint
            && _hasClickedFocusPoint
            && !_clickedFocusPointInFrustum;
    }

    private int CalculateAccumulationStateHash()
    {
        unchecked
        {
            int hash = 17;
            hash = AddHash(hash, _textureSize.x);
            hash = AddHash(hash, _textureSize.y);
            hash = AddHash(hash, numberOfPasses);
            hash = AddHash(hash, numBounces);
            hash = AddHash(hash, shadowQuality);
            hash = AddHash(hash, topLevelBvhMinObjectCount);
            hash = AddHash(hash, shadowBvhMinObjectCount);
            hash = AddHash(hash, maxLightSamples);
            hash = AddHash(hash, (int)lightSamplingStrategy);
            hash = AddHash(hash, lightSampleCount);
            hash = AddHash(hash, shadowRandomness);
            hash = AddHash(hash, lightFalloffScale);
            hash = AddHash(hash, cameraFocalDistance);
            hash = AddHash(hash, (int)cameraApertureMode);
            hash = AddHash(hash, cameraApertureRadius);
            hash = AddHash(hash, cameraFStop);
            hash = AddHash(hash, cameraApertureScale);
            hash = AddHash(hash, cameraApertureBladeCount);
            hash = AddHash(hash, cameraApertureBladeRotation);
            hash = AddHash(hash, cameraAnamorphicRatio);
            hash = AddHash(hash, IsTrackedFocusPointOutsideFrustum() ? 1 : 0);
            hash = AddHash(hash, fireflyClamp);
            if (enableCaustics)
            {
                hash = AddHash(hash, causticPhotonCount);
                hash = AddHash(hash, causticGatherRadius);
                hash = AddHash(hash, causticSeed);
                hash = AddHash(hash, causticIntensity);
                hash = AddHash(hash, _causticPhotonStateHash);
            }
            hash = AddHash(hash, _water != null ? _water.GetInstanceID() : 0);
            if (_water != null)
            {
                hash = AddHash(hash, _water.TopCenter);
                hash = AddHash(hash, new Vector3(_water.Size.x, _water.Size.y, _water.Depth));
                hash = AddHash(hash, _water.Color.r);
                hash = AddHash(hash, _water.Color.g);
                hash = AddHash(hash, _water.Color.b);
                hash = AddHash(hash, _water.Smoothness);
                hash = AddHash(hash, _water.Opacity);
                hash = AddHash(hash, _water.AbsorptionStrength);
                hash = AddHash(hash, _water.RefractionIndex);
                hash = AddHash(hash, _water.WaveAmplitude);
                hash = AddHash(hash, _water.WaveScale);
                hash = AddHash(hash, _water.WaveSpeed);
                hash = AddHash(hash, _water.MarchSteps);
                hash = AddHash(hash, _water.RefinementSteps);
            }
            hash = AddHash(hash, _fogVolume != null ? _fogVolume.GetInstanceID() : 0);
            hash = AddHash(hash, enableVolumetricFog ? 1 : 0);
            hash = AddHash(hash, fogDensityScale);
            hash = AddHash(hash, fogScatteringScale);
            hash = AddHash(hash, fogInScatteringIntensity);
            hash = AddHash(hash, enableFogMultipleScattering ? 1 : 0);
            if (_fogVolume != null)
            {
                hash = AddHash(hash, _fogVolume.Center);
                hash = AddHash(hash, _fogVolume.Size);
                hash = AddHash(hash, _fogVolume.Density);
                hash = AddHash(hash, _fogVolume.ScatteringAlbedo.r);
                hash = AddHash(hash, _fogVolume.ScatteringAlbedo.g);
                hash = AddHash(hash, _fogVolume.ScatteringAlbedo.b);
            }
            hash = AddHash(hash, randomNoise ? 1 : 0);
            hash = AddHash(hash, skyboxTexture != null ? skyboxTexture.GetInstanceID() : 0);
            hash = AddHash(hash, _skyboxLightColor.r);
            hash = AddHash(hash, _skyboxLightColor.g);
            hash = AddHash(hash, _skyboxLightColor.b);
            hash = AddHash(hash, renderTextureCamera.cameraToWorldMatrix);
            hash = AddHash(hash, renderTextureCamera.projectionMatrix);
            hash = AddHash(hash, _spheres.Count);
            for (int i = 0; i < _spheres.Count; i++)
            {
                hash = AddHash(hash, _spheres[i]);
            }

            hash = AddHash(hash, _lights.Count);
            for (int i = 0; i < _lights.Count; i++)
            {
        hash = AddHash(hash, _lights[i]);
            }

            hash = AddHash(hash, _triangles.Count);
            hash = AddHash(hash, _meshInfos.Count);
            for (int i = 0; i < _meshObjects.Count; i++)
            {
                hash = AddHash(hash, _meshObjects[i]);
            }

            return hash;
        }
    }

    private int CalculateCausticPhotonStateHash()
    {
        unchecked
        {
            int hash = 17;
            hash = AddHash(hash, 5); // Progressive, low-discrepancy photon-map algorithm version.
            hash = AddHash(hash, causticPhotonCount);
            hash = AddHash(hash, causticGatherRadius);
            hash = AddHash(hash, causticSeed);
            hash = AddHash(hash, numBounces);
            hash = AddHash(hash, _spheres.Count);
            for (int i = 0; i < _spheres.Count; i++)
            {
                hash = AddHash(hash, _spheres[i]);
            }

            hash = AddHash(hash, _lights.Count);
            for (int i = 0; i < _lights.Count; i++)
            {
                hash = AddHash(hash, _lights[i]);
            }

            hash = AddHash(hash, _triangles.Count);
            hash = AddHash(hash, _meshInfos.Count);
            for (int i = 0; i < _meshObjects.Count; i++)
            {
                hash = AddHash(hash, _meshObjects[i]);
            }
            hash = AddHash(hash, _water != null ? _water.GetInstanceID() : 0);
            if (_water != null)
            {
                hash = AddHash(hash, _water.TopCenter);
                hash = AddHash(hash, new Vector3(_water.Size.x, _water.Size.y, _water.Depth));
                hash = AddHash(hash, _water.Color.r);
                hash = AddHash(hash, _water.Color.g);
                hash = AddHash(hash, _water.Color.b);
                hash = AddHash(hash, _water.Smoothness);
                hash = AddHash(hash, _water.Opacity);
                hash = AddHash(hash, _water.AbsorptionStrength);
                hash = AddHash(hash, _water.RefractionIndex);
                hash = AddHash(hash, _water.WaveAmplitude);
                hash = AddHash(hash, _water.WaveScale);
                hash = AddHash(hash, _water.WaveSpeed);
                hash = AddHash(hash, _water.MarchSteps);
                hash = AddHash(hash, _water.RefinementSteps);
                // Frozen water is deterministic in single-frame mode; include the phase otherwise.
                if (!_singleFrame && _water.WaveAmplitude > 0.0f && _water.WaveSpeed > 0.0f)
                {
                    hash = AddHash(hash, GetRenderTime());
                }
            }
            return hash;
        }
    }

    private static int AddHash(int hash, int value)
    {
        unchecked
        {
            return hash * 31 + value;
        }
    }

    private static int AddHash(int hash, float value)
    {
        return AddHash(hash, value.GetHashCode());
    }

    private static int AddHash(int hash, Vector3 value)
    {
        hash = AddHash(hash, value.x);
        hash = AddHash(hash, value.y);
        return AddHash(hash, value.z);
    }

    private static int AddHash(int hash, Matrix4x4 value)
    {
        for (int i = 0; i < 16; i++)
        {
            hash = AddHash(hash, value[i]);
        }

        return hash;
    }

    private static int AddHash(int hash, Sphere value)
    {
        hash = AddHash(hash, value.position);
        hash = AddHash(hash, value.color);
        hash = AddHash(hash, value.emission);
        hash = AddHash(hash, value.radius);
        hash = AddHash(hash, value.smoothness);
        hash = AddHash(hash, value.opacity);
        hash = AddHash(hash, value.refraction);
        return AddHash(hash, value.materialType);
    }

    private static int AddHash(int hash, Light value)
    {
        hash = AddHash(hash, value.position);
        hash = AddHash(hash, value.emission);
        hash = AddHash(hash, value.u);
        hash = AddHash(hash, value.radius);
        hash = AddHash(hash, value.v);
        hash = AddHash(hash, value.area);
        hash = AddHash(hash, value.normal);
        return AddHash(hash, value.type);
    }

    private static int AddHash(int hash, RayTracedMesh value)
    {
        hash = AddHash(hash, value.transform.localToWorldMatrix);
        hash = AddHash(hash, value.previousColor);
        hash = AddHash(hash, value.previousEmission);
        hash = AddHash(hash, value.previousSmoothness);
        hash = AddHash(hash, value.previousMetallic);
        hash = AddHash(hash, value.previousOpacity);
        hash = AddHash(hash, value.previousRefraction);
        hash = AddHash(hash, value.previousMaterialType);
        hash = AddHash(hash, value.previousAlbedoTexture != null ? value.previousAlbedoTexture.GetInstanceID() : 0);
        hash = AddHash(hash, value.previousMetallicRoughnessTexture != null ? value.previousMetallicRoughnessTexture.GetInstanceID() : 0);
        hash = AddHash(hash, value.previousNormalTexture != null ? value.previousNormalTexture.GetInstanceID() : 0);
        return AddHash(hash, value.previousInterpolateNormals ? 1 : 0);
    }
}
