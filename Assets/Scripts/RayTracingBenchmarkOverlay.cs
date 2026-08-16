using System.Text;
using UnityEngine;

public class RayTracingBenchmarkOverlay : MonoBehaviour
{
    public const float PanelLeft = 12.0f;
    public const float PanelTop = 12.0f;
    public const float PanelWidth = 580.0f;
    public const float PanelHeight = 330.0f;

    public GameManager gameManager;
    public bool showOverlay = false;
    public int averageFrameCount = 120;
    public KeyCode toggleKey = KeyCode.Z;

    private readonly StringBuilder _builder = new StringBuilder(512);
    private float _frameTimeSum;
    private int _frameSamples;
    private float _averageFrameMs;
    private GUIStyle _style;

    public float AverageFrameMs => _averageFrameMs;

    private void Awake()
    {
        if (gameManager == null)
        {
            gameManager = FindFirstObjectByType<GameManager>();
        }

        if (gameManager != null && gameObject != gameManager.gameObject)
        {
            showOverlay = false;
            enabled = false;
            Destroy(this);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showOverlay = !showOverlay;
        }

        _frameTimeSum += Time.unscaledDeltaTime * 1000.0f;
        _frameSamples++;

        int sampleCount = Mathf.Max(1, averageFrameCount);
        if (_frameSamples >= sampleCount)
        {
            _averageFrameMs = _frameTimeSum / _frameSamples;
            _frameTimeSum = 0.0f;
            _frameSamples = 0;
        }
    }

    private void OnGUI()
    {
        if (!showOverlay || gameManager == null)
        {
            return;
        }

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                padding = new RectOffset(10, 10, 8, 8)
            };
            _style.normal.textColor = Color.white;
        }

        _builder.Length = 0;
        _builder.AppendLine("Ray Tracing Benchmark");
        _builder.Append("Frame avg: ").Append(_averageFrameMs.ToString("0.00")).AppendLine(" ms");
        _builder.Append("Resolution: ").Append(gameManager.TextureSize.x).Append('x').Append(gameManager.TextureSize.y)
            .Append(" internal / ").Append(gameManager.DisplayTextureSize.x).Append('x').AppendLine(gameManager.DisplayTextureSize.y.ToString());
        _builder.Append("Passes: ").Append(gameManager.numberOfPasses).Append("  Bounces: ").Append(gameManager.numBounces).Append("  Shadow quality: ").AppendLine(gameManager.shadowQuality.ToString());
        _builder.Append("Light sampling: ").Append(gameManager.Lighting.LightSamplingStrategy)
            .Append("  Samples: ").AppendLine(gameManager.Lighting.LightSampleCount.ToString());
        _builder.Append("Accumulation: ").Append(gameManager.enableFrameAccumulation ? "on" : "off")
            .Append("  Frames: ").AppendLine(gameManager.AccumulatedFrameCount.ToString());
        _builder.Append("Caustics: ").Append(gameManager.enableCaustics ? "on" : "off");
        if (gameManager.enableCaustics)
        {
            _builder.Append("  Photons: ").Append(gameManager.Caustics.GridPhotonCount)
                .Append("  Target pairs: ").Append(gameManager.Caustics.TargetPairCount)
                .Append("  Grid cells: ").Append(gameManager.Caustics.GridCellCount)
                .Append("  OOB: ").Append(gameManager.Caustics.GridOutOfBoundsCount);
        }
        _builder.AppendLine();
        _builder.Append("Fog: ").Append(gameManager.IsVolumetricFogActive ? "on" : "off");
        if (gameManager.IsVolumetricFogActive)
        {
            Color fogAlbedo = gameManager.EffectiveFogScatteringAlbedo;
            _builder.Append("  Density: ").Append(gameManager.EffectiveFogDensity.ToString("0.000"))
                .Append("  Albedo: ")
                .Append(fogAlbedo.r.ToString("0.00")).Append(',')
                .Append(fogAlbedo.g.ToString("0.00")).Append(',')
                .Append(fogAlbedo.b.ToString("0.00"))
                .Append("  In-scatter: ").Append(gameManager.fogInScatteringIntensity.ToString("0.0"))
                .Append("  Multiple: ").Append(gameManager.enableFogMultipleScattering ? "on" : "off");
        }
        _builder.AppendLine();
        _builder.Append("Water: ").AppendLine(gameManager.HasWaterVolume ? "present" : "none");
        _builder.Append("Spheres: ").Append(gameManager.SphereCount).Append("  Lights: ").Append(gameManager.LightCount).Append("  Meshes: ").Append(gameManager.MeshCount).AppendLine();
        _builder.Append("Light types: ").Append(gameManager.SphereLightCount).Append(" sphere, ")
            .Append(gameManager.MeshLightCount).Append(" mesh (")
            .Append(gameManager.TriangleLightCount).AppendLine(" triangles)");
        _builder.Append("Triangles: ").AppendLine(gameManager.TriangleCount.ToString());
        _builder.Append("TLAS: ").Append(gameManager.IsTopLevelBvhActive ? "on" : "off")
            .Append("  Objects: ").Append(gameManager.TopLevelBvhObjectCount)
            .Append("  Nodes: ").Append(gameManager.TopLevelBvhNodeCount)
            .Append("  Threshold: ").AppendLine(gameManager.topLevelBvhMinObjectCount.ToString());
        _builder.Append("Shadow BVH: ").Append(gameManager.IsShadowBvhActive ? "on" : "off")
            .Append("  Objects: ").Append(gameManager.ShadowBvhObjectCount)
            .Append("  Nodes: ").Append(gameManager.ShadowBvhNodeCount)
            .Append("  Threshold: ").AppendLine(gameManager.shadowBvhMinObjectCount.ToString());
        _builder.Append("Toggle: ").Append(toggleKey);

        GUI.Box(new Rect(PanelLeft, PanelTop, PanelWidth, PanelHeight), _builder.ToString(), _style);
    }
}
