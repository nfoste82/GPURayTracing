using System;
using UnityEngine;

namespace PathTracing.Lighting
{
    /// <summary>Builds a luminance-times-solid-angle distribution for an equirectangular environment.</summary>
    public sealed class EnvironmentImportanceSampling : IDisposable
    {
        private const int CdfStride = sizeof(float);

        private ComputeBuffer _conditionalCdfBuffer;
        private ComputeBuffer _marginalCdfBuffer;
        private Texture2D _source;
        private int _width;
        private int _height;
        private int _maximumWidth;
        private int _maximumHeight;
        private float _highlightThreshold;
        private float _highlightSoftKnee;
        private float _highlightIntensity;

        public ComputeBuffer ConditionalCdfBuffer => _conditionalCdfBuffer;
        public ComputeBuffer MarginalCdfBuffer => _marginalCdfBuffer;
        public int Width => _width;
        public int Height => _height;
        public bool IsValid => _conditionalCdfBuffer != null && _marginalCdfBuffer != null && _width > 0 && _height > 0;

        public bool Rebuild(
            Texture texture, int maximumWidth, int maximumHeight,
            float highlightThreshold, float highlightSoftKnee, float highlightIntensity,
            out string error)
        {
            error = null;
            if (texture == null)
            {
                Clear();
                return false;
            }

            if (texture is not Texture2D source)
            {
                Clear();
                error = "Environment importance sampling requires a readable Texture2D HDRI.";
                return false;
            }

            int width = Mathf.Clamp(source.width, 1, Mathf.Max(1, maximumWidth));
            int height = Mathf.Clamp(source.height, 1, Mathf.Max(1, maximumHeight));
            Color[] pixels;
            int pixelWidth;
            int pixelHeight;
            if (source.isReadable)
            {
                pixels = source.GetPixels();
                pixelWidth = source.width;
                pixelHeight = source.height;
            }
            else
            {
                // Import settings commonly disable Read/Write for skyboxes. Read back a capped,
                // linear copy instead of requiring every environment asset to remain CPU-readable.
                pixels = ReadPixels(source, width, height);
                pixelWidth = width;
                pixelHeight = height;
            }
            var conditional = new float[width * height];
            var marginal = new float[height];

            for (int y = 0; y < height; y++)
            {
                float rowWeight = 0.0f;
                float sineTheta = Mathf.Sin(Mathf.PI * ((y + 0.5f) / height));
                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.Min(pixelWidth - 1, x * pixelWidth / width);
                    // Shader skybox V runs from zero at the north pole toward negative values;
                    // repeat wrapping maps it to the texture's rows in reverse order.
                    int sourceY = pixelHeight - 1 - Mathf.Min(pixelHeight - 1, y * pixelHeight / height);
                    Color color = pixels[sourceY * pixelWidth + sourceX];
                    float luminance = ApplyHighlightBoost(
                        Mathf.Max(0.0f, color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f),
                        highlightThreshold, highlightSoftKnee, highlightIntensity);
                    rowWeight += luminance * sineTheta;
                    conditional[y * width + x] = rowWeight;
                }

                if (rowWeight > 0.0f)
                {
                    for (int x = 0; x < width; x++)
                    {
                        conditional[y * width + x] /= rowWeight;
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        conditional[y * width + x] = (x + 1.0f) / width;
                    }
                }

                marginal[y] = rowWeight + (y > 0 ? marginal[y - 1] : 0.0f);
            }

            float totalWeight = marginal[height - 1];
            if (totalWeight > 0.0f)
            {
                for (int y = 0; y < height; y++)
                {
                    marginal[y] /= totalWeight;
                }
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    marginal[y] = (y + 1.0f) / height;
                }
            }

            EnsureBuffers(conditional.Length, marginal.Length);
            _conditionalCdfBuffer.SetData(conditional);
            _marginalCdfBuffer.SetData(marginal);
            _source = source;
            _width = width;
            _height = height;
            _maximumWidth = maximumWidth;
            _maximumHeight = maximumHeight;
            _highlightThreshold = highlightThreshold;
            _highlightSoftKnee = highlightSoftKnee;
            _highlightIntensity = highlightIntensity;
            return true;
        }

        private static Color[] ReadPixels(Texture2D source, int width, int height)
        {
            var previous = RenderTexture.active;
            var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readable = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);
                return readable.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(readable);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(readable);
                }
            }
        }

        public bool NeedsRebuild(
            Texture texture, int maximumWidth, int maximumHeight,
            float highlightThreshold, float highlightSoftKnee, float highlightIntensity) =>
            !IsValid || _source != texture || _maximumWidth != maximumWidth || _maximumHeight != maximumHeight
            || !Mathf.Approximately(_highlightThreshold, highlightThreshold)
            || !Mathf.Approximately(_highlightSoftKnee, highlightSoftKnee)
            || !Mathf.Approximately(_highlightIntensity, highlightIntensity);

        public void EnsureDummyBuffers()
        {
            if (_conditionalCdfBuffer != null && _marginalCdfBuffer != null)
            {
                return;
            }
            EnsureBuffers(1, 1);
            _conditionalCdfBuffer.SetData(new[] { 1.0f });
            _marginalCdfBuffer.SetData(new[] { 1.0f });
        }

        public void Clear()
        {
            _source = null;
            _width = 0;
            _height = 0;
            _maximumWidth = 0;
            _maximumHeight = 0;
            _highlightThreshold = 0.0f;
            _highlightSoftKnee = 0.0f;
            _highlightIntensity = 0.0f;
        }

        public void Dispose()
        {
            _conditionalCdfBuffer?.Release();
            _marginalCdfBuffer?.Release();
            _conditionalCdfBuffer = null;
            _marginalCdfBuffer = null;
            Clear();
        }

        private void EnsureBuffers(int conditionalCount, int marginalCount)
        {
            if (_conditionalCdfBuffer == null || _conditionalCdfBuffer.count != conditionalCount)
            {
                _conditionalCdfBuffer?.Release();
                _conditionalCdfBuffer = new ComputeBuffer(conditionalCount, CdfStride);
            }
            if (_marginalCdfBuffer == null || _marginalCdfBuffer.count != marginalCount)
            {
                _marginalCdfBuffer?.Release();
                _marginalCdfBuffer = new ComputeBuffer(marginalCount, CdfStride);
            }
        }

        private static float ApplyHighlightBoost(float luminance, float threshold, float softKnee, float intensity)
        {
            if (threshold <= 0.0f || intensity <= 0.0f)
            {
                return luminance;
            }

            float knee = Mathf.Max(0.0f, threshold * softKnee);
            float excess = Mathf.Max(0.0f, luminance - threshold);
            if (knee > 0.0f)
            {
                excess *= excess / (excess + knee);
            }
            return luminance + excess * intensity;
        }
    }
}
