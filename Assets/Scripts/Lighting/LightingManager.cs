using System;
using System.Collections.Generic;
using PathTracing.PathTracedTypes;
using PathTracing.Shapes;
using UnityEngine;

namespace PathTracing.Lighting
{
    /// Owns scene-light state, GPU light buffers, and the lighting controls edited on GameManager.
    /// Acceleration-structure integration remains with GameManager.
    [Serializable]
    public sealed class LightingManager
    {
        private const float VirtualSunDistance = 10000.0f;
        private const int LightStride = 88;
        private const int MeshLightTriangleCdfStride = 4;
        private const int MaxImportanceLights = 128;
        private static readonly int SkyboxLight = Shader.PropertyToID("_SkyboxLight");
        private static readonly int SamplingStrategy = Shader.PropertyToID("_LightSamplingStrategy");
        private static readonly int LightSampleCountId = Shader.PropertyToID("_LightSampleCount");
        private static readonly int LightFalloffScaleId = Shader.PropertyToID("_LightFalloffScale");
        private static readonly int NumLights = Shader.PropertyToID("_NumLights");
        private static readonly int HasTransparentShadowBlockersId = Shader.PropertyToID("_HasTransparentShadowBlockers");
        private static readonly int MaxLightSamples = Shader.PropertyToID("_MaxLightSamples");
        private static readonly int ShadowQuality = Shader.PropertyToID("_ShadowQuality");
        private static readonly int ShadowRandomness = Shader.PropertyToID("_ShadowRandomness");
        private static readonly int LightsId = Shader.PropertyToID("_Lights");
        private static readonly int MeshLightTriangleCdfId = Shader.PropertyToID("_MeshLightTriangleCdf");

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
        private readonly List<float> _meshLightTriangleCdf = new();
        private ComputeBuffer _lightBuffer;
        private ComputeBuffer _meshLightTriangleCdfBuffer;
        private bool _warnedImportanceLightOverflow;
        private bool _hasTransparentSphereBlockers;
        private bool _hasTransparentMeshBlockers;

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
        public int LightCount => _lights.Count;
        public int SphereLightCount => _lightObjects.Count;
        public int DirectionalLightCount => _directionalLights.Count;

        public IReadOnlyList<float> MeshLightTriangleCdf => _meshLightTriangleCdf;
        public ComputeBuffer LightBuffer => _lightBuffer;
        public ComputeBuffer MeshLightTriangleCdfBuffer => _meshLightTriangleCdfBuffer;
        public bool HasTransparentShadowBlockers => _hasTransparentSphereBlockers || _hasTransparentMeshBlockers;

        public int AddLightStateHash(int hash)
        {
            unchecked
            {
                hash = hash * 31 + _lights.Count;
                for (var i = 0; i < _lights.Count; i++)
                {
                    hash = _lights[i].AddHash(hash);
                }
                return hash;
            }
        }

        public void SetShaderParameters(ComputeShader shader)
        {
            shader.SetVector(SkyboxLight, new Vector4(
                _skyboxLightColor.r / 255.0f,
                _skyboxLightColor.g / 255.0f,
                _skyboxLightColor.b / 255.0f,
                1.0f));
            shader.SetInt(SamplingStrategy, (int)_lightSamplingStrategy);
            shader.SetInt(LightSampleCountId, _lightSampleCount);
            shader.SetFloat(LightFalloffScaleId, _lightFalloffScale);
            SetShaderLightCount(shader);
        }

        public void SetShaderLightCount(ComputeShader shader) => shader.SetInt(NumLights, _lights.Count);

        public void SetShaderTransparentShadowBlockers(ComputeShader shader, bool hasTransparentShadowBlockers) =>
            shader.SetInt(HasTransparentShadowBlockersId, hasTransparentShadowBlockers ? 1 : 0);

        public void SetShaderSamplingParameters(ComputeShader shader, int maxLightSamples, int shadowQuality,
            float shadowRandomness)
        {
            shader.SetInt(MaxLightSamples, maxLightSamples);
            shader.SetInt(ShadowQuality, shadowQuality);
            shader.SetFloat(ShadowRandomness, shadowRandomness);
        }

        public void SetTransparentSphereBlockers(bool value) => _hasTransparentSphereBlockers = value;

        public void SetTransparentMeshBlockers(bool value) => _hasTransparentMeshBlockers = value;

        public void WarnIfImportanceLightLimitExceeded()
        {
            if (_lightSamplingStrategy == LightSamplingStrategy.ImportanceSampled && _lights.Count > MaxImportanceLights)
            {
                if (!_warnedImportanceLightOverflow)
                {
                    Debug.LogWarning(
                        $"ImportanceSampled light strategy supports up to {MaxImportanceLights} lights, " +
                        $"but the scene has {_lights.Count}. Lights beyond {MaxImportanceLights} are ignored " +
                        "for importance weighting. Raise MaxImportanceLights in RayTracingCompute.compute " +
                        "(and the matching constant in LightingManager) or use a different light sampling strategy.");
                    _warnedImportanceLightOverflow = true;
                }
            }
            else
            {
                _warnedImportanceLightOverflow = false;
            }
        }

        public void RegisterSphereLight(PathTracingObject obj, Transform transform, RayLight light,
            SphereCollider collider, List<Triangle> triangles, float radius, Action onChanged)
        {
            var lightData = new Light
            {
                position = transform.TransformPoint(collider.center),
                radius = radius,
                area = Mathf.PI * radius * radius,
                emission = light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity),
                type = (int)PathTracedLightType.Sphere
            };
            int insertionIndex = _lightObjects.Count;
            _lights.Insert(insertionIndex, lightData);
            ShiftTriangleLightIndices(triangles, insertionIndex, 1);
            _lightObjects.Add(new PathTracedLight
            {
                obj = obj,
                transform = transform,
                light = light,
                collider = collider
            });
            onChanged?.Invoke();
        }

        public bool UnregisterSphereLight(PathTracingObject obj, List<Triangle> triangles, Action onChanged)
        {
            var lightIndex = _lightObjects.FindIndex(light => light.obj == obj);
            if (lightIndex < 0)
            {
                return false;
            }

            _lightObjects.RemoveAt(lightIndex);
            _lights.RemoveAt(lightIndex);
            ShiftTriangleLightIndices(triangles, lightIndex, -1);
            onChanged?.Invoke();
            return true;
        }

        public bool UpdateSphereLights(out bool lightsChanged, out bool boundsChanged)
        {
            lightsChanged = false;
            boundsChanged = false;
            for (var i = 0; i < _lightObjects.Count; i++)
            {
                var lightObject = _lightObjects[i];
                var lightData = _lights[i];
                var previousLightData = lightData;
                var position = lightObject.transform.TransformPoint(lightObject.collider.center);
                var radius = GetWorldSphereRadius(lightObject.collider, lightObject.transform);
                boundsChanged |= lightData.position != position || !Mathf.Approximately(lightData.radius, radius);
                lightData.position = position;
                lightData.radius = radius;
                lightData.area = Mathf.PI * lightData.radius * lightData.radius;
                lightData.type = (int)PathTracedLightType.Sphere;
                lightData.emission = lightObject.light.Color.ToVector3() * Mathf.Max(0.0f, lightObject.light.Intensity);
                lightsChanged |= !lightData.Equals(previousLightData);
                _lights[i] = lightData;
            }

            return lightsChanged;
        }

        public void AddMeshLight(List<Triangle> triangles, int triangleStart, int triangleCount,
            Vector3 position, float totalArea, Vector3 emission, out int lightIndex)
        {
            lightIndex = _lights.Count;
            for (var triangleIndex = triangleStart; triangleIndex < triangleStart + triangleCount; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                triangle.lightIndex = lightIndex;
                triangles[triangleIndex] = triangle;
            }

            _lights.Add(new Light
            {
                position = position,
                emission = emission,
                type = (int)PathTracedLightType.Mesh,
                triangleStart = triangleStart,
                triangleCount = triangleCount,
                totalArea = totalArea
            });
        }

        public void RemoveMeshLights()
        {
            var analyticLightCount = _lightObjects.Count + _directionalLights.Count * 2;
            if (_lights.Count > analyticLightCount)
            {
                _lights.RemoveRange(analyticLightCount, _lights.Count - analyticLightCount);
            }
        }

        public void UpdateMeshLightEmission(int lightIndex, Vector3 emission)
        {
            if (lightIndex < 0 || lightIndex >= _lights.Count)
            {
                return;
            }

            var light = _lights[lightIndex];
            light.emission = emission;
            _lights[lightIndex] = light;
        }

        public void RebuildMeshLightTriangleCdf(List<Triangle> triangles, List<MeshInfo> meshes)
        {
            _meshLightTriangleCdf.Clear();
            for (var i = 0; i < triangles.Count; i++)
            {
                _meshLightTriangleCdf.Add(0.0f);
            }

            for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                var mesh = meshes[meshIndex];
                if (mesh.lightIndex < 0 || mesh.lightIndex >= _lights.Count)
                {
                    continue;
                }

                var cumulativeArea = 0.0f;
                for (var triangleIndex = mesh.triangleStart;
                    triangleIndex < mesh.triangleStart + mesh.triangleCount; triangleIndex++)
                {
                    var triangle = triangles[triangleIndex];
                    cumulativeArea += 0.5f * Vector3.Cross(
                        triangle.vertex1 - triangle.vertex0,
                        triangle.vertex2 - triangle.vertex0).magnitude;
                    _meshLightTriangleCdf[triangleIndex] = cumulativeArea;
                }

                if (cumulativeArea <= 0.000001f)
                {
                    continue;
                }

                for (var triangleIndex = mesh.triangleStart;
                    triangleIndex < mesh.triangleStart + mesh.triangleCount; triangleIndex++)
                {
                    _meshLightTriangleCdf[triangleIndex] /= cumulativeArea;
                }
                _meshLightTriangleCdf[mesh.triangleStart + mesh.triangleCount - 1] = 1.0f;
            }
        }

        public void EnsureBuffers()
        {
            var requiredLightCount = Mathf.Max(1, _lights.Count);
            if (_lightBuffer == null || _lightBuffer.count < requiredLightCount)
            {
                _lightBuffer?.Release();
                _lightBuffer = CreateComputeBuffer(_lights, LightStride);
            }

            var requiredCdfCount = Mathf.Max(1, _meshLightTriangleCdf.Count);
            if (_meshLightTriangleCdfBuffer == null || _meshLightTriangleCdfBuffer.count < requiredCdfCount)
            {
                _meshLightTriangleCdfBuffer?.Release();
                _meshLightTriangleCdfBuffer = CreateComputeBuffer(_meshLightTriangleCdf, MeshLightTriangleCdfStride);
            }
        }

        public void UploadLightData()
        {
            EnsureBuffers();
            if (_lights.Count > 0)
            {
                _lightBuffer.SetData(_lights);
            }
        }

        public void UploadMeshLightTriangleCdf()
        {
            EnsureBuffers();
            _meshLightTriangleCdfBuffer.SetData(_meshLightTriangleCdf.Count > 0
                ? _meshLightTriangleCdf
                : new List<float> { 0.0f });
        }

        public void SetBuffers(ComputeShader shader, int kernelHandle)
        {
            if (_lightBuffer != null)
            {
                shader.SetBuffer(kernelHandle, LightsId, _lightBuffer);
            }
            if (_meshLightTriangleCdfBuffer != null)
            {
                shader.SetBuffer(kernelHandle, MeshLightTriangleCdfId, _meshLightTriangleCdfBuffer);
            }
        }

        public void ReleaseBuffers()
        {
            _lightBuffer?.Release();
            _meshLightTriangleCdfBuffer?.Release();
            _lightBuffer = null;
            _meshLightTriangleCdfBuffer = null;
        }

        public float GetNearestSphereLightIntersection(Ray ray, float nearestDistance)
        {
            for (var i = 0; i < _lights.Count; i++)
            {
                if (_lights[i].type != (int)PathTracedLightType.Sphere)
                {
                    continue;
                }

                var hitDistance = _lights[i].Intersect(ray.origin, ray.direction);
                if (hitDistance >= 0.0f && hitDistance < nearestDistance)
                {
                    nearestDistance = hitDistance;
                }
            }
            return nearestDistance;
        }

        private static void ShiftTriangleLightIndices(List<Triangle> triangles, int index, int delta)
        {
            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                if (delta > 0 ? triangle.lightIndex >= index : triangle.lightIndex > index)
                {
                    triangle.lightIndex += delta;
                    triangles[i] = triangle;
                }
            }
        }

        private static float GetWorldSphereRadius(SphereCollider sphereCollider, Transform sphereTransform)
        {
            var scale = sphereTransform.lossyScale;
            var largestAxisScale = Mathf.Abs(scale.x);
            var yScale = Mathf.Abs(scale.y);
            if (yScale > largestAxisScale) largestAxisScale = yScale;
            var zScale = Mathf.Abs(scale.z);
            if (zScale > largestAxisScale) largestAxisScale = zScale;
            return sphereCollider.radius * largestAxisScale;
        }

        private static ComputeBuffer CreateComputeBuffer<T>(List<T> data, int stride) where T : struct
        {
            var buffer = new ComputeBuffer(Mathf.Max(1, data.Count), stride);
            if (data.Count > 0)
            {
                buffer.SetData(data);
            }
            else
            {
                buffer.SetData(new[] { default(T) });
            }
            return buffer;
        }

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
