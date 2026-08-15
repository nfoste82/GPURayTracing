using UnityEngine;

[DisallowMultipleComponent]
public sealed class RayTracingSceneDebugOptions : MonoBehaviour
{
    [Tooltip("Keeps RayObjectPreview mesh renderers visible in Play mode for Scene/Game view debugging.")]
    public bool keepRasterPreviewRenderersInPlayMode;

    private void OnEnable()
    {
        Apply();
    }

    private void OnValidate()
    {
        Apply();
    }

    private void OnDisable()
    {
        RayObjectPreview.KeepRenderersEnabledInPlayMode = false;
    }

    private void Apply()
    {
        RayObjectPreview.KeepRenderersEnabledInPlayMode = keepRasterPreviewRenderersInPlayMode;
    }
}
