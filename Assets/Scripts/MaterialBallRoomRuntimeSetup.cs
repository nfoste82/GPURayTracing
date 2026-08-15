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
        MaterialBallSceneController.EnsureAreaLights(gameObject);

        GameManager manager = GetComponentInParent<GameManager>();
        if (manager == null)
        {
            yield break;
        }

        foreach (PathTracingObject pathTracingObject in GetComponentsInChildren<PathTracingObject>(true))
        {
            pathTracingObject.RefreshRegistration();
        }
        manager.RebuildBuffers();
    }
}
