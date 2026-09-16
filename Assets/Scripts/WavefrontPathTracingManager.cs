using System;
using System.Collections.Generic;
using UnityEngine;

namespace PathTracing
{
    /// <summary>Owns the pixel-domain queues used by the staged camera path tracer.</summary>
    public sealed class WavefrontPathTracingManager
    {
        // These match the scalar StructuredBuffer layouts in RayTracingWavefront.compute.
        private const int PathStateStride = 352;
        private const int HitRecordStride = 144;
        private const int ThreadCount = 64;

        private static readonly int WavefrontAdaptiveSampling = Shader.PropertyToID("_WavefrontAdaptiveSampling");
        private static readonly int AdaptiveSampleLayer = Shader.PropertyToID("_AdaptiveSampleLayer");
        private static readonly int WavefrontPassIndex = Shader.PropertyToID("_WavefrontPassIndex");
        private static readonly int WavefrontThreadsPerGroup = Shader.PropertyToID("_WavefrontThreadsPerGroup");
        private static readonly int WavefrontPaths = Shader.PropertyToID("_WavefrontPaths");
        private static readonly int WavefrontHits = Shader.PropertyToID("_WavefrontHits");
        private static readonly int WavefrontCurrentQueue = Shader.PropertyToID("_WavefrontCurrentQueue");
        private static readonly int WavefrontNextQueue = Shader.PropertyToID("_WavefrontNextQueue");
        private static readonly int WavefrontCompletedQueue = Shader.PropertyToID("_WavefrontCompletedQueue");
        private static readonly int WavefrontShadowWork = Shader.PropertyToID("_WavefrontShadowWork");
        private static readonly int WavefrontFirstDirectLight = Shader.PropertyToID("_WavefrontFirstDirectLight");
        private static readonly int WavefrontPathDiagnostics = Shader.PropertyToID("_WavefrontPathDiagnostics");
        private static readonly int WavefrontPathGuideStates = Shader.PropertyToID("_WavefrontPathGuideStates");
        private static readonly int WavefrontCounters = Shader.PropertyToID("_WavefrontCounters");
        private static readonly int WavefrontDispatchArgs = Shader.PropertyToID("_WavefrontDispatchArgs");
        private static readonly int WavefrontAdaptiveRadiance = Shader.PropertyToID("_WavefrontAdaptiveRadiance");
        private static readonly int WavefrontPixelCapacity = Shader.PropertyToID("_WavefrontPixelCapacity");
        private static readonly int WavefrontFrameResult = Shader.PropertyToID("_WavefrontFrameResult");
        private static readonly int Result = Shader.PropertyToID("Result");
        private static readonly int AccumulationResult = Shader.PropertyToID("AccumulationResult");

        private ComputeBuffer _paths;
        private ComputeBuffer _hits;
        private ComputeBuffer _currentQueue;
        private ComputeBuffer _nextQueue;
        private ComputeBuffer _completedQueue;
        private ComputeBuffer _shadowWork;
        private ComputeBuffer _firstDirectLight;
        private ComputeBuffer _emptyFirstDirectLight;
        private ComputeBuffer _pathDiagnostics;
        private ComputeBuffer _emptyPathDiagnostics;
        private ComputeBuffer _pathGuideStates;
        private ComputeBuffer _emptyPathGuideStates;
        private ComputeBuffer _counters;
        private ComputeBuffer _dispatchArguments;
        private ComputeBuffer _adaptiveRadiance;
        private RenderTexture _frameResult;
        private int _capacity;
        private int _pixelCapacity;
        private readonly Dictionary<ComputeShader, Kernels> _kernelsByShader = new Dictionary<ComputeShader, Kernels>();

        private sealed class Kernels
        {
            public readonly int ClearFrame;
            public readonly int ClearQueues;
            public readonly int Generate;
            public readonly int BuildDispatchArgs;
            public readonly int Intersect;
            public readonly int Classify;
            public readonly int DirectLight;
            public readonly int ClearShadowQueue;
            public readonly int TraceShadows;
            public readonly int RecordDirectLightGuides;
            public readonly int ResolveShadowWork;
            public readonly int Scatter;
            public readonly int CopyNextQueue;
            public readonly int PublishNextQueue;
            public readonly int RetireCurrentQueue;
            public readonly int Resolve;
            public readonly int RecordPathGuides;
            public readonly int ResolveAdaptive;
            public readonly int Present;

            public Kernels(ComputeShader shader)
            {
                ClearFrame = shader.FindKernel("CSWavefrontClearFrame");
                ClearQueues = shader.FindKernel("CSWavefrontClearQueues");
                Generate = shader.FindKernel("CSWavefrontGenerate");
                BuildDispatchArgs = shader.FindKernel("CSWavefrontBuildDispatchArgs");
                Intersect = shader.FindKernel("CSWavefrontIntersect");
                Classify = shader.FindKernel("CSWavefrontClassify");
                DirectLight = shader.FindKernel("CSWavefrontDirectLight");
                ClearShadowQueue = shader.FindKernel("CSWavefrontClearShadowQueue");
                TraceShadows = shader.FindKernel("CSWavefrontTraceShadows");
                RecordDirectLightGuides = shader.FindKernel("CSWavefrontRecordDirectLightGuides");
                ResolveShadowWork = shader.FindKernel("CSWavefrontResolveShadowWork");
                Scatter = shader.FindKernel("CSWavefrontScatter");
                CopyNextQueue = shader.FindKernel("CSWavefrontCopyNextQueue");
                PublishNextQueue = shader.FindKernel("CSWavefrontPublishNextQueue");
                RetireCurrentQueue = shader.FindKernel("CSWavefrontRetireCurrentQueue");
                Resolve = shader.FindKernel("CSWavefrontResolve");
                RecordPathGuides = shader.FindKernel("CSWavefrontRecordPathGuides");
                ResolveAdaptive = shader.FindKernel("CSWavefrontResolveAdaptive");
                Present = shader.FindKernel("CSWavefrontPresent");
            }
        }

        public void EnsureResources(Vector2Int size, int adaptiveLayers = 0)
        {
            int pixelCapacity = Mathf.Max(1, size.x * size.y);
            int layerCapacity = Mathf.Max(1, adaptiveLayers);
            int capacity = checked(pixelCapacity * layerCapacity);
            if (_capacity == capacity && _pixelCapacity == pixelCapacity && _frameResult != null) return;
            ReleaseResources();
            _capacity = capacity;
            _pixelCapacity = pixelCapacity;
            _paths = new ComputeBuffer(capacity, PathStateStride);
            _hits = new ComputeBuffer(capacity, HitRecordStride);
            _currentQueue = new ComputeBuffer(capacity, sizeof(uint));
            _nextQueue = new ComputeBuffer(capacity, sizeof(uint));
            _completedQueue = new ComputeBuffer(capacity, sizeof(uint));
            _shadowWork = new ComputeBuffer(capacity, sizeof(uint) + sizeof(float) * 3);
            _emptyFirstDirectLight = new ComputeBuffer(1, sizeof(float) * 3);
            _counters = new ComputeBuffer(4, sizeof(uint));
            _dispatchArguments = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            _adaptiveRadiance = new ComputeBuffer(capacity, sizeof(float) * 3);
            _frameResult = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            _frameResult.Create();
        }

        public void Dispatch(ComputeShader shader, Vector2Int size, int passes, int bounces, int adaptiveLayers,
            bool useDirectLightDebug, bool usePathDiagnostics, bool usePathGuiding, Action<ComputeShader, int> bindShared, RenderTexture output, RenderTexture accumulation)
        {
            EnsureResources(size, adaptiveLayers);
            if (useDirectLightDebug && _firstDirectLight == null)
                _firstDirectLight = new ComputeBuffer(_capacity, sizeof(float) * 3);
            if (!useDirectLightDebug && _firstDirectLight != null)
            {
                _firstDirectLight.Release();
                _firstDirectLight = null;
            }
            if (usePathDiagnostics && _pathDiagnostics == null)
                _pathDiagnostics = new ComputeBuffer(_capacity, sizeof(float) * 7);
            if (!usePathDiagnostics && _pathDiagnostics != null)
            {
                _pathDiagnostics.Release();
                _pathDiagnostics = null;
            }
            if (usePathGuiding && _pathGuideStates == null)
                _pathGuideStates = new ComputeBuffer(_capacity, sizeof(float) * 10);
            if (!usePathGuiding && _pathGuideStates != null)
            {
                _pathGuideStates.Release();
                _pathGuideStates = null;
            }
            Kernels kernels = GetKernels(shader);
            Bind(shader, kernels.ClearFrame, output, accumulation);
            bindShared(shader, kernels.ClearFrame);
            ComputeDispatch.Dispatch(shader, kernels.ClearFrame, Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1);

            int pathPasses = adaptiveLayers > 0 ? 1 : Mathf.Max(1, passes);
            for (int pass = 0; pass < pathPasses; pass++)
            {
                shader.SetInt(WavefrontAdaptiveSampling, adaptiveLayers > 0 ? 1 : 0);
                shader.SetInt(AdaptiveSampleLayer, pass);

                BindAndDispatch(shader, kernels.ClearQueues, bindShared, output, accumulation, 1, 1, 1, pass);
                BindAndDispatch(shader, kernels.Generate, bindShared, output, accumulation,
                    Mathf.CeilToInt(size.x * Mathf.Max(1, adaptiveLayers) / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, pass);

                for (int bounce = 0; bounce < Mathf.Max(1, bounces); bounce++)
                {
                    BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, ThreadCount);
                    BindAndDispatchIndirect(shader, kernels.Intersect, bindShared, output, accumulation);

                    BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, 4);
                    BindAndDispatchIndirect(shader, kernels.Classify, bindShared, output, accumulation);

                    BindAndDispatch(shader, kernels.ClearShadowQueue, bindShared, output, accumulation, 1, 1, 1, 0);
                    BindAndDispatchIndirect(shader, kernels.DirectLight, bindShared, output, accumulation);

                    BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 3, 4);
                    BindAndDispatchIndirect(shader, kernels.TraceShadows, bindShared, output, accumulation);

                    if (usePathGuiding)
                        BindAndDispatchIndirect(shader, kernels.RecordDirectLightGuides, bindShared, output, accumulation);

                    BindAndDispatchIndirect(shader, kernels.ResolveShadowWork, bindShared, output, accumulation);

                    // Shadow dispatches overwrite the indirect arguments with the shadow-work count.
                    // Scatter must still process every current path, not only paths with direct light.
                    BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, 4);
                    BindAndDispatchIndirect(shader, kernels.Scatter, bindShared, output, accumulation);

                    BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 1, ThreadCount);
                    BindAndDispatchIndirect(shader, kernels.CopyNextQueue, bindShared, output, accumulation);

                    BindAndDispatch(shader, kernels.PublishNextQueue, bindShared, output, accumulation, 1, 1, 1, 0);
                }

                // Evaluate sky or emitter radiance reached by the final allowed scatter.
                BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, ThreadCount);
                BindAndDispatchIndirect(shader, kernels.Intersect, bindShared, output, accumulation);

                BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, 4);
                BindAndDispatchIndirect(shader, kernels.Classify, bindShared, output, accumulation);

                BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 0, ThreadCount);
                BindAndDispatchIndirect(shader, kernels.RetireCurrentQueue, bindShared, output, accumulation);

                BuildQueueDispatch(shader, kernels.BuildDispatchArgs, bindShared, output, accumulation, 2, ThreadCount);
                BindAndDispatchIndirect(shader, kernels.Resolve, bindShared, output, accumulation);

                if (usePathGuiding)
                    BindAndDispatchIndirect(shader, kernels.RecordPathGuides, bindShared, output, accumulation);
            }

            if (adaptiveLayers > 0)
            {
                BindAndDispatch(shader, kernels.ResolveAdaptive, bindShared, output, accumulation,
                    Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, 0);
            }

            shader.SetInt(WavefrontAdaptiveSampling, adaptiveLayers > 0 ? 1 : 0);

            BindAndDispatch(shader, kernels.Present, bindShared, output, accumulation,
                Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, 0);
        }

        private void BindAndDispatch(ComputeShader shader, int kernel, Action<ComputeShader, int> bindShared,
            RenderTexture output, RenderTexture accumulation, int x, int y, int z, int passIndex)
        {
            Bind(shader, kernel, output, accumulation);
            shader.SetInt(WavefrontPassIndex, passIndex);
            bindShared(shader, kernel);
            ComputeDispatch.Dispatch(shader, kernel, x, y, z);
        }

        private void BuildQueueDispatch(ComputeShader shader, int kernel, Action<ComputeShader, int> bindShared,
            RenderTexture output, RenderTexture accumulation, int queueSelector, int threadsPerGroup)
        {
            Bind(shader, kernel, output, accumulation);
            shader.SetInt(WavefrontPassIndex, queueSelector);
            shader.SetInt(WavefrontThreadsPerGroup, threadsPerGroup);
            bindShared(shader, kernel);
            ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
        }

        private void BindAndDispatchIndirect(ComputeShader shader, int kernel, Action<ComputeShader, int> bindShared,
            RenderTexture output, RenderTexture accumulation)
        {
            Bind(shader, kernel, output, accumulation);
            bindShared(shader, kernel);
            ComputeDispatch.DispatchIndirect(shader, kernel, _dispatchArguments);
        }

        private void Bind(ComputeShader shader, int kernel, RenderTexture output, RenderTexture accumulation)
        {
            shader.SetBuffer(kernel, WavefrontPaths, _paths);
            shader.SetBuffer(kernel, WavefrontHits, _hits);
            shader.SetBuffer(kernel, WavefrontCurrentQueue, _currentQueue);
            shader.SetBuffer(kernel, WavefrontNextQueue, _nextQueue);
            shader.SetBuffer(kernel, WavefrontCompletedQueue, _completedQueue);
            shader.SetBuffer(kernel, WavefrontShadowWork, _shadowWork);
            shader.SetBuffer(kernel, WavefrontFirstDirectLight, _firstDirectLight ?? _emptyFirstDirectLight);
            shader.SetBuffer(kernel, WavefrontPathDiagnostics, _pathDiagnostics ?? (_emptyPathDiagnostics ??= new ComputeBuffer(1, sizeof(float) * 7)));
            shader.SetBuffer(kernel, WavefrontPathGuideStates, _pathGuideStates ?? (_emptyPathGuideStates ??= new ComputeBuffer(1, sizeof(float) * 10)));
            shader.SetBuffer(kernel, WavefrontCounters, _counters);
            shader.SetBuffer(kernel, WavefrontDispatchArgs, _dispatchArguments);
            shader.SetBuffer(kernel, WavefrontAdaptiveRadiance, _adaptiveRadiance);
            shader.SetInt(WavefrontPixelCapacity, _pixelCapacity);
            shader.SetTexture(kernel, WavefrontFrameResult, _frameResult);
            shader.SetTexture(kernel, Result, output);
            shader.SetTexture(kernel, AccumulationResult, accumulation);
        }

        private Kernels GetKernels(ComputeShader shader)
        {
            if (!_kernelsByShader.TryGetValue(shader, out Kernels kernels))
            {
                kernels = new Kernels(shader);
                _kernelsByShader.Add(shader, kernels);
            }
            return kernels;
        }

        public void ReleaseResources()
        {
            _paths?.Release();
            _hits?.Release();
            _currentQueue?.Release();
            _nextQueue?.Release();
            _completedQueue?.Release();
            _shadowWork?.Release();
            _firstDirectLight?.Release();
            _emptyFirstDirectLight?.Release();
            _pathDiagnostics?.Release();
            _emptyPathDiagnostics?.Release();
            _pathGuideStates?.Release();
            _emptyPathGuideStates?.Release();
            _counters?.Release();
            _dispatchArguments?.Release();
            _adaptiveRadiance?.Release();
            _frameResult?.Release();
            _paths = _hits = _currentQueue = _nextQueue = _completedQueue = _shadowWork = _firstDirectLight = _emptyFirstDirectLight = _pathDiagnostics = _emptyPathDiagnostics = _pathGuideStates = _emptyPathGuideStates = _counters = _dispatchArguments = _adaptiveRadiance = null;
            _frameResult = null;
            _capacity = 0;
            _pixelCapacity = 0;
        }
    }
}
