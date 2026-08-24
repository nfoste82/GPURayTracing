using UnityEditor;
using UnityEditor.SceneManagement;

[InitializeOnLoad]
public static class RayTracingQuickControlsAutoOpen
{
    private const string GalleryOpenedThisSession = "RayTracing.SceneGalleryOpenedThisSession";
    static RayTracingQuickControlsAutoOpen()
    {
        EditorSceneManager.sceneOpened += OnSceneOpened;
        EditorApplication.delayCall += OpenProjectWindows;
    }

    private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode _)
    {
        EditorApplication.delayCall += () => RayTracingQuickControlsWindow.OpenForScene(scene);
    }

    private static void OpenForActiveScene()
    {
        RayTracingQuickControlsWindow.OpenForScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    private static void OpenProjectWindows()
    {
        if (!SessionState.GetBool(GalleryOpenedThisSession, false))
        {
            SessionState.SetBool(GalleryOpenedThisSession, true);
            RayTracingSceneGalleryWindow.Open();
        }
        OpenForActiveScene();
    }
}
