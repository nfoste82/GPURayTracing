using UnityEngine;

namespace PathTracing.PathTracedTypes
{
    public struct PathTracedLight
    {
        public PathTracingObject obj;
        public Transform transform;
        public RayLight light;
        public SphereCollider collider;
    }
}