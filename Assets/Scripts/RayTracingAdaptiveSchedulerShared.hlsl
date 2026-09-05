// Adaptive resources intentionally stay independent of the renderer's scene declarations.
#ifndef RAY_TRACING_ADAPTIVE_TRACE
RWTexture2D<float4> Result;
RWTexture2D<float4> AccumulationResult;
RWTexture2D<float4> Beauty;
#endif
RWTexture2D<float4> AdaptiveSamplingState;
RWTexture2D<float4> AdaptiveSamplingM2;
Texture2D<float4> AdaptiveBootstrapPriority;
RWStructuredBuffer<uint4> AdaptiveGroupState;
#ifdef RAY_TRACING_ADAPTIVE_TRACE
StructuredBuffer<uint4> AdaptiveGroupInfo;
#else
RWStructuredBuffer<uint4> AdaptiveGroupInfo;
#endif
StructuredBuffer<uint4> AdaptiveProbeGroups;
RWStructuredBuffer<uint> AdaptiveGroupBucket;
RWStructuredBuffer<uint> AdaptiveGroupExtraDemand;
RWStructuredBuffer<uint> AdaptiveRawBucketDemand;
RWStructuredBuffer<uint> AdaptiveWorkListMetadata;

#define AdaptiveMetadataWorkItemCount 0u
#define AdaptiveMetadataPrioritySum 1u
#define AdaptiveMetadataAssignedPaths 2u
#define AdaptiveMetadataWorkListOverflow 3u
#define AdaptiveMetadataBootstrapPixels 4u
#define AdaptiveMetadataRequestedPaths 5u
#define AdaptiveMetadataRetiredPaths 6u
#define AdaptiveMetadataBootstrapPaths 7u
#define AdaptiveMetadataPathCountMin 8u
#define AdaptiveMetadataPathCountMax 9u
#define AdaptiveMetadataPathCountSum 10u
#define AdaptiveMetadataUncertaintySum 11u
#define AdaptiveMetadataUncertaintyMax 12u
#define AdaptiveMetadataFullResolutionPaths 13u
#define AdaptiveMetadataGuidancePaths 14u
#define AdaptiveMetadataCoarseGroups 15u
#define AdaptiveMetadataBucketPopulationStart 16u
#define AdaptiveMetadataBucketAdmittedPathsStart 32u
#define AdaptiveMetadataBucketBudgetStart 48u

#ifndef RAY_TRACING_ADAPTIVE_TRACE
int _NumberOfPasses;
#endif
int _AdaptiveSamplingMinSamples;
uint _AdaptiveGroupWidth;
uint _AdaptiveGroupHeight;
uint _AdaptiveGroupCount;
int _AdaptiveCaptureDiagnostics;
uint _AdaptiveScheduleRotation;
float _AdaptiveHighestBucketSampleRate;
uint _AdaptiveBucketCount;
uint _AdaptiveMaxPathsPerPixel;
uint _AdaptiveSampleLayer;
float _AdaptiveNormalizePriorityByLuminance;
int _UseAdaptiveBootstrapPriority;
uint _AdaptiveBootstrapGroupDivisor;

#ifndef RAY_TRACING_ADAPTIVE_TRACE
uint Hash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}
#endif
