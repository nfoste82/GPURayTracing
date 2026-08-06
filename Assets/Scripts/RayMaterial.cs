using UnityEngine;

public class RayMaterial : MonoBehaviour
{
    public enum MaterialType
    {
        Diffuse = 0,
        Metal = 1,
        Glass = 2
    }

    public MaterialType Type = MaterialType.Metal;

    public Color32 Color;

    [Tooltip("Optional albedo texture for mesh objects. Sphere materials still use Color only.")]
    public Texture2D AlbedoTexture;

    [Range(0f, 1f)]
    [Tooltip("Continuous mesh metallic response. Existing Metal materials remain fully metallic when this is left at zero.")]
    public float Metallic;

    [Tooltip("Optional mesh data texture using glTF channels: green is roughness and blue is metallic. Values multiply the scalar controls.")]
    public Texture2D MetallicRoughnessTexture;

    [Tooltip("Optional tangent-space mesh normal texture. Imported mesh tangents are used when available.")]
    public Texture2D NormalTexture;

    [Tooltip("Interpolate imported mesh vertex normals for smooth shading, refraction, and caustic photon optics. Intersections still use the triangle geometry.")]
    public bool InterpolateNormals;

    [Range(0f, 1f)]
    [Tooltip("Surface smoothness. For glass, lower values broaden both reflections and transmitted refraction for a frosted appearance.")]
    public float Smoothness = 0.5f;

    [Range(0f, 1f)]
    [Tooltip("Glass absorption density and transparent-shadow strength. Dielectric reflection remains controlled by IOR and viewing angle.")]
    public float Opacity = 1f;

    [Range(0f, 1f)]
    [Tooltip("Minimum glass reflection chance before IOR-based Fresnel raises it toward one at grazing angles.")]
    public float Specular;

    [Range(0f, 1f)]
    [Tooltip("Glass transmission chance after Fresnel reflection. One preserves fully refractive glass.")]
    public float Transmission = 1f;

    [Range(1f, 4f)]
    public float RefractionIndex = 1f;
}
