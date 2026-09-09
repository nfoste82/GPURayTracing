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
        private ComputeBuffer _counters;
        private ComputeBuffer _dispatchArguments;
        private RenderTexture _frameResult;
        private int _capacity;

        public void EnsureResources(Vector2Int size)
        {
            int capacity = Mathf.Max(1, size.x * size.y);
            if (_capacity == capacity && _frameResult != null) return;
            ReleaseResources();
            _capacity = capacity;
            _paths = new ComputeBuffer(capacity, PathStateStride);
            _hits = new ComputeBuffer(capacity, HitRecordStride);
            _currentQueue = new ComputeBuffer(capacity, sizeof(uint));
            _nextQueue = new ComputeBuffer(capacity, sizeof(uint));
            _completedQueue = new ComputeBuffer(capacity, sizeof(uint));
            _shadowWork = new ComputeBuffer(capacity, sizeof(uint) + sizeof(float) * 3);
            _emptyFirstDirectLight = new ComputeBuffer(1, sizeof(float) * 3);
            _counters = new ComputeBuffer(4, sizeof(uint));
            _dispatchArguments = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            _frameResult = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            _frameResult.Create();
        }

        public void Dispatch(ComputeShader shader, Vector2Int size, int passes, int bounces,
            bool useDirectLightDebug, bool usePathDiagnostics, Action<ComputeShader, int> bindShared, RenderTexture output, RenderTexture accumulation)
        {
            EnsureResources(size);
            if (useDirectLightDebug && _firstDirectLight == null)
                _firstDirectLight = new ComputeBuffer(_capacity, sizeof(float) * 3);
            if (!useDirectLightDebug && _firstDirectLight != null)
            {
                _firstDirectLight.Release();
                _firstDirectLight = null;
            }
            if (usePathDiagnostics && _pathDiagnostics == null)
                _pathDiagnostics = new ComputeBuffer(_capacity, sizeof(float) * 4);
            if (!usePathDiagnostics && _pathDiagnostics != null)
            {
                _pathDiagnostics.Release();
                _pathDiagnostics = null;
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
            int present = shader.FindKernel("CSWavefrontPresent");
            int queueGroups = Mathf.CeilToInt(_capacity / (float)ThreadCount);

            for (int pass = 0; pass < Mathf.Max(1, passes); pass++)
            {
                BindAndDispatch(shader, clearQueues, bindShared, output, accumulation, 1, 1, 1, pass);
                BindAndDispatch(shader, generate, bindShared, output, accumulation,
                    Mathf.CeilToInt(size.x / 4.0f), Mathf.CeilToInt(size.y / 4.0f), 1, pass);

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
            shader.SetBuffer(kernel, "_WavefrontPathDiagnostics", _pathDiagnostics ?? (_emptyPathDiagnostics ??= new ComputeBuffer(1, sizeof(float) * 4)));
            shader.SetBuffer(kernel, "_WavefrontCounters", _counters);
            shader.SetBuffer(kernel, "_WavefrontDispatchArgs", _dispatchArguments);
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
            _counters?.Release();
            _dispatchArguments?.Release();
            _frameResult?.Release();
            _paths = _hits = _currentQueue = _nextQueue = _completedQueue = _shadowWork = _firstDirectLight = _emptyFirstDirectLight = _pathDiagnostics = _emptyPathDiagnostics = _counters = _dispatchArguments = null;
            _frameResult = null;
            _capacity = 0;
        }
    }
}
