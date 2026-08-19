using System;
using UnityEngine;

namespace PathTracing.Denoising
{
    [Serializable]
    public sealed class SpatialDenoisingManager
    {
        private static readonly int PreservationMask = UnityEngine.Shader.PropertyToID("PreservationMask");
        private static readonly int PresentationResult = UnityEngine.Shader.PropertyToID("PresentationResult");
        private static readonly int TemporalDebugMode = UnityEngine.Shader.PropertyToID("_TemporalDebugMode");
        private static readonly int FeatureNormal = UnityEngine.Shader.PropertyToID("FeatureNormal");
        private static readonly int FeatureDepth = UnityEngine.Shader.PropertyToID("FeatureDepth");
        private static readonly int FeatureValidity = UnityEngine.Shader.PropertyToID("FeatureValidity");
        private static readonly int FeatureAlbedo = UnityEngine.Shader.PropertyToID("FeatureAlbedo");
        private static readonly int FeatureIdentity = UnityEngine.Shader.PropertyToID("FeatureIdentity");
        private static readonly int Variance = UnityEngine.Shader.PropertyToID("Variance");
        private static readonly int DepthSigma = UnityEngine.Shader.PropertyToID("_DepthSigma");
        private static readonly int NormalPower = UnityEngine.Shader.PropertyToID("_NormalPower");
        private static readonly int AlbedoSigma = UnityEngine.Shader.PropertyToID("_AlbedoSigma");
        private static readonly int LuminanceSigma = UnityEngine.Shader.PropertyToID("_LuminanceSigma");
        private static readonly int UseVarianceGuidance = UnityEngine.Shader.PropertyToID("_UseVarianceGuidance");
        private static readonly int EnableCausticPreservation = UnityEngine.Shader.PropertyToID("_EnableCausticPreservation");
        private static readonly int InputBeauty = UnityEngine.Shader.PropertyToID("InputBeauty");
        private static readonly int StepWidth = UnityEngine.Shader.PropertyToID("_StepWidth");
        private static readonly int FilteredBeauty = UnityEngine.Shader.PropertyToID("FilteredBeauty");
        private static readonly int FeatureDebugMode = UnityEngine.Shader.PropertyToID("_FeatureDebugMode");
        private static readonly int Beauty = UnityEngine.Shader.PropertyToID("Beauty");
        private static readonly int GeneratedPreservationMask = UnityEngine.Shader.PropertyToID("GeneratedPreservationMask");
        private static readonly int CausticPreservationThreshold = UnityEngine.Shader.PropertyToID("_CausticPreservationThreshold");
        private static readonly int Exposure = UnityEngine.Shader.PropertyToID("_Exposure");
        private static readonly int EnableGlare = UnityEngine.Shader.PropertyToID("_EnableGlare");
        private static readonly int GlareIntensity = UnityEngine.Shader.PropertyToID("_GlareIntensity");
        private static readonly int GlareMip0 = UnityEngine.Shader.PropertyToID("GlareMip0");
        private static readonly int GlareMip1 = UnityEngine.Shader.PropertyToID("GlareMip1");
        private static readonly int GlareMip2 = UnityEngine.Shader.PropertyToID("GlareMip2");
        private static readonly int GlareMip3 = UnityEngine.Shader.PropertyToID("GlareMip3");

        [Tooltip("Applies an edge-aware spatial A-trous filter to linear HDR beauty. This does not use temporal history.")]
        public bool enabled = true;
        
        [Range(1, 5), Tooltip("A-trous passes use increasing pixel steps: 1, 2, 4, 8, and 16.")]
        public int iterations = 1;
        
        [Range(0.01f, 4.0f)] 
        public float depthSigma = 0.25f;
        
        [Range(1.0f, 256.0f)] 
        public float normalPower = 64.0f;
        
        [Range(0.01f, 4.0f)] 
        public float albedoSigma = 0.25f;
        
        [Range(0.01f, 4.0f)] 
        public float luminanceSigma = 0.08f;

        private RenderTexture _ping;
        private RenderTexture _pong;
        private RenderTexture _iteration1;
        private RenderTexture _iteration2;
        private RenderTexture _iteration3;

        public ComputeShader Shader { get; private set; }

        public RenderTexture CausticPreservationMask { get; private set; }

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
            if (Shader == null || _ping != null)
            {
                return;
            }
            
            _ping = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            _pong = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            CausticPreservationMask = CreateTexture(size, RenderTextureFormat.RHalf);
        }

        public RenderTexture CreateTexture(Vector2Int size, RenderTextureFormat format) => CreateTextureInternal(size, format);

        public void ReleaseResources()
        {
            Release(_ping); Release(_pong); Release(_iteration1); Release(_iteration2); Release(_iteration3); Release(CausticPreservationMask);
            _ping = _pong = _iteration1 = _iteration2 = _iteration3 = CausticPreservationMask = null;
        }

        public void Present(RenderTexture source, RenderTexture destination, float exposure)
        {
            EnsureShader();
            
            if (Shader == null || source == null || destination == null)
            {
                return;
            }
            
            var kernel = Shader.FindKernel("CSPresent");
            Shader.SetTexture(kernel, InputBeauty, source);
            Shader.SetTexture(kernel, PresentationResult, destination);
            Shader.SetFloat(Exposure, exposure);
            Shader.SetInt(EnableGlare, 0);
            Shader.SetFloat(GlareIntensity, 0.0f);
            Shader.SetTexture(kernel, GlareMip0, source);
            Shader.SetTexture(kernel, GlareMip1, source);
            Shader.SetTexture(kernel, GlareMip2, source);
            Shader.SetTexture(kernel, GlareMip3, source);
            ComputeDispatch.Dispatch(Shader, kernel, Mathf.CeilToInt(destination.width / 8.0f), Mathf.CeilToInt(destination.height / 8.0f), 1);
        }

        public void PresentFeatureDebug(DebugRenderMode mode, RenderTexture normal, RenderTexture albedo, RenderTexture depth,
            RenderTexture identity, RenderTexture validity, RenderTexture output, Vector2Int size)
        {
            EnsureResources(size);
            
            if (Shader == null)
            {
                return;
            }
            
            var kernel = Shader.FindKernel("CSVisualizeFeature");
            
            Shader.SetTexture(kernel, FeatureNormal, normal); 
            Shader.SetTexture(kernel, FeatureAlbedo, albedo);
            Shader.SetTexture(kernel, FeatureDepth, depth); 
            Shader.SetTexture(kernel, FeatureIdentity, identity);
            Shader.SetTexture(kernel, FeatureValidity, validity); 
            Shader.SetTexture(kernel, PresentationResult, output);
            Shader.SetInt(FeatureDebugMode, (int)mode - (int)DebugRenderMode.FeatureNormal + 1);
            
            ComputeDispatch.Dispatch(Shader, kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        public void GenerateCausticPreservationMask(RenderTexture beauty, RenderTexture validity, Vector2Int size,
            float threshold, bool causticsEnabled)
        {
            EnsureResources(size);
            
            if (Shader == null || CausticPreservationMask == null)
            {
                return;
            }
            
            var kernel = Shader.FindKernel("CSGeneratePreservationMask");
            
            Shader.SetTexture(kernel, Beauty, beauty); 
            Shader.SetTexture(kernel, FeatureValidity, validity);
            Shader.SetTexture(kernel, GeneratedPreservationMask, CausticPreservationMask);
            Shader.SetFloat(CausticPreservationThreshold, threshold);
            Shader.SetInt(EnableCausticPreservation, causticsEnabled ? 1 : 0);
            
            ComputeDispatch.Dispatch(Shader, kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        public RenderTexture Filter(RenderTexture source, RenderTexture variance, RenderTexture normal, RenderTexture albedo,
            RenderTexture depth, RenderTexture identity, RenderTexture validity, DebugRenderMode mode, Vector2Int size,
            bool causticsEnabled, float causticThreshold)
        {
            EnsureResources(size);
            
            if (Shader == null || _ping == null)
            {
                return source;
            }
            
            var kernel = Shader.FindKernel("CSAtrous");
            
            Shader.SetTexture(kernel, FeatureNormal, normal);
            Shader.SetTexture(kernel, FeatureAlbedo, albedo);
            Shader.SetTexture(kernel, FeatureDepth, depth); 
            Shader.SetTexture(kernel, FeatureIdentity, identity);
            Shader.SetTexture(kernel, FeatureValidity, validity); 
            Shader.SetTexture(kernel, Variance, variance ?? validity);
            Shader.SetTexture(kernel, PreservationMask, CausticPreservationMask);
            Shader.SetFloat(DepthSigma, depthSigma); 
            Shader.SetFloat(NormalPower, normalPower);
            Shader.SetFloat(AlbedoSigma, albedoSigma); 
            Shader.SetFloat(LuminanceSigma, luminanceSigma);
            Shader.SetInt(UseVarianceGuidance, variance != null ? 1 : 0);
            Shader.SetInt(EnableCausticPreservation, causticsEnabled ? 1 : 0);
            
            var debugIteration = mode == DebugRenderMode.AtrousIteration3 ? 3 : mode == DebugRenderMode.AtrousIteration2 ? 2 : 1;
            var captureDebug = mode == DebugRenderMode.AtrousIteration1 || mode == DebugRenderMode.AtrousIteration2 || mode == DebugRenderMode.AtrousIteration3;
            var passCount = Mathf.Clamp(Mathf.Max(iterations, debugIteration), 1, 5);
            
            if (causticsEnabled)
            {
                GenerateCausticPreservationMask(source, validity, size, causticThreshold, true);
            }
            
            var input = source;
            var output = _ping;
            var debugOutput = captureDebug ? GetIterationTexture(debugIteration, size) : null;
            int groupsX = Mathf.CeilToInt(size.x / 8.0f), groupsY = Mathf.CeilToInt(size.y / 8.0f);
            for (var i = 0; i < passCount; i++)
            {
                Shader.SetTexture(kernel, InputBeauty, input); 
                Shader.SetTexture(kernel, FilteredBeauty, output);
                Shader.SetInt(StepWidth, 1 << i); 
                
                ComputeDispatch.Dispatch(Shader, kernel, groupsX, groupsY, 1);
                
                if (captureDebug && i == debugIteration - 1)
                {
                    Graphics.CopyTexture(output, debugOutput);
                }
                
                input = output; 
                output = output == _ping ? _pong : _ping;
            }
            return captureDebug ? debugOutput : input;
        }

        public void PresentCausticPreservationMask(RenderTexture beauty, RenderTexture validity, RenderTexture output,
            Vector2Int size, float threshold, bool causticsEnabled)
        {
            GenerateCausticPreservationMask(beauty, validity, size, threshold, causticsEnabled);
            
            if (Shader == null || CausticPreservationMask == null)
            {
                return;
            }
            
            var kernel = Shader.FindKernel("CSVisualizeTemporal");
            Shader.SetTexture(kernel, PreservationMask, CausticPreservationMask);
            Shader.SetTexture(kernel, PresentationResult, output); 
            Shader.SetInt(TemporalDebugMode, 9);
            ComputeDispatch.Dispatch(Shader, kernel, Mathf.CeilToInt(size.x / 8.0f), Mathf.CeilToInt(size.y / 8.0f), 1);
        }

        private void EnsureShader()
        {
            if (Shader == null)
            {
                Shader = Resources.Load<ComputeShader>("RayTracingSpatialDenoiser");
            }
            
            if (Shader == null)
            {
                Debug.LogError("Spatial denoiser shader was not found at Resources/RayTracingSpatialDenoiser.");
            }
        }

        private RenderTexture GetIterationTexture(int iteration, Vector2Int size)
        {
            if (iteration == 1)
            {
                if (_iteration1 == null)
                {
                    _iteration1 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
                }
                return _iteration1;
            }
            
            if (iteration == 2)
            {
                if (_iteration2 == null)
                {
                    _iteration2 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
                }
                return _iteration2;
            }
            
            if (_iteration3 == null)
            {
                _iteration3 = CreateTextureInternal(size, RenderTextureFormat.ARGBHalf);
            }
            return _iteration3;
        }

        private static RenderTexture CreateTextureInternal(Vector2Int size, RenderTextureFormat format)
        {
            var texture = new RenderTexture(size.x, size.y, 0, format) { enableRandomWrite = true, filterMode = FilterMode.Point };
            texture.Create(); 
            
            return texture;
        }

        private static void Release(RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
            }
        }
    }
}
