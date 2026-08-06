using UnityEngine;

public class RayLight : MonoBehaviour
{
    public Color32 Color;

    [Min(0.0f)]
    [Tooltip("HDR radiance multiplier. Raise this above one to make the emitter brighter than the skybox.")]
    public float Intensity = 1.0f;
}
