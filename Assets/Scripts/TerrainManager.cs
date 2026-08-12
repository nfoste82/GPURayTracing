using UnityEngine;

/// Owns terrain registration and the GPU resources used to render the active terrain.
[DisallowMultipleComponent]
public sealed class TerrainManager : MonoBehaviour
{
    public event System.Action TerrainChanged;
    private RayTracingTerrain _terrain;
    private ComputeBuffer _terrainCellBuffer;
    private ComputeBuffer _terrainHeightBuffer;
    private Texture2D _terrainAlphamapTexture;

    public RayTracingTerrain Terrain => _terrain;

    public bool RegisterTerrain(RayTracingTerrain terrain)
    {
        if (_terrain != null && _terrain != terrain)
        {
            Debug.LogError($"Only one active RayTracingTerrain is supported by TerrainManager '{name}'.", terrain);
            return false;
        }

        _terrain = terrain;
        RebuildTerrainResources();
        TerrainChanged?.Invoke();
        return true;
    }

    public void UnregisterTerrain(RayTracingTerrain terrain)
    {
        if (_terrain != terrain)
        {
            return;
        }

        _terrain = null;
        ReleaseTerrainResources();
        TerrainChanged?.Invoke();
    }

    public void SetShaderParameters(ComputeShader shader, int kernelHandle)
    {
        TerrainData data = _terrain != null ? _terrain.Data : null;
        bool enabled = data != null && _terrainCellBuffer != null && _terrainHeightBuffer != null;
        if (!enabled)
        {
            shader.DisableKeyword("TERRAIN_ENABLED");
            return;
        }

        shader.EnableKeyword("TERRAIN_ENABLED");
        Vector3 position = _terrain.Terrain.transform.position;
        Vector3 size = data.size;
        TerrainLayer[] layers = data.terrainLayers;
        shader.SetTexture(kernelHandle, "_TerrainAlphamap", _terrainAlphamapTexture != null ? _terrainAlphamapTexture : Texture2D.blackTexture);
        shader.SetTexture(kernelHandle, "_TerrainLayer0", GetTerrainLayerTexture(layers, 0));
        shader.SetTexture(kernelHandle, "_TerrainLayer1", GetTerrainLayerTexture(layers, 1));
        shader.SetTexture(kernelHandle, "_TerrainLayer2", GetTerrainLayerTexture(layers, 2));
        shader.SetTexture(kernelHandle, "_TerrainLayer3", GetTerrainLayerTexture(layers, 3));
        shader.SetTexture(kernelHandle, "_TerrainNormal0", GetTerrainLayerNormalTexture(layers, 0));
        shader.SetTexture(kernelHandle, "_TerrainNormal1", GetTerrainLayerNormalTexture(layers, 1));
        shader.SetTexture(kernelHandle, "_TerrainNormal2", GetTerrainLayerNormalTexture(layers, 2));
        shader.SetTexture(kernelHandle, "_TerrainNormal3", GetTerrainLayerNormalTexture(layers, 3));
        shader.SetTexture(kernelHandle, "_TerrainMask0", GetTerrainLayerMaskTexture(layers, 0));
        shader.SetTexture(kernelHandle, "_TerrainMask1", GetTerrainLayerMaskTexture(layers, 1));
        shader.SetTexture(kernelHandle, "_TerrainMask2", GetTerrainLayerMaskTexture(layers, 2));
        shader.SetTexture(kernelHandle, "_TerrainMask3", GetTerrainLayerMaskTexture(layers, 3));
        shader.SetVector("_TerrainLayer0Tiling", GetTerrainLayerTiling(layers, 0, size));
        shader.SetVector("_TerrainLayer1Tiling", GetTerrainLayerTiling(layers, 1, size));
        shader.SetVector("_TerrainLayer2Tiling", GetTerrainLayerTiling(layers, 2, size));
        shader.SetVector("_TerrainLayer3Tiling", GetTerrainLayerTiling(layers, 3, size));
        shader.SetVector("_TerrainLayerProperties0", GetTerrainLayerProperties(layers, 0));
        shader.SetVector("_TerrainLayerProperties1", GetTerrainLayerProperties(layers, 1));
        shader.SetVector("_TerrainLayerProperties2", GetTerrainLayerProperties(layers, 2));
        shader.SetVector("_TerrainLayerProperties3", GetTerrainLayerProperties(layers, 3));
        shader.SetVector("_TerrainPosition", new Vector4(position.x, position.y, position.z, 0.0f));
        shader.SetVector("_TerrainSize", new Vector4(size.x, size.y, size.z, 0.0f));
        shader.SetInt("_TerrainCellResolution", Mathf.Clamp(_terrain.AccelerationResolution, 4, 64));
        shader.SetInt("_TerrainHeightmapResolution", _terrain.HeightmapResolution);
        shader.SetInt("_TerrainMarchSteps", Mathf.Clamp(_terrain.MarchSteps, 4, 16));
        shader.SetInt("_TerrainRefinementSteps", Mathf.Clamp(_terrain.RefinementSteps, 2, 8));
        SetComputeBuffer(shader, "_TerrainCells", _terrainCellBuffer, kernelHandle);
        SetComputeBuffer(shader, "_TerrainHeights", _terrainHeightBuffer, kernelHandle);
    }

    private void OnDestroy() => ReleaseTerrainResources();

    private void RebuildTerrainResources()
    {
        ReleaseTerrainResources();
        TerrainData data = _terrain != null ? _terrain.Data : null;
        if (data == null)
        {
            return;
        }

        RayTracingTerrain.TerrainCell[] cells = _terrain.BuildCells();
        _terrainCellBuffer = new ComputeBuffer(Mathf.Max(1, cells.Length), sizeof(float) * 2);
        _terrainCellBuffer.SetData(cells);
        float[] heights = _terrain.BuildHeights();
        _terrainHeightBuffer = new ComputeBuffer(Mathf.Max(1, heights.Length), sizeof(float));
        _terrainHeightBuffer.SetData(heights.Length > 0 ? heights : new[] { 0.0f });

        int alphaWidth = Mathf.Max(1, data.alphamapWidth);
        int alphaHeight = Mathf.Max(1, data.alphamapHeight);
        _terrainAlphamapTexture = new Texture2D(alphaWidth, alphaHeight, TextureFormat.RGBA32, false, true)
        {
            name = "Ray Tracing Terrain Alphamap",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] alphamap = _terrain.BuildAlphamap();
        _terrainAlphamapTexture.SetPixels(alphamap.Length > 0 ? alphamap : new[] { Color.black });
        _terrainAlphamapTexture.Apply(false, true);
        if (GetComponent<GameManager>() != null && GetComponent<GameManager>().profileStartup)
        {
            Vector4 averageWeights = _terrain.GetAverageLayerWeights();
            Debug.Log($"Terrain material upload: {data.terrainLayers.Length} layers, {alphaWidth}x{alphaHeight} alphamap, " +
                $"average weights ({averageWeights.x:F2}, {averageWeights.y:F2}, {averageWeights.z:F2}, {averageWeights.w:F2}).", _terrain);
        }
    }

    private void ReleaseTerrainResources()
    {
        _terrainCellBuffer?.Release();
        _terrainCellBuffer = null;
        _terrainHeightBuffer?.Release();
        _terrainHeightBuffer = null;
        if (_terrainAlphamapTexture != null)
        {
            Destroy(_terrainAlphamapTexture);
            _terrainAlphamapTexture = null;
        }
    }

    private static void SetComputeBuffer(ComputeShader shader, string name, ComputeBuffer buffer, int kernelHandle)
    {
        shader.SetBuffer(kernelHandle, name, buffer);
    }

    private static Texture2D GetTerrainLayerTexture(TerrainLayer[] layers, int index) =>
        layers != null && index < layers.Length && layers[index] != null && layers[index].diffuseTexture != null ? layers[index].diffuseTexture : Texture2D.whiteTexture;

    private static Texture2D GetTerrainLayerNormalTexture(TerrainLayer[] layers, int index) =>
        layers != null && index < layers.Length && layers[index] != null && layers[index].normalMapTexture != null ? layers[index].normalMapTexture : Texture2D.normalTexture;

    private static Texture2D GetTerrainLayerMaskTexture(TerrainLayer[] layers, int index) =>
        layers != null && index < layers.Length && layers[index] != null && layers[index].maskMapTexture != null ? layers[index].maskMapTexture : Texture2D.whiteTexture;

    private static Vector4 GetTerrainLayerProperties(TerrainLayer[] layers, int index)
    {
        if (layers == null || index < 0 || index >= layers.Length || layers[index] == null)
        {
            return new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        }
        TerrainLayer layer = layers[index];
        return new Vector4(layer.metallic, layer.smoothness, layer.normalScale, 0.04f);
    }

    private static Vector2 GetTerrainLayerTiling(TerrainLayer[] layers, int index, Vector3 terrainSize)
    {
        if (layers == null || index >= layers.Length || layers[index] == null)
        {
            return Vector2.one;
        }
        Vector2 tileSize = layers[index].tileSize;
        return new Vector2(terrainSize.x / Mathf.Max(0.001f, tileSize.x), terrainSize.z / Mathf.Max(0.001f, tileSize.y));
    }
}
