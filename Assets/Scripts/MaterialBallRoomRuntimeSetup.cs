using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MaterialBallRoomRuntimeSetup : MonoBehaviour
{
    private IEnumerator Start()
    {
        // The promoted prefab cannot serialize the procedural area-light meshes that the original
        // remote loader created. Restore them after all scene objects have enabled.
        yield return null;
        PrepareForRendering();

        GameManager manager = GetComponentInParent<GameManager>();
        if (manager == null)
        {
            GameManager[] managers = FindObjectsByType<GameManager>(FindObjectsSortMode.None);
            manager = managers.Length == 1 ? managers[0] : null;
        }

        foreach (PathTracingObject pathTracingObject in GetComponentsInChildren<PathTracingObject>(true))
        {
            pathTracingObject.RefreshRegistration();
        }
        if (manager != null)
        {
            manager.RebuildBuffers();
        }
    }

    public void PrepareForRendering()
    {
        MaterialBallSceneController.EnsureAreaLights(gameObject);
    }
}
