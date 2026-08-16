using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RayTracingQuickControlsWindow : EditorWindow
{
    private const string WindowTitle = "Ray Tracing Controls";
    private Vector2 _scrollPosition;
    private Editor _gameManagerEditor;
    private GameManager _inspectedManager;

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
        DestroyGameManagerEditor();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange _)
    {
        Repaint();
    }

    private void OnGUI()
    {
        Scene scene = SceneManager.GetActiveScene();
        GameManager manager = FindGameManager(scene);
        if (manager == null)
        {
            EditorGUILayout.HelpBox("The active scene has no GameManager.", MessageType.Info);
            return;
        }

        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        DrawGameManagerControls(manager);

        RayTracingQuickControls profile = FindProfile(scene);
        if (profile != null && profile.Entries.Count > 0)
        {
            EditorGUILayout.Space(10.0f);
            EditorGUILayout.LabelField("Scene Controls", EditorStyles.boldLabel);
            foreach (RayTracingQuickControls.Entry entry in profile.Entries)
            {
                DrawEntry(entry);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawGameManagerControls(GameManager manager)
    {
        if (manager == null)
        {
            DestroyGameManagerEditor();
            return;
        }

        if (_inspectedManager != manager || _gameManagerEditor == null)
        {
            DestroyGameManagerEditor();
            _inspectedManager = manager;
            _gameManagerEditor = Editor.CreateEditor(manager, typeof(GameManagerEditor));
        }

        if (_gameManagerEditor == null || _gameManagerEditor.target == null)
        {
            DestroyGameManagerEditor();
            return;
        }

        EditorGUILayout.LabelField("Ray Tracing Settings", EditorStyles.boldLabel);
        _gameManagerEditor.OnInspectorGUI();
    }

    private void DestroyGameManagerEditor()
    {
        if (_gameManagerEditor != null)
        {
            DestroyImmediate(_gameManagerEditor);
            _gameManagerEditor = null;
        }
        _inspectedManager = null;
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

    private static GameManager FindGameManager(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            GameManager manager = root.GetComponentInChildren<GameManager>(true);
            if (manager != null)
            {
                return manager;
            }
        }
        return null;
    }
}
