using UnityEngine;

namespace PathTracing.Lighting
{
    public struct Light : System.IEquatable<Light>
    {
        public Vector3 position;
        public Vector3 emission;
        public Vector3 u;
        public float radius;
        public Vector3 v;
        public float area;
        public Vector3 normal;
        public int type;
        public int triangleStart;
        public int triangleCount;
        public float totalArea;
        public float padding;

        public int AddHash(int hash)
        {
            hash = GameManager.AddHash(hash, position);
            hash = GameManager.AddHash(hash, emission);
            hash = GameManager.AddHash(hash, u);
            hash = GameManager.AddHash(hash, radius);
            hash = GameManager.AddHash(hash, v);
            hash = GameManager.AddHash(hash, area);
            hash = GameManager.AddHash(hash, normal);
            return GameManager.AddHash(hash, type);
        }

        public bool Equals(Light other)
        {
            return position == other.position
                && emission == other.emission
                && u == other.u
                && radius == other.radius
                && v == other.v
                && area == other.area
                && normal == other.normal
                && type == other.type
                && triangleStart == other.triangleStart
                && triangleCount == other.triangleCount
                && totalArea == other.totalArea
                && padding == other.padding;
        }

        public override bool Equals(object obj)
        {
            return obj is Light other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = position.GetHashCode();
                hash = hash * 31 + emission.GetHashCode();
                hash = hash * 31 + u.GetHashCode();
                hash = hash * 31 + radius.GetHashCode();
                hash = hash * 31 + v.GetHashCode();
                hash = hash * 31 + area.GetHashCode();
                hash = hash * 31 + normal.GetHashCode();
                hash = hash * 31 + type;
                hash = hash * 31 + triangleStart;
                hash = hash * 31 + triangleCount;
                hash = hash * 31 + totalArea.GetHashCode();
                return hash * 31 + padding.GetHashCode();
            }
        }
        
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
