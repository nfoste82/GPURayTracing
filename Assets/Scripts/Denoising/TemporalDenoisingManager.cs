using System;
using UnityEngine;

namespace PathTracing.TemporalDenoising
{
    [Serializable]
    public sealed class TemporalDenoisingManager
    {
        [Tooltip("Uses camera-only temporal reprojection and bounded HDR accumulation while the camera moves, then allows progressive still accumulation when it stops.")]
        public bool enabled;

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

        private static readonly int PreviousDepth = Shader.PropertyToID("PreviousDepth");
        private static readonly int Beauty = Shader.PropertyToID("Beauty");
        private static readonly int FeatureNormal = Shader.PropertyToID("FeatureNormal");
        private static readonly int FeatureDepth = Shader.PropertyToID("FeatureDepth");
        private static readonly int FeatureIdentity = Shader.PropertyToID("FeatureIdentity");
        private static readonly int FeatureValidity = Shader.PropertyToID("FeatureValidity");
        private static readonly int MotionVectors = Shader.PropertyToID("MotionVectors");
        private static readonly int PreviousRadiance = Shader.PropertyToID("PreviousRadiance");
        private static readonly int PreviousNormal = Shader.PropertyToID("PreviousNormal");
        private static readonly int PreviousIdentity = Shader.PropertyToID("PreviousIdentity");
        private static readonly int PreviousValidity = Shader.PropertyToID("PreviousValidity");
        private static readonly int PreviousHistoryLength = Shader.PropertyToID("PreviousHistoryLength");
        private static readonly int ReprojectedRadiance = Shader.PropertyToID("ReprojectedRadiance");
        private static readonly int NextRadiance = Shader.PropertyToID("NextRadiance");
        private static readonly int NextNormal = Shader.PropertyToID("NextNormal");
        private static readonly int NextDepth = Shader.PropertyToID("NextDepth");
        private static readonly int NextIdentity = Shader.PropertyToID("NextIdentity");
        private static readonly int NextValidity = Shader.PropertyToID("NextValidity");
        private static readonly int NextHistoryLength = Shader.PropertyToID("NextHistoryLength");
        private static readonly int TemporalDiagnostics = Shader.PropertyToID("TemporalDiagnostics");
        private static readonly int TemporalHistoryValid = Shader.PropertyToID("_TemporalHistoryValid");
        private static readonly int TemporalUnsupported = Shader.PropertyToID("_TemporalUnsupported");
        private static readonly int TemporalDepthThreshold = Shader.PropertyToID("_TemporalDepthThreshold");
        private static readonly int TemporalNormalThreshold = Shader.PropertyToID("_TemporalNormalThreshold");
        private static readonly int TemporalMaxHistoryLength = Shader.PropertyToID("_TemporalMaxHistoryLength");
        private static readonly int TemporalCameraRotationDelta = Shader.PropertyToID("_TemporalCameraRotationDelta");
        private static readonly int HistoryLength = Shader.PropertyToID("HistoryLength");
        private static readonly int PreviousMoments = Shader.PropertyToID("PreviousMoments");
        private static readonly int NextMoments = Shader.PropertyToID("NextMoments");
        private static readonly int NextVariance = Shader.PropertyToID("NextVariance");
        private static readonly int Variance = Shader.PropertyToID("Variance");
        private static readonly int PreservationMask = Shader.PropertyToID("PreservationMask");
        private static readonly int PresentationResult = Shader.PropertyToID("PresentationResult");
        private static readonly int TemporalDebugMode = Shader.PropertyToID("_TemporalDebugMode");
        private static readonly int Exposure = Shader.PropertyToID("_Exposure");
        private static readonly int InputBeauty = Shader.PropertyToID("InputBeauty");
        private static readonly int FrameJitterNdc = Shader.PropertyToID("_FrameJitterNdc");
        private static readonly int UseTemporalJitter = Shader.PropertyToID("_UseTemporalJitter");
        
        private GameManager _gameManager;
        private RenderTexture _motionVectorTexture;
        private RenderTexture _radianceHistoryA;
        private RenderTexture _radianceHistoryB;
        private RenderTexture _normalHistoryA;
        private RenderTexture _normalHistoryB;
        private RenderTexture _depthHistoryA;
        private RenderTexture _depthHistoryB;
        private RenderTexture _identityHistoryA;
        private RenderTexture _identityHistoryB;
        private RenderTexture _validityHistoryA;
        private RenderTexture _validityHistoryB;
        private RenderTexture _historyLengthA;
        private RenderTexture _historyLengthB;
        private RenderTexture _momentsA;
        private RenderTexture _momentsB;
        private RenderTexture _varianceTexture;
        private RenderTexture _reprojectedRadianceTexture;
        private RenderTexture _diagnosticsTexture;
        private bool _historyReadIsA = true;
        private bool _historyValid;
        private bool _hasStateHash;
        private int _stateHash;
        private Matrix4x4 _currentViewProjection;
        private Matrix4x4 _previousViewProjection;
        private Vector2 _currentJitterNdc;
        private Vector3 _previousCameraPosition;
        private Quaternion _previousCameraRotation;
        private uint _frameIndex;
        private bool _hasRenderedCameraState;
        private Vector3 _lastRenderedCameraPosition;
        private Quaternion _lastRenderedCameraRotation;

        public bool DynamicSceneChanged { get; set; }
        public Vector2 CurrentJitterNdc => _currentJitterNdc;
        public bool HasResources => _radianceHistoryA != null;
        public bool HistoryValid => _historyValid;
        // This camera-state check is also used by progressive sampling when temporal denoising
        // is disabled. Camera motion must not inherit the stationary accumulation history.
        public bool IsCameraMovingForSampling => IsCameraMoving();

        public void ValidateSettings()
        {
            temporalDepthThreshold = Mathf.Max(0.01f, temporalDepthThreshold);
            temporalNormalThreshold = Mathf.Clamp(temporalNormalThreshold, -1.0f, 1.0f);
            temporalCameraCutDistance = Mathf.Max(0.01f, temporalCameraCutDistance);
            temporalCameraCutAngle = Mathf.Clamp(temporalCameraCutAngle, 1.0f, 180.0f);
            temporalMaxHistoryLength = Mathf.Clamp(temporalMaxHistoryLength, 1, 64);
            temporalMotionDistance = Mathf.Max(0.00001f, temporalMotionDistance);
            temporalMotionAngle = Mathf.Max(0.0001f, temporalMotionAngle);
        }

        public void Initialize(GameManager gameManager)
        {
            _gameManager = gameManager;
        }

        public bool IsDebugMode(DebugRenderMode mode)
        {
            return mode == DebugRenderMode.MotionVectors
                   || mode == DebugRenderMode.TemporalReprojectedRadiance
                   || mode == DebugRenderMode.TemporalHistoryAcceptance
                   || mode == DebugRenderMode.TemporalRejectionReason
                   || mode == DebugRenderMode.TemporalDenoised
                   || mode == DebugRenderMode.TemporalHistoryLength
                   || mode == DebugRenderMode.TemporalDenoisedTint
                   || mode == DebugRenderMode.TemporalVariance;
        }

        public bool ShouldRun(DebugRenderMode mode)
        {
            return enabled || IsDebugMode(mode);
        }

        public bool ShouldUseAccumulation(DebugRenderMode mode)
        {
            return enabled
                   && mode == DebugRenderMode.FinalColor
                   && IsCameraMoving();
        }

        public void SetRayTracingShaderParameters(ComputeShader shader, DebugRenderMode mode)
        {
            shader.SetVector(FrameJitterNdc, new Vector4(_currentJitterNdc.x, _currentJitterNdc.y, 0.0f, 0.0f));
            shader.SetInt(UseTemporalJitter, ShouldRun(mode) ? 1 : 0);
        }

        public void EnsureResources()
        {
            _gameManager.SpatialDenoising.EnsureResources(_gameManager.TextureSize);
            if (_gameManager.SpatialDenoising.Shader == null || HasResources)
            {
                return;
            }

            _motionVectorTexture = CreateTexture(RenderTextureFormat.RGHalf);
            _radianceHistoryA = CreateTexture(RenderTextureFormat.ARGBHalf);
            _radianceHistoryB = CreateTexture(RenderTextureFormat.ARGBHalf);
            _normalHistoryA = CreateTexture(RenderTextureFormat.ARGBHalf);
            _normalHistoryB = CreateTexture(RenderTextureFormat.ARGBHalf);
            _depthHistoryA = CreateTexture(RenderTextureFormat.RHalf);
            _depthHistoryB = CreateTexture(RenderTextureFormat.RHalf);
            _identityHistoryA = CreateTexture(RenderTextureFormat.RFloat);
            _identityHistoryB = CreateTexture(RenderTextureFormat.RFloat);
            _validityHistoryA = CreateTexture(RenderTextureFormat.RHalf);
            _validityHistoryB = CreateTexture(RenderTextureFormat.RHalf);
            _historyLengthA = CreateTexture(RenderTextureFormat.RHalf);
            _historyLengthB = CreateTexture(RenderTextureFormat.RHalf);
            _momentsA = CreateTexture(RenderTextureFormat.RGHalf);
            _momentsB = CreateTexture(RenderTextureFormat.RGHalf);
            _varianceTexture = CreateTexture(RenderTextureFormat.RHalf);
            _reprojectedRadianceTexture = CreateTexture(RenderTextureFormat.ARGBHalf);
            _diagnosticsTexture = CreateTexture(RenderTextureFormat.RGHalf);
        }

        public void ReleaseResources()
        {
            Release(_motionVectorTexture);
            Release(_radianceHistoryA);
            Release(_radianceHistoryB);
            Release(_normalHistoryA);
            Release(_normalHistoryB);
            Release(_depthHistoryA);
            Release(_depthHistoryB);
            Release(_identityHistoryA);
            Release(_identityHistoryB);
            Release(_validityHistoryA);
            Release(_validityHistoryB);
            Release(_historyLengthA);
            Release(_historyLengthB);
            Release(_momentsA);
            Release(_momentsB);
            Release(_varianceTexture);
            Release(_reprojectedRadianceTexture);
            Release(_diagnosticsTexture);
            _motionVectorTexture = null;
            _radianceHistoryA = null;
            _radianceHistoryB = null;
            _normalHistoryA = null;
            _normalHistoryB = null;
            _depthHistoryA = null;
            _depthHistoryB = null;
            _identityHistoryA = null;
            _identityHistoryB = null;
            _validityHistoryA = null;
            _validityHistoryB = null;
            _historyLengthA = null;
            _historyLengthB = null;
            _momentsA = null;
            _momentsB = null;
            _varianceTexture = null;
            _reprojectedRadianceTexture = null;
            _diagnosticsTexture = null;
            _historyReadIsA = true;
        }

        public void ResetHistory()
        {
            _historyValid = false;
            _hasStateHash = false;
            _historyReadIsA = true;
        }

        public void PrepareCameraState()
        {
            var camera = _gameManager.renderTextureCamera;
            var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            _currentViewProjection = gpuProjection * camera.worldToCameraMatrix;
            _currentJitterNdc = GetJitter(_frameIndex, _gameManager.TextureSize);
            var stateHash = CalculateStateHash();
            var cameraCut = _historyValid
                            && (Vector3.Distance(camera.transform.position, _previousCameraPosition) >= temporalCameraCutDistance
                                || Quaternion.Angle(camera.transform.rotation, _previousCameraRotation) >= temporalCameraCutAngle);
            if (!_hasStateHash || stateHash != _stateHash || cameraCut)
            {
                ResetHistory();
                _stateHash = stateHash;
                _hasStateHash = true;
            }

            if (!_historyValid)
            {
                _previousViewProjection = _currentViewProjection;
            }
        }

        public void CommitCameraState()
        {
            var camera = _gameManager.renderTextureCamera;
            _previousViewProjection = _currentViewProjection;
            _previousCameraPosition = camera.transform.position;
            _previousCameraRotation = camera.transform.rotation;
            _frameIndex++;
        }

        public void CommitRenderedCameraState()
        {
            var camera = _gameManager.renderTextureCamera;
            _lastRenderedCameraPosition = camera.transform.position;
            _lastRenderedCameraRotation = camera.transform.rotation;
            _hasRenderedCameraState = true;
        }

        public void SetDynamicSceneChanged(bool changed)
        {
            DynamicSceneChanged = changed;
        }

        public void MarkDynamicSceneChanged() => DynamicSceneChanged = true;

        public void Run(DebugRenderMode debugMode)
        {
            EnsureResources();
            var shader = _gameManager.SpatialDenoising.Shader;
            if (shader == null || _motionVectorTexture == null)
            {
                return;
            }

            var groupsX = Mathf.CeilToInt(_gameManager.TextureSize.x / 8.0f);
            var groupsY = Mathf.CeilToInt(_gameManager.TextureSize.y / 8.0f);
            var motionKernel = shader.FindKernel("CSGenerateCameraMotion");
            shader.SetTexture(motionKernel, "FeatureDepth", _gameManager.FeatureDepthTexture);
            shader.SetTexture(motionKernel, "FeatureValidity", _gameManager.FeatureValidityTexture);
            shader.SetTexture(motionKernel, "GeneratedMotionVectors", _motionVectorTexture);
            shader.SetMatrix("_CurrentUnjitteredViewProjection", _currentViewProjection);
            shader.SetMatrix("_PreviousUnjitteredViewProjection", _previousViewProjection);
            shader.SetMatrix("_CameraToWorld", _gameManager.renderTextureCamera.cameraToWorldMatrix);
            shader.SetMatrix("_CameraInverseProjection", _gameManager.renderTextureCamera.projectionMatrix.inverse);
            ComputeDispatch.Dispatch(shader, motionKernel, groupsX, groupsY, 1);

            var previousRadiance = _historyReadIsA ? _radianceHistoryA : _radianceHistoryB;
            var nextRadiance = _historyReadIsA ? _radianceHistoryB : _radianceHistoryA;
            var previousNormal = _historyReadIsA ? _normalHistoryA : _normalHistoryB;
            var nextNormal = _historyReadIsA ? _normalHistoryB : _normalHistoryA;
            var previousDepth = _historyReadIsA ? _depthHistoryA : _depthHistoryB;
            var nextDepth = _historyReadIsA ? _depthHistoryB : _depthHistoryA;
            var previousIdentity = _historyReadIsA ? _identityHistoryA : _identityHistoryB;
            var nextIdentity = _historyReadIsA ? _identityHistoryB : _identityHistoryA;
            var previousValidity = _historyReadIsA ? _validityHistoryA : _validityHistoryB;
            var nextValidity = _historyReadIsA ? _validityHistoryB : _validityHistoryA;
            var previousLength = _historyReadIsA ? _historyLengthA : _historyLengthB;
            var nextLength = _historyReadIsA ? _historyLengthB : _historyLengthA;
            var previousMoments = _historyReadIsA ? _momentsA : _momentsB;
            var nextMoments = _historyReadIsA ? _momentsB : _momentsA;

            var validationKernel = shader.FindKernel("CSTemporalReprojectValidate");
            shader.SetTexture(validationKernel, Beauty, _gameManager.BeautyTexture);
            shader.SetTexture(validationKernel, FeatureNormal, _gameManager.FeatureNormalTexture);
            shader.SetTexture(validationKernel, FeatureDepth, _gameManager.FeatureDepthTexture);
            shader.SetTexture(validationKernel, FeatureIdentity, _gameManager.FeatureIdentityTexture);
            shader.SetTexture(validationKernel, FeatureValidity, _gameManager.FeatureValidityTexture);
            shader.SetTexture(validationKernel, MotionVectors, _motionVectorTexture);
            shader.SetTexture(validationKernel, PreviousRadiance, previousRadiance);
            shader.SetTexture(validationKernel, PreviousNormal, previousNormal);
            shader.SetTexture(validationKernel, PreviousDepth, previousDepth);
            shader.SetTexture(validationKernel, PreviousIdentity, previousIdentity);
            shader.SetTexture(validationKernel, PreviousValidity, previousValidity);
            shader.SetTexture(validationKernel, PreviousHistoryLength, previousLength);
            shader.SetTexture(validationKernel, ReprojectedRadiance, _reprojectedRadianceTexture);
            shader.SetTexture(validationKernel, NextRadiance, nextRadiance);
            shader.SetTexture(validationKernel, NextNormal, nextNormal);
            shader.SetTexture(validationKernel, NextDepth, nextDepth);
            shader.SetTexture(validationKernel, NextIdentity, nextIdentity);
            shader.SetTexture(validationKernel, NextValidity, nextValidity);
            shader.SetTexture(validationKernel, NextHistoryLength, nextLength);
            shader.SetTexture(validationKernel, TemporalDiagnostics, _diagnosticsTexture);
            shader.SetInt(TemporalHistoryValid, _historyValid ? 1 : 0);
            shader.SetInt(TemporalUnsupported, IsPathUnsupported() ? 1 : 0);
            shader.SetFloat(TemporalDepthThreshold, temporalDepthThreshold);
            shader.SetFloat(TemporalNormalThreshold, temporalNormalThreshold);
            shader.SetInt(TemporalMaxHistoryLength, temporalMaxHistoryLength);
            shader.SetFloat(TemporalCameraRotationDelta, _hasRenderedCameraState
                ? Quaternion.Angle(_gameManager.renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
            ComputeDispatch.Dispatch(shader, validationKernel, groupsX, groupsY, 1);

            var momentsKernel = shader.FindKernel("CSUpdateTemporalMoments");
            shader.SetTexture(momentsKernel, Beauty, _gameManager.BeautyTexture);
            shader.SetTexture(momentsKernel, MotionVectors, _motionVectorTexture);
            shader.SetTexture(momentsKernel, HistoryLength, nextLength);
            shader.SetTexture(momentsKernel, TemporalDiagnostics, _diagnosticsTexture);
            shader.SetTexture(momentsKernel, PreviousMoments, previousMoments);
            shader.SetTexture(momentsKernel, NextMoments, nextMoments);
            shader.SetTexture(momentsKernel, NextVariance, _varianceTexture);
            shader.SetFloat(TemporalCameraRotationDelta, _hasRenderedCameraState
                ? Quaternion.Angle(_gameManager.renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
            ComputeDispatch.Dispatch(shader, momentsKernel, groupsX, groupsY, 1);

            if (_gameManager.enableCaustics || debugMode == DebugRenderMode.CausticPreservationMask)
            {
                _gameManager.SpatialDenoising.GenerateCausticPreservationMask(
                    _gameManager.BeautyTexture, _gameManager.FeatureValidityTexture, _gameManager.TextureSize,
                    _gameManager.causticPreservationThreshold, _gameManager.enableCaustics);
            }

            if (IsDebugMode(debugMode))
            {
                var temporalDebugMode = debugMode == DebugRenderMode.MotionVectors ? 1
                    : debugMode == DebugRenderMode.TemporalReprojectedRadiance ? 2
                    : debugMode == DebugRenderMode.TemporalHistoryAcceptance ? 3
                    : debugMode == DebugRenderMode.TemporalRejectionReason ? 4
                    : debugMode == DebugRenderMode.TemporalDenoised ? 5
                    : debugMode == DebugRenderMode.TemporalHistoryLength ? 6
                    : debugMode == DebugRenderMode.TemporalDenoisedTint ? 7 : 8;
                var visualizeKernel = shader.FindKernel("CSVisualizeTemporal");
                shader.SetTexture(visualizeKernel, MotionVectors, _motionVectorTexture);
                shader.SetTexture(visualizeKernel, ReprojectedRadiance, _reprojectedRadianceTexture);
                shader.SetTexture(visualizeKernel, NextRadiance, nextRadiance);
                shader.SetTexture(visualizeKernel, TemporalDiagnostics, _diagnosticsTexture);
                shader.SetTexture(visualizeKernel, HistoryLength, nextLength);
                shader.SetTexture(visualizeKernel, Variance, _varianceTexture);
                shader.SetTexture(visualizeKernel, PreservationMask, _gameManager.SpatialDenoising.CausticPreservationMask);
                shader.SetTexture(visualizeKernel, PresentationResult, _gameManager.OutputTexture);
                shader.SetInt(TemporalDebugMode, temporalDebugMode);
                shader.SetFloat(Exposure, _gameManager.exposure);
                shader.SetInt(TemporalMaxHistoryLength, temporalMaxHistoryLength);
                ComputeDispatch.Dispatch(shader, visualizeKernel, groupsX, groupsY, 1);
            }
            else if (ShouldUseAccumulation(debugMode))
            {
                if (temporalVarianceGuidedFiltering)
                {
                    _gameManager.SetPresentationSource(_gameManager.SpatialDenoising.Filter(nextRadiance, _varianceTexture,
                        _gameManager.FeatureNormalTexture, _gameManager.FeatureAlbedoTexture,
                        _gameManager.FeatureDepthTexture, _gameManager.FeatureIdentityTexture,
                        _gameManager.FeatureValidityTexture, debugMode, _gameManager.TextureSize,
                        _gameManager.enableCaustics, _gameManager.causticPreservationThreshold));
                }
                else
                {
                    _gameManager.Glare.Present(nextRadiance, _gameManager.OutputTexture, _gameManager.exposure,
                        false, 0.0f, 0.0f, 0.0f);
                }
            }

            _historyReadIsA = !_historyReadIsA;
            _historyValid = true;
        }

        private bool IsCameraMoving()
        {
            var camera = _gameManager.renderTextureCamera;
            return _hasRenderedCameraState
                   && (Vector3.Distance(camera.transform.position, _lastRenderedCameraPosition) >= temporalMotionDistance
                       || Quaternion.Angle(camera.transform.rotation, _lastRenderedCameraRotation) >= temporalMotionAngle);
        }

        private bool IsPathUnsupported()
        {
            return _gameManager.IsFogEnabledInternal()
                   || DynamicSceneChanged
                   || (_gameManager.WaterInternal != null
                       && _gameManager.WaterInternal.WaveSpeed > 0.0f
                       && _gameManager.WaterInternal.WaveAmplitude > 0.0f);
        }

        private int CalculateStateHash()
        {
            unchecked
            {
                int hash = 17;
                hash = AddHash(hash, _gameManager.TextureSize.x);
                hash = AddHash(hash, _gameManager.TextureSize.y);
                hash = AddHash(hash, _gameManager.renderResolutionPercent);
                hash = AddHash(hash, _gameManager.numberOfPasses);
                hash = AddHash(hash, _gameManager.numBounces);
                hash = AddHash(hash, _gameManager.shadowQuality);
                hash = AddHash(hash, _gameManager.Lighting.LightSamplingStrategy.GetHashCode());
                hash = AddHash(hash, _gameManager.Lighting.LightSampleCount);
                hash = AddHash(hash, _gameManager.maxLightSamples);
                hash = AddHash(hash, _gameManager.GetCameraApertureRadiusInternal());
                hash = AddHash(hash, _gameManager.enableCaustics ? 1 : 0);
                hash = AddHash(hash, _gameManager.IsFogEnabledInternal() ? 1 : 0);
                hash = AddHash(hash, _gameManager.WaterInternal != null ? _gameManager.WaterInternal.GetInstanceID() : 0);
                hash = AddHash(hash, _gameManager.skyboxTexture != null ? _gameManager.skyboxTexture.GetInstanceID() : 0);
                hash = AddHash(hash, _gameManager.SphereCount);
                hash = AddHash(hash, _gameManager.LightCount);
                hash = AddHash(hash, _gameManager.MeshCount);
                return hash;
            }
        }

        private static int AddHash(int hash, float value) => AddHash(hash, value.GetHashCode());
        private static int AddHash(int hash, int value) => hash * 31 + value;

        private static Vector2 GetJitter(uint frameIndex, Vector2Int size)
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

        private static void Release(RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
            }
        }

        private RenderTexture CreateTexture(RenderTextureFormat format)
        {
            var size = _gameManager.TextureSize;
            var texture = new RenderTexture(size.x, size.y, 0, format)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            texture.Create();
            return texture;
        }
    }
}
