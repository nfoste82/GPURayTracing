using System.Collections.Generic;
using PathTracing.Lighting;
using PathTracing.Shapes;
using UnityEngine;
using Light = PathTracing.Lighting.Light;

namespace PathTracing.AccelerationStructures
{
    public class SceneBvhManager
    {
        public const int LeafTriangleCount = 4;
        public const int StackSize = 32;
        public const int BakeFormatVersion = 2;
        public const float BoundsPadding = 0.001f;
        private static readonly int NumTopLevelBvhNodes = Shader.PropertyToID("_NumTopLevelBvhNodes");
        private static readonly int NumShadowBvhNodes = Shader.PropertyToID("_NumShadowBvhNodes");
        private static readonly int TopLevelBvhNodes = Shader.PropertyToID("_TopLevelBvhNodes");
        private static readonly int ShadowBvhNodes = Shader.PropertyToID("_ShadowBvhNodes");
        private readonly List<TopLevelBvhNode> _topLevelNodes = new();
        private readonly List<TopLevelBvhNode> _shadowNodes = new();
        private readonly List<TopLevelBvhBuildItem> _topLevelItems = new();
        private readonly List<TopLevelBvhBuildItem> _shadowItems = new();
        private readonly TopLevelBvhBuilder _builder = new();
        private ComputeBuffer _topLevelBuffer;
        private ComputeBuffer _shadowBuffer;
        private bool _topLevelDirty = true;
        private bool _shadowDirty = true;
        private int _lastTopLevelMinObjectCount = int.MinValue;
        private int _lastShadowMinObjectCount = int.MinValue;

        public int TopLevelNodeCount => _topLevelNodes.Count;
        public int ShadowNodeCount => _shadowNodes.Count;
        public int TopLevelObjectCount => _topLevelItems.Count;
        public int ShadowObjectCount => _shadowItems.Count;
        public bool IsTopLevelActive => _topLevelNodes.Count > 0;
        public bool IsShadowActive => _shadowNodes.Count > 0;
        public bool IsTopLevelDirty => _topLevelDirty;

        public void MarkTopLevelDirty() => _topLevelDirty = true;
        public void MarkShadowDirty() => _shadowDirty = true;
        public void MarkAllDirty()
        {
            _topLevelDirty = true;
            _shadowDirty = true;
        }

        public void Update(List<Sphere> spheres, IReadOnlyList<Light> lights, List<MeshInfo> meshes,
            int topLevelMinObjectCount, int shadowMinObjectCount, int stackSize)
        {
            UpdateTopLevel(spheres, lights, meshes, topLevelMinObjectCount, stackSize);
            UpdateShadow(spheres, meshes, shadowMinObjectCount, stackSize);
        }

        public void Rebuild(List<Sphere> spheres, IReadOnlyList<Light> lights, List<MeshInfo> meshes,
            int topLevelMinObjectCount, int shadowMinObjectCount, int stackSize)
        {
            RebuildTopLevel(spheres, lights, meshes, topLevelMinObjectCount, stackSize);
            RebuildShadow(spheres, meshes, shadowMinObjectCount, stackSize);
        }

        public void RebuildTopLevel(List<Sphere> spheres, IReadOnlyList<Light> lights, List<MeshInfo> meshes,
            int minObjectCount, int stackSize)
        {
            BuildTopLevel(spheres, lights, meshes, minObjectCount, stackSize);
            _topLevelDirty = false;
            _lastTopLevelMinObjectCount = minObjectCount;
            UploadBuffer(ref _topLevelBuffer, _topLevelNodes);
        }

        public void RebuildShadow(List<Sphere> spheres, List<MeshInfo> meshes, int minObjectCount, int stackSize)
        {
            BuildShadow(spheres, meshes, minObjectCount, stackSize);
            _shadowDirty = false;
            _lastShadowMinObjectCount = minObjectCount;
            UploadBuffer(ref _shadowBuffer, _shadowNodes);
        }

        public void SetShaderParameters(ComputeShader shader)
        {
            shader.SetInt(NumTopLevelBvhNodes, _topLevelNodes.Count);
            shader.SetInt(NumShadowBvhNodes, _shadowNodes.Count);
        }

        public void SetBuffers(ComputeShader shader, int kernelHandle)
        {
            if (_topLevelBuffer != null) shader.SetBuffer(kernelHandle, TopLevelBvhNodes, _topLevelBuffer);
            if (_shadowBuffer != null) shader.SetBuffer(kernelHandle, ShadowBvhNodes, _shadowBuffer);
        }

        public void Release()
        {
            _topLevelBuffer?.Release();
            _shadowBuffer?.Release();
            _topLevelBuffer = null;
            _shadowBuffer = null;
        }

        private void UpdateTopLevel(List<Sphere> spheres, IReadOnlyList<Light> lights, List<MeshInfo> meshes, int minObjectCount, int stackSize)
        {
            if (!_topLevelDirty && _lastTopLevelMinObjectCount == minObjectCount) return;
            BuildTopLevel(spheres, lights, meshes, minObjectCount, stackSize);
            _topLevelDirty = false;
            _lastTopLevelMinObjectCount = minObjectCount;
            UploadBuffer(ref _topLevelBuffer, _topLevelNodes);
        }

        private void UpdateShadow(List<Sphere> spheres, List<MeshInfo> meshes, int minObjectCount, int stackSize)
        {
            if (!_shadowDirty && _lastShadowMinObjectCount == minObjectCount) return;
            BuildShadow(spheres, meshes, minObjectCount, stackSize);
            _shadowDirty = false;
            _lastShadowMinObjectCount = minObjectCount;
            UploadBuffer(ref _shadowBuffer, _shadowNodes);
        }

        private void BuildTopLevel(List<Sphere> spheres, IReadOnlyList<Light> lights, List<MeshInfo> meshes, int minObjectCount, int stackSize)
        {
            _topLevelNodes.Clear();
            _topLevelItems.Clear();
            for (int i = 0; i < spheres.Count; i++) AddSphere(_topLevelItems, spheres[i].position, spheres[i].radius, 0, i);
            for (int i = 0; i < lights.Count; i++)
            {
                if (lights[i].type == (int)PathTracedLightType.Sphere) AddSphere(_topLevelItems, lights[i].position, lights[i].radius, 1, i);
            }
            AddMeshes(_topLevelItems, meshes, false);
            if (_topLevelItems.Count >= minObjectCount && _topLevelItems.Count > 0) _builder.Build(_topLevelItems, _topLevelNodes, stackSize);
        }

        private void BuildShadow(List<Sphere> spheres, List<MeshInfo> meshes, int minObjectCount, int stackSize)
        {
            _shadowNodes.Clear();
            _shadowItems.Clear();
            for (int i = 0; i < spheres.Count; i++) AddSphere(_shadowItems, spheres[i].position, spheres[i].radius, 0, i);
            AddMeshes(_shadowItems, meshes, true);
            if (_shadowItems.Count >= minObjectCount && _shadowItems.Count > 0) _builder.Build(_shadowItems, _shadowNodes, stackSize);
        }

        private static void AddMeshes(List<TopLevelBvhBuildItem> items, List<MeshInfo> meshes, bool excludeLights)
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                if (excludeLights && meshes[i].isLight != 0) continue;
                items.Add(new TopLevelBvhBuildItem { boundsMin = meshes[i].boundsMin, boundsMax = meshes[i].boundsMax, objectType = 2, objectIndex = i });
            }
        }

        private static void AddSphere(List<TopLevelBvhBuildItem> items, Vector3 position, float radius, int objectType, int objectIndex)
        {
            Vector3 extent = Vector3.one * (radius + BoundsPadding);
            items.Add(new TopLevelBvhBuildItem { boundsMin = position - extent, boundsMax = position + extent, objectType = objectType, objectIndex = objectIndex });
        }

        private static void UploadBuffer(ref ComputeBuffer buffer, List<TopLevelBvhNode> nodes)
        {
            int requiredCount = Mathf.Max(1, nodes.Count);
            if (buffer == null || buffer.count < requiredCount)
            {
                buffer?.Release();
                buffer = new ComputeBuffer(requiredCount, 48);
            }
            if (nodes.Count > 0) buffer.SetData(nodes);
            else buffer.SetData(new[] { default(TopLevelBvhNode) });
        }
    }
}
