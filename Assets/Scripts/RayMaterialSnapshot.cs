using PathTracing;
using PathTracing.PathTracedTypes;
using PathTracing.Shapes;
using UnityEngine;

internal readonly struct RayMaterialSnapshot
{
    public readonly Vector3 color;
    public readonly Vector3 emission;
    public readonly float smoothness;
    public readonly float metallic;
    public readonly float opacity;
    public readonly float refraction;
    public readonly float specular;
    public readonly float transmission;
    public readonly int materialType;
    public readonly Texture2D albedoTexture;
    public readonly Texture2D metallicRoughnessTexture;
    public readonly Texture2D normalTexture;
    public readonly Texture2D parallaxTexture;
    public readonly Vector2 textureUvScale;
    public readonly float parallaxStrength;
    public readonly float minimumParallaxStrength;
    public readonly bool interpolateNormals;

    private RayMaterialSnapshot(RayMaterial material, RayLight light)
    {
        color = material != null ? material.Color.ToVector3() : Vector3.one;
        emission = light != null ? light.Color.ToVector3() * Mathf.Max(0.0f, light.Intensity) : Vector3.zero;
        smoothness = material != null ? material.Smoothness : 0.0f;
        metallic = material != null ? GetEffectiveMetallic(material) : 0.0f;
        opacity = material != null ? Mathf.Clamp01(material.Opacity) : 1.0f;
        refraction = material != null ? material.RefractionIndex : 1.0f;
        specular = material != null ? Mathf.Clamp01(material.Specular) : 0.0f;
        transmission = material != null ? Mathf.Clamp01(material.Transmission) : 1.0f;
        materialType = light != null ? 3 : (int)material.Type;
        albedoTexture = material != null ? material.AlbedoTexture : null;
        metallicRoughnessTexture = material != null ? material.MetallicRoughnessTexture : null;
        normalTexture = material != null ? material.NormalTexture : null;
        parallaxTexture = material != null ? material.ParallaxTexture : null;
        textureUvScale = material != null ? material.TextureUvScale : Vector2.one;
        parallaxStrength = material != null ? material.ParallaxStrength : 0.0f;
        minimumParallaxStrength = material != null ? Mathf.Min(material.MinimumParallaxStrength, parallaxStrength) : 0.0f;
        interpolateNormals = material != null && material.InterpolateNormals;
    }

    public static RayMaterialSnapshot Create(RayMaterial material, RayLight light) => new(material, light);

    public bool DiffersFrom(PathTracedMesh mesh)
    {
        return mesh.previousColor != color
            || mesh.previousEmission != emission
            || !Mathf.Approximately(mesh.previousSmoothness, smoothness)
            || !Mathf.Approximately(mesh.previousMetallic, metallic)
            || !Mathf.Approximately(mesh.previousOpacity, opacity)
            || !Mathf.Approximately(mesh.previousRefraction, refraction)
            || !Mathf.Approximately(mesh.previousSpecular, specular)
            || !Mathf.Approximately(mesh.previousTransmission, transmission)
            || mesh.previousMaterialType != materialType
            || mesh.previousAlbedoTexture != albedoTexture
            || mesh.previousMetallicRoughnessTexture != metallicRoughnessTexture
            || mesh.previousNormalTexture != normalTexture
            || mesh.previousParallaxTexture != parallaxTexture
            || mesh.previousTextureUvScale != textureUvScale
            || !Mathf.Approximately(mesh.previousParallaxStrength, parallaxStrength)
            || !Mathf.Approximately(mesh.previousMinimumParallaxStrength, minimumParallaxStrength);
    }

    public void StoreIn(ref PathTracedMesh mesh)
    {
        mesh.previousColor = color;
        mesh.previousEmission = emission;
        mesh.previousSmoothness = smoothness;
        mesh.previousMetallic = metallic;
        mesh.previousOpacity = opacity;
        mesh.previousRefraction = refraction;
        mesh.previousSpecular = specular;
        mesh.previousTransmission = transmission;
        mesh.previousMaterialType = materialType;
        mesh.previousAlbedoTexture = albedoTexture;
        mesh.previousMetallicRoughnessTexture = metallicRoughnessTexture;
        mesh.previousNormalTexture = normalTexture;
        mesh.previousParallaxTexture = parallaxTexture;
        mesh.previousTextureUvScale = textureUvScale;
        mesh.previousParallaxStrength = parallaxStrength;
        mesh.previousMinimumParallaxStrength = minimumParallaxStrength;
        mesh.previousInterpolateNormals = interpolateNormals;
    }

    public void ApplyTo(ref Triangle triangle)
    {
        triangle.color = color;
        triangle.emission = emission;
        triangle.smoothness = smoothness;
        triangle.metallic = metallic;
        triangle.opacity = opacity;
        triangle.refraction = refraction;
        triangle.specular = specular;
        triangle.transmission = transmission;
        triangle.materialType = materialType;
        triangle.textureUvScale = textureUvScale;
        triangle.parallaxStrength = parallaxStrength;
        triangle.minimumParallaxStrength = minimumParallaxStrength;
    }

    private static float GetEffectiveMetallic(RayMaterial material)
    {
        return material.Type == RayMaterial.MaterialType.Metal && Mathf.Approximately(material.Metallic, 0.0f)
            ? 1.0f
            : Mathf.Clamp01(material.Metallic);
    }
}
