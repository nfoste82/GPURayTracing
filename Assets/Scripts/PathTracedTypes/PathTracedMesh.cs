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

        public int AddHash(int hash)
        {
            hash = GameManager.AddHash(hash, transform.localToWorldMatrix);
            hash = GameManager.AddHash(hash, previousColor);
            hash = GameManager.AddHash(hash, previousEmission);
            hash = GameManager.AddHash(hash, previousSmoothness);
            hash = GameManager.AddHash(hash, previousMetallic);
            hash = GameManager.AddHash(hash, previousOpacity);
            hash = GameManager.AddHash(hash, previousRefraction);
            hash = GameManager.AddHash(hash, previousSpecular);
            hash = GameManager.AddHash(hash, previousTransmission);
            hash = GameManager.AddHash(hash, previousMaterialType);
            hash = GameManager.AddHash(hash, previousAlbedoTexture != null ? previousAlbedoTexture.GetInstanceID() : 0);
            hash = GameManager.AddHash(hash, previousMetallicRoughnessTexture != null ? previousMetallicRoughnessTexture.GetInstanceID() : 0);
            hash = GameManager.AddHash(hash, previousNormalTexture != null ? previousNormalTexture.GetInstanceID() : 0);
            hash = GameManager.AddHash(hash, previousParallaxTexture != null ? previousParallaxTexture.GetInstanceID() : 0);
            hash = GameManager.AddHash(hash, previousTextureUvScale);
            hash = GameManager.AddHash(hash, previousParallaxStrength);
            hash = GameManager.AddHash(hash, previousMinimumParallaxStrength);
            return GameManager.AddHash(hash, previousInterpolateNormals ? 1 : 0);
        }
    }
}
