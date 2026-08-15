using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RayTracingQuickControlsWindow : EditorWindow
{
    private const string WindowTitle = "Ray Tracing Controls";
    private Vector2 _scrollPosition;

    [MenuItem("Window/Ray Tracing/Quick Controls")]
    public static void Open()
    {
        GetWindow<RayTracingQuickControlsWindow>(typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow"));
    }

    public static void OpenForScene(Scene scene)
    {
        if (scene.IsValid() && scene.isLoaded && FindProfile(scene) != null)
        {
            Open();
        }
    }

    private void OnEnable()
    {
        titleContent = new GUIContent(WindowTitle);
        EditorApplication.hierarchyChanged += Repaint;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private void OnDisable()
    {
        EditorApplication.hierarchyChanged -= Repaint;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private void OnPlayModeStateChanged(PlayModeStateChange _)
    {
        Repaint();
    }

    private void OnGUI()
    {
        RayTracingQuickControls profile = FindProfile(SceneManager.GetActiveScene());
        if (profile == null)
        {
            EditorGUILayout.HelpBox("The active scene has no Ray Tracing Quick Controls profile.", MessageType.Info);
            return;
        }

        var entries = profile.Entries;
        if (entries.Count == 0)
        {
            EditorGUILayout.HelpBox("Controls are populated when this scene's content is available.", MessageType.Info);
            return;
        }

        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        foreach (RayTracingQuickControls.Entry entry in entries)
        {
            DrawEntry(entry);
        }
        EditorGUILayout.EndScrollView();
    }

    private static void DrawEntry(RayTracingQuickControls.Entry entry)
    {
        if (entry.Target == null)
        {
            return;
        }

        EditorGUILayout.Space(4.0f);
        EditorGUILayout.LabelField(entry.Label, EditorStyles.boldLabel);
        if (entry.Target is RayMaterial material)
        {
            RayMaterialEditor.DrawControls(material);
            return;
        }
        var serializedTarget = new SerializedObject(entry.Target);
        serializedTarget.Update();
        foreach (string propertyPath in entry.PropertyPaths)
        {
            SerializedProperty property = serializedTarget.FindProperty(propertyPath);
            if (property != null)
            {
                EditorGUILayout.PropertyField(property, true);
            }
        }
        if (serializedTarget.ApplyModifiedProperties())
        {
            EditorUtility.SetDirty(entry.Target);
            if (!Application.isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(entry.Target.gameObject.scene);
            }
        }
    }

    private static RayTracingQuickControls FindProfile(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            RayTracingQuickControls profile = root.GetComponentInChildren<RayTracingQuickControls>(true);
            if (profile != null)
            {
                return profile;
            }
        }
        return null;
    }
}
