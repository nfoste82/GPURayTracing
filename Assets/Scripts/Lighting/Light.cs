using UnityEngine;

namespace PathTracing.Lighting
{
    public struct Light
    {
        public Vector3 position;
        public Vector3 emission;
        public Vector3 u;
        public Vector3 v;
        public float radius;
        public float area;
        public Vector3 normal;
        public int type;
        public int triangleStart;
        public int triangleCount;
        public float totalArea;
        public float padding;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var diffToSphere = position - origin;
            var b = Vector3.Dot(diffToSphere, direction);
            if (b < 0f)
            {
                return -1.0f;
            }

            var c = diffToSphere.sqrMagnitude - radius * radius;
            var discriminant = (b * b) - c;
            if (discriminant < 0.0f)
            {
                return -1.0f;
            }

            var hitDistance = b - Mathf.Sqrt(discriminant) - 0.001f;
            return hitDistance < 0.0f ? 0.0f : hitDistance;
        }
    }
}