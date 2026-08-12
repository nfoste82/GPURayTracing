using UnityEngine;

namespace PathTracing.Shapes
{
    public struct MeshInfo
    {
        public Vector3 boundsMin;
        public int rootNodeIndex;
        public Vector3 boundsMax;
        public int triangleStart;
        public int triangleCount;
        public int meshIndex;
        public int isLight;
        public int lightIndex;
    }
}