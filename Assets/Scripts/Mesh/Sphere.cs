using UnityEngine;

namespace PathTracing.Shapes
{
    public struct Sphere
    {
        public Vector3 position;
        public Vector3 color;
        public Vector3 emission;
        public float radius;
        public float smoothness;
        public float opacity;
        public float refraction;
        public float specular;
        public float transmission;
        public int materialType;
        public int textureIndex;
        public int normalTextureIndex;
        public int parallaxTextureIndex;
        public Vector2 textureUvScale;
        public float parallaxStrength;
        public float minimumParallaxStrength;
        
        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var diffToSphere = position - origin;
            var b = Vector3.Dot(diffToSphere, direction);

            // ray is pointing away from sphere (b < 0)
            if (b < 0f)
            {
                return -1.0f;
            }
            
            var c = diffToSphere.sqrMagnitude - radius * radius;

            var discriminant = (b * b) - c; 

            // A negative discriminant corresponds to ray missing sphere 
            if (discriminant < 0.0f)
            {
                return -1.0f;
            } 

            // Ray now found to intersect sphere, compute smallest t value of intersection
            var hitDistance = b - Mathf.Sqrt(discriminant) - 0.001f;

            // If hit distance is negative, ray started inside sphere so clamp it to zero
            if (hitDistance < 0.0f)
            {
                hitDistance = 0.0f;
            }

            return hitDistance;
        }
    }
}
