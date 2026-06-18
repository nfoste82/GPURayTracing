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

    [MenuItem("GameObject/Ray Tracing/Ground Preview Plane", false, 3)]
    private static void CreateGroundPreviewPlane(MenuCommand command)
    {
        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        gameObject.name = "Ray Traced Ground Preview";
        GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
        gameObject.transform.position = new Vector3(0.0f, -0.01f, 0.0f);
        gameObject.transform.localScale = new Vector3(40.0f, 0.02f, 40.0f);

        var renderer = gameObject.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = CreatePreviewMaterial(new Color(0.8f, 0.8f, 0.8f));

        FinishCreate(gameObject, "Create Ray Traced Ground Preview");
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

    private static Material CreatePreviewMaterial(Color color)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        var material = new Material(shader)
        {
            name = "Ray Preview Material",
            color = color
        };
        return material;
    }
}
