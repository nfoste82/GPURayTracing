using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class RayTracingSceneViewCameraSync
{
    static RayTracingSceneViewCameraSync()
    {
        EditorSceneManager.sceneOpened += OnSceneOpened;
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        EditorApplication.delayCall += () => AlignSceneViewToRayTracingCamera(scene);
    }

    private static void AlignSceneViewToRayTracingCamera(Scene scene)
    {
        if (!scene.IsValid() || SceneView.lastActiveSceneView == null)
        {
            return;
        }

        foreach (GameManager manager in Object.FindObjectsByType<GameManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (manager.gameObject.scene == scene && manager.renderTextureCamera != null)
            {
                SceneView.lastActiveSceneView.AlignViewToObject(manager.renderTextureCamera.transform);
                return;
            }
        }
    }
}
