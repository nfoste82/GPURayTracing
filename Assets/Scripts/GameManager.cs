using System;
using System.Collections.Generic;
using DefaultNamespace;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
#endif

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;
    
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
    public LightSamplingStrategy lightSamplingStrategy = LightSamplingStrategy.AllLights;

    [Tooltip("UniformRandom/ImportanceSampled only: how many lights each shading point samples per pass. 1 is fastest/noisiest; higher values reduce noise toward AllLights quality at proportional cost.")]
    [Range(1, 64)]
    public int lightSampleCount = 1;

    [Header("Caustics prototype")]
    [Tooltip("Builds a photon map for sphere-light caustics through glass spheres. Disabled by default.")]
    public bool enableCaustics = false;

    [Range(64, 16384)]
    public int causticPhotonCount = 2048;

    [Range(0.01f, 2.0f)]
    public float causticGatherRadius = 0.3f;

    public int causticSeed = 1;

    [Range(0.0f, 4.0f)]
    public float causticIntensity = 1.0f;
    
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
        Caustics = 10
    }

    [Header("Debug render modes")]
    public DebugRenderMode debugRenderMode = DebugRenderMode.FinalColor;

    [Header("Misc settings")]
    public bool cameraAutoFocus = true;

    [Tooltip("Autofocus ignores ray-traced objects with opacity at or below this value, allowing focus through mostly transparent glass.")]
    [Range(0.0f, 1.0f)]
    public float autoFocusTransparentOpacityThreshold = 0.5f;
    
    [Range(0.1f, 100f)]
    public float cameraFocalDistance = 100f;

    [Range(0.0f, 1.0f)]
    public float groundSmoothness = 0.98f;

    [Tooltip("Higher values make direct light fall off faster with distance.")]
    [Range(0.001f, 1.0f)]
    public float lightFalloffScale = 0.16f;

    [Tooltip("Master brightness applied before ACES tone mapping. Acts like a camera exposure dial.")]
    [Range(0.0f, 8.0f)]
    public float exposure = 1.0f;

    [Tooltip("Maximum luminance of one path sample before averaging. Reduces persistent fireflies from rare specular paths; 0 disables the clamp.")]
    [Range(0.0f, 32.0f)]
    public float fireflyClamp = 0.0f;

    private float previousFocalDistance = 100f;
    private float timeSincePreviousFocusDistance = 1f;

    public bool randomNoise = false;

    public Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);

    public Texture skyboxTexture;

    [Tooltip("Fallback texture used when no mesh albedo textures are active. Created automatically at runtime if unset.")]
    public Texture2D defaultMeshAlbedoTexture;

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
    private readonly List<Texture2D> _meshAlbedoTextures = new List<Texture2D>();
    private Texture2DArray _meshAlbedoTextureArray;
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _meshBuffer;
    private ComputeBuffer _bvhNodeBuffer;
    private ComputeBuffer _topLevelBvhNodeBuffer;
    private ComputeBuffer _shadowBvhNodeBuffer;
    private ComputeBuffer _causticPhotonBuffer;
    private ComputeBuffer _causticPhotonMetadataBuffer;
    private int _causticPhotonStateHash;
    private bool _hasCausticPhotonStateHash;
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
    public bool HasCausticResources => _causticPhotonBuffer != null && _causticPhotonMetadataBuffer != null;
    public int CausticDispatchCount => _causticDispatchCount;

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
    private const int MeshTextureSize = 128;
    private const int TriangleStride = 164;
    private const int MeshInfoStride = 48;
    private const int BvhNodeStride = 48;
    private const int TopLevelBvhNodeStride = 48;
    private const int CausticPhotonStride = 36;
    private const int CausticMetadataCount = 4;
    private const int CausticTraceThreadCount = 64;
    private const int BvhLeafTriangleCount = 4;
    private const int BvhStackSize = 64;
    private const float BvhBoundsPadding = 0.0001f;
    private const float GroundPreviewSize = 40.0f;
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
        public Vector3 color;
        public float smoothness;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector2 uv2;
        public float opacity;
        public Vector3 emission;
        public float refraction;
        public int materialType;
        public int meshIndex;
        public int textureIndex;
        public int interpolateNormals;
        public int lightIndex;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var edge1 = vertex1 - vertex0;
            var edge2 = vertex2 - vertex0;
            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);

            if (Mathf.Abs(determinant) < 0.000001f)
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
        public float previousOpacity;
        public float previousRefraction;
        public int previousMaterialType;
        public Texture2D previousAlbedoTexture;
        public bool previousInterpolateNormals;
    }

    private Water _water;

    private void Start()
    {
        SyncUnitySkyboxPreview();
        CreateOutputTexture(Screen.width, Screen.height);
        RebuildBuffers();
    }

    private void OnValidate()
    {
        SyncUnitySkyboxPreview();
    }

    private void OnDrawGizmos()
    {
        DrawGroundPreview();
    }

    private void OnDrawGizmosSelected()
    {
        DrawGroundPreview();
    }

    private static void DrawGroundPreview()
    {
#if UNITY_EDITOR
        var vertices = new[]
        {
            new Vector3(-GroundPreviewSize * 0.5f, 0.0f, -GroundPreviewSize * 0.5f),
            new Vector3(-GroundPreviewSize * 0.5f, 0.0f, GroundPreviewSize * 0.5f),
            new Vector3(GroundPreviewSize * 0.5f, 0.0f, GroundPreviewSize * 0.5f),
            new Vector3(GroundPreviewSize * 0.5f, 0.0f, -GroundPreviewSize * 0.5f)
        };

        var previousZTest = Handles.zTest;
        Handles.zTest = CompareFunction.LessEqual;
        Handles.DrawSolidRectangleWithOutline(vertices, new Color(0.8f, 0.8f, 0.8f, 1.0f), new Color(0.8f, 0.8f, 0.8f, 1.0f));

        float halfSize = GroundPreviewSize * 0.5f;
        const float gridSpacing = 2.0f;
        Handles.color = new Color(0.55f, 0.55f, 0.55f, 1.0f);
        for (float offset = -halfSize; offset <= halfSize; offset += gridSpacing)
        {
            Handles.DrawLine(new Vector3(-halfSize, 0.001f, offset), new Vector3(halfSize, 0.001f, offset));
            Handles.DrawLine(new Vector3(offset, 0.001f, -halfSize), new Vector3(offset, 0.001f, halfSize));
        }
        Handles.zTest = previousZTest;
#else
        var center = new Vector3(0.0f, -0.001f, 0.0f);
        var size = new Vector3(GroundPreviewSize, 0.02f, GroundPreviewSize);
        float halfSize = GroundPreviewSize * 0.5f;
        const float gridSpacing = 2.0f;

        Gizmos.color = new Color(0.8f, 0.8f, 0.8f, 1.0f);
        Gizmos.DrawWireCube(center, size);

        for (float offset = -halfSize; offset <= halfSize; offset += gridSpacing)
        {
            Gizmos.DrawLine(new Vector3(-halfSize, 0.0f, offset), new Vector3(halfSize, 0.0f, offset));
            Gizmos.DrawLine(new Vector3(offset, 0.0f, -halfSize), new Vector3(offset, 0.0f, halfSize));
        }
#endif
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
        ResetFrameAccumulation();
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
        
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            camera.transform.eulerAngles += new Vector3(0f, -movementDelta * 50f, 0f);
        }
        else if (Input.GetKey(KeyCode.RightArrow))
        {
            camera.transform.eulerAngles += new Vector3(0f, movementDelta * 50f, 0f);
        }
        
        if (Input.GetKey(KeyCode.UpArrow))
        {
            camera.transform.eulerAngles += new Vector3(movementDelta * 50f, 0f, 0f);
        }
        else if (Input.GetKey(KeyCode.DownArrow))
        {
            camera.transform.eulerAngles += new Vector3(-movementDelta * 50f, 0f, 0f);
        }
    }
    
    private void OnDestroy()
    {
        _outputTexture?.Release();
        _accumulationTexture?.Release();
        _sphereBuffer?.Release();
        _lightBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
        _topLevelBvhNodeBuffer?.Release();
        _shadowBvhNodeBuffer?.Release();
        ReleaseCausticResources();
        DestroyRuntimeTextureArray();
    }

    private void DestroyRuntimeTextureArray()
    {
        if (_meshAlbedoTextureArray == null)
        {
            return;
        }

        Destroy(_meshAlbedoTextureArray);
        _meshAlbedoTextureArray = null;
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
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }

    private void ResetFrameAccumulation()
    {
        _accumulatedFrameCount = 0;
        _hasAccumulationStateHash = false;
    }

    private void EnsureCausticResources()
    {
        int photonCapacity = Mathf.Max(1, causticPhotonCount);
        if (_causticPhotonBuffer != null && _causticPhotonBuffer.count == photonCapacity && _causticPhotonMetadataBuffer != null)
        {
            return;
        }

        ReleaseCausticResources();
        _causticPhotonBuffer = new ComputeBuffer(photonCapacity, CausticPhotonStride);
        _causticPhotonMetadataBuffer = new ComputeBuffer(CausticMetadataCount, sizeof(uint));
        _hasCausticPhotonStateHash = false;
    }

    private void ReleaseCausticResources()
    {
        _causticPhotonBuffer?.Release();
        _causticPhotonMetadataBuffer?.Release();
        _causticPhotonBuffer = null;
        _causticPhotonMetadataBuffer = null;
        _hasCausticPhotonStateHash = false;
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

        EnsureCausticResources();
        int stateHash = CalculateCausticPhotonStateHash();
        if (_hasCausticPhotonStateHash && stateHash == _causticPhotonStateHash)
        {
            _previousCausticsEnabled = true;
            return;
        }

        shader.EnableKeyword("CAUSTICS_ENABLED");
        int clearKernel = shader.FindKernel("ClearCausticPhotons");
        int traceKernel = shader.FindKernel("TraceCausticPhotons");
        SetPhotonTraceSceneParameters();
        SetCausticShaderParameters(clearKernel);
        SetCausticShaderParameters(traceKernel);
        SetSceneBuffers(traceKernel);
        shader.Dispatch(clearKernel, 1, 1, 1);
        shader.Dispatch(traceKernel, Mathf.CeilToInt(Mathf.Max(1, causticPhotonCount) / (float)CausticTraceThreadCount), 1, 1);
        _causticDispatchCount++;
        _causticPhotonStateHash = stateHash;
        _hasCausticPhotonStateHash = true;
        _previousCausticsEnabled = true;
        ResetFrameAccumulation();
    }

    private void SetPhotonTraceSceneParameters()
    {
        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);
        shader.SetInt("_WaterEnabled", 0);
    }

    private bool ShouldUseFrameAccumulation()
    {
        bool animatedWater = _water != null && _water.WaveAmplitude > 0.0f && _water.WaveSpeed > 0.0f && !_singleFrame;
        return enableFrameAccumulation && debugRenderMode == DebugRenderMode.FinalColor && !animatedWater;
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

        // Detect a switch to a debug variant that has not been compiled yet. The first Dispatch of
        // a new variant blocks the main thread while the GPU backend compiles it. To avoid an
        // apparently frozen app, we defer that blocking dispatch by one frame: this frame we set the
        // overlay flag and re-show the previous output (no heavy dispatch), so OnGUI can paint the
        // "Compiling shader variant" message; next frame we run the stalling dispatch with that
        // message already on screen.
        int requestedVariant = GetShaderVariantKey(debugRenderMode, enableCaustics);
        bool shaderVariantChanged = debugRenderMode != _appliedDebugRenderMode || enableCaustics != _appliedCausticsEnabled;
        if (!_pendingVariantWarmup
            && shaderVariantChanged
            && !_warmedShaderVariants.Contains(requestedVariant))
        {
            _pendingVariantWarmup = true;
            Graphics.Blit(_outputTexture, dest);
            return;
        }

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

        UpdateSpheres();
        UpdateTriangles();
        UpdateTopLevelBvh();
        UpdateShadowBvh();
        UpdateCausticPhotonMap();

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

        var kernelHandle = shader.FindKernel("CSMain");

        SetShaderParameters(kernelHandle);
        UpdateTextureFromCompute(kernelHandle);
        _renderedFrameCount++;

        if (useFrameAccumulation)
        {
            _accumulatedFrameCount++;
        }

        // The dispatch above triggered (and blocked on) any first-time variant compile. Record
        // that this debug mode is now warm so future switches to it are instant, and clear the
        // overlay flag.
        _warmedShaderVariants.Add(requestedVariant);
        _appliedDebugRenderMode = debugRenderMode;
        _appliedCausticsEnabled = enableCaustics;
        _pendingVariantWarmup = false;

        Graphics.Blit(_outputTexture, dest);
    }

    private static int GetShaderVariantKey(DebugRenderMode mode, bool causticsEnabled)
    {
        return ((int)mode << 1) | (causticsEnabled ? 1 : 0);
    }

    private void UpdateSpheres()
    {
        _hasTransparentSphereBlockers = false;
        for (int i = 0; i < _spheres.Count; ++i)
        {
            var sphere = _spheres[i];
            var sphereObject = _sphereObjects[i];

            sphere.position = sphereObject.transform.TransformPoint(sphereObject.collider.center);

            sphere.radius = GetWorldSphereRadius(sphereObject.collider, sphereObject.transform);

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

            lightData.position = lightObject.transform.TransformPoint(lightObject.collider.center);

            lightData.radius = GetWorldSphereRadius(lightObject.collider, lightObject.transform);
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

        if (!UpdateMeshChangeCache())
        {
            return;
        }

        RebuildTriangleData();

        if (_triangleBuffer != null && _triangles.Count > 0)
        {
            _triangleBuffer.SetData(_triangles);
        }

        if (_meshBuffer != null && _meshInfos.Count > 0)
        {
            _meshBuffer.SetData(_meshInfos);
        }

        if (_bvhNodeBuffer != null && _bvhNodes.Count > 0)
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

    private bool UpdateMeshChangeCache()
    {
        bool changed = false;
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
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            var materialType = light != null ? 3 : (int)material.Type;
            var albedoTexture = material != null ? material.AlbedoTexture : null;
            bool interpolateNormals = material != null && material.InterpolateNormals;

            if (opacity < ShadowBlockerOpaqueThreshold)
            {
                _hasTransparentMeshBlockers = true;
            }

            if (meshObject.previousLocalToWorld == localToWorld
                && meshObject.previousColor == color
                && meshObject.previousEmission == emission
                && Mathf.Approximately(meshObject.previousSmoothness, smoothness)
                && Mathf.Approximately(meshObject.previousOpacity, opacity)
                && Mathf.Approximately(meshObject.previousRefraction, refraction)
                && meshObject.previousMaterialType == materialType
                && meshObject.previousAlbedoTexture == albedoTexture
                && meshObject.previousInterpolateNormals == interpolateNormals)
            {
                continue;
            }

            meshObject.previousLocalToWorld = localToWorld;
            meshObject.previousColor = color;
            meshObject.previousEmission = emission;
            meshObject.previousSmoothness = smoothness;
            meshObject.previousOpacity = opacity;
            meshObject.previousRefraction = refraction;
            meshObject.previousMaterialType = materialType;
            meshObject.previousAlbedoTexture = albedoTexture;
            meshObject.previousInterpolateNormals = interpolateNormals;
            _meshObjects[i] = meshObject;
            changed = true;
        }

        return changed;
    }

    private void RebuildTriangleData()
    {
        _triangles.Clear();
        _meshInfos.Clear();
        _bvhNodes.Clear();
        _meshAlbedoTextures.Clear();
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

            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            var uvs = mesh.uv;
            var normals = mesh.normals;
            var localToWorld = meshObject.transform.localToWorldMatrix;
            var normalToWorld = localToWorld.inverse.transpose;
            var material = meshObject.material;
            var light = meshObject.light;
            bool isLight = light != null;
            var color = material != null ? material.Color.ToVector3() : Vector3.one;
            var emission = isLight ? light.Color.ToVector3() : Vector3.zero;
            var smoothness = material != null ? material.Smoothness : 0.0f;
            var opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
            var refraction = material != null ? material.RefractionIndex : 1.0f;
            int materialType = isLight ? 3 : (int)material.Type;
            int textureIndex = material != null ? GetMeshAlbedoTextureIndex(material.AlbedoTexture) : -1;
            bool interpolateNormals = material != null && material.InterpolateNormals && normals.Length == vertices.Length;
            var meshTriangles = new List<Triangle>(indices.Length / 3);

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int index0 = indices[i];
                int index1 = indices[i + 1];
                int index2 = indices[i + 2];
                var vertex0 = localToWorld.MultiplyPoint3x4(vertices[index0]);
                var vertex1 = localToWorld.MultiplyPoint3x4(vertices[index1]);
                var vertex2 = localToWorld.MultiplyPoint3x4(vertices[index2]);
                var normal = Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0).normalized;
                var normal0 = interpolateNormals ? normalToWorld.MultiplyVector(normals[index0]).normalized : normal;
                var normal1 = interpolateNormals ? normalToWorld.MultiplyVector(normals[index1]).normalized : normal;
                var normal2 = interpolateNormals ? normalToWorld.MultiplyVector(normals[index2]).normalized : normal;

                int lightIndex = isLight ? _lights.Count : -1;
                meshTriangles.Add(new Triangle
                {
                    vertex0 = vertex0,
                    vertex1 = vertex1,
                    vertex2 = vertex2,
                    normal = normal,
                    normal0 = normal0,
                    normal1 = normal1,
                    normal2 = normal2,
                    color = color,
                    emission = emission,
                    uv0 = GetMeshUv(uvs, index0),
                    uv1 = GetMeshUv(uvs, index1),
                    uv2 = GetMeshUv(uvs, index2),
                    smoothness = smoothness,
                    opacity = opacity,
                    refraction = refraction,
                    materialType = materialType,
                    meshIndex = meshIndex,
                    textureIndex = textureIndex,
                    interpolateNormals = interpolateNormals ? 1 : 0,
                    lightIndex = lightIndex
                });

                if (isLight)
                {
                    AddTriangleLight(vertex0, vertex1, vertex2, normal, emission);
                }
            }

            if (meshTriangles.Count == 0)
            {
                continue;
            }

            var triangleStart = _triangles.Count;
            var rootNodeIndex = BuildBvhNode(meshTriangles, 0, meshTriangles.Count);
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

        RebuildMeshAlbedoTextureArray();
    }

    private int GetMeshAlbedoTextureIndex(Texture2D texture)
    {
        if (texture == null)
        {
            return -1;
        }

        int existingIndex = _meshAlbedoTextures.IndexOf(texture);
        if (existingIndex >= 0)
        {
            return existingIndex;
        }

        _meshAlbedoTextures.Add(texture);
        return _meshAlbedoTextures.Count - 1;
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

    private void RebuildMeshAlbedoTextureArray()
    {
        EnsureDefaultMeshAlbedoTexture();
        DestroyRuntimeTextureArray();

        int textureCount = Mathf.Max(1, _meshAlbedoTextures.Count);
        _meshAlbedoTextureArray = new Texture2DArray(
            MeshTextureSize,
            MeshTextureSize,
            textureCount,
            TextureFormat.RGBA32,
            false)
        {
            name = "Ray Tracing Mesh Albedo Texture Array",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point
        };

        for (int i = 0; i < textureCount; i++)
        {
            Texture2D source = i < _meshAlbedoTextures.Count && _meshAlbedoTextures[i] != null
                ? _meshAlbedoTextures[i]
                : defaultMeshAlbedoTexture;
            CopyTextureToArraySlice(source, _meshAlbedoTextureArray, i);
        }

        _meshAlbedoTextureArray.Apply(false, false);
    }

    private void EnsureDefaultMeshAlbedoTexture()
    {
        if (defaultMeshAlbedoTexture != null)
        {
            return;
        }

        defaultMeshAlbedoTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            name = "Default Mesh Albedo",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point
        };
        defaultMeshAlbedoTexture.SetPixel(0, 0, Color.white);
        defaultMeshAlbedoTexture.Apply(false, true);
    }

    private static void CopyTextureToArraySlice(Texture2D source, Texture2DArray destination, int slice)
    {
        var pixels = new Color32[MeshTextureSize * MeshTextureSize];
        if (source == null)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }
        }
        else if (source.isReadable)
        {
            for (int y = 0; y < MeshTextureSize; y++)
            {
                float v = (y + 0.5f) / MeshTextureSize;
                for (int x = 0; x < MeshTextureSize; x++)
                {
                    float u = (x + 0.5f) / MeshTextureSize;
                    pixels[y * MeshTextureSize + x] = source.GetPixelBilinear(u, v);
                }
            }
        }
        else
        {
            Debug.LogWarning($"Mesh albedo texture '{source.name}' is not readable; using white fallback for ray tracing.");
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
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
        int leftCount = FindTopLevelSahSplit(items, start, count);

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

    // Top-level / shadow BVH equivalent of FindTriangleSahSplit. Scores candidate splits across
    // all three axes by SAH and leaves items sorted on the winning axis so the chosen split is
    // contiguous. Falls back to a longest-axis median split if no positive-area split is found.
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

    private int BuildBvhNode(List<Triangle> meshTriangles, int start, int count, int depth = 1)
    {
        if (depth > BvhStackSize)
        {
            throw new InvalidOperationException($"Mesh BVH depth {depth} exceeds traversal stack capacity {BvhStackSize}.");
        }

        var nodeIndex = _bvhNodes.Count;
        var boundsMin = GetTriangleBoundsMin(meshTriangles[start]);
        var boundsMax = GetTriangleBoundsMax(meshTriangles[start]);

        for (int i = start + 1; i < start + count; i++)
        {
            Encapsulate(meshTriangles[i], ref boundsMin, ref boundsMax);
        }

        var padding = Vector3.one * BvhBoundsPadding;
        boundsMin -= padding;
        boundsMax += padding;

        _bvhNodes.Add(new BvhNode
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
            var triangleStart = _triangles.Count;
            for (int i = start; i < start + count; i++)
            {
                _triangles.Add(meshTriangles[i]);
            }

            _bvhNodes[nodeIndex] = new BvhNode
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

        int leftCount = FindTriangleSahSplit(meshTriangles, start, count);

        int rightCount = count - leftCount;
        int leftChildIndex = BuildBvhNode(meshTriangles, start, leftCount, depth + 1);
        int rightChildIndex = BuildBvhNode(meshTriangles, start + leftCount, rightCount, depth + 1);

        _bvhNodes[nodeIndex] = new BvhNode
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

    // Chooses how many triangles go to the left child using the surface area heuristic (SAH).
    // For each axis it sorts by centroid, sweeps every candidate split, and scores it as
    // SA(left)*leftCount + SA(right)*rightCount. The lowest-scoring split across all axes wins,
    // leaving meshTriangles sorted on the winning axis so [start, start+leftCount) is the left set.
    // Falls back to a median split if no positive-area split is found. This replaces the old
    // longest-axis median split with a quality-aware split that produces tighter, less overlapping
    // child bounds, so traversal skips more triangles.
    private int FindTriangleSahSplit(List<Triangle> meshTriangles, int start, int count)
    {
        int bestAxis = -1;
        int bestSplit = count / 2;
        float bestCost = float.MaxValue;

        EnsureSahScratch(count);

        for (int axis = 0; axis < 3; axis++)
        {
            int sortAxis = axis;
            meshTriangles.Sort(start, count, Comparer<Triangle>.Create((a, b) =>
                GetTriangleCentroid(a)[sortAxis].CompareTo(GetTriangleCentroid(b)[sortAxis])));

            // Suffix bounds: _sahSuffixArea[i] = half surface area of triangles [start+i, start+count).
            var suffixMin = GetTriangleBoundsMin(meshTriangles[start + count - 1]);
            var suffixMax = GetTriangleBoundsMax(meshTriangles[start + count - 1]);
            _sahSuffixArea[count - 1] = HalfSurfaceArea(suffixMax - suffixMin);
            for (int i = count - 2; i >= 0; i--)
            {
                Encapsulate(meshTriangles[start + i], ref suffixMin, ref suffixMax);
                _sahSuffixArea[i] = HalfSurfaceArea(suffixMax - suffixMin);
            }

            // Sweep left-to-right, growing the prefix bounds and combining with the suffix.
            var prefixMin = GetTriangleBoundsMin(meshTriangles[start]);
            var prefixMax = GetTriangleBoundsMax(meshTriangles[start]);
            for (int leftCount = 1; leftCount < count; leftCount++)
            {
                float leftArea = HalfSurfaceArea(prefixMax - prefixMin);
                float rightArea = _sahSuffixArea[leftCount];
                float cost = leftArea * leftCount + rightArea * (count - leftCount);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAxis = sortAxis;
                    bestSplit = leftCount;
                }

                Encapsulate(meshTriangles[start + leftCount], ref prefixMin, ref prefixMax);
            }
        }

        if (bestAxis < 0)
        {
            // No usable split found; fall back to longest-axis median split.
            bestAxis = GetLongestAxis(
                GetTriangleBoundsMax(meshTriangles[start]) - GetTriangleBoundsMin(meshTriangles[start]));
            bestSplit = count / 2;
        }

        // Re-sort on the winning axis so the chosen split is contiguous in the list.
        int finalAxis = bestAxis;
        meshTriangles.Sort(start, count, Comparer<Triangle>.Create((a, b) =>
            GetTriangleCentroid(a)[finalAxis].CompareTo(GetTriangleCentroid(b)[finalAxis])));

        return Mathf.Clamp(bestSplit, 1, count - 1);
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

    public void RebuildBuffers()
    {
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

        RebuildTriangleData();
        RebuildTopLevelBvh();
        RebuildShadowBvh();

        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);

        _sphereBuffer = CreateComputeBuffer(_spheres, SphereStride);
        _lightBuffer = CreateComputeBuffer(_lights, LightStride);
        _triangleBuffer = CreateComputeBuffer(_triangles, TriangleStride);
        _meshBuffer = CreateComputeBuffer(_meshInfos, MeshInfoStride);
        _bvhNodeBuffer = CreateComputeBuffer(_bvhNodes, BvhNodeStride);
        _topLevelBvhNodeBuffer = CreateComputeBuffer(_topLevelBvhNodes, TopLevelBvhNodeStride);
        _shadowBvhNodeBuffer = CreateComputeBuffer(_shadowBvhNodes, TopLevelBvhNodeStride);
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
                previousOpacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f,
                previousRefraction = material != null ? material.RefractionIndex : 1.0f,
                previousMaterialType = rayLight != null ? 3 : (int)material.Type,
                previousAlbedoTexture = material != null ? material.AlbedoTexture : null,
                previousInterpolateNormals = material != null && material.InterpolateNormals
            });
            RebuildTriangleData();
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
            RebuildTriangleData();
            return;
        }

        var meshIndex = _meshObjects.FindIndex(mesh => mesh.obj == obj);
        if (meshIndex >= 0)
        {
            _meshObjects.RemoveAt(meshIndex);
            RebuildTriangleData();
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
        shader.SetInt("_CausticSeed", causticSeed);
        shader.SetFloat("_CausticGatherRadius", Mathf.Max(0.001f, causticGatherRadius));
        shader.SetFloat("_CausticIntensity", Mathf.Max(0.0f, causticIntensity));
        SetComputeBuffer("_CausticPhotons", _causticPhotonBuffer, kernelHandle);
        SetComputeBuffer("_CausticPhotonMetadata", _causticPhotonMetadataBuffer, kernelHandle);
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
        
        // Ground plane
        {
            var hitDistance = -ray.origin.y / ray.direction.y;

            if (hitDistance > 0 && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
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
        if (_meshAlbedoTextureArray == null)
        {
            RebuildMeshAlbedoTextureArray();
        }
        shader.SetTexture(kernelHandle, "_MeshAlbedoTextures", _meshAlbedoTextureArray);

        shader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);

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
        shader.SetInt("_DebugRenderMode", (int)debugRenderMode);
        shader.SetInt("_UseFrameAccumulation", ShouldUseFrameAccumulation() ? 1 : 0);
        shader.SetInt("_AccumulatedFrameCount", _accumulatedFrameCount);
        shader.SetInt("_SampleOffset", CalculateSampleOffset());

        // The shader splits its debug render path behind the DEBUG_RENDER keyword so the default
        // final-color variant compiles without any debug intersection/scatter code (a large shader
        // compile-time saving). Only enable the debug variant when a debug mode is actually active.
        if (debugRenderMode == DebugRenderMode.FinalColor)
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
        shader.SetFloat("_GroundSmoothness", groundSmoothness);
        shader.SetFloat("_Exposure", exposure);
        shader.SetFloat("_FireflyClamp", Mathf.Max(0.0f, fireflyClamp));
        bool waterEnabled = _water != null;
        Vector3 waterCenter = waterEnabled ? _water.TopCenter : Vector3.zero;
        Vector2 waterSize = waterEnabled ? _water.Size : Vector2.one;
        Color32 waterColor = waterEnabled ? _water.Color : new Color32(255, 255, 255, 255);
        shader.SetInt("_WaterEnabled", waterEnabled ? 1 : 0);
        shader.SetVector("_WaterCenter", new Vector4(waterCenter.x, waterCenter.y, waterCenter.z, 0.0f));
        shader.SetVector("_WaterSize", new Vector4(waterSize.x, waterSize.y, 0.0f, 0.0f));
        shader.SetFloat("_WaterDepth", waterEnabled ? _water.Depth : 1.0f);
        var waterColorVector = waterColor.ToVector3();
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
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTopLevelBvhNodes", _topLevelBvhNodes.Count);
        shader.SetInt("_NumShadowBvhNodes", _shadowBvhNodes.Count);

        // When no shadow-casting blocker is transparent, the shader can use a cheaper
        // pure-occlusion shadow path that early-outs on the first opaque blocker.
        bool hasTransparentShadowBlockers = _hasTransparentSphereBlockers || _hasTransparentMeshBlockers;
        shader.SetInt("_HasTransparentShadowBlockers", hasTransparentShadowBlockers ? 1 : 0);
        SetSceneBuffers(kernelHandle);
    }

    private int CalculateSampleOffset()
    {
        long frameIndex = ShouldUseFrameAccumulation() ? _accumulatedFrameCount : _renderedFrameCount;
        long sampleOffset = frameIndex * Mathf.Max(1, numberOfPasses);
        return (int)Math.Min(int.MaxValue, sampleOffset);
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
            hash = AddHash(hash, groundSmoothness);
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
            hash = AddHash(hash, 1); // Photon-map algorithm version.
            hash = AddHash(hash, causticPhotonCount);
            hash = AddHash(hash, causticSeed);
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
        hash = AddHash(hash, value.previousOpacity);
        hash = AddHash(hash, value.previousRefraction);
        hash = AddHash(hash, value.previousMaterialType);
        hash = AddHash(hash, value.previousAlbedoTexture != null ? value.previousAlbedoTexture.GetInstanceID() : 0);
        return AddHash(hash, value.previousInterpolateNormals ? 1 : 0);
    }
}
