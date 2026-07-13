using System.Text;
using UnityEngine;

public class RayTracingBenchmarkOverlay : MonoBehaviour
{
    public GameManager gameManager;
    public bool showOverlay = true;
    public int averageFrameCount = 120;
    public KeyCode toggleKey = KeyCode.F3;

    private readonly StringBuilder _builder = new StringBuilder(512);
    private float _frameTimeSum;
    private int _frameSamples;
    private float _averageFrameMs;
    private GUIStyle _style;

    private void Awake()
    {
        if (gameManager == null)
        {
            gameManager = FindObjectOfType<GameManager>();
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
        _builder.Append("Resolution: ").Append(gameManager.TextureSize.x).Append('x').AppendLine(gameManager.TextureSize.y.ToString());
        _builder.Append("Passes: ").Append(gameManager.numberOfPasses).Append("  Bounces: ").Append(gameManager.numBounces).Append("  Shadow quality: ").AppendLine(gameManager.shadowQuality.ToString());
        _builder.Append("Light sampling: ").Append(gameManager.lightSamplingStrategy)
            .Append("  Samples: ").AppendLine(gameManager.lightSampleCount.ToString());
        _builder.Append("Dynamic quality: ").Append(gameManager.enableDynamicQuality ? "on" : "off")
            .Append("  Target: ").Append(gameManager.dynamicQualityTargetFrameRate).Append(" FPS")
            .Append("  Avg: ").Append(gameManager.DynamicQualityAverageFrameMs.ToString("0.00")).AppendLine(" ms");
        _builder.Append("Accumulation: ").Append(gameManager.enableFrameAccumulation ? "on" : "off")
            .Append("  Frames: ").AppendLine(gameManager.AccumulatedFrameCount.ToString());
        _builder.Append("Caustics: ").Append(gameManager.enableCaustics ? "on" : "off");
        if (gameManager.enableCaustics)
        {
            _builder.Append("  Photons: ").Append(gameManager.CausticGridPhotonCount)
                .Append("  Grid cells: ").Append(gameManager.CausticGridCellCount)
                .Append("  OOB: ").Append(gameManager.CausticGridOutOfBoundsCount);
        }
        _builder.AppendLine();
        _builder.Append("Spheres: ").Append(gameManager.SphereCount).Append("  Lights: ").Append(gameManager.LightCount).Append("  Meshes: ").Append(gameManager.MeshCount).AppendLine();
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

        GUI.Box(new Rect(12, 12, 520, 270), _builder.ToString(), _style);
    }
}
