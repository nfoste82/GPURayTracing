using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GameViewGizmoPlayModeState
{
    private const string GameViewTypeName = "UnityEditor.GameView,UnityEditor";
    private const string AnnotationUtilityTypeName = "UnityEditor.AnnotationUtility,UnityEditor";
    private const string HadStoredStateKey = "GPURayTracing.GameViewGizmos.HadStoredState";
    private const string GizmosWereEnabledKey = "GPURayTracing.GameViewGizmos.WereEnabled";
    private const string PlayModeGizmosDefaultedOffKey = "GPURayTracing.GameViewGizmos.DefaultedOff";

    private static readonly string[] GizmoMemberNames =
    {
        "showGizmos",
        "m_Gizmos",
        "m_ShowGizmos",
        "s_ShowGizmos"
    };

    static GameViewGizmoPlayModeState()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            bool gizmosWereEnabled = TryGetGameViewGizmos(out bool enabled) && enabled;
            SessionState.SetBool(HadStoredStateKey, true);
            SessionState.SetBool(GizmosWereEnabledKey, gizmosWereEnabled);
        }
        else if (state == PlayModeStateChange.EnteredPlayMode)
        {
            SetGameViewGizmos(false);
            EditorApplication.delayCall += DisableGameViewGizmosAfterPlayModeStarts;
            SessionState.SetBool(PlayModeGizmosDefaultedOffKey, true);
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            if (SessionState.GetBool(HadStoredStateKey, false)
                && SessionState.GetBool(GizmosWereEnabledKey, false)
                && SessionState.GetBool(PlayModeGizmosDefaultedOffKey, false)
                && TryGetGameViewGizmos(out bool enabled)
                && !enabled)
            {
                SetGameViewGizmos(true);
            }

            SessionState.SetBool(HadStoredStateKey, false);
            SessionState.SetBool(GizmosWereEnabledKey, false);
            SessionState.SetBool(PlayModeGizmosDefaultedOffKey, false);
        }
    }

    private static void DisableGameViewGizmosAfterPlayModeStarts()
    {
        if (EditorApplication.isPlaying)
        {
            SetGameViewGizmos(false);
        }
    }

    private static bool TryGetGameViewGizmos(out bool enabled)
    {
        enabled = false;

        foreach (var gameView in GetGameViews())
        {
            if (TryGetBoolMember(gameView.GetType(), gameView, out enabled))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetGameViewGizmos(bool enabled)
    {
        bool changed = TrySetAnnotationUtilityGizmos(enabled);

        foreach (var gameView in GetGameViews())
        {
            changed |= TrySetBoolMember(gameView.GetType(), gameView, enabled);

            if (gameView is EditorWindow window)
            {
                window.Repaint();
            }
        }

        if (changed)
        {
            SceneView.RepaintAll();
            EditorApplication.RepaintHierarchyWindow();
        }
    }

    private static UnityEngine.Object[] GetGameViews()
    {
        var gameViewType = Type.GetType(GameViewTypeName);
        return gameViewType == null ? Array.Empty<UnityEngine.Object>() : Resources.FindObjectsOfTypeAll(gameViewType);
    }

    private static bool TrySetAnnotationUtilityGizmos(bool enabled)
    {
        var annotationUtilityType = Type.GetType(AnnotationUtilityTypeName);
        return annotationUtilityType != null && TrySetBoolMember(annotationUtilityType, null, enabled);
    }

    private static bool TryGetBoolMember(Type type, object instance, out bool value)
    {
        value = false;

        foreach (var name in GizmoMemberNames)
        {
            if (TryFindProperty(type, name, out var property) && property.PropertyType == typeof(bool) && property.CanRead)
            {
                value = (bool)property.GetValue(property.GetMethod.IsStatic ? null : instance);
                return true;
            }

            if (TryFindField(type, name, out var field) && field.FieldType == typeof(bool))
            {
                value = (bool)field.GetValue(field.IsStatic ? null : instance);
                return true;
            }
        }

        return false;
    }

    private static bool TrySetBoolMember(Type type, object instance, bool value)
    {
        foreach (var name in GizmoMemberNames)
        {
            if (TryFindProperty(type, name, out var property) && property.PropertyType == typeof(bool) && property.CanWrite)
            {
                property.SetValue(property.SetMethod.IsStatic ? null : instance, value);
                return true;
            }

            if (TryFindField(type, name, out var field) && field.FieldType == typeof(bool) && !field.IsInitOnly)
            {
                field.SetValue(field.IsStatic ? null : instance, value);
                return true;
            }
        }

        return false;
    }

    private static bool TryFindProperty(Type type, string name, out PropertyInfo property)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        for (var currentType = type; currentType != null; currentType = currentType.BaseType)
        {
            property = currentType.GetProperty(name, flags);
            if (property != null)
            {
                return true;
            }
        }

        property = null;
        return false;
    }

    private static bool TryFindField(Type type, string name, out FieldInfo field)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        for (var currentType = type; currentType != null; currentType = currentType.BaseType)
        {
            field = currentType.GetField(name, flags);
            if (field != null)
            {
                return true;
            }
        }

        field = null;
        return false;
    }
}
