using UnityEngine;

[RequireComponent(typeof(Camera))]
public class RayTracingCameraRenderer : MonoBehaviour
{
    public GameManager GameManager;

    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (GameManager == null)
        {
            Graphics.Blit(src, dest);
            return;
        }

        GameManager.RenderImage(src, dest);
    }
}
