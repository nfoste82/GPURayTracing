using UnityEngine;

[DisallowMultipleComponent]
public class Water : MonoBehaviour
{
    [Header("Material")]
    public Color32 Color = new Color32(44, 116, 132, 255);

    [Range(0.0f, 1.0f)]
    public float Smoothness = 0.96f;

    [Range(0.0f, 1.0f)]
    public float Opacity = 0.18f;

    [Tooltip("Distance-based water absorption density. Higher values darken and tint long underwater ray segments more strongly.")]
    [Range(0.0f, 2.0f)]
    public float AbsorptionStrength = 0.22f;

    [Range(1.0f, 3.0f)]
    public float RefractionIndex = 1.33f;

    [Header("Waves")]
    [Tooltip("Maximum wave height above and below the transform's Y position.")]
    [Range(0.0f, 2.0f)]
    public float WaveAmplitude = 0.22f;

    [Tooltip("Scales the procedural wave frequency. Higher values create shorter waves.")]
    [Range(0.05f, 4.0f)]
    public float WaveScale = 0.55f;

    [Tooltip("Procedural wave animation speed. Animated water disables frame accumulation to avoid ghosting.")]
    [Range(0.0f, 4.0f)]
    public float WaveSpeed = 0.75f;

    [Range(8, 64)]
    public int MarchSteps = 28;

    [Range(2, 8)]
    public int RefinementSteps = 5;

    public Vector3 TopCenter => transform.position;

    public Vector2 Size
    {
        get
        {
            Vector3 scale = transform.lossyScale;
            return new Vector2(Mathf.Max(0.01f, Mathf.Abs(scale.x)), Mathf.Max(0.01f, Mathf.Abs(scale.z)));
        }
    }

    public float Depth => Mathf.Max(0.01f, Mathf.Abs(transform.lossyScale.y));

    private void Reset()
    {
        transform.localScale = new Vector3(24.0f, 4.0f, 24.0f);
    }

    private void OnEnable()
    {
        var gameManager = GetComponentInParent<GameManager>();
        if (gameManager == null)
        {
            Debug.LogError($"Water '{name}' must be a child of a GameManager.", this);
            enabled = false;
            return;
        }

        if (!gameManager.RegisterWater(this))
        {
            enabled = false;
        }
    }

    private void OnDisable()
    {
        var gameManager = GetComponentInParent<GameManager>();
        if (gameManager != null)
        {
            gameManager.UnregisterWater(this);
        }
    }

    private void OnDrawGizmos()
    {
        Vector2 size = Size;
        float depth = Depth;
        float waveHeight = Mathf.Max(0.02f, WaveAmplitude);
        Vector3 center = TopCenter + Vector3.down * (depth - waveHeight) * 0.5f;
        Vector3 volumeSize = new Vector3(size.x, depth + waveHeight, size.y);

        Gizmos.color = new Color(Color.r / 255.0f, Color.g / 255.0f, Color.b / 255.0f, 0.35f);
        Gizmos.DrawWireCube(center, volumeSize);

        float halfX = size.x * 0.5f;
        float halfZ = size.y * 0.5f;
        float y = TopCenter.y;
        Gizmos.DrawLine(new Vector3(center.x - halfX, y, center.z - halfZ), new Vector3(center.x + halfX, y, center.z - halfZ));
        Gizmos.DrawLine(new Vector3(center.x + halfX, y, center.z - halfZ), new Vector3(center.x + halfX, y, center.z + halfZ));
        Gizmos.DrawLine(new Vector3(center.x + halfX, y, center.z + halfZ), new Vector3(center.x - halfX, y, center.z + halfZ));
        Gizmos.DrawLine(new Vector3(center.x - halfX, y, center.z + halfZ), new Vector3(center.x - halfX, y, center.z - halfZ));
    }
}
