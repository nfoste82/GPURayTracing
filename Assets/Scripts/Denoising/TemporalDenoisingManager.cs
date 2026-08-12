using UnityEngine;

public sealed class TemporalDenoisingManager
{
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
        return _gameManager.enableTemporalDenoising || IsDebugMode(mode);
    }

    public bool ShouldUseAccumulation(DebugRenderMode mode)
    {
        return _gameManager.enableTemporalDenoising
            && mode == DebugRenderMode.FinalColor
            && IsCameraMoving();
    }

    public void EnsureResources()
    {
        _gameManager.EnsureSpatialDenoiserResourcesInternal();
        if (_gameManager.SpatialDenoiserShader == null || HasResources)
        {
            return;
        }

        _motionVectorTexture = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RGHalf);
        _radianceHistoryA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.ARGBHalf);
        _radianceHistoryB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.ARGBHalf);
        _normalHistoryA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.ARGBHalf);
        _normalHistoryB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.ARGBHalf);
        _depthHistoryA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _depthHistoryB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _identityHistoryA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RFloat);
        _identityHistoryB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RFloat);
        _validityHistoryA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _validityHistoryB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _historyLengthA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _historyLengthB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _momentsA = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RGHalf);
        _momentsB = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RGHalf);
        _varianceTexture = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RHalf);
        _reprojectedRadianceTexture = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.ARGBHalf);
        _diagnosticsTexture = _gameManager.CreateFeatureTextureInternal(RenderTextureFormat.RGHalf);
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
        Camera camera = _gameManager.renderTextureCamera;
        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
        _currentViewProjection = gpuProjection * camera.worldToCameraMatrix;
        _currentJitterNdc = GetJitter(_frameIndex, _gameManager.TextureSize);
        int stateHash = CalculateStateHash();
        bool cameraCut = _historyValid
            && (Vector3.Distance(camera.transform.position, _previousCameraPosition) >= _gameManager.temporalCameraCutDistance
                || Quaternion.Angle(camera.transform.rotation, _previousCameraRotation) >= _gameManager.temporalCameraCutAngle);
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

    public void Run(DebugRenderMode debugMode)
    {
        EnsureResources();
        var shader = _gameManager.SpatialDenoiserShader;
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
        shader.Dispatch(motionKernel, groupsX, groupsY, 1);

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
        shader.SetTexture(validationKernel, "Beauty", _gameManager.BeautyTexture);
        shader.SetTexture(validationKernel, "FeatureNormal", _gameManager.FeatureNormalTexture);
        shader.SetTexture(validationKernel, "FeatureDepth", _gameManager.FeatureDepthTexture);
        shader.SetTexture(validationKernel, "FeatureIdentity", _gameManager.FeatureIdentityTexture);
        shader.SetTexture(validationKernel, "FeatureValidity", _gameManager.FeatureValidityTexture);
        shader.SetTexture(validationKernel, "MotionVectors", _motionVectorTexture);
        shader.SetTexture(validationKernel, "PreviousRadiance", previousRadiance);
        shader.SetTexture(validationKernel, "PreviousNormal", previousNormal);
        shader.SetTexture(validationKernel, "PreviousDepth", previousDepth);
        shader.SetTexture(validationKernel, "PreviousIdentity", previousIdentity);
        shader.SetTexture(validationKernel, "PreviousValidity", previousValidity);
        shader.SetTexture(validationKernel, "PreviousHistoryLength", previousLength);
        shader.SetTexture(validationKernel, "ReprojectedRadiance", _reprojectedRadianceTexture);
        shader.SetTexture(validationKernel, "NextRadiance", nextRadiance);
        shader.SetTexture(validationKernel, "NextNormal", nextNormal);
        shader.SetTexture(validationKernel, "NextDepth", nextDepth);
        shader.SetTexture(validationKernel, "NextIdentity", nextIdentity);
        shader.SetTexture(validationKernel, "NextValidity", nextValidity);
        shader.SetTexture(validationKernel, "NextHistoryLength", nextLength);
        shader.SetTexture(validationKernel, "TemporalDiagnostics", _diagnosticsTexture);
        shader.SetInt("_TemporalHistoryValid", _historyValid ? 1 : 0);
        shader.SetInt("_TemporalUnsupported", IsPathUnsupported() ? 1 : 0);
        shader.SetFloat("_TemporalDepthThreshold", _gameManager.temporalDepthThreshold);
        shader.SetFloat("_TemporalNormalThreshold", _gameManager.temporalNormalThreshold);
        shader.SetInt("_TemporalMaxHistoryLength", _gameManager.temporalMaxHistoryLength);
        shader.SetFloat("_TemporalCameraRotationDelta", _hasRenderedCameraState
            ? Quaternion.Angle(_gameManager.renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
        shader.Dispatch(validationKernel, groupsX, groupsY, 1);

        var momentsKernel = shader.FindKernel("CSUpdateTemporalMoments");
        shader.SetTexture(momentsKernel, "Beauty", _gameManager.BeautyTexture);
        shader.SetTexture(momentsKernel, "MotionVectors", _motionVectorTexture);
        shader.SetTexture(momentsKernel, "HistoryLength", nextLength);
        shader.SetTexture(momentsKernel, "TemporalDiagnostics", _diagnosticsTexture);
        shader.SetTexture(momentsKernel, "PreviousMoments", previousMoments);
        shader.SetTexture(momentsKernel, "NextMoments", nextMoments);
        shader.SetTexture(momentsKernel, "NextVariance", _varianceTexture);
        shader.SetFloat("_TemporalCameraRotationDelta", _hasRenderedCameraState
            ? Quaternion.Angle(_gameManager.renderTextureCamera.transform.rotation, _lastRenderedCameraRotation) : 0.0f);
        shader.Dispatch(momentsKernel, groupsX, groupsY, 1);

        if (_gameManager.enableCaustics || debugMode == DebugRenderMode.CausticPreservationMask)
        {
            _gameManager.GenerateCausticPreservationMaskInternal(groupsX, groupsY);
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
            shader.SetTexture(visualizeKernel, "MotionVectors", _motionVectorTexture);
            shader.SetTexture(visualizeKernel, "ReprojectedRadiance", _reprojectedRadianceTexture);
            shader.SetTexture(visualizeKernel, "NextRadiance", nextRadiance);
            shader.SetTexture(visualizeKernel, "TemporalDiagnostics", _diagnosticsTexture);
            shader.SetTexture(visualizeKernel, "HistoryLength", nextLength);
            shader.SetTexture(visualizeKernel, "Variance", _varianceTexture);
            shader.SetTexture(visualizeKernel, "PreservationMask", _gameManager.CausticPreservationMaskTexture);
            shader.SetTexture(visualizeKernel, "PresentationResult", _gameManager.OutputTexture);
            shader.SetInt("_TemporalDebugMode", temporalDebugMode);
            shader.SetFloat("_Exposure", _gameManager.exposure);
            shader.SetInt("_TemporalMaxHistoryLength", _gameManager.temporalMaxHistoryLength);
            shader.Dispatch(visualizeKernel, groupsX, groupsY, 1);
        }
        else if (ShouldUseAccumulation(debugMode))
        {
            if (_gameManager.temporalVarianceGuidedFiltering)
            {
                _gameManager.RunSpatialDenoiserInternal(nextRadiance, _varianceTexture);
            }
            else
            {
                var presentKernel = shader.FindKernel("CSPresent");
                shader.SetTexture(presentKernel, "InputBeauty", nextRadiance);
                shader.SetTexture(presentKernel, "PresentationResult", _gameManager.OutputTexture);
                shader.SetFloat("_Exposure", _gameManager.exposure);
                shader.Dispatch(presentKernel, groupsX, groupsY, 1);
            }
        }

        _historyReadIsA = !_historyReadIsA;
        _historyValid = true;
    }

    private bool IsCameraMoving()
    {
        var camera = _gameManager.renderTextureCamera;
        return _hasRenderedCameraState
            && (Vector3.Distance(camera.transform.position, _lastRenderedCameraPosition) >= _gameManager.temporalMotionDistance
                || Quaternion.Angle(camera.transform.rotation, _lastRenderedCameraRotation) >= _gameManager.temporalMotionAngle);
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
            hash = AddHash(hash, _gameManager.lightSamplingStrategy.GetHashCode());
            hash = AddHash(hash, _gameManager.lightSampleCount);
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
}
