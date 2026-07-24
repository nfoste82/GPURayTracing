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

        gameObject.AddComponent<RayTracingObject>();
        gameObject.AddComponent<RayObjectPreview>();

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

        gameObject.AddComponent<RayTracingObject>();
        gameObject.AddComponent<RayObjectPreview>();

        FinishCreate(gameObject, "Create Ray Traced Light");
    }

    [MenuItem("GameObject/Ray Tracing/Fog Volume", false, 3)]
    private static void CreateFogVolume(MenuCommand command)
    {
        var gameObject = CreateBaseObject(command, "Fog Volume");
        gameObject.AddComponent<FogVolume>();
        gameObject.transform.localScale = new Vector3(20.0f, 10.0f, 20.0f);
        FinishCreate(gameObject, "Create Fog Volume");
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
