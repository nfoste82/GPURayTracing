using UnityEditor;
using UnityEngine;

public static class RayMeshPrimitiveMenu
{
    [MenuItem("GameObject/Ray Tracing/Cube", false, 10)]
    private static void CreateCube(MenuCommand command)
    {
        CreatePrimitive(command, "Ray Traced Cube", RayMeshPrimitive.PrimitiveType.Cube, new Color32(180, 205, 255, 255));
    }

    [MenuItem("GameObject/Ray Tracing/Pyramid", false, 11)]
    private static void CreatePyramid(MenuCommand command)
    {
        CreatePrimitive(command, "Ray Traced Pyramid", RayMeshPrimitive.PrimitiveType.Pyramid, new Color32(255, 195, 130, 255));
    }

    [MenuItem("GameObject/Ray Tracing/Dodecahedron", false, 12)]
    private static void CreateDodecahedron(MenuCommand command)
    {
        CreatePrimitive(command, "Ray Traced Dodecahedron", RayMeshPrimitive.PrimitiveType.Dodecahedron, new Color32(180, 255, 190, 255));
    }

    private static void CreatePrimitive(MenuCommand command, string name, RayMeshPrimitive.PrimitiveType type, Color32 color)
    {
        var gameObject = new GameObject(name);
        GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);

        var meshFilter = gameObject.AddComponent<MeshFilter>();
        var meshRenderer = gameObject.AddComponent<MeshRenderer>();
        var rayMaterial = gameObject.AddComponent<RayMaterial>();
        gameObject.AddComponent<RayTracingObject>();
        var rayMeshPrimitive = gameObject.AddComponent<RayMeshPrimitive>();

        rayMeshPrimitive.Type = type;
        rayMeshPrimitive.EnsureMesh();

        rayMaterial.Type = RayMaterial.MaterialType.Glass;
        rayMaterial.Color = color;
        rayMaterial.Smoothness = 1.0f;
        rayMaterial.Opacity = 0.5f;
        rayMaterial.RefractionIndex = 1.5f;

        meshRenderer.sharedMaterial = CreatePreviewMaterial(color);

        Undo.RegisterCreatedObjectUndo(gameObject, $"Create {name}");
        Selection.activeGameObject = gameObject;
    }

    private static Material CreatePreviewMaterial(Color32 color)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        var material = new Material(shader)
        {
            name = "Ray Mesh Preview Material",
            color = color
        };
        material.hideFlags = HideFlags.HideAndDontSave;
        return material;
    }
}
