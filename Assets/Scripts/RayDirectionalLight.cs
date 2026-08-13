using UnityEngine;

/// <summary>
/// Analytic sun/sky light. The transform's forward axis is the direction light travels.
/// </summary>
public class RayDirectionalLight : MonoBehaviour
{
    public Color32 Color = new Color32(255, 255, 255, 255);

    [Min(0.0f)]
    [Tooltip("HDR radiance multiplier. Unlike a point light, this does not attenuate with distance.")]
    public float Intensity = 1.0f;

    [Range(0.0f, 10.0f)]
    [Tooltip("Angular radius in degrees. Zero produces hard shadows; larger values sample a sun cone for soft shadows.")]
    public float AngularRadius = 0.27f;

    private void OnEnable()
    {
        if (Application.isPlaying)
        {
            var manager = GetComponentInParent<GameManager>();
            if (manager != null)
            {
                manager.RegisterDirectionalLight(this);
            }
        }
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
        {
            var manager = GetComponentInParent<GameManager>();
            if (manager != null)
            {
                manager.UnregisterDirectionalLight(this);
            }
        }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color;
        Gizmos.DrawRay(transform.position, transform.forward * 2.0f);
    }
}
