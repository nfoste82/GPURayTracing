using UnityEditor;
using UnityEngine;

public static class RaySceneObjectMenu
{
    [MenuItem("GameObject/Ray Tracing/Sphere", false, 1)]
    private static void CreateSphere(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Ray Traced Sphere");
        var collider = gameObject.AddComponent<SphereCollider>();
        collider.radius = 0.5f;

        var material = gameObject.AddComponent<RayMaterial>();
        material.Type = RayMaterial.MaterialType.Metal;
        material.Color = Color.white;
        material.Smoothness = 1.0f;
        material.Opacity = 1.0f;
        material.RefractionIndex = 1.0f;

        gameObject.AddComponent<PathTracingObject>();

        FinishCreate(gameObject, "Create Ray Traced Sphere");
    }

    [MenuItem("GameObject/Ray Tracing/Light Sphere", false, 2)]
    private static void CreateLightSphere(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Ray Traced Light");
        var collider = gameObject.AddComponent<SphereCollider>();
        collider.radius = 0.5f;

        var light = gameObject.AddComponent<RayLight>();
        light.Color = Color.white;

        var unityLight = gameObject.AddComponent<Light>();
        unityLight.type = LightType.Point;
        unityLight.color = light.Color;

        gameObject.AddComponent<PathTracingObject>();

        FinishCreate(gameObject, "Create Ray Traced Light");
    }

    [MenuItem("GameObject/Ray Tracing/Fog Volume", false, 4)]
    private static void CreateFogVolume(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Fog Volume");
        gameObject.AddComponent<FogVolume>();
        gameObject.transform.localScale = new Vector3(20.0f, 10.0f, 20.0f);
        FinishCreate(gameObject, "Create Fog Volume");
    }

    [MenuItem("GameObject/Ray Tracing/Directional Light", false, 3)]
    private static void CreateDirectionalLight(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Ray Traced Directional Light");
        gameObject.transform.rotation = Quaternion.Euler(50.0f, -30.0f, 0.0f);
        gameObject.AddComponent<RayDirectionalLight>();
        FinishCreate(gameObject, "Create Ray Traced Directional Light");
    }

    [MenuItem("GameObject/Ray Tracing/Water Volume", false, 5)]
    private static void CreateWaterVolume(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Water Volume");
        gameObject.AddComponent<Water>();
        FinishCreate(gameObject, "Create Water Volume");
    }

    private static GameObject CreateBaseObject(MenuCommand command, string name)
    {
        var gameObject = new GameObject(name);
        GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
        return gameObject;
    }

    private static void FinishCreate(GameObject gameObject, string undoName)
    {
        Undo.RegisterCreatedObjectUndo(gameObject, undoName);
        Selection.activeGameObject = gameObject;
    }

}
