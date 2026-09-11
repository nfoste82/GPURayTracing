using UnityEngine;

namespace PathTracing.Lighting
{
    // Reservoir records use structured buffers so the wavefront renderer retains enough UAV slots for HDR
    // output and accumulation textures on Metal.
    public sealed class TemporalRisManager
    {
        private static readonly int Enabled = Shader.PropertyToID("_TemporalRisEnabled");
        private static readonly int HistoryValid = Shader.PropertyToID("_TemporalRisHistoryValid");
        private static readonly int Unsupported = Shader.PropertyToID("_TemporalRisUnsupported");
        private static readonly int MaxM = Shader.PropertyToID("_TemporalRisMaxM");
        private static readonly int DepthThreshold = Shader.PropertyToID("_TemporalRisDepthThreshold");
        private static readonly int NormalThreshold = Shader.PropertyToID("_TemporalRisNormalThreshold");
        private static readonly int TextureSize = Shader.PropertyToID("_TemporalRisTextureSize");
        private static readonly int CurrentViewProjection = Shader.PropertyToID("_TemporalRisCurrentViewProjection");
        private static readonly int PreviousViewProjection = Shader.PropertyToID("_TemporalRisPreviousViewProjection");
        private static readonly int PreviousReservoir = Shader.PropertyToID("_TemporalRisPreviousReservoir");
        private static readonly int NextReservoir = Shader.PropertyToID("_TemporalRisNextReservoir");
        private static readonly int PreviousNormal = Shader.PropertyToID("_TemporalRisPreviousNormal");
        private static readonly int PreviousDepth = Shader.PropertyToID("_TemporalRisPreviousDepth");
        private static readonly int PreviousIdentity = Shader.PropertyToID("_TemporalRisPreviousIdentity");
        private static readonly int PreviousValidity = Shader.PropertyToID("_TemporalRisPreviousValidity");
        private static readonly int Diagnostics = Shader.PropertyToID("_TemporalRisDiagnostics");
        private static readonly int SpatialEnabled = Shader.PropertyToID("_SpatialRisEnabled");
        private static readonly int SpatialNeighborCount = Shader.PropertyToID("_SpatialRisNeighborCount");
        private static readonly int SpatialLocalReservoir = Shader.PropertyToID("_SpatialRisLocalReservoir");
        private static readonly int SpatialPostCandidateRng = Shader.PropertyToID("_SpatialRisPostCandidateRng");
        private static readonly int SpatialLocalWriteReservoir = Shader.PropertyToID("_SpatialRisLocalWriteReservoir");
        private static readonly int SpatialPostCandidateRngWrite = Shader.PropertyToID("_SpatialRisPostCandidateRngWrite");
        private static readonly int SpatialReceiverNormal = Shader.PropertyToID("_SpatialRisReceiverNormal");
        private static readonly int SpatialReceiverDepth = Shader.PropertyToID("_SpatialRisReceiverDepth");
        private static readonly int SpatialReceiverIdentity = Shader.PropertyToID("_SpatialRisReceiverIdentity");
        private static readonly int SpatialReceiverValidity = Shader.PropertyToID("_SpatialRisReceiverValidity");

        private const int ReservoirStride = sizeof(float) * 16;
        public const int DiagnosticsCount = 10;
        public const int EligibleCount = 0;
        public const int HistoryAcceptedCount = 1;
        public const int HistoryOutOfBoundsCount = 2;
        public const int HistoryFeatureRejectedCount = 3;
        public const int HistoryReservoirRejectedCount = 4;
        public const int HistoryZeroTargetCount = 5;
        public const int HistoryMergedCount = 6;
        public const int HistorySelectedCount = 7;
        public const int RetainedMTotal = 8;
        public const int EffectiveMTotal = 9;
        private readonly ComputeBuffer[] _reservoirs = new ComputeBuffer[2];
        private ComputeBuffer _diagnostics;
        private ComputeBuffer _spatialLocalReservoir;
        private ComputeBuffer _spatialPostCandidateRng;
        private readonly RenderTexture[] _normal = new RenderTexture[2];
        private readonly RenderTexture[] _depth = new RenderTexture[2];
        private readonly RenderTexture[] _identity = new RenderTexture[2];
        private readonly RenderTexture[] _validity = new RenderTexture[2];
        private bool _readIsA = true;
        private bool _historyValid;
        private Matrix4x4 _previousViewProjection;

        public void EnsureResources(Vector2Int size)
        {
            if (_reservoirs[0] != null) return;
            var count = Mathf.Max(1, size.x * size.y);
            for (var i = 0; i < 2; i++)
            {
                _reservoirs[i] = new ComputeBuffer(count, ReservoirStride);
                _normal[i] = CreateTexture(size, RenderTextureFormat.ARGBHalf);
                _depth[i] = CreateTexture(size, RenderTextureFormat.RHalf);
                _identity[i] = CreateTexture(size, RenderTextureFormat.RFloat);
                _validity[i] = CreateTexture(size, RenderTextureFormat.RHalf);
            }
            _diagnostics = new ComputeBuffer(DiagnosticsCount, sizeof(uint));
            _spatialLocalReservoir = new ComputeBuffer(count, ReservoirStride);
            _spatialPostCandidateRng = new ComputeBuffer(count, sizeof(uint));
        }

        public void BindSpatialPrepass(ComputeShader shader, int kernel, GameManager gameManager)
        {
            EnsureResources(gameManager.TextureSize);
            shader.SetInt(SpatialEnabled, 1);
            shader.SetInt(SpatialNeighborCount, gameManager.Lighting.SpatialRisNeighborCount);
            shader.SetInt(Unsupported, 0);
            shader.SetInt(MaxM, gameManager.Lighting.InitialRisCandidateCount + gameManager.Lighting.SpatialRisNeighborCount);
            shader.SetFloat(DepthThreshold, 0.05f);
            shader.SetFloat(NormalThreshold, 0.9f);
            shader.SetInts(TextureSize, gameManager.TextureSize.x, gameManager.TextureSize.y);
            shader.SetBuffer(kernel, SpatialLocalWriteReservoir, _spatialLocalReservoir);
            shader.SetBuffer(kernel, SpatialPostCandidateRngWrite, _spatialPostCandidateRng);
        }

        public void BindSpatialResolve(ComputeShader shader, int kernel, GameManager gameManager)
        {
            EnsureResources(gameManager.TextureSize);
            shader.SetBuffer(kernel, SpatialLocalReservoir, _spatialLocalReservoir);
            shader.SetBuffer(kernel, SpatialPostCandidateRng, _spatialPostCandidateRng);
            shader.SetTexture(kernel, SpatialReceiverNormal, gameManager.FeatureNormalTexture);
            shader.SetTexture(kernel, SpatialReceiverDepth, gameManager.FeatureDepthTexture);
            shader.SetTexture(kernel, SpatialReceiverIdentity, gameManager.FeatureIdentityTexture);
            shader.SetTexture(kernel, SpatialReceiverValidity, gameManager.FeatureValidityTexture);
        }

        public void Bind(ComputeShader shader, int kernel, GameManager gameManager, bool enabled, bool unsupported)
        {
            EnsureResources(gameManager.TextureSize);
            var read = _readIsA ? 0 : 1;
            var write = _readIsA ? 1 : 0;
            var camera = gameManager.renderTextureCamera;
            // The renderer maps its own pixel coordinates to clip space rather than rendering through a
            // camera target. Asking Unity for a render-texture projection flips Y, which sends
            // temporal history to the vertically mirrored receiver during camera motion.
            var current = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
            if (!_historyValid) _previousViewProjection = current;
            shader.SetInt(Enabled, enabled ? 1 : 0);
            shader.SetInt(SpatialEnabled, gameManager.Lighting.SpatialRisEnabled ? 1 : 0);
            shader.SetInt(SpatialNeighborCount, gameManager.Lighting.SpatialRisNeighborCount);
            shader.SetInt(HistoryValid, _historyValid ? 1 : 0);
            shader.SetInt(Unsupported, unsupported ? 1 : 0);
            shader.SetInt(MaxM, gameManager.Lighting.InitialRisCandidateCount + (gameManager.Lighting.SpatialRisEnabled
                ? gameManager.Lighting.SpatialRisNeighborCount : gameManager.Lighting.TemporalRisHistoryMCap));
            shader.SetFloat(DepthThreshold, 0.05f);
            shader.SetFloat(NormalThreshold, 0.9f);
            shader.SetInts(TextureSize, gameManager.TextureSize.x, gameManager.TextureSize.y);
            shader.SetMatrix(CurrentViewProjection, current);
            shader.SetMatrix(PreviousViewProjection, _previousViewProjection);
            shader.SetBuffer(kernel, PreviousReservoir, _reservoirs[read]);
            shader.SetBuffer(kernel, NextReservoir, _reservoirs[write]);
            shader.SetBuffer(kernel, Diagnostics, _diagnostics);
            shader.SetTexture(kernel, PreviousNormal, _normal[read]); shader.SetTexture(kernel, PreviousDepth, _depth[read]);
            shader.SetTexture(kernel, PreviousIdentity, _identity[read]); shader.SetTexture(kernel, PreviousValidity, _validity[read]);
            // These resources are declared by every temporal-RIS final-color variant. Bind them
            // even when spatial reuse is off so Unity does not reject the dispatch before the
            // shader can take its _SpatialRisEnabled fallback branch.
            BindSpatialResolve(shader, kernel, gameManager);
        }


        public void Commit(GameManager manager)
        {
            var write = _readIsA ? 1 : 0;
            Graphics.CopyTexture(manager.FeatureNormalTexture, _normal[write]);
            Graphics.CopyTexture(manager.FeatureDepthTexture, _depth[write]);
            Graphics.CopyTexture(manager.FeatureIdentityTexture, _identity[write]);
            Graphics.CopyTexture(manager.FeatureValidityTexture, _validity[write]);
            var camera = manager.renderTextureCamera;
            _previousViewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
            _readIsA = !_readIsA;
            _historyValid = true;
        }

        public void ClearDiagnostics()
        {
            if (_diagnostics == null) return;
            _diagnostics.SetData(new uint[DiagnosticsCount]);
        }

        public uint[] ReadDiagnostics()
        {
            if (_diagnostics == null) return new uint[DiagnosticsCount];
            var diagnostics = new uint[DiagnosticsCount];
            _diagnostics.GetData(diagnostics);
            return diagnostics;
        }

        public void InvalidateHistory()
        {
            // The reservoir buffers may retain old data, but Bind marks it unreadable until a
            // current frame has replaced it. Keeping the allocation avoids a reset-time stall.
            _historyValid = false;
            _readIsA = true;
        }

        public void ReleaseResources()
        {
            for (var i = 0; i < 2; i++)
            {
                _reservoirs[i]?.Release();
                _normal[i]?.Release(); _depth[i]?.Release(); _identity[i]?.Release(); _validity[i]?.Release();
                _reservoirs[i] = null;
                _normal[i] = null; _depth[i] = null; _identity[i] = null; _validity[i] = null;
            }
            _diagnostics?.Release();
            _diagnostics = null;
            _spatialLocalReservoir?.Release();
            _spatialLocalReservoir = null;
            _spatialPostCandidateRng?.Release();
            _spatialPostCandidateRng = null;
            _historyValid = false;
            _readIsA = true;
        }

        private static RenderTexture CreateTexture(Vector2Int size, RenderTextureFormat format)
        {
            var result = new RenderTexture(size.x, size.y, 0, format) { enableRandomWrite = true, filterMode = FilterMode.Point };
            result.Create();
            return result;
        }
    }
}
