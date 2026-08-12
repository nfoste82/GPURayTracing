using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public struct TopLevelBvhNode
    {
        public Vector3 boundsMin;
        public int leftChildIndex;
        public Vector3 boundsMax;
        public int rightChildIndex;
        public int objectType;
        public int objectIndex;
        public int padding0;
        public int padding1;
    }
}