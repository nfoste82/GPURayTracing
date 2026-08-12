using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public struct BvhNode
    {
        public Vector3 boundsMin;
        public int leftChildIndex;
        public Vector3 boundsMax;
        public int rightChildIndex;
        public int triangleStart;
        public int triangleCount;
        public int padding0;
        public int padding1;
    }
}