using UnityEngine;

namespace DefaultNamespace
{
    public static class ColorExtensions
    {
        public static Vector3 ToVector3(this Color32 color)
        {
            return new Vector3(color.r / 255f, color.g / 255f, color.b / 255f);
        }
    }
}