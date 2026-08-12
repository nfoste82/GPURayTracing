using System.Collections.Generic;
using PathTracing.Lighting;
using PathTracing.Shapes;
using UnityEngine;
using Light = PathTracing.Lighting.Light;

namespace PathTracing.Caustics
{
    public class CausticsLogic
    {
        public static bool IsCausticRefractor(Sphere sphere)
        {
            return (sphere.materialType == 2 || sphere.opacity < 1.0f) && sphere.opacity < 1.0f;
        }

        public static bool IsCausticRefractor(Water water)
        {
            return water != null && water.Opacity < 1.0f;
        }
        
        public static bool IsCausticRefractor(MeshInfo mesh, List<Triangle> triangles)
        {
            if (mesh.isLight != 0 || mesh.triangleCount <= 0)
            {
                return false;
            }
        
            var triangleMaterial = triangles[mesh.triangleStart];
            return (triangleMaterial.materialType == 2 || triangleMaterial.opacity < 1.0f) && triangleMaterial.opacity < 1.0f;
        }
        
        public static bool IsCausticLight(Light light)
        {
            return light.type == (int)PathTracedLightType.Sphere || 
                   ((light.type == (int)PathTracedLightType.Triangle || light.type == (int)PathTracedLightType.SunTriangle) && light.area > 1e-6f);
        }
        
        public static float GetCausticPairWeight(Light light, Vector3 targetPosition, float targetRadius)
        {
            var lightPosition = (light.type == (int)PathTracedLightType.Triangle || light.type == (int)PathTracedLightType.SunTriangle)
                ? light.position + (light.u + light.v) / 3.0f
                : light.position;
        
            var distanceSquared = Mathf.Max(1e-6f, (targetPosition - lightPosition).sqrMagnitude);
            var projectedTarget = Mathf.Min(4.0f * Mathf.PI, Mathf.PI * targetRadius * targetRadius / distanceSquared);
            
            var luminance = Vector3.Dot(light.emission, new Vector3(0.2126f, 0.7152f, 0.0722f));
            
            var emitterScale = (light.type == (int)PathTracedLightType.Triangle || light.type == (int)PathTracedLightType.SunTriangle) ? Mathf.Max(1e-6f, light.area) : 1.0f;
            
            var facing = (light.type == (int)PathTracedLightType.Triangle || light.type == (int)PathTracedLightType.SunTriangle)
                ? Mathf.Max(0.0f, Vector3.Dot(light.normal, (targetPosition - lightPosition).normalized))
                : 1.0f;
            
            return Mathf.Max(0.0f, luminance) * emitterScale * facing * Mathf.Max(1e-8f, projectedTarget);
        }
        
        public static Vector3Int CalculateCausticGridDimensions(Vector3 size, float cellSize)
        {
            return new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.z / cellSize)));
        }
        
        public static void EncapsulateCausticBounds(Vector3 min, Vector3 max, ref bool hasBounds, ref Vector3 boundsMin, ref Vector3 boundsMax)
        {
            if (!hasBounds)
            {
                boundsMin = min;
                boundsMax = max;
                hasBounds = true;
                return;
            }

            boundsMin = Vector3.Min(boundsMin, min);
            boundsMax = Vector3.Max(boundsMax, max);
        }
    }
}