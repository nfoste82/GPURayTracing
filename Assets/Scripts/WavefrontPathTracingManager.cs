using System;
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
            int clearFrame = shader.FindKernel("CSWavefrontClearFrame");
            Bind(shader, clearFrame, output, accumulation);
            bindShared(shader, clearFrame);
            ComputeDispatch.Dispatch(shader, clearFrame, Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1);

            int clearQueues = shader.FindKernel("CSWavefrontClearQueues");
            int generate = shader.FindKernel("CSWavefrontGenerate");
            int buildDispatchArgs = shader.FindKernel("CSWavefrontBuildDispatchArgs");
            int intersect = shader.FindKernel("CSWavefrontIntersect");
            int classify = shader.FindKernel("CSWavefrontClassify");
            int directLight = shader.FindKernel("CSWavefrontDirectLight");
            int clearShadowQueue = shader.FindKernel("CSWavefrontClearShadowQueue");
            int traceShadows = shader.FindKernel("CSWavefrontTraceShadows");
            int resolveShadowWork = shader.FindKernel("CSWavefrontResolveShadowWork");
            int scatter = shader.FindKernel("CSWavefrontScatter");
            int copyNextQueue = shader.FindKernel("CSWavefrontCopyNextQueue");
            int publishNextQueue = shader.FindKernel("CSWavefrontPublishNextQueue");
            int retireCurrentQueue = shader.FindKernel("CSWavefrontRetireCurrentQueue");
            int resolve = shader.FindKernel("CSWavefrontResolve");
            int resolveAdaptive = shader.FindKernel("CSWavefrontResolveAdaptive");
            int present = shader.FindKernel("CSWavefrontPresent");

            int pathPasses = adaptiveLayers > 0 ? 1 : Mathf.Max(1, passes);
            for (int pass = 0; pass < pathPasses; pass++)
            {
                shader.SetInt("_WavefrontAdaptiveSampling", adaptiveLayers > 0 ? 1 : 0);
                shader.SetInt("_AdaptiveSampleLayer", pass);
                BindAndDispatch(shader, clearQueues, bindShared, output, accumulation, 1, 1, 1, pass);
                BindAndDispatch(shader, generate, bindShared, output, accumulation,
                    Mathf.CeilToInt(size.x * Mathf.Max(1, adaptiveLayers) / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, pass);

                for (int bounce = 0; bounce < Mathf.Max(1, bounces); bounce++)
                {
                    BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 0, ThreadCount);
                    BindAndDispatchIndirect(shader, intersect, bindShared, output, accumulation);
                    BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 0, 4);
                    BindAndDispatchIndirect(shader, classify, bindShared, output, accumulation);
                    BindAndDispatch(shader, clearShadowQueue, bindShared, output, accumulation, 1, 1, 1, 0);
                    BindAndDispatchIndirect(shader, directLight, bindShared, output, accumulation);
                    BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 3, 4);
                    BindAndDispatchIndirect(shader, traceShadows, bindShared, output, accumulation);
                    BindAndDispatchIndirect(shader, resolveShadowWork, bindShared, output, accumulation);
                    // Shadow dispatches overwrite the indirect arguments with the shadow-work count.
                    // Scatter must still process every current path, not only paths with direct light.
                    BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 0, 4);
                    BindAndDispatchIndirect(shader, scatter, bindShared, output, accumulation);
                    BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 1, ThreadCount);
                    BindAndDispatchIndirect(shader, copyNextQueue, bindShared, output, accumulation);
                    BindAndDispatch(shader, publishNextQueue, bindShared, output, accumulation, 1, 1, 1, 0);
                }

                BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 0, ThreadCount);
                BindAndDispatchIndirect(shader, retireCurrentQueue, bindShared, output, accumulation);
                BuildQueueDispatch(shader, buildDispatchArgs, bindShared, output, accumulation, 2, ThreadCount);
                BindAndDispatchIndirect(shader, resolve, bindShared, output, accumulation);
            }

            if (adaptiveLayers > 0)
            {
                BindAndDispatch(shader, resolveAdaptive, bindShared, output, accumulation,
                    Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, 0);
            }

            shader.SetInt("_WavefrontAdaptiveSampling", adaptiveLayers > 0 ? 1 : 0);
            BindAndDispatch(shader, present, bindShared, output, accumulation,
                Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, 0);
        }

        private void BindAndDispatch(ComputeShader shader, int kernel, Action<ComputeShader, int> bindShared,
            RenderTexture output, RenderTexture accumulation, int x, int y, int z, int passIndex)
        {
            Bind(shader, kernel, output, accumulation);
            shader.SetInt("_WavefrontPassIndex", passIndex);
            bindShared(shader, kernel);
            ComputeDispatch.Dispatch(shader, kernel, x, y, z);
        }

        private void BuildQueueDispatch(ComputeShader shader, int kernel, Action<ComputeShader, int> bindShared,
            RenderTexture output, RenderTexture accumulation, int queueSelector, int threadsPerGroup)
        {
            Bind(shader, kernel, output, accumulation);
            shader.SetInt("_WavefrontPassIndex", queueSelector);
            shader.SetInt("_WavefrontThreadsPerGroup", threadsPerGroup);
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
            shader.SetBuffer(kernel, "_WavefrontPaths", _paths);
            shader.SetBuffer(kernel, "_WavefrontHits", _hits);
            shader.SetBuffer(kernel, "_WavefrontCurrentQueue", _currentQueue);
            shader.SetBuffer(kernel, "_WavefrontNextQueue", _nextQueue);
            shader.SetBuffer(kernel, "_WavefrontCompletedQueue", _completedQueue);
            shader.SetBuffer(kernel, "_WavefrontShadowWork", _shadowWork);
            shader.SetBuffer(kernel, "_WavefrontFirstDirectLight", _firstDirectLight ?? _emptyFirstDirectLight);
            shader.SetBuffer(kernel, "_WavefrontPathDiagnostics", _pathDiagnostics ?? (_emptyPathDiagnostics ??= new ComputeBuffer(1, sizeof(float) * 7)));
            shader.SetBuffer(kernel, "_WavefrontPathGuideStates", _pathGuideStates ?? (_emptyPathGuideStates ??= new ComputeBuffer(1, sizeof(float) * 10)));
            shader.SetBuffer(kernel, "_WavefrontCounters", _counters);
            shader.SetBuffer(kernel, "_WavefrontDispatchArgs", _dispatchArguments);
            shader.SetBuffer(kernel, "_WavefrontAdaptiveRadiance", _adaptiveRadiance);
            shader.SetInt("_WavefrontPixelCapacity", _pixelCapacity);
            shader.SetTexture(kernel, "_WavefrontFrameResult", _frameResult);
            shader.SetTexture(kernel, "Result", output);
            shader.SetTexture(kernel, "AccumulationResult", accumulation);
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
