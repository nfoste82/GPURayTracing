using UnityEngine;

[DisallowMultipleComponent]
public class FogVolume : MonoBehaviour
{
    [Tooltip("Extinction per world unit. Higher values make the volume denser and shorten the average scattering distance.")]
    [Range(0.0f, 2.0f)]
    public float Density = 0.12f;

    [Tooltip("Fraction and color of extinguished light that is scattered rather than absorbed.")]
    public Color ScatteringAlbedo = new Color(0.92f, 0.92f, 0.92f, 1.0f);

    public Vector3 Center => transform.position;

    public Vector3 Size
    {
        get
        {
            Vector3 scale = transform.lossyScale;
            return new Vector3(
                Mathf.Max(0.01f, Mathf.Abs(scale.x)),
                Mathf.Max(0.01f, Mathf.Abs(scale.y)),
                Mathf.Max(0.01f, Mathf.Abs(scale.z)));
        }
    }

    private void Reset()
    {
        transform.localScale = new Vector3(20.0f, 10.0f, 20.0f);
    }

    private void OnEnable()
    {
        var gameManager = GetComponentInParent<GameManager>();
        if (gameManager == null)
        {
            Debug.LogError($"FogVolume '{name}' must be a child of a GameManager.", this);
            enabled = false;
            return;
        }

        if (!gameManager.RegisterFogVolume(this))
        {
            enabled = false;
        }
    }

    private void OnDisable()
    {
        var gameManager = GetComponentInParent<GameManager>();
        if (gameManager != null)
        {
            gameManager.UnregisterFogVolume(this);
        }
    }

    private void OnDrawGizmos()
    {
        Color color = ScatteringAlbedo;
        color.a = 0.35f;
        Gizmos.color = color;
        Gizmos.DrawWireCube(Center, Size);
    }
}
