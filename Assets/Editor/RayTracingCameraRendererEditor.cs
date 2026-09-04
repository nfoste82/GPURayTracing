using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(RayTracingCameraRenderer))]
public sealed class RayTracingCameraRendererEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (target is not RayTracingCameraRenderer renderer || renderer == null)
        {
            return;
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(SceneView.lastActiveSceneView == null))
        {
            if (GUILayout.Button("Match Scene View Camera"))
            {
                MatchSceneViewCamera(renderer);
            }
        }
    }

    private static void MatchSceneViewCamera(RayTracingCameraRenderer renderer)
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null || sceneView.camera == null)
        {
            return;
        }

        var sceneCameraTransform = sceneView.camera.transform;
        var transform = renderer.transform;

        Undo.RecordObject(transform, "Match Scene View Camera");
        transform.position = sceneCameraTransform.position;
        transform.rotation = sceneCameraTransform.rotation;

        if (!Application.isPlaying)
        {
            EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
        }
    }
}
