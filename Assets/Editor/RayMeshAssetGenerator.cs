using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class RayMeshAssetGenerator
{
    private const string MeshFolder = "Assets/Meshes";
    private const string TexturedPlanePath = MeshFolder + "/TexturedPlane.asset";
    private const string StanfordBunnySourcePath = "Assets/Models/Bunny/stanford-bunny-69451.obj";
    private const string StanfordBunnyMeshPath = "Assets/Models/Bunny/stanford-bunny-69451.asset";

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

    public static Mesh GetOrCreateStanfordBunnyMesh()
    {
        Mesh bunny = AssetDatabase.LoadAssetAtPath<Mesh>(StanfordBunnyMeshPath);
        if (bunny != null)
        {
            return bunny;
        }

        string sourcePath = Path.GetFullPath(StanfordBunnySourcePath);
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var vertices = new List<Vector3>(35947);
        var triangles = new List<int>(69451 * 3);
        foreach (string line in File.ReadLines(sourcePath))
        {
            string[] values = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 4 && values[0] == "v")
            {
                vertices.Add(new Vector3(
                    float.Parse(values[1], CultureInfo.InvariantCulture) * 1000.0f,
                    float.Parse(values[2], CultureInfo.InvariantCulture) * 1000.0f,
                    float.Parse(values[3], CultureInfo.InvariantCulture) * 1000.0f));
            }
            else if (values.Length == 4 && values[0] == "f")
            {
                triangles.Add(int.Parse(values[1], CultureInfo.InvariantCulture) - 1);
                triangles.Add(int.Parse(values[2], CultureInfo.InvariantCulture) - 1);
                triangles.Add(int.Parse(values[3], CultureInfo.InvariantCulture) - 1);
            }
        }

        if (vertices.Count != 35947 || triangles.Count != 69451 * 3)
        {
            Debug.LogError($"Stanford bunny source has unexpected topology: {vertices.Count} vertices, {triangles.Count / 3} triangles.");
            return null;
        }

        bunny = new Mesh
        {
            name = "Stanford Bunny 69451",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        bunny.SetVertices(vertices);
        bunny.SetTriangles(triangles, 0, true);
        bunny.SetNormals(CalculateAreaWeightedNormals(vertices, triangles));
        bunny.RecalculateBounds();
        AssetDatabase.CreateAsset(bunny, StanfordBunnyMeshPath);
        return bunny;
    }

    private static List<Vector3> CalculateAreaWeightedNormals(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles)
    {
        var normals = new Vector3[vertices.Count];
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex += 3)
        {
            int index0 = triangles[triangleIndex];
            int index1 = triangles[triangleIndex + 1];
            int index2 = triangles[triangleIndex + 2];
            Vector3 faceNormal = Vector3.Cross(vertices[index1] - vertices[index0], vertices[index2] - vertices[index0]);
            normals[index0] += faceNormal;
            normals[index1] += faceNormal;
            normals[index2] += faceNormal;
        }

        for (int vertexIndex = 0; vertexIndex < normals.Length; vertexIndex++)
        {
            normals[vertexIndex] = normals[vertexIndex].sqrMagnitude > 1e-12f
                ? normals[vertexIndex].normalized
                : Vector3.up;
        }
        return new List<Vector3>(normals);
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
