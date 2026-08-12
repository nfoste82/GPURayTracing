using System.Collections.Generic;
using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public class TopLevelBvhBuildItemComparer : IComparer<TopLevelBvhBuildItem>
    {
        public int Axis;

        public int Compare(TopLevelBvhBuildItem x, TopLevelBvhBuildItem y)
        {
            return GetTopLevelBvhItemCentroid(x)[Axis].CompareTo(GetTopLevelBvhItemCentroid(y)[Axis]);
        }
        
        private static Vector3 GetTopLevelBvhItemCentroid(TopLevelBvhBuildItem item)
        {
            return (item.boundsMin + item.boundsMax) * 0.5f;
        }
    }
}