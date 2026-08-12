using UnityEngine;

namespace PathTracing.PathTracedTypes
{
    public struct PathTracedMesh
    {
        public PathTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public RayLight light;
        public Mesh mesh;
        public Matrix4x4 previousLocalToWorld;
        public Vector3 previousColor;
        public Vector3 previousEmission;
        public float previousSmoothness;
        public float previousMetallic;
        public float previousOpacity;
        public float previousRefraction;
        public float previousSpecular;
        public float previousTransmission;
        public int previousMaterialType;
        public Texture2D previousAlbedoTexture;
        public Texture2D previousMetallicRoughnessTexture;
        public Texture2D previousNormalTexture;
        public Texture2D previousParallaxTexture;
        public Vector2 previousTextureUvScale;
        public float previousParallaxStrength;
        public float previousMinimumParallaxStrength;
        public bool previousInterpolateNormals;
    }
}
