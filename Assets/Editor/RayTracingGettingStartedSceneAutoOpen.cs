using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class RayTracingGettingStartedSceneAutoOpen
{
    private const string GettingStartedScenePath = "Assets/Scenes/Generated/GettingStarted.unity";
    private const string SceneGeneratorTypeName = "RayTracingSceneGenerator";
    private const string GenerateGettingStartedSceneMethodName = "GenerateGettingStartedScene";
    private const string OpenedForSessionKey = "RayTracingGettingStartedSceneAutoOpen.OpenedForSession";

    static RayTracingGettingStartedSceneAutoOpen()
    {
        if (!Application.isBatchMode && !SessionState.GetBool(OpenedForSessionKey, false))
        {
            SessionState.SetBool(OpenedForSessionKey, true);
            EditorApplication.delayCall += OpenGettingStartedScene;
        }
    }

    private static void OpenGettingStartedScene()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(GettingStartedScenePath) == null)
        {
            MethodInfo generateScene = FindSceneGenerator();
            if (generateScene == null)
            {
                return;
            }

            generateScene.Invoke(null, null);
            AssetDatabase.Refresh();
        }

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(GettingStartedScenePath) != null)
        {
            EditorSceneManager.OpenScene(GettingStartedScenePath);
        }
    }

    private static MethodInfo FindSceneGenerator()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type generatorType = assembly.GetType(SceneGeneratorTypeName);
            MethodInfo generateScene = generatorType?.GetMethod(
                GenerateGettingStartedSceneMethodName,
                BindingFlags.Public | BindingFlags.Static);
            if (generateScene != null)
            {
                return generateScene;
            }
        }

        return null;
    }
}
