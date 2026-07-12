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

    [Tooltip("Interpolate imported mesh vertex normals for smooth shading. Intersections and refraction still use the triangle geometry.")]
    public bool InterpolateNormals;

    [Range(0f, 1f)]
    public float Smoothness = 0.5f;

    [Range(0f, 1f)]
    public float Opacity = 1f;

    [Range(1f, 4f)]
    public float RefractionIndex = 1f;
}
