using System;
using System.Collections;
using System.Collections.Generic;
using PathTracing.Shapes;
using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public sealed class MeshBvhTemplateCache : ICollection
    {
        private readonly Dictionary<long, MeshBvhTemplate> _templates = new();

        public int Count => _templates.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => ((ICollection)_templates).SyncRoot;

        public MeshBvhTemplate GetOrBuild(Mesh mesh, bool interpolateNormals, int leafTriangleCount, int stackSize,
            float boundsPadding, out bool wasBuilt)
        {
            long key = GetKey(mesh, interpolateNormals);
            if (_templates.TryGetValue(key, out MeshBvhTemplate template))
            {
                wasBuilt = false;
                return template;
            }

            template = MeshBvhBuilder.Build(mesh, interpolateNormals, leafTriangleCount, stackSize, boundsPadding);
            _templates.Add(key, template);
            wasBuilt = true;
            return template;
        }

        public void Clear() => _templates.Clear();

        public void Set(Mesh mesh, bool interpolateNormals, MeshBvhTemplate template)
        {
            _templates[GetKey(mesh, interpolateNormals)] = template;
        }

        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public IEnumerator GetEnumerator() => _templates.GetEnumerator();

        private static long GetKey(Mesh mesh, bool interpolateNormals)
        {
            return ((long)mesh.GetInstanceID() << 1) | (interpolateNormals ? 1L : 0L);
        }
    }
}
