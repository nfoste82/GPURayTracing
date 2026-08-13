using System;
using UnityEngine;

namespace PathTracing.Denoising
{
    [Serializable]
    public sealed class SpatialDenoisingManager
    {
        [Tooltip("Applies an edge-aware spatial A-trous filter to linear HDR beauty. This does not use temporal history.")]
        public bool enabled = true;
        [Range(1, 5), Tooltip("A-trous passes use increasing pixel steps: 1, 2, 4, 8, and 16.")]
        public int iterations = 1;
        [Range(0.01f, 4.0f)] public float depthSigma = 0.25f;
        [Range(1.0f, 256.0f)] public float normalPower = 64.0f;
        [Range(0.01f, 4.0f)] public float albedoSigma = 0.25f;
        [Range(0.01f, 4.0f)] public float luminanceSigma = 0.08f;

        private ComputeShader _shader;
        private RenderTexture _ping;
        private RenderTexture _pong;
        private RenderTexture _iteration1;
        private RenderTexture _iteration2;
        private RenderTexture _iteration3;
        private RenderTexture _causticPreservationMask;

        public ComputeShader Shader => _shader;
        public RenderTexture CausticPreservationMask => _causticPreservationMask;

        public void ValidateSettings()
        {
            iterations = Mathf.Clamp(iterations, 1, 5);
            depthSigma = Mathf.Max(0.01f, depthSigma);
            normalPower = Mathf.Max(1.0f, normalPower);
            albedoSigma = Mathf.Max(0.01f, albedoSigma);
            luminanceSigma = Mathf.Max(0.01f, luminanceSigma);
        }

        public bool ShouldRun(DebugRenderMode mode) => enabled
            || mode == DebugRenderMode.SpatialDenoised
            || mode == DebugRenderMode.AtrousIteration1
            || mode == DebugRenderMode.AtrousIteration2
            || mode == DebugRenderMode.AtrousIteration3;

        public void EnsureResources(Vector2Int size)
        {
            EnsureShader();
            if (_shader == null || _ping != null) return;
            _ping = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            _pong = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            _causticPreservationMask = CreateTexture(size, RenderTextureFormat.RHalf);
        }

        public RenderTexture CreateTexture(Vector2Int size, RenderTextureFormat format) => CreateTextureInternal(size, format);

        public void ReleaseResources()
        {
            Release(_ping); Release(_pong); Release(_iteration1); Release(_iteration2); Release(_iteration3); Release(_causticPreservationMask);
            _ping = _pong = _iteration1 = _iteration2 = _iteration3 = _causticPreservationMask = null;
        }

        public void Present(RenderTexture source, RenderTexture destination, float exposure)
        {
            EnsureShader();
            if (_shader == null || source == null || destination == null) return;
            int kernel = _shader.FindKernel("CSPresent");
            _shader.SetTexture(kernel, "InputBeauty", source);
            _shader.SetTexture(kernel, "PresentationResult", destination);
            _shader.SetFloat("_Exposure", exposure);
            _shader.Dispatch(kernel, Mathf.CeilToInt(destination.width / 8.0f), Mathf.CeilToInt(destination.height / 8.0f), 1);
        }

        public void PresentFeatureDebug(DebugRenderMode mode, RenderTexture normal, RenderTexture albedo, RenderTexture depth,
            RenderTexture identity, RenderTexture validity, RenderTexture output, Vector2Int size)
        {
            EnsureResources(size);
            if (_shader == null) return;
            int kernel = _shader.FindKernel("CSVisualizeFeature");
            _shader.SetTexture(kernel, "FeatureNormal", normal); _shader.SetTexture(kernel, "FeatureAlbedo", albedo);
            _shader.SetTexture(kernel, "FeatureDepth", depth); _shader.SetTexture(kernel, "FeatureIdentity", identity);
            _shader.SetTexture(kernel, "FeatureValidity", validity); _shader.SetTexture(kernel, "PresentationResult", output);
            _shader.SetInt("_FeatureDebugMode", (int)mode - (int)DebugRenderMode.FeatureNormal + 1);
            _shader.Dispatch(kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        public void GenerateCausticPreservationMask(RenderTexture beauty, RenderTexture validity, Vector2Int size,
            float threshold, bool causticsEnabled)
        {
            EnsureResources(size);
            if (_shader == null || _causticPreservationMask == null) return;
            int kernel = _shader.FindKernel("CSGeneratePreservationMask");
            _shader.SetTexture(kernel, "Beauty", beauty); _shader.SetTexture(kernel, "FeatureValidity", validity);
            _shader.SetTexture(kernel, "GeneratedPreservationMask", _causticPreservationMask);
            _shader.SetFloat("_CausticPreservationThreshold", threshold);
            _shader.SetInt("_EnableCausticPreservation", causticsEnabled ? 1 : 0);
            _shader.Dispatch(kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        public RenderTexture Filter(RenderTexture source, RenderTexture variance, RenderTexture normal, RenderTexture albedo,
            RenderTexture depth, RenderTexture identity, RenderTexture validity, DebugRenderMode mode, Vector2Int size,
            bool causticsEnabled, float causticThreshold)
        {
            EnsureResources(size);
            if (_shader == null || _ping == null) return source;
            int kernel = _shader.FindKernel("CSAtrous");
            _shader.SetTexture(kernel, "FeatureNormal", normal); _shader.SetTexture(kernel, "FeatureAlbedo", albedo);
            _shader.SetTexture(kernel, "FeatureDepth", depth); _shader.SetTexture(kernel, "FeatureIdentity", identity);
            _shader.SetTexture(kernel, "FeatureValidity", validity); _shader.SetTexture(kernel, "Variance", variance ?? validity);
            _shader.SetTexture(kernel, "PreservationMask", _causticPreservationMask);
            _shader.SetFloat("_DepthSigma", depthSigma); _shader.SetFloat("_NormalPower", normalPower);
            _shader.SetFloat("_AlbedoSigma", albedoSigma); _shader.SetFloat("_LuminanceSigma", luminanceSigma);
            _shader.SetInt("_UseVarianceGuidance", variance != null ? 1 : 0);
            _shader.SetInt("_EnableCausticPreservation", causticsEnabled ? 1 : 0);
            int debugIteration = mode == DebugRenderMode.AtrousIteration3 ? 3 : mode == DebugRenderMode.AtrousIteration2 ? 2 : 1;
            bool captureDebug = mode == DebugRenderMode.AtrousIteration1 || mode == DebugRenderMode.AtrousIteration2 || mode == DebugRenderMode.AtrousIteration3;
            int passCount = Mathf.Clamp(Mathf.Max(iterations, debugIteration), 1, 5);
            if (causticsEnabled) GenerateCausticPreservationMask(source, validity, size, causticThreshold, true);
            RenderTexture input = source;
            RenderTexture output = _ping;
            RenderTexture debugOutput = captureDebug ? GetIterationTexture(debugIteration, size) : null;
            int groupsX = Mathf.CeilToInt(size.x / 8.0f), groupsY = Mathf.CeilToInt(size.y / 8.0f);
            for (int i = 0; i < passCount; i++)
            {
                _shader.SetTexture(kernel, "InputBeauty", input); _shader.SetTexture(kernel, "FilteredBeauty", output);
                _shader.SetInt("_StepWidth", 1 << i); _shader.Dispatch(kernel, groupsX, groupsY, 1);
                if (captureDebug && i == debugIteration - 1) Graphics.CopyTexture(output, debugOutput);
                input = output; output = output == _ping ? _pong : _ping;
            }
            return captureDebug ? debugOutput : input;
        }

        public void PresentCausticPreservationMask(RenderTexture beauty, RenderTexture validity, RenderTexture output,
            Vector2Int size, float threshold, bool causticsEnabled)
        {
            GenerateCausticPreservationMask(beauty, validity, size, threshold, causticsEnabled);
            if (_shader == null || _causticPreservationMask == null) return;
            int kernel = _shader.FindKernel("CSVisualizeTemporal");
            _shader.SetTexture(kernel, "PreservationMask", _causticPreservationMask);
            _shader.SetTexture(kernel, "PresentationResult", output); _shader.SetInt("_TemporalDebugMode", 9);
            _shader.Dispatch(kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        private void EnsureShader()
        {
            if (_shader == null) _shader = Resources.Load<ComputeShader>("RayTracingSpatialDenoiser");
            if (_shader == null) Debug.LogError("Spatial denoiser shader was not found at Resources/RayTracingSpatialDenoiser.");
        }

        private RenderTexture GetIterationTexture(int iteration, Vector2Int size)
        {
            if (iteration == 1)
            {
                if (_iteration1 == null) _iteration1 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
                return _iteration1;
            }
            if (iteration == 2)
            {
                if (_iteration2 == null) _iteration2 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
                return _iteration2;
            }
            if (_iteration3 == null) _iteration3 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
            return _iteration3;
        }

        private static RenderTexture CreateTextureInternal(Vector2Int size, RenderTextureFormat format)
        {
            var texture = new RenderTexture(size.x, size.y, 0, format) { enableRandomWrite = true, filterMode = FilterMode.Point };
            texture.Create(); return texture;
        }

        private static void Release(RenderTexture texture) { if (texture != null) texture.Release(); }
    }
}
