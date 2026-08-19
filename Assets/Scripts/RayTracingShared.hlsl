#include "RayTracingCompilerWarnings.hlsl"

// Create a RenderTexture with enableRandomWrite flag and set it
// with cs.SetTexture
RWTexture2D<float4> Result;
RWTexture2D<float4> AccumulationResult;
// R: path-sample count, G: luminance mean, B: Welford M2, A: unused.
RWTexture2D<float4> AdaptiveSamplingState;
RWTexture2D<float4> Beauty;
RWTexture2D<float4> FeatureNormal;
RWTexture2D<float4> FeatureAlbedo;
RWTexture2D<float> FeatureDepth;
RWTexture2D<float> FeatureIdentity;
RWTexture2D<float> FeatureValidity;
RWStructuredBuffer<float4> RegressionResults;
RWStructuredBuffer<float4> _FocusQueryResult;

float4x4 _CameraToWorld;
float4x4 _CameraInverseProjection;
float2 _FrameJitterNdc;
int _UseTemporalJitter;

Texture2D<float4> _SkyboxTexture;
SamplerState sampler_SkyboxTexture;
StructuredBuffer<float> _EnvironmentConditionalCdf;
StructuredBuffer<float> _EnvironmentMarginalCdf;
Texture2DArray<float4> _MeshAlbedoTextures;
SamplerState sampler_MeshAlbedoTextures;
Texture2DArray<float4> _MeshMetallicRoughnessTextures;
SamplerState sampler_MeshMetallicRoughnessTextures;
Texture2DArray<float4> _MeshNormalTextures;
SamplerState sampler_MeshNormalTextures;
Texture2DArray<float4> _MeshParallaxTextures;
SamplerState sampler_MeshParallaxTextures;

float4 _SkyboxLight;
int _EnvironmentLightEnabled;
int _EnvironmentLightSampleCount;
float _EnvironmentHighlightThreshold;
float _EnvironmentHighlightSoftKnee;
float _EnvironmentHighlightIntensity;
int _EnvironmentCdfWidth;
int _EnvironmentCdfHeight;

int _NumberOfPasses;
float _SubpixelJitterScale;
int _ShadowQuality;
int _NumBounces;
int _DebugRenderMode;
int _MaxLightSamples;
int _LightSamplingStrategy;
int _LightSampleCount;
int _UseFrameAccumulation;
int _AccumulatedFrameCount;
int _UseAdaptiveSampling;
int _AdaptiveSamplingMinSamples;
float _AdaptiveSamplingRelativeError;
float _AdaptiveSamplingAbsoluteError;
int _AdaptiveSamplingMaxInterval;
float _ShadowRandomness;
float _LightFalloffScale;
float _ParallaxMaximumStrengthCosine;
float _FocalDistance;
float _ApertureRadius;
int _ApertureBladeCount;
float _ApertureBladeRotation;
float _AnamorphicRatio;
float2 _FocusQueryUv;
float _Exposure;
float _FireflyClamp;

int _WaterEnabled;
float3 _WaterCenter;
float2 _WaterSize;
float _WaterDepth;
float3 _WaterColor;
float _WaterSmoothness;
float _WaterOpacity;
float _WaterAbsorptionStrength;
float _WaterRefraction;
float _WaterWaveAmplitude;
float _WaterWaveScale;
float _WaterWaveSpeed;
float _WaterTime;
int _WaterMarchSteps;
int _WaterRefinementSteps;
// Used by the terrain-cell debug visualization, including debug variants where the terrain
// intersection path is not compiled.
int _TerrainCellResolution;

#if defined(TERRAIN_ENABLED)
float3 _TerrainPosition;
float3 _TerrainSize;
int _TerrainHeightmapResolution;
int _TerrainMarchSteps;
int _TerrainRefinementSteps;
StructuredBuffer<float> _TerrainHeights;
Texture2D<float4> _TerrainAlphamap;
SamplerState sampler_TerrainAlphamap;
Texture2D<float4> _TerrainLayer0;
Texture2D<float4> _TerrainLayer1;
Texture2D<float4> _TerrainLayer2;
Texture2D<float4> _TerrainLayer3;
Texture2D<float4> _TerrainNormal0;
Texture2D<float4> _TerrainNormal1;
Texture2D<float4> _TerrainNormal2;
Texture2D<float4> _TerrainNormal3;
Texture2D<float4> _TerrainMask0;
Texture2D<float4> _TerrainMask1;
Texture2D<float4> _TerrainMask2;
Texture2D<float4> _TerrainMask3;
SamplerState sampler_TerrainLayer0;
float2 _TerrainLayer0Tiling;
float2 _TerrainLayer1Tiling;
float2 _TerrainLayer2Tiling;
float2 _TerrainLayer3Tiling;
float4 _TerrainLayerProperties0;
float4 _TerrainLayerProperties1;
float4 _TerrainLayerProperties2;
float4 _TerrainLayerProperties3;
struct TerrainCell { float minHeight; float maxHeight; };
StructuredBuffer<TerrainCell> _TerrainCells;
#endif

#if defined(FOG_ENABLED)
float3 _FogBoundsMin;
float3 _FogBoundsMax;
float _FogDensity;
float3 _FogScatteringAlbedo;
float _FogInScatteringIntensity;
int _FogMultipleScattering;
#endif

uint _Seed;
uint _SampleOffset;

int _NumSpheres;
int _NumLights;
int _NumTriangles;
int _NumMeshes;
int _NumTopLevelBvhNodes;
int _NumShadowBvhNodes;

// 1 when at least one shadow-casting blocker (regular sphere or mesh triangle) is transparent.
// When 0, GetShadowTransmittance() takes a cheaper pure-occlusion path that returns black on
// the first opaque blocker and never does nearest-transparent-blocker bookkeeping.
int _HasTransparentShadowBlockers;
int _CausticsEnabled;

int _CausticPhotonCapacity;
int _CausticPhotonAttemptCount;
int _CausticMaxBounces;
uint _CausticSeed;
uint _CausticFrameIndex;
float _CausticGatherRadius;
float _CausticIntensity;
float3 _CausticGridMin;
float _CausticGridCellSize;
int3 _CausticGridDimensions;
int _CausticGridCellCount;
int _NumCausticTargetPairs;

static const float PI = 3.141593f;
// Keep this finite sentinel well below the backend's float-literal limit. It is still vastly
// farther than any supported scene or ray segment, including terrain and caustic probes.
static const float RayMaxDistance = 1.0e20f;

static const int DebugFinalColor = 0;
static const int DebugNormals = 1;
static const int DebugAlbedo = 2;
static const int DebugEmission = 3;
static const int DebugDirectLight = 4;
static const int DebugThroughput = 5;
static const int DebugBounceCount = 6;
static const int DebugHitDistance = 7;
static const int DebugAccelerationStructures = 8;
static const int DebugGlassScatter = 9;
static const int DebugCaustics = 10;
static const int DebugRawBeauty = 11;
static const int DebugFeatureNormal = 12;
static const int DebugFeatureAlbedo = 13;
static const int DebugFeatureDepth = 14;
static const int DebugFeatureIdentity = 15;
static const int DebugFeatureValidity = 16;
static const int DebugTerrainCells = 30;

static const int MaterialDiffuse = 0;
static const int MaterialMetal = 1;
static const int MaterialGlass = 2;
static const int MaterialEmissive = 3;
static const int MaterialWater = 4;

static const int MediumTypeAir = 0;
static const int MediumTypeSphere = 1;
static const int MediumTypeMesh = 2;
static const int MediumTypeWater = 3;
static const int MediumStackCapacity = 8;
static const int MaxTransparentShadowBoundaries = 32;
static const int MediumTransitionNone = 0;
static const int MediumTransitionEnter = 1;
static const int MediumTransitionExit = 2;
static const int MediumStackStatusOverflow = 1;
static const int MediumStackStatusUnmatchedExit = 2;

static const int TopLevelObjectTypeInternal = -1;
static const int TopLevelObjectTypeSphere = 0;
static const int TopLevelObjectTypeLight = 1;
static const int TopLevelObjectTypeMesh = 2;
static const int LightTypeSphere = 0;
static const int LightTypeTriangle = 1;
static const int LightTypeDirectional = 2;
static const int LightTypeSunTriangle = 3;
static const int LightTypeMesh = 4;

static const int LightSamplingAllLights = 0;
static const int LightSamplingUniformRandom = 1;
static const int LightSamplingImportance = 2;

// Max lights the importance-sampling weight cache can hold. Lights beyond this are ignored
// by importance sampling. GameManager warns if the scene exceeds this count.
static const int MaxImportanceLights = 128;

static const float MinDirectLightThroughput = 0.01f;
static const int BvhStackSize = 32;
static const float WaterHitEpsilon = 0.001f;
static const float GlassAbsorptionColorFloor = 0.001f;
static const float GlassNeutralAbsorption = 0.08f;
static const float ThinTransparentSurfaceDistance = 0.25f;

uint Hash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352d;
    value ^= value >> 15;
    value *= 0x846ca68b;
    value ^= value >> 16;
    return value;
}

float CausticSequenceSample(uint photonIndex, uint dimension)
{
    uint scramble = Hash(_CausticSeed ^ (_CausticFrameIndex * 2246822519u) ^ (dimension * 3266489917u));
    uint permutation = Hash(dimension * 668265263u) | 1u;
    uint bits = reversebits(photonIndex * permutation + scramble);
    return min((bits + 0.5f) * 2.3283064365386963e-10f, 0.99999994f);
}

float CausticDecorrelatedSample(uint photonIndex, uint dimension)
{
    // Independent hashes avoid the visible 2D lattices produced when two affine bit-reversal
    // dimensions are used directly as coordinates on an area light or refractor.
    uint bits = Hash(
        _CausticSeed
        ^ (_CausticFrameIndex * 2246822519u)
        ^ (photonIndex * 747796405u)
        ^ (dimension * 2891336453u));
    return min((bits + 0.5f) * 2.3283064365386963e-10f, 0.99999994f);
}

uint CreateRngState(uint2 pixel, uint sampleIndex)
{
    uint state = _Seed;
    state ^= pixel.x * 1973u;
    state ^= pixel.y * 9277u;
    state ^= sampleIndex * 26699u;
    return Hash(state);
}

float rand(inout uint rngState)
{
    rngState = Hash(rngState);
    return (rngState & 0x00ffffff) / 16777216.0f;
}

// =======================================================
// Sphere
struct Sphere
{
    float3 position;
    float3 color;
    float3 emission;
    float radius;
    float smoothness;
    float opacity;
    float refraction;
    float specular;
    float transmission;
    int materialType;
    int textureIndex;
    int normalTextureIndex;
    int parallaxTextureIndex;
    float2 textureUvScale;
    float parallaxStrength;
    float minimumParallaxStrength;
};

StructuredBuffer<Sphere> _Spheres;

struct Light
{
    float3 position;
    float3 emission;
    float3 u;
    float radius;
    float3 v;
    float area;
    float3 normal;
    int type;
    int triangleStart;
    int triangleCount;
    float totalArea;
    float padding;
};

StructuredBuffer<Light> _Lights;
StructuredBuffer<float> _MeshLightTriangleCdf;

struct MeshTriangle
{
    float3 vertex0;
    float3 vertex1;
    float3 vertex2;
    float3 normal;
    float3 normal0;
    float3 normal1;
    float3 normal2;
    float4 tangent0;
    float4 tangent1;
    float4 tangent2;
    float3 color;
    float smoothness;
    float metallic;
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float opacity;
    float3 emission;
    float refraction;
    float specular;
    float transmission;
    int materialType;
    int meshIndex;
    int textureIndex;
    int metallicRoughnessTextureIndex;
    int normalTextureIndex;
    float normalStrength;
    int parallaxTextureIndex;
    float2 textureUvScale;
    float textureUvRotation;
    float parallaxStrength;
    float minimumParallaxStrength;
    int interpolateNormals;
    int lightIndex;
};

StructuredBuffer<MeshTriangle> _Triangles;

struct MeshInfo
{
    float3 boundsMin;
    int rootNodeIndex;
    float3 boundsMax;
    int triangleStart;
    int triangleCount;
    int meshIndex;
    int isLight;
    int padding1;
};

struct BvhNode
{
    float3 boundsMin;
    int leftChildIndex;
    float3 boundsMax;
    int rightChildIndex;
    int triangleStart;
    int triangleCount;
    int padding0;
    int padding1;
};

StructuredBuffer<MeshInfo> _Meshes;
StructuredBuffer<BvhNode> _BvhNodes;

struct TopLevelBvhNode
{
    float3 boundsMin;
    int leftChildIndex;
    float3 boundsMax;
    int rightChildIndex;
    int objectType;
    int objectIndex;
    int padding0;
    int padding1;
};

StructuredBuffer<TopLevelBvhNode> _TopLevelBvhNodes;
StructuredBuffer<TopLevelBvhNode> _ShadowBvhNodes;

struct CausticPhoton
{
    float3 position;
    float3 incomingDirection;
    float3 power;
};

RWStructuredBuffer<CausticPhoton> _CausticPhotons;
RWStructuredBuffer<uint> _CausticPhotonMetadata;
RWStructuredBuffer<int> _CausticGridCellHeads;
RWStructuredBuffer<int> _CausticPhotonNext;

#if defined(CAUSTIC_PHOTON_TRACE)
struct CausticTargetPair
{
    int lightIndex;
    int refractorType;
    int refractorIndex;
    int triangleStart;
    int triangleCount;
    float cumulativeProbability;
    float selectionProbability;
    float padding;
};

struct CausticTargetTriangle
{
    int triangleIndex;
    float cumulativeProbability;
    float selectionProbability;
};

StructuredBuffer<CausticTargetPair> _CausticTargetPairs;
StructuredBuffer<CausticTargetTriangle> _CausticTargetTriangles;
#endif

// =======================================================
// Ray
struct Ray
{
    float3 origin;
    float3 direction;
};

Ray CreateRay(float3 origin, float3 direction)
{
    Ray ray;
    ray.origin = origin;
    ray.direction = direction;
    return ray;
}

struct ScatterResult
{
    Ray ray;
    float3 attenuation;
    int bouncesConsumed;
    int mediumTransition;
    float materialPdf;
};

struct BrdfSample
{
    float3 direction;
    float3 weight;
    float pdf;
};

ScatterResult CreateScatterResult(Ray ray, float3 attenuation, int bouncesConsumed, int mediumTransition)
{
    ScatterResult result;
    result.ray = ray;
    result.attenuation = attenuation;
    result.bouncesConsumed = max(1, bouncesConsumed);
    result.mediumTransition = mediumTransition;
    result.materialPdf = 0.0f;
    return result;
}

Ray CreateCameraRay(float2 uv)
{
    // Transform the camera origin to world space
    float3 origin = mul(_CameraToWorld, float4(0.0f, 0.0f, 0.0f, 1.0f)).xyz;

    // Invert the perspective projection of the view-space position
    float3 direction = mul(_CameraInverseProjection, float4(uv, 0.0f, 1.0f)).xyz;

    // Transform the direction from camera to world space and normalize
    direction = mul(_CameraToWorld, float4(direction, 0.0f)).xyz;
    direction = normalize(direction);

    return CreateRay(origin, direction);
}

float2 SampleConcentricDisk(inout uint rngState)
{
    float2 offset = float2(rand(rngState), rand(rngState)) * 2.0f - 1.0f;
    if (offset.x == 0.0f && offset.y == 0.0f)
    {
        return float2(0.0f, 0.0f);
    }

    float radius;
    float angle;
    if (abs(offset.x) > abs(offset.y))
    {
        radius = offset.x;
        angle = (PI * 0.25f) * (offset.y / offset.x);
    }
    else
    {
        radius = offset.y;
        angle = (PI * 0.5f) - (PI * 0.25f) * (offset.x / offset.y);
    }

    return radius * float2(cos(angle), sin(angle));
}

float2 SampleAperture(inout uint rngState)
{
    float2 samplePosition;
    int bladeCount = max(0, _ApertureBladeCount);
    if (bladeCount < 3)
    {
        samplePosition = SampleConcentricDisk(rngState);
    }
    else
    {
        int blade = min(bladeCount - 1, (int)(rand(rngState) * bladeCount));
        float angle0 = _ApertureBladeRotation + (2.0f * PI * blade) / bladeCount;
        float angle1 = _ApertureBladeRotation + (2.0f * PI * (blade + 1)) / bladeCount;
        float2 vertex0 = float2(cos(angle0), sin(angle0));
        float2 vertex1 = float2(cos(angle1), sin(angle1));
        float2 barycentric = float2(rand(rngState), rand(rngState));
        if (barycentric.x + barycentric.y > 1.0f)
        {
            barycentric = 1.0f - barycentric;
        }
        samplePosition = vertex0 * barycentric.x + vertex1 * barycentric.y;
    }

    float anamorphicScale = sqrt(max(0.01f, _AnamorphicRatio));
    return samplePosition * float2(anamorphicScale, 1.0f / anamorphicScale);
}
// =======================================================

// =======================================================
// RayHit
struct RayHit
{
    float3 position;
    float3 obj_position;
    float3 normal;
    float3 geometricNormal;
    float3 emission;
    float3 color;
    float obj_radius;
    float distance;
    float smoothness;
    float metallic;
    float opacity;
    float refraction;
    float specular;
    float transmission;
    int materialType;
    int meshIndex;
    int objectIndex;
    float2 uv;
    int textureIndex;
    int metallicRoughnessTextureIndex;
    int normalTextureIndex;
    int lightIndex;
    int triangleIndex;
};

// Medium identity is separate from material type: two glass objects can have the same material
// properties but still represent different boundaries in a nested path.
struct MediumIdentity
{
    int type;
    int objectIndex;
    float refractionIndex;
    float opacity;
    float3 absorptionColor;
};

struct MediumStack
{
    MediumIdentity entry0;
    MediumIdentity entry1;
    MediumIdentity entry2;
    MediumIdentity entry3;
    MediumIdentity entry4;
    MediumIdentity entry5;
    MediumIdentity entry6;
    MediumIdentity entry7;
    int count;
    int status;
};

MediumIdentity CreateAirMedium()
{
    MediumIdentity medium;
    medium.type = MediumTypeAir;
    medium.objectIndex = -1;
    medium.refractionIndex = 1.0f;
    medium.opacity = 0.0f;
    medium.absorptionColor = float3(1.0f, 1.0f, 1.0f);
    return medium;
}

MediumIdentity CreateHitMedium(RayHit hit)
{
    MediumIdentity medium;
    medium.type = hit.materialType == MaterialWater
        ? MediumTypeWater
        : (hit.obj_radius > 0.0f ? MediumTypeSphere : MediumTypeMesh);
    medium.objectIndex = medium.type == MediumTypeSphere ? hit.objectIndex : hit.meshIndex;
    medium.refractionIndex = max(1.0f, hit.refraction);
    medium.opacity = saturate(hit.opacity);
    medium.absorptionColor = saturate(hit.color);
    return medium;
}

MediumIdentity CreateWaterMedium()
{
    MediumIdentity medium;
    medium.type = MediumTypeWater;
    medium.objectIndex = -1;
    medium.refractionIndex = max(1.0f, _WaterRefraction);
    medium.opacity = saturate(_WaterOpacity);
    medium.absorptionColor = saturate(_WaterColor);
    return medium;
}

bool IsSameMedium(MediumIdentity left, MediumIdentity right)
{
    return left.type == right.type && left.objectIndex == right.objectIndex;
}

bool IsPointInWater(float3 position);
void PushMedium(inout MediumStack stack, MediumIdentity medium);

bool MediumStackContains(in MediumStack stack, MediumIdentity medium)
{
    if (stack.count > 7 && IsSameMedium(stack.entry7, medium)) return true;
    if (stack.count > 6 && IsSameMedium(stack.entry6, medium)) return true;
    if (stack.count > 5 && IsSameMedium(stack.entry5, medium)) return true;
    if (stack.count > 4 && IsSameMedium(stack.entry4, medium)) return true;
    if (stack.count > 3 && IsSameMedium(stack.entry3, medium)) return true;
    if (stack.count > 2 && IsSameMedium(stack.entry2, medium)) return true;
    if (stack.count > 1 && IsSameMedium(stack.entry1, medium)) return true;
    return IsSameMedium(stack.entry0, medium);
}

MediumIdentity CreateSphereMedium(Sphere sphere, int sphereIndex)
{
    MediumIdentity medium;
    medium.type = MediumTypeSphere;
    medium.objectIndex = sphereIndex;
    medium.refractionIndex = max(1.0f, sphere.refraction);
    medium.opacity = saturate(sphere.opacity);
    medium.absorptionColor = saturate(sphere.color);
    return medium;
}

MediumStack CreateMediumStack(float3 rayOrigin)
{
    MediumStack stack;
    MediumIdentity air = CreateAirMedium();
    stack.count = 1;
    stack.status = 0;
    stack.entry0 = air;
    stack.entry1 = air;
    stack.entry2 = air;
    stack.entry3 = air;
    stack.entry4 = air;
    stack.entry5 = air;
    stack.entry6 = air;
    stack.entry7 = air;

    if (IsPointInWater(rayOrigin))
    {
        stack.entry1 = CreateWaterMedium();
        stack.count = 2;
    }

    // Add containing glass spheres from largest to smallest so the innermost medium is active.
    [loop]
    while (stack.count < MediumStackCapacity)
    {
        int containingSphereIndex = -1;
        float containingSphereRadius = -1.0f;
        int sphereIndex;
        [loop]
        for (sphereIndex = 0; sphereIndex < _NumSpheres; sphereIndex++)
        {
            Sphere sphere = _Spheres[sphereIndex];
            bool isGlass = sphere.materialType == MaterialGlass || sphere.opacity < 1.0f;
            bool containsOrigin = dot(rayOrigin - sphere.position, rayOrigin - sphere.position) < sphere.radius * sphere.radius;
            MediumIdentity sphereMedium = CreateSphereMedium(sphere, sphereIndex);
            if (isGlass && containsOrigin && !MediumStackContains(stack, sphereMedium) && sphere.radius > containingSphereRadius)
            {
                containingSphereIndex = sphereIndex;
                containingSphereRadius = sphere.radius;
            }
        }

        if (containingSphereIndex < 0)
        {
            break;
        }

        PushMedium(stack, CreateSphereMedium(_Spheres[containingSphereIndex], containingSphereIndex));
    }

    return stack;
}

MediumStack CreateShadowMediumStack(float3 rayOrigin)
{
    MediumStack stack;
    MediumIdentity air = CreateAirMedium();
    stack.count = 1;
    stack.status = 0;
    stack.entry0 = air;
    stack.entry1 = air;
    stack.entry2 = air;
    stack.entry3 = air;
    stack.entry4 = air;
    stack.entry5 = air;
    stack.entry6 = air;
    stack.entry7 = air;

    // Water remains handled by EstimateWaterDistanceAlongSegment(). Initialize only containing
    // spheres here so transparent shadow traversal does not apply water absorption twice.
    [loop]
    while (stack.count < MediumStackCapacity)
    {
        int containingSphereIndex = -1;
        float containingSphereRadius = -1.0f;
        int sphereIndex;
        [loop]
        for (sphereIndex = 0; sphereIndex < _NumSpheres; sphereIndex++)
        {
            Sphere sphere = _Spheres[sphereIndex];
            bool isGlass = sphere.materialType == MaterialGlass || sphere.opacity < 1.0f;
            bool containsOrigin = dot(rayOrigin - sphere.position, rayOrigin - sphere.position) < sphere.radius * sphere.radius;
            MediumIdentity sphereMedium = CreateSphereMedium(sphere, sphereIndex);
            if (isGlass && containsOrigin && !MediumStackContains(stack, sphereMedium) && sphere.radius > containingSphereRadius)
            {
                containingSphereIndex = sphereIndex;
                containingSphereRadius = sphere.radius;
            }
        }

        if (containingSphereIndex < 0)
        {
            break;
        }

        PushMedium(stack, CreateSphereMedium(_Spheres[containingSphereIndex], containingSphereIndex));
    }

    return stack;
}

MediumIdentity GetCurrentMedium(in MediumStack stack)
{
    int index = max(0, stack.count - 1);
    if (index == 7) return stack.entry7;
    if (index == 6) return stack.entry6;
    if (index == 5) return stack.entry5;
    if (index == 4) return stack.entry4;
    if (index == 3) return stack.entry3;
    if (index == 2) return stack.entry2;
    if (index == 1) return stack.entry1;
    return stack.entry0;
}

MediumIdentity GetParentMedium(in MediumStack stack)
{
    int index = max(0, stack.count - 2);
    if (index == 6) return stack.entry6;
    if (index == 5) return stack.entry5;
    if (index == 4) return stack.entry4;
    if (index == 3) return stack.entry3;
    if (index == 2) return stack.entry2;
    if (index == 1) return stack.entry1;
    return stack.entry0;
}

void PushMedium(inout MediumStack stack, MediumIdentity medium)
{
    if (stack.count >= MediumStackCapacity)
    {
        stack.status |= MediumStackStatusOverflow;
        return;
    }

    if (stack.count == 7) stack.entry7 = medium;
    else if (stack.count == 6) stack.entry6 = medium;
    else if (stack.count == 5) stack.entry5 = medium;
    else if (stack.count == 4) stack.entry4 = medium;
    else if (stack.count == 3) stack.entry3 = medium;
    else if (stack.count == 2) stack.entry2 = medium;
    else stack.entry1 = medium;
    stack.count++;
}

void PopMatchingMedium(inout MediumStack stack, MediumIdentity medium)
{
    if (stack.count <= 1 || !IsSameMedium(GetCurrentMedium(stack), medium))
    {
        stack.status |= MediumStackStatusUnmatchedExit;
        return;
    }

    stack.count--;
}

MediumIdentity GetMediumStackEntry(in MediumStack stack, int index)
{
    if (index == 7) return stack.entry7;
    if (index == 6) return stack.entry6;
    if (index == 5) return stack.entry5;
    if (index == 4) return stack.entry4;
    if (index == 3) return stack.entry3;
    if (index == 2) return stack.entry2;
    if (index == 1) return stack.entry1;
    return stack.entry0;
}

void SetMediumStackEntry(inout MediumStack stack, int index, MediumIdentity medium)
{
    if (index == 7) stack.entry7 = medium;
    else if (index == 6) stack.entry6 = medium;
    else if (index == 5) stack.entry5 = medium;
    else if (index == 4) stack.entry4 = medium;
    else if (index == 3) stack.entry3 = medium;
    else if (index == 2) stack.entry2 = medium;
    else if (index == 1) stack.entry1 = medium;
    else stack.entry0 = medium;
}

void RemoveMatchingMedium(inout MediumStack stack, MediumIdentity medium)
{
    int matchingIndex = -1;
    int index;
    [unroll]
    for (index = 1; index < MediumStackCapacity; index++)
    {
        if (index < stack.count && IsSameMedium(GetMediumStackEntry(stack, index), medium))
        {
            matchingIndex = index;
        }
    }

    if (matchingIndex < 0)
    {
        stack.status |= MediumStackStatusUnmatchedExit;
        return;
    }

    [unroll]
    for (index = 1; index < MediumStackCapacity - 1; index++)
    {
        if (index >= matchingIndex && index + 1 < stack.count)
        {
            SetMediumStackEntry(stack, index, GetMediumStackEntry(stack, index + 1));
        }
    }

    stack.count--;
}

void ApplyMediumTransition(inout MediumStack stack, RayHit hit, int transition)
{
    if (transition == MediumTransitionEnter)
    {
        MediumIdentity boundaryMedium = CreateHitMedium(hit);
        if (IsSameMedium(GetCurrentMedium(stack), boundaryMedium))
        {
            PopMatchingMedium(stack, boundaryMedium);
        }
        else
        {
            PushMedium(stack, boundaryMedium);
        }
    }
    else if (transition == MediumTransitionExit)
    {
        RemoveMatchingMedium(stack, CreateHitMedium(hit));
    }
}

float2 GetMediumTransitionIndices(MediumIdentity currentMedium, MediumIdentity parentMedium, RayHit hit, bool entering)
{
    MediumIdentity boundaryMedium = CreateHitMedium(hit);
    return entering
        ? float2(currentMedium.refractionIndex, boundaryMedium.refractionIndex)
        : float2(currentMedium.refractionIndex, parentMedium.refractionIndex);
}

float2 GetBoundaryTransitionIndices(in MediumStack stack, RayHit hit, out bool entering)
{
    MediumIdentity currentMedium = GetCurrentMedium(stack);
    MediumIdentity boundaryMedium = CreateHitMedium(hit);
    entering = !MediumStackContains(stack, boundaryMedium);
    MediumIdentity targetMedium = currentMedium;
    if (IsSameMedium(currentMedium, boundaryMedium))
    {
        targetMedium = GetParentMedium(stack);
    }
    return GetMediumTransitionIndices(currentMedium, targetMedium, hit, entering);
}

RayHit CreateRayHit()
{
    RayHit hit;
    hit.position = float3(0.0f, 0.0f, 0.0f);
    hit.obj_position = float3(0.0f, 0.0f, 0.0f);
    hit.normal = float3(0.0f, 0.0f, 0.0f);
    hit.geometricNormal = float3(0.0f, 0.0f, 0.0f);
    hit.emission = float3(0.0f, 0.0f, 0.0f);
    hit.color = float3(0.0f, 0.0f, 0.0f);
    hit.obj_radius = 0.0f;
    hit.distance = RayMaxDistance;
    hit.smoothness = 0.0f;
    hit.metallic = 0.0f;
    hit.opacity = 1.0f;
    hit.specular = 0.0f;
    hit.transmission = 1.0f;
    hit.refraction = 1.0f;
    hit.materialType = MaterialDiffuse;
    hit.meshIndex = -1;
    hit.objectIndex = -1;
    hit.uv = float2(0.0f, 0.0f);
    hit.textureIndex = -1;
    hit.metallicRoughnessTextureIndex = -1;
    hit.normalTextureIndex = -1;
    hit.lightIndex = -1;
    hit.triangleIndex = -1;

    return hit;
}
// =======================================================

float GetWaterWaveHeight(float2 worldXZ)
{
    float amplitude = max(0.0f, _WaterWaveAmplitude);
    if (amplitude <= 0.0f)
    {
        return _WaterCenter.y;
    }

    float2 p = (worldXZ - _WaterCenter.xz) * max(0.001f, _WaterWaveScale);
    float t = _WaterTime * _WaterWaveSpeed;

    float h = 0.0f;
    h += sin(dot(p, normalize(float2(0.86f, 0.51f))) * 1.25f + t * 1.10f) * 0.46f;
    h += sin(dot(p, normalize(float2(-0.34f, 0.94f))) * 1.95f + t * 1.55f + 1.7f) * 0.27f;
    h += sin(dot(p, normalize(float2(0.12f, -0.99f))) * 2.80f + t * 2.05f + 3.1f) * 0.17f;
    h += sin(dot(p, normalize(float2(-0.78f, -0.62f))) * 4.10f + t * 2.70f + 0.4f) * 0.10f;

    return _WaterCenter.y + h * amplitude;
}

float3 GetWaterNormal(float2 worldXZ)
{
    float sampleOffset = max(0.03f, 0.08f / max(0.001f, _WaterWaveScale));
    float heightLeft = GetWaterWaveHeight(worldXZ - float2(sampleOffset, 0.0f));
    float heightRight = GetWaterWaveHeight(worldXZ + float2(sampleOffset, 0.0f));
    float heightBack = GetWaterWaveHeight(worldXZ - float2(0.0f, sampleOffset));
    float heightForward = GetWaterWaveHeight(worldXZ + float2(0.0f, sampleOffset));

    float dHeightDx = (heightRight - heightLeft) / (2.0f * sampleOffset);
    float dHeightDz = (heightForward - heightBack) / (2.0f * sampleOffset);
    return normalize(float3(-dHeightDx, 1.0f, -dHeightDz));
}

bool IsInsideWaterXZ(float2 worldXZ)
{
    float2 halfSize = max(_WaterSize, float2(0.01f, 0.01f)) * 0.5f;
    float2 delta = abs(worldXZ - _WaterCenter.xz);
    return delta.x <= halfSize.x && delta.y <= halfSize.y;
}

bool IsPointInWater(float3 position)
{
    float bottom = _WaterCenter.y - max(0.01f, _WaterDepth);
    return _WaterEnabled != 0
        && IsInsideWaterXZ(position.xz)
        && position.y >= bottom
        && position.y <= GetWaterWaveHeight(position.xz);
}

float3 GetWaterAbsorptionTransmittance(float distanceInWater)
{
    float strength = max(0.0f, _WaterAbsorptionStrength);
    if (strength <= 0.0f || distanceInWater <= 0.0f)
    {
        return float3(1.0f, 1.0f, 1.0f);
    }

    float3 absorption = (1.0f - saturate(_WaterColor)) * strength;
    return exp(-absorption * distanceInWater);
}

void IntersectWater(Ray ray, inout RayHit bestHit);
#if defined(TERRAIN_ENABLED)
void IntersectTerrain(Ray ray, inout RayHit bestHit);
#endif
bool IntersectAabbInverse(float3 rayOrigin, float3 inverseDirection, float3 boundsMin, float3 boundsMax, float maxDistance, out float entryDistance);

float GetWaterDistanceAlongRay(Ray ray, float maxDistance)
{
    if (!IsPointInWater(ray.origin))
    {
        return 0.0f;
    }

    RayHit volumeExit = CreateRayHit();
    IntersectWater(ray, volumeExit);
    if (volumeExit.distance >= RayMaxDistance)
    {
        return 0.0f;
    }

    float segmentEndDistance = maxDistance < RayMaxDistance ? max(0.0f, maxDistance) : volumeExit.distance;
    return max(0.0f, min(segmentEndDistance, volumeExit.distance));
}

float EstimateWaterDistanceAlongSegment(float3 startPosition, float3 endPosition)
{
    if (_WaterEnabled == 0)
    {
        return 0.0f;
    }

    float3 segment = endPosition - startPosition;
    float segmentLength = length(segment);
    if (segmentLength <= 0.001f)
    {
        return 0.0f;
    }

    bool startUnderWater = IsPointInWater(startPosition);
    bool endUnderWater = IsPointInWater(endPosition);
    if (startUnderWater && endUnderWater)
    {
        return segmentLength;
    }

    if (!startUnderWater && !endUnderWater)
    {
        return 0.0f;
    }

    float low = 0.0f;
    float high = 1.0f;
    int i;
    [loop]
    for (i = 0; i < 6; i++)
    {
        float mid = (low + high) * 0.5f;
        bool midUnderWater = IsPointInWater(startPosition + segment * mid);
        if (midUnderWater == startUnderWater)
        {
            low = mid;
        }
        else
        {
            high = mid;
        }
    }

    return startUnderWater ? high * segmentLength : (1.0f - high) * segmentLength;
}

bool IntersectWaterSurfaceBounds(Ray ray, float maxDistance, out float entryDistance, out float exitDistance)
{
    float waveHeight = max(0.001f, _WaterWaveAmplitude + WaterHitEpsilon);
    float2 halfSize = max(_WaterSize, float2(0.01f, 0.01f)) * 0.5f;
    float3 boundsMin = float3(_WaterCenter.x - halfSize.x, _WaterCenter.y - waveHeight, _WaterCenter.z - halfSize.y);
    float3 boundsMax = float3(_WaterCenter.x + halfSize.x, _WaterCenter.y + waveHeight, _WaterCenter.z + halfSize.y);

    float3 inverseDirection;
    if (ray.direction.x == 0.0f) inverseDirection.x = RayMaxDistance;
    else inverseDirection.x = 1.0f / ray.direction.x;
    if (ray.direction.y == 0.0f) inverseDirection.y = RayMaxDistance;
    else inverseDirection.y = 1.0f / ray.direction.y;
    if (ray.direction.z == 0.0f) inverseDirection.z = RayMaxDistance;
    else inverseDirection.z = 1.0f / ray.direction.z;
    float3 t0 = (boundsMin - ray.origin) * inverseDirection;
    float3 t1 = (boundsMax - ray.origin) * inverseDirection;
    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);
    float tMin = max(max(tMin3.x, tMin3.y), tMin3.z);
    float tMax = min(min(tMax3.x, tMax3.y), tMax3.z);

    entryDistance = max(0.001f, tMin);
    exitDistance = tMax;
    return tMax >= entryDistance && entryDistance < maxDistance;
}

void SetWaterHit(Ray ray, float hitT, float3 outwardNormal, inout RayHit bestHit)
{
    if (hitT <= 0.001f || hitT >= bestHit.distance)
    {
        return;
    }

    bestHit.position = ray.origin + hitT * ray.direction;
    bestHit.obj_position = _WaterCenter;
    bestHit.normal = dot(outwardNormal, ray.direction) > 0.0f ? -outwardNormal : outwardNormal;
    bestHit.geometricNormal = bestHit.normal;
    bestHit.emission = float3(0.0f, 0.0f, 0.0f);
    bestHit.color = _WaterColor;
    bestHit.obj_radius = 0.0f;
    bestHit.distance = hitT;
    bestHit.smoothness = _WaterSmoothness;
    bestHit.metallic = 0.0f;
    bestHit.opacity = saturate(_WaterOpacity);
    bestHit.specular = 0.0f;
    bestHit.transmission = 1.0f;
    bestHit.refraction = max(1.0f, _WaterRefraction);
    bestHit.materialType = MaterialWater;
    bestHit.meshIndex = -1;
    bestHit.objectIndex = -1;
    bestHit.uv = bestHit.position.xz - _WaterCenter.xz;
    bestHit.textureIndex = -1;
    bestHit.metallicRoughnessTextureIndex = -1;
    bestHit.normalTextureIndex = -1;
}

void IntersectWaterFlatBoundaries(Ray ray, inout RayHit bestHit)
{
    float2 halfSize = max(_WaterSize, float2(0.01f, 0.01f)) * 0.5f;
    float2 boundsMin = _WaterCenter.xz - halfSize;
    float2 boundsMax = _WaterCenter.xz + halfSize;
    float bottom = _WaterCenter.y - max(0.01f, _WaterDepth);
    float t;
    float3 position;

    if (abs(ray.direction.y) > 0.000001f)
    {
        t = (bottom - ray.origin.y) / ray.direction.y;
        position = ray.origin + ray.direction * t;
        if (position.x >= boundsMin.x && position.x <= boundsMax.x
            && position.z >= boundsMin.y && position.z <= boundsMax.y)
        {
            SetWaterHit(ray, t, float3(0.0f, -1.0f, 0.0f), bestHit);
        }
    }

    if (abs(ray.direction.x) > 0.000001f)
    {
        t = (boundsMin.x - ray.origin.x) / ray.direction.x;
        position = ray.origin + ray.direction * t;
        if (position.z >= boundsMin.y && position.z <= boundsMax.y
            && position.y >= bottom && position.y <= GetWaterWaveHeight(position.xz))
        {
            SetWaterHit(ray, t, float3(-1.0f, 0.0f, 0.0f), bestHit);
        }

        t = (boundsMax.x - ray.origin.x) / ray.direction.x;
        position = ray.origin + ray.direction * t;
        if (position.z >= boundsMin.y && position.z <= boundsMax.y
            && position.y >= bottom && position.y <= GetWaterWaveHeight(position.xz))
        {
            SetWaterHit(ray, t, float3(1.0f, 0.0f, 0.0f), bestHit);
        }
    }

    if (abs(ray.direction.z) > 0.000001f)
    {
        t = (boundsMin.y - ray.origin.z) / ray.direction.z;
        position = ray.origin + ray.direction * t;
        if (position.x >= boundsMin.x && position.x <= boundsMax.x
            && position.y >= bottom && position.y <= GetWaterWaveHeight(position.xz))
        {
            SetWaterHit(ray, t, float3(0.0f, 0.0f, -1.0f), bestHit);
        }

        t = (boundsMax.y - ray.origin.z) / ray.direction.z;
        position = ray.origin + ray.direction * t;
        if (position.x >= boundsMin.x && position.x <= boundsMax.x
            && position.y >= bottom && position.y <= GetWaterWaveHeight(position.xz))
        {
            SetWaterHit(ray, t, float3(0.0f, 0.0f, 1.0f), bestHit);
        }
    }
}

void IntersectWaterTop(Ray ray, inout RayHit bestHit)
{
    float entryDistance;
    float exitDistance;
    if (!IntersectWaterSurfaceBounds(ray, bestHit.distance, entryDistance, exitDistance))
    {
        return;
    }

    exitDistance = min(exitDistance, bestHit.distance);
    int marchSteps = clamp(_WaterMarchSteps, 8, 64);
    float stepSize = max(0.001f, (exitDistance - entryDistance) / marchSteps);

    float previousT = entryDistance;
    float3 previousPosition = ray.origin + ray.direction * previousT;
    float previousSignedDistance = previousPosition.y - GetWaterWaveHeight(previousPosition.xz);

    if (abs(previousSignedDistance) <= WaterHitEpsilon && IsInsideWaterXZ(previousPosition.xz))
    {
        previousSignedDistance = dot(ray.direction, float3(0.0f, 1.0f, 0.0f)) > 0.0f ? -WaterHitEpsilon : WaterHitEpsilon;
    }

    float hitT = RayMaxDistance;
    int i;
    [loop]
    for (i = 1; i <= marchSteps; i++)
    {
        float currentT = min(exitDistance, entryDistance + stepSize * i);
        float3 currentPosition = ray.origin + ray.direction * currentT;
        float currentSignedDistance = currentPosition.y - GetWaterWaveHeight(currentPosition.xz);

        if (IsInsideWaterXZ(currentPosition.xz)
            && previousSignedDistance * currentSignedDistance <= 0.0f)
        {
            float lowT = previousT;
            float highT = currentT;
            int refinementSteps = clamp(_WaterRefinementSteps, 2, 8);
            int r;
            [loop]
            for (r = 0; r < refinementSteps; r++)
            {
                float midT = (lowT + highT) * 0.5f;
                float3 midPosition = ray.origin + ray.direction * midT;
                float midSignedDistance = midPosition.y - GetWaterWaveHeight(midPosition.xz);

                if (previousSignedDistance * midSignedDistance <= 0.0f)
                {
                    highT = midT;
                    currentSignedDistance = midSignedDistance;
                }
                else
                {
                    lowT = midT;
                    previousSignedDistance = midSignedDistance;
                }
            }

            hitT = highT;
            break;
        }

        previousT = currentT;
        previousSignedDistance = currentSignedDistance;
    }

    if (hitT >= bestHit.distance || hitT <= 0.001f)
    {
        return;
    }

    float3 hitPosition = ray.origin + hitT * ray.direction;
    SetWaterHit(ray, hitT, GetWaterNormal(hitPosition.xz), bestHit);
    bestHit.position.y = GetWaterWaveHeight(bestHit.position.xz);
}

void IntersectWater(Ray ray, inout RayHit bestHit)
{
    if (_WaterEnabled == 0)
    {
        return;
    }

    IntersectWaterFlatBoundaries(ray, bestHit);
    IntersectWaterTop(ray, bestHit);
}

#if defined(TERRAIN_ENABLED)
float GetTerrainHeight(float2 worldXZ)
{
    float2 uv = saturate((worldXZ - _TerrainPosition.xz) / max(_TerrainSize.xz, float2(0.001f, 0.001f)));
    int resolution = max(1, _TerrainHeightmapResolution);
    float2 samplePosition = uv * (resolution - 1);
    int2 sample0 = min(resolution - 1, (int2)floor(samplePosition));
    int2 sample1 = min(resolution - 1, sample0 + 1);
    float2 blend = frac(samplePosition);
    float height00 = _TerrainHeights[sample0.y * resolution + sample0.x];
    float height10 = _TerrainHeights[sample0.y * resolution + sample1.x];
    float height01 = _TerrainHeights[sample1.y * resolution + sample0.x];
    float height11 = _TerrainHeights[sample1.y * resolution + sample1.x];
    float normalizedHeight = lerp(lerp(height00, height10, blend.x), lerp(height01, height11, blend.x), blend.y);
    return _TerrainPosition.y + normalizedHeight * _TerrainSize.y;
}

float3 GetTerrainNormal(float2 worldXZ)
{
    float2 sampleOffset = _TerrainSize.xz / max(1.0f, (float)(_TerrainHeightmapResolution - 1));
    float heightX = GetTerrainHeight(worldXZ + float2(sampleOffset.x, 0.0f)) - GetTerrainHeight(worldXZ - float2(sampleOffset.x, 0.0f));
    float heightZ = GetTerrainHeight(worldXZ + float2(0.0f, sampleOffset.y)) - GetTerrainHeight(worldXZ - float2(0.0f, sampleOffset.y));
    return normalize(float3(-heightX / (2.0f * sampleOffset.x), 1.0f, -heightZ / (2.0f * sampleOffset.y)));
}

void GetTerrainWeights(float2 uv, out float4 weights)
{
    int2 alphamapSize;
    _TerrainAlphamap.GetDimensions(alphamapSize.x, alphamapSize.y);
    float2 alphamapPosition = saturate(uv) * (alphamapSize - 1);
    int2 alphamap0 = (int2)floor(alphamapPosition);
    int2 alphamap1 = min(alphamapSize - 1, alphamap0 + 1);
    float2 alphamapBlend = frac(alphamapPosition);
    weights = lerp(
        lerp(_TerrainAlphamap.Load(int3(alphamap0.x, alphamap0.y, 0)), _TerrainAlphamap.Load(int3(alphamap1.x, alphamap0.y, 0)), alphamapBlend.x),
        lerp(_TerrainAlphamap.Load(int3(alphamap0.x, alphamap1.y, 0)), _TerrainAlphamap.Load(int3(alphamap1.x, alphamap1.y, 0)), alphamapBlend.x),
        alphamapBlend.y);
    float weightSum = max(0.0001f, dot(weights, 1.0f));
    weights /= weightSum;
}

void GetTerrainTextureSampleCoordinates(float2 uv, int2 size, out int2 p0, out int2 p1, out float2 blend)
{
    float2 position = frac(uv) * size - 0.5f;
    p0 = (int2)floor(position);
    p1 = p0 + 1;
    p0 = (p0 % size + size) % size;
    p1 = (p1 % size + size) % size;
    blend = frac(position);
}

#define SAMPLE_TERRAIN_TEXTURE(texture, uv) SampleTerrainTexture_##texture(uv)
#define DEFINE_TERRAIN_TEXTURE_SAMPLER(texture) \
float4 SampleTerrainTexture_##texture(float2 uv) \
{ \
    int2 size; texture.GetDimensions(size.x, size.y); \
    int2 p0; int2 p1; float2 blend; \
    GetTerrainTextureSampleCoordinates(uv, size, p0, p1, blend); \
    return lerp(lerp(texture.Load(int3(p0.x, p0.y, 0)), texture.Load(int3(p1.x, p0.y, 0)), blend.x), \
        lerp(texture.Load(int3(p0.x, p1.y, 0)), texture.Load(int3(p1.x, p1.y, 0)), blend.x), blend.y); \
}

DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainLayer0)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainLayer1)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainLayer2)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainLayer3)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainNormal0)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainNormal1)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainNormal2)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainNormal3)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainMask0)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainMask1)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainMask2)
DEFINE_TERRAIN_TEXTURE_SAMPLER(_TerrainMask3)

float3 GetTerrainAlbedo(float2 uv)
{
    float4 weights;
    GetTerrainWeights(uv, weights);
    // Use explicit wrapping and bilinear filtering so all backends match the terrain preview
    // without depending on an imported texture's sampler state.
    float2 layerUv0 = frac(uv * _TerrainLayer0Tiling);
    float2 layerUv1 = frac(uv * _TerrainLayer1Tiling);
    float2 layerUv2 = frac(uv * _TerrainLayer2Tiling);
    float2 layerUv3 = frac(uv * _TerrainLayer3Tiling);
    float3 color0 = SAMPLE_TERRAIN_TEXTURE(_TerrainLayer0, layerUv0).rgb;
    float3 color1 = SAMPLE_TERRAIN_TEXTURE(_TerrainLayer1, layerUv1).rgb;
    float3 color2 = SAMPLE_TERRAIN_TEXTURE(_TerrainLayer2, layerUv2).rgb;
    float3 color3 = SAMPLE_TERRAIN_TEXTURE(_TerrainLayer3, layerUv3).rgb;
    return color0 * weights.x + color1 * weights.y + color2 * weights.z + color3 * weights.w;
}

float3 GetTerrainNormal(float2 uv, float3 geometricNormal)
{
    float4 weights; GetTerrainWeights(uv, weights);
    float3 n0 = SAMPLE_TERRAIN_TEXTURE(_TerrainNormal0, uv * _TerrainLayer0Tiling).xyz * 2.0f - 1.0f;
    float3 n1 = SAMPLE_TERRAIN_TEXTURE(_TerrainNormal1, uv * _TerrainLayer1Tiling).xyz * 2.0f - 1.0f;
    float3 n2 = SAMPLE_TERRAIN_TEXTURE(_TerrainNormal2, uv * _TerrainLayer2Tiling).xyz * 2.0f - 1.0f;
    float3 n3 = SAMPLE_TERRAIN_TEXTURE(_TerrainNormal3, uv * _TerrainLayer3Tiling).xyz * 2.0f - 1.0f;
    n0.xy *= _TerrainLayerProperties0.z;
    n1.xy *= _TerrainLayerProperties1.z;
    n2.xy *= _TerrainLayerProperties2.z;
    n3.xy *= _TerrainLayerProperties3.z;
    float3 tangent = float3(1.0f, 0.0f, 0.0f);
    tangent = normalize(tangent - geometricNormal * dot(tangent, geometricNormal));
    if (dot(tangent, tangent) < 1e-6f) tangent = float3(0.0f, 0.0f, 1.0f);
    float3 bitangent = normalize(cross(geometricNormal, tangent));
    float3 tangentNormal = normalize(n0 * weights.x + n1 * weights.y + n2 * weights.z + n3 * weights.w);
    float3 mapped = normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + geometricNormal * tangentNormal.z);
    return dot(mapped, geometricNormal) < 0.0f ? -mapped : mapped;
}

float4 GetTerrainMask(float2 uv)
{
    float4 weights; GetTerrainWeights(uv, weights);
    return SAMPLE_TERRAIN_TEXTURE(_TerrainMask0, uv * _TerrainLayer0Tiling) * weights.x
        + SAMPLE_TERRAIN_TEXTURE(_TerrainMask1, uv * _TerrainLayer1Tiling) * weights.y
        + SAMPLE_TERRAIN_TEXTURE(_TerrainMask2, uv * _TerrainLayer2Tiling) * weights.z
        + SAMPLE_TERRAIN_TEXTURE(_TerrainMask3, uv * _TerrainLayer3Tiling) * weights.w;
}

void SetTerrainHit(Ray ray, float hitDistance, inout RayHit bestHit)
{
    float3 position = ray.origin + ray.direction * hitDistance;
    float2 uv = (position.xz - _TerrainPosition.xz) / max(_TerrainSize.xz, float2(0.001f, 0.001f));
    float3 geometricNormal = GetTerrainNormal(position.xz);
    float3 normal = GetTerrainNormal(uv, geometricNormal);
    float4 mask = GetTerrainMask(uv);
    float4 weights;
    GetTerrainWeights(uv, weights);
    float metallic = dot(weights, float4(_TerrainLayerProperties0.x, _TerrainLayerProperties1.x,
        _TerrainLayerProperties2.x, _TerrainLayerProperties3.x)) * mask.r;
    float smoothness = dot(weights, float4(_TerrainLayerProperties0.y, _TerrainLayerProperties1.y,
        _TerrainLayerProperties2.y, _TerrainLayerProperties3.y)) * mask.a;
    bestHit.position = position;
    bestHit.obj_position = _TerrainPosition;
    bestHit.geometricNormal = dot(geometricNormal, ray.direction) > 0.0f ? -geometricNormal : geometricNormal;
    bestHit.normal = dot(normal, ray.direction) > 0.0f ? -normal : normal;
    bestHit.emission = 0.0f;
    bestHit.color = GetTerrainAlbedo(uv) * mask.g;
    bestHit.obj_radius = 0.0f;
    bestHit.distance = hitDistance;
    bestHit.smoothness = saturate(smoothness);
    bestHit.metallic = saturate(metallic);
    bestHit.opacity = 1.0f;
    bestHit.refraction = 1.0f;
    bestHit.specular = 0.0f;
    bestHit.transmission = 1.0f;
    bestHit.materialType = MaterialDiffuse;
    bestHit.meshIndex = -1;
    bestHit.objectIndex = -2;
    bestHit.uv = uv;
    bestHit.textureIndex = -1;
    bestHit.metallicRoughnessTextureIndex = -1;
    bestHit.normalTextureIndex = -1;
    bestHit.lightIndex = -1;
}

void IntersectTerrain(Ray ray, inout RayHit bestHit)
{
    float3 boundsMin = _TerrainPosition;
    float3 boundsMax = _TerrainPosition + _TerrainSize;
    float entry;
    if (!IntersectAabbInverse(ray.origin, 1.0f / ray.direction, boundsMin, boundsMax, bestHit.distance, entry))
    {
        return;
    }

    float exit = min(bestHit.distance, min(
        abs(ray.direction.x) < 1e-8f ? RayMaxDistance : ((ray.direction.x > 0.0f ? boundsMax.x : boundsMin.x) - ray.origin.x) / ray.direction.x,
        min(abs(ray.direction.y) < 1e-8f ? RayMaxDistance : ((ray.direction.y > 0.0f ? boundsMax.y : boundsMin.y) - ray.origin.y) / ray.direction.y,
            abs(ray.direction.z) < 1e-8f ? RayMaxDistance : ((ray.direction.z > 0.0f ? boundsMax.z : boundsMin.z) - ray.origin.z) / ray.direction.z)));
    entry = max(0.001f, entry);
    if (exit <= entry)
    {
        return;
    }

    // March at heightmap-texel scale across the full ray interval. Restarting a fixed march in
    // each coarse DDA cell made the root approximation discontinuous at cell boundaries, which
    // exposed the acceleration grid as visible facets at grazing angles.
    float2 heightmapTexelSize = _TerrainSize.xz / max(1.0f, (float)(_TerrainHeightmapResolution - 1));
    float2 traversedXZ = abs(ray.direction.xz) * (exit - entry);
    int steps = clamp((int)ceil(max(traversedXZ.x / heightmapTexelSize.x, traversedXZ.y / heightmapTexelSize.y)), 1, 1024);
    float previousT = entry;
    float previousDistance = ray.origin.y + ray.direction.y * previousT - GetTerrainHeight((ray.origin + ray.direction * previousT).xz);
    int step;
    [loop]
    for (step = 1; step <= steps; step++)
    {
        float sampleT = lerp(entry, exit, (float)step / steps);
        float sampleDistance = ray.origin.y + ray.direction.y * sampleT - GetTerrainHeight((ray.origin + ray.direction * sampleT).xz);
        if (previousDistance * sampleDistance <= 0.0f)
        {
            float low = previousT;
            float high = sampleT;
            int refinement;
            [loop]
            for (refinement = 0; refinement < 12; refinement++)
            {
                float mid = (low + high) * 0.5f;
                float midDistance = ray.origin.y + ray.direction.y * mid - GetTerrainHeight((ray.origin + ray.direction * mid).xz);
                if (previousDistance * midDistance <= 0.0f) high = mid;
                else { low = mid; previousDistance = midDistance; }
            }
            SetTerrainHit(ray, high, bestHit);
            return;
        }
        previousT = sampleT;
        previousDistance = sampleDistance;
    }
}
#endif

float2 ApplySphereParallax(Sphere sphere, float3 normal, float2 uv, float3 viewDirection);
float3 ApplySphereNormal(Sphere sphere, float3 geometricNormal, float2 uv);

void IntersectSphere(Ray ray, inout RayHit bestHit, Sphere sphere, int objectIndex)
{
    // Calculate distance along the ray where the sphere is intersected
    float3 d = sphere.position - ray.origin;
    float p1 = dot(ray.direction, d);

    float radiusSqr = sphere.radius * sphere.radius;
    float distanceToCenterSqr = dot(d, d);

    // Ray is outside the sphere and pointing away from it.
    if (p1 < 0 && distanceToCenterSqr > radiusSqr)
    {
        return;
    }

    float p2sqr = p1 * p1 - distanceToCenterSqr + radiusSqr;

    // Ray missed the sphere
    if (p2sqr < 0)
    {
        return;
    }

    float p2 = sqrt(p2sqr);
    float t = p1 - p2 > 0 ? p1 - p2 : p1 + p2;
    if (t > 0 && t < bestHit.distance)
    {
        bestHit.position = ray.origin + t * ray.direction;
        bestHit.obj_position = sphere.position;
        bestHit.geometricNormal = normalize(bestHit.position - sphere.position);
        bestHit.normal = dot(bestHit.geometricNormal, ray.direction) > 0.0f
            ? -bestHit.geometricNormal
            : bestHit.geometricNormal;
        bestHit.emission = sphere.emission;
        bestHit.color = sphere.color;
        bestHit.obj_radius = sphere.radius;
        bestHit.distance = t;
        bestHit.smoothness = sphere.smoothness;
        bestHit.metallic = sphere.materialType == MaterialMetal ? 1.0f : 0.0f;
        bestHit.opacity = sphere.opacity;
        bestHit.refraction = sphere.refraction;
        bestHit.specular = sphere.specular;
        bestHit.transmission = sphere.transmission;
        bestHit.materialType = sphere.materialType;
        bestHit.meshIndex = -1;
        bestHit.objectIndex = objectIndex;
        float3 sphereNormal = bestHit.geometricNormal;
        float2 uv = float2(
            atan2(sphereNormal.z, sphereNormal.x) / (2.0f * PI) + 0.5f,
            asin(clamp(sphereNormal.y, -1.0f, 1.0f)) / PI + 0.5f);
        uv *= sphere.textureUvScale;
        uv = ApplySphereParallax(sphere, sphereNormal, uv, normalize(-ray.direction));
        bestHit.normal = ApplySphereNormal(sphere, bestHit.normal, uv);
        bestHit.uv = uv;
        bestHit.textureIndex = sphere.textureIndex;
        bestHit.metallicRoughnessTextureIndex = -1;
        bestHit.normalTextureIndex = sphere.normalTextureIndex;
        bestHit.lightIndex = objectIndex;
    }
}

void IntersectLightSphere(Ray ray, inout RayHit bestHit, Light light, int objectIndex)
{
    Sphere sphere;
    sphere.position = light.position;
    sphere.color = float3(0.0f, 0.0f, 0.0f);
    sphere.emission = light.emission;
    sphere.radius = light.radius;
    sphere.smoothness = 0.0f;
    sphere.opacity = 1.0f;
    sphere.specular = 0.0f;
    sphere.transmission = 1.0f;
    sphere.refraction = 1.0f;
    sphere.materialType = MaterialEmissive;
    sphere.textureIndex = -1;
    sphere.normalTextureIndex = -1;
    sphere.parallaxTextureIndex = -1;
    sphere.textureUvScale = float2(1.0f, 1.0f);
    sphere.parallaxStrength = 0.0f;
    sphere.minimumParallaxStrength = 0.0f;
    IntersectSphere(ray, bestHit, sphere, objectIndex);
}

// Boolean occlusion test for opaque shadow blockers: returns true when the sphere blocks the ray
// before maxDistance. Avoids building a RayHit, used by the pure-occlusion shadow fast path when
// the scene has no transparent shadow blockers.
bool SphereOccludes(Ray ray, Sphere sphere, float maxDistance)
{
    float3 d = sphere.position - ray.origin;
    float p1 = dot(ray.direction, d);
    if (p1 < 0)
    {
        return false;
    }

    float p2sqr = p1 * p1 - dot(d, d) + sphere.radius * sphere.radius;
    if (p2sqr < 0)
    {
        return false;
    }

    float p2 = sqrt(p2sqr);
    float t = p1 - p2 > 0 ? p1 - p2 : p1 + p2;
    return t > 0.001f && t < maxDistance;
}

float GetSphereDistanceThroughMedium(Ray ray, Sphere sphere, float maxDistance)
{
    float3 d = sphere.position - ray.origin;
    float p1 = dot(ray.direction, d);
    float p2sqr = p1 * p1 - dot(d, d) + sphere.radius * sphere.radius;
    if (p2sqr < 0.0f)
    {
        return 0.0f;
    }

    float p2 = sqrt(p2sqr);
    float entryDistance = max(0.0f, p1 - p2);
    float exitDistance = min(maxDistance, p1 + p2);
    return max(0.0f, exitDistance - entryDistance);
}

float GetSphereExitDistance(Ray ray, float3 spherePosition, float sphereRadius)
{
    float3 d = spherePosition - ray.origin;
    float p1 = dot(ray.direction, d);
    float p2sqr = p1 * p1 - dot(d, d) + sphereRadius * sphereRadius;
    if (p2sqr < 0.0f)
    {
        return 0.0f;
    }

    return p1 + sqrt(p2sqr);
}

float3 GetAbsorptionTransmittance(float3 filterColor, float opacity, float distanceThroughMedium)
{
    float densityDistance = max(0.0f, distanceThroughMedium) * saturate(opacity);
    float3 spectralFilter = pow(max(saturate(filterColor), GlassAbsorptionColorFloor.xxx), densityDistance);
    float neutralLoss = exp(-GlassNeutralAbsorption * densityDistance);
    return saturate(spectralFilter * neutralLoss);
}

float GetMediumSegmentDistance(Ray ray, float hitDistance, MediumIdentity medium)
{
    if (medium.type == MediumTypeAir)
    {
        return 0.0f;
    }

    if (medium.type == MediumTypeWater)
    {
        return GetWaterDistanceAlongRay(ray, hitDistance);
    }

    return hitDistance < RayMaxDistance ? max(0.0f, hitDistance) : 0.0f;
}

float3 GetMediumSegmentTransmittance(MediumIdentity medium, float distanceThroughMedium)
{
    if (medium.type == MediumTypeAir || distanceThroughMedium <= 0.0f)
    {
        return float3(1.0f, 1.0f, 1.0f);
    }

    return medium.type == MediumTypeWater
        ? GetWaterAbsorptionTransmittance(distanceThroughMedium)
        : GetAbsorptionTransmittance(medium.absorptionColor, medium.opacity, distanceThroughMedium);
}

float3 GetActiveMediumSegmentTransmittance(Ray ray, float hitDistance, in MediumStack stack)
{
    MediumIdentity medium = GetCurrentMedium(stack);
    return GetMediumSegmentTransmittance(medium, GetMediumSegmentDistance(ray, hitDistance, medium));
}

void ApplyFiniteMediumExitAfterSegment(inout MediumStack stack, Ray ray, RayHit hit)
{
    MediumIdentity medium = GetCurrentMedium(stack);
    if (medium.type != MediumTypeWater || hit.materialType == MaterialWater)
    {
        return;
    }

    float waterDistance = GetWaterDistanceAlongRay(ray, hit.distance);
    if (waterDistance + 0.001f < hit.distance)
    {
        PopMatchingMedium(stack, medium);
    }
}

float3 GetTransparentShadowTransmittance(RayHit hit, float distanceThroughMedium)
{
    return GetAbsorptionTransmittance(hit.color, hit.opacity, distanceThroughMedium) * saturate(1.0f - hit.opacity);
}

float3 GetTransparentShadowBoundaryTransmittance(RayHit hit)
{
    float boundaryTransmission = saturate(1.0f - hit.opacity);
    return boundaryTransmission.xxx;
}

bool IntersectTriangleRaw(Ray ray, MeshTriangle meshTriangle, out float hitDistance, out float3 normal, out float2 barycentric)
{
    float3 edge1 = meshTriangle.vertex1 - meshTriangle.vertex0;
    float3 edge2 = meshTriangle.vertex2 - meshTriangle.vertex0;
    float3 p = cross(ray.direction, edge2);
    float determinant = dot(edge1, p);
    float determinantScale = length(edge1) * length(p);

    hitDistance = RayMaxDistance;
    normal = meshTriangle.normal;
    barycentric = float2(0.0f, 0.0f);

    if (determinantScale <= 0.0f || abs(determinant) <= 0.000001f * determinantScale)
    {
        return false;
    }

    float inverseDeterminant = 1.0f / determinant;
    float3 t = ray.origin - meshTriangle.vertex0;
    float u = dot(t, p) * inverseDeterminant;

    if (u < 0.0f || u > 1.0f)
    {
        return false;
    }

    float3 q = cross(t, edge1);
    float v = dot(ray.direction, q) * inverseDeterminant;

    if (v < 0.0f || u + v > 1.0f)
    {
        return false;
    }

    hitDistance = dot(edge2, q) * inverseDeterminant;
    barycentric = float2(u, v);
    return hitDistance > 0.001f;
}

float3 GetTriangleOpticalNormal(MeshTriangle meshTriangle, float3 geometricNormal, float2 barycentric, float2 uv)
{
    float3 opticalNormal = geometricNormal;
    if (meshTriangle.interpolateNormals != 0)
    {
        opticalNormal = normalize(meshTriangle.normal0 * (1.0f - barycentric.x - barycentric.y)
            + meshTriangle.normal1 * barycentric.x
            + meshTriangle.normal2 * barycentric.y);
        opticalNormal = dot(opticalNormal, geometricNormal) < 0.0f ? -opticalNormal : opticalNormal;
    }

    if (meshTriangle.normalTextureIndex < 0)
    {
        return opticalNormal;
    }

    float4 tangentData = meshTriangle.tangent0 * (1.0f - barycentric.x - barycentric.y)
        + meshTriangle.tangent1 * barycentric.x
        + meshTriangle.tangent2 * barycentric.y;
    float3 tangent = tangentData.xyz - opticalNormal * dot(tangentData.xyz, opticalNormal);
    if (dot(tangent, tangent) <= 1e-8f)
    {
        float3 helper = abs(opticalNormal.y) < 0.999f
            ? float3(0.0f, 1.0f, 0.0f)
            : float3(1.0f, 0.0f, 0.0f);
        tangent = normalize(cross(helper, opticalNormal));
        tangentData.w = 1.0f;
    }
    tangent = normalize(tangent);
    float3 bitangent = normalize(cross(opticalNormal, tangent)) * (tangentData.w < 0.0f ? -1.0f : 1.0f);
    float3 tangentNormal = _MeshNormalTextures.SampleLevel(
        sampler_MeshNormalTextures,
        float3(frac(uv), meshTriangle.normalTextureIndex),
        0).xyz * 2.0f - 1.0f;
    tangentNormal.xy *= meshTriangle.normalStrength;
    tangentNormal.z = sqrt(saturate(1.0f - dot(tangentNormal.xy, tangentNormal.xy)));
    float3 mappedNormal = normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + opticalNormal * tangentNormal.z);
    return dot(mappedNormal, geometricNormal) < 0.0f ? -mappedNormal : mappedNormal;
}

float2 ApplySimpleParallax(MeshTriangle meshTriangle, float3 normal, float2 barycentric, float2 uv, float3 viewDirection)
{
    if (meshTriangle.parallaxTextureIndex < 0 || meshTriangle.parallaxStrength <= 0.0f)
    {
        return uv;
    }

    float4 tangentData = meshTriangle.tangent0 * (1.0f - barycentric.x - barycentric.y)
        + meshTriangle.tangent1 * barycentric.x + meshTriangle.tangent2 * barycentric.y;
    float3 tangent = tangentData.xyz - normal * dot(tangentData.xyz, normal);
    if (dot(tangent, tangent) <= 1e-8f)
    {
        float3 helper = abs(normal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
        tangent = normalize(cross(helper, normal));
        tangentData.w = 1.0f;
    }
    else
    {
        tangent = normalize(tangent);
    }
    float3 bitangent = normalize(cross(normal, tangent)) * (tangentData.w < 0.0f ? -1.0f : 1.0f);
    float3 viewTangent = normalize(float3(dot(viewDirection, tangent), dot(viewDirection, bitangent), dot(viewDirection, normal)));
    float height = _MeshParallaxTextures.SampleLevel(sampler_MeshParallaxTextures, float3(frac(uv), meshTriangle.parallaxTextureIndex), 0).r;
    // Simple parallax has no self-occlusion; fade it out before the grazing-angle offset becomes misleading.
    float grazingFade = smoothstep(0.0f, max(_ParallaxMaximumStrengthCosine, 0.0001f), abs(viewTangent.z));
    float parallaxStrength = lerp(meshTriangle.minimumParallaxStrength, meshTriangle.parallaxStrength, grazingFade);
    float offsetScale = parallaxStrength * (height - 0.5f) / max(abs(viewTangent.z), 0.15f);
    return uv + viewTangent.xy * offsetScale;
}

float2 ApplySphereParallax(Sphere sphere, float3 normal, float2 uv, float3 viewDirection)
{
    if (sphere.parallaxTextureIndex < 0 || sphere.parallaxStrength <= 0.0f)
    {
        return uv;
    }

    float3 helper = abs(normal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    float3 tangent = normalize(cross(helper, normal));
    float3 bitangent = normalize(cross(normal, tangent));
    float3 viewTangent = normalize(float3(dot(viewDirection, tangent), dot(viewDirection, bitangent), dot(viewDirection, normal)));
    float height = _MeshParallaxTextures.SampleLevel(sampler_MeshParallaxTextures, float3(frac(uv), sphere.parallaxTextureIndex), 0).r;
    float grazingFade = smoothstep(0.0f, max(_ParallaxMaximumStrengthCosine, 0.0001f), abs(viewTangent.z));
    float parallaxStrength = lerp(sphere.minimumParallaxStrength, sphere.parallaxStrength, grazingFade);
    float offsetScale = parallaxStrength * (height - 0.5f) / max(abs(viewTangent.z), 0.15f);
    return uv + viewTangent.xy * offsetScale;
}

float3 ApplySphereNormal(Sphere sphere, float3 geometricNormal, float2 uv)
{
    if (sphere.normalTextureIndex < 0)
    {
        return geometricNormal;
    }

    float3 helper = abs(geometricNormal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    float3 tangent = normalize(cross(helper, geometricNormal));
    float3 bitangent = normalize(cross(geometricNormal, tangent));
    float3 tangentNormal = _MeshNormalTextures.SampleLevel(
        sampler_MeshNormalTextures,
        float3(frac(uv), sphere.normalTextureIndex),
        0).xyz * 2.0f - 1.0f;
    float3 mappedNormal = normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + geometricNormal * tangentNormal.z);
    return dot(mappedNormal, geometricNormal) < 0.0f ? -mappedNormal : mappedNormal;
}

RayHit IntersectTriangle(Ray ray, RayHit currentHit, MeshTriangle meshTriangle, int triangleIndex)
{
    RayHit bestHit = currentHit;
    float hitDistance;
    float3 normal;
    float2 barycentric;
    if (IntersectTriangleRaw(ray, meshTriangle, hitDistance, normal, barycentric)
        && hitDistance > 0.001f && hitDistance < bestHit.distance)
    {
        float3 geometricNormal = normal;
        float2 uv = meshTriangle.uv0 * (1.0f - barycentric.x - barycentric.y)
            + meshTriangle.uv1 * barycentric.x
            + meshTriangle.uv2 * barycentric.y;
        float3 viewDirection = normalize(-ray.direction);
        uv *= meshTriangle.textureUvScale;
        float sine;
        float cosine;
        sincos(meshTriangle.textureUvRotation, sine, cosine);
        uv = float2(cosine * uv.x - sine * uv.y, sine * uv.x + cosine * uv.y);
        uv = ApplySimpleParallax(meshTriangle, geometricNormal, barycentric, uv, viewDirection);
        normal = GetTriangleOpticalNormal(meshTriangle, geometricNormal, barycentric, uv);

        if (dot(normal, ray.direction) > 0.0f)
        {
            normal = -normal;
        }
        if (dot(geometricNormal, ray.direction) > 0.0f)
        {
            geometricNormal = -geometricNormal;
        }

        bestHit.position = ray.origin + hitDistance * ray.direction;
        bestHit.obj_position = bestHit.position;
        bestHit.normal = normal;
        bestHit.geometricNormal = geometricNormal;
        bestHit.emission = meshTriangle.emission;
        bestHit.color = meshTriangle.color;
        bestHit.obj_radius = 0.0f;
        bestHit.distance = hitDistance;
        bestHit.smoothness = meshTriangle.smoothness;
        bestHit.metallic = meshTriangle.metallic;
        bestHit.opacity = meshTriangle.opacity;
        bestHit.refraction = meshTriangle.refraction;
        bestHit.specular = meshTriangle.specular;
        bestHit.transmission = meshTriangle.transmission;
        bestHit.materialType = meshTriangle.materialType;
        bestHit.meshIndex = meshTriangle.meshIndex;
        bestHit.objectIndex = -1;
        bestHit.uv = uv;
        bestHit.textureIndex = meshTriangle.textureIndex;
        bestHit.metallicRoughnessTextureIndex = meshTriangle.metallicRoughnessTextureIndex;
        bestHit.normalTextureIndex = meshTriangle.normalTextureIndex;
        bestHit.lightIndex = meshTriangle.lightIndex;
        bestHit.triangleIndex = triangleIndex;
    }

    return bestHit;
}

// Slab AABB test that takes a precomputed inverse ray direction so BVH traversal does not
// repeat the 3 divides for every node it visits. Returns the entry distance (tMin, clamped to
// 0 when the ray origin is inside the box) through entryDistance so callers can order child
// visits near-first; entryDistance is only valid when the function returns true.
bool IntersectAabbInverse(float3 rayOrigin, float3 inverseDirection, float3 boundsMin, float3 boundsMax, float maxDistance, out float entryDistance)
{
    float3 t0 = (boundsMin - rayOrigin) * inverseDirection;
    float3 t1 = (boundsMax - rayOrigin) * inverseDirection;
    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);
    float tMin = max(max(tMin3.x, tMin3.y), tMin3.z);
    float tMax = min(min(tMax3.x, tMax3.y), tMax3.z);

    entryDistance = max(0.0f, tMin);
    return tMax >= entryDistance && tMin < maxDistance;
}

bool IntersectAabb(Ray ray, float3 boundsMin, float3 boundsMax, float maxDistance)
{
    float3 inverseDirection = 1.0f / ray.direction;
    float entryDistance;
    return IntersectAabbInverse(ray.origin, inverseDirection, boundsMin, boundsMax, maxDistance, entryDistance);
}

#if defined(FOG_ENABLED)
bool GetFogInterval(Ray ray, float maxDistance, out float entryDistance, out float exitDistance)
{
    entryDistance = 0.0f;
    exitDistance = 0.0f;
    if (_FogDensity <= 0.0f)
    {
        return false;
    }

    float3 inverseDirection = 1.0f / ray.direction;
    float3 t0 = (_FogBoundsMin - ray.origin) * inverseDirection;
    float3 t1 = (_FogBoundsMax - ray.origin) * inverseDirection;
    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);
    float tMin = max(max(tMin3.x, tMin3.y), tMin3.z);
    float tMax = min(min(tMax3.x, tMax3.y), tMax3.z);
    entryDistance = max(0.0f, tMin);
    exitDistance = min(tMax, maxDistance);
    return exitDistance > entryDistance;
}

float GetFogDistanceAlongSegment(float3 startPosition, float3 endPosition)
{
    float3 offset = endPosition - startPosition;
    float segmentLength = length(offset);
    if (segmentLength <= 1e-6f)
    {
        return 0.0f;
    }

    float entryDistance;
    float exitDistance;
    return GetFogInterval(CreateRay(startPosition, offset / segmentLength), segmentLength, entryDistance, exitDistance)
        ? exitDistance - entryDistance
        : 0.0f;
}

float GetFogTransmittanceAlongSegment(float3 startPosition, float3 endPosition)
{
    return exp(-max(0.0f, _FogDensity) * GetFogDistanceAlongSegment(startPosition, endPosition));
}

bool SampleFogScatteringEvent(Ray ray, float maxDistance, inout uint rngState, out float eventDistance)
{
    eventDistance = 0.0f;
    float entryDistance;
    float exitDistance;
    if (!GetFogInterval(ray, maxDistance, entryDistance, exitDistance))
    {
        return false;
    }

    float freeFlightDistance = -log(max(1e-6f, 1.0f - rand(rngState))) / max(_FogDensity, 1e-6f);
    eventDistance = entryDistance + freeFlightDistance;
    return eventDistance < exitDistance;
}
#endif

float3 SampleUniformSphere(inout uint rngState)
{
    float z = 1.0f - 2.0f * rand(rngState);
    float phi = 2.0f * PI * rand(rngState);
    float radial = sqrt(max(0.0f, 1.0f - z * z));
    return float3(radial * cos(phi), radial * sin(phi), z);
}

void IntersectMeshBvh(Ray ray, inout RayHit bestHit, MeshInfo meshInfo)
{
    float3 inverseDirection = 1.0f / ray.direction;
    float ignoredEntry;
    if (meshInfo.triangleCount <= 0 || !IntersectAabbInverse(ray.origin, inverseDirection, meshInfo.boundsMin, meshInfo.boundsMax, bestHit.distance, ignoredEntry))
    {
        return;
    }

    int stack[BvhStackSize];
    int stackCount = 0;
    stack[stackCount++] = meshInfo.rootNodeIndex;

    [loop]
    while (stackCount > 0)
    {
        int nodeIndex = stack[--stackCount];
        BvhNode node = _BvhNodes[nodeIndex];

        if (node.triangleCount > 0)
        {
            [loop]
            for (int i = 0; i < node.triangleCount; i++)
            {
                int triangleIndex = node.triangleStart + i;
                bestHit = IntersectTriangle(ray, bestHit, _Triangles[triangleIndex], triangleIndex);
            }
            continue;
        }

        // Test both children, then push the farther one first so the nearer child is popped
        // and traversed first. A closer hit shrinks bestHit.distance, letting the farther
        // child fail its AABB test and skip its whole subtree.
        int leftIndex = node.leftChildIndex;
        int rightIndex = node.rightChildIndex;
        float leftEntry = RayMaxDistance;
        float rightEntry = RayMaxDistance;
        bool hitLeft = leftIndex >= 0 &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[leftIndex].boundsMin, _BvhNodes[leftIndex].boundsMax, bestHit.distance, leftEntry);
        bool hitRight = rightIndex >= 0 &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[rightIndex].boundsMin, _BvhNodes[rightIndex].boundsMax, bestHit.distance, rightEntry);

        if (hitLeft && hitRight)
        {
            int nearIndex = leftEntry <= rightEntry ? leftIndex : rightIndex;
            int farIndex = leftEntry <= rightEntry ? rightIndex : leftIndex;
            if (stackCount < BvhStackSize) { stack[stackCount++] = farIndex; }
            if (stackCount < BvhStackSize) { stack[stackCount++] = nearIndex; }
        }
        else if (hitLeft && stackCount < BvhStackSize)
        {
            stack[stackCount++] = leftIndex;
        }
        else if (hitRight && stackCount < BvhStackSize)
        {
            stack[stackCount++] = rightIndex;
        }
    }
}

// Pure-occlusion mesh traversal for the opaque-only shadow fast path. Returns true on the first
// triangle that blocks the ray before maxDistance. Assumes the scene has no transparent shadow
// blockers, so any hit fully occludes and the search can stop immediately.
bool MeshBvhOccludes(Ray ray, float maxDistance, MeshInfo meshInfo)
{
    float3 inverseDirection = 1.0f / ray.direction;
    float ignoredEntry;
    if (meshInfo.triangleCount <= 0 || !IntersectAabbInverse(ray.origin, inverseDirection, meshInfo.boundsMin, meshInfo.boundsMax, maxDistance, ignoredEntry))
    {
        return false;
    }

    int stack[BvhStackSize];
    int stackCount = 0;
    stack[stackCount++] = meshInfo.rootNodeIndex;

    [loop]
    while (stackCount > 0)
    {
        int nodeIndex = stack[--stackCount];
        BvhNode node = _BvhNodes[nodeIndex];

        if (node.triangleCount > 0)
        {
            [loop]
            for (int i = 0; i < node.triangleCount; i++)
            {
                float hitDistance;
                float3 hitNormal;
                float2 hitBarycentric;
                if (IntersectTriangleRaw(ray, _Triangles[node.triangleStart + i], hitDistance, hitNormal, hitBarycentric)
                    && hitDistance > 0.001f && hitDistance < maxDistance)
                {
                    return true;
                }
            }
            continue;
        }

        int leftIndex = node.leftChildIndex;
        int rightIndex = node.rightChildIndex;
        float ignoredLeft;
        float ignoredRight;
        if (leftIndex >= 0 && stackCount < BvhStackSize &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[leftIndex].boundsMin, _BvhNodes[leftIndex].boundsMax, maxDistance, ignoredLeft))
        {
            stack[stackCount++] = leftIndex;
        }

        if (rightIndex >= 0 && stackCount < BvhStackSize &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[rightIndex].boundsMin, _BvhNodes[rightIndex].boundsMax, maxDistance, ignoredRight))
        {
            stack[stackCount++] = rightIndex;
        }
    }

    return false;
}

bool IntersectMeshBvhForExit(Ray ray, MeshInfo meshInfo, out float exitDistance, out float3 exitGeometricNormal, out float3 exitOpticalNormal)
{
    exitDistance = RayMaxDistance;
    exitGeometricNormal = float3(0.0f, 1.0f, 0.0f);
    exitOpticalNormal = exitGeometricNormal;

    float3 inverseDirection = 1.0f / ray.direction;
    float ignoredEntry;
    if (meshInfo.triangleCount <= 0 || !IntersectAabbInverse(ray.origin, inverseDirection, meshInfo.boundsMin, meshInfo.boundsMax, exitDistance, ignoredEntry))
    {
        return false;
    }

    int stack[BvhStackSize];
    int stackCount = 0;
    stack[stackCount++] = meshInfo.rootNodeIndex;

    [loop]
    while (stackCount > 0)
    {
        int nodeIndex = stack[--stackCount];
        BvhNode node = _BvhNodes[nodeIndex];

        if (node.triangleCount > 0)
        {
            [loop]
            for (int i = 0; i < node.triangleCount; i++)
            {
                float triangleDistance;
                float3 triangleNormal;
                float2 triangleBarycentric;
                MeshTriangle meshTriangle = _Triangles[node.triangleStart + i];
                if (IntersectTriangleRaw(ray, meshTriangle, triangleDistance, triangleNormal, triangleBarycentric) && triangleDistance < exitDistance)
                {
                    exitDistance = triangleDistance;
                    exitGeometricNormal = triangleNormal;
                    float2 exitUv = meshTriangle.uv0 * (1.0f - triangleBarycentric.x - triangleBarycentric.y)
                        + meshTriangle.uv1 * triangleBarycentric.x
                        + meshTriangle.uv2 * triangleBarycentric.y;
                    exitOpticalNormal = GetTriangleOpticalNormal(meshTriangle, triangleNormal, triangleBarycentric, exitUv);
                }
            }
            continue;
        }

        int leftIndex = node.leftChildIndex;
        int rightIndex = node.rightChildIndex;
        float leftEntry;
        float rightEntry;
        bool hitLeft = leftIndex >= 0 &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[leftIndex].boundsMin, _BvhNodes[leftIndex].boundsMax, exitDistance, leftEntry);
        bool hitRight = rightIndex >= 0 &&
            IntersectAabbInverse(ray.origin, inverseDirection, _BvhNodes[rightIndex].boundsMin, _BvhNodes[rightIndex].boundsMax, exitDistance, rightEntry);

        if (hitLeft && hitRight)
        {
            int nearIndex = leftEntry <= rightEntry ? leftIndex : rightIndex;
            int farIndex = leftEntry <= rightEntry ? rightIndex : leftIndex;
            if (stackCount < BvhStackSize) { stack[stackCount++] = farIndex; }
            if (stackCount < BvhStackSize) { stack[stackCount++] = nearIndex; }
        }
        else if (hitLeft && stackCount < BvhStackSize)
        {
            stack[stackCount++] = leftIndex;
        }
        else if (hitRight && stackCount < BvhStackSize)
        {
            stack[stackCount++] = rightIndex;
        }
    }

    return exitDistance < RayMaxDistance;
}

RayHit GetNearestIntersectionBounded(Ray ray, float maxDistance, int ignoredMeshIndex, int ignoredSphereIndex)
{
	RayHit bestHit = CreateRayHit();
	bestHit.distance = maxDistance;

	IntersectWater(ray, bestHit);
	#if defined(TERRAIN_ENABLED)
	IntersectTerrain(ray, bestHit);
	#endif

	if (_NumTopLevelBvhNodes <= 0)
	{
		int i;
		[loop]
		for (i = 0; i < _NumSpheres; i++)
		{
			if (i == ignoredSphereIndex)
			{
				continue;
			}

			IntersectSphere(ray, bestHit, _Spheres[i], i);
		}

		[loop]
		for (i = 0; i < _NumLights; i++)
		{
            if (_Lights[i].type == LightTypeSphere)
            {
                IntersectLightSphere(ray, bestHit, _Lights[i], i);
            }
		}

		[loop]
		for (i = 0; i < _NumMeshes; i++)
		{
			if (_Meshes[i].meshIndex == ignoredMeshIndex)
			{
				continue;
			}

			IntersectMeshBvh(ray, bestHit, _Meshes[i]);
		}

		if (bestHit.distance >= maxDistance)
		{
			return CreateRayHit();
		}

		return bestHit;
	}

	int stack[BvhStackSize];
	int stackCount = 0;
	stack[stackCount++] = 0;

	float3 inverseDirection = 1.0f / ray.direction;

	[loop]
	while (stackCount > 0)
	{
		int nodeIndex = stack[--stackCount];
		TopLevelBvhNode node = _TopLevelBvhNodes[nodeIndex];

		if (node.objectType == TopLevelObjectTypeSphere)
		{
			if (node.objectIndex == ignoredSphereIndex)
			{
				continue;
			}

			IntersectSphere(ray, bestHit, _Spheres[node.objectIndex], node.objectIndex);
			continue;
		}

		if (node.objectType == TopLevelObjectTypeLight)
		{
			IntersectLightSphere(ray, bestHit, _Lights[node.objectIndex], node.objectIndex);
			continue;
		}

		if (node.objectType == TopLevelObjectTypeMesh)
		{
			if (_Meshes[node.objectIndex].meshIndex == ignoredMeshIndex)
			{
				continue;
			}

			IntersectMeshBvh(ray, bestHit, _Meshes[node.objectIndex]);
			continue;
		}

		// Internal node: test both children and push farther-first so the nearer subtree is
		// traversed first, shrinking bestHit.distance to cull the farther subtree sooner.
		int leftIndex = node.leftChildIndex;
		int rightIndex = node.rightChildIndex;
		float leftEntry;
		float rightEntry;
		bool hitLeft = leftIndex >= 0 &&
			IntersectAabbInverse(ray.origin, inverseDirection, _TopLevelBvhNodes[leftIndex].boundsMin, _TopLevelBvhNodes[leftIndex].boundsMax, bestHit.distance, leftEntry);
		bool hitRight = rightIndex >= 0 &&
			IntersectAabbInverse(ray.origin, inverseDirection, _TopLevelBvhNodes[rightIndex].boundsMin, _TopLevelBvhNodes[rightIndex].boundsMax, bestHit.distance, rightEntry);

		if (hitLeft && hitRight)
		{
			int nearIndex = leftEntry <= rightEntry ? leftIndex : rightIndex;
			int farIndex = leftEntry <= rightEntry ? rightIndex : leftIndex;
			if (stackCount < BvhStackSize) { stack[stackCount++] = farIndex; }
			if (stackCount < BvhStackSize) { stack[stackCount++] = nearIndex; }
		}
		else if (hitLeft && stackCount < BvhStackSize)
		{
			stack[stackCount++] = leftIndex;
		}
		else if (hitRight && stackCount < BvhStackSize)
		{
			stack[stackCount++] = rightIndex;
		}
	}

	if (bestHit.distance >= maxDistance)
	{
		return CreateRayHit();
	}

	return bestHit;
}

RayHit GetNearestIntersection(Ray ray)
{
    return GetNearestIntersectionBounded(ray, RayMaxDistance, -2, -1);
}

float3 GetClosestPointOnLineSegment(float3 linePointStart, float3 linePointEnd, float3 testPoint)
{
    float3 lineDiff = linePointEnd - linePointStart;
    float lineSegSqrLength = dot(lineDiff, lineDiff);

    float3 lineToPoint = testPoint - linePointStart;
    float dotProduct = dot(lineDiff, lineToPoint);

    if (lineSegSqrLength <= 1e-12f)
    {
        return linePointStart;
    }

    float percentageAlongLine = dotProduct / lineSegSqrLength;

    if (percentageAlongLine < 0.0f || percentageAlongLine > 1.0f)
    {
        // Point isn't within the line segment
        return float3(0.0f, 0.0f, 0.0f);
    }

    return linePointStart + (percentageAlongLine * (linePointEnd - linePointStart));
}

bool RefractSnell(float3 sourceDirection, float sourceRefraction, float targetRefraction, float3 surfaceNormal, out float3 refractedDirection)
{
    float3 incident = normalize(sourceDirection);
    float3 normal = normalize(surfaceNormal);
    float sourceIndex = max(0.001f, sourceRefraction);
    float targetIndex = max(0.001f, targetRefraction);
    float eta = sourceIndex / targetIndex;
    float cosIncident = clamp(dot(-incident, normal), -1.0f, 1.0f);

    if (cosIncident < 0.0f)
    {
        normal = -normal;
        cosIncident = -cosIncident;
    }

    float sinTransmittedSqr = eta * eta * max(0.0f, 1.0f - cosIncident * cosIncident);
    if (sinTransmittedSqr > 1.0f)
    {
        refractedDirection = float3(0.0f, 0.0f, 0.0f);
        return false;
    }

    float cosTransmitted = sqrt(max(0.0f, 1.0f - sinTransmittedSqr));
    refractedDirection = normalize(eta * incident + (eta * cosIncident - cosTransmitted) * normal);
    return true;
}

void CreateBasisFromNormal(float3 normal, out float3 tangent, out float3 bitangent)
{
    float3 helper = abs(normal.y) < 0.999f ? float3(0.0f, 1.0f, 0.0f) : float3(1.0f, 0.0f, 0.0f);
    tangent = normalize(cross(helper, normal));
    bitangent = cross(normal, tangent);
}

float2 SampleDisk(inout uint rngState)
{
    float radius = sqrt(rand(rngState));
    float angle = 2.0f * PI * rand(rngState);
    return float2(cos(angle), sin(angle)) * radius;
}

float3 SampleCone(float3 axis, float angularRadius, inout uint rngState)
{
    if (angularRadius <= 1e-6f)
    {
        return axis;
    }

    float cosTheta = lerp(1.0f, cos(angularRadius), rand(rngState));
    float sinTheta = sqrt(max(0.0f, 1.0f - cosTheta * cosTheta));
    float phi = 2.0f * PI * rand(rngState);
    float3 tangent;
    float3 bitangent;
    CreateBasisFromNormal(axis, tangent, bitangent);
    return normalize(axis * cosTheta + tangent * (cos(phi) * sinTheta) + bitangent * (sin(phi) * sinTheta));
}

float GetDirectLightFalloff(float distanceToLight, float lightRadius)
{
    float areaScale = max(1.0f, lightRadius * lightRadius);
    float distanceScale = max(1.0f, distanceToLight * distanceToLight * max(0.001f, _LightFalloffScale));
    return saturate(areaScale / distanceScale);
}

// Pure-occlusion shadow query used when the scene has no transparent shadow blockers
// (_HasTransparentShadowBlockers == 0). Returns true as soon as any opaque blocker is found
// before the light, skipping all the nearest-transparent-blocker bookkeeping and the per-leaf
// RayHit construction the general path needs. Mirrors the structure of GetShadowTransmittance's
// traversal (flat loops for small scenes, shadow BVH otherwise) but returns a boolean.
bool IsShadowRayBlocked(Ray rayToLight, float distanceToLight)
{
    if (_NumShadowBvhNodes <= 0)
    {
        int j;
        [loop]
        for (j = 0; j < _NumSpheres; j++)
        {
            if (SphereOccludes(rayToLight, _Spheres[j], distanceToLight))
            {
                return true;
            }
        }

        [loop]
        for (j = 0; j < _NumMeshes; j++)
        {
            if (_Meshes[j].isLight != 0)
            {
                continue;
            }

            if (MeshBvhOccludes(rayToLight, distanceToLight, _Meshes[j]))
            {
                return true;
            }
        }

        return false;
    }

    int stack[BvhStackSize];
    int stackCount = 0;
    stack[stackCount++] = 0;

    float3 inverseDirection = 1.0f / rayToLight.direction;

    [loop]
    while (stackCount > 0)
    {
        int nodeIndex = stack[--stackCount];
        TopLevelBvhNode node = _ShadowBvhNodes[nodeIndex];

        if (node.objectType == TopLevelObjectTypeSphere)
        {
            if (SphereOccludes(rayToLight, _Spheres[node.objectIndex], distanceToLight))
            {
                return true;
            }
            continue;
        }

        if (node.objectType == TopLevelObjectTypeMesh)
        {
            if (MeshBvhOccludes(rayToLight, distanceToLight, _Meshes[node.objectIndex]))
            {
                return true;
            }
            continue;
        }

        int leftIndex = node.leftChildIndex;
        int rightIndex = node.rightChildIndex;
        float ignoredLeft;
        float ignoredRight;
        if (leftIndex >= 0 && stackCount < BvhStackSize &&
            IntersectAabbInverse(rayToLight.origin, inverseDirection, _ShadowBvhNodes[leftIndex].boundsMin, _ShadowBvhNodes[leftIndex].boundsMax, distanceToLight, ignoredLeft))
        {
            stack[stackCount++] = leftIndex;
        }

        if (rightIndex >= 0 && stackCount < BvhStackSize &&
            IntersectAabbInverse(rayToLight.origin, inverseDirection, _ShadowBvhNodes[rightIndex].boundsMin, _ShadowBvhNodes[rightIndex].boundsMax, distanceToLight, ignoredRight))
        {
            stack[stackCount++] = rightIndex;
        }
    }

    return false;
}

RayHit GetNearestShadowBlocker(Ray rayToLight, float distanceToLight)
{
    RayHit blockerHit = CreateRayHit();
    blockerHit.distance = distanceToLight;

    if (_NumShadowBvhNodes <= 0)
    {
        int objectIndex;
        [loop]
        for (objectIndex = 0; objectIndex < _NumSpheres; objectIndex++)
        {
            IntersectSphere(rayToLight, blockerHit, _Spheres[objectIndex], objectIndex);
        }

        [loop]
        for (objectIndex = 0; objectIndex < _NumMeshes; objectIndex++)
        {
            if (_Meshes[objectIndex].isLight == 0)
            {
                IntersectMeshBvh(rayToLight, blockerHit, _Meshes[objectIndex]);
            }
        }

        return blockerHit;
    }

    int stack[BvhStackSize];
    int stackCount = 0;
    stack[stackCount++] = 0;
    float3 inverseDirection = 1.0f / rayToLight.direction;

    [loop]
    while (stackCount > 0)
    {
        int nodeIndex = stack[--stackCount];
        TopLevelBvhNode node = _ShadowBvhNodes[nodeIndex];

        if (node.objectType == TopLevelObjectTypeSphere)
        {
            IntersectSphere(rayToLight, blockerHit, _Spheres[node.objectIndex], node.objectIndex);
            continue;
        }

        if (node.objectType == TopLevelObjectTypeMesh)
        {
            IntersectMeshBvh(rayToLight, blockerHit, _Meshes[node.objectIndex]);
            continue;
        }

        int leftIndex = node.leftChildIndex;
        int rightIndex = node.rightChildIndex;
        float leftEntry;
        float rightEntry;
        bool hitLeft = leftIndex >= 0 &&
            IntersectAabbInverse(rayToLight.origin, inverseDirection, _ShadowBvhNodes[leftIndex].boundsMin, _ShadowBvhNodes[leftIndex].boundsMax, blockerHit.distance, leftEntry);
        bool hitRight = rightIndex >= 0 &&
            IntersectAabbInverse(rayToLight.origin, inverseDirection, _ShadowBvhNodes[rightIndex].boundsMin, _ShadowBvhNodes[rightIndex].boundsMax, blockerHit.distance, rightEntry);

        if (hitLeft && hitRight)
        {
            int nearIndex = leftEntry <= rightEntry ? leftIndex : rightIndex;
            int farIndex = leftEntry <= rightEntry ? rightIndex : leftIndex;
            if (stackCount < BvhStackSize) { stack[stackCount++] = farIndex; }
            if (stackCount < BvhStackSize) { stack[stackCount++] = nearIndex; }
        }
        else if (hitLeft && stackCount < BvhStackSize)
        {
            stack[stackCount++] = leftIndex;
        }
        else if (hitRight && stackCount < BvhStackSize)
        {
            stack[stackCount++] = rightIndex;
        }
    }

    return blockerHit;
}

bool HasPairedMeshShadowExit(Ray rayToLight, RayHit entryHit, float distanceToLight)
{
    float distanceAfterEntry = distanceToLight - entryHit.distance - 0.002f;
    if (entryHit.meshIndex < 0 || distanceAfterEntry <= 0.0f)
    {
        return false;
    }

    Ray insideRay = CreateRay(entryHit.position + rayToLight.direction * 0.002f, rayToLight.direction);
    RayHit exitHit = CreateRayHit();
    exitHit.distance = distanceAfterEntry;
    IntersectMeshBvh(insideRay, exitHit, _Meshes[entryHit.meshIndex]);
    return exitHit.distance < distanceAfterEntry;
}

float3 GetShadowTransmittance(Ray rayToLight, float distanceToLight)
{
    // Opaque-only fast path: a single occlusion query, no transparent-blocker tracking.
    if (_HasTransparentShadowBlockers == 0)
    {
        return IsShadowRayBlocked(rayToLight, distanceToLight)
            ? float3(0.0f, 0.0f, 0.0f)
            : float3(1.0f, 1.0f, 1.0f);
    }

    float3 transmittance = float3(1.0f, 1.0f, 1.0f);
    MediumStack mediumStack = CreateShadowMediumStack(rayToLight.origin);
    Ray segmentRay = rayToLight;
    float remainingDistance = distanceToLight;
    int boundaryIndex;

    [loop]
    for (boundaryIndex = 0; boundaryIndex < MaxTransparentShadowBoundaries && remainingDistance > 0.001f; boundaryIndex++)
    {
        RayHit blockerHit = GetNearestShadowBlocker(segmentRay, remainingDistance);
        float segmentDistance = min(blockerHit.distance, remainingDistance);
        transmittance *= GetMediumSegmentTransmittance(GetCurrentMedium(mediumStack), segmentDistance);

        if (max(transmittance.x, max(transmittance.y, transmittance.z)) <= 0.001f)
        {
            return float3(0.0f, 0.0f, 0.0f);
        }

        if (blockerHit.distance >= remainingDistance)
        {
            return transmittance;
        }

        if (blockerHit.opacity >= 1.0f)
        {
            return float3(0.0f, 0.0f, 0.0f);
        }

        MediumIdentity boundaryMedium = CreateHitMedium(blockerHit);
        bool exiting = MediumStackContains(mediumStack, boundaryMedium);
        bool isMeshBoundary = blockerHit.meshIndex >= 0;
        bool useThinSurfaceFallback = isMeshBoundary && !exiting &&
            !HasPairedMeshShadowExit(segmentRay, blockerHit, remainingDistance);

        if (useThinSurfaceFallback)
        {
            transmittance *= GetTransparentShadowTransmittance(blockerHit, ThinTransparentSurfaceDistance);
        }
        else if (exiting)
        {
            RemoveMatchingMedium(mediumStack, boundaryMedium);
        }
        else
        {
            // Mesh opacity describes its transmissive boundary response. Spheres historically
            // apply that factor once for the whole analytic volume, so preserve that behavior.
            if (isMeshBoundary)
            {
                transmittance *= GetTransparentShadowBoundaryTransmittance(blockerHit);
            }
            PushMedium(mediumStack, boundaryMedium);
        }

        if (max(transmittance.x, max(transmittance.y, transmittance.z)) <= 0.001f)
        {
            return float3(0.0f, 0.0f, 0.0f);
        }

        float advanceDistance = blockerHit.distance + 0.002f;
        segmentRay.origin += segmentRay.direction * advanceDistance;
        remainingDistance -= advanceDistance;
    }

    // A malformed or excessively deep transparent boundary sequence must not leak full light.
    return remainingDistance > 0.001f ? float3(0.0f, 0.0f, 0.0f) : transmittance;
}

float3 GetAlbedo(RayHit hit)
{
    if (hit.textureIndex >= 0)
    {
        float3 textureColor = _MeshAlbedoTextures.SampleLevel(
            sampler_MeshAlbedoTextures,
            float3(frac(hit.uv), hit.textureIndex),
            0).rgb;
        return hit.color * textureColor;
    }

    return hit.color;
}

float2 GetMetallicRoughness(RayHit hit)
{
    float metallic = saturate(hit.metallic);
    float roughness = saturate(1.0f - hit.smoothness);
    if (hit.metallicRoughnessTextureIndex >= 0)
    {
        float4 sample = _MeshMetallicRoughnessTextures.SampleLevel(
            sampler_MeshMetallicRoughnessTextures,
            float3(frac(hit.uv), hit.metallicRoughnessTextureIndex),
            0);
        metallic *= sample.b;
        roughness *= sample.g;
    }
    return float2(saturate(metallic), max(0.03f, roughness));
}

bool IsGlassMaterial(RayHit hit)
{
    return hit.materialType == MaterialGlass || hit.materialType == MaterialWater || hit.opacity < 1.0f;
}

bool IsWaterMaterial(RayHit hit)
{
    return hit.materialType == MaterialWater;
}

float3 GetSurfaceF0(RayHit hit, float3 albedo)
{
    if (hit.materialType == MaterialMetal)
    {
        return saturate(albedo);
    }

    if (IsGlassMaterial(hit))
    {
        float refraction = max(1.0f, hit.refraction);
        float dielectricF0 = (1.0f - refraction) / (1.0f + refraction);
        dielectricF0 *= dielectricF0;
        return IsWaterMaterial(hit)
            ? dielectricF0.xxx
            : lerp(saturate(hit.specular).xxx, float3(1.0f, 1.0f, 1.0f), dielectricF0);
    }

    return lerp(float3(0.04f, 0.04f, 0.04f), saturate(albedo), GetMetallicRoughness(hit).x);
}

float GetBrdfSpecularProbability(RayHit hit)
{
    if (hit.materialType == MaterialMetal || IsGlassMaterial(hit))
    {
        return 1.0f;
    }
    return lerp(0.5f, 1.0f, GetMetallicRoughness(hit).x);
}

float GetGgxAlpha(RayHit hit)
{
    float roughness = GetMetallicRoughness(hit).y;
    return roughness * roughness;
}

float GgxDistribution(float normalDotHalf, float alpha)
{
    float alphaSquared = alpha * alpha;
    float denominator = normalDotHalf * normalDotHalf * (alphaSquared - 1.0f) + 1.0f;
    return alphaSquared / max(PI * denominator * denominator, 1e-6f);
}

float GgxSmithG1(float normalDotDirection, float alpha)
{
    float alphaSquared = alpha * alpha;
    float root = sqrt(alphaSquared + (1.0f - alphaSquared) * normalDotDirection * normalDotDirection);
    return (2.0f * normalDotDirection) / max(normalDotDirection + root, 1e-6f);
}

float3 FresnelSchlick(float directionDotHalf, float3 f0)
{
    return f0 + (1.0f - f0) * pow(1.0f - directionDotHalf, 5.0f);
}

float PowerHeuristic(float firstPdf, float secondPdf)
{
    float firstSquared = firstPdf * firstPdf;
    float secondSquared = secondPdf * secondPdf;
    return firstSquared / max(firstSquared + secondSquared, 1e-12f);
}

// Returns the sampling density in solid angle for MIS weighting. The renderer's historical
// light-strength model remains unchanged; these PDFs only decide how explicit-light and BRDF
// samples share paths that both techniques can discover.
float GetLightShapePdf(Light light, float3 shadingPosition, float3 lightPosition)
{
    if (light.type == LightTypeDirectional)
    {
        return 0.0f;
    }

    float3 toLight = lightPosition - shadingPosition;
    float distanceSquared = dot(toLight, toLight);
    if (distanceSquared <= 1e-8f)
    {
        return 0.0f;
    }

    float3 directionToLight = toLight * rsqrt(distanceSquared);
    if (light.type == LightTypeTriangle || light.type == LightTypeSunTriangle)
    {
        float lightFacing = saturate(dot(light.normal, -directionToLight));
        return lightFacing > 1e-6f && light.area > 1e-6f
            ? distanceSquared / (lightFacing * light.area)
            : 0.0f;
    }

    float3 diskNormal = normalize(shadingPosition - light.position);
    float directionDotDiskNormal = dot(directionToLight, -diskNormal);
    if (directionDotDiskNormal <= 1e-6f)
    {
        return 0.0f;
    }

    float diskDistance = dot(light.position - shadingPosition, -diskNormal) / directionDotDiskNormal;
    float3 diskPoint = shadingPosition + directionToLight * diskDistance;
    float diskRadius = light.radius * max(0.0f, _ShadowRandomness);
    float diskArea = PI * diskRadius * diskRadius;
    float radialDistanceSquared = dot(diskPoint - light.position, diskPoint - light.position);
    return diskArea > 1e-8f && radialDistanceSquared <= diskRadius * diskRadius
        ? (diskDistance * diskDistance) / (directionDotDiskNormal * diskArea)
        : 0.0f;
}

float GetTriangleArea(MeshTriangle meshTriangle)
{
    return 0.5f * length(cross(
        meshTriangle.vertex1 - meshTriangle.vertex0,
        meshTriangle.vertex2 - meshTriangle.vertex0));
}

int SelectMeshLightTriangle(Light light, inout uint rngState, out float triangleProbability)
{
    triangleProbability = 0.0f;
    if (light.triangleCount <= 0 || light.totalArea <= 1e-8f)
    {
        return -1;
    }

    float target = rand(rngState);
    int lastIndex = light.triangleStart + light.triangleCount - 1;
    int triangleIndex = lastIndex;
    int i;
    [loop]
    for (i = light.triangleStart; i <= lastIndex; i++)
    {
        if (target <= _MeshLightTriangleCdf[i])
        {
            triangleIndex = i;
            break;
        }
    }

    MeshTriangle meshTriangle = _Triangles[triangleIndex];
    triangleProbability = GetTriangleArea(meshTriangle) / light.totalArea;
    return triangleProbability > 0.0f ? triangleIndex : -1;
}

float3 EvaluateMaterialBrdf(Ray ray, RayHit hit, float3 lightDirection, out float pdf)
{
    float3 normal = hit.normal;
    float3 viewDirection = normalize(-ray.direction);
    float normalDotView = saturate(dot(normal, viewDirection));
    float normalDotLight = saturate(dot(normal, lightDirection));
    pdf = 0.0f;
    if (normalDotView <= 0.0f || normalDotLight <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float3 halfDirection = normalize(lightDirection + viewDirection);
    float normalDotHalf = saturate(dot(normal, halfDirection));
    float viewDotHalf = saturate(dot(viewDirection, halfDirection));
    if (normalDotHalf <= 0.0f || viewDotHalf <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float3 albedo = GetAlbedo(hit);
    float3 fresnel = FresnelSchlick(viewDotHalf, GetSurfaceF0(hit, albedo));
    float alpha = GetGgxAlpha(hit);
    float distribution = GgxDistribution(normalDotHalf, alpha);
    float geometry = GgxSmithG1(normalDotView, alpha) * GgxSmithG1(normalDotLight, alpha);
    float3 specular = fresnel * distribution * geometry /
        max(4.0f * normalDotView * normalDotLight, 1e-6f);

    float3 diffuse = float3(0.0f, 0.0f, 0.0f);
    if (!IsGlassMaterial(hit))
    {
        float metallic = GetMetallicRoughness(hit).x;
        diffuse = (1.0f - fresnel) * albedo * (1.0f - metallic) * (1.0f / PI);
    }

    float specularPdf = distribution * normalDotHalf / max(4.0f * viewDotHalf, 1e-6f);
    float diffusePdf = normalDotLight * (1.0f / PI);
    float specularProbability = GetBrdfSpecularProbability(hit);
    pdf = lerp(diffusePdf, specularPdf, specularProbability);

    return diffuse + specular;
}

float3 GetSkyboxColor(float3 direction);
float3 GetSkyboxDirection(float2 uv);
int SelectEnvironmentCdf(bool marginal, int offset, int count, float target);

float3 SampleSingleLight(int lightIndex, Ray ray, RayHit hit, int sampleCount,
                          float lightSelectionPdf, int lightTechniqueSampleCount,
                          bool volumeEvent,
                          inout uint rngState)
{
    float3 lightTotal = float3(0.0f, 0.0f, 0.0f);
    bool isEnvironment = lightIndex < 0;
    if (isEnvironment && (_EnvironmentLightEnabled == 0 || _EnvironmentCdfWidth <= 0 || _EnvironmentCdfHeight <= 0))
    {
        return lightTotal;
    }

    Light light;
    float triangleSelectionProbability = 1.0f;
    bool isMeshLight = false;
    if (!isEnvironment)
    {
        light = _Lights[lightIndex];
        isMeshLight = light.type == LightTypeMesh;
        if (light.type == LightTypeMesh)
        {
            int triangleIndex = SelectMeshLightTriangle(light, rngState, triangleSelectionProbability);
            if (triangleIndex < 0)
            {
                return lightTotal;
            }

            MeshTriangle meshTriangle = _Triangles[triangleIndex];
            light.position = meshTriangle.vertex0;
            light.u = meshTriangle.vertex1 - meshTriangle.vertex0;
            light.v = meshTriangle.vertex2 - meshTriangle.vertex0;
            light.normal = meshTriangle.normal;
            light.area = GetTriangleArea(meshTriangle);
            light.type = LightTypeTriangle;
        }
    }
    bool isDirectional = false;
    float3 lightPos = 0.0f;
    float3 ptToLight = 0.0f;
    float3 tangent = 0.0f;
    float3 bitangent = 0.0f;
    if (!isEnvironment)
    {
        isDirectional = light.type == LightTypeDirectional;
        lightPos = light.type == LightTypeTriangle || light.type == LightTypeSunTriangle
            ? light.position + (light.u + light.v) / 3.0f
            : light.position;
        ptToLight = isDirectional ? -normalize(light.position) : normalize(lightPos - hit.position);
        CreateBasisFromNormal(ptToLight, tangent, bitangent);
    }

    int sampleIndex;
    [loop]
    for (sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
    {
        float3 offsetPt = hit.position;
        float3 ptToOffset;
        float distanceToLight;
        float environmentPdf = 0.0f;
        if (isEnvironment)
        {
            int y = SelectEnvironmentCdf(true, 0, _EnvironmentCdfHeight, rand(rngState));
            int x = SelectEnvironmentCdf(false, y * _EnvironmentCdfWidth, _EnvironmentCdfWidth, rand(rngState));
            float2 distributionUv = (float2(x, y) + float2(rand(rngState), rand(rngState)))
                / float2(_EnvironmentCdfWidth, _EnvironmentCdfHeight);
            float rowCdf = _EnvironmentMarginalCdf[y];
            float previousRowCdf = y > 0 ? _EnvironmentMarginalCdf[y - 1] : 0.0f;
            float columnCdf = _EnvironmentConditionalCdf[y * _EnvironmentCdfWidth + x];
            float previousColumnCdf = x > 0 ? _EnvironmentConditionalCdf[y * _EnvironmentCdfWidth + x - 1] : 0.0f;
            float texelProbability = max(0.0f, rowCdf - previousRowCdf)
                * max(0.0f, columnCdf - previousColumnCdf);
            float theta = distributionUv.y * PI;
            float texelSolidAngle = (2.0f * PI / _EnvironmentCdfWidth) * (PI / _EnvironmentCdfHeight)
                * max(abs(sin(theta)), 1e-5f);
            environmentPdf = texelProbability / texelSolidAngle;
            ptToOffset = GetSkyboxDirection(float2(distributionUv.x, 1.0f - distributionUv.y));
            distanceToLight = RayMaxDistance;
        }
        else
        {
            if (isDirectional)
            {
                offsetPt = hit.position + SampleCone(ptToLight, light.radius, rngState);
            }
            else if (light.type == LightTypeTriangle || light.type == LightTypeSunTriangle)
            {
                float r1 = rand(rngState);
                float r2 = rand(rngState);
                if (r1 + r2 > 1.0f)
                {
                    r1 = 1.0f - r1;
                    r2 = 1.0f - r2;
                }
                offsetPt = light.position + light.u * r1 + light.v * r2;
            }
            else
            {
                float2 diskSample = SampleDisk(rngState) * light.radius * max(0.0f, _ShadowRandomness);
                offsetPt = lightPos + tangent * diskSample.x + bitangent * diskSample.y;
            }

            ptToOffset = isDirectional ? normalize(offsetPt - hit.position) : offsetPt - hit.position;
            distanceToLight = isDirectional ? RayMaxDistance : length(ptToOffset);
            ptToOffset = normalize(ptToOffset);
        }

        float shadowOffsetSign = dot(ptToOffset, hit.geometricNormal) >= 0.0f ? 1.0f : -1.0f;
        float3 shadowOrigin = hit.position + hit.geometricNormal * (0.001f * shadowOffsetSign);
#if defined(FOG_ENABLED)
        if (volumeEvent)
        {
            shadowOrigin = hit.position + ptToOffset * 0.001f;
        }
#endif
        Ray rayToLight = CreateRay(shadowOrigin, ptToOffset);

        float rayNormalDot = saturate(dot(ptToOffset, hit.normal));
#if defined(FOG_ENABLED)
        if (volumeEvent)
        {
            rayNormalDot = 1.0f;
        }
#endif
        if (rayNormalDot <= 0.0f)
        {
            continue;
        }

        float3 shadowTransmittance = GetShadowTransmittance(rayToLight, distanceToLight);
        if (max(shadowTransmittance.x, max(shadowTransmittance.y, shadowTransmittance.z)) <= 0.001f)
        {
            continue;
        }

        if (isEnvironment || isDirectional)
        {
            shadowTransmittance *= GetWaterAbsorptionTransmittance(GetWaterDistanceAlongRay(rayToLight, RayMaxDistance));
        }
        else
        {
            shadowTransmittance *= GetWaterAbsorptionTransmittance(EstimateWaterDistanceAlongSegment(hit.position, offsetPt));
        }
#if defined(FOG_ENABLED)
        if (isEnvironment || isDirectional)
        {
            float fogEntryDistance;
            float fogExitDistance;
            if (GetFogInterval(rayToLight, RayMaxDistance, fogEntryDistance, fogExitDistance))
            {
                shadowTransmittance *= exp(-max(0.0f, _FogDensity) * (fogExitDistance - fogEntryDistance));
            }
        }
        else
        {
            shadowTransmittance *= GetFogTransmittanceAlongSegment(hit.position, offsetPt);
        }
#endif
        if (max(shadowTransmittance.x, max(shadowTransmittance.y, shadowTransmittance.z)) <= 0.001f)
        {
            continue;
        }

        if (isEnvironment)
        {
            if (environmentPdf <= 1e-8f)
            {
                continue;
            }

            float materialPdf;
            float3 brdf = EvaluateMaterialBrdf(ray, hit, ptToOffset, materialPdf);
            float misWeight = PowerHeuristic(environmentPdf, materialPdf);
            lightTotal += GetSkyboxColor(ptToOffset) * _SkyboxLight.xyz * shadowTransmittance
                * brdf * rayNormalDot * misWeight / environmentPdf;
            continue;
        }

        float lightShapePdf = GetLightShapePdf(light, hit.position, offsetPt);
        float materialPdf;
        float3 materialResponse = EvaluateMaterialBrdf(ray, hit, ptToOffset, materialPdf);
#if defined(FOG_ENABLED)
        if (volumeEvent)
        {
            materialPdf = 0.0f;
            materialResponse = float3(1.0f / (4.0f * PI), 1.0f / (4.0f * PI), 1.0f / (4.0f * PI));
        }
#endif
        // Sphere lights retain the renderer's historical falloff-scaled direct-light model,
        // which is not the same estimator as an emissive sphere hit. Applying complementary
        // MIS weights between them removes energy from the center of reflected sphere lights.
        if ((light.type == LightTypeTriangle || light.type == LightTypeSunTriangle) && lightShapePdf > 0.0f)
        {
            float lightPdf = lightSelectionPdf * triangleSelectionProbability * lightShapePdf;
            float misWeight = PowerHeuristic(lightTechniqueSampleCount * lightPdf, materialPdf);
            float distanceScale = max(1.0f, distanceToLight * distanceToLight * max(0.001f, _LightFalloffScale));
            float lightStrength = saturate(dot(light.normal, -ptToOffset)) * light.area / distanceScale;
            if (light.type == LightTypeSunTriangle)
            {
                // The virtual sun is represented by two triangle-light entries. Each entry
                // contributes half of the analytic directional radiance.
                lightStrength = 0.5f;
            }
            lightTotal += light.emission * lightStrength * shadowTransmittance * materialResponse
                * rayNormalDot * misWeight * (isMeshLight ? 1.0f / triangleSelectionProbability : 1.0f);
        }
        else if (isDirectional)
        {
            // Directional lights are delta lights at zero radius and are deliberately outside
            // area-light/BRDF MIS. Finite-radius cones remain direct-light-only for now.
            lightTotal += light.emission * shadowTransmittance * materialResponse * rayNormalDot;
        }
        else
        {
            // Zero-radius disk sampling is a delta-light fallback and has no competing BRDF PDF.
            float lightStrength = GetDirectLightFalloff(distanceToLight, light.radius);
            lightTotal += light.emission * lightStrength * shadowTransmittance * materialResponse * rayNormalDot;
        }
    }

    return lightTotal / sampleCount;
}

float LightImportanceWeight(int lightIndex, float3 shadingPosition);

float GetLightSelectionPdf(int lightIndex, int lightCount, bool sampleAllLights, float3 shadingPosition)
{
    if (lightIndex < 0 || lightIndex >= lightCount)
    {
        return 0.0f;
    }

    if (sampleAllLights)
    {
        return 1.0f;
    }

    if (_LightSamplingStrategy == LightSamplingUniformRandom)
    {
        return 1.0f / lightCount;
    }

    int weightedCount = min(lightCount, MaxImportanceLights);
    if (lightIndex >= weightedCount)
    {
        return 0.0f;
    }

    float totalWeight = 0.0f;
    int i;
    [loop]
    for (i = 0; i < weightedCount; i++)
    {
        totalWeight += LightImportanceWeight(i, shadingPosition);
    }
    return totalWeight > 0.0f ? LightImportanceWeight(lightIndex, shadingPosition) / totalWeight : 0.0f;
}

// Cheap per-hit estimate of how much a light is likely to contribute, used to bias
// importance sampling toward nearby/bright lights. Intentionally ignores shadows and the
// surface normal (too expensive to evaluate before picking), so it is only an estimate;
// the 1/pdf correction keeps the final result unbiased regardless of estimate accuracy.
// Uses squared distance directly to avoid a sqrt; mirrors GetDirectLightFalloff's math.
float LightImportanceWeight(int lightIndex, float3 shadingPosition)
{
    Light light = _Lights[lightIndex];
    float3 emission = light.emission;
    float luminance = max(emission.x, max(emission.y, emission.z));
    if (light.type == LightTypeDirectional)
    {
        return max(1e-6f, luminance);
    }
    if (light.type == LightTypeSunTriangle)
    {
        // A directional light is represented by two virtual triangles, whose combined
        // importance should equal one analytic source rather than two independent lights.
        return max(1e-6f, luminance * 0.5f);
    }
    float3 toLight = light.position - shadingPosition;
    float distSq = dot(toLight, toLight);

    float lightRadius = light.radius;
    float areaScale = max(1.0f, (light.type == LightTypeTriangle || light.type == LightTypeSunTriangle) ? light.area
        : light.type == LightTypeMesh ? light.totalArea : lightRadius * lightRadius);
    float distanceScale = max(1.0f, distSq * max(0.001f, _LightFalloffScale));
    float falloff = saturate(areaScale / distanceScale);

    // Keep a small floor so every light retains a nonzero pick probability (required for
    // the estimator to stay unbiased).
    return max(1e-6f, luminance * falloff);
}

// Picks which light index to shade this iteration and returns the scalar weight to apply to
// its contribution. This isolates the (cheap) per-strategy selection logic from the
// (expensive, BVH-traversing) SampleSingleLight body so the latter can be inlined at a single
// call site. Inlining SampleSingleLight at multiple sites previously made the Metal/HLSL
// compiler duplicate the BVH-traversal loop many times, causing multi-minute shader compiles
// that hung Unity on "Importing Assets".
//
//  iteration   : current draw index in [0, drawCount)
//  drawCount   : total draws this hit (1 for AllLights-per-index, else _LightSampleCount)
//  lightCount  : number of lights under consideration (after the diagnostic cap)
//  outWeight   : multiplier for this draw's contribution (Monte Carlo / pdf correction)
//  outSelectionPdf: probability of selecting the returned light for one draw
//  returns     : the light index to sample
int SelectLightForDraw(int iteration, int drawCount, int lightCount, float3 shadingPosition,
                       inout uint rngState, out float outWeight, out float outSelectionPdf)
{
    if (_LightSamplingStrategy == LightSamplingUniformRandom)
    {
        // Uniform pick: each draw is sum*(lightCount/drawCount)/drawCount summed over draws,
        // i.e. per-draw weight = lightCount / drawCount.
        outWeight = (float)lightCount / drawCount;
        outSelectionPdf = 1.0f / lightCount;
        return min(lightCount - 1, (int)(rand(rngState) * lightCount));
    }

    if (_LightSamplingStrategy == LightSamplingImportance)
    {
        int weightedCount = min(lightCount, MaxImportanceLights);

        // Accumulate total importance weight (cheap, no BVH traversal, no large array).
        float totalWeight = 0.0f;
        int w;
        [loop]
        for (w = 0; w < weightedCount; w++)
        {
            totalWeight += LightImportanceWeight(w, shadingPosition);
        }

        if (totalWeight <= 0.0f)
        {
            outWeight = 0.0f;
            outSelectionPdf = 0.0f;
            return 0;
        }

        // Walk the weighted CDF on the fly to select a light proportional to its weight.
        float target = rand(rngState) * totalWeight;
        float cumulative = 0.0f;
        int chosen = weightedCount - 1;
        float chosenWeight = LightImportanceWeight(chosen, shadingPosition);
        int c;
        [loop]
        for (c = 0; c < weightedCount; c++)
        {
            float weight = LightImportanceWeight(c, shadingPosition);
            cumulative += weight;
            if (target <= cumulative)
            {
                chosen = c;
                chosenWeight = weight;
                break;
            }
        }

        // pdf for one draw = chosenWeight / totalWeight. Average over draws => /drawCount.
        // weight = 1 / (drawCount * pdf) = totalWeight / (drawCount * chosenWeight).
        outSelectionPdf = chosenWeight / totalWeight;
        outWeight = 1.0f / (drawCount * outSelectionPdf);
        return chosen;
    }

    // AllLights: one draw per light index, weight 1. The caller iterates every light.
    outWeight = 1.0f;
    outSelectionPdf = 1.0f;
    return iteration;
}

float3 GetLightHittingPoint(Ray ray, RayHit hit, int samplesPerLight,
                            bool volumeEvent, inout uint rngState)
{
    int sampleCount = max(1, samplesPerLight);
    // Preserve fog's established finite-emitter estimator; environment NEE is a surface path.
    bool sampleEnvironment = !volumeEvent && _EnvironmentLightEnabled != 0
        && _EnvironmentCdfWidth > 0 && _EnvironmentCdfHeight > 0;

    int lightCount = _NumLights;
    // Diagnostic cap: when _MaxLightSamples > 0, only consider the first N lights.
    // Used to confirm the per-hit light loop is the performance bottleneck.
    if (_MaxLightSamples > 0 && _MaxLightSamples < lightCount)
    {
        lightCount = _MaxLightSamples;
    }

    if (lightCount <= 0 && !sampleEnvironment)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    // Determine how many draws to take this hit:
    //  - AllLights: one draw per light (drawCount = lightCount), each weight 1.
    //  - UniformRandom / Importance: _LightSampleCount draws, clamped to the light count.
    //    If that would cover (nearly) every light anyway, fall back to AllLights behaviour to
    //    avoid selection variance at the same cost.
    int drawCount;
    bool sampleAllLights =
        _LightSamplingStrategy != LightSamplingUniformRandom &&
        _LightSamplingStrategy != LightSamplingImportance;

    if (lightCount <= 0)
    {
        drawCount = 0;
    }
    else if (sampleAllLights)
    {
        drawCount = lightCount;
    }
    else
    {
        int requested = clamp(_LightSampleCount, 1, lightCount);
        if (requested >= lightCount)
        {
            // Cover every light: cheaper to treat as AllLights (weight 1, no 1/pdf scaling).
            sampleAllLights = true;
            drawCount = lightCount;
        }
        else
        {
            drawCount = requested;
        }
    }

    // SINGLE inlined SampleSingleLight call site. Finite-light and environment samples both use
    // this visibility path so the Metal compiler sees only one shadow-BVH traversal body.
    float3 accumulated = float3(0.0f, 0.0f, 0.0f);
    int d;
    [loop]
    for (d = 0; d < drawCount + (sampleEnvironment ? 1 : 0); d++)
    {
        float weight;
        float selectionPdf;
        int lightIndex;
        int selectedSampleCount;
        int lightTechniqueSampleCount;
        if (d == drawCount)
        {
            // A negative index identifies the separately sampled environment distribution.
            lightIndex = -1;
            weight = 1.0f;
            selectionPdf = 1.0f;
            selectedSampleCount = max(1, _EnvironmentLightSampleCount);
            lightTechniqueSampleCount = selectedSampleCount;
        }
        else if (sampleAllLights)
        {
            lightIndex = d;
            weight = 1.0f;
            selectionPdf = 1.0f;
            selectedSampleCount = sampleCount;
            lightTechniqueSampleCount = sampleCount;
        }
        else
        {
            lightIndex = SelectLightForDraw(
                d, drawCount, lightCount, hit.position, rngState, weight, selectionPdf);
            selectedSampleCount = sampleCount;
            lightTechniqueSampleCount = sampleCount * drawCount;
        }

        if (weight <= 0.0f)
        {
            continue;
        }

        accumulated += SampleSingleLight(lightIndex, ray, hit, selectedSampleCount,
            selectionPdf, lightTechniqueSampleCount,
            volumeEvent,
            rngState) * weight;
    }

    // For random strategies the per-draw weights already include the 1/drawCount averaging,
    // so no extra division is needed here.
    return accumulated;
}

float GetLightPdfForHit(float3 shadingPosition, RayHit lightHit, bool softShadows, out int sampleCount)
{
    sampleCount = 0;
    int lightCount = _NumLights;
    if (_MaxLightSamples > 0 && _MaxLightSamples < lightCount)
    {
        lightCount = _MaxLightSamples;
    }
    if (lightHit.lightIndex < 0 || lightHit.lightIndex >= lightCount)
    {
        return 0.0f;
    }
    if (_Lights[lightHit.lightIndex].type == LightTypeSphere)
    {
        return 0.0f;
    }

    bool sampleAllLights =
        _LightSamplingStrategy != LightSamplingUniformRandom &&
        _LightSamplingStrategy != LightSamplingImportance;
    int drawCount = lightCount;
    if (!sampleAllLights)
    {
        int requested = clamp(_LightSampleCount, 1, lightCount);
        sampleAllLights = requested >= lightCount;
        drawCount = sampleAllLights ? lightCount : requested;
    }

    int samplesPerLight = softShadows ? max(1, _ShadowQuality + 1) : 1;
    sampleCount = samplesPerLight * (sampleAllLights ? 1 : drawCount);
    float selectionPdf = GetLightSelectionPdf(lightHit.lightIndex, lightCount, sampleAllLights, shadingPosition);
    Light light = _Lights[lightHit.lightIndex];
    if (light.type == LightTypeMesh)
    {
        if (lightHit.triangleIndex < light.triangleStart
            || lightHit.triangleIndex >= light.triangleStart + light.triangleCount)
        {
            return 0.0f;
        }

        MeshTriangle meshTriangle = _Triangles[lightHit.triangleIndex];
        float triangleProbability = GetTriangleArea(meshTriangle) / max(light.totalArea, 1e-8f);
        Light triangleLight = light;
        triangleLight.position = meshTriangle.vertex0;
        triangleLight.u = meshTriangle.vertex1 - meshTriangle.vertex0;
        triangleLight.v = meshTriangle.vertex2 - meshTriangle.vertex0;
        triangleLight.normal = meshTriangle.normal;
        triangleLight.area = GetTriangleArea(meshTriangle);
        triangleLight.type = LightTypeTriangle;
        return selectionPdf * triangleProbability * GetLightShapePdf(triangleLight, shadingPosition, lightHit.position);
    }
    return selectionPdf * GetLightShapePdf(light, shadingPosition, lightHit.position);
}

float3 GetSkyboxColor(float3 direction)
{
    float theta = acos(direction.y) / -PI;
    float phi = atan2(direction.x, -direction.z) / -PI * 0.5f;
    float3 color = _SkyboxTexture.SampleLevel(sampler_SkyboxTexture, float2(phi, theta), 0).xyz;
    if (_EnvironmentHighlightThreshold <= 0.0f || _EnvironmentHighlightIntensity <= 0.0f)
    {
        return color;
    }

    float luminance = max(0.0f, dot(color, float3(0.2126f, 0.7152f, 0.0722f)));
    float knee = max(0.0f, _EnvironmentHighlightThreshold * saturate(_EnvironmentHighlightSoftKnee));
    float excess = max(0.0f, luminance - _EnvironmentHighlightThreshold);
    if (knee > 0.0f)
    {
        excess *= excess / (excess + knee);
    }
    float boostedLuminance = luminance + excess * _EnvironmentHighlightIntensity;
    return color * (boostedLuminance / max(luminance, 1e-6f));
}

float2 GetSkyboxUv(float3 direction)
{
    return float2(atan2(direction.x, -direction.z) / -PI * 0.5f, acos(direction.y) / -PI);
}

float3 GetSkyboxDirection(float2 uv)
{
    float theta = (1.0f - uv.y) * PI;
    float phi = -uv.x * PI * 2.0f;
    float sinTheta = sin(theta);
    return float3(sinTheta * sin(phi), cos(theta), -sinTheta * cos(phi));
}

int SelectEnvironmentCdf(bool marginal, int offset, int count, float target)
{
    int low = 0;
    int high = count - 1;
    [loop]
    while (low < high)
    {
        int middle = (low + high) / 2;
        float value = marginal ? _EnvironmentMarginalCdf[offset + middle] : _EnvironmentConditionalCdf[offset + middle];
        if (target <= value)
        {
            high = middle;
        }
        else
        {
            low = middle + 1;
        }
    }
    return low;
}

float GetEnvironmentPdf(float3 direction)
{
    if (_EnvironmentLightEnabled == 0 || _EnvironmentCdfWidth <= 0 || _EnvironmentCdfHeight <= 0)
    {
        return 0.0f;
    }

    float2 skyboxUv = GetSkyboxUv(direction);
    float2 uv = float2(frac(skyboxUv.x), saturate(-skyboxUv.y));
    int x = min(_EnvironmentCdfWidth - 1, (int)(uv.x * _EnvironmentCdfWidth));
    int y = min(_EnvironmentCdfHeight - 1, (int)(uv.y * _EnvironmentCdfHeight));
    float rowCdf = _EnvironmentMarginalCdf[y];
    float previousRowCdf = y > 0 ? _EnvironmentMarginalCdf[y - 1] : 0.0f;
    float columnCdf = _EnvironmentConditionalCdf[y * _EnvironmentCdfWidth + x];
    float previousColumnCdf = x > 0 ? _EnvironmentConditionalCdf[y * _EnvironmentCdfWidth + x - 1] : 0.0f;
    float texelProbability = max(0.0f, rowCdf - previousRowCdf) * max(0.0f, columnCdf - previousColumnCdf);
    float theta = (1.0f - uv.y) * PI;
    float texelSolidAngle = (2.0f * PI / _EnvironmentCdfWidth) * (PI / _EnvironmentCdfHeight) * max(abs(sin(theta)), 1e-5f);
    return texelProbability / texelSolidAngle;
}

float3 GetRandomizedNormalBasedOnAmount(float3 normal, float amount, inout uint rngState)
{
    float3 normalWithRand = normalize( float3(
                normal.x + rand(rngState) * (1 - amount) - rand(rngState) * (1 - amount),
                normal.y + rand(rngState) * (1 - amount) - rand(rngState) * (1 - amount),
                normal.z + rand(rngState) * (1 - amount) - rand(rngState) * (1 - amount)));
    return normalWithRand;
}

bool DidHitSky(RayHit hit)
{
    return hit.distance >= RayMaxDistance;
}

bool DidHitLight(RayHit hit)
{
    return (hit.emission.x + hit.emission.y + hit.emission.z) > 0.0f;
}

float3 GetTerminalHitColor(Ray ray, RayHit hit)
{
    if (DidHitSky(hit))
    {
        return GetSkyboxColor(ray.direction) * _SkyboxLight.xyz;
    }

    if (DidHitLight(hit))
    {
        return hit.emission;
    }

    return float3(0.0f, 0.0f, 0.0f);
}

float3 GetEmission(RayHit hit)
{
    return hit.emission;
}

float GetTransmissionAmount(RayHit hit)
{
    return saturate(1.0f - hit.opacity);
}

float GetFresnelReflectanceForNormal(Ray ray, RayHit hit, float sourceRefraction, float targetRefraction, float3 boundaryNormal)
{
    float sourceIndex = max(0.001f, sourceRefraction);
    float targetIndex = max(0.001f, targetRefraction);
    float r0 = (sourceIndex - targetIndex) / (sourceIndex + targetIndex);
    r0 *= r0;

    float cosTheta = saturate(abs(dot(-ray.direction, boundaryNormal)));
    float reflectance = r0 + (1.0f - r0) * pow(1.0f - cosTheta, 5.0f);
    return reflectance;
}

float GetFresnelReflectance(Ray ray, RayHit hit, float sourceRefraction, float targetRefraction)
{
    return GetFresnelReflectanceForNormal(ray, hit, sourceRefraction, targetRefraction, hit.normal);
}

float GetGlassReflectionProbability(float fresnelReflectance, RayHit hit)
{
    return lerp(saturate(hit.specular), 1.0f, fresnelReflectance);
}

float3 GetCausticOpticalNormal(RayHit hit)
{
    return hit.normal;
}

float3 GetDiffuseScatterDirection(float3 normal, inout uint rngState)
{
    float2 diskSample = SampleDisk(rngState);
    float z = sqrt(max(0.0f, 1.0f - dot(diskSample, diskSample)));
    float3 tangent;
    float3 bitangent;
    CreateBasisFromNormal(normal, tangent, bitangent);

    return normalize(tangent * diskSample.x + bitangent * diskSample.y + normal * z);
}

float3 SampleGgxHalfDirection(float3 normal, float alpha, inout uint rngState)
{
    float u1 = rand(rngState);
    float u2 = rand(rngState);
    float alphaSquared = alpha * alpha;
    float cosTheta = sqrt((1.0f - u1) / max(1.0f + (alphaSquared - 1.0f) * u1, 1e-6f));
    float sinTheta = sqrt(max(0.0f, 1.0f - cosTheta * cosTheta));
    float phi = 2.0f * PI * u2;
    float3 tangent;
    float3 bitangent;
    CreateBasisFromNormal(normal, tangent, bitangent);
    return normalize(tangent * (cos(phi) * sinTheta) + bitangent * (sin(phi) * sinTheta) + normal * cosTheta);
}

float3 SampleDielectricBoundaryNormal(float3 opticalNormal, float3 geometricNormal, float3 incidentDirection, float smoothness, float sourceRefraction, float targetRefraction, inout uint rngState)
{
    float3 geometricBoundaryNormal = dot(incidentDirection, geometricNormal) < 0.0f ? geometricNormal : -geometricNormal;
    float3 opticalBoundaryNormal = dot(opticalNormal, geometricBoundaryNormal) >= 0.0f ? opticalNormal : -opticalNormal;
    float roughness = max(0.03f, 1.0f - saturate(smoothness));

    if (smoothness >= 0.9999f)
    {
        float3 reflectedDirection = reflect(incidentDirection, opticalBoundaryNormal);
        float3 transmittedDirection;
        bool canTransmit = RefractSnell(incidentDirection, sourceRefraction, targetRefraction, opticalBoundaryNormal, transmittedDirection);
        bool crossesBoundary = !canTransmit || dot(transmittedDirection, geometricBoundaryNormal) < -1e-5f;
        return dot(reflectedDirection, geometricBoundaryNormal) > 1e-5f && crossesBoundary
            ? opticalBoundaryNormal
            : geometricBoundaryNormal;
    }

    [unroll]
    for (int sampleIndex = 0; sampleIndex < 8; sampleIndex++)
    {
        float3 microfacetNormal = SampleGgxHalfDirection(opticalBoundaryNormal, roughness * roughness, rngState);
        float3 reflectedDirection = reflect(incidentDirection, microfacetNormal);
        float3 transmittedDirection;
        bool canTransmit = RefractSnell(incidentDirection, sourceRefraction, targetRefraction, microfacetNormal, transmittedDirection);
        bool crossesBoundary = !canTransmit || dot(transmittedDirection, geometricBoundaryNormal) < -1e-5f;
        if (dot(reflectedDirection, geometricBoundaryNormal) > 1e-5f && crossesBoundary)
        {
            return microfacetNormal;
        }
    }

    return geometricBoundaryNormal;
}

BrdfSample SampleMaterialBrdf(Ray ray, RayHit hit, inout uint rngState)
{
    BrdfSample sample;
    sample.direction = hit.normal;
    sample.weight = float3(0.0f, 0.0f, 0.0f);
    sample.pdf = 0.0f;

    float specularProbability = GetBrdfSpecularProbability(hit);
    if (rand(rngState) < specularProbability)
    {
        float3 viewDirection = normalize(-ray.direction);
        float3 halfDirection = SampleGgxHalfDirection(hit.normal, GetGgxAlpha(hit), rngState);
        if (dot(viewDirection, halfDirection) <= 0.0f)
        {
            return sample;
        }
        sample.direction = normalize(reflect(ray.direction, halfDirection));
    }
    else
    {
        sample.direction = GetDiffuseScatterDirection(hit.normal, rngState);
    }

    float normalDotDirection = saturate(dot(hit.normal, sample.direction));
    if (normalDotDirection <= 0.0f)
    {
        return sample;
    }

    float3 brdf = EvaluateMaterialBrdf(ray, hit, sample.direction, sample.pdf);
    if (sample.pdf > 1e-6f)
    {
        sample.weight = brdf * normalDotDirection / sample.pdf;
    }
    return sample;
}

bool HasPathEnergy(float3 throughput)
{
    return max(throughput.x, max(throughput.y, throughput.z)) > 0.001f;
}

bool ShouldSampleDirectLight(float3 throughput)
{
    return max(throughput.x, max(throughput.y, throughput.z)) > MinDirectLightThroughput;
}

void ApplySphereRefraction(inout Ray ray, Ray sourceRay, inout RayHit hit, int remainingBounces, bool entering, float sourceRefraction, float targetRefraction, float3 boundaryNormal, inout uint rngState, out int bouncesConsumed, out int mediumTransition, out float mediumDistanceTraveled)
{
    bouncesConsumed = 1;
    mediumTransition = MediumTransitionNone;
    mediumDistanceTraveled = 0.0f;

    if (!entering)
    {
        float3 exitDirection;
        if (RefractSnell(sourceRay.direction, sourceRefraction, targetRefraction, boundaryNormal, exitDirection))
        {
            mediumTransition = MediumTransitionExit;
        }
        else
        {
            exitDirection = reflect(sourceRay.direction, boundaryNormal);
        }

        ray.direction = exitDirection;
        ray.origin = hit.position + (ray.direction * 0.001f);
        return;
    }

    float3 entryDirection;
    if (!RefractSnell(sourceRay.direction, sourceRefraction, targetRefraction, boundaryNormal, entryDirection))
    {
        ray.direction = reflect(sourceRay.direction, boundaryNormal);
        ray.origin = hit.position + (ray.direction * 0.001f);
        return;
    }

    ray.direction = entryDirection;
    ray.origin = hit.position + ray.direction * 0.001f;
    mediumTransition = MediumTransitionEnter;
}

void ApplyPlanarTransmission(inout Ray ray, Ray sourceRay, inout RayHit hit, int remainingBounces, bool entering, float sourceRefraction, float targetRefraction, float3 boundaryNormal, inout uint rngState, out int bouncesConsumed, out int mediumTransition, out float mediumDistanceTraveled)
{
    bouncesConsumed = 1;
    mediumTransition = MediumTransitionNone;
    mediumDistanceTraveled = 0.0f;

    float3 normal = boundaryNormal;

    if (!entering)
    {
        float3 exitDirection;
        if (RefractSnell(sourceRay.direction, sourceRefraction, targetRefraction, normal, exitDirection))
        {
            ray.direction = exitDirection;
            mediumTransition = MediumTransitionExit;
        }
        else
        {
            ray.direction = reflect(sourceRay.direction, normal);
        }

        ray.origin = hit.position + ray.direction * 0.001f;
        return;
    }

    float3 entryDirection;
    if (!RefractSnell(sourceRay.direction, sourceRefraction, targetRefraction, normal, entryDirection))
    {
        ray.direction = reflect(sourceRay.direction, normal);
        ray.origin = hit.position + (ray.direction * 0.001f);
        return;
    }

    entryDirection = normalize(entryDirection);
    Ray insideRay = CreateRay(hit.position + entryDirection * 0.001f, entryDirection);
    float distanceThroughMedium = 0.0f;

    [loop]
    while (bouncesConsumed < remainingBounces)
    {
        float exitDistance = RayMaxDistance;
        float3 exitGeometricNormal = hit.geometricNormal;
        float3 exitOpticalNormal = normal;

        int i;
        [loop]
        for (i = 0; i < _NumMeshes; i++)
        {
            MeshInfo meshInfo = _Meshes[i];
            if (meshInfo.meshIndex != hit.meshIndex)
            {
                continue;
            }

            float meshExitDistance;
            float3 meshExitGeometricNormal;
            float3 meshExitOpticalNormal;
            if (IntersectMeshBvhForExit(insideRay, meshInfo, meshExitDistance, meshExitGeometricNormal, meshExitOpticalNormal) && meshExitDistance < exitDistance)
            {
                exitDistance = meshExitDistance;
                exitGeometricNormal = meshExitGeometricNormal;
                exitOpticalNormal = meshExitOpticalNormal;
            }
        }

        if (exitDistance >= RayMaxDistance)
        {
            mediumDistanceTraveled = ThinTransparentSurfaceDistance;
            ray.direction = insideRay.direction;
            ray.origin = insideRay.origin;
            return;
        }

        if (dot(exitGeometricNormal, insideRay.direction) < 0.0f)
        {
            exitGeometricNormal = -exitGeometricNormal;
            exitOpticalNormal = -exitOpticalNormal;
        }

        float3 exitPoint = insideRay.origin + insideRay.direction * exitDistance;
        RayHit interiorHit = GetNearestIntersectionBounded(insideRay, exitDistance, hit.meshIndex, -1);
        if (!DidHitSky(interiorHit))
        {
            mediumDistanceTraveled = distanceThroughMedium;
            ray.direction = insideRay.direction;
            ray.origin = insideRay.origin;
            mediumTransition = MediumTransitionEnter;
            return;
        }

        distanceThroughMedium += exitDistance;
        bouncesConsumed++;

        float3 exitDirection;
        float3 exitBoundaryNormal = SampleDielectricBoundaryNormal(-exitOpticalNormal, -exitGeometricNormal, insideRay.direction, hit.smoothness, targetRefraction, sourceRefraction, rngState);
        if (RefractSnell(insideRay.direction, targetRefraction, sourceRefraction, exitBoundaryNormal, exitDirection))
        {
            mediumDistanceTraveled = distanceThroughMedium;
            ray.direction = exitDirection;
            ray.origin = exitPoint + (ray.direction * 0.001f);
            return;
        }

        float3 reflectedDirection = normalize(reflect(insideRay.direction, exitBoundaryNormal));
        insideRay = CreateRay(exitPoint + reflectedDirection * 0.001f, reflectedDirection);
    }

    mediumDistanceTraveled = distanceThroughMedium;
    ray.direction = insideRay.direction;
    ray.origin = insideRay.origin;
    mediumTransition = MediumTransitionEnter;
}

int ApplyWaterTransmission(inout Ray ray, Ray sourceRay, RayHit hit, bool entering, float sourceRefraction, float targetRefraction)
{
    float3 normal = dot(sourceRay.direction, hit.normal) < 0.0f ? hit.normal : -hit.normal;
    float3 transmissionDirection;

    bool transmitted = RefractSnell(sourceRay.direction, sourceRefraction, targetRefraction, normal, transmissionDirection);
    ray.direction = transmitted ? transmissionDirection : reflect(sourceRay.direction, hit.normal);
    ray.origin = hit.position + ray.direction * 0.01f;
    return transmitted
        ? (entering ? MediumTransitionEnter : MediumTransitionExit)
        : MediumTransitionNone;
}

ScatterResult CreateScatteredRay(Ray sourceRay, inout RayHit hit, int bounce, int remainingBounces, in MediumStack mediumStack, inout uint rngState)
{
    float3 albedo = GetAlbedo(hit);
    float3 roughNormal = GetRandomizedNormalBasedOnAmount(hit.normal, hit.smoothness, rngState);
    Ray scatteredRay = CreateRay(hit.position + (hit.geometricNormal * 0.001f), reflect(sourceRay.direction, roughNormal));

    if (IsWaterMaterial(hit))
    {
        bool entering;
        float2 transitionIndices = GetBoundaryTransitionIndices(mediumStack, hit, entering);
        float fresnelReflectance = GetFresnelReflectance(sourceRay, hit, transitionIndices.x, transitionIndices.y);
        float transmissionProbability = GetTransmissionAmount(hit) * (1.0f - fresnelReflectance);
        if (rand(rngState) >= transmissionProbability)
        {
            scatteredRay.direction = reflect(sourceRay.direction, roughNormal);
            scatteredRay.origin = hit.position + scatteredRay.direction * 0.01f;
            return CreateScatterResult(scatteredRay, float3(1.0f, 1.0f, 1.0f), 1, MediumTransitionNone);
        }

        int mediumTransition = ApplyWaterTransmission(scatteredRay, sourceRay, hit, entering, transitionIndices.x, transitionIndices.y);
        return CreateScatterResult(scatteredRay, float3(1.0f, 1.0f, 1.0f), 1, mediumTransition);
    }

    if (IsGlassMaterial(hit))
    {
        bool entering;
        float2 transitionIndices = GetBoundaryTransitionIndices(mediumStack, hit, entering);
        float3 boundaryNormal = SampleDielectricBoundaryNormal(hit.normal, hit.geometricNormal, sourceRay.direction, hit.smoothness, transitionIndices.x, transitionIndices.y, rngState);
        float fresnelReflectance = GetFresnelReflectanceForNormal(sourceRay, hit, transitionIndices.x, transitionIndices.y, boundaryNormal);
        float reflectionProbability = GetGlassReflectionProbability(fresnelReflectance, hit);
        float transmissionProbability = saturate(hit.transmission) * (1.0f - reflectionProbability);

        if (rand(rngState) < transmissionProbability)
        {
            int bouncesConsumed = 1;
            int mediumTransition = MediumTransitionNone;
            float mediumDistanceTraveled = 0.0f;
            if (hit.obj_radius > 0.0f)
            {
                ApplySphereRefraction(scatteredRay, sourceRay, hit, remainingBounces, entering, transitionIndices.x, transitionIndices.y, boundaryNormal, rngState, bouncesConsumed, mediumTransition, mediumDistanceTraveled);
            }
            else
            {
                ApplyPlanarTransmission(scatteredRay, sourceRay, hit, remainingBounces, entering, transitionIndices.x, transitionIndices.y, boundaryNormal, rngState, bouncesConsumed, mediumTransition, mediumDistanceTraveled);
            }

            float3 tint = GetMediumSegmentTransmittance(CreateHitMedium(hit), mediumDistanceTraveled);
            return CreateScatterResult(scatteredRay, tint, bouncesConsumed, mediumTransition);
        }

        scatteredRay.direction = reflect(sourceRay.direction, boundaryNormal);
        scatteredRay.origin = hit.position + scatteredRay.direction * 0.001f;
        return CreateScatterResult(scatteredRay, float3(1.0f, 1.0f, 1.0f), 1, MediumTransitionNone);
    }

    BrdfSample brdfSample = SampleMaterialBrdf(sourceRay, hit, rngState);
    scatteredRay.direction = brdfSample.direction;
    float offsetSign = dot(scatteredRay.direction, hit.geometricNormal) >= 0.0f ? 1.0f : -1.0f;
    scatteredRay.origin = hit.position + hit.geometricNormal * (0.001f * offsetSign);
    ScatterResult result = CreateScatterResult(scatteredRay, brdfSample.weight, 1, MediumTransitionNone);
    result.materialPdf = brdfSample.pdf;
    return result;
}

float3 GetDirectLight(Ray ray, RayHit hit, bool softShadows, inout uint rngState)
{
    int samplesPerLight = softShadows ? max(1, _ShadowQuality + 1) : 1;
    return GetLightHittingPoint(ray, hit, samplesPerLight, false, rngState);
}

#if defined(FOG_ENABLED)
float3 GetFogDirectLight(Ray ray, float3 position, bool softShadows, inout uint rngState)
{
    RayHit eventHit = CreateRayHit();
    eventHit.position = position;
    int samplesPerLight = softShadows ? max(1, _ShadowQuality + 1) : 1;
    return GetLightHittingPoint(ray, eventHit, samplesPerLight, true, rngState);
}
#endif

bool ApplyRussianRoulette(inout float3 throughput, int bounce, inout uint rngState)
{
    if (bounce < 2)
    {
        return true;
    }

    float survivalProbability = clamp(max(throughput.x, max(throughput.y, throughput.z)), 0.05f, 0.95f);
    if (rand(rngState) > survivalProbability)
    {
        return false;
    }

    throughput /= survivalProbability;
    return true;
}

bool IsCausticReceiver(RayHit hit)
{
    return !DidHitSky(hit) && !DidHitLight(hit) && hit.materialType == MaterialDiffuse && hit.opacity >= 1.0f;
}

float3 GatherCausticRadiance(RayHit hit)
{
    if (!IsCausticReceiver(hit) || _CausticPhotonAttemptCount <= 0 || _CausticGatherRadius <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    float radiusSquared = _CausticGatherRadius * _CausticGatherRadius;
    float3 photonPower = float3(0.0f, 0.0f, 0.0f);
    int3 centerCell = (int3)floor((hit.position - _CausticGridMin) / _CausticGridCellSize);
    int cellRadius = max(1, (int)ceil(_CausticGatherRadius / _CausticGridCellSize));

    [loop]
    for (int z = -cellRadius; z <= cellRadius; z++)
    {
        [loop]
        for (int y = -cellRadius; y <= cellRadius; y++)
        {
            [loop]
            for (int x = -cellRadius; x <= cellRadius; x++)
            {
                int3 cell = centerCell + int3(x, y, z);
                if (any(cell < 0) || any(cell >= _CausticGridDimensions))
                {
                    continue;
                }

                int cellIndex = cell.x + _CausticGridDimensions.x * (cell.y + _CausticGridDimensions.y * cell.z);
                int photonIndex = _CausticGridCellHeads[cellIndex];
                [loop]
                while (photonIndex >= 0)
                {
                    CausticPhoton photon = _CausticPhotons[photonIndex];
                    float3 offset = photon.position - hit.position;
                    float distanceSquared = dot(offset, offset);
                    if (distanceSquared <= radiusSquared && dot(hit.normal, -photon.incomingDirection) > 0.0f)
                    {
                        float kernelWeight = 2.0f * (1.0f - distanceSquared / radiusSquared);
                        photonPower += photon.power * kernelWeight;
                    }
                    photonIndex = _CausticPhotonNext[photonIndex];
                }
            }
        }
    }

    float normalization = max(1.0f, _CausticPhotonAttemptCount * PI * radiusSquared);
    return photonPower * GetAlbedo(hit) * (_CausticIntensity / normalization);
}

float3 TraceVisibleCausticRadiance(Ray ray, inout uint rngState)
{
    float3 throughput = float3(1.0f, 1.0f, 1.0f);
    MediumStack mediumStack = CreateMediumStack(ray.origin);

    [loop]
    for (int bounce = 0; bounce < _NumBounces; bounce++)
    {
        RayHit hit = GetNearestIntersection(ray);
        throughput *= GetActiveMediumSegmentTransmittance(ray, hit.distance, mediumStack);
        if (!HasPathEnergy(throughput) || DidHitSky(hit) || DidHitLight(hit))
        {
            return float3(0.0f, 0.0f, 0.0f);
        }
        ApplyFiniteMediumExitAfterSegment(mediumStack, ray, hit);

        if (IsCausticReceiver(hit))
        {
            return throughput * GatherCausticRadiance(hit);
        }

        // Photon radiance is visible through specular boundaries, but not after an opaque bounce.
        if (!IsGlassMaterial(hit))
        {
            return float3(0.0f, 0.0f, 0.0f);
        }

        int remainingBounces = _NumBounces - bounce;
        ScatterResult scatter = CreateScatteredRay(ray, hit, bounce, remainingBounces, mediumStack, rngState);
        ApplyMediumTransition(mediumStack, hit, scatter.mediumTransition);
        ray = scatter.ray;
        throughput *= scatter.attenuation;
        bounce += scatter.bouncesConsumed - 1;
    }

    return float3(0.0f, 0.0f, 0.0f);
}

float3 TracePath(Ray ray, inout uint rngState)
{
    float3 radiance = float3(0.0f, 0.0f, 0.0f);
    float3 throughput = float3(1.0f, 1.0f, 1.0f);
    MediumStack mediumStack = CreateMediumStack(ray.origin);
    float3 previousSurfacePosition = float3(0.0f, 0.0f, 0.0f);
    float previousMaterialPdf = 0.0f;
    bool previousDirectLightSampled = false;
    bool previousSoftShadows = false;
    bool canGatherCaustics = true;

    [loop]
    for (int bounce = 0; bounce < _NumBounces; bounce++)
    {
        RayHit hit = GetNearestIntersection(ray);

#if defined(FOG_ENABLED)
        float fogEventDistance;
        if (SampleFogScatteringEvent(ray, hit.distance, rngState, fogEventDistance))
        {
            throughput *= GetActiveMediumSegmentTransmittance(ray, fogEventDistance, mediumStack);
            if (!HasPathEnergy(throughput))
            {
                break;
            }

            float3 eventPosition = ray.origin + ray.direction * fogEventDistance;
            RayHit fogSegmentHit = CreateRayHit();
            fogSegmentHit.distance = fogEventDistance;
            ApplyFiniteMediumExitAfterSegment(mediumStack, ray, fogSegmentHit);
            if (ShouldSampleDirectLight(throughput))
            {
                radiance += throughput * _FogScatteringAlbedo * max(0.0f, _FogInScatteringIntensity)
                    * GetFogDirectLight(ray, eventPosition, bounce == 0, rngState);
            }

            if (_FogMultipleScattering == 0)
            {
                break;
            }

            throughput *= saturate(_FogScatteringAlbedo);
            ray.direction = SampleUniformSphere(rngState);
            ray.origin = eventPosition + ray.direction * 0.001f;
            previousMaterialPdf = 0.0f;
            previousDirectLightSampled = false;
            if (!HasPathEnergy(throughput) || !ApplyRussianRoulette(throughput, bounce, rngState))
            {
                break;
            }
            continue;
        }
#endif

        throughput *= GetActiveMediumSegmentTransmittance(ray, hit.distance, mediumStack);
        if (!HasPathEnergy(throughput))
        {
            break;
        }
        ApplyFiniteMediumExitAfterSegment(mediumStack, ray, hit);

        if (DidHitSky(hit))
        {
            float misWeight = 1.0f;
            if (previousDirectLightSampled && previousMaterialPdf > 0.0f && _EnvironmentLightEnabled != 0)
            {
                float environmentPdf = GetEnvironmentPdf(ray.direction);
                if (environmentPdf > 0.0f)
                {
                    misWeight = PowerHeuristic(previousMaterialPdf, _EnvironmentLightSampleCount * environmentPdf);
                }
            }
            radiance += throughput * GetTerminalHitColor(ray, hit) * misWeight;
            break;
        }

        float3 emission = GetEmission(hit);
        if (DidHitLight(hit))
        {
            float misWeight = 1.0f;
            if (previousDirectLightSampled && previousMaterialPdf > 0.0f)
            {
                int lightSampleCount;
                float lightPdf = GetLightPdfForHit(previousSurfacePosition, hit, previousSoftShadows, lightSampleCount);
                if (lightPdf > 0.0f)
                {
                    misWeight = PowerHeuristic(previousMaterialPdf, lightSampleCount * lightPdf);
                }
            }
            radiance += throughput * emission * misWeight;
            break;
        }

        if (_CausticsEnabled != 0 && canGatherCaustics)
        {
            radiance += throughput * GatherCausticRadiance(hit);
        }

        bool sampledDirectLight = false;
        if (ShouldSampleDirectLight(throughput))
        {
            bool softShadows = bounce == 0;
            float3 directLight = GetDirectLight(ray, hit, softShadows, rngState);
            radiance += throughput * directLight;
            sampledDirectLight = true;
            previousSoftShadows = softShadows;
        }

        int remainingBounces = _NumBounces - bounce;
        ScatterResult scatter = CreateScatteredRay(ray, hit, bounce, remainingBounces, mediumStack, rngState);
        ApplyMediumTransition(mediumStack, hit, scatter.mediumTransition);
        previousSurfacePosition = hit.position;
        previousMaterialPdf = scatter.materialPdf;
        previousDirectLightSampled = sampledDirectLight;
        canGatherCaustics = canGatherCaustics && IsGlassMaterial(hit);
        ray = scatter.ray;
        throughput *= scatter.attenuation;
        bounce += scatter.bouncesConsumed - 1;

        if (!HasPathEnergy(throughput))
        {
            break;
        }

        if (!ApplyRussianRoulette(throughput, bounce, rngState))
        {
            break;
        }
    }

    return radiance;
}

float3 ClampFirefly(float3 radiance)
{
    float maximumLuminance = max(0.0f, _FireflyClamp);
    if (maximumLuminance <= 0.0f)
    {
        return radiance;
    }

    float luminance = dot(radiance, float3(0.2126f, 0.7152f, 0.0722f));
    return luminance > maximumLuminance
        ? radiance * (maximumLuminance / luminance)
        : radiance;
}

#if DEBUG_RENDER
float3 TraceCausticPaths(Ray ray, inout uint rngState)
{
    if (_CausticsEnabled != 0)
    {
        return TraceVisibleCausticRadiance(ray, rngState);
    }

    float3 throughput = float3(1.0f, 1.0f, 1.0f);
    MediumStack mediumStack = CreateMediumStack(ray.origin);
    bool hitDiffuseReceiver = false;
    bool sampledDielectricAfterDiffuse = false;

    [loop]
    for (int bounce = 0; bounce < _NumBounces; bounce++)
    {
        RayHit hit = GetNearestIntersection(ray);
        throughput *= GetActiveMediumSegmentTransmittance(ray, hit.distance, mediumStack);
        if (!HasPathEnergy(throughput))
        {
            break;
        }
        ApplyFiniteMediumExitAfterSegment(mediumStack, ray, hit);

        if (DidHitSky(hit))
        {
            break;
        }

        if (DidHitLight(hit))
        {
            return sampledDielectricAfterDiffuse ? throughput * GetEmission(hit) : float3(0.0f, 0.0f, 0.0f);
        }

        bool isDielectric = IsGlassMaterial(hit) || IsWaterMaterial(hit);
        if (hitDiffuseReceiver && isDielectric)
        {
            sampledDielectricAfterDiffuse = true;
        }
        if (!isDielectric && hit.materialType == MaterialDiffuse)
        {
            hitDiffuseReceiver = true;
        }

        int remainingBounces = _NumBounces - bounce;
        ScatterResult scatter = CreateScatteredRay(ray, hit, bounce, remainingBounces, mediumStack, rngState);
        ApplyMediumTransition(mediumStack, hit, scatter.mediumTransition);
        ray = scatter.ray;
        throughput *= scatter.attenuation;
        bounce += scatter.bouncesConsumed - 1;

        if (!HasPathEnergy(throughput) || !ApplyRussianRoulette(throughput, bounce, rngState))
        {
            break;
        }
    }

    return float3(0.0f, 0.0f, 0.0f);
}

float3 GetDebugRenderColor(Ray ray, inout uint rngState)
{
    if (_DebugRenderMode == DebugCaustics)
    {
        return TraceCausticPaths(ray, rngState);
    }

    RayHit hit = GetNearestIntersection(ray);

    if (_DebugRenderMode == DebugAccelerationStructures)
    {
        float topLevelActive = _NumTopLevelBvhNodes > 0 ? 1.0f : 0.0f;
        float shadowActive = _NumShadowBvhNodes > 0 ? 1.0f : 0.0f;
        float shadowScale = saturate(_NumShadowBvhNodes / 255.0f);
        float topLevelScale = saturate(_NumTopLevelBvhNodes / 255.0f);

        if (DidHitSky(hit))
        {
            return float3(topLevelScale, shadowScale, 0.0f);
        }

        if (DidHitLight(hit))
        {
            return float3(1.0f, shadowActive * 0.5f, 0.0f);
        }

        if (IsGlassMaterial(hit))
        {
            return float3(topLevelActive * 0.2f, 0.25f + shadowActive * 0.75f, 1.0f);
        }

        if (hit.meshIndex >= 0)
        {
            return float3(1.0f, 0.2f + shadowActive * 0.8f, topLevelActive * 0.2f);
        }

        return float3(topLevelActive, shadowActive, 0.15f);
    }

    if (_DebugRenderMode == DebugTerrainCells)
    {
        if (DidHitSky(hit) || hit.objectIndex != -2)
        {
            return float3(0.0f, 0.0f, 0.0f);
        }

        float2 cellUv = frac(hit.uv * max(1.0f, (float)_TerrainCellResolution));
        float2 distanceToBoundary = min(cellUv, 1.0f - cellUv);
        float boundary = 1.0f - smoothstep(0.015f, 0.035f, min(distanceToBoundary.x, distanceToBoundary.y));
        return float3(cellUv, boundary);
    }

    if (DidHitSky(hit))
    {
        return DebugHitDistance == _DebugRenderMode ? float3(1.0f, 1.0f, 1.0f) : float3(0.0f, 0.0f, 0.0f);
    }

    if (_DebugRenderMode == DebugNormals)
    {
        return hit.normal * 0.5f + 0.5f;
    }

    if (_DebugRenderMode == DebugAlbedo)
    {
        return GetAlbedo(hit);
    }

    if (_DebugRenderMode == DebugEmission)
    {
        return saturate(GetEmission(hit));
    }

    if (_DebugRenderMode == DebugDirectLight)
    {
        return saturate(GetDirectLight(ray, hit, true, rngState));
    }

    if (_DebugRenderMode == DebugHitDistance)
    {
        return saturate(hit.distance / 25.0f).xxx;
    }

    if (_DebugRenderMode == DebugGlassScatter)
    {
        if (!IsGlassMaterial(hit))
        {
            return GetAlbedo(hit) * 0.12f;
        }

        MediumStack mediumStack = CreateMediumStack(ray.origin);
        bool entering;
        float2 transitionIndices = GetBoundaryTransitionIndices(mediumStack, hit, entering);
        float fresnelReflectance = GetFresnelReflectance(ray, hit, transitionIndices.x, transitionIndices.y);
        float reflectionProbability = GetGlassReflectionProbability(fresnelReflectance, hit);
        if (rand(rngState) < reflectionProbability)
        {
            return float3(1.0f, reflectionProbability, 0.0f);
        }

        return float3(0.0f, reflectionProbability, GetTransmissionAmount(hit) * hit.transmission);
    }

    if (_DebugRenderMode == DebugBounceCount || _DebugRenderMode == DebugThroughput)
    {
        float3 throughput = float3(1.0f, 1.0f, 1.0f);
        int completedBounces = 0;
        MediumStack mediumStack = CreateMediumStack(ray.origin);

        [loop]
        for (int bounce = 0; bounce < _NumBounces; bounce++)
        {
            hit = GetNearestIntersection(ray);
            throughput *= GetActiveMediumSegmentTransmittance(ray, hit.distance, mediumStack);
            if (!HasPathEnergy(throughput))
            {
                break;
            }
            ApplyFiniteMediumExitAfterSegment(mediumStack, ray, hit);

            if (DidHitSky(hit) || DidHitLight(hit))
            {
                break;
            }

            int remainingBounces = _NumBounces - bounce;
            ScatterResult scatter = CreateScatteredRay(ray, hit, bounce, remainingBounces, mediumStack, rngState);
            ApplyMediumTransition(mediumStack, hit, scatter.mediumTransition);
            ray = scatter.ray;
            throughput *= scatter.attenuation;

            completedBounces += scatter.bouncesConsumed;
            bounce += scatter.bouncesConsumed - 1;

            if (!HasPathEnergy(throughput))
            {
                break;
            }

            if (!ApplyRussianRoulette(throughput, bounce, rngState))
            {
                break;
            }
        }

        if (_DebugRenderMode == DebugBounceCount)
        {
            return (completedBounces / (float)max(1, _NumBounces)).xxx;
        }

        return saturate(throughput);
    }

    return TracePath(ray, rngState);
}
#endif // DEBUG_RENDER

float GetFeatureIdentity(RayHit hit)
{
    if (DidHitSky(hit))
    {
        return 0.0f;
    }

    if (hit.materialType == MaterialWater)
    {
        return 1.0f;
    }

    if (hit.lightIndex >= 0)
    {
        return 2000000.0f + hit.lightIndex;
    }

    return hit.obj_radius > 0.0f
        ? 2.0f + hit.objectIndex
        : 1000000.0f + hit.meshIndex;
}

float3 GetFeatureIdentityDebugColor(float identity)
{
    if (identity <= 0.0f)
    {
        return float3(0.0f, 0.0f, 0.0f);
    }

    uint hash = Hash((uint)identity);
    return float3(
        (hash & 255u) / 255.0f,
        ((hash >> 8) & 255u) / 255.0f,
        ((hash >> 16) & 255u) / 255.0f);
}

// Feature buffers use an unjittered pinhole primary ray. This makes their surface decisions
// stable across stochastic path samples; beauty remains independently sampled and accumulated.
void WriteDenoiserFeatures(uint2 pixel, uint width, uint height)
{
    float2 uv = ((float2(pixel) + 0.5f) / float2(width, height)) * 2.0f - 1.0f;
    RayHit hit = GetNearestIntersection(CreateCameraRay(uv));
    if (DidHitSky(hit))
    {
        FeatureNormal[pixel] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        FeatureAlbedo[pixel] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        FeatureDepth[pixel] = 0.0f;
        FeatureIdentity[pixel] = 0.0f;
        FeatureValidity[pixel] = 0.0f;
        return;
    }

    // Normal alpha is a reactive flag, preserving the existing eight-UAV production contract.
    // Transmission boundaries get a short temporal history. Other view-dependent paths remain
    // fully reactive because primary-surface motion cannot represent their radiance motion.
    float reactive = (IsGlassMaterial(hit) || hit.materialType == MaterialWater) ? 0.5f
        : (hit.materialType == MaterialMetal || hit.lightIndex >= 0 || hit.smoothness > 0.8f) ? 1.0f : 0.0f;
    FeatureNormal[pixel] = float4(normalize(hit.normal), reactive);
    FeatureAlbedo[pixel] = float4(saturate(GetAlbedo(hit)), 1.0f);
    FeatureDepth[pixel] = hit.distance;
    FeatureIdentity[pixel] = GetFeatureIdentity(hit);
    FeatureValidity[pixel] = 1.0f;
}

// Narkowicz 2015 ACES filmic tone mapping approximation. Maps the open-ended
// HDR radiance range into [0,1] so bright values roll off smoothly instead of
// clipping hard to white.
float3 ACESFilmicToneMap(float3 color)
{
    const float a = 2.51f;
    const float b = 0.03f;
    const float c = 2.43f;
    const float d = 0.59f;
    const float e = 0.14f;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

