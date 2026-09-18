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

        private const string WavefrontShaderPath = "Assets/Resources/RayTracingWavefront.compute";
        private const string UtilityShaderPath = "Assets/Resources/RayTracingUtility.compute";
        private const string FeaturesShaderPath = "Assets/Resources/RayTracingFeatures.compute";
        private const string FocusShaderPath = "Assets/Resources/RayTracingFocus.compute";
        private const string AdaptiveSchedulerShaderPath = "Assets/Resources/RayTracingAdaptiveScheduler.compute";
        private const string RegressionProbeShaderPath = "Assets/Resources/RayTracingRegressionProbe.compute";
        private const string DenoiserShaderPath = "Assets/Resources/RayTracingSpatialDenoiser.compute";

        [Test]
        public void ExportAntiAliasing_ComputeShaderExposesFxaaAndSmaaKernels()
        {
            ComputeShader denoiser = AssetDatabase.LoadAssetAtPath<ComputeShader>(DenoiserShaderPath);

            Assert.That(denoiser, Is.Not.Null);
            Assert.That(denoiser.FindKernel("CSFXAA"), Is.GreaterThanOrEqualTo(0));
            Assert.That(denoiser.FindKernel("CSSMAAEdge"), Is.GreaterThanOrEqualTo(0));
            Assert.That(denoiser.FindKernel("CSSMAABlend"), Is.GreaterThanOrEqualTo(0));
        }
        private const string SharedShaderPath = "Assets/Scripts/RayTracingShared.hlsl";
        private const float Epsilon = 0.0001f;
        // Transform.eulerAngles round-trips through a quaternion, producing roughly 0.00025 degrees
        // of platform-dependent error near the pitch limits.
        private const float CameraRotationEpsilon = 0.001f;

        private static int CountOccurrences(string value, string fragment)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += fragment.Length;
            }
            return count;
        }

        [Test]
        public void PathSampler_UsesOwenScrambledSobolWithStableSemanticDimensions()
        {
            string shared = System.IO.File.ReadAllText(SharedShaderPath);
            string main = System.IO.File.ReadAllText(WavefrontShaderPath);

            Assert.That(shared, Does.Contain("struct RngState"));
            Assert.That(shared, Does.Contain("RngState CreateRngState(uint2 pixel, uint sampleIndex)"));
            Assert.That(shared, Does.Contain("uint OwenScramble(uint value, uint seed)"));
            Assert.That(shared, Does.Contain("uint SobolBits(uint sampleIndex, uint dimension)"));
            Assert.That(shared, Does.Contain("uint ShuffleSobolIndex(uint sampleIndex, uint dimension, uint scramble)"));
            Assert.That(shared, Does.Contain("float SobolSample(uint shuffledIndex, uint dimension, uint scramble)"));
            Assert.That(shared, Does.Contain("StructuredBuffer<uint> _SobolDirectionNumbers"));
            Assert.That(shared, Does.Contain("static const uint SampleDimensionsPerBounce = 160u"));
            Assert.That(shared, Does.Contain("static const uint SampleDimensionScatterOffset = 112u"));
            Assert.That(shared, Does.Contain("static const uint SampleDimensionRouletteOffset = 156u"));
            Assert.That(shared, Does.Contain("uint block = dimension >> 2u"));
            Assert.That(shared, Does.Contain("rngState.shuffledIndex = ShuffleSobolIndex"));
            Assert.That(shared, Does.Contain("SobolSample(rngState.shuffledIndex, dimension, rngState.scramble)"));
            Assert.That(shared, Does.Contain("if (firstInRange || (dimension & 3u) == 0u)"));
            Assert.That(shared, Does.Contain("uint bitIndex = (uint)firstbitlow(sampleIndex)"));
            Assert.That(shared, Does.Contain("sampleIndex &= sampleIndex - 1u"));
            Assert.That(shared, Does.Contain("dimension < (uint)clamp(_SobolDimensionLimit, 1, 2568)"));
            Assert.That(shared, Does.Not.Contain("_UseOwenScrambledSobol"));
            Assert.That(shared, Does.Contain("_SobolDimensionLimit"));
            Assert.That(shared, Does.Contain("rngState.fallback = Hash(rngState.fallback ^ dimension)"));
            Assert.That(main, Does.Contain("SetRngDimension(path.rngState, BounceSampleDimension((uint)path.bounce, SampleDimensionDirectLightOffset))"));
            Assert.That(main, Does.Contain("SetRngDimension(path.rngState, BounceSampleDimension((uint)path.bounce, SampleDimensionScatterOffset))"));
            Assert.That(main, Does.Contain("SetRngDimension(path.rngState, BounceSampleDimension((uint)rouletteBounce, SampleDimensionRouletteOffset))"));
            Assert.That(main, Does.Contain("SetRngDimension(rngState, SampleDimensionPixelFilter)"));
            Assert.That(main, Does.Contain("SetRngDimension(rngState, SampleDimensionLens)"));
            Assert.That(main, Does.Contain("RngState rngState = CreateRngState(pixel, sampleIndex)"));

            int setDimensionStart = shared.IndexOf("void SetRngDimension(inout RngState rngState, uint dimension)", StringComparison.Ordinal);
            int causticSampleStart = shared.IndexOf("float CausticSequenceSample", setDimensionStart, StringComparison.Ordinal);
            Assert.That(setDimensionStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(causticSampleStart, Is.GreaterThan(setDimensionStart));
            string setDimension = shared.Substring(setDimensionStart, causticSampleStart - setDimensionStart);
            Assert.That(setDimension, Does.Not.Contain("ShuffleSobolIndex"),
                "Semantic dimension changes must not pay for a Sobol shuffle until rand() consumes the range.");

            string inspector = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            Assert.That(inspector, Does.Contain("DrawSamplerSettings(manager)"));
            Assert.That(inspector, Does.Contain("Owen-Sobol Dimension Limit"));
        }

        [Test]
        public void PathGuiding_IsDisabledByDefaultAndUsesMixturePdfHooks()
        {
            string shared = System.IO.File.ReadAllText(SharedShaderPath);
            string manager = System.IO.File.ReadAllText("Assets/Scripts/PathGuidingManager.cs");
            string settings = System.IO.File.ReadAllText("Assets/Scripts/SceneSettings.cs");
            string rebuild = System.IO.File.ReadAllText("Assets/Resources/PathGuiding.compute");
            string probe = System.IO.File.ReadAllText(RegressionProbeShaderPath);

            Assert.That(shared, Does.Contain("int _PathGuideEnabled"));
            Assert.That(shared, Does.Contain("float PathGuidePdf"));
            Assert.That(shared, Does.Contain("RecordPathGuideObservation"));
            Assert.That(shared, Does.Contain("cosBottom - cosTop"));
            Assert.That(shared, Does.Contain("_PathGuideObservationCounts"));
            Assert.That(shared, Does.Contain("log2(1.0f + luminance) * 8.0f"),
                "Bright observations must be bounded before atomic guide accumulation.");
            Assert.That(shared, Does.Contain("InterlockedMin(_PathGuideObservationCounts"),
                "The guide activation counter must remain bounded during long progressive renders.");
            Assert.That(shared, Does.Contain("_PathGuideCdf[baseIndex + (uint)_PathGuideDirectionBinCount - 1u] < 0.999f"),
                "Guide sampling must fall back until the CDF is initialized by a completed rebuild.");
            Assert.That(shared, Does.Contain("float GetMaterialContinuationPdf"));
            Assert.That(shared, Does.Contain("GetMaterialContinuationPdf(ray, hit, ptToOffset, allowPathGuide)"));
            Assert.That(shared, Does.Contain("if (hit.materialType == MaterialDiffuse) return true;"));
            Assert.That(shared, Does.Contain("SampleMaterialBrdf(sourceRay, hit, true, rngState)"));
            Assert.That(shared, Does.Contain("materialPdf = GetMaterialContinuationPdf(ray, hit, candidate.direction, allowPathGuide);"),
                "Initial RIS candidate targets and selected-candidate evaluation must use the same guide-mixture PDF.");
            Assert.That(manager, Does.Contain("public bool Enabled { get; set; }"));
            Assert.That(manager, Does.Contain("_training ?? _inertTraining"),
                "The disabled path must bind inert buffers because Unity validates shared structured buffers before runtime branches.");
            Assert.That(manager, Does.Contain("public void ReleaseGuideResources()"));
            Assert.That(manager, Does.Contain("_cdf.SetData(CreateUniformCdf())"),
                "The guide CDF must be valid before observations can activate path-guide sampling.");
            Assert.That(manager, Does.Contain("cdf[baseIndex + bin] = (bin + 1) / (float)DirectionBinCount"));
            Assert.That(settings, Does.Contain("public bool EnablePathGuiding = false;"));
            Assert.That(rebuild, Does.Contain("void RebuildPathGuide"));
            Assert.That(rebuild, Does.Not.Contain("_PathGuideCellCounts"));
            Assert.That(rebuild, Does.Contain("_PathGuideTraining[baseIndex + bin] = 0u"),
                "CDF rebuilds must consume guide training so long-running renders cannot overflow it.");
            Assert.That(probe, Does.Contain("SampleMaterialBrdf(normalIncidenceRay, diffuseBrdfHit, false, brdfRngState)"));
        }

        [Test]
        public void AdaptiveSampling_IsDisabledByProjectDefaultButSceneSettingsCanEnableIt()
        {
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string settings = System.IO.File.ReadAllText("Assets/Scripts/SceneSettings.cs");

            Assert.That(manager, Does.Contain("public bool enableAdaptiveSampling = false"));
            Assert.That(settings, Does.Contain("public bool EnableAdaptiveSampling = true"));
            Assert.That(manager, Does.Contain("enableAdaptiveSampling = settings.EnableAdaptiveSampling"));
        }

        [Test]
        public void SobolDirectionNumbers_MatchJoeKuoReferenceCoordinates()
        {
            Type directionType = Type.GetType("PathTracing.Sampling.SobolDirectionNumbers, Assembly-CSharp");
            Assert.That(directionType, Is.Not.Null);
            MethodInfo buildMethod = directionType.GetMethod("BuildDirectionNumbers", new[] { typeof(string) });
            Assert.That(buildMethod, Is.Not.Null);
            string encodedParameters = System.IO.File.ReadAllText("Assets/Resources/SobolJoeKuoParameters.txt");
            uint[] directions = (uint[])buildMethod.Invoke(null, new object[] { encodedParameters });
            Assert.That(directions.Length, Is.EqualTo(2568 * 32));

            Assert.That(EvaluateSobolBits(directions, 1u, 0), Is.EqualTo(0x80000000u));
            Assert.That(EvaluateSobolBits(directions, 2u, 0), Is.EqualTo(0x40000000u));
            Assert.That(EvaluateSobolBits(directions, 3u, 0), Is.EqualTo(0xc0000000u));
            Assert.That(EvaluateSobolBits(directions, 2u, 1), Is.EqualTo(0xc0000000u));
            Assert.That(EvaluateSobolBits(directions, 3u, 1), Is.EqualTo(0x40000000u));
            Assert.That(EvaluateSobolBits(directions, 2u, 2), Is.EqualTo(0xc0000000u));
            Assert.That(EvaluateSobolBits(directions, 3u, 2), Is.EqualTo(0x40000000u));
        }

        private static uint EvaluateSobolBits(uint[] directions, uint index, int dimension)
        {
            uint value = 0u;
            int offset = dimension * 32;
            for (int bit = 0; index != 0u; bit++, index >>= 1)
            {
                if ((index & 1u) != 0u)
                {
                    value ^= directions[offset + bit];
                }
            }
            return value;
        }

        [Test]
        public void ExperimentCapture_PreservesVariantRandomNoiseOverride()
        {
            string capture = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            int captureVariantStart = capture.IndexOf("private static CaptureResult CaptureVariant(", StringComparison.Ordinal);
            int captureVariantEnd = capture.IndexOf("private static void WriteRisReuseDiagnostics", captureVariantStart, StringComparison.Ordinal);

            Assert.That(captureVariantStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(captureVariantEnd, Is.GreaterThan(captureVariantStart));
            string captureVariant = capture.Substring(captureVariantStart, captureVariantEnd - captureVariantStart);
            Assert.That(captureVariant, Does.Not.Contain("manager.randomNoise = false"));
        }

        [Test]
        public void AdaptiveGroupScheduler_PublishesPerGroupSampleAssignments()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Group scheduler parity requires an active compute graphics device.");

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AdaptiveSchedulerShaderPath);
            Assert.That(shader, Is.Not.Null);
            string source = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shader.HasKernel("CSAdaptiveApplyBucketRemap"), Is.True);
            Assert.That(shader.HasKernel("ClearAdaptiveAllocationMetadata"), Is.True);
            Assert.That(source, Does.Contain("AdaptiveGroupInfo[flatGroup] = uint4(explorationAdmission ? 1u : 0u, serviceAge, validPixels"));
            Assert.That(source, Does.Not.Contain("CSAdaptiveCompactGroupWorkList"));
            Assert.That(source, Does.Not.Contain("AdaptiveRootWorkList"));
            Assert.That(source, Does.Not.Contain("AdaptiveWorkRootOffsets"));
            Assert.That(source, Does.Not.Contain("AdaptiveTraceWorkList"));
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
            int remapStart = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", classifyStart, StringComparison.Ordinal);
            Assert.That(classifyStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(remapStart, Is.GreaterThan(classifyStart));
            string classify = shaderSource.Substring(classifyStart, remapStart - classifyStart);

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
            Assert.That(remap, Does.Contain("rcp(GetAdaptiveBucketRateFloat(targetBucketIndex + 1u))"));
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

            int diagnosticsStart = shaderSource.IndexOf("void CSAdaptiveDiagnostics", remapStart, StringComparison.Ordinal);
            string remap = shaderSource.Substring(remapStart, diagnosticsStart - remapStart);
            Assert.That(remap, Does.Contain("uint2 logicalGroup = GetAdaptiveLogicalGroup(groupId.xy)"));
        }

        [Test]
        public void AdaptiveScheduler_UsesFullResolutionRgbWelfordScores()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveClassifyGroups", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string allocation = shaderSource.Substring(start, end - start);
            Assert.That(allocation, Does.Contain("AdaptiveSamplingM2[pixel].rgb"));
            Assert.That(allocation, Does.Contain("count * (count - 1.0f)"));
            Assert.That(allocation, Does.Not.Contain("AdaptiveSamplingState[pixel].yzw"));
            Assert.That(allocation, Does.Contain("_AdaptiveLuminanceErrorWeight"));
            Assert.That(allocation, Does.Contain("pow(max(0.25f, luminance), _AdaptiveLuminanceErrorWeight)"));
            Assert.That(allocation, Does.Contain("pow(max(0.25f, meanLuminance), _AdaptiveLuminanceErrorWeight)"));
            Assert.That(allocation, Does.Not.Contain("_AdaptiveNormalizePriorityByLuminance"));
            Assert.That(allocation, Does.Contain("_AdaptiveSpatialDisagreementPriority"));
            Assert.That(allocation, Does.Contain("scoreSquaredSum"));
            Assert.That(allocation, Does.Contain("_AdaptiveGroupRmsScoreBlend"));
            Assert.That(allocation, Does.Contain("float rmsScore"));
            Assert.That(allocation, Does.Contain("disagreementSquaredSum"));
            Assert.That(allocation, Does.Contain("float averageSpp = pathCountSum / max(1u, validPixels)"));
            Assert.That(allocation, Does.Contain("float decay = min(1.0f"));
            Assert.That(allocation, Does.Contain("if (!groupBootstrap)"));
        }

        [Test]
        public void AdaptiveWavefront_QueuesEligibleSampleLayersTogether()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string wavefrontSource = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");
            Assert.That(wavefrontSource, Does.Contain("sampleLayer * _WavefrontPixelCapacity"));
            Assert.That(wavefrontSource, Does.Contain("void CSWavefrontResolveAdaptive"));
            Assert.That(managerSource, Does.Contain("maxLayers,"));
            Assert.That(managerSource, Does.Not.Contain("RayTracingAdaptiveTrace"));
        }

        [Test]
        public void NonAdaptiveWavefront_ReusesPerPixelPathStorageAcrossPasses()
        {
            string wavefrontSource = System.IO.File.ReadAllText(WavefrontShaderPath);
            int generateStart = wavefrontSource.IndexOf("void CSWavefrontGenerate", StringComparison.Ordinal);
            int dispatchArgsStart = wavefrontSource.IndexOf("void CSWavefrontBuildDispatchArgs", generateStart, StringComparison.Ordinal);
            string generate = wavefrontSource.Substring(generateStart, dispatchArgsStart - generateStart);

            Assert.That(generate, Does.Contain("uint pathIndex = pixel.x + pixel.y * width;"));
            Assert.That(generate, Does.Contain("if (_WavefrontAdaptiveSampling != 0)\n        pathIndex += sampleLayer * _WavefrontPixelCapacity;"));
            Assert.That(generate, Does.Not.Contain("pixel.y * width + sampleLayer"));
        }

        [Test]
        public void AdaptiveBootstrap_UsesTheWavefrontRenderer()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(managerSource, Does.Contain("private void DispatchAdaptiveBootstrap(ComputeShader wavefrontShader)"));
            Assert.That(managerSource, Does.Contain("_wavefrontPathTracingManager.Dispatch(wavefrontShader, size"));
            Assert.That(managerSource, Does.Not.Contain("bootstrapShader.FindKernel"));
            Assert.That(managerSource, Does.Contain("UpscaleAdaptiveBootstrap"));
        }

        [Test]
        public void AdaptiveBootstrap_AllocatesPathGuideStateForThePathGuidedShader()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int bootstrapStart = managerSource.IndexOf("private void DispatchAdaptiveBootstrap", StringComparison.Ordinal);
            int bootstrapEnd = managerSource.IndexOf("private void BindAdaptiveBootstrapUtilityResources", bootstrapStart, StringComparison.Ordinal);
            string bootstrap = managerSource.Substring(bootstrapStart, bootstrapEnd - bootstrapStart);

            Assert.That(bootstrap, Does.Contain("wavefrontShader == _wavefrontPathGuidedShader"));
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
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int attributeStart = shaderSource.LastIndexOf("[numthreads", start, StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string remap = shaderSource.Substring(attributeStart, end - attributeStart);
            Assert.That(remap, Does.Contain("[numthreads(1,1,1)]"));
            Assert.That(remap, Does.Contain("AdaptiveGroupBucket"));
            Assert.That(remap, Does.Contain("AdaptiveGroupExtraDemand"));
            Assert.That(remap, Does.Contain("_AdaptiveExplorationShare"));
            Assert.That(remap, Does.Contain("bucket < priorityBucketCount / 2u"));
            Assert.That(remap, Does.Contain("float regularConditionalFraction = (fraction - explorationRate)"));
            Assert.That(remap, Does.Contain("/ max(0.000001f, 1.0f - explorationRate)"));
            Assert.That(remap, Does.Contain("_AdaptiveScheduleReclassified != 0"));
            Assert.That(remap, Does.Contain("AdaptiveGroupState[flatGroup].y = bucket"));
            Assert.That(remap, Does.Contain("AdaptiveGroupState[flatGroup].w = serviceAge"));
            Assert.That(remap, Does.Contain("AdaptiveGroupInfo[flatGroup] = uint4(explorationAdmission ? 1u : 0u, serviceAge"));
            Assert.That(shaderSource, Does.Contain("explorationCredit"));
            Assert.That(shaderSource, Does.Contain("Hash(flatGroup ^ 0x9e3779b9u) & 65535u"));
            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataWorkItemCount], validPixels)"));
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
            int end = managerSource.IndexOf("if (targetShader == _wavefrontRisShader", start, StringComparison.Ordinal);
            string reset = managerSource.Substring(start, end - start);
            Assert.That(reset, Does.Contain("ClearAdaptiveSamplingState"));
            Assert.That(reset, Does.Contain("ClearAdaptiveGroupState"));
        }

        [Test]
        public void AdaptiveCaustics_RebuildsBaseBeautyBeforeCompositing()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int dispatchStart = managerSource.IndexOf("private void DispatchRenderFrame", StringComparison.Ordinal);
            int dispatchEnd = managerSource.IndexOf("private void FinalizeRenderFrame", dispatchStart, StringComparison.Ordinal);
            string dispatch = managerSource.Substring(dispatchStart, dispatchEnd - dispatchStart);

            Assert.That(dispatch, Does.Contain("ShouldUseAdaptiveSampling() && !_adaptiveBootstrapPreviewActive"));
            Assert.That(dispatch, Does.Contain("Graphics.CopyTexture(_accumulationTexture, _beautyTexture)"));
            Assert.That(dispatch.IndexOf("Graphics.CopyTexture(_accumulationTexture, _beautyTexture)", StringComparison.Ordinal),
                Is.LessThan(dispatch.IndexOf("DispatchFinalColorCaustics(frame.useFrameAccumulation)", StringComparison.Ordinal)));
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
        public void AdaptiveBootstrapAndFullResolutionUseTheSameShaderWarmupKey()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = managerSource.IndexOf("private bool TryDeferShaderVariantWarmup", StringComparison.Ordinal);
            int end = managerSource.IndexOf("private void PrepareRenderFrame", start, StringComparison.Ordinal);
            string warmup = managerSource.Substring(start, end - start);

            Assert.That(warmup, Does.Not.Contain("_adaptiveBootstrapFrameCount"));
            Assert.That(warmup, Does.Not.Contain("useAdaptiveTraceShader"));
            Assert.That(warmup, Does.Not.Contain("? 4"));
            Assert.That(warmup, Does.Contain("HasWaterVolume ? frame.fogEnabled ? 8 : 3"));
        }

        [Test]
        public void AdaptiveBootstrap_IsDisabledByDefaultAndOptIn()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string settingsSource = System.IO.File.ReadAllText("Assets/Scripts/SceneSettings.cs");
            string captureSource = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");

            Assert.That(managerSource, Does.Contain("public bool enableAdaptiveBootstrap;"));
            Assert.That(managerSource, Does.Contain("if (enableAdaptiveBootstrap && _adaptiveBootstrapFrameCount"));
            Assert.That(managerSource, Does.Contain("if (enableAdaptiveBootstrap && !_adaptiveBootstrapSeeded)"));
            Assert.That(settingsSource, Does.Contain("public bool EnableAdaptiveBootstrap = false;"));
            Assert.That(captureSource, Does.Contain("-rayTracingEnableAdaptiveBootstrap"));
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
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(utilitySource, Does.Contain("void ComposeAdaptiveBootstrap"));
            Assert.That(utilitySource, Does.Contain("int _AdaptiveSamplingMinSamples"));
            Assert.That(utilitySource, Does.Contain("AdaptiveSamplingState[id.xy].x >= (float)_AdaptiveSamplingMinSamples"));
            Assert.That(managerSource, Does.Contain("private bool _adaptiveBootstrapPreviewActive"));
            Assert.That(managerSource, Does.Contain("private int _adaptiveBootstrapPreviewFramesRemaining"));
            Assert.That(managerSource, Does.Contain("_adaptiveBootstrapPreviewActive = adaptiveGuidanceHistoryFrames <= 0 && _adaptiveBootstrapPreviewFramesRemaining > 0"));
            Assert.That(managerSource, Does.Contain("utilityShader.SetInt(AdaptiveSamplingMinSamples, Mathf.Clamp(adaptiveSamplingMinSamples, 1, 64))"));
            Assert.That(managerSource, Does.Contain("if (_adaptiveBootstrapPreviewActive)"));
        }

        [Test]
        public void AdaptiveScheduler_UsesBootstrapPriorityOnlyForTheFirstFineSchedule()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
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
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
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
            int compactStart = shaderSource.IndexOf("void CSAdaptiveDiagnostics", remapStart, StringComparison.Ordinal);
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
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string remap = shaderSource.Substring(start, end - start);

            Assert.That(remap, Does.Contain("uint bootstrapBatch = (flatGroup + _AdaptiveScheduleRotation)"));
            Assert.That(remap, Does.Not.Contain("Hash(flatGroup ^ (_AdaptiveScheduleRotation * 1597334677u))"));
        }

        [Test]
        public void AdaptiveScheduler_GivesEveryUntouchedGroupOneFineSampleBeforeRotatingBootstrap()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);

            Assert.That(shaderSource, Does.Contain("bool needsFirstFineSample = AdaptiveSamplingState[logicalGroup * 8u].x == 0.0f"));
            Assert.That(shaderSource, Does.Contain("AdaptiveSamplingState[logicalGroup * 8u].x == 0.0f"));
            Assert.That(shaderSource, Does.Contain("!needsFirstFineSample && _AdaptiveBootstrapGroupDivisor > 1u && bootstrapBatch != 0u"));
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
        public void AdaptiveScheduler_UsesGroupAssignmentsWithoutACompactWorkList()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shaderSource, Does.Not.Contain("CSAdaptiveCompactGroupWorkList"));
            Assert.That(shaderSource, Does.Not.Contain("AdaptiveWorkList["));
        }

        [Test]
        public void AdaptiveScheduler_DerivesAccountingFromCompactGroupGrants()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int remapStart = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int diagnosticsStart = shaderSource.IndexOf("void CSAdaptiveDiagnostics", remapStart, StringComparison.Ordinal);
            string remap = shaderSource.Substring(remapStart, diagnosticsStart - remapStart);

            Assert.That(remap, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataWorkItemCount], validPixels)"));
            Assert.That(remap, Does.Contain("AdaptiveGroupInfo[flatGroup] = uint4(explorationAdmission ? 1u : 0u, serviceAge"));
            Assert.That(remap, Does.Contain("paths / validPixels"));
        }

        [Test]
        public void AdaptiveScheduler_UsesWavefrontTrace()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = managerSource.IndexOf("private void DispatchAdaptiveSampling", StringComparison.Ordinal);
            int end = managerSource.IndexOf("private void DispatchAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string dispatch = managerSource.Substring(start, end - start);

            Assert.That(dispatch, Does.Not.Contain("_adaptiveRootWorkListBuffer"));
            Assert.That(dispatch, Does.Not.Contain("CSAdaptiveResolveRoot"));
            Assert.That(dispatch, Does.Contain("_wavefrontPathTracingManager.Dispatch(wavefrontShader"));
            Assert.That(managerSource, Does.Not.Contain("_adaptiveWorkListBuffer"));
        }

        [Test]
        public void AdaptiveWavefront_ReceivesExistingGroupAssignments()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(managerSource, Does.Contain("BindWavefrontAdaptiveResources"));
            Assert.That(managerSource, Does.Not.Contain("ActiveAdaptiveTraceShader"));
            Assert.That(managerSource, Does.Not.Contain("CSBuildAdaptiveDispatchArgs"));
        }

        [Test]
        public void AdaptiveScheduler_AccountsForFullResolutionPaths()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            int start = shaderSource.IndexOf("void CSAdaptiveApplyBucketRemap", StringComparison.Ordinal);
            int end = shaderSource.IndexOf("void CSAdaptiveDiagnostics", start, StringComparison.Ordinal);
            string allocation = shaderSource.Substring(start, end - start);

            Assert.That(allocation, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
            Assert.That(allocation, Does.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataFullResolutionPaths]"));
            Assert.That(shaderSource, Does.Not.Contain("InterlockedAdd(AdaptiveWorkListMetadata[AdaptiveMetadataGuidancePaths], samplesPerGroup)"));
        }

        [Test]
        public void AdaptiveTrace_DiagnosticsRetirementIsCaptureOnly()
        {
            string shaderSource = System.IO.File.ReadAllText(AdaptiveSchedulerShaderPath);
            Assert.That(shaderSource, Does.Contain("void RecordAdaptiveRetiredPaths"));
            Assert.That(shaderSource, Does.Contain("AdaptiveMetadataRetiredPaths] = AdaptiveWorkListMetadata[AdaptiveMetadataAssignedPaths]"));
        }

        [Test]
        public void AdaptiveWavefront_DoesNotRetainLegacyAdaptiveTraceDeclarations()
        {
            string sharedSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            string schedulerSharedSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingAdaptiveSchedulerShared.hlsl");

            Assert.That(sharedSource, Does.Not.Contain("RAY_TRACING_ADAPTIVE_TRACE"));
            Assert.That(schedulerSharedSource, Does.Not.Contain("RAY_TRACING_ADAPTIVE_TRACE"));
        }

        [Test]
        public void ProductionRenderer_UsesDedicatedWavefrontPathGuidingAndRisReuse()
        {
            string shared = System.IO.File.ReadAllText(SharedShaderPath);
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string wavefrontPathGuided = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefrontPathGuided.compute");
            string wavefrontRis = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefrontRis.compute");

            Assert.That(shared, Does.Contain("#if defined(PATH_GUIDING_ENABLED)\nRWStructuredBuffer<uint> _PathGuideTraining;"));
            Assert.That(shared, Does.Contain("defined(WAVEFRONT_RIS_REUSE)"));
            Assert.That(wavefrontPathGuided, Does.Contain("#define PATH_GUIDING_ENABLED 1"));
            Assert.That(wavefrontPathGuided, Does.Contain("#include_with_pragmas \"RayTracingWavefront.compute\""));
            Assert.That(wavefrontRis, Does.Contain("#define EXPERIMENTAL_RIS_REUSE 1"));
            Assert.That(wavefrontRis, Does.Contain("#define WAVEFRONT_RIS_REUSE 1"));
            Assert.That(manager, Does.Not.Contain("RayTracingExperimentalRis"));
            Assert.That(manager, Does.Not.Contain("RayTracingExperimentalPathGuided"));
            Assert.That(manager, Does.Contain("ShouldUseWavefrontPathGuidedShader()"));
        }

        [Test]
        public void AabbTraversalHelper_IsSharedByAllRendererAssets()
        {
            string sharedSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            int helperIndex = sharedSource.IndexOf("bool IntersectAabbInverse", StringComparison.Ordinal);
            int waterBlockIndex = sharedSource.IndexOf("#if defined(WATER_ENABLED)\nfloat GetWaterWaveHeight", StringComparison.Ordinal);
            int terrainBlockIndex = sharedSource.IndexOf("#if defined(TERRAIN_ENABLED)\nfloat GetTerrainHeight", StringComparison.Ordinal);

            Assert.That(helperIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(sharedSource.IndexOf("bool IntersectAabbInverse", helperIndex + 1, StringComparison.Ordinal), Is.EqualTo(-1));
            Assert.That(helperIndex, Is.LessThan(waterBlockIndex));
            Assert.That(helperIndex, Is.LessThan(terrainBlockIndex));
        }

        [Test]
        public void FinalColor_FogIsIsolatedInDedicatedAssets()
        {
            string wavefrontFog = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefrontFog.compute");
            string wavefrontWaterFog = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefrontWaterFog.compute");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(wavefrontFog, Does.Contain("#define FOG_ENABLED 1"));
            Assert.That(wavefrontFog, Does.Not.Contain("WATER_ENABLED"));
            Assert.That(wavefrontWaterFog, Does.Contain("#define WATER_ENABLED 1"));
            Assert.That(wavefrontWaterFog, Does.Contain("#define FOG_ENABLED 1"));
            Assert.That(manager, Does.Contain("Resources.Load<ComputeShader>(\"RayTracingWavefrontFog\")"));
            Assert.That(manager, Does.Contain("Resources.Load<ComputeShader>(\"RayTracingWavefrontWaterFog\")"));
        }

        [Test]
        public void WavefrontFinalColor_UsesTerrainKeywordAndBindsTerrainForEveryStage()
        {
            string wavefront = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");
            string terrainManager = System.IO.File.ReadAllText("Assets/Scripts/TerrainManager.cs");
            string wavefrontManager = System.IO.File.ReadAllText("Assets/Scripts/WavefrontPathTracingManager.cs");
            string gameManager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(wavefront, Does.Contain("#pragma multi_compile _ TERRAIN_ENABLED"));
            Assert.That(terrainManager, Does.Contain("shader.EnableKeyword(\"TERRAIN_ENABLED\")"));
            Assert.That(terrainManager, Does.Contain("shader.DisableKeyword(\"TERRAIN_ENABLED\")"));
            Assert.That(wavefrontManager, Does.Contain("bindShared(shader, kernel)"));
            Assert.That(gameManager, Does.Contain("SetTerrainShaderParameters(kernelHandle, targetShader)"));
        }

        [Test]
        public void WavefrontFinalColor_QueuesAndResolvesShadowWorkBeforeScatter()
        {
            string wavefront = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/WavefrontPathTracingManager.cs");
            string precompiler = System.IO.File.ReadAllText("Assets/Editor/RayTracingShaderPrecompiler.cs");

            Assert.That(wavefront, Does.Contain("struct ShadowWorkItem"));
            Assert.That(wavefront, Does.Contain("void CSWavefrontClearShadowQueue"));
            Assert.That(wavefront, Does.Contain("void CSWavefrontTraceShadows"));
            Assert.That(wavefront, Does.Contain("void CSWavefrontResolveShadowWork"));
            Assert.That(wavefront, Does.Contain("EnqueueShadowWork(pathIndex)"));
            Assert.That(wavefront, Does.Contain("float3 directLight = GetLightHittingPoint("));
            Assert.That(manager, Does.Contain("new ComputeBuffer(4, sizeof(uint))"));
            Assert.That(manager, Does.Contain("shader.SetBuffer(kernel, WavefrontShadowWork, _shadowWork)"));
            Assert.That(manager, Does.Contain("BindAndDispatchIndirect(shader, kernels.TraceShadows"));
            Assert.That(manager, Does.Contain("BindAndDispatchIndirect(shader, kernels.ResolveShadowWork"));
            Assert.That(manager.IndexOf("BindAndDispatch(shader, kernels.ClearShadowQueue", StringComparison.Ordinal),
                Is.LessThan(manager.IndexOf("BindAndDispatchIndirect(shader, kernels.DirectLight", StringComparison.Ordinal)));
            Assert.That(manager.IndexOf("BindAndDispatchIndirect(shader, kernels.TraceShadows", StringComparison.Ordinal),
                Is.LessThan(manager.IndexOf("BindAndDispatchIndirect(shader, kernels.Scatter", StringComparison.Ordinal)));
            int resolveShadowIndex = manager.IndexOf("BindAndDispatchIndirect(shader, kernels.ResolveShadowWork", StringComparison.Ordinal);
            int scatterDispatchArgumentsIndex = manager.IndexOf("BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, 4)",
                resolveShadowIndex, StringComparison.Ordinal);
            Assert.That(scatterDispatchArgumentsIndex, Is.GreaterThan(resolveShadowIndex));
            Assert.That(scatterDispatchArgumentsIndex,
                Is.LessThan(manager.IndexOf("BindAndDispatchIndirect(shader, kernels.Scatter", StringComparison.Ordinal)));
            Assert.That(precompiler, Does.Contain("CSWavefrontTraceShadows"));
            Assert.That(precompiler, Does.Contain("CSWavefrontResolveShadowWork"));
        }

        [Test]
        public void WavefrontFinalColor_CachesStageKernelsAndPropertyIds()
        {
            string manager = System.IO.File.ReadAllText("Assets/Scripts/WavefrontPathTracingManager.cs");

            Assert.That(manager, Does.Contain("private readonly Dictionary<ComputeShader, Kernels> _kernelsByShader"));
            Assert.That(manager, Does.Contain("private Kernels GetKernels(ComputeShader shader)"));
            Assert.That(manager, Does.Contain("Kernels kernels = GetKernels(shader)"));
            Assert.That(manager, Does.Contain("private static readonly int WavefrontPaths = Shader.PropertyToID(\"_WavefrontPaths\")"));
            Assert.That(manager, Does.Contain("shader.SetBuffer(kernel, WavefrontPaths, _paths)"));
            Assert.That(manager, Does.Not.Contain("shader.SetBuffer(kernel, \"_WavefrontPaths\", _paths)"));
        }

        [Test]
        public void WavefrontEventBudget_RunsTerminalClassificationWithoutAnotherScatter()
        {
            string wavefront = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/WavefrontPathTracingManager.cs");

            Assert.That(wavefront, Does.Contain("if (path.bounce >= _NumBounces)"));
            Assert.That(manager, Does.Contain("// Evaluate sky or emitter radiance reached by the final allowed scatter."));
            int loopEnd = manager.IndexOf("// Evaluate sky or emitter radiance reached by the final allowed scatter.", StringComparison.Ordinal);
            int terminalIntersect = manager.IndexOf("BindAndDispatchIndirect(shader, kernels.Intersect", loopEnd, StringComparison.Ordinal);
            int terminalClassify = manager.IndexOf("BindAndDispatchIndirect(shader, kernels.Classify", terminalIntersect, StringComparison.Ordinal);
            int retire = manager.IndexOf("BindAndDispatchIndirect(shader, kernels.RetireCurrentQueue", terminalClassify, StringComparison.Ordinal);
            Assert.That(terminalIntersect, Is.GreaterThan(loopEnd));
            Assert.That(terminalClassify, Is.GreaterThan(terminalIntersect));
            Assert.That(retire, Is.GreaterThan(terminalClassify));
            string terminalPass = manager.Substring(loopEnd, retire - loopEnd);
            Assert.That(terminalPass, Does.Not.Contain("BindAndDispatchIndirect(shader, kernels.Scatter"),
                "The terminal pass must not create another transport event.");
        }

        [Test]
        public void WavefrontPresent_UsesStoredFirstHitForBasicGeometryDebugModes()
        {
            string wavefront = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/WavefrontPathTracingManager.cs");
            string precompiler = System.IO.File.ReadAllText("Assets/Editor/RayTracingShaderPrecompiler.cs");

            Assert.That(wavefront, Does.Contain("_DebugRenderMode >= DebugNormals && _DebugRenderMode <= DebugThroughput"));
            Assert.That(wavefront, Does.Contain("RayHit hit = _WavefrontHits[pathIndex];"));
            Assert.That(wavefront, Does.Contain("color = hit.normal * 0.5f + 0.5f;"));
            Assert.That(wavefront, Does.Contain("color = GetAlbedo(hit);"));
            Assert.That(wavefront, Does.Contain("color = saturate(GetEmission(hit));"));
            Assert.That(wavefront, Does.Contain("color = saturate(hit.distance / 25.0f).xxx;"));
            Assert.That(wavefront, Does.Contain("_WavefrontFirstDirectLight[work.pathIndex] = directLight;"));
            Assert.That(wavefront, Does.Contain("color = saturate(_WavefrontFirstDirectLight[pathIndex]);"));
            Assert.That(wavefront, Does.Contain("_WavefrontFirstDirectLight[pathIndex] = 0.0f;"));
            Assert.That(wavefront, Does.Contain("struct WavefrontPathDiagnostic"));
            Assert.That(wavefront, Does.Contain("diagnostic.throughput = path.throughput;"));
            Assert.That(wavefront, Does.Contain("diagnostic.bounceCount = (uint)path.bounce;"));
            Assert.That(wavefront, Does.Contain("color = saturate(_WavefrontPathDiagnostics[pathIndex].throughput);"));
            Assert.That(wavefront, Does.Contain("_WavefrontPathDiagnostics[pathIndex].bounceCount / (float)max(1, _NumBounces)"));
            Assert.That(wavefront, Does.Contain("_DebugRenderMode == DebugAccelerationStructures"));
            Assert.That(wavefront, Does.Contain("float topLevelActive = _NumTopLevelBvhNodes > 0 ? 1.0f : 0.0f;"));
            Assert.That(wavefront, Does.Contain("float shadowActive = _NumShadowBvhNodes > 0 ? 1.0f : 0.0f;"));
            Assert.That(wavefront, Does.Contain("_DebugRenderMode == DebugTerrainCells"));
            Assert.That(wavefront, Does.Contain("hit.objectIndex != -2"));
            Assert.That(wavefront, Does.Contain("float2 cellUv = frac(hit.uv * max(1.0f, (float)_TerrainCellResolution));"));
            Assert.That(manager, Does.Contain("private const int PathStateStride = 352;"));
            Assert.That(manager, Does.Contain("bool useDirectLightDebug"));
            Assert.That(manager, Does.Contain("bool usePathDiagnostics"));
            Assert.That(manager, Does.Contain("new ComputeBuffer(_capacity, sizeof(float) * 3)"));
            Assert.That(manager, Does.Contain("new ComputeBuffer(_capacity, sizeof(float) * 7)"));
            Assert.That(manager, Does.Contain("_firstDirectLight?.Release();"));
            Assert.That(manager, Does.Contain("_pathDiagnostics?.Release();"));
            Assert.That(precompiler, Does.Contain("_WavefrontFirstDirectLight"));
            Assert.That(precompiler, Does.Contain("_WavefrontPathDiagnostics"));
            Assert.That(wavefront, Does.Contain("struct WavefrontPathGuideState"));
            Assert.That(wavefront, Does.Contain("RecordWavefrontPathGuide"));
            Assert.That(wavefront, Does.Contain("IsPathGuideEligible(hit, true)"));
            Assert.That(wavefront, Does.Contain("samplesPerLight, volumeEvent, !volumeEvent,"),
                "Direct-light MIS must use the path-guide mixture PDF at the primary bounce when the continuation sampler does.");
            Assert.That(manager, Does.Contain("bool usePathGuiding"));
            Assert.That(manager, Does.Contain("new ComputeBuffer(_capacity, sizeof(float) * 10)"));
            Assert.That(precompiler, Does.Contain("_WavefrontPathGuideStates"));
            Assert.That(wavefront, Does.Contain("_WavefrontAdaptiveSampling"));
            Assert.That(wavefront, Does.Contain("AdaptiveGroupInfo[flatGroup].w <= _AdaptiveSampleLayer"));
            Assert.That(wavefront, Does.Contain("AdaptiveSamplingState[path.pixel]"));
            Assert.That(manager, Does.Contain("bindShared(shader, kernel)"));
            string gameManager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            Assert.That(gameManager, Does.Contain("targetShader.SetBuffer(kernelHandle, AdaptiveGroupInfo, _adaptiveGroupInfoBuffer)"));
            Assert.That(gameManager, Does.Contain("DispatchAdaptiveSampling(targetShader)"));
            Assert.That(gameManager, Does.Contain("_wavefrontPathTracingManager.Dispatch(wavefrontShader"));
            Assert.That(gameManager, Does.Contain("DispatchAdaptiveBootstrap(wavefrontShader)"));
            Assert.That(manager, Does.Not.Contain("!enableAdaptiveBootstrap && ShouldUseFrameAccumulation()"));
        }

        [Test]
        public void BulkShaderPrecompile_ExcludesKnownTimedOutDebugKernel()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingShaderPrecompiler.cs");
            int assetsStart = source.IndexOf("private static readonly ShaderAsset[] RendererAssets", StringComparison.Ordinal);
            int assetsEnd = source.IndexOf(";", assetsStart, StringComparison.Ordinal);
            string rendererAssets = source.Substring(assetsStart, assetsEnd - assetsStart);

            Assert.That(rendererAssets, Does.Not.Contain("DebugShader"));
            Assert.That(source, Does.Contain("All Supported Renderer Assets"));
        }

        [Test]
        public void GeometryDebugModes_UseWavefrontDiagnostics()
        {
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string inspector = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            string precompiler = System.IO.File.ReadAllText("Assets/Editor/RayTracingShaderPrecompiler.cs");
            string wavefront = System.IO.File.ReadAllText("Assets/Resources/RayTracingWavefront.compute");

            Assert.That(manager, Does.Not.Contain("FindKernel(\"CSDebugMain\")"));
            Assert.That(inspector, Does.Contain("DrawProperty(\"debugRenderMode\", \"Debug Render Mode\")"));
            Assert.That(precompiler, Does.Not.Contain("RayTracingDebug"));
            Assert.That(wavefront, Does.Contain("_DebugRenderMode == DebugGlassScatter"));
            Assert.That(wavefront, Does.Contain("diagnostic.glassScatter = color;"));
            Assert.That(wavefront, Does.Contain("color = _WavefrontPathDiagnostics[pathIndex].glassScatter;"));
        }

        [Test]
        public void TemporalRis_UsesValidatedReprojectedReservoirHistory()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string temporalManager = System.IO.File.ReadAllText("Assets/Scripts/Lighting/TemporalRisManager.cs");

            Assert.That(source, Does.Contain("_TemporalRisPreviousReservoir"));
            Assert.That(source, Does.Contain("RWStructuredBuffer<TemporalRisReservoir> _TemporalRisNextReservoir"));
            Assert.That(source, Does.Contain("GetTemporalRisPreviousPixel"));
            Assert.That(source, Does.Contain("StoreTemporalRisReservoir"));
            Assert.That(source, Does.Contain("_TemporalRisHistoryValid"));
            Assert.That(source, Does.Contain("ReprojectTemporalRisCandidate"));
            Assert.That(source, Does.Contain("retainedPreviousFraction"));
            Assert.That(source, Does.Contain("GetInitialRisReservoirScale"));
            Assert.That(source, Does.Contain("GetTemporalRisMergedWeight"));
            Assert.That(source, Does.Contain("unshadowed *= PowerHeuristic(candidate.proposalPdf, materialPdf);"));
            Assert.That(source, Does.Contain("candidate.proposalPdf * candidate.triangleSelectionProbability * lightShapePdf"));
            Assert.That(source, Does.Contain("uint temporalReuseRngState = Hash(rngState.fallback ^ 0x9e3779b9u);"));
            Assert.That(source, Does.Contain("rand(temporalReuseRngState) * totalWeight < mergedWeight"));
            Assert.That(source, Does.Contain("uint spatialReuseRngState = Hash(rngState.fallback ^ 0x85ebca6bu);"));
            Assert.That(source, Does.Contain("rand(spatialReuseRngState) * totalWeight < mergedWeight"));
            Assert.That(source, Does.Contain("bool useSpatialRisReservoir = useSpatialRis && HasSpatialRisLocalReservoir(pixel);"));
            Assert.That(source, Does.Contain("if (!useSpatialRisReservoir)"));
            Assert.That(manager, Does.Contain("ShouldRunTemporalRis"));
            Assert.That(manager, Does.Contain("TemporalRisManager"));
            Assert.That(manager, Does.Contain("_temporalRisManager.InvalidateHistory()"));
            Assert.That(manager, Does.Contain("if (spatialRisPrepassShader == null)"));
            Assert.That(manager, Does.Contain("Resources.Load<ComputeShader>(\"RayTracingSpatialRisPrepass\")"));
            Assert.That(manager, Does.Contain("UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>"));
            Assert.That(temporalManager, Does.Contain("gameManager.Lighting.InitialRisCandidateCount + (gameManager.Lighting.SpatialRisEnabled"));
            Assert.That(temporalManager, Does.Contain("gameManager.Lighting.TemporalRisHistoryMCap"));
            Assert.That(temporalManager, Does.Contain("public void InvalidateHistory()"));
            Assert.That(source, Does.Contain("_TemporalRisDiagnostics"));
            Assert.That(temporalManager, Does.Contain("DiagnosticsCount = 10"));
            string spatialPrepassMeta = System.IO.File.ReadAllText("Assets/Resources/RayTracingSpatialRisPrepass.compute.meta");
            Assert.That(spatialPrepassMeta, Does.Contain("currentAPIMask: 65536"));
        }

        [Test]
        public void TemporalRis_StaticDirectLightBenchmark_UsesComparableVariants()
        {
            string generator = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneGenerator.cs");
            string manifest = System.IO.File.ReadAllText(
                "Assets/Editor/RayTracingExperiments/temporal_ris_static_direct_light_fixed_work.json");

            Assert.That(generator, Does.Contain("CreateTemporalRisStressScene()"));
            Assert.That(generator, Does.Contain("Benchmark_TemporalRisStress"));
            Assert.That(generator, Does.Contain("CreateTemporalRisStableDirectLightScene()"));
            Assert.That(generator, Does.Contain("Benchmark_TemporalRisStableDirectLight"));
            Assert.That(manifest, Does.Contain("TemporalRisStress.unity"));
            Assert.That(manifest, Does.Contain("\"samples\": 200"));
            Assert.That(manifest, Does.Contain("\"name\": \"local_ris\""));
            Assert.That(manifest, Does.Contain("\"name\": \"temporal_ris\""));
            Assert.That(manifest, Does.Contain("\"path\": \"Lighting.TemporalRisEnabled\", \"value\": \"false\""));
            Assert.That(manifest, Does.Contain("\"path\": \"Lighting.TemporalRisEnabled\", \"value\": \"true\""));
            string sweep = System.IO.File.ReadAllText(
                "Assets/Editor/RayTracingExperiments/temporal_ris_candidate_split_sweep_fixed_work.json");
            Assert.That(sweep, Does.Contain("Lighting.TemporalRisHistoryMCap"));
            Assert.That(sweep, Does.Contain("temporal_local_1_history_4"));

            string stableProgressive = System.IO.File.ReadAllText(
                "Assets/Editor/RayTracingExperiments/temporal_ris_stable_direct_light_fixed_work.json");
            Assert.That(stableProgressive, Does.Contain("TemporalRisStableDirectLight.unity"));
            Assert.That(stableProgressive, Does.Contain("\"samples\": 200"));
            Assert.That(stableProgressive, Does.Contain("\"name\": \"local_ris\""));
            Assert.That(stableProgressive, Does.Contain("\"name\": \"temporal_ris\""));

            string stableTrials = System.IO.File.ReadAllText(
                "Assets/Editor/RayTracingExperiments/temporal_ris_stable_direct_light_one_frame_trials.json");
            Assert.That(stableTrials, Does.Contain("TemporalRisStableDirectLight.unity"));
            Assert.That(stableTrials, Does.Contain("\"temporalRisWarmupFrames\": [1, 2, 4, 8]"));
            Assert.That(stableTrials, Does.Contain("\"temporalRisTrialsPerWarmup\": 32"));
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
            int traceStart = source.IndexOf("_wavefrontPathTracingManager.Dispatch(wavefrontShader", dispatchStart, StringComparison.Ordinal);
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
            Assert.That(dispatch, Does.Contain("_wavefrontPathTracingManager.Dispatch(wavefrontShader"));
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
            Assert.That(source, Does.Contain("SynchronizeCaptureGpu()"));
        }

        [Test]
        public void SceneCapture_SynchronizesEveryMeasuredFrame()
        {
            string capture = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            int captureVariantStart = capture.IndexOf("private static CaptureResult CaptureVariant(", StringComparison.Ordinal);
            int captureVariantEnd = capture.IndexOf("private static void WriteRisReuseDiagnostics", captureVariantStart, StringComparison.Ordinal);

            Assert.That(captureVariantStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(captureVariantEnd, Is.GreaterThan(captureVariantStart));
            string captureVariant = capture.Substring(captureVariantStart, captureVariantEnd - captureVariantStart);
            Assert.That(captureVariant, Does.Contain("SynchronizeCaptureGpu();"));
            Assert.That(captureVariant, Does.Not.Contain("durationSeconds > 0.0 || adaptiveSampling"));
        }

        [Test]
        public void SceneCapture_CanPostProcessCompletedExperimentReferences()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");

            Assert.That(source, Does.Contain("-rayTracingPostProcessExperimentReferences"));
            Assert.That(source, Does.Contain("PostProcessExperimentReferences"));
            Assert.That(source, Does.Contain("reference_comparison.csv"));
            Assert.That(source, Does.Contain("_vs_reference_difference.png"));
            Assert.That(source, Does.Contain("generateVariantComparisonImages = true"));
            Assert.That(source, Does.Contain("if (experiment.generateVariantComparisonImages)"));
        }

        [Test]
        public void SceneCapture_ExperimentsAllowSingleVariantReferenceMetrics()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");

            Assert.That(source, Does.Contain("experiment.variants == null || experiment.variants.Length == 0"));
            Assert.That(source, Does.Contain("at least one variant"));
            Assert.That(source, Does.Contain("WriteExperimentComparison(sceneRoot, results, referencePath, reference)"));
            Assert.That(source, Does.Contain("$\"{results[first].name}_vs_reference_difference.png\""));
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
            Assert.That(captureSource, Does.Contain("Path.Combine(DefaultOutputFolder, \"Heatmaps\", sceneName, label)"));
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
            Assert.That(source, Does.Contain("Directory.GetFiles(_folder, \"frame_*.txt\")"));
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
            Assert.That(source, Does.Contain("Enable adaptive live diagnostics"));
            Assert.That(source, Does.Contain("SetAdaptiveCaptureDiagnostics(manager.enableAdaptiveSampling)"));
            Assert.That(source, Does.Contain("ReadAdaptiveAllocationForCapture"));
            Assert.That(source, Does.Contain("HeatmapFolderName = \"TestCaptures/Heatmaps\""));
            Assert.That(source, Does.Contain("OnPlayModeStateChanged"));
            Assert.That(source, Does.Contain("EnteredPlayMode"));
            Assert.That(source, Does.Contain("ApplyLiveGenerationSettings"));
            Assert.That(source, Does.Contain("LiveGenerationPreference"));
            Assert.That(source, Does.Contain("SessionState.GetBool"));
            Assert.That(source, Does.Contain("_folder = GetLiveFolder()"));
            Assert.That(source, Does.Contain("PollForLatestFrame(true)"));
            Assert.That(source, Does.Contain("ReadAdaptiveAllocationForCapture"));
            Assert.That(source, Does.Contain("ReadAdaptiveAllocationStatsForCapture"));
            Assert.That(source, Does.Contain("EditorApplication.isPaused"));
            Assert.That(source, Does.Contain("RayTracingSceneCapture.TryCompareCurrentRenderToReference"));
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
            Assert.That(source, Does.Not.Contain("adaptivePriorityMode"));
            Assert.That(source, Does.Contain("adaptiveLuminanceErrorWeight"));
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
            Assert.That(window, Does.Contain("reference_metrics_available,reference_status"));
            Assert.That(window, Does.Contain("rgb_mean_linear,rgb_mean_luminance"));
            Assert.That(window, Does.Contain("ReadCurrentFinalColorPixels"));
            Assert.That(window, Does.Contain("CalculatePsnrImprovement"));
            Assert.That(window, Does.Contain("SanitizeCsvValue"));
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
            int end = source.IndexOf("private static uint[] ReadAdaptiveBucketGroupCounts", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);
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
        public void LiveFrameIdleCap_PacesCompletedFramesWithoutInterruptingDispatch()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string editor = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            Assert.That(source, Does.Contain("liveFrameIdlePercent"));
            Assert.That(source, Does.Contain("liveFrameCooldownMilliseconds"));
            Assert.That(source, Does.Contain("ShouldDeferLiveFrame"));
            Assert.That(source, Does.Contain("ScheduleNextLiveFrame(renderStart)"));
            Assert.That(source, Does.Contain("_videoCaptureManager.IsActive"));
            Assert.That(source, Does.Contain("Graphics.Blit(_presentationTexture != null ? _presentationTexture : src, dest)"));
            Assert.That(editor, Does.Contain("liveFrameIdlePercent"));
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
            Assert.That(source, Does.Contain("$\"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}\""));
            Assert.That(source, Does.Contain("metrics.csv"));
            Assert.That(source, Does.Contain("metrics.rgbRootMeanSquaredError"));
            Assert.That(source, Does.Not.Contain("metrics.rgbRmse"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveSamplingMinSamples"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveLuminanceErrorWeight"));
            Assert.That(source, Does.Contain("TryGetOptionalFloatArgument(\"-rayTracingAdaptiveLuminanceErrorWeight\", -3.0f, 3.0f"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveGroupRmsScoreBlend"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveExplorationShare"));
            Assert.That(source, Does.Contain("TryGetOptionalFloatArgument(\"-rayTracingAdaptiveExplorationShare\", 0.0f, 0.5f"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveReclassificationInterval"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveHighestBucketSampleRate"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveMaxPathsPerPixel"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapFrames"));
            Assert.That(source, Does.Contain("TryGetOptionalIntegerArgument(\"-rayTracingAdaptiveBootstrapFrames\", 1, 512"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapResolutionScale"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveGuidanceHistoryFrames"));
            Assert.That(source, Does.Contain("-rayTracingAdaptiveBootstrapGroupDivisor"));
            Assert.That(source, Does.Contain("TryGetOptionalIntegerArgument(\"-rayTracingAdaptiveBootstrapGroupDivisor\", 1, 16"));
            Assert.That(source, Does.Not.Contain("-rayTracingAdaptivePriorityMode"));
            Assert.That(source, Does.Contain("ApplyAdaptiveSamplingOverrides"));
            Assert.That(source, Does.Contain("private const double MaximumTimedCaptureSeconds = 600.0"));
            Assert.That(source, Does.Contain("GetCommandLineArgument(\"-rayTracingDurationSeconds\") != null"));
        }

        [Test]
        public void SceneCapture_ExperimentsPreserveVariantAdaptiveSamplingAndAllowTimedRuns()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            string sponzaManifest = System.IO.File.ReadAllText("Assets/Editor/RayTracingExperiments/sponza_adaptive_sampling_comparison.json");

            Assert.That(source, Does.Contain("DebugRenderMode.FinalColor, manager.enableAdaptiveSampling,"));
            Assert.That(source, Does.Contain("true, manager.enableAdaptiveSampling && !experiment.disableAdaptiveInstrumentation,"));
            Assert.That(source, Does.Contain("WriteGroupDiagnostics(sceneRoot, variantName, result, referencePath)"));
            Assert.That(source, Does.Contain("cumulativeFinePaths"));
            Assert.That(source, Does.Contain("servedGroupFraction"));
            Assert.That(source, Does.Contain("assignedPathErrorSpearman"));
            Assert.That(source, Does.Contain("cumulativeFinePathErrorSpearman"));
            Assert.That(source, Does.Contain("current_assigned_paths,cumulative_fine_paths"));
            Assert.That(source, Does.Contain("Final schedule is uniform"));
            Assert.That(source, Does.Contain("historical bootstrap-cohort count offsets"));
            Assert.That(sponzaManifest, Does.Contain("adaptive_welford_history_0_h1"));
            Assert.That(sponzaManifest, Does.Contain("\"adaptiveHighestBucketSampleRate\", \"value\": \"1.0\""));
            Assert.That(source, Does.Contain("experiment.samples < 0"));
            Assert.That(source, Does.Contain("experiment.samples <= 0 && experiment.durationSeconds <= 0.0"));
        }

        [Test]
        public void SceneCapture_AdaptiveComparisonWritesOneVariantPerCsvRow()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            Assert.That(source, Does.Contain("adaptive_variant_comparison.csv"));
            Assert.That(source, Does.Contain("adaptive_off"));
            Assert.That(source, Does.Contain("adaptive_welford"));
            Assert.That(source, Does.Not.Contain("adaptive_dammertz"));
            Assert.That(source, Does.Contain("rgb_rmse"));
            Assert.That(source, Does.Contain("retired_paths"));
            Assert.That(source, Does.Contain("adaptive_diagnostics.json"));
            Assert.That(source, Does.Contain("mean_linear_luminance"));
            Assert.That(source, Does.Contain("reference_rgb_rmse"));
            Assert.That(source, Does.Not.Contain("runDammertz"));
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
            Assert.That(source, Does.Contain("manager.Lighting.TemporalRisEnabled = false"));
            Assert.That(source, Does.Contain("schemaVersion = 2"));
            Assert.That(source, Does.Contain("sceneSha256"));
            Assert.That(source, Does.Contain("shaderSha256"));
            Assert.That(source, Does.Contain("settingsSha256"));
            Assert.That(source, Does.Contain("sourceRevision"));
        }

        [Test]
        public void SceneCapture_CanImportPngReferencesWithoutRenderStatistics()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            string inspector = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            Assert.That(source, Does.Contain("ImportReferencePng"));
            Assert.That(source, Does.Contain("GetReferenceImagePath(scenePath, DefaultReferenceRoot, image.width, image.height)"));
            Assert.That(source, Does.Contain("captureKind = captureKind"));
            Assert.That(source, Does.Contain("durationSeconds < 0.0"));
            Assert.That(inspector, Does.Contain("Import PNG as Reference"));
            Assert.That(inspector, Does.Contain("GetImportedReferencePath"));
        }

        [Test]
        public void ImageAverageWindow_RequiresPngInputsAndMatchingDimensions()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingImageAverageWindow.cs");
            Assert.That(source, Does.Contain("Average PNG Images"));
            Assert.That(source, Does.Contain("Path.GetExtension(path)"));
            Assert.That(source, Does.Contain("width != _width || height != _height"));
            Assert.That(source, Does.Contain("_sampleCounts"));
            Assert.That(source, Does.Contain("GetWeight"));
            Assert.That(source, Does.Contain("sums[pixelIndex] += pixels[pixelIndex] * pixelWeight"));
            Assert.That(source, Does.Contain("sample count"));
            Assert.That(source, Does.Contain("output.EncodeToPNG()"));
        }

        [Test]
        public void SceneCapture_GenericExperimentsUseExistingReferencesAndTypedOverrides()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneCapture.cs");
            string manifest = System.IO.File.ReadAllText("Assets/Editor/RayTracingExperiments/caustics_gather_radius_decay.json");
            Assert.That(source, Does.Contain("-rayTracingExperiment"));
            Assert.That(source, Does.Contain("ValidateExperiment"));
            Assert.That(source, Does.Contain("ApplyExperimentOverrides"));
            Assert.That(source, Does.Contain("FindLongestReference"));
            Assert.That(source, Does.Contain("variant_comparison.csv"));
            Assert.That(source, Does.Contain("WriteReferenceMetrics"));
            Assert.That(manifest, Does.Contain("fixed_radius"));
            Assert.That(manifest, Does.Contain("new_default"));
            Assert.That(manifest, Does.Contain("Caustics.GatherRadiusDecayRate"));
            Assert.That(manifest, Does.Contain("\"value\": \"0.0\""));
            Assert.That(manifest, Does.Contain("\"value\": \"0.35\""));
        }

        [Test]
        public void EnvironmentLighting_DoesNotReadFiniteLightTypeForTriangleRejection()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            int start = source.IndexOf("float3 SampleSingleLight(", StringComparison.Ordinal);
            int end = source.IndexOf("float LightImportanceWeight(", start, StringComparison.Ordinal);
            string sampling = source.Substring(start, end - start).Replace("\r\n", "\n");

            // Undefined finite-light data can happen to pass GPU tests on one compiler/backend.
            Assert.That(sampling, Does.Contain("bool isTriangleLight = false;"));
            Assert.That(sampling, Does.Contain("bool isSunTriangle = false;"));
            Assert.That(sampling, Does.Contain("if (!isEnvironment)\n    {\n"
                + "        isDirectional = light.type == LightTypeDirectional;\n"
                + "        isTriangleLight = light.type == LightTypeTriangle;\n"
                + "        isSunTriangle = light.type == LightTypeSunTriangle;"));
            Assert.That(CountOccurrences(sampling, "isTriangleLight = light.type"), Is.EqualTo(1));
            Assert.That(CountOccurrences(sampling, "isSunTriangle = light.type"), Is.EqualTo(1));
            Assert.That(sampling, Does.Contain("float lightShapePdf = (isTriangleLight || isSunTriangle)"));
        }

        [Test]
        public void InitialRis_UsesOneSelectedProductionShadowPath()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            Assert.That(source, Does.Contain("int _InitialRisCandidateCount"));
            Assert.That(source, Does.Contain("struct InitialRisCandidate"));
            Assert.That(source, Does.Contain("totalWeight / (effectiveCandidateCount * selectedWeight)"));
            Assert.That(source, Does.Contain("GetShadowTransmittance(rayToLight, distanceToLight)"));
            Assert.That(CountOccurrences(source, "GetShadowTransmittance(rayToLight, distanceToLight)"), Is.EqualTo(1),
                "RIS candidates must reuse SampleSingleLight's only production shadow query.");
            Assert.That(source, Does.Contain("(!IsGlassMaterial(hit) || HasDirectDielectricLight())"),
                "Dielectric surfaces should directly sample only non-intersectable directional reflection.");
            Assert.That(source, Does.Contain("IsGlassMaterial(hit) && (isEnvironment || (!isDirectional && !isSunTriangle))"),
                "Finite lights and the environment must remain continuation-only on dielectric surfaces.");
            Assert.That(source, Does.Contain("illumination through glass is sampled only"),
                "Straight shadow connections must not approximate refracted dielectric transport.");
            Assert.That(CountOccurrences(source, "accumulated += SampleSingleLight("), Is.EqualTo(1),
                "RIS candidates must reuse GetLightHittingPoint's only production light-sampling call site.");
            string wavefront = System.IO.File.ReadAllText(WavefrontShaderPath);
            Assert.That(CountOccurrences(wavefront, "float3 directLight = GetLightHittingPoint("), Is.EqualTo(1),
                "Surface and fog events must share one optimizer-visible production direct-light call site.");
            Assert.That(source, Does.Not.Contain("GetFogDirectLight("),
                "A fog wrapper with constant arguments can make Metal specialize a second shadow traversal graph.");
            Assert.That(source, Does.Contain("out bool initialRisSelected"));
            Assert.That(source, Does.Not.Contain("suppressInitialRisTerminalEvent"),
                "RIS NEE must retain the complementary BRDF terminal-light path for mesh emitters.");
        }

        [Test]
        public void InitialRis_CandidateCountIsUploadedWithoutResettingLocalAccumulation()
        {
            string lighting = System.IO.File.ReadAllText("Assets/Scripts/Lighting/LightingManager.cs");
            string manager = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string temporal = System.IO.File.ReadAllText("Assets/Scripts/Denoising/TemporalDenoisingManager.cs");
            string settings = System.IO.File.ReadAllText("Assets/Scripts/SceneSettings.cs");
            Assert.That(lighting, Does.Contain("InitialRisCandidateCountId"));
            Assert.That(lighting, Does.Contain("shader.SetInt(InitialRisCandidateCountId, _initialRisCandidateCount)"));
            Assert.That(settings, Does.Contain("InitialRisCandidateCount = 4"));
            Assert.That(manager, Does.Contain("Lighting.InitialRisCandidateCount = settings.InitialRisCandidateCount"));
            Assert.That(manager, Does.Not.Contain("AddHash(hash, Lighting.InitialRisCandidateCount)"));
            Assert.That(manager, Does.Contain("_temporalRisManager.InvalidateHistory()"));
            Assert.That(temporal, Does.Contain("AddHash(hash, _gameManager.Lighting.InitialRisCandidateCount)"));
            Assert.That(lighting, Does.Contain("TemporalRisEnabled"));
            Assert.That(manager, Does.Contain("Lighting.TemporalRisEnabled = settings.TemporalRisEnabled"));
            Assert.That(lighting, Does.Contain("TemporalRisHistoryMCap"));
            Assert.That(settings, Does.Contain("TemporalRisHistoryMCap = 1"));
            Assert.That(manager, Does.Contain("Lighting.TemporalRisHistoryMCap = settings.TemporalRisHistoryMCap"));
        }

        [Test]
        public void RisReuse_IsExperimentalAndDisabledByDefault()
        {
            string lighting = System.IO.File.ReadAllText("Assets/Scripts/Lighting/LightingManager.cs");
            string settings = System.IO.File.ReadAllText("Assets/Scripts/SceneSettings.cs");
            string editor = System.IO.File.ReadAllText("Assets/Editor/GameManagerEditor.cs");
            string generator = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneGenerator.cs");

            Assert.That(lighting, Does.Contain("private bool _temporalRisEnabled = false"));
            Assert.That(lighting, Does.Contain("Experimental: reuses validated primary opaque direct-light reservoirs"));
            Assert.That(settings, Does.Contain("public bool TemporalRisEnabled = false"));
            Assert.That(settings, Does.Contain("public bool SpatialRisEnabled = false"));
            Assert.That(editor, Does.Contain("Temporal RIS Reuse (Experimental)"));
            Assert.That(editor, Does.Contain("Spatial RIS Reuse (Experimental)"));
            Assert.That(generator, Does.Not.Contain("TemporalRisEnabled = true"));
            Assert.That(generator, Does.Not.Contain("SpatialRisEnabled = true"));
        }

        [Test]
        public void GgxT1_EvaluationMatchesDoubleAnalyticalBrdfAndPdf(
            [Values(0.03f, 0.05f, 0.1f, 0.2f)] float roughness,
            [Values(1.0f, 0.1f, 0.01f)] float normalDotView,
            [Values(0.0f, 0.5f)] float metallic)
        {
            double alpha = GgxReferenceAlpha(roughness);
            var view = new Vector3((float)Math.Sqrt(1.0 - (double)normalDotView * normalDotView), normalDotView, 0.0f);
            // Probe the peak, sub-lobe widths, and the diffuse-dominated off-specular region.
            foreach (double k in new[] { 0.0, 0.5, 1.0, 2.0, 16.0 })
            {
                double angle = 2.0 * Math.Atan(k * alpha);
                var light = new Vector3(-view.x, (float)(view.y * Math.Cos(angle)), (float)(view.y * Math.Sin(angle)));
                Vector4 actual = RunGgxProbe(roughness, metallic, view, light, 0, 1)[0];
                GgxDoubleReference(view, light, alpha, metallic, out double d, out double pdf, out double r, out double g, out double b);
                string label = $"roughness={roughness}, NdotV={normalDotView}, metallic={metallic}, k={k}";
                double tolerance = normalDotView < 0.1f ? 0.005 : 0.0005;
                AssertGgxClose(actual.w, pdf, tolerance, "PDF " + label);
                AssertGgxClose(actual.x, r, tolerance, "BRDF R " + label);
                AssertGgxClose(actual.y, g, tolerance, "BRDF G " + label);
                AssertGgxClose(actual.z, b, tolerance, "BRDF B " + label);
                if (normalDotView == 1.0f && k == 0.0)
                {
                    double f0 = 0.04 * (1.0 - metallic) + (double)0.8f * metallic;
                    double diffuse = (1.0 - f0) * (double)0.8f * (1.0 - metallic) / Math.PI;
                    double extractedD = 4.0 * (actual.x - diffuse) / f0;
                    AssertGgxClose(extractedD, d, 0.0005, "D peak extracted from BRDF " + label);
                    AssertGgxClose(extractedD, 1.0 / (Math.PI * alpha * alpha), 0.0005, "Analytical D peak " + label);
                }
            }

            foreach (Vector3 light in new[] { Vector3.up, new Vector3(0.0f, 0.01f, (float)Math.Sqrt(1.0 - 0.01f * (double)0.01f)) })
            {
                Vector4 actual = RunGgxProbe(roughness, metallic, view, light, 0, 1)[0];
                GgxDoubleReference(view, light, alpha, metallic, out _, out double pdf, out double r, out double g, out double b);
                AssertGgxClose(actual.w, pdf, 0.0005, "Off-specular PDF");
                AssertGgxClose(actual.x, r, 0.0005, "Off-specular BRDF R");
                AssertGgxClose(actual.y, g, 0.0005, "Off-specular BRDF G");
                AssertGgxClose(actual.z, b, 0.0005, "Off-specular BRDF B");
            }
        }

        [Test]
        public void GgxT1_SampledPdfMatchesIndependentDoubleDensity(
            [Values(0.03f, 0.05f, 0.1f, 0.2f)] float roughness,
            [Values(1.0f, 0.1f, 0.01f)] float normalDotView,
            [Values(0.0f, 0.5f)] float metallic,
            [Values(12345, 81723)] int seed)
        {
            const int attempts = 65536;
            double alpha = GgxReferenceAlpha(roughness);
            var view = new Vector3((float)Math.Sqrt(1.0 - (double)normalDotView * normalDotView), normalDotView, 0.0f);
            Vector4[] results = RunGgxProbe(roughness, metallic, view, Vector3.zero, seed, attempts);
            double maxRelativeError = 0.0;
            int worstAttempt = -1;
            int valid = 0;
            for (int i = 0; i < attempts; i++)
            {
                Vector4 sample = results[2 * i];
                Vector4 weight = results[2 * i + 1];
                var direction = new Vector3(sample.x, sample.y, sample.z);
                if (weight.w != 1.0f || !IsFinite(weight) || !IsFinite(sample) || Math.Abs(direction.sqrMagnitude - 1.0) > 0.00001)
                    Assert.Fail($"Invalid sample record at attempt {i}, seed {seed}: {sample}");
                // Null attempts have no solid-angle density; never condition the distribution on success.
                if (sample.w == 0.0f)
                {
                    if (sample.y > 0.0f) Assert.Fail($"Unexpected above-surface null at attempt {i}, seed {seed}");
                    Assert.That(weight, Is.EqualTo(new Vector4(0, 0, 0, 1)), "Null samples must carry zero throughput");
                    continue;
                }
                if (sample.y <= 0.0f || sample.w < 0.0f)
                    Assert.Fail($"Invalid positive-density direction at attempt {i}, seed {seed}: {sample}");
                valid++;
                GgxDoubleReference(view, direction, alpha, metallic, out _, out double pdf, out double r, out double g, out double b);
                // T1 isolates D. The existing BRDF grazing-denominator clamp and low-PDF
                // throughput gate remain separate transport defects, not an analytical reference.
                if (sample.w > 1e-6f && 4.0 * normalDotView * direction.y > 1e-6)
                {
                    double tolerance = normalDotView < 0.1f ? 0.005 : 0.0005;
                    double scale = direction.y / pdf;
                    if (Math.Abs(weight.x - r * scale) > Math.Abs(r * scale) * tolerance + 1e-8 ||
                        Math.Abs(weight.y - g * scale) > Math.Abs(g * scale) * tolerance + 1e-8 ||
                        Math.Abs(weight.z - b * scale) > Math.Abs(b * scale) * tolerance + 1e-8)
                        Assert.Fail($"Independent BRDF*cos/PDF weight mismatch at attempt {i}, seed {seed}: {weight}");
                }
                double error = Math.Abs(sample.w - pdf) / pdf;
                if (error > maxRelativeError)
                {
                    maxRelativeError = error;
                    worstAttempt = i;
                }
            }
            Assert.That(valid, Is.GreaterThan(attempts / 2));
            // Grazing half-vector reconstruction amplifies float direction roundoff.
            Assert.That(maxRelativeError, Is.LessThanOrEqualTo(normalDotView < 0.1f ? 0.005 : 0.0005),
                $"Independent mixed VNDF PDF, seed={seed}, worst attempt={worstAttempt}");
        }

        [Test]
        public void GgxT1_NormalViewConeAndNullFrequenciesMatchAnalyticalMass(
            [Values(0.03f, 0.05f, 0.1f, 0.2f)] float roughness,
            [Values(0.0f, 0.5f)] float metallic,
            [Values(12345, 81723)] int seed)
        {
            const int attempts = 65536;
            double alpha = GgxReferenceAlpha(roughness);
            double pSpec = 0.5 + 0.5 * metallic;
            double[] angles = { 2.0 * Math.Atan(0.5 * alpha), 2.0 * Math.Atan(alpha), 2.0 * Math.Atan(2.0 * alpha), Math.PI / 2.0 };
            var counts = new int[angles.Length + 1];
            Vector4[] results = RunGgxProbe(roughness, metallic, Vector3.up, Vector3.zero, seed, attempts);
            for (int i = 0; i < attempts; i++)
            {
                Vector4 sample = results[2 * i];
                if (results[2 * i + 1].w != 1.0f || !IsFinite(sample) || sample.w < 0.0f)
                    Assert.Fail($"Invalid frequency record at attempt {i}, seed {seed}: {sample}");
                if (sample.w == 0.0f)
                {
                    counts[angles.Length]++;
                    continue;
                }
                if (sample.y <= 0.0f) Assert.Fail($"Positive PDF below surface at attempt {i}, seed {seed}");
                // atan2 retains the narrow cone angle even when float NdotL rounds to one.
                double angle = Math.Atan2(Math.Sqrt((double)sample.x * sample.x + (double)sample.z * sample.z), sample.y);
                for (int cone = 0; cone < angles.Length; cone++)
                    if (angle <= angles[cone]) counts[cone]++;
            }
            for (int cone = 0; cone < counts.Length; cone++)
            {
                double probability = pSpec * alpha * alpha / (1.0 + alpha * alpha);
                if (cone < angles.Length)
                {
                    double t = Math.Tan(angles[cone] / 2.0);
                    double sine = Math.Sin(angles[cone]);
                    probability = pSpec * t * t / (alpha * alpha + t * t) + (1.0 - pSpec) * sine * sine;
                }
                double expectedCount = attempts * probability;
                // Binomial six sigma, plus two counts for discrete RNG/float boundary roundoff.
                double tolerance = 6.0 * Math.Sqrt(attempts * probability * (1.0 - probability)) + 2.0;
                Assert.That(counts[cone], Is.EqualTo(expectedCount).Within(tolerance),
                    $"seed={seed}, cone={cone} (4=null), all {attempts} attempts included");
            }
        }

        private static Vector4[] RunGgxProbe(float roughness, float metallic, Vector3 view, Vector3 light, int seed, int count)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("GGX analytical probes require an active compute graphics device.");
            ComputeShader asset = AssetDatabase.LoadAssetAtPath<ComputeShader>(RegressionProbeShaderPath);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.HasKernel("CSGgxRegressionProbe"), Is.True, "Missing GGX kernel on a compute-capable backend.");
            ComputeShader shader = UnityEngine.Object.Instantiate(asset);
            try
            {
                using (var buffer = new ComputeBuffer(count * 2, sizeof(float) * 4))
                using (var sobol = new ComputeBuffer(32, sizeof(uint)))
                {
                    int kernel = shader.FindKernel("CSGgxRegressionProbe");
                    var results = new Vector4[count * 2];
                    buffer.SetData(results);
                    sobol.SetData(new uint[32]);
                    shader.SetBuffer(kernel, "RegressionResults", buffer);
                    shader.SetBuffer(kernel, "_SobolDirectionNumbers", sobol);
                    shader.SetInt("_SobolDimensionLimit", 1);
                    shader.SetInt("_Seed", seed);
                    shader.SetInt("_GgxProbeSampleCount", count);
                    shader.SetInt("_GgxProbeSample", count > 1 ? 1 : 0);
                    shader.SetFloat("_GgxProbeSmoothness", 1.0f - roughness);
                    shader.SetFloat("_GgxProbeMetallic", metallic);
                    shader.SetVector("_GgxProbeView", view);
                    shader.SetVector("_GgxProbeLight", light);
                    shader.Dispatch(kernel, (count + 63) / 64, 1, 1);
                    buffer.GetData(results);
                    return results;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(shader);
            }
        }

        private static double GgxReferenceAlpha(float roughness)
        {
            float smoothness = 1.0f - roughness;
            double effectiveRoughness = Math.Max((double)0.03f, (double)(1.0f - smoothness));
            return effectiveRoughness * effectiveRoughness;
        }

        private static void GgxDoubleReference(Vector3 view, Vector3 light, double alpha, double metallic,
            out double distribution, out double pdf, out double r, out double g, out double b)
        {
            // Independent double arithmetic: no shader outputs/helpers or float Vector3 math in the oracle.
            double vLength = Math.Sqrt((double)view.x * view.x + (double)view.y * view.y + (double)view.z * view.z);
            double vx = view.x / vLength, vy = view.y / vLength, vz = view.z / vLength;
            double hx = vx + light.x, hy = vy + light.y, hz = vz + light.z;
            double hLength = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            hx /= hLength;
            hy /= hLength;
            hz /= hLength;
            double denominator = hx * hx + hz * hz + alpha * alpha * hy * hy;
            distribution = alpha * alpha / (Math.PI * denominator * denominator);
            double gView = 2.0 * vy / (vy + Math.Sqrt(vy * vy + alpha * alpha * (1.0 - vy * vy)));
            double nl = light.y;
            double gLight = 2.0 * nl / (nl + Math.Sqrt(nl * nl + alpha * alpha * (1.0 - nl * nl)));
            // The VNDF reflection Jacobian cancels VdotH for unit incident/outgoing directions.
            double pSpec = 0.5 + 0.5 * metallic;
            pdf = pSpec * distribution * gView / (4.0 * vy) + (1.0 - pSpec) * nl / Math.PI;
            double vh = Math.Min(1.0, Math.Max(0.0, vx * hx + vy * hy + vz * hz));
            double schlick = Math.Pow(1.0 - vh, 5.0);
            double specular = distribution * gView * gLight / (4.0 * vy * nl);
            double Channel(double albedo)
            {
                double f0 = 0.04 * (1.0 - metallic) + albedo * metallic;
                double fresnel = f0 + (1.0 - f0) * schlick;
                return fresnel * specular + (1.0 - fresnel) * albedo * (1.0 - metallic) / Math.PI;
            }
            r = Channel((double)0.8f);
            g = Channel((double)0.4f);
            b = Channel((double)0.2f);
        }

        private static bool IsFinite(Vector4 value)
        {
            for (int i = 0; i < 4; i++)
                if (float.IsNaN(value[i]) || float.IsInfinity(value[i])) return false;
            return true;
        }

        private static void AssertGgxClose(double actual, double expected, double relativeTolerance, string label)
        {
            Assert.That(actual, Is.EqualTo(expected).Within(Math.Abs(expected) * relativeTolerance + 1e-8), label);
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
                var buffer = new ComputeBuffer(51, sizeof(float) * 4);
                var sphereBuffer = new ComputeBuffer(1, 64);
                var sobolBuffer = new ComputeBuffer(1, sizeof(uint));
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
                shader.SetInt("_Seed", 12345);
                shader.SetBuffer(kernel, "_Spheres", sphereBuffer);
                shader.SetBuffer(kernel, "_SobolDirectionNumbers", sobolBuffer);
                shader.SetBuffer(kernel, "RegressionResults", buffer);
                shader.Dispatch(kernel, 1, 1, 1);

                var results = new Vector4[51];
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
                AssertVector(results[47], new Vector4(0.6496169f, 0.7189237f, 0.7189237f, 1.0f), "pale water retains neutral depth absorption", 0.0002f);
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
                AssertVector(results[48], new Vector4(0.0f, 0.5f, 0.125f, 1.0f),
                    "triangle back-face rejection and physical area geometry", 0.0002f);
                AssertVector(results[49], new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                    "back-facing mesh emitters terminate without radiance");
                AssertVector(results[50], new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                    "non-emissive refractive spheres are not lights despite their object index");
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
                AssertVector(results[42], new Vector4(1.25f, 2.0f, 1.1111111f, 0.8333333f),
                    "local and temporal RIS reservoir normalization", 0.0002f);
                AssertVector(results[43], Vector4.zero, "temporal RIS invalid-history rejection");
                Assert.That(results[44].x, Is.EqualTo(results[44].y).Within(0.00001f),
                    "GGX evaluation must report the visible-normal reflection PDF");
                Assert.That(results[44].w, Is.GreaterThan(0.0001f),
                    "The oblique GGX probe must distinguish VNDF from the old NDF PDF");
                AssertFinitePositiveSample(results[45], results[46]);
                Assert.That(results[45].w, Is.EqualTo(results[46].w).Within(0.00001f),
                    "The sampled GGX VNDF PDF must match shared BRDF evaluation");
            }
            finally
            {
                sphereBuffer.Release();
                sobolBuffer.Release();
                buffer.Release();
            }
        }

        [Test]
        public void ProductionShader_UsesSamplingEfficiencyHotPaths()
        {
            string shader = System.IO.File.ReadAllText(SharedShaderPath);

            StringAssert.Contains("while (low < high)", shader,
                "Mesh-light triangle CDF selection should use logarithmic binary search.");
            StringAssert.Contains("SampleGgxVisibleNormal(hit.normal, viewDirection, GetGgxAlpha(hit), rngState)", shader,
                "Opaque GGX continuation should sample the visible-normal distribution.");
            StringAssert.Contains("float visibleNormalPdf = distribution * GgxSmithG1(normalDotView, alpha)", shader,
                "GGX MIS must use the PDF matching visible-normal sampling.");

            int scatterStart = shader.IndexOf("ScatterResult CreateScatteredRay", StringComparison.Ordinal);
            int waterStart = shader.IndexOf("if (IsWaterMaterial(hit))", scatterStart, StringComparison.Ordinal);
            Assert.That(scatterStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(waterStart, Is.GreaterThan(scatterStart));
            string commonScatterSetup = shader.Substring(scatterStart, waterStart - scatterStart);
            StringAssert.DoesNotContain("GetAlbedo(hit)", commonScatterSetup,
                "Scattering should not fetch an unused albedo before selecting the material path.");
            StringAssert.DoesNotContain("GetRandomizedNormalBasedOnAmount", commonScatterSetup,
                "Opaque and glass paths should not pay for the water-only randomized normal.");

            int lightStart = shader.IndexOf("float3 SampleSingleLight", StringComparison.Ordinal);
            int lightLoop = shader.IndexOf("for (sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)", lightStart, StringComparison.Ordinal);
            Assert.That(lightStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(lightLoop, Is.GreaterThan(lightStart));
            string lightSetup = shader.Substring(lightStart, lightLoop - lightStart);
            StringAssert.Contains("if (!useRisCandidate && !isDirectional", lightSetup,
                "Only ordinary sphere-light samples should build the disk tangent frame.");
            Assert.That(CountOccurrences(lightSetup, "CreateBasisFromNormal"), Is.EqualTo(1),
                "The sphere-light tangent frame should be built once outside the soft-shadow sample loop.");
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
        public void SpatialDenoiser_FeatureControlsAreNotMaskedByObjectIdentity()
        {
            string source = System.IO.File.ReadAllText(DenoiserShaderPath);
            int kernelStart = source.IndexOf("void CSAtrous", StringComparison.Ordinal);
            int nextKernel = source.IndexOf("void CSGeneratePreservationMask", kernelStart, StringComparison.Ordinal);

            Assert.That(kernelStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextKernel, Is.GreaterThan(kernelStart));
            string kernel = source.Substring(kernelStart, nextKernel - kernelStart);
            Assert.That(kernel, Does.Contain("depthDifference / _DepthSigma"));
            Assert.That(kernel, Does.Contain("_NormalPower"));
            Assert.That(kernel, Does.Contain("_AlbedoSigma"));
            Assert.That(kernel, Does.Not.Contain("FeatureIdentity"),
                "An unconditional identity rejection prevents depth, normal, and albedo controls from affecting object boundaries.");
        }

        [Test]
        public void ProductionComputeKernels_AreOwnedByTheirSplitAssets()
        {
            ComputeShader renderer = AssetDatabase.LoadAssetAtPath<ComputeShader>(WavefrontShaderPath);
            ComputeShader utility = AssetDatabase.LoadAssetAtPath<ComputeShader>(UtilityShaderPath);
            ComputeShader features = AssetDatabase.LoadAssetAtPath<ComputeShader>(FeaturesShaderPath);
            ComputeShader focus = AssetDatabase.LoadAssetAtPath<ComputeShader>(FocusShaderPath);

            Assert.That(renderer.HasKernel("CSWavefrontPresent"), Is.True);
            Assert.That(renderer.HasKernel("ClearAccumulation"), Is.False);
            Assert.That(renderer.HasKernel("CSFeatures"), Is.False);
            Assert.That(renderer.HasKernel("CSFocusQuery"), Is.False);
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
                Assert.That(glarePixel.r, Is.GreaterThanOrEqualTo(disabledPixel.r - Epsilon),
                    "Glare must preserve bright source pixels when the backend's presentation path cannot add a visible center-pixel contribution.");
                Assert.That(ReadPixel(withGlare, 17, 16).r, Is.LessThanOrEqualTo(glarePixel.r + Epsilon),
                    "Glare should decay smoothly away from a point source instead of forming a repeated pixel block.");

                present.Invoke(glare, new object[] { source, withoutGlare, 1.0f, false, 0.0f, 1.0f, 4.0f });
                Assert.That(ReadPixel(withoutGlare, 20, 16).r, Is.EqualTo(0.0f).Within(Epsilon),
                    "Disabled glare must not add a halo away from the source pixel.");
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
        public void GameManager_AccumulationStateHash_DoesNotResetWhenPathSamplerChanges()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Path Sampler State Hash Test");
            var cameraObject = new GameObject("Path Sampler State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);

                MethodInfo hashMethod = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);
                int defaultHash = (int)hashMethod.Invoke(manager, null);

                managerType.GetField("sobolDimensionLimit").SetValue(manager, 2568);
                Assert.That(hashMethod.Invoke(manager, null), Is.EqualTo(defaultHash),
                    "Changing an unbiased Sobol/hash sampler boundary must preserve progressive accumulation.");

                managerType.GetField("sobolDimensionLimit").SetValue(manager, 328);
                managerType.GetField("samplingSeed").SetValue(manager, 2);
                Assert.That(hashMethod.Invoke(manager, null), Is.EqualTo(defaultHash),
                    "Changing an unbiased sampling seed must preserve progressive accumulation.");

                managerType.GetField("randomNoise").SetValue(manager, true);
                Assert.That(hashMethod.Invoke(manager, null), Is.EqualTo(defaultHash),
                    "Changing an unbiased random-noise source must preserve progressive accumulation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_CausticSeedRebuildsPhotonMapWithoutResettingCompatibleAccumulation()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Caustic Seed State Hash Test");
            var cameraObject = new GameObject("Caustic Seed State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);
                managerType.GetField("enableCaustics").SetValue(manager, true);

                MethodInfo accumulationHash = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo photonHash = managerType.GetMethod("CalculatePhotonStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(accumulationHash, Is.Not.Null);
                Assert.That(photonHash, Is.Not.Null);
                int initialAccumulationHash = (int)accumulationHash.Invoke(manager, null);
                int initialPhotonHash = (int)photonHash.Invoke(manager, new object[] { true });

                object caustics = managerType.GetProperty("Caustics").GetValue(manager);
                PropertyInfo seed = caustics.GetType().GetProperty("Seed");
                seed.SetValue(caustics, 2);

                Assert.That(accumulationHash.Invoke(manager, null), Is.EqualTo(initialAccumulationHash),
                    "Changing an unbiased caustic photon seed must preserve compatible SPPM accumulation.");
                Assert.That(photonHash.Invoke(manager, new object[] { true }), Is.Not.EqualTo(initialPhotonHash),
                    "Changing the caustic seed must still trigger a photon-map rebuild.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_AccumulationStateHash_DoesNotResetWhenPathGuideMixtureChanges()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Path Guide Mixture State Hash Test");
            var cameraObject = new GameObject("Path Guide Mixture State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);

                MethodInfo hashMethod = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);
                int defaultHash = (int)hashMethod.Invoke(manager, null);

                managerType.GetField("pathGuidingMixtureWeight").SetValue(manager, 0.8f);
                Assert.That(hashMethod.Invoke(manager, null), Is.EqualTo(defaultHash),
                    "Changing an unbiased path-guide mixture must preserve progressive accumulation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_AccumulationStateHash_DoesNotResetWhenPathGuidingChanges()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Path Guiding State Hash Test");
            var cameraObject = new GameObject("Path Guiding State Hash Camera");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);

                MethodInfo hashMethod = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hashMethod, Is.Not.Null);
                int disabledHash = (int)hashMethod.Invoke(manager, null);

                managerType.GetField("enablePathGuiding").SetValue(manager, true);
                Assert.That(hashMethod.Invoke(manager, null), Is.EqualTo(disabledHash),
                    "Toggling an unbiased path-guide proposal must preserve progressive accumulation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_AdaptiveSamplingToggle_IsDisabledByDefault()
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
                    "Uniform sampling must remain the project-wide default render path.");
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

                managerType.GetField("enableAdaptiveSampling").SetValue(manager, false);
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
                managerType.GetField("adaptiveLuminanceErrorWeight").SetValue(manager, -0.5f);
                int changedLuminanceErrorWeightHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveSpatialDisagreementPriority").SetValue(manager, 0.5f);
                int changedSpatialDisagreementHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveGroupRmsScoreBlend").SetValue(manager, 0.5f);
                int changedRmsBlendHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveExplorationShare").SetValue(manager, 0.05f);
                int changedExplorationHash = (int)hashMethod.Invoke(manager, null);
                managerType.GetField("adaptiveHighestBucketSampleRate").SetValue(manager, 4.0f);
                int changedHighestRateHash = (int)hashMethod.Invoke(manager, null);

                Assert.That(adaptiveHash, Is.Not.EqualTo(uniformHash));
                Assert.That(changedPolicyHash, Is.Not.EqualTo(adaptiveHash));
                Assert.That(changedCoarseThresholdHash, Is.Not.EqualTo(changedPolicyHash));
                Assert.That(changedCoarseUpdateLimitHash, Is.Not.EqualTo(changedCoarseThresholdHash));
                Assert.That(changedIntervalHash, Is.Not.EqualTo(changedCoarseUpdateLimitHash));
                Assert.That(changedLuminanceErrorWeightHash, Is.Not.EqualTo(changedIntervalHash));
                Assert.That(changedSpatialDisagreementHash, Is.Not.EqualTo(changedLuminanceErrorWeightHash));
                Assert.That(changedRmsBlendHash, Is.Not.EqualTo(changedSpatialDisagreementHash));
                Assert.That(changedExplorationHash, Is.Not.EqualTo(changedRmsBlendHash));
                Assert.That(changedHighestRateHash, Is.Not.EqualTo(changedExplorationHash));
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

                ComputeShader causticsShader = Resources.Load<ComputeShader>("RayTracingCaustics");
                Assert.That(causticsShader, Is.Not.Null);
                int gatherKernel = causticsShader.FindKernel("CSCausticsDebug");
                var causticsImage = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true
                };
                causticsImage.Create();
                var causticSppmPhotonCount = new RenderTexture(256, 256, 0, RenderTextureFormat.RFloat)
                {
                    enableRandomWrite = true
                };
                causticSppmPhotonCount.Create();
                try
                {
                    managerType.GetMethod("SetShaderParameters", BindingFlags.Instance | BindingFlags.NonPublic,
                            null, new[] { typeof(ComputeShader), typeof(int) }, null)
                        .Invoke(manager, new object[] { causticsShader, gatherKernel });
                    causticsShader.SetTexture(gatherKernel, "Result", causticsImage);
                    causticsShader.SetTexture(gatherKernel, "AccumulationResult", causticsImage);
                    causticsShader.SetTexture(gatherKernel, "CausticSppmState", causticsImage);
                    causticsShader.SetTexture(gatherKernel, "CausticSppmPhotonCount", causticSppmPhotonCount);
                    causticsShader.SetInt("_UseCausticSppm", 0);
                    causticsShader.SetInt("_UseFrameAccumulation", 0);
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
                    causticSppmPhotonCount.Release();
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

        [TestCase(3u, 4u)]
        [TestCase(5u, 6u)]
        [TestCase(7u, 8u)]
        public void CausticSurfaceSamples_HaveBroadJointCoverage(uint firstDimension, uint secondDimension)
        {
            const int gridSize = 16;
            const int sampleCount = 4096;
            var occupied = new bool[gridSize * gridSize];
            int occupiedCount = 0;

            for (uint photonIndex = 0; photonIndex < sampleCount; photonIndex++)
            {
                float first = CausticDecorrelatedSample(photonIndex, firstDimension, 1u, 0u);
                float second = CausticDecorrelatedSample(photonIndex, secondDimension, 1u, 0u);
                int cell = Mathf.Min((int)(first * gridSize), gridSize - 1)
                           + gridSize * Mathf.Min((int)(second * gridSize), gridSize - 1);
                if (occupied[cell]) continue;
                occupied[cell] = true;
                occupiedCount++;
            }

            Assert.That(occupiedCount, Is.GreaterThanOrEqualTo(240),
                "Paired photon coordinates must cover the 2D domain rather than an affine bit-reversal lattice.");
        }

        [Test]
        public void CausticPhotonTracing_UsesDecorrelatedSpatialSamplesAndFirstHitOwnership()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingCausticsKernels.hlsl");
            int start = source.IndexOf("void TraceCausticPhotons", StringComparison.Ordinal);
            int end = source.IndexOf("void BuildCausticGrid", start, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string trace = source.Substring(start, end - start);

            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 3u)"));
            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 4u)"));
            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 5u)"));
            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 6u)"));
            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 7u)"));
            Assert.That(trace, Does.Contain("CausticDecorrelatedSample(id.x, 8u)"));
            Assert.That(trace, Does.Contain("RayHit refractorHit = GetNearestIntersection(photonRay)"));
            Assert.That(trace, Does.Contain("refractorHit.objectIndex == refractorIndex"));
            Assert.That(trace, Does.Contain("refractorHit.meshIndex == _Meshes[refractorMeshIndex].meshIndex"));
            Assert.That(trace, Does.Contain("IsWaterMaterial(refractorHit)"));
            Assert.That(trace, Does.Not.Contain("IntersectSphere(photonRay, refractorHit"));
            Assert.That(trace, Does.Not.Contain("IntersectMeshBvh(photonRay, refractorHit"));
        }

        [TestCase("Caustics", true, true, true, true)]
        [TestCase("Caustics", true, false, true, false)]
        [TestCase("Caustics", true, true, false, false)]
        [TestCase("Caustics", true, false, false, false)]
        [TestCase("Caustics", false, true, true, false)]
        [TestCase("FinalColor", true, false, false, true)]
        [TestCase("FinalColor", false, false, false, false)]
        [TestCase("Normals", true, true, true, false)]
        public void GameManager_FrameAccumulation_RequiresEnabledCausticsAndShaderForDebug(
            string mode, bool accumulationEnabled, bool causticsEnabled, bool hasShader, bool expected)
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null);
            var gameObject = new GameObject("Caustics Accumulation Gating Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = gameObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);
                object temporal = managerType.GetField("_temporalDenoisingManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
                temporal.GetType().GetMethod("Initialize").Invoke(temporal, new object[] { manager });

                FieldInfo modeField = managerType.GetField("debugRenderMode");
                modeField.SetValue(manager, Enum.Parse(modeField.FieldType, mode));
                managerType.GetField("enableFrameAccumulation").SetValue(manager, accumulationEnabled);
                managerType.GetField("enableCaustics").SetValue(manager, causticsEnabled);
                ComputeShader shader = hasShader
                    ? AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Resources/RayTracingCaustics.compute") : null;
                if (hasShader) Assert.That(shader, Is.Not.Null);
                managerType.GetField("causticsShader").SetValue(manager, shader);

                MethodInfo shouldAccumulate = managerType.GetMethod("ShouldUseFrameAccumulation", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(shouldAccumulate, Is.Not.Null);
                Assert.That(shouldAccumulate.Invoke(manager, null), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void GameManager_FinalizeRenderFrame_CountsAccumulatedFramesIncludingCausticsDebug(
            bool useFrameAccumulation, bool dedicatedDebug)
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null);
            var gameObject = new GameObject("Caustics Frame Count Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = gameObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);
                object temporal = managerType.GetField("_temporalDenoisingManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
                temporal.GetType().GetMethod("Initialize").Invoke(temporal, new object[] { manager });
                managerType.GetField("enablePathGuiding").SetValue(manager, false);
                FieldInfo modeField = managerType.GetField("debugRenderMode");
                modeField.SetValue(manager, Enum.Parse(modeField.FieldType, dedicatedDebug ? "Caustics" : "FinalColor"));

                Type frameType = managerType.GetNestedType("RenderFrame", BindingFlags.NonPublic);
                Assert.That(frameType, Is.Not.Null);
                object frame = Activator.CreateInstance(frameType);
                frameType.GetField("useFrameAccumulation").SetValue(frame, useFrameAccumulation);
                frameType.GetField("useDedicatedCausticsDebugKernel").SetValue(frame, dedicatedDebug);
                MethodInfo finalize = managerType.GetMethod("FinalizeRenderFrame", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(finalize, Is.Not.Null);
                FieldInfo accumulatedCount = managerType.GetField("_accumulatedFrameCount", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo renderedCount = managerType.GetField("_renderedFrameCount", BindingFlags.NonPublic | BindingFlags.Instance);
                accumulatedCount.SetValue(manager, 7);
                renderedCount.SetValue(manager, 11L);

                var arguments = new[] { frame };
                for (int i = 1; i <= 2; i++)
                {
                    finalize.Invoke(manager, arguments);
                    Assert.That(accumulatedCount.GetValue(manager), Is.EqualTo(7 + (useFrameAccumulation ? i : 0)));
                    Assert.That(renderedCount.GetValue(manager), Is.EqualTo(11L + i));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_AccumulationStateHash_DistinguishesCausticsDebugFromFinalColor()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null);
            var gameObject = new GameObject("Caustics Mode State Hash Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                Camera camera = gameObject.AddComponent<Camera>();
                Component cameraManager = gameObject.GetComponent(Type.GetType("CameraManager, Assembly-CSharp"));
                cameraManager.GetType().GetField("renderTextureCamera").SetValue(cameraManager, camera);
                managerType.GetField("enableCaustics").SetValue(manager, true);
                FieldInfo modeField = managerType.GetField("debugRenderMode");
                MethodInfo hash = managerType.GetMethod("CalculateAccumulationStateHash", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(hash, Is.Not.Null);

                modeField.SetValue(manager, Enum.Parse(modeField.FieldType, "FinalColor"));
                int finalColorHash = (int)hash.Invoke(manager, null);
                modeField.SetValue(manager, Enum.Parse(modeField.FieldType, "Caustics"));
                Assert.That(hash.Invoke(manager, null), Is.Not.EqualTo(finalColorHash),
                    "Beauty and gather-only radiance must not share progressive history.");
                modeField.SetValue(manager, Enum.Parse(modeField.FieldType, "FinalColor"));
                Assert.That(hash.Invoke(manager, null), Is.EqualTo(finalColorHash),
                    "The mode must affect the state hash without mutating other renderer state.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_PrepareRenderFrame_ResetsBeforePhotonMapAndRecordsHashAfterward()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = source.IndexOf("private void PrepareRenderFrame(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = source.IndexOf("private void DispatchRenderFrame(", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string prepare = source.Substring(start, end - start);

            int previous = -1;
            foreach (string fragment in new[]
            {
                "frame.useFrameAccumulation = ShouldUseFrameAccumulation()",
                "CalculateAccumulationStateHash()",
                "if (!_hasAccumulationStateHash || accumulationStateHash != _accumulationStateHash)",
                "ResetFrameAccumulation();",
                "ResetFrameAccumulation(!_preserveTemporalRisHistoryForNextNonAccumulatedFrame, false);",
                "UpdateCausticPhotonMap();",
                "_accumulationStateHash = CalculateAccumulationStateHash();",
                "_hasAccumulationStateHash = true;"
            })
            {
                int position = prepare.IndexOf(fragment, StringComparison.Ordinal);
                Assert.That(position, Is.GreaterThan(previous),
                    $"Expected {fragment} after the preceding preparation step; photon-map resets must not invalidate the newly recorded hash.");
                previous = position;
            }
        }

        [Test]
        public void CausticsDebugMode_UsesPersistentSppmFluxRadiusAndPhotonCount()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingCausticsKernels.hlsl");
            int start = source.IndexOf("float3 UpdateCausticSppm(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = source.IndexOf("void CSCausticsDebug(", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string update = source.Substring(start, end - start);
            Assert.That(update, Does.Contain("effectiveNewPhotons = alpha * batchGather.photonCount"));
            Assert.That(update, Does.Contain("updatedRadius = max(0.001f, radius * sqrt(radiusRatio))"));
            Assert.That(update, Does.Contain("actualRadiusRatio = updatedRadius * updatedRadius"));
            Assert.That(update, Does.Contain("updatedFlux = (state.rgb + batchGather.flux * _CausticIntensity) * actualRadiusRatio"));
            Assert.That(update, Does.Contain("CausticSppmPhotonCount[pixel] = updatedCount"));
            Assert.That(source, Does.Contain("color = UpdateCausticSppm(id.xy, batchGather)"));
        }

        [Test]
        public void CausticSppm_RadiusFloorPreservesFluxWhenAreaCannotShrink()
        {
            const float minimumRadius = 0.001f;
            const float previousFlux = 3.0f;
            const float batchFlux = 2.0f;
            const float previousCount = 1000.0f;
            const float batchCount = 100.0f;
            const float alpha = 0.05f;

            float countRatio = (previousCount + alpha * batchCount) / (previousCount + batchCount);
            float updatedRadius = Mathf.Max(minimumRadius, minimumRadius * Mathf.Sqrt(countRatio));
            float actualAreaRatio = updatedRadius * updatedRadius / (minimumRadius * minimumRadius);
            float updatedFlux = (previousFlux + batchFlux) * actualAreaRatio;

            Assert.That(updatedRadius, Is.EqualTo(minimumRadius));
            Assert.That(actualAreaRatio, Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(updatedFlux, Is.EqualTo(previousFlux + batchFlux).Within(1e-6f));
            Assert.That((previousFlux + batchFlux) * countRatio, Is.LessThan(updatedFlux),
                "The unclamped count ratio would discard flux after radius reduction has stopped.");
        }

        [Test]
        public void CausticGatherKernels_ShareCameraFilterAndLensSampling()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayTracingCausticsKernels.hlsl");
            int helperStart = source.IndexOf("Ray CreateCausticCameraRay", StringComparison.Ordinal);
            int helperEnd = source.IndexOf("float GetCausticSppmRadius", helperStart, StringComparison.Ordinal);
            string helper = source.Substring(helperStart, helperEnd - helperStart);
            Assert.That(helper, Does.Contain("SetRngDimension(rngState, SampleDimensionLens)"));
            Assert.That(helper, Does.Contain("SampleAperture(rngState)"));

            int debugStart = source.IndexOf("void CSCausticsDebug", StringComparison.Ordinal);
            int finalStart = source.IndexOf("void CSCausticsFinalColor", debugStart, StringComparison.Ordinal);
            string debug = source.Substring(debugStart, finalStart - debugStart);
            string final = source.Substring(finalStart);
            Assert.That(debug, Does.Contain("if (_UseTemporalJitter != 0) uv += _FrameJitterNdc"));
            Assert.That(final, Does.Contain("if (_UseTemporalJitter != 0) uv += _FrameJitterNdc"));
            Assert.That(debug, Does.Contain("CreateCausticCameraRay(uv, rngState)"));
            Assert.That(final, Does.Contain("CreateCausticCameraRay(uv, rngState)"));
            Assert.That(debug, Does.Contain("CausticGather gather = TraceVisibleCausticFlux(ray, gatherRadius, rngState)"));
            Assert.That(final, Does.Contain("CausticGather gather = TraceVisibleCausticFlux(ray, gatherRadius, rngState)"));
            Assert.That(debug, Does.Not.Contain("TraceVisibleCausticRadiance"));
            Assert.That(final, Does.Not.Contain("TraceVisibleCausticRadiance"));
        }

        [Test]
        public void CausticsDebugMode_BindsSharedAccumulatorBeforeDispatch()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int start = source.IndexOf("private void UpdateTextureFromCompute(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = source.IndexOf("private void DispatchAdaptiveSampling(", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string update = source.Substring(start, end - start);
            int binding = update.IndexOf("targetShader.SetTexture(kernelHandle, AccumulationResult, _accumulationTexture);", StringComparison.Ordinal);
            Assert.That(binding, Is.GreaterThanOrEqualTo(0));
            Assert.That(update.IndexOf("ComputeDispatch.Dispatch(targetShader, kernelHandle,", StringComparison.Ordinal),
                Is.GreaterThan(binding));

            start = source.IndexOf("private void BindShaderCameraAndRendererSamplingParameters(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            end = source.IndexOf("private void EnsurePathGuidingResources(", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string sampling = source.Substring(start, end - start);
            Assert.That(sampling, Does.Contain("targetShader.SetInt(UseFrameAccumulation, ShouldUseFrameAccumulation() ? 1 : 0)"));
            Assert.That(sampling, Does.Contain("targetShader.SetInt(FrameCount, _accumulatedFrameCount)"));
            Assert.That(source, Does.Contain("BindCausticSppmState(frame.kernelHandle)"));
        }

        [Test]
        public void CausticsDebugMode_UsesDedicatedGatherKernelWithoutDebugVariant()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            string sharedSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            string causticsSource = System.IO.File.ReadAllText("Assets/Resources/RayTracingCaustics.compute");
            Assert.That(managerSource, Does.Contain(
                "useDedicatedCausticsDebugKernel = enableCaustics && causticsShader != null"));
            Assert.That(managerSource, Does.Contain(
                "frame.computeShader.FindKernel(frame.useDedicatedCausticsDebugKernel ? \"CSCausticsDebug\""));
            Assert.That(managerSource, Does.Contain(
                "&& debugRenderMode == DebugRenderMode.Caustics"));
            Assert.That(managerSource, Does.Contain("DispatchFinalColorCaustics(frame.useFrameAccumulation)"));
            Assert.That(managerSource, Does.Contain("FindKernel(\"CSCausticsFinalColor\")"));
            Assert.That(managerSource, Does.Contain("FindKernel(\"CompositeCaustics\")"));
            Assert.That(causticsSource, Does.Contain("#define CAUSTICS_KERNELS 1"));
            Assert.That(sharedSource, Does.Contain("#if defined(CAUSTICS_KERNELS)\nbool IsCausticReceiver"));
            Assert.That(sharedSource, Does.Not.Contain("radiance += throughput * GatherCausticRadiance(hit)"));
        }

        [Test]
        public void VisibleCaustics_ContinueOnlyThroughSmoothMetalReflections()
        {
            string sharedSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");
            int reflectorStart = sharedSource.IndexOf("bool IsCausticReflector", StringComparison.Ordinal);
            int reflectorEnd = sharedSource.IndexOf("struct CausticGather", reflectorStart, StringComparison.Ordinal);
            int visibilityStart = sharedSource.IndexOf("CausticGather TraceVisibleCausticFlux", StringComparison.Ordinal);
            int visibilityEnd = sharedSource.IndexOf("float3 ClampFirefly", visibilityStart, StringComparison.Ordinal);

            Assert.That(reflectorStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(reflectorEnd, Is.GreaterThan(reflectorStart));
            Assert.That(visibilityStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(visibilityEnd, Is.GreaterThan(visibilityStart));

            string reflector = sharedSource.Substring(reflectorStart, reflectorEnd - reflectorStart);
            string visibility = sharedSource.Substring(visibilityStart, visibilityEnd - visibilityStart);
            Assert.That(reflector, Does.Contain("hit.materialType == MaterialMetal"));
            Assert.That(reflector, Does.Contain("GetMetallicRoughness(hit).y <= 0.20f"));
            Assert.That(visibility, Does.Contain("!IsGlassMaterial(hit) && !IsCausticReflector(hit)"));
            Assert.That(visibility, Does.Contain("CreateScatteredRay(ray, hit"));
        }

        [Test]
        public void WavefrontFinalColor_CompositesDedicatedCausticsAfterBeautyCopy()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int dispatchStart = managerSource.IndexOf("private void DispatchRenderFrame", StringComparison.Ordinal);
            int dispatchEnd = managerSource.IndexOf("private void FinalizeRenderFrame", dispatchStart, StringComparison.Ordinal);
            string dispatch = managerSource.Substring(dispatchStart, dispatchEnd - dispatchStart);

            int wavefrontBeautyCopy = dispatch.IndexOf("IsProductionFinalColorShader(frame.computeShader)", StringComparison.Ordinal);
            int causticsDispatch = dispatch.IndexOf("DispatchFinalColorCaustics(frame.useFrameAccumulation)", StringComparison.Ordinal);
            Assert.That(wavefrontBeautyCopy, Is.GreaterThanOrEqualTo(0));
            Assert.That(causticsDispatch, Is.GreaterThan(wavefrontBeautyCopy));
            Assert.That(dispatch, Does.Contain("Graphics.CopyTexture(_outputTexture, _beautyTexture)"));
            Assert.That(dispatch, Does.Contain("enableCaustics && debugRenderMode == DebugRenderMode.FinalColor"));
        }

        [Test]
        public void CausticPhotonTracing_UsesSharedRngStateWithHashFallback()
        {
            string shaderSource = System.IO.File.ReadAllText("Assets/Scripts/RayTracingCausticsKernels.hlsl");

            Assert.That(shaderSource, Does.Contain("inout RngState rngState"));
            Assert.That(shaderSource, Does.Contain("rngState.dimension = 4096u"));
            Assert.That(shaderSource, Does.Contain("rngState.fallback = causticHashSeed"));
        }

        private static uint Sum(uint[] values)
        {
            uint sum = 0;
            foreach (uint value in values) sum += value;
            return sum;
        }

        private static float CausticDecorrelatedSample(uint photonIndex, uint dimension, uint seed, uint frameIndex)
        {
            uint bits = ShaderHash(seed
                                   ^ unchecked(frameIndex * 2246822519u)
                                   ^ unchecked(photonIndex * 747796405u)
                                   ^ unchecked(dimension * 2891336453u));
            return Mathf.Min((bits + 0.5f) * 2.3283064365386963e-10f, 0.99999994f);
        }

        private static uint ShaderHash(uint value)
        {
            value ^= value >> 16;
            value = unchecked(value * 0x7feb352du);
            value ^= value >> 15;
            value = unchecked(value * 0x846ca68bu);
            value ^= value >> 16;
            return value;
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
