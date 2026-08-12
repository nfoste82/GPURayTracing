using System.IO;
using UnityEditor;
using UnityEngine;

public static class RayMeshAssetGenerator
{
    private const string MeshFolder = "Assets/Meshes";
    private const string TexturedPlanePath = MeshFolder + "/TexturedPlane.asset";

    [MenuItem("Tools/Ray Tracing/Generate Textured Plane Mesh")]
    public static void GenerateTexturedPlaneMesh()
    {
        Mesh plane = GetOrCreateTexturedPlaneMesh();

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(TexturedPlanePath, ImportAssetOptions.ForceUpdate);
        Selection.activeObject = plane;
        Debug.Log($"Generated textured plane mesh at {TexturedPlanePath}.", plane);
    }

    [MenuItem("GameObject/Ray Tracing/Textured Plane", false, 20)]
    private static void CreateTexturedPlane(MenuCommand command)
    {
        var gameObject = new GameObject("Ray Traced Textured Plane");
        GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);

        var meshFilter = gameObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = GetOrCreateTexturedPlaneMesh();
        var meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = CreatePreviewMaterial();

        var meshCollider = gameObject.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = meshFilter.sharedMesh;
        var rayMaterial = gameObject.AddComponent<RayMaterial>();
        rayMaterial.Type = RayMaterial.MaterialType.Diffuse;
        rayMaterial.Color = Color.white;
        rayMaterial.Smoothness = 0.8f;
        gameObject.AddComponent<PathTracingObject>();

        Undo.RegisterCreatedObjectUndo(gameObject, "Create Ray Traced Textured Plane");
        Selection.activeGameObject = gameObject;
    }

    public static Mesh GetOrCreateTexturedPlaneMesh()
    {
        Directory.CreateDirectory(MeshFolder);

        var plane = AssetDatabase.LoadAssetAtPath<Mesh>(TexturedPlanePath);
        if (plane == null)
        {
            plane = new Mesh();
            AssetDatabase.CreateAsset(plane, TexturedPlanePath);
        }

        plane.name = "Textured Plane";
        plane.Clear();
        plane.vertices = new[]
        {
            // Top
            new Vector3(-0.5f, 0.05f, -0.5f),
            new Vector3(0.5f, 0.05f, -0.5f),
            new Vector3(0.5f, 0.05f, 0.5f),
            new Vector3(-0.5f, 0.05f, 0.5f),
            // Bottom
            new Vector3(-0.5f, -0.05f, -0.5f),
            new Vector3(0.5f, -0.05f, -0.5f),
            new Vector3(0.5f, -0.05f, 0.5f),
            new Vector3(-0.5f, -0.05f, 0.5f),
            // Front
            new Vector3(-0.5f, -0.05f, -0.5f),
            new Vector3(0.5f, -0.05f, -0.5f),
            new Vector3(0.5f, 0.05f, -0.5f),
            new Vector3(-0.5f, 0.05f, -0.5f),
            // Back
            new Vector3(-0.5f, -0.05f, 0.5f),
            new Vector3(-0.5f, 0.05f, 0.5f),
            new Vector3(0.5f, 0.05f, 0.5f),
            new Vector3(0.5f, -0.05f, 0.5f),
            // Left
            new Vector3(-0.5f, -0.05f, -0.5f),
            new Vector3(-0.5f, 0.05f, -0.5f),
            new Vector3(-0.5f, 0.05f, 0.5f),
            new Vector3(-0.5f, -0.05f, 0.5f),
            // Right
            new Vector3(0.5f, -0.05f, -0.5f),
            new Vector3(0.5f, -0.05f, 0.5f),
            new Vector3(0.5f, 0.05f, 0.5f),
            new Vector3(0.5f, 0.05f, -0.5f)
        };
        plane.uv = new[]
        {
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(6.0f, 0.0f),
            new Vector2(6.0f, 6.0f),
            new Vector2(0.0f, 6.0f)
        };
        plane.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            8, 11, 10, 8, 10, 9,
            12, 15, 14, 12, 14, 13,
            16, 19, 18, 16, 18, 17,
            20, 23, 22, 20, 22, 21
        };
        plane.RecalculateNormals();
        plane.RecalculateBounds();
        EditorUtility.SetDirty(plane);
        return plane;
    }

    private static Material CreatePreviewMaterial()
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        return new Material(shader)
        {
            name = "Ray Textured Plane Preview Material",
            color = Color.white
        };
    }
}
