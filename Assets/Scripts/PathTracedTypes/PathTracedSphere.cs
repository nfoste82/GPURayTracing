using UnityEngine;

namespace PathTracing.PathTracedTypes
{
    public struct PathTracedSphere
    {
        public PathTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public SphereCollider collider;
    }
}