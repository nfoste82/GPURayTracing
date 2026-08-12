using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public struct TopLevelBvhBuildItem
    {
        public Vector3 boundsMin;
        public Vector3 boundsMax;
        public int objectType;
        public int objectIndex;
    }
}