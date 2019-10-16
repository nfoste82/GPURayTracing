using UnityEngine;

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;

    private RenderTexture _outputTexture;
    private Vector2Int _textureSize;
    
    private void Start()
    {
        _textureSize = new Vector2Int(Screen.width, Screen.height);
        _outputTexture = new RenderTexture(_textureSize.x, _textureSize.y, 24)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        _outputTexture.Create();
    }

    private void Update()
    {
        UpdateTextureFromCompute();
    }

    private void UpdateTextureFromCompute()
    {
        var kernelHandle = shader.FindKernel("CSMain");
        
        shader.SetTexture(kernelHandle, "Result", _outputTexture);
        shader.Dispatch(kernelHandle, _textureSize.x / 8, _textureSize.y / 8, 1);
    }
    
    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        renderTextureCamera.targetTexture = null;
        Graphics.Blit(_outputTexture, null as RenderTexture);
    }
}
