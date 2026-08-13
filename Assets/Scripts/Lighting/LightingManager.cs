using System;
using System.Collections.Generic;
using PathTracing.PathTracedTypes;
using PathTracing.Shapes;
using UnityEngine;

namespace PathTracing.Lighting
{
    /// Owns scene-light state and the lighting controls edited on GameManager.
    /// Buffer upload and acceleration-structure integration remain with GameManager.
    [Serializable]
    public sealed class LightingManager
    {
        private const float VirtualSunDistance = 10000.0f;

        [SerializeField, Tooltip("How direct lighting samples scene lights. AllLights is accurate but scales with light count; UniformRandom is much faster in many-light scenes but noisy; ImportanceSampled favors bright/nearby lights for much less noise per sample.")]
        private LightSamplingStrategy _lightSamplingStrategy = LightSamplingStrategy.ImportanceSampled;

        [SerializeField, Range(1, 64), Tooltip("UniformRandom/ImportanceSampled only: how many lights each shading point samples per pass. 1 is fastest/noisiest; higher values reduce noise toward AllLights quality at proportional cost.")]
        private int _lightSampleCount = 1;

        [SerializeField, Range(0.001f, 1.0f), Tooltip("Higher values make direct light fall off faster with distance.")]
        private float _lightFalloffScale = 0.16f;

        [SerializeField]
        private Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);

        [NonSerialized]
        private readonly List<Light> _lights = new();
        [NonSerialized]
        private readonly List<PathTracedLight> _lightObjects = new();
        [NonSerialized]
        private readonly List<RayDirectionalLight> _directionalLights = new();

        public LightSamplingStrategy LightSamplingStrategy
        {
            get => _lightSamplingStrategy;
            set => _lightSamplingStrategy = value;
        }

        public int LightSampleCount
        {
            get => _lightSampleCount;
            set => _lightSampleCount = Mathf.Clamp(value, 1, 64);
        }

        public float LightFalloffScale
        {
            get => _lightFalloffScale;
            set => _lightFalloffScale = Mathf.Clamp(value, 0.001f, 1.0f);
        }

        public Color32 SkyboxLightColor
        {
            get => _skyboxLightColor;
            set => _skyboxLightColor = value;
        }

        public IReadOnlyList<Light> Lights => _lights;
        public IReadOnlyList<PathTracedLight> LightObjects => _lightObjects;
        public IReadOnlyList<RayDirectionalLight> DirectionalLights => _directionalLights;

        internal List<Light> MutableLights => _lights;
        internal List<PathTracedLight> MutableLightObjects => _lightObjects;
        internal List<RayDirectionalLight> MutableDirectionalLights => _directionalLights;

        public void RegisterDirectionalLight(
            RayDirectionalLight directionalLight,
            List<Triangle> triangles,
            Action onChanged)
        {
            if (directionalLight == null || _directionalLights.Contains(directionalLight))
            {
                return;
            }

            _directionalLights.Add(directionalLight);
            var insertionIndex = _lightObjects.Count + (_directionalLights.Count - 1) * 2;
            UpdateVirtualSunTriangles(directionalLight, out Light first, out Light second);
            _lights.Insert(insertionIndex, first);
            _lights.Insert(insertionIndex + 1, second);
            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle triangle = triangles[i];
                if (triangle.lightIndex >= insertionIndex)
                {
                    triangle.lightIndex += 2;
                    triangles[i] = triangle;
                }
            }

            onChanged?.Invoke();
        }

        public void UnregisterDirectionalLight(
            RayDirectionalLight directionalLight,
            List<Triangle> triangles,
            Action onChanged)
        {
            var directionalIndex = _directionalLights.IndexOf(directionalLight);
            if (directionalIndex < 0)
            {
                return;
            }

            var lightIndex = _lightObjects.Count + directionalIndex * 2;
            _directionalLights.RemoveAt(directionalIndex);
            _lights.RemoveAt(lightIndex);
            _lights.RemoveAt(lightIndex);
            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                if (triangle.lightIndex > lightIndex)
                {
                    triangle.lightIndex -= 2;
                    triangles[i] = triangle;
                }
            }

            onChanged?.Invoke();
        }

        public bool UpdateDirectionalLights(out bool boundsChanged)
        {
            var changed = false;
            boundsChanged = false;
            var directionalStart = _lightObjects.Count;
            for (var i = 0; i < _directionalLights.Count; i++)
            {
                var directionalLight = _directionalLights[i];
                var lightIndex = directionalStart + i * 2;
                if (directionalLight == null || lightIndex + 1 >= _lights.Count)
                {
                    continue;
                }

                UpdateVirtualSunTriangles(directionalLight, out Light first, out Light second);
                changed |= !first.Equals(_lights[lightIndex]) || !second.Equals(_lights[lightIndex + 1]);
                boundsChanged |= LightBoundsChanged(first, _lights[lightIndex])
                    || LightBoundsChanged(second, _lights[lightIndex + 1]);
                _lights[lightIndex] = first;
                _lights[lightIndex + 1] = second;
            }

            return changed;
        }

        private static void UpdateVirtualSunTriangles(
            RayDirectionalLight directionalLight,
            out Light first,
            out Light second)
        {
            var lightDirection = directionalLight.transform.forward.normalized;
            var center = -lightDirection * VirtualSunDistance;
            var radius = VirtualSunDistance * Mathf.Tan(
                Mathf.Clamp(directionalLight.AngularRadius, 0.0f, 10.0f) * Mathf.Deg2Rad);
            var tangent = Vector3.Cross(
                Mathf.Abs(lightDirection.y) < 0.999f ? Vector3.up : Vector3.right,
                lightDirection).normalized * radius;
            var bitangent = Vector3.Cross(lightDirection, tangent).normalized * radius;
            var emission = directionalLight.Color.ToVector3() * Mathf.Max(0.0f, directionalLight.Intensity);

            first = CreateVirtualSunTriangle(
                center - tangent - bitangent, tangent * 2.0f, bitangent * 2.0f, lightDirection, emission);
            second = CreateVirtualSunTriangle(
                center + tangent + bitangent, -tangent * 2.0f, -bitangent * 2.0f, lightDirection, emission);
        }

        private static Light CreateVirtualSunTriangle(
            Vector3 position,
            Vector3 u,
            Vector3 v,
            Vector3 normal,
            Vector3 emission)
        {
            return new Light
            {
                position = position,
                emission = emission,
                u = u,
                v = v,
                radius = VirtualSunDistance,
                area = Vector3.Cross(u, v).magnitude * 0.5f,
                normal = normal,
                type = (int)PathTracedLightType.SunTriangle
            };
        }

        private static bool LightBoundsChanged(Light current, Light previous)
        {
            return current.type != previous.type
                || current.position != previous.position
                || current.u != previous.u
                || current.v != previous.v;
        }
    }
}
