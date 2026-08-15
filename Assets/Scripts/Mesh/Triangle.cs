using UnityEngine;

namespace PathTracing.Shapes
{
    public struct Triangle
    {
        public Vector3 vertex0;
        public Vector3 vertex1;
        public Vector3 vertex2;
        public Vector3 normal;
        public Vector3 normal0;
        public Vector3 normal1;
        public Vector3 normal2;
        public Vector4 tangent0;
        public Vector4 tangent1;
        public Vector4 tangent2;
        public Vector3 color;
        public float smoothness;
        public float metallic;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector2 uv2;
        public float opacity;
        public Vector3 emission;
        public float refraction;
        public float specular;
        public float transmission;
        public int materialType;
        public int meshIndex;
        public int textureIndex;
        public int metallicRoughnessTextureIndex;
        public int normalTextureIndex;
        public float normalStrength;
        public int parallaxTextureIndex;
        public Vector2 textureUvScale;
        public float textureUvRotation;
        public float parallaxStrength;
        public float minimumParallaxStrength;
        public int interpolateNormals;
        public int lightIndex;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var edge1 = vertex1 - vertex0;
            var edge2 = vertex2 - vertex0;
            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);
            var determinantScale = edge1.magnitude * p.magnitude;

            if (determinantScale <= 0.0f || Mathf.Abs(determinant) <= 0.000001f * determinantScale)
            {
                return -1.0f;
            }

            var inverseDeterminant = 1.0f / determinant;
            var t = origin - vertex0;
            var u = Vector3.Dot(t, p) * inverseDeterminant;

            if (u < 0.0f || u > 1.0f)
            {
                return -1.0f;
            }

            var q = Vector3.Cross(t, edge1);
            var v = Vector3.Dot(direction, q) * inverseDeterminant;

            if (v < 0.0f || u + v > 1.0f)
            {
                return -1.0f;
            }

            var hitDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
            return hitDistance > 0.001f ? hitDistance : -1.0f;
        }
    }
}
