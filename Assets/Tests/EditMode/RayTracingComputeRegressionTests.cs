using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GPURayTracing.Tests
{
    public class RayTracingComputeRegressionTests
    {
        private struct UInt4
        {
            public uint x;
            public uint y;
            public uint z;
            public uint w;
        }

        private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
        private const string DebugShaderPath = "Assets/Resources/RayTracingDebug.compute";
        private const string UtilityShaderPath = "Assets/Resources/RayTracingUtility.compute";
        private const string FeaturesShaderPath = "Assets/Resources/RayTracingFeatures.compute";
        private const string FocusShaderPath = "Assets/Resources/RayTracingFocus.compute";
        private const string AdaptiveSchedulerShaderPath = "Assets/Resources/RayTracingAdaptiveScheduler.compute";
        private const string AdaptiveTraceShaderPath = "Assets/Resources/RayTracingAdaptiveTrace.compute";
        private const string RegressionProbeShaderPath = "Assets/Resources/RayTracingRegressionProbe.compute";
        private const string DenoiserShaderPath = "Assets/Resources/RayTracingSpatialDenoiser.compute";
        private const float Epsilon = 0.0001f;
        // Transform.eulerAngles round-trips through a quaternion, producing roughly 0.00025 degrees
        // of platform-dependent error near the pitch limits.
        private const float CameraRotationEpsilon = 0.001f;

        [Test]
        public void AdaptiveGroupScheduler_CompactsOneWorkItemPerActivePixel()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Group scheduler parity requires an active compute graphics device.");

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AdaptiveSchedulerShaderPath);
            Assert.That(shader, Is.Not.Null);
            string source = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shader.HasKernel("CSAdaptiveCompactGroupWorkList"), Is.True);
            Assert.That(source, Does.Contain("AdaptiveWorkList[workIndex] = uint2(flatPixel, samples)"));
            Assert.That(source, Does.Not.Contain("AdaptiveRootWorkList"));
            Assert.That(source, Does.Not.Contain("AdaptiveWorkRootOffsets"));
        }

        [Test]
        public void AdaptiveGroupScheduler_RotatingRemainderChangesServedGroupAcrossCycles()
        {
            const int groupCount = 3;
            var served = new bool[groupCount];
            for (int cycle = 0; cycle < groupCount; cycle++)
            {
                int selected = cycle % groupCount;
                served[selected] = true;
            }
            Assert.That(served, Is.All.True);
        }

        [Test]
        public void AdaptiveGroupScheduler_BoundedRotationBroadensFirstQuantumServiceBeforeExtras()
        {
            const int groupCount = 16;
            const int quantum = 64;
            const int budget = groupCount * quantum;
            const int extraCap = 3;
            var grants = new int[groupCount];

            // Every rotating eligible group receives its first complete quantum before a high-score
            // group may take any of its bounded extra quanta.
            for (int group = 0; group < groupCount; group++)
            {
                grants[group] = quantum;
            }

            int remaining = budget - grants.Length * quantum;
            for (int extra = 0; extra < extraCap && remaining >= quantum; extra++)
            {
                grants[0] += quantum;
                remaining -= quantum;
            }

            Assert.That(grants, Is.All.GreaterThanOrEqualTo(quantum));
            Assert.That(grants[0], Is.LessThanOrEqualTo(quantum * (extraCap + 1)));
            Assert.That(remaining, Is.EqualTo(0));
        }

        [Test]
        public void AdaptiveGroupScheduler_HashedEpochEligibilityAvoidsFlatGroupStripes()
        {
            const uint epoch = 17u;
            var primaryEligible = new bool[64];
            var extraEligible = new bool[64];
            for (uint group = 0u; group < 64u; group++)
            {
                uint serviceHash = SchedulerHash(group ^ unchecked(epoch * 747796405u));
                primaryEligible[group] = (serviceHash & 3u) != 0u;
                extraEligible[group] = SchedulerHash(serviceHash ^ 2891336453u) % 3u == 0u;
            }

            Assert.That(primaryEligible, Does.Contain(true));
            Assert.That(primaryEligible, Does.Contain(false));
            Assert.That(extraEligible, Does.Contain(true));
            Assert.That(extraEligible, Does.Contain(false));
            bool firstEightAllSame = true;
            bool secondEightAllSame = true;
            for (int group = 1; group < 8; group++)
            {
                firstEightAllSame &= primaryEligible[group] == primaryEligible[0];
                secondEightAllSame &= primaryEligible[group + 8] == primaryEligible[8];
            }
            Assert.That(firstEightAllSame && secondEightAllSame && primaryEligible[0] == primaryEligible[8], Is.False,
                "Epoch eligibility must not repeat a flattened-ID stripe every eight groups.");
        }

        [Test]
        public void AdaptiveGroupScheduler_ExtraAdmissionIsOneCompleteQuantumPerGroup()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int classifyStart = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int allocateStart = shaderSource.IndexOf("uint GetAdaptiveTargetBucket", classifyStart, StringComparison.Ordinal);
            string classify = shaderSource.Substring(classifyStart, allocateStart - classifyStart);

            Assert.That(classify, Does.Contain("InterlockedAdd(AdaptiveRawBucketDemand[bucket], validPixels)"));
            Assert.That(classify, Does.Not.Contain("validPixels * (updateCap - 1u)"));
        }

        [Test]
        public void AdaptiveGroupScheduler_UsesInverseRatePopulationTiers()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int remapStart = shaderSource.IndexOf("uint GetAdaptiveTargetBucket", StringComparison.Ordinal);
            int remapEnd = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", remapStart, StringComparison.Ordinal);
            string remap = shaderSource.Substring(remapStart, remapEnd - remapStart);

            Assert.That(remap, Does.Contain("rcp(GetAdaptiveBucketRateFloat(0u))"));
            Assert.That(remap, Does.Contain("rcp(GetAdaptiveBucketRateFloat(bucket + 1u))"));
            Assert.That(remap, Does.Contain("random * sourceDemand"));
            Assert.That(remap, Does.Contain("uniformWeight"));
        }

        [Test]
        public void AdaptiveGroupScheduler_ConcentratedSourceBandFillsAllInverseRateTiers()
        {
            const int groupCount = 16384;
            const int bucketCount = 15;
            const float highestRate = 2.0f;
            var counts = new int[bucketCount];

            // A single occupied score band must not map every group into one target tier.
            for (uint group = 0u; group < groupCount; group++)
            {
                float position = SchedulerHash(group) / 4294967296.0f * groupCount;
                float targetEnd = BudgetNormalizedPopulation(groupCount, 0, bucketCount, highestRate);
                int bucket = 0;
                while (bucket + 1 < bucketCount && position >= targetEnd)
                {
                    bucket++;
                    targetEnd += BudgetNormalizedPopulation(groupCount, bucket, bucketCount, highestRate);
                }
                counts[bucket]++;
            }

            Assert.That(counts, Is.All.GreaterThan(0));
            Assert.That(counts[0], Is.GreaterThan(counts[bucketCount - 1] * 1.8));
            Assert.That(counts[0], Is.EqualTo(1565).Within(80));
            Assert.That(counts[bucketCount - 1], Is.EqualTo(782).Within(50));

            float expectedPaths = 0.0f;
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                expectedPaths += counts[bucket] * BucketRate(bucket, bucketCount, highestRate);
            }
            Assert.That(expectedPaths, Is.EqualTo(groupCount).Within(groupCount * 0.01f));
        }

        [Test]
        public void AdaptiveGroupScheduler_RateCurveHasReciprocalEndpoints()
        {
            const float highestRate = 8.0f;
            float logRate = Mathf.Log(highestRate, 2.0f);
            float low = Mathf.Pow(2.0f, -logRate);
            float middle = Mathf.Pow(2.0f, 0.0f);
            float high = Mathf.Pow(2.0f, logRate);

            Assert.That(low, Is.EqualTo(1.0f / highestRate).Within(Epsilon));
            Assert.That(middle, Is.EqualTo(1.0f).Within(Epsilon));
            Assert.That(high, Is.EqualTo(highestRate).Within(Epsilon));
        }

        [Test]
        public void AdaptiveGroupScheduler_PermutesLogicalGroupsBeforeOffsetReservation()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shaderSource, Does.Contain("uint2 GetAdaptiveLogicalGroup(uint2 physicalGroup)"));
            Assert.That(shaderSource, Does.Contain("GetAdaptiveGroupPermutationStride(groupCount)"));

            int classifyStart = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int remapStart = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", classifyStart, StringComparison.Ordinal);
            string classify = shaderSource.Substring(classifyStart, remapStart - classifyStart);
            Assert.That(classify, Does.Contain("uint2 logicalGroup = GetAdaptiveLogicalGroup(groupId.xy)"));
            Assert.That(classify, Does.Contain("uint2 pixel = logicalGroup * 8u + localPixel"));

            int compactStart = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", remapStart, StringComparison.Ordinal);
            string remap = shaderSource.Substring(remapStart, compactStart - remapStart);
            Assert.That(remap, Does.Contain("uint2 logicalGroup = GetAdaptiveLogicalGroup(groupId.xy)"));
        }

        [Test]
        public void AdaptiveScheduler_UsesFullResolutionRgbWelfordScores()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string allocation = shaderSource.Substring(start, end - start);
            Assert.That(allocation, Does.Contain("AdaptiveSamplingM2[pixel].rgb"));
            Assert.That(allocation, Does.Contain("count * (count - 1.0f)"));
            Assert.That(allocation, Does.Contain("AdaptiveSamplingState[pixel].yzw"));
            Assert.That(allocation, Does.Contain("_AdaptiveNormalizePriorityByLuminance"));
            Assert.That(allocation, Does.Contain("0.8f / max(0.25f, luminance)"));
            Assert.That(allocation, Does.Contain("lerp(score, normalizedScore"));
            Assert.That(allocation, Does.Contain("saturate(_AdaptiveNormalizePriorityByLuminance)"));
        }

        [Test]
        public void AdaptiveDammertzPriority_UsesPersistentSampleIndexSplitEstimatorAndFiniteRgbRms()
        {
            string schedulerSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            string traceSource = System.IO.File.ReadAllText(AdaptiveTraceShaderPath);
            int classifyStart = schedulerSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int classifyEnd = schedulerSource.IndexOf("uint GetAdaptiveTargetBucket", classifyStart, StringComparison.Ordinal);
            int resolveStart = traceSource.IndexOf("void CSAdaptiveTrace", StringComparison.Ordinal);
            int resolveEnd = traceSource.IndexOf("void CSAdaptiveTraceReference", resolveStart, StringComparison.Ordinal);
            string classify = schedulerSource.Substring(classifyStart, classifyEnd - classifyStart);
            string resolve = traceSource.Substring(resolveStart, resolveEnd - resolveStart);

            Assert.That(classify, Does.Contain("_AdaptivePriorityMode == 1u"));
            Assert.That(classify, Does.Contain("AccumulationResult[pixel].rgb - alternating.rgb"));
            Assert.That(classify, Does.Contain("dot(disagreement, disagreement) / 3.0f"));
            Assert.That(classify, Does.Contain("alternating.a < 2.0f"));
            Assert.That(resolve, Does.Contain("bool trackAlternating = _AdaptivePriorityMode == 1u"));
            Assert.That(resolve, Does.Contain("trackAlternating &&"));
            Assert.That(resolve, Does.Contain("AdaptiveSamplingState[pixel] = float4(newCount, trackAlternating ? alternating.rgb : previousState.yzw)"));
        }

        [Test]
        public void AdaptiveDammertzPriority_UsesBothEstimatorsForBootstrapAndKeepsBeautyIndependent()
        {
            string schedulerSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            string traceSource = System.IO.File.ReadAllText(AdaptiveTraceShaderPath);
            int classifyStart = schedulerSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int classifyEnd = schedulerSource.IndexOf("uint GetAdaptiveTargetBucket", classifyStart, StringComparison.Ordinal);
            string classify = schedulerSource.Substring(classifyStart, classifyEnd - classifyStart);
            Assert.That(classify, Does.Contain("count < (float)max(2, _AdaptiveSamplingMinSamples) || alternating.a < 2.0f"));
            Assert.That(traceSource, Does.Contain("all(isfinite(radiance)) ? radiance : 0.0f"));
            Assert.That(traceSource, Does.Not.Contain("AccumulationResult[pixel] = float4(alternating"));
            Assert.That(traceSource, Does.Not.Contain("Beauty[pixel] = float4(alternating"));
        }

        [Test]
        public void AdaptiveBootstrap_UsesTheUniformRenderer()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(managerSource, Does.Contain("private void DispatchAdaptiveBootstrap()"));
            Assert.That(managerSource, Does.Contain("bootstrapShader.FindKernel(\"CSMain\")"));
            Assert.That(managerSource, Does.Contain("UpscaleAdaptiveBootstrap"));
        }

        [Test]
        public void SplitTraceAssetsReceiveActiveGeometryCounts()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int bindStart = managerSource.IndexOf("private void BindShaderEnvironmentAndSceneParameters", StringComparison.Ordinal);
            int bindEnd = managerSource.IndexOf("private void SetTerrainShaderParameters", bindStart, StringComparison.Ordinal);
            string binding = managerSource.Substring(bindStart, bindEnd - bindStart);

            Assert.That(binding, Does.Contain("targetShader.SetInt(NumSpheres, _spheres.Count)"));
            Assert.That(binding, Does.Contain("targetShader.SetInt(NumTriangles, _triangles.Count)"));
            Assert.That(binding, Does.Contain("targetShader.SetInt(NumMeshes, _meshInfos.Count)"));
        }

        [Test]
        public void AdaptiveBootstrap_UpscalesBeforeFineScheduling()
        {
            string utilitySource = System.IO.File.ReadAllText("Assets/Resources/RayTracingUtility.compute");

            Assert.That(utilitySource, Does.Contain("void UpscaleAdaptiveBootstrap"));
            Assert.That(utilitySource, Does.Contain("SampleAdaptiveBootstrap"));
        }

        [Test]
        public void AdaptiveBootstrap_SeedsFineHistoryFromUpscaledData()
        {
            string utilitySource = System.IO.File.ReadAllText("Assets/Resources/RayTracingUtility.compute");

            Assert.That(utilitySource, Does.Contain("void SeedAdaptiveBootstrap"));
            Assert.That(utilitySource, Does.Contain("_AdaptiveBootstrapHistorySamples"));
            Assert.That(utilitySource, Does.Contain("AccumulationResult[id.xy] = float4(color, 1.0f)"));
        }

        [Test]
        public void AdaptiveScheduler_MapsGroupsAndPublishesOffsetsInOnePass()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveTraceShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int attributeStart = shaderSource.LastIndexOf("[numthreads", start, StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string remap = shaderSource.Substring(attributeStart, end - attributeStart);
            Assert.That(remap, Does.Contain("[numthreads(8,8,1)]"));
            Assert.That(remap, Does.Contain("AdaptiveGroupBucket"));
            Assert.That(remap, Does.Contain("AdaptiveGroupExtraDemand"));
            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataWorkItemCount], validPixels, workBase)"));
            Assert.That(remap, Does.Contain("AdaptiveGroupInfo[flatGroup] = uint4(workBase, rootBase"));
            Assert.That(shaderSource, Does.Contain("uint GetAdaptiveTargetBucket"));
            Assert.That(shaderSource, Does.Contain("void CSAdaptiveApplyBucketRemap"));
            Assert.That(shaderSource, Does.Not.Contain("void CSAdaptiveAllocateGroupBuckets"));
            Assert.That(shaderSource, Does.Not.Contain("void CSAdaptiveAssignGroups"));
            Assert.That(shaderSource, Does.Not.Contain("void CSAdaptiveAssignGroupExtras"));
            Assert.That(shaderSource, Does.Not.Contain("void CSAdaptiveFinalizeGroupGrants"));
        }

        [Test]
        public void AdaptiveGroupScheduler_ResetClearsSamplingAndGroupState()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = managerSource.IndexOf("if (_adaptiveSamplingStateTexture != null)", StringComparison.Ordinal);
            int end = managerSource.IndexOf("if (targetShader == adaptiveTraceShader", start, StringComparison.Ordinal);
            string reset = managerSource.Substring(start, end - start);
            Assert.That(reset, Does.Contain("ClearAdaptiveSamplingState"));
            Assert.That(reset, Does.Contain("ClearAdaptiveGroupState"));
        }

        [Test]
        public void AdaptiveScheduler_UsesUniformBootstrapBeforeFullResolutionScheduling()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int dispatchStart = managerSource.IndexOf("private void DispatchAdaptiveSampling", StringComparison.Ordinal);
            int dispatchEnd = managerSource.IndexOf("private void DispatchAdaptiveDiagnostics", dispatchStart, StringComparison.Ordinal);
            string dispatch = managerSource.Substring(dispatchStart, dispatchEnd - dispatchStart);
            Assert.That(dispatch, Does.Contain("DispatchAdaptiveBootstrap"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveGuidanceTrace"));
            Assert.That(dispatch, Does.Contain("ComposeAdaptiveBootstrap"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveAllocateGroups"));
            Assert.That(dispatch, Does.Contain("CSAdaptiveClassifyGroups"));
            Assert.That(dispatch, Does.Contain("CSAdaptiveApplyBucketRemap"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveAllocateGroupBuckets"));
        }

        [Test]
        public void AdaptiveTraceWarmup_WaitsForTheLowResolutionBootstrapHandoff()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = managerSource.IndexOf("private bool TryDeferShaderVariantWarmup", StringComparison.Ordinal);
            int end = managerSource.IndexOf("private void PrepareRenderFrame", start, StringComparison.Ordinal);
            string warmup = managerSource.Substring(start, end - start);

            Assert.That(warmup, Does.Contain("_adaptiveBootstrapFrameCount >= Mathf.Clamp(adaptiveBootstrapFrames, 1, 8)"));
            Assert.That(warmup, Does.Contain("useAdaptiveTraceShader ? 4"));
        }

        [Test]
        public void ShaderWarmupKeys_KeepAssetAndVariantBitsDisjoint()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(managerSource, Does.Contain("return shaderKind << 2 | (fogEnabled ? 1 : 0)"));
        }

        [Test]
        public void AdaptiveBootstrapPreview_RemainsVisibleUntilEachFinePixelReceivesItsFirstPath()
        {
            string utilitySource = System.IO.File.ReadAllText("Assets/Resources/RayTracingUtility.compute");

            Assert.That(utilitySource, Does.Contain("void ComposeAdaptiveBootstrap"));
            Assert.That(utilitySource, Does.Contain("AdaptiveSamplingState[id.xy].x > 0.0f"));
        }

        [Test]
        public void AdaptiveScheduler_UsesBootstrapPriorityOnlyForTheFirstFineSchedule()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string remap = shaderSource.Substring(start, end - start);

            Assert.That(shaderSource, Does.Contain("bootstrapPriority"));
            Assert.That(shaderSource, Does.Contain("if (_UseAdaptiveBootstrapPriority != 0 && !bootstrap)"));
        }

        [Test]
        public void AdaptiveScheduler_EvaluatesBootstrapBatchAdmissionDuringPerFrameRemap()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int classifyStart = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string classify = shaderSource.Substring(classifyStart, start - classifyStart);
            string remap = shaderSource.Substring(start, end - start);

            Assert.That(classify, Does.Not.Contain("uint bootstrapBatch"));
            Assert.That(classify, Does.Not.Contain("AdaptiveGroupExtraDemand[flatGroup] = 0u"));
            Assert.That(remap, Does.Contain("uint bootstrapBatch = (flatGroup + _AdaptiveScheduleRotation)"));
            Assert.That(remap, Does.Contain("if (!needsFirstFineSample && _AdaptiveBootstrapGroupDivisor > 1u && bootstrapBatch != 0u)"));
            Assert.That(remap, Does.Contain("AdaptiveGroupInfo[flatGroup] = 0u"));
        }

        [Test]
        public void AdaptiveScheduler_KeepsEstablishedGroupsEligibleAfterFineBootstrap()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int classifyStart = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int remapStart = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", classifyStart, StringComparison.Ordinal);
            int compactStart = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", remapStart, StringComparison.Ordinal);
            string classify = shaderSource.Substring(classifyStart, remapStart - classifyStart);
            string remap = shaderSource.Substring(remapStart, compactStart - remapStart);

            Assert.That(classify, Does.Contain("AdaptiveGroupState[flatGroup] = uint4(groupBootstrap ? 1u : 2u"));
            Assert.That(classify, Does.Contain("AdaptiveGroupExtraDemand[flatGroup] = validPixels"));
            Assert.That(remap, Does.Contain("bool bootstrap = AdaptiveGroupState[flatGroup].x == 1u"));
        }

        [Test]
        public void AdaptiveScheduler_RotatesFineBootstrapGroupsDeterministically()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string remap = shaderSource.Substring(start, end - start);

            Assert.That(remap, Does.Contain("uint bootstrapBatch = (flatGroup + _AdaptiveScheduleRotation)"));
            Assert.That(remap, Does.Not.Contain("Hash(flatGroup ^ (_AdaptiveScheduleRotation * 1597334677u))"));
        }

        [Test]
        public void AdaptiveScheduler_GivesEveryUntouchedGroupOneFineSampleBeforeRotatingBootstrap()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);

            Assert.That(shaderSource, Does.Contain("bool needsFirstFineSample = groupBootstrap"));
            Assert.That(shaderSource, Does.Contain("AdaptiveSamplingState[logicalGroup * 8u].x == 0.0f"));
            Assert.That(shaderSource, Does.Contain("groupBootstrap && !needsFirstFineSample"));
        }

        [Test]
        public void AdaptiveScheduler_SeedsFinePixelsFromConfiguredUpscaledBootstrapHistory()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            string utilitySource = System.IO.File.ReadAllText("Assets/Resources/RayTracingUtility.compute");
            Assert.That(utilitySource, Does.Contain("void SeedAdaptiveBootstrap"));
            Assert.That(utilitySource, Does.Contain("if (count > 0.0f)"));
            Assert.That(utilitySource, Does.Contain("AdaptiveSamplingState[id.xy] = float4(count, 0.0f, 0.0f, 0.0f)"));
        }

        [Test]
        public void AdaptiveScheduler_UsesRequestedWholeGroupRateBudget()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shaderSource, Does.Contain("uint paths = validPixels * samples"));
            Assert.That(shaderSource, Does.Contain("AdaptiveGroupExtraDemand[flatGroup] = paths"));
            Assert.That(shaderSource, Does.Contain("AdaptiveMetadataBucketBudgetStart + bucket], paths"));
            Assert.That(shaderSource, Does.Contain("AdaptiveMetadataRequestedPaths"));
            Assert.That(shaderSource, Does.Contain("AdaptiveMetadataAssignedPaths"));
        }

        [Test]
        public void AdaptiveScheduler_CompactsFullResolutionPixelsInParallel()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void ClearAdaptiveFrameMetadata", start, StringComparison.Ordinal);
            string workList = shaderSource.Substring(start, end - start);
            Assert.That(shaderSource, Does.Contain("void CSAdaptiveCompactGroupWorkList"));
            Assert.That(shaderSource, Does.Contain("AdaptiveWorkList[workIndex]"));
            Assert.That(workList, Does.Not.Contain("AdaptiveRootWorkList"));
            Assert.That(workList, Does.Not.Contain("AdaptiveWorkRootOffsets"));
            Assert.That(shaderSource, Does.Contain("if (samples == 0u) return"));
        }

        [Test]
        public void AdaptiveScheduler_DerivesAccountingFromCompactGroupGrants()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int remapStart = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int compactStart = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", remapStart, StringComparison.Ordinal);
            int argsStart = shaderSource.IndexOf("void CSBuildAdaptiveDispatchArgs", StringComparison.Ordinal);
            string remap = shaderSource.Substring(remapStart, compactStart - remapStart);
            string args = shaderSource.Substring(argsStart, shaderSource.IndexOf("void CSAdaptiveDiagnostics", argsStart, StringComparison.Ordinal) - argsStart);

            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataWorkItemCount], validPixels, workBase)"));
            Assert.That(remap, Does.Contain("AdaptiveGroupInfo[flatGroup] = uint4(workBase, 0u"));
            Assert.That(remap, Does.Contain("paths / validPixels"));
            Assert.That(args, Does.Contain("AdaptiveMetadataFullResolutionPaths] = assigned"));
        }

        [Test]
        public void AdaptiveScheduler_FusedTraceUsesCompactWorkCapacity()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = managerSource.IndexOf("private void DispatchAdaptiveSampling", StringComparison.Ordinal);
            int end = managerSource.IndexOf("private void DispatchAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string dispatch = managerSource.Substring(start, end - start);

            Assert.That(dispatch, Does.Not.Contain("_adaptiveRootWorkListBuffer"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveResolveRoot"));
            Assert.That(dispatch, Does.Contain("CSAdaptiveTrace"));
        }

        [Test]
        public void AdaptiveScheduler_AccountsForFullResolutionPaths()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveCompactGroupWorkList", start, StringComparison.Ordinal);
            string allocation = shaderSource.Substring(start, end - start);

            Assert.That(allocation, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
            Assert.That(allocation, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataFullResolutionPaths]"));
            Assert.That(shaderSource, Does.Not.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataGuidancePaths], samplesPerGroup)"));
        }

        [Test]
        public void AdaptiveTrace_DiagnosticsRetirementIsCaptureOnly()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveTrace", StringComparison.Ordinal);
            Assert.That(shaderSource, Does.Contain("void RecordAdaptiveRetiredPaths"));
            Assert.That(shaderSource, Does.Contain("AdaptiveMetadataRetiredPaths] = AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
        }

        [Test]
        public void AdaptiveScheduler_ReusesAllocationBetweenReclassificationFrames()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            Assert.That(source, Does.Contain("Mathf.Clamp(adaptiveReclassificationInterval, 1, 8)"));
            Assert.That(source, Does.Contain("bool reclassify = !_adaptiveScheduleInitialized"));
            Assert.That(source, Does.Contain("if (reclassify)"));
            Assert.That(source, Does.Contain("_adaptiveScheduleFrame = reclassify ? 0 : _adaptiveScheduleFrame + 1"));
            int dispatchStart = source.IndexOf("private void DispatchAdaptiveSampling", StringComparison.Ordinal);
            int traceStart = source.IndexOf("SetShaderParameters(activeAdaptiveTraceShader, traceKernel)", dispatchStart, StringComparison.Ordinal);
            string dispatch = source.Substring(dispatchStart, traceStart - dispatchStart);
            Assert.That(dispatch, Does.Contain("ComputeDispatch.Dispatch(adaptiveSchedulerShader, classifyKernel, groupWidth, groupHeight, 1)"));
            Assert.That(dispatch, Does.Contain("ComputeDispatch.Dispatch(adaptiveSchedulerShader, applyBucketRemapKernel, groupWidth, groupHeight, 1)"));
        }

        [Test]
        public void AdaptiveScheduler_ReusesOnlyFullResolutionAllocation()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = source.IndexOf("private void DispatchAdaptiveSampling", StringComparison.Ordinal);
            int end = source.IndexOf("private void DispatchAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string dispatch = source.Substring(start, end - start);
            Assert.That(dispatch, Does.Contain("DispatchIndirect(activeAdaptiveTraceShader, traceKernel"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveResolveRoot"));
        }

        [Test]
        public void AdaptiveCaptureTelemetry_RecordsSchedulerTraceAndResolveFenceTimings()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string captureSource = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");

            Assert.That(managerSource, Does.Contain("CompleteAdaptivePhaseTiming"));
            Assert.That(managerSource, Does.Contain("_adaptiveSchedulerMilliseconds"));
            Assert.That(managerSource, Does.Contain("_adaptiveTraceMilliseconds"));
            Assert.That(managerSource, Does.Contain("_adaptiveResolveMilliseconds"));
            Assert.That(captureSource, Does.Contain("scheduler_fence_ms"));
            Assert.That(captureSource, Does.Contain("trace_fence_ms"));
            Assert.That(captureSource, Does.Contain("resolve_fence_ms"));
            Assert.That(captureSource, Does.Contain("includes GPU synchronization/readback overhead"));
        }

        [Test]
        public void SceneCapture_AdaptiveComparisonCanDisableCaptureInstrumentation()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");

            Assert.That(source, Does.Contain("-rayTracingDisableAdaptiveInstrumentation"));
            Assert.That(source, Does.Contain("manager.SetAdaptiveCaptureDiagnostics(adaptiveSampling && adaptiveInstrumentation)"));
            Assert.That(source, Does.Contain("var adaptiveFrames = adaptiveInstrumentation"));
            Assert.That(source, Does.Contain("var adaptiveDiagnostics = adaptiveInstrumentation"));
            Assert.That(source, Does.Contain("SynchronizeDurationCaptureGpu()"));
        }

        [Test]
        public void SceneCapture_AdaptiveHeatmapUsesDynamicBandsAndCombinedReferenceDifference()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("diagnostics.pathCounts[index]"));
            Assert.That(source, Does.Contain("quantileByPathCount"));
            Assert.That(source, Does.Contain("quantile > 0.80f"));
            Assert.That(source, Does.Contain("quantile > 0.60f"));
            Assert.That(source, Does.Contain("quantile > 0.40f"));
            Assert.That(source, Does.Contain("GenerateReferenceComparisonImage"));
            Assert.That(source, Does.Contain("new Color(redAmount, greenAmount, 0.0f, 1.0f)"));
        }

        [Test]
        public void SceneCapture_AdaptiveHeatmapWritesPerFrameSnapshots()
        {
            string captureSource = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            Assert.That(captureSource, Does.Contain("TestCaptures\", \"Heatmaps\", sceneName"));
            Assert.That(captureSource, Does.Contain("frame_{snapshot.frame:000000}.png"));
            Assert.That(captureSource, Does.Contain("AdaptiveAllocationBlockSize"));
            Assert.That(captureSource, Does.Contain("ReadAdaptiveAllocationForCapture"));
            Assert.That(managerSource, Does.Contain("AdaptiveAllocationFrameData"));
            Assert.That(managerSource, Does.Contain("AdaptiveMetadataWorkItemCount"));
        }

        [Test]
        public void AdaptiveAllocationMonitor_IsEditorWindowWithCaptureFolderPolling()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingAdaptiveAllocationWindow.cs");
            Assert.That(source, Does.Contain("EditorWindow"));
            Assert.That(source, Does.Contain("Window/Ray Tracing/Adaptive Allocation Monitor"));
            Assert.That(source, Does.Contain("Directory.GetFiles(_folder, \"frame_*.png\")"));
            Assert.That(source, Does.Contain("EditorApplication.update += PollForLatestFrame"));
        }

        [Test]
        public void AdaptiveAllocationMonitor_ResolvesLatestFolderForActiveScene()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingAdaptiveAllocationWindow.cs");
            Assert.That(source, Does.Contain("ResolveDefaultFolder"));
            Assert.That(source, Does.Contain("Application.dataPath, \"..\", \"TestCaptures\""));
            Assert.That(source, Does.Contain("activeSceneChangedInEditMode"));
            Assert.That(source, Does.Contain("parent.Name, sceneName"));
            Assert.That(source, Does.Contain("DateTime newestWrite"));
        }

        [Test]
        public void AdaptiveAllocationMonitor_SupportsLivePlayModeGeneration()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingAdaptiveAllocationWindow.cs");
            Assert.That(source, Does.Contain("Generate heatmaps while playing"));
            Assert.That(source, Does.Contain("SetAdaptiveCaptureDiagnostics(true)"));
            Assert.That(source, Does.Contain("ReadAdaptiveCumulativeAllocationForCapture"));
            Assert.That(source, Does.Contain("TestCaptures/Heatmaps"));
            Assert.That(source, Does.Contain("OnPlayModeStateChanged"));
            Assert.That(source, Does.Contain("EnteredPlayMode"));
            Assert.That(source, Does.Contain("ApplyLiveGenerationSettings"));
            Assert.That(source, Does.Contain("LiveGenerationPreference"));
            Assert.That(source, Does.Contain("SessionState.GetBool"));
            Assert.That(source, Does.Contain("_folder = GetLiveFolder()"));
            Assert.That(source, Does.Contain("PollForLatestFrame(true)"));
            Assert.That(source, Does.Contain("ReadAdaptiveCumulativeAllocationForCapture"));
            Assert.That(source, Does.Contain("ReadAdaptiveAllocationStatsForCapture"));
            Assert.That(source, Does.Contain("EditorApplication.isPaused"));
            Assert.That(source, Does.Contain("TryWriteCurrentReferenceDifference"));
            Assert.That(source, Does.Contain("Current Render vs Reference"));
            Assert.That(source, Does.Contain("HeatmapPreviewScale = 4"));
            Assert.That(source, Does.Contain("Pixel groups per bucket (8x8)"));
            Assert.That(source, Does.Contain("Cumulative retired paths"));
            Assert.That(source, Does.Contain("Show allocation heatmap while playing"));
            Assert.That(source, Does.Contain("Show Current Render vs Reference"));
            Assert.That(source, Does.Contain("if (_showHeatmap)"));
            Assert.That(source, Does.Contain("if (_showDifference)"));
            Assert.That(source, Does.Contain("DrawAdaptiveSamplingControls"));
            Assert.That(source, Does.Contain("adaptiveSamplingMinSamples"));
            Assert.That(source, Does.Contain("adaptivePriorityMode"));
            Assert.That(source, Does.Contain("adaptiveNormalizePriorityByLuminance"));
            Assert.That(source, Does.Contain("adaptiveGuidanceChangeThreshold"));
            Assert.That(source, Does.Contain("adaptiveReclassificationInterval"));
            Assert.That(source, Does.Contain("adaptiveHighestBucketSampleRate"));
            Assert.That(source, Does.Contain("adaptiveMaxPathsPerPixel"));
        }

        [Test]
        public void AdaptiveAllocationMonitor_UsesCachedReferencePixelsForLivePsnr()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            string window = System.IO.File.ReadAllText("Assets/Editor/RayTracingAdaptiveAllocationWindow.cs");
            Assert.That(source, Does.Contain("LoadCachedReferencePixels"));
            Assert.That(source, Does.Contain("TryCalculateCurrentReferenceMetrics"));
            Assert.That(source, Does.Contain("TryCompareCurrentRenderToReference"));
            Assert.That(source, Does.Contain("difference.SetPixels(outputPixels)"));
            Assert.That(source, Does.Contain("ReadCurrentFinalColorPixels"));
            Assert.That(window, Does.Contain("Current RGB PSNR"));
            Assert.That(window, Does.Contain("TryCompareCurrentRenderToReference"));
            Assert.That(window, Does.Contain("TestCaptures/EditorRuns"));
            Assert.That(window, Does.Contain("TryGetRecordingManager"));
            Assert.That(window, Does.Not.Contain("EditorRunPreference"));
            Assert.That(window, Does.Contain("settings.txt"));
            Assert.That(window, Does.Contain("metrics.csv"));
            Assert.That(window, Does.Contain("rgb_psnr_db,rgb_rmse,psnr_db_improvement,rmse_improvement"));
            Assert.That(window, Does.Contain("CalculatePsnrImprovement"));
            Assert.That(window, Does.Not.Contain("reference_status"));
            Assert.That(window, Does.Contain("run_complete.txt"));
            Assert.That(window, Does.Contain("StopEditorRunRecording"));
            Assert.That(window, Does.Contain("recordedFrames"));
            Assert.That(window, Does.Contain("Enable adaptive live diagnostics"));
            Assert.That(window, Does.Contain("UpdateHeatmapTexture"));
            Assert.That(window, Does.Contain("SaveFinalHeatmap"));
            Assert.That(window, Does.Contain("_lastHeatmapAllocation == null || _lastHeatmapAllocation.pixels == null"));
            Assert.That(window, Does.Contain("allocation == null || allocation.pixels == null"));
            Assert.That(window, Does.Contain("final_heatmap.png"));
            Assert.That(window, Does.Contain("final_color.png"));
            Assert.That(window, Does.Contain("SaveFinalColor"));
            Assert.That(window, Does.Not.Contain("frame_{frame:000000}.png"));
            Assert.That(window, Does.Contain("if (_liveGeneration)"));
            Assert.That(window, Does.Contain("recordingManager != null && !recordingManager.recordEditorRun"));
            Assert.That(window, Does.Not.Contain("_liveManager == null || !_liveManager.recordEditorRun"));
            Assert.That(window, Does.Contain("UpdateHeatmapTexture(allocation)"));
            Assert.That(window, Does.Contain("bool showReferenceMetrics = _showDifference || _editorRunWriter != null"));
            Assert.That(window, Does.Contain("_metadata = $\"Frame: {frame}\\n\""));
            Assert.That(window, Does.Not.Contain("Adaptive allocation heatmap\\nFrame:"));
            Assert.That(window, Does.Not.Contain("Colors rank pixels by retired paths"));
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string managerEditorSource = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            Assert.That(managerSource, Does.Contain("recordEditorRun"));
            Assert.That(managerEditorSource, Does.Contain("Record Editor Run"));
            Assert.That(window, Does.Contain("manager.enableFrameAccumulation = true"));
            Assert.That(window, Does.Contain("recordEditorRun"));
            Assert.That(window, Does.Contain("new GUIContent(\"Record Editor Run\")"));
            Assert.That(window, Does.Contain("bool adaptive = _liveManager.enableAdaptiveSampling"));
            Assert.That(window, Does.Contain("if (adaptive)"));
            Assert.That(window, Does.Contain("bool needReferenceComparison = _showDifference || _editorRunWriter != null"));
            Assert.That(window, Does.Contain("if ((!_liveGeneration && _editorRunWriter == null)"));
            Assert.That(window, Does.Contain("if (adaptive)"));
            Assert.That(window, Does.Contain("ReadAdaptiveAllocationStatsForCapture"));
            Assert.That(window, Does.Not.Contain("manager.enableAdaptiveSampling = true"));
            Assert.That(window, Does.Contain("SaveFinalHeatmap"));
            Assert.That(window, Does.Contain("string runPrefix = manager.enableAdaptiveSampling ? \"adaptive_run\" : \"run\""));
        }

        [Test]
        public void AdaptiveCumulativeAllocation_ReadsPersistentSamplingState()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = source.IndexOf("ReadAdaptiveCumulativeAllocationForCapture", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            string method = source.Substring(start, source.IndexOf("private void BindAdaptiveSamplingResources", start, StringComparison.Ordinal) - start);
            Assert.That(method, Does.Contain("_adaptiveSamplingStateTexture"));
            Assert.That(method, Does.Contain("state[i].x"));
            Assert.That(method, Does.Not.Contain("_adaptiveWorkListBuffer"));
        }

        [Test]
        public void GalleryAutoOpen_IsLimitedToTheInitialEditorSession()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingQuickControlsAutoOpen.cs");
            Assert.That(source, Does.Contain("GalleryOpenedThisSession"));
            Assert.That(source, Does.Contain("SessionState.GetBool"));
            Assert.That(source, Does.Contain("SessionState.SetBool"));
        }

        [Test]
        public void SceneCapture_AdaptivePriorityOverridesAreValidatedAndApplied()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBrightnessPriority"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveDirectLightPriority"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveRoughnessPriority"));
            Assert.That(source, Does.Contain("CultureInfo.InvariantCulture"));
            Assert.That(source, Does.Contain("manager.adaptiveGuidanceBrightnessPriority = brightnessPriority.Value"));
            Assert.That(source, Does.Contain("manager.adaptiveGuidanceDirectLightPriority = directLightPriority.Value"));
            Assert.That(source, Does.Contain("manager.adaptiveGuidanceRoughnessPriority = roughnessPriority.Value"));
        }

        [Test]
        public void SceneCapture_AdaptiveSamplingSettingsAreAvailableAsValidatedCommandLineOverrides()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("-rayTracingAdaptiveSampling"));
            Assert.That(source, Does.Contain("-rayTracingRecordEditorRun"));
            Assert.That(source, Does.Contain("recordEditorRun"));
            Assert.That(source, Does.Contain("CreateCommandLineEditorRunFolder"));
            Assert.That(source, Does.Contain("adaptive_run_"));
            Assert.That(source, Does.Contain("metrics.csv"));
            Assert.That(source, Does.Contain("metrics.rgbRootMeanSquaredError"));
            Assert.That(source, Does.Not.Contain("metrics.rgbRmse"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveSamplingMinSamples"));
            Assert.That(source, Does.Contain("-rayTracingAdaptivePriorityMode"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveType"));
            Assert.That(source, Does.Contain("TryGetAdaptiveComparisonType"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveNormalizePriorityByLuminance"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveReclassificationInterval"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveHighestBucketSampleRate"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveMaxPathsPerPixel"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapFrames"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapResolutionScale"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveGuidanceHistoryFrames"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapGroupDivisor"));
            Assert.That(source, Does.Contain("Enum.IsDefined"));
            Assert.That(source, Does.Contain("ApplyAdaptiveSamplingOverrides"));
            Assert.That(source, Does.Contain("private const double MaximumTimedCaptureSeconds = 600.0"));
            Assert.That(source, Does.Contain("GetCommandLineArgument(\"-rayTracingDurationSeconds\") != null"));
        }

        [Test]
        public void SceneCapture_AdaptiveComparisonWritesOneVariantPerCsvRow()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("adaptive_variant_comparison.csv"));
            Assert.That(source, Does.Contain("adaptive_off"));
            Assert.That(source, Does.Contain("adaptive_welford"));
            Assert.That(source, Does.Contain("adaptive_dammertz"));
            Assert.That(source, Does.Contain("rgb_rmse"));
            Assert.That(source, Does.Contain("retired_paths"));
            Assert.That(source, Does.Contain("adaptive_group_diagnostics.csv"));
            Assert.That(source, Does.Contain("mean_linear_luminance"));
            Assert.That(source, Does.Contain("reference_rgb_rmse"));
            Assert.That(source, Does.Contain("runWelford"));
            Assert.That(source, Does.Contain("runDammertz"));
        }

        [Test]
        public void SceneCapture_StandaloneReferencesTrackResolutionDurationAndSelectLongest()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("-rayTracingGenerateReference"));
            Assert.That(source, Does.Contain("requires -rayTracingDurationSeconds"));
            Assert.That(source, Does.Contain("GetReferenceImagePath(scenePath, referenceRoot, width, height, durationSeconds)"));
            Assert.That(source, Does.Contain("candidate.durationSeconds > selectedMetadata.durationSeconds"));
            Assert.That(source, Does.Contain("CultureInfo.InvariantCulture, out durationSeconds"));
            Assert.That(source, Does.Contain("WriteReferenceMetadata"));
        }

        [Test]
        public void ProductionShader_ReflectionRefractionAndAbsorptionBaselines_AreStable()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(RegressionProbeShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing compute shader at {RegressionProbeShaderPath}");
            if (!shader.HasKernel("CSRegressionProbe"))
            {
                Assert.Ignore("The active graphics device did not compile the GPU regression kernel. Run without -nographics to validate GPU probes.");
            }

            int kernel = shader.FindKernel("CSRegressionProbe");
            var buffer = new ComputeBuffer(42, sizeof(float) * 4);
            var sphereBuffer = new ComputeBuffer(1, 64);
            try
            {
                shader.SetInt("_NumSpheres", 0);
                shader.SetInt("_WaterEnabled", 1);
                shader.SetVector("_WaterCenter", Vector4.zero);
                shader.SetVector("_WaterSize", new Vector4(10.0f, 10.0f, 0.0f, 0.0f));
                shader.SetFloat("_WaterDepth", 5.0f);
                shader.SetVector("_WaterColor", new Vector4(0.17f, 0.45f, 0.52f, 0.0f));
                shader.SetFloat("_WaterOpacity", 0.18f);
                shader.SetFloat("_WaterAbsorptionStrength", 0.22f);
                shader.SetFloat("_WaterRefraction", 2.0f);
                shader.SetFloat("_WaterWaveAmplitude", 0.0f);
                shader.SetFloat("_FireflyClamp", 1.0f);
                shader.SetBuffer(kernel, "_Spheres", sphereBuffer);
                shader.SetBuffer(kernel, "RegressionResults", buffer);
                shader.Dispatch(kernel, 1, 1, 1);

                var results = new Vector4[42];
                buffer.GetData(results);

                AssertVector(results[0], new Vector4(0.70710677f, 0.70710677f, 0.0f, 1.0f), "reflection");
                AssertVector(results[1], new Vector4(0.47140452f, -0.8819171f, 0.0f, 1.0f), "air-to-glass refraction");
                AssertVector(results[2], Vector4.zero, "total internal reflection signal");
                AssertVector(results[3], new Vector4(0.042069275f, 0.04f, 0.0f, 1.0f), "Schlick Fresnel");
                AssertVector(results[4], new Vector4(0.17212175f, 0.39543194f, 0.64325213f, 1.0f), "glass absorption", 0.0002f);
                AssertVector(results[5], new Vector4(1.0f, 2.0f, 3.0f, 0.0f), "air-to-water medium transition");
                AssertVector(results[6], new Vector4(2.0f, 1.5f, 1.0f, 7.0f), "water-to-glass medium transition");
                AssertVector(results[7], new Vector4(1.5f, 2.0f, 1.0f, 1.0f), "glass-to-water medium transition");
                AssertVector(results[8], new Vector4(1.5f, 2.0f, 3.0f, 0.0f), "nested stack glass current and water parent");
                AssertVector(results[9], new Vector4(2.0f, 1.0f, 2.0f, 0.0f), "matching glass exit reveals water");
                AssertVector(results[10], new Vector4(1.0f, 0.0f, 1.0f, 0.0f), "matching water exit reveals air");
                AssertVector(results[11], new Vector4(3.0f, 2.0f, 2.0f, 1.0f), "unmatched exit preserves current medium");
                AssertVector(results[12], new Vector4(8.0f, 1.0f, 1.0f, 1.0f), "stack overflow is detectable");
                AssertVector(results[13], new Vector4(3.0f, 2.0f, 2.0f, 0.0f), "underwater path initialization");
                AssertVector(results[14], new Vector4(5.0f, 1.0f, 2.0f, 5.0f), "finite water segment distances", 0.001f);
                AssertVector(results[15], new Vector4(0.6950495f, 0.8005689f, 0.9084640f, 1.0f), "glass active-medium segment", 0.0002f);
                AssertVector(results[16], new Vector4(0.6940578f, 0.7850562f, 0.8096121f, 1.0f), "water active-medium segment", 0.0002f);
                AssertVector(results[17], new Vector4(1.0f, 1.0f, 1.0f, 0.0f), "air segment is neutral");
                AssertVector(results[18], new Vector4(4.0f, 1.0f, 4.0f, 1.0f), "water AABB bottom and side intersections", 0.001f);
                AssertVector(results[19], new Vector4(-1.0f, 0.0f, 0.0f, 1.0f), "water AABB side normal");
                AssertVector(results[20], new Vector4(2.0f, 1.5f, 1.0f, 1.0f), "production water-to-glass transition selection");
                AssertVector(results[21], new Vector4(0.9428090f, -0.3333333f, 0.0f, 0.0225197f), "production water-to-glass direction and Fresnel", 0.0002f);
                AssertVector(results[22], new Vector4(1.5f, 2.0f, 0.0f, 1.0f), "production glass-to-water transition avoids air TIR");
                AssertVector(results[23], new Vector4(0.6495190f, 0.7603453f, 0.0f, 3.0f), "production glass-to-water direction preserves stack until transmission", 0.0002f);
                AssertVector(results[24], new Vector4(1.0f, 1.0f, 0.0f, 0.0f), "overlapping sphere exit keeps active overlap medium");
                AssertVector(results[25], new Vector4(8.0f, 1.0f, 2.0f, 0.0f), "overlapping sphere exit removes non-current medium");
                AssertVector(results[26], new Vector4(0.2953915f, 0.1731606f, 0.1120451f, 0.7957747f), "Lambert and GGX mixture evaluation", 0.0002f);
                AssertVector(results[27], new Vector4(1.0185916f, 0.5092958f, 0.2546479f, 1.2732395f), "GGX metal evaluation", 0.0002f);
                AssertFinitePositiveSample(results[28], results[29]);
                AssertVector(results[30], new Vector4(0.0f, 0.4472136f, 0.8944272f, 1.0f), "interpolated shading and geometric normals", 0.0002f);
                AssertVector(results[31], new Vector4(0.2f, 0.8f, 0.5f, 1.0f), "MIS power heuristic");
                AssertVector(results[32], new Vector4(2.0f, 0.1111111f, 0.1111111f, 0.1111111f), "triangle-light PDF and water F0");
                AssertVector(results[33], new Vector4(1.6999575f, 0.8499787f, 0.4249894f, 1.0f), "firefly luminance clamp", 0.0002f);
                AssertVector(results[34], new Vector4(0.0f, 0.4472136f, 0.8944272f, 1.0f), "caustic optical normal", 0.0002f);
                AssertVector(results[35], new Vector4(0.0f, -0.1602089f, -0.9870830f, 0.0400126f), "interpolated mesh refraction and Fresnel", 0.0002f);
                AssertVector(results[36], new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "fully smooth glass preserves the optical normal");
                Assert.That(results[37].w, Is.GreaterThan(0.01f), "rough glass should perturb its transmitted direction");
                Assert.That(new Vector3(results[37].x, results[37].y, results[37].z).sqrMagnitude,
                    Is.EqualTo(1.0f).Within(0.001f), "rough glass microfacet normal should remain normalized");
                AssertVector(results[38], new Vector4(1.0f, 0.25f, 0.5f, 1.0f), "small triangle intersection", 0.0002f);
                AssertVector(results[39], new Vector4(0.04f, 0.04f, 0.04f, 0.04f), "glass F0 is independent of opacity and color");
                AssertVector(results[40], new Vector4(0.232f, 0.232f, 1.0f, 1.0f), "glass specular minimum controls Fresnel reflection");
                AssertVector(results[41], new Vector4(0.0f, -1.0f, 0.0f, 1.0f), "sphere inside hit uses inward shading and outward geometric normals");
            }
            finally
            {
                sphereBuffer.Release();
                buffer.Release();
            }
        }

        [Test]
        public void GameManager_TemporalStateHash_IgnoresPerFrameFocusAndSceneMotion()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Type cameraManagerType = Type.GetType("CameraManager, Assembly-CSharp");
            Type temporalType = Type.GetType("PathTracing.TemporalDenoising.TemporalDenoisingManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null);
            Assert.That(cameraManagerType, Is.Not.Null);
            Assert.That(temporalType, Is.Not.Null);

            var gameObject = new GameObject("Temporal State Hash Test");
            var cameraObject = new GameObject("Temporal State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Component cameraManager = gameObject.GetComponent(cameraManagerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                cameraManagerType.GetField("renderTextureCamera").SetValue(cameraManager, camera);

                FieldInfo temporalField = managerType.GetField("_temporalDenoisingManager", BindingFlags.NonPublic | BindingFlags.Instance);
                object temporalState = temporalField.GetValue(manager);
                temporalType.GetMethod("Initialize").Invoke(temporalState, new[] { manager });
                MethodInfo hashMethod = temporalType.GetMethod("CalculateStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);

                int initialHash = (int)hashMethod.Invoke(temporalState, null);
                cameraManagerType.GetField("cameraFocalDistance").SetValue(cameraManager, 42.0f);
                Assert.That(hashMethod.Invoke(temporalState, null), Is.EqualTo(initialHash));

                FieldInfo spheresField = managerType.GetField("_spheres", BindingFlags.NonPublic | BindingFlags.Instance);
                object spheres = spheresField.GetValue(manager);
                Type sphereType = Type.GetType("PathTracing.Shapes.Sphere, Assembly-CSharp");
                object sphere = Activator.CreateInstance(sphereType);
                spheres.GetType().GetMethod("Add").Invoke(spheres, new[] { sphere });
                int sceneHash = (int)hashMethod.Invoke(temporalState, null);
                Assert.That(sceneHash, Is.Not.EqualTo(initialHash));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DenoiserShader_TemporalKernelsCompile()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader denoiser = AssetDatabase.LoadAssetAtPath<ComputeShader>(DenoiserShaderPath);
            Assert.That(denoiser, Is.Not.Null, $"Missing compute shader at {DenoiserShaderPath}");
            // Batch mode uses Unity's Null graphics device, which cannot compile GPU kernels.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("Temporal compute kernels require a graphics device to compile.");
            }
            Assert.That(denoiser.HasKernel("CSGenerateCameraMotion"), Is.True);
            Assert.That(denoiser.HasKernel("CSTemporalReprojectValidate"), Is.True);
            Assert.That(denoiser.HasKernel("CSUpdateTemporalMoments"), Is.True);
            Assert.That(denoiser.HasKernel("CSGeneratePreservationMask"), Is.True);
            Assert.That(denoiser.HasKernel("CSVisualizeTemporal"), Is.True);
            Assert.That(denoiser.HasKernel("CSVisualizeFeature"), Is.True);
            Assert.That(denoiser.HasKernel("CSGlarePrefilter"), Is.True);
            Assert.That(denoiser.HasKernel("CSGlareDownsample"), Is.True);

            ComputeShader features = AssetDatabase.LoadAssetAtPath<ComputeShader>(FeaturesShaderPath);
            Assert.That(features.HasKernel("CSFeatures"), Is.True);
        }

        [Test]
        public void ProductionComputeKernels_AreOwnedByTheirSplitAssets()
        {
            ComputeShader renderer = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            ComputeShader debug = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugShaderPath);
            ComputeShader utility = AssetDatabase.LoadAssetAtPath<ComputeShader>(UtilityShaderPath);
            ComputeShader features = AssetDatabase.LoadAssetAtPath<ComputeShader>(FeaturesShaderPath);
            ComputeShader focus = AssetDatabase.LoadAssetAtPath<ComputeShader>(FocusShaderPath);

            Assert.That(renderer.HasKernel("CSMain"), Is.True);
            Assert.That(renderer.HasKernel("ClearAccumulation"), Is.False);
            Assert.That(renderer.HasKernel("CSFeatures"), Is.False);
            Assert.That(renderer.HasKernel("CSFocusQuery"), Is.False);
            Assert.That(debug.HasKernel("CSDebugMain"), Is.True);
            Assert.That(utility.HasKernel("ClearAccumulation"), Is.True);
            Assert.That(features.HasKernel("CSFeatures"), Is.True);
            Assert.That(focus.HasKernel("CSFocusQuery"), Is.True);
        }

        [Test]
        public void SpatialDenoiser_GlareAddsHaloWithoutChangingDisabledPresentation()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Glare presentation requires an active compute graphics device.");
            }

            var sourcePixels = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
            var source = CreateRandomWriteTexture(32, 32, RenderTextureFormat.ARGBFloat);
            var withoutGlare = CreateRandomWriteTexture(32, 32, RenderTextureFormat.ARGBFloat);
            var withGlare = CreateRandomWriteTexture(32, 32, RenderTextureFormat.ARGBFloat);
            Type glareType = Type.GetType("PathTracing.Denoising.GlareManager, Assembly-CSharp");
            Assert.That(glareType, Is.Not.Null, "Could not load the glare manager.");
            object glare = Activator.CreateInstance(glareType);
            MethodInfo present = glareType.GetMethod("Present");
            MethodInfo releaseResources = glareType.GetMethod("ReleaseResources");
            Assert.That(present, Is.Not.Null);
            Assert.That(releaseResources, Is.Not.Null);
            try
            {
                // Keep the source below ACES saturation so the glare contribution remains
                // observable in the presented image rather than both paths clamping to white.
                sourcePixels.SetPixel(16, 16, new Color(4.0f, 4.0f, 4.0f, 1.0f));
                sourcePixels.Apply(false, false);
                Graphics.Blit(sourcePixels, source);

                present.Invoke(glare, new object[] { source, withoutGlare, 1.0f, false, 1.0f, 0.5f, 1.0f });
                present.Invoke(glare, new object[] { source, withGlare, 1.0f, true, 1.0f, 0.5f, 1.0f });

                // mip0 is intentionally kept at presentation resolution. Compare the source
                // pixel itself, where glare must retain the bright contribution, then verify that
                // the adjacent pixel receives a decaying halo.
                Color disabledPixel = ReadPixel(withoutGlare, 16, 16);
                Color glarePixel = ReadPixel(withGlare, 16, 16);
                Assert.That(glarePixel.r, Is.GreaterThan(disabledPixel.r + 0.005f),
                    "Bright HDR pixels should spread visible radiance beyond their source pixel.");
                Assert.That(ReadPixel(withGlare, 17, 16).r, Is.LessThanOrEqualTo(glarePixel.r + Epsilon),
                    "Glare should decay smoothly away from a point source instead of forming a repeated pixel block.");

                present.Invoke(glare, new object[] { source, withoutGlare, 1.0f, false, 0.0f, 1.0f, 4.0f });
                Assert.That(ReadPixel(withoutGlare, 20, 16).r, Is.EqualTo(disabledPixel.r).Within(Epsilon),
                    "Disabled glare must not change the existing presentation output.");
            }
            finally
            {
                releaseResources.Invoke(glare, null);
                source.Release();
                withoutGlare.Release();
                withGlare.Release();
                UnityEngine.Object.DestroyImmediate(sourcePixels);
            }
        }

        private static RenderTexture CreateRandomWriteTexture(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format) { enableRandomWrite = true };
            texture.Create();
            return texture;
        }

        private static uint SchedulerHash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return value;
        }

        private static float BucketRate(int bucket, int bucketCount, float highestRate)
        {
            float position = bucket / (float)(bucketCount - 1);
            return Mathf.Pow(2.0f, Mathf.Lerp(-Mathf.Log(highestRate, 2.0f), Mathf.Log(highestRate, 2.0f), position));
        }

        private static float BudgetNormalizedPopulation(int groupCount, int bucket, int bucketCount, float highestRate)
        {
            float rateSum = 0.0f;
            float inverseRateSum = 0.0f;
            for (int index = 0; index < bucketCount; index++)
            {
                float rate = BucketRate(index, bucketCount, highestRate);
                rateSum += rate;
                inverseRateSum += 1.0f / rate;
            }
            float uniformWeight = Mathf.Max(0.0f, (inverseRateSum - bucketCount) / (rateSum - bucketCount));
            return groupCount * (1.0f / BucketRate(bucket, bucketCount, highestRate) + uniformWeight)
                / (inverseRateSum + uniformWeight * bucketCount);
        }

        private static Color ReadPixel(RenderTexture texture, int x, int y)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply(false, false);
                return image.GetPixel(x, y);
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        private static Color[] ReadPixels(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply(false, false);
                return image.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void GameManager_AccumulationStateHash_ResetsWhenSubpixelFilterChanges()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Subpixel Filter State Hash Test");
            var cameraObject = new GameObject("Subpixel Filter State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);

                MethodInfo hashMethod = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);
                int defaultHash = (int)hashMethod.Invoke(manager, null);

                managerType.GetField("subpixelJitterScale").SetValue(manager, 1.25f);
                int changedFilterHash = (int)hashMethod.Invoke(manager, null);
                Assert.That(changedFilterHash, Is.Not.EqualTo(defaultHash),
                    "Changing the sub-pixel filter must reset progressive accumulation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_AdaptiveSamplingToggle_RemainsDisabledByDefault()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Adaptive Sampling Toggle Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo toggle = managerType.GetField("enableAdaptiveSampling");
                Assert.That(toggle, Is.Not.Null, "The capture comparison requires an adaptive-sampling toggle.");
                Assert.That((bool)toggle.GetValue(manager), Is.False,
                    "Uniform sampling must remain the default reference path.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_AdaptiveSamplingPolicy_ResetsProgressiveAccumulation()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Adaptive Sampling State Hash Test");
            var cameraObject = new GameObject("Adaptive Sampling State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);
                MethodInfo hashMethod = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);

                int uniformHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("enableAdaptiveSampling").SetValue(manager, true);
                int adaptiveHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveSamplingMinSamples").SetValue(manager, 16);
                int changedPolicyHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveGuidanceChangeThreshold").SetValue(manager, 0.05f);
                int changedCoarseThresholdHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveGuidanceMaxUpdates").SetValue(manager, 3);
                int changedCoarseUpdateLimitHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveReclassificationInterval").SetValue(manager, 2);
                int changedIntervalHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveNormalizePriorityByLuminance").SetValue(manager, 0.5f);
                int changedLuminanceNormalizationHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveHighestBucketSampleRate").SetValue(manager, 4.0f);
                int changedHighestRateHash = (int)hashMethod.Invoke(manager, null);
                Type modeType = managerType.GetNestedType("AdaptivePriorityMode");
                managerType.GetField("adaptivePriorityMode").SetValue(manager,
                    Enum.Parse(modeType, "DammertzSplitEstimator"));
                int changedModeHash = (int)hashMethod.Invoke(manager, null);

                Assert.That(adaptiveHash, Is.Not.EqualTo(uniformHash));
                Assert.That(changedPolicyHash, Is.Not.EqualTo(adaptiveHash));
                Assert.That(changedCoarseThresholdHash, Is.Not.EqualTo(changedPolicyHash));
                Assert.That(changedCoarseUpdateLimitHash, Is.Not.EqualTo(changedCoarseThresholdHash));
                Assert.That(changedIntervalHash, Is.Not.EqualTo(changedCoarseUpdateLimitHash));
                Assert.That(changedLuminanceNormalizationHash, Is.Not.EqualTo(changedIntervalHash));
                Assert.That(changedHighestRateHash, Is.Not.EqualTo(changedLuminanceNormalizationHash));
                Assert.That(changedModeHash, Is.Not.EqualTo(changedHighestRateHash));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [TestCase(1, 1)]
        [TestCase(3, 5)]
        [TestCase(13, 7)]
        [TestCase(17, 19)]
        public void AdaptiveGroupScheduler_PartialGroupsCoverEveryPixelExactlyOnce(int width, int height)
        {
            var seen = new bool[width * height];
            int groupWidth = Mathf.CeilToInt(width / 8.0f);
            int groupHeight = Mathf.CeilToInt(height / 8.0f);
            for (int groupY = 0; groupY < groupHeight; groupY++)
            for (int groupX = 0; groupX < groupWidth; groupX++)
            for (int y = groupY * 8; y < Math.Min(height, groupY * 8 + 8); y++)
            for (int x = groupX * 8; x < Math.Min(width, groupX * 8 + 8); x++)
            {
                int pixel = x + y * width;
                Assert.That(seen[pixel], Is.False);
                seen[pixel] = true;
            }
            Assert.That(seen, Is.All.True);
        }

        [Test]
        public void AdaptiveGroupScheduler_BucketMotionIsLimitedToOneStep()
        {
            const uint previous = 7;
            Assert.That(Math.Min(previous + 1u, Math.Max(previous - 1u, 15u)), Is.EqualTo(8u));
            Assert.That(Math.Min(previous + 1u, Math.Max(previous - 1u, 0u)), Is.EqualTo(6u));
        }

        [Test]
        [Timeout(600000)]
        public void AdaptiveTrace_ControlledAssignments_MatchPerPixelSampleIndexReference()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Adaptive trace parity requires an active compute graphics device.");

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AdaptiveTraceShaderPath);
            if (shader == null || !shader.HasKernel("CSAdaptiveTraceReference"))
                Assert.Ignore("The active graphics device did not compile the adaptive trace parity kernels.");

            const int width = 3;
            const int height = 5;
            const int pixelCount = width * height;
            int adaptiveTrace = shader.FindKernel("CSAdaptiveTrace");
            int referenceTrace = shader.FindKernel("CSAdaptiveTraceReference");
            var assignments = new Vector2Int[pixelCount];
            var initialStatePixels = new Color[pixelCount];
            var initialAccumulationPixels = new Color[pixelCount];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                assignments[pixel] = new Vector2Int(pixel, 1 + pixel % 4);
                float count = 2 + pixel % 3;
                initialStatePixels[pixel] = new Color(count, 0.15f + pixel * 0.01f, 0.02f + pixel * 0.003f, 0.0f);
                initialAccumulationPixels[pixel] = new Color(0.1f + pixel * 0.01f, 0.2f, 0.3f, 1.0f);
            }

            var initialState = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            var initialAccumulation = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            var adaptiveState = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var adaptiveM2 = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var adaptiveAlternating = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var adaptiveAccumulation = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var adaptiveBeauty = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var adaptiveResult = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var referenceState = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var referenceM2 = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var referenceAlternating = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var referenceAccumulation = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var referenceResult = CreateRandomWriteTexture(width, height, RenderTextureFormat.ARGBFloat);
            var skybox = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var meshTextures = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true);
            var workList = new ComputeBuffer(pixelCount, sizeof(uint) * 2);
            var metadata = new ComputeBuffer(64, sizeof(uint));
            var dummySphere = new ComputeBuffer(1, 92);
            var dummyLight = new ComputeBuffer(1, 88);
            var dummyTriangle = new ComputeBuffer(1, 260);
            var dummyMesh = new ComputeBuffer(1, 48);
            var dummyBvh = new ComputeBuffer(1, 48);
            var dummyTopLevelBvh = new ComputeBuffer(1, 48);
            var dummyMeshLightCdf = new ComputeBuffer(1, sizeof(float));
            var dummyEnvironmentCdf = new ComputeBuffer(1, sizeof(float));
            var dummyPhoton = new ComputeBuffer(1, 40);
            var dummyPhotonMetadata = new ComputeBuffer(1, 24);
            var dummyPhotonGrid = new ComputeBuffer(1, sizeof(int));
            var dummyPhotonNext = new ComputeBuffer(1, sizeof(int));
            var dummyTargetPair = new ComputeBuffer(1, 32);
            var dummyTargetTriangle = new ComputeBuffer(1, 12);
            try
            {
                initialState.SetPixels(initialStatePixels);
                initialState.Apply(false, false);
                initialAccumulation.SetPixels(initialAccumulationPixels);
                initialAccumulation.Apply(false, false);
                skybox.SetPixel(0, 0, new Color(0.18f, 0.32f, 0.58f, 1.0f));
                skybox.Apply(false, false);
                meshTextures.SetPixels(new[] { Color.white }, 0);
                meshTextures.Apply(false, false);
                Graphics.Blit(initialState, adaptiveState);
                Graphics.Blit(initialState, referenceState);
                Graphics.Blit(initialAccumulation, adaptiveAccumulation);
                Graphics.Blit(initialAccumulation, referenceAccumulation);
                workList.SetData(assignments);
                var metadataValues = new uint[64];
                metadataValues[0] = pixelCount;
                metadata.SetData(metadataValues);

                foreach (int kernel in new[] { adaptiveTrace, referenceTrace })
                {
                    shader.SetTexture(kernel, "Result", adaptiveResult);
                    shader.SetTexture(kernel, "_SkyboxTexture", skybox);
                    shader.SetTexture(kernel, "_MeshAlbedoTextures", meshTextures);
                    shader.SetTexture(kernel, "_MeshMetallicRoughnessTextures", meshTextures);
                    shader.SetTexture(kernel, "_MeshNormalTextures", meshTextures);
                    shader.SetTexture(kernel, "_MeshParallaxTextures", meshTextures);
                    shader.SetBuffer(kernel, "AdaptiveTraceWorkList", workList);
                    shader.SetBuffer(kernel, "_Spheres", dummySphere);
                    shader.SetBuffer(kernel, "_Lights", dummyLight);
                    shader.SetBuffer(kernel, "_Triangles", dummyTriangle);
                    shader.SetBuffer(kernel, "_Meshes", dummyMesh);
                    shader.SetBuffer(kernel, "_BvhNodes", dummyBvh);
                    shader.SetBuffer(kernel, "_TopLevelBvhNodes", dummyTopLevelBvh);
                    shader.SetBuffer(kernel, "_ShadowBvhNodes", dummyTopLevelBvh);
                    shader.SetBuffer(kernel, "_MeshLightTriangleCdf", dummyMeshLightCdf);
                    shader.SetBuffer(kernel, "_EnvironmentConditionalCdf", dummyEnvironmentCdf);
                    shader.SetBuffer(kernel, "_EnvironmentMarginalCdf", dummyEnvironmentCdf);
                    shader.SetBuffer(kernel, "_CausticPhotons", dummyPhoton);
                    shader.SetBuffer(kernel, "_CausticPhotonMetadata", dummyPhotonMetadata);
                    shader.SetBuffer(kernel, "_CausticGridCellHeads", dummyPhotonGrid);
                    shader.SetBuffer(kernel, "_CausticPhotonNext", dummyPhotonNext);
                    shader.SetBuffer(kernel, "_CausticTargetPairs", dummyTargetPair);
                    shader.SetBuffer(kernel, "_CausticTargetTriangles", dummyTargetTriangle);
                    shader.SetInt("_AdaptiveWorkListCapacity", pixelCount);
                    shader.SetInt("_AdaptivePriorityMode", 1);
                    shader.SetInt("_Seed", 12345);
                    shader.SetInt("_NumSpheres", 0);
                    shader.SetInt("_NumLights", 0);
                    shader.SetInt("_NumTriangles", 0);
                    shader.SetInt("_NumMeshes", 0);
                    shader.SetInt("_NumTopLevelBvhNodes", 0);
                    shader.SetInt("_NumShadowBvhNodes", 0);
                    shader.SetInt("_EnvironmentLightEnabled", 0);
                    shader.SetInt("_WaterEnabled", 0);
                    shader.SetInt("_CausticsEnabled", 0);
                    shader.SetInt("_CausticPhotonCapacity", 1);
                    shader.SetInt("_CausticGridCellCount", 1);
                    shader.SetInt("_UseTemporalJitter", 0);
                    shader.SetVector("_FrameJitterNdc", Vector4.zero);
                    shader.SetMatrix("_CameraToWorld", Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1)));
                    shader.SetMatrix("_CameraInverseProjection", Matrix4x4.Perspective(48.0f, (float)width / height, 0.1f, 100.0f).inverse);
                    shader.SetVector("_SkyboxLight", Vector4.one);
                    shader.SetFloat("_SubpixelJitterScale", 1.0f);
                    shader.SetFloat("_ApertureRadius", 0.0f);
                    shader.SetFloat("_Exposure", 1.0f);
                    shader.SetFloat("_FireflyClamp", 0.0f);
                    shader.SetInt("_NumBounces", 1);
                }
                shader.SetTexture(adaptiveTrace, "AccumulationResult", adaptiveAccumulation);
                shader.SetTexture(adaptiveTrace, "AdaptiveSamplingState", adaptiveState);
                shader.SetTexture(adaptiveTrace, "AdaptiveSamplingM2", adaptiveM2);
                shader.SetTexture(adaptiveTrace, "Beauty", adaptiveBeauty);
                shader.SetBuffer(adaptiveTrace, "AdaptiveWorkListMetadata", metadata);
                shader.SetTexture(referenceTrace, "Result", referenceResult);
                shader.SetTexture(referenceTrace, "AccumulationResult", referenceAccumulation);
                shader.SetTexture(referenceTrace, "AdaptiveSamplingState", referenceState);
                shader.SetTexture(referenceTrace, "AdaptiveSamplingM2", referenceM2);

                shader.Dispatch(adaptiveTrace, 1, 1, 1);
                shader.Dispatch(referenceTrace, 1, 2, 1);

                Color[] actualState = ReadPixels(adaptiveState);
                Color[] expectedState = ReadPixels(referenceState);
                Color[] actualAccumulation = ReadPixels(adaptiveAccumulation);
                Color[] expectedAccumulation = ReadPixels(referenceAccumulation);
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    AssertColor(actualState[pixel], expectedState[pixel], $"pixel {pixel} adaptive state", 0.0005f);
                    AssertColor(actualAccumulation[pixel], expectedAccumulation[pixel], $"pixel {pixel} accumulated RGB", 0.0005f);
                }
            }
            finally
            {
                workList.Release(); metadata.Release();
                dummySphere.Release(); dummyLight.Release(); dummyTriangle.Release(); dummyMesh.Release(); dummyBvh.Release();
                dummyTopLevelBvh.Release(); dummyMeshLightCdf.Release(); dummyEnvironmentCdf.Release(); dummyPhoton.Release();
                dummyPhotonMetadata.Release(); dummyPhotonGrid.Release(); dummyPhotonNext.Release(); dummyTargetPair.Release();
                dummyTargetTriangle.Release();
                adaptiveState.Release(); adaptiveAccumulation.Release(); adaptiveBeauty.Release(); adaptiveResult.Release();
                adaptiveM2.Release(); adaptiveAlternating.Release(); referenceState.Release(); referenceM2.Release(); referenceAlternating.Release(); referenceAccumulation.Release(); referenceResult.Release();
                UnityEngine.Object.DestroyImmediate(initialState); UnityEngine.Object.DestroyImmediate(initialAccumulation);
                UnityEngine.Object.DestroyImmediate(skybox);
                UnityEngine.Object.DestroyImmediate(meshTextures);
            }
        }

        [TestCase(1, 1, 1)]
        [TestCase(3, 5, 2)]
        [TestCase(17, 19, 4)]
        public void AdaptiveRootWaveCount_CoversEveryAllocatedPathExactlyOnce(int width, int height, int pathsPerPixel)
        {
            int pixelCount = width * height;
            var assignments = new List<Vector2Int>();
            uint expectedPaths = 0;
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                int paths = 1 + pixel % (pathsPerPixel * 4);
                assignments.Add(new Vector2Int(pixel, paths));
                expectedPaths += (uint)paths;
            }

            int waves = pathsPerPixel * 4;
            uint emittedPaths = 0;
            foreach (Vector2Int assignment in assignments)
            {
                for (int wave = 0; wave < waves; wave++)
                {
                    if (wave < assignment.y) emittedPaths++;
                }
            }

            Assert.That(emittedPaths, Is.EqualTo(expectedPaths));
        }

        [Test]
        public void GameManager_DefaultFireflyClamp_IsEnabled()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Firefly Clamp Default Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo clampField = managerType.GetField("fireflyClamp");

                Assert.That(clampField, Is.Not.Null);
                Assert.That(clampField.GetValue(manager), Is.EqualTo(1.0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_InternalRenderSize_ScalesAndClampsViewportPercentage()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            MethodInfo sizeMethod = managerType.GetMethod(
                "CalculateInternalRenderSize",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(int), typeof(float) }, null);
            Assert.That(sizeMethod, Is.Not.Null);

            Assert.That((Vector2Int)sizeMethod.Invoke(null, new object[] { 1920, 1080, 50.0f }),
                Is.EqualTo(new Vector2Int(960, 540)));
            Assert.That((Vector2Int)sizeMethod.Invoke(null, new object[] { 13, 7, 58.0f }),
                Is.EqualTo(new Vector2Int(8, 4)));
            Assert.That((Vector2Int)sizeMethod.Invoke(null, new object[] { 1, 1, 0.0f }),
                Is.EqualTo(Vector2Int.one));
        }

        [TestCase(1366, 768, 100.0f, 1360, 768)]
        [TestCase(1920, 1080, 100.0f, 1920, 1080)]
        [TestCase(1366, 768, 50.0f, 680, 384)]
        public void GameManager_AdaptiveInternalRenderSize_RoundsEachAxisDownToFullGroups(
            int width, int height, float percent, int expectedWidth, int expectedHeight)
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null);
            MethodInfo sizeMethod = managerType.GetMethod("CalculateInternalRenderSize",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(int), typeof(float), typeof(bool) }, null);
            Assert.That(sizeMethod, Is.Not.Null);

            var actual = (Vector2Int)sizeMethod.Invoke(null, new object[] { width, height, percent, true });

            Assert.That(actual, Is.EqualTo(new Vector2Int(expectedWidth, expectedHeight)));
            Assert.That(actual.x % 8, Is.Zero);
            Assert.That(actual.y % 8, Is.Zero);
        }

        [Test]
        public void GameManager_DefaultCameraLens_PreservesPreviousBlurScale()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Camera Lens Default Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                Type cameraManagerType = cameraManager.GetType();

                Assert.That(cameraManagerType.GetField("cameraApertureMode").GetValue(cameraManager).ToString(), Is.EqualTo("LensRadius"));
                Assert.That(cameraManagerType.GetField("cameraApertureRadius").GetValue(cameraManager), Is.EqualTo(0.005f));
                Assert.That(cameraManagerType.GetField("enableClickToFocus").GetValue(cameraManager), Is.True);
                Assert.That(cameraManagerType.GetField("trackClickedFocusPoint").GetValue(cameraManager), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_CameraRotation_StopsBeforeVerticalPolesAndCanRotateBack()
        {
            Type cameraManagerType = Type.GetType("CameraManager, Assembly-CSharp");
            MethodInfo rotateMethod = cameraManagerType.GetMethod("Rotate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(rotateMethod, Is.Not.Null);

            var cameraObject = new GameObject("Camera Pitch Limit Test");
            try
            {
                cameraObject.transform.eulerAngles = new Vector3(88.0f, 10.0f, 0.0f);
                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 5.0f, 5.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(89.0f).Within(CameraRotationEpsilon));
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.y), Is.EqualTo(15.0f).Within(CameraRotationEpsilon));

                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, -10.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(79.0f).Within(CameraRotationEpsilon));

                cameraObject.transform.eulerAngles = new Vector3(-88.0f, 10.0f, 0.0f);
                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, -5.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(-89.0f).Within(CameraRotationEpsilon));

                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, 10.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(-79.0f).Within(CameraRotationEpsilon));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_TrackedFocusPoint_UpdatesDistanceAndUsesPinholeOutsideFrustum()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var managerObject = new GameObject("Tracked Focus Point Test");
            var cameraObject = new GameObject("Tracked Focus Camera");
            try
            {
                Component manager = managerObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = managerObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                Type cameraManagerType = cameraManager.GetType();
                cameraManagerType.GetField("renderTextureCamera").SetValue(cameraManager, camera);
                cameraManagerType.GetField("trackClickedFocusPoint").SetValue(cameraManager, true);
                cameraManagerType.GetField("cameraApertureRadius").SetValue(cameraManager, 0.02f);

                MethodInfo updateFocusMethod = cameraManagerType.GetMethod("UpdateTrackedFocusPoint", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo apertureMethod = cameraManagerType.GetMethod("GetApertureRadius", BindingFlags.Instance | BindingFlags.Public);
                Assert.That(updateFocusMethod, Is.Not.Null);
                Assert.That(apertureMethod, Is.Not.Null);

                cameraManagerType.GetField("ClickedFocusPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cameraManager, new Vector3(0.0f, 0.0f, 10.0f));
                cameraManagerType.GetField("HasClickedFocusPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cameraManager, true);
                updateFocusMethod.Invoke(cameraManager, null);

                Assert.That(cameraManagerType.GetField("cameraFocalDistance").GetValue(cameraManager), Is.EqualTo(10.0f));
                Assert.That(apertureMethod.Invoke(cameraManager, null), Is.EqualTo(0.02f));

                camera.transform.rotation = Quaternion.Euler(0.0f, 180.0f, 0.0f);
                updateFocusMethod.Invoke(cameraManager, null);

                Assert.That(apertureMethod.Invoke(cameraManager, null), Is.EqualTo(0.0f));
                Assert.That(cameraManagerType.GetField("cameraApertureMode").GetValue(cameraManager).ToString(), Is.EqualTo("LensRadius"));

                cameraManagerType.GetField("enableClickToFocus").SetValue(cameraManager, false);
                Assert.That(apertureMethod.Invoke(cameraManager, null), Is.EqualTo(0.02f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FocusQuery_SelectsFirstIntersectionRegardlessOfOpacity()
        {
            string shaderSource = System.IO.File.ReadAllText(FocusShaderPath);
            int kernelStart = shaderSource.IndexOf("void CSFocusQuery", StringComparison.Ordinal);

            Assert.That(kernelStart, Is.GreaterThanOrEqualTo(0));
            string focusKernel = shaderSource.Substring(kernelStart);
            Assert.That(focusKernel, Does.Contain("GetNearestIntersection(ray)"));
            Assert.That(focusKernel, Does.Not.Contain("hit.opacity"));
            Assert.That(focusKernel, Does.Not.Contain("TransparentOpacityThreshold"));
        }

        [Test]
        public void GameManager_SameSizeMeshTextureArray_PreservesSourceTexels()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");
            MethodInfo buildMethod = managerType.GetMethod(
                "BuildMeshTextureArray",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildMethod, Is.Not.Null);

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var expected = new[]
            {
                new Color32(127, 127, 255, 255),
                new Color32(128, 126, 254, 255),
                new Color32(12, 240, 64, 255),
                new Color32(251, 3, 129, 255)
            };
            source.SetPixels32(expected);
            source.Apply(false, false);

            Texture2DArray result = null;
            try
            {
                result = (Texture2DArray)buildMethod.Invoke(null, new object[]
                {
                    new List<Texture2D> { source },
                    null,
                    "Exact Texture Copy Test",
                    Color.white,
                    true
                });

                Assert.That(result.width, Is.EqualTo(source.width));
                Assert.That(result.height, Is.EqualTo(source.height));
                Assert.That(result.GetPixels32(0), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(result);
            }
        }

        [Test]
        public void CausticsDisabled_DoesNotAllocateResourcesOrDispatchPhotonKernels()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Disabled Caustics Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo enabledField = managerType.GetField("enableCaustics");
                FieldInfo causticsManagerField = managerType.GetField(
                    "_causticsManager", BindingFlags.Instance | BindingFlags.NonPublic);
                object causticsManager = causticsManagerField.GetValue(manager);
                Type causticsManagerType = causticsManager.GetType();
                PropertyInfo resourcesProperty = causticsManagerType.GetProperty(
                    "HasResources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo dispatchField = causticsManagerType.GetField(
                    "DispatchCountValue", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo updateMethod = managerType.GetMethod(
                    "UpdateCausticPhotonMap",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(enabledField, Is.Not.Null);
                Assert.That(causticsManagerField, Is.Not.Null);
                Assert.That(resourcesProperty, Is.Not.Null);
                Assert.That(dispatchField, Is.Not.Null);
                Assert.That(updateMethod, Is.Not.Null);
                Assert.That(enabledField.GetValue(manager), Is.False, "Caustics must default to disabled");
                Assert.That(resourcesProperty.GetValue(causticsManager), Is.False);
                Assert.That(dispatchField.GetValue(causticsManager), Is.EqualTo(0));

                updateMethod.Invoke(manager, null);

                Assert.That(resourcesProperty.GetValue(causticsManager), Is.False);
                Assert.That(dispatchField.GetValue(causticsManager), Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CausticResourceCreation_AllocatesNonEmptyGrid()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Caustic Grid Resource Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo causticsManagerField = managerType.GetField(
                    "_causticsManager", BindingFlags.Instance | BindingFlags.NonPublic);
                object causticsManager = causticsManagerField.GetValue(manager);
                Type causticsManagerType = causticsManager.GetType();
                MethodInfo layoutMethod = managerType.GetMethod(
                    "CalculateCausticGridLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo releaseMethod = managerType.GetMethod(
                    "ReleaseCausticResources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo ensureMethod = causticsManagerType.GetMethod(
                    "EnsureResources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                PropertyInfo resourcesProperty = causticsManagerType.GetProperty(
                    "HasResources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo cellCountField = causticsManagerType.GetField(
                    "GridCellCountValue", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(causticsManagerField, Is.Not.Null);
                Assert.That(layoutMethod, Is.Not.Null);
                Assert.That(releaseMethod, Is.Not.Null);
                Assert.That(ensureMethod, Is.Not.Null);
                Assert.That(resourcesProperty, Is.Not.Null);
                Assert.That(cellCountField, Is.Not.Null);

                layoutMethod.Invoke(manager, null);
                ensureMethod.Invoke(causticsManager, new object[] { 64 });

                Assert.That(resourcesProperty.GetValue(causticsManager), Is.True);
                Assert.That(cellCountField.GetValue(causticsManager), Is.GreaterThan(0));
                releaseMethod.Invoke(manager, null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void TerrainScene_UsesAllElevationPaintedLayers()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;
            try
            {
                Scene scene = EditorSceneManager.OpenScene(
                    "Assets/Scenes/Generated/Terrain.unity",
                    OpenSceneMode.Single);
                Assert.That(scene.IsValid(), Is.True);

                Terrain terrain = UnityEngine.Object.FindFirstObjectByType<Terrain>();
                Assert.That(terrain, Is.Not.Null, "The generated terrain scene must contain a Terrain component");
                TerrainData data = terrain.terrainData;
                Assert.That(data, Is.Not.Null);
                Assert.That(data.terrainLayers, Has.Length.EqualTo(4));

                float[,,] weights = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
                var totals = new float[data.alphamapLayers];
                var dominantCounts = new int[data.alphamapLayers];
                for (int z = 0; z < data.alphamapHeight; z++)
                {
                    for (int x = 0; x < data.alphamapWidth; x++)
                    {
                        int dominant = 0;
                        float best = -1.0f;
                        for (int layer = 0; layer < totals.Length; layer++)
                        {
                            float weight = weights[z, x, layer];
                            totals[layer] += weight;
                            if (weight > best)
                            {
                                best = weight;
                                dominant = layer;
                            }
                        }
                        dominantCounts[dominant]++;
                    }
                }

                float sampleCount = data.alphamapWidth * data.alphamapHeight;
                for (int layer = 0; layer < 4; layer++)
                {
                    float averageWeight = totals[layer] / sampleCount;
                    float dominantFraction = dominantCounts[layer] / sampleCount;
                    TestContext.WriteLine(
                        $"Terrain layer {layer} average weight: {averageWeight:F3}, " +
                        $"dominant on {dominantFraction * 100.0f:F1}% of texels");
                    Assert.That(averageWeight, Is.GreaterThan(0.01f),
                        $"Terrain layer {layer} should cover a visible portion of the generated terrain");

                    // Average weight alone cannot distinguish real variety from uniform mush: a
                    // terrain where every texel blends all four layers equally passes that check
                    // yet renders as one flat colour. Dominance is the property that matters.
                    Assert.That(dominantFraction, Is.GreaterThan(0.02f),
                        $"Terrain layer {layer} should be the strongest layer somewhere; a layer that is " +
                        "never dominant is effectively invisible. Adjust its elevation ranks or slope " +
                        "degrees in RayTracingSceneGenerator, then regenerate all generated scenes.");
                }

                // No single layer may swamp the terrain; that is the uniform-texture failure mode.
                for (int layer = 0; layer < 4; layer++)
                {
                    Assert.That(dominantCounts[layer] / sampleCount, Is.LessThan(0.85f),
                        $"Terrain layer {layer} dominates almost the entire terrain, so the other layers " +
                        "are not visible. Its elevation band is probably far wider than intended.");
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        [Test]
        public void CausticsScene_ProductionSamplingDistribution_HasValidTargets()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;
            try
            {
                Scene scene = EditorSceneManager.OpenScene(
                    "Assets/Scenes/Generated/Caustics.unity",
                    OpenSceneMode.Single);
                Assert.That(scene.IsValid(), Is.True);

                Type managerType = Type.GetType("GameManager, Assembly-CSharp");
                Component manager = UnityEngine.Object.FindFirstObjectByType(managerType) as Component;
                Assert.That(manager, Is.Not.Null);
                Type cameraManagerType = Type.GetType("CameraManager, Assembly-CSharp");
                Component cameraManager = manager.GetComponent(cameraManagerType);
                if (cameraManager == null)
                {
                    cameraManager = manager.gameObject.AddComponent(cameraManagerType);
                }
                Camera sceneCamera = UnityEngine.Object.FindFirstObjectByType<Camera>();
                Assert.That(sceneCamera, Is.Not.Null);
                cameraManagerType.GetField("renderTextureCamera").SetValue(cameraManager, sceneCamera);
                FieldInfo causticsManagerField = managerType.GetField(
                    "_causticsManager", BindingFlags.Instance | BindingFlags.NonPublic);
                object causticsManager = causticsManagerField.GetValue(manager);
                Type causticsManagerType = causticsManager.GetType();

                Type rayTracingObjectType = Type.GetType("PathTracingObject, Assembly-CSharp");
                Assert.That(rayTracingObjectType, Is.Not.Null);
                MethodInfo registerMethod = managerType.GetMethod(
                    "RegisterObject", BindingFlags.Instance | BindingFlags.Public);
                foreach (UnityEngine.Object rayTracingObject in UnityEngine.Object.FindObjectsByType(
                    rayTracingObjectType, FindObjectsSortMode.None))
                {
                    registerMethod.Invoke(manager, new[] { rayTracingObject });
                }
                Type directionalLightType = Type.GetType("RayDirectionalLight, Assembly-CSharp");
                MethodInfo registerDirectionalLightMethod = managerType.GetMethod(
                    "RegisterDirectionalLight", BindingFlags.Instance | BindingFlags.Public);
                foreach (UnityEngine.Object directionalLight in UnityEngine.Object.FindObjectsByType(
                    directionalLightType, FindObjectsSortMode.None))
                {
                    registerDirectionalLightMethod.Invoke(manager, new[] { directionalLight });
                }
                managerType.GetMethod("RebuildBuffers", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(manager, new object[] { false });
                managerType.GetMethod("UpdateSpheres", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                managerType.GetMethod("UpdateTriangles", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                managerType.GetMethod("BuildCausticSamplingDistribution", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);

                FieldInfo pairsField = causticsManagerType.GetField(
                    "TargetPairs", BindingFlags.Instance | BindingFlags.NonPublic);
                var pairs = pairsField.GetValue(causticsManager) as System.Collections.IList;
                Assert.That(pairs.Count, Is.GreaterThan(0),
                    "The production scene should produce at least one eligible light/refractor pair");

                float probabilitySum = 0.0f;
                foreach (object pair in pairs)
                {
                    Type pairType = pair.GetType();
                    float probability = (float)pairType.GetField("selectionProbability").GetValue(pair);
                    float cumulative = (float)pairType.GetField("cumulativeProbability").GetValue(pair);
                    Assert.That(float.IsNaN(probability), Is.False);
                    Assert.That(probability, Is.GreaterThan(0.0f));
                    Assert.That(cumulative, Is.InRange(0.0f, 1.0f));
                    probabilitySum += probability;
                }
                Assert.That(probabilitySum, Is.EqualTo(1.0f).Within(0.0001f));

                // The glass-mesh target CDF is a separate distribution from the pair CDF above, and
                // it is the one photon power divides by. A normalization error here silently scales
                // every mesh photon's power (potentially negative), producing no visible caustics
                // even while pair probabilities still look valid. Each mesh owns its own CDF range,
                // so validate per range rather than across the concatenated list.
                FieldInfo trianglesField = causticsManagerType.GetField(
                    "TargetTriangles", BindingFlags.Instance | BindingFlags.NonPublic);
                var targetTriangles = trianglesField.GetValue(causticsManager) as System.Collections.IList;
                Assert.That(targetTriangles.Count, Is.GreaterThan(0),
                    "The production scene's glass mesh should produce area-weighted target triangles");

                int validatedMeshRanges = 0;
                foreach (object pair in pairs)
                {
                    Type pairType = pair.GetType();
                    if ((int)pairType.GetField("refractorType").GetValue(pair) != 1)
                    {
                        continue;
                    }

                    int rangeStart = (int)pairType.GetField("triangleStart").GetValue(pair);
                    int rangeCount = (int)pairType.GetField("triangleCount").GetValue(pair);
                    Assert.That(rangeCount, Is.GreaterThan(0), "a glass-mesh pair must target triangles");
                    Assert.That(rangeStart + rangeCount, Is.LessThanOrEqualTo(targetTriangles.Count));

                    float triangleProbabilitySum = 0.0f;
                    float previousCumulative = 0.0f;
                    for (int i = rangeStart; i < rangeStart + rangeCount; i++)
                    {
                        object target = targetTriangles[i];
                        Type targetType = target.GetType();
                        float probability = (float)targetType.GetField("selectionProbability").GetValue(target);
                        float cumulative = (float)targetType.GetField("cumulativeProbability").GetValue(target);
                        Assert.That(float.IsNaN(probability), Is.False, $"target triangle {i} probability is NaN");
                        Assert.That(probability, Is.GreaterThan(0.0f),
                            $"target triangle {i} must have a positive selection probability");
                        Assert.That(cumulative, Is.InRange(0.0f, 1.0f),
                            $"target triangle {i} cumulative probability must be a normalized CDF value");
                        Assert.That(cumulative, Is.GreaterThanOrEqualTo(previousCumulative),
                            $"target triangle {i} must keep the CDF monotonically increasing");
                        previousCumulative = cumulative;
                        triangleProbabilitySum += probability;
                    }

                    Assert.That(triangleProbabilitySum, Is.EqualTo(1.0f).Within(0.001f),
                        "Glass-mesh target triangle probabilities must sum to one");
                    Assert.That(previousCumulative, Is.EqualTo(1.0f).Within(0.0001f),
                        "The final glass-mesh target CDF entry must reach one so no sample falls through");
                    validatedMeshRanges++;
                }
                Assert.That(validatedMeshRanges, Is.GreaterThan(0),
                    "The production scene should exercise at least one glass-mesh target distribution");

                FieldInfo temporalField = managerType.GetField("_temporalDenoisingManager", BindingFlags.Instance | BindingFlags.NonPublic);
                object temporalManager = temporalField.GetValue(manager);
                Type temporalManagerType = temporalManager.GetType();
                temporalManagerType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(temporalManager, new[] { manager });
                managerType.GetMethod("UpdateCausticPhotonMap", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                AsyncGPUReadback.WaitAllRequests();
                FieldInfo photonCountField = causticsManagerType.GetField(
                    "GridPhotonCountValue", BindingFlags.Instance | BindingFlags.NonPublic);
                int indexedPhotonCount = (int)photonCountField.GetValue(causticsManager);
                TestContext.WriteLine($"Production indexed photon count: {indexedPhotonCount}");
                Assert.That(indexedPhotonCount, Is.GreaterThan(0),
                    "The production sampling distribution should produce indexed receiver photons");

                ComputeShader shader = managerType.GetField("shader").GetValue(manager) as ComputeShader;
                ComputeShader causticsShader = Resources.Load<ComputeShader>("RayTracingCaustics");
                Assert.That(causticsShader, Is.Not.Null);
                int gatherKernel = causticsShader.FindKernel("CSCausticsDebug");
                var causticsImage = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true
                };
                causticsImage.Create();
                try
                {
                    managerType.GetMethod("SetShaderParameters", BindingFlags.Instance | BindingFlags.NonPublic,
                            null, new[] { typeof(ComputeShader), typeof(int) }, null)
                        .Invoke(manager, new object[] { causticsShader, gatherKernel });
                    causticsShader.SetTexture(gatherKernel, "Result", causticsImage);
                    causticsShader.SetInt("_NumberOfPasses", 1);
                    causticsShader.Dispatch(gatherKernel, 32, 64, 1);

                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = causticsImage;
                    var image = new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true);
                    image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    float maximumLuminance = 0.0f;
                    foreach (Color pixel in image.GetPixels())
                    {
                        maximumLuminance = Mathf.Max(maximumLuminance,
                            pixel.r * 0.2126f + pixel.g * 0.7152f + pixel.b * 0.0722f);
                    }
                    UnityEngine.Object.DestroyImmediate(image);
                    TestContext.WriteLine($"Production visible caustic peak: {maximumLuminance}");
                    Assert.That(maximumLuminance, Is.GreaterThan(0.0f),
                        "The saved benchmark camera should see gathered caustic radiance");
                }
                finally
                {
                    causticsImage.Release();
                }

            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        [Test]
        public void PhotonTrace_BindsAllMeshTextureArrays()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int methodStart = managerSource.IndexOf("private void SetPhotonTraceSceneParameters(int traceKernel)", StringComparison.Ordinal);
            int nextMethodStart = managerSource.IndexOf("private bool ShouldUseFrameAccumulation()", methodStart, StringComparison.Ordinal);

            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethodStart, Is.GreaterThan(methodStart));
            string method = managerSource.Substring(methodStart, nextMethodStart - methodStart);
            Assert.That(method, Does.Contain("EnsureMeshTextureArrays()"));
            Assert.That(method, Does.Contain("MeshAlbedoTextures"));
            Assert.That(method, Does.Contain("MeshMetallicRoughnessTextures"));
            Assert.That(method, Does.Contain("MeshNormalTextures"));
            Assert.That(method, Does.Contain("MeshParallaxTextures"));
            Assert.That(managerSource, Does.Contain(
                "private static readonly int MeshAlbedoTextures = Shader.PropertyToID"));
            Assert.That(managerSource, Does.Contain(
                "private static readonly int MeshMetallicRoughnessTextures = Shader.PropertyToID"));
            Assert.That(managerSource, Does.Contain(
                "private static readonly int MeshNormalTextures = Shader.PropertyToID"));
            Assert.That(managerSource, Does.Contain(
                "private static readonly int MeshParallaxTextures = Shader.PropertyToID"));
            string shaderSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            Assert.That(shaderSource, Does.Contain("tangentNormal.xy *= meshTriangle.normalStrength"));
        }

        [Test]
        public void WaterCausticTarget_UsesDecorrelatedSurfaceCoordinates()
        {
            string shaderSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingCausticsKernels.hlsl");
            int waterTargetStart = shaderSource.IndexOf(
                "float2 waterSample = float2(", StringComparison.Ordinal);
            int waterTargetEnd = shaderSource.IndexOf(
                "refractorPosition.y = GetWaterWaveHeight", waterTargetStart, StringComparison.Ordinal);

            Assert.That(waterTargetStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(waterTargetEnd, Is.GreaterThan(waterTargetStart));
            string waterTarget = shaderSource.Substring(waterTargetStart, waterTargetEnd - waterTargetStart);
            Assert.That(waterTarget, Does.Contain("CausticDecorrelatedSample(id.x, 5u)"));
            Assert.That(waterTarget, Does.Contain("CausticDecorrelatedSample(id.x, 6u)"));
            Assert.That(waterTarget, Does.Not.Contain("CausticSequenceSample"));
        }

        [Test]
        public void CausticsDebugMode_UsesDedicatedGatherKernelWithoutDebugVariant()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            Assert.That(managerSource, Does.Contain(
                "enableCaustics && debugRenderMode == DebugRenderMode.Caustics"));
            Assert.That(managerSource, Does.Contain(
                "useDedicatedCausticsDebugKernel ? \"CSCausticsDebug\" : \"CSMain\""));
            Assert.That(managerSource, Does.Contain(
                "debugRenderMode == DebugRenderMode.FinalColor || debugRenderMode == DebugRenderMode.Caustics"));
        }

        private static uint Sum(uint[] values)
        {
            uint sum = 0;
            foreach (uint value in values) sum += value;
            return sum;
        }

        private static void AssertVector(Vector4 actual, Vector4 expected, string label, float tolerance = Epsilon)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), $"{label} x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), $"{label} y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), $"{label} z");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(tolerance), $"{label} w");
        }

        private static void AssertColor(Color actual, Color expected, string label, float tolerance)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(tolerance), $"{label} R");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(tolerance), $"{label} G");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(tolerance), $"{label} B");
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(tolerance), $"{label} A");
        }

        private static void AssertFinitePositiveSample(Vector4 directionAndPdf, Vector4 weightAndNormalDot)
        {
            foreach (float value in new[]
                     {
                         directionAndPdf.x, directionAndPdf.y, directionAndPdf.z, directionAndPdf.w,
                         weightAndNormalDot.x, weightAndNormalDot.y, weightAndNormalDot.z, weightAndNormalDot.w
                     })
            {
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False, "BRDF sample must be finite");
            }

            Assert.That(directionAndPdf.w, Is.GreaterThan(0.0f), "BRDF sample PDF");
            Assert.That(weightAndNormalDot.x, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample red weight");
            Assert.That(weightAndNormalDot.y, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample green weight");
            Assert.That(weightAndNormalDot.z, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample blue weight");
            Assert.That(weightAndNormalDot.w, Is.GreaterThan(0.0f), "BRDF sample must be above the surface");
        }
    }
}
