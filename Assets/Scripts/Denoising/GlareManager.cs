using System;
using UnityEngine;

namespace PathTracing.Denoising
{
    [Serializable]
    public sealed class GlareManager
    {
        private static readonly int GlareResult = Shader.PropertyToID("GlareResult");
        private static readonly int InputBeauty = Shader.PropertyToID("InputBeauty");
        private static readonly int GlareThreshold = Shader.PropertyToID("_GlareThreshold");
        private static readonly int GlareSoftKnee = Shader.PropertyToID("_GlareSoftKnee");
        private static readonly int PresentationResult = Shader.PropertyToID("PresentationResult");
        private static readonly int Exposure = Shader.PropertyToID("_Exposure");
        private static readonly int EnableGlare = Shader.PropertyToID("_EnableGlare");
        private static readonly int GlareIntensity = Shader.PropertyToID("_GlareIntensity");
        private ComputeShader _shader;
        private readonly RenderTexture[] _glareMips = new RenderTexture[4];

        public void Present(RenderTexture source, RenderTexture destination, float exposure,
            bool enabled, float threshold, float softKnee, float intensity)
        {
            EnsureShader();
            if (_shader == null || source == null || destination == null)
            {
                return;
            }

            var kernel = _shader.FindKernel("CSPresent");
            if (enabled)
            {
                DispatchGlare(source, destination, threshold, softKnee);
            }

            _shader.SetTexture(kernel, InputBeauty, source);
            _shader.SetTexture(kernel, PresentationResult, destination);
            _shader.SetFloat(Exposure, exposure);
            _shader.SetInt(EnableGlare, enabled ? 1 : 0);
            _shader.SetFloat(GlareIntensity, intensity);
            for (var i = 0; i < _glareMips.Length; i++)
            {
                _shader.SetTexture(kernel, $"GlareMip{i}", enabled ? _glareMips[i] : source);
            }

            ComputeDispatch.Dispatch(_shader, kernel, Mathf.CeilToInt(destination.width / 8.0f),
                Mathf.CeilToInt(destination.height / 8.0f), 1);
        }

        public void ReleaseResources()
        {
            for (var i = 0; i < _glareMips.Length; i++)
            {
                Release(_glareMips[i]);
                _glareMips[i] = null;
            }
        }

        private void DispatchGlare(RenderTexture source, RenderTexture destination, float threshold, float softKnee)
        {
            EnsureResources(destination.width, destination.height);
            
            if (_glareMips[0] == null)
            {
                return;
            }

            var prefilter = _shader.FindKernel("CSGlarePrefilter");
            _shader.SetTexture(prefilter, InputBeauty, source);
            _shader.SetTexture(prefilter, GlareResult, _glareMips[0]);
            _shader.SetFloat(GlareThreshold, Mathf.Max(0.0f, threshold));
            _shader.SetFloat(GlareSoftKnee, Mathf.Clamp01(softKnee));
            Dispatch(prefilter, _glareMips[0]);

            var downsample = _shader.FindKernel("CSGlareDownsample");
            for (var i = 1; i < _glareMips.Length; i++)
            {
                _shader.SetTexture(downsample, InputBeauty, _glareMips[i - 1]);
                _shader.SetTexture(downsample, GlareResult, _glareMips[i]);
                Dispatch(downsample, _glareMips[i]);
            }
        }

        private void EnsureResources(int width, int height)
        {
            var mipWidth = width;
            var mipHeight = height;
            for (var i = 0; i < _glareMips.Length; i++)
            {
                if (_glareMips[i] == null || _glareMips[i].width != mipWidth || _glareMips[i].height != mipHeight)
                {
                    Release(_glareMips[i]);
                    _glareMips[i] = CreateTexture(new Vector2Int(mipWidth, mipHeight));
                }
                mipWidth = Mathf.Max(1, mipWidth / 2);
                mipHeight = Mathf.Max(1, mipHeight / 2);
            }
        }

        private void Dispatch(int kernel, RenderTexture target)
        {
            ComputeDispatch.Dispatch(_shader, kernel, Mathf.CeilToInt(target.width / 8.0f),
                Mathf.CeilToInt(target.height / 8.0f), 1);
        }

        private void EnsureShader()
        {
            if (_shader == null)
            {
                _shader = Resources.Load<ComputeShader>("RayTracingSpatialDenoiser");
                
                if (_shader == null)
                {
                    Debug.LogError("Spatial denoiser shader was not found at Resources/RayTracingSpatialDenoiser.");
                }
            }
        }

        private static RenderTexture CreateTexture(Vector2Int size)
        {
            var texture = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBHalf)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
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
