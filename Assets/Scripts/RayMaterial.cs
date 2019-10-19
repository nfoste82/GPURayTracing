using UnityEngine;

public class RayMaterial : MonoBehaviour
{
    public Color32 Color;

    [Range(0f, 1f)]
    public float Smoothness = 0.5f;

    [Range(0f, 1f)]
    public float Opacity = 1f;

    [Range(1f, 4f)]
    public float RefractionIndex = 1f;
}