using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(RayMaterial))]
[RequireComponent(typeof(RayTracingObject))]
public class RayMeshPrimitive : MonoBehaviour
{
    public enum PrimitiveType
    {
        Cube,
        Pyramid,
        Dodecahedron
    }

    public PrimitiveType Type = PrimitiveType.Cube;
    public bool HideRasterizedRendererInPlayMode = true;

    private MeshFilter _meshFilter;

    public void EnsureMesh()
    {
        _meshFilter = _meshFilter != null ? _meshFilter : GetComponent<MeshFilter>();
        _meshFilter.sharedMesh = CreateMesh(Type);
    }

    private void Awake()
    {
        EnsureMesh();

        if (HideRasterizedRendererInPlayMode)
        {
            GetComponent<MeshRenderer>().enabled = false;
        }
    }

    private void Reset()
    {
        var material = GetComponent<RayMaterial>();
        material.Type = RayMaterial.MaterialType.Glass;
        material.Color = new Color32(180, 205, 255, 255);
        material.Smoothness = 1.0f;
        material.Opacity = 0.5f;
        material.RefractionIndex = 1.5f;

        EnsureMesh();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            EnsureMesh();
        }
    }

    private static Mesh CreateMesh(PrimitiveType type)
    {
        switch (type)
        {
            case PrimitiveType.Cube:
                return CreateCube();
            case PrimitiveType.Pyramid:
                return CreatePyramid();
            case PrimitiveType.Dodecahedron:
                return CreateDodecahedron();
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static Mesh CreateCube()
    {
        var vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f)
        };

        var triangles = new List<int>();
        AddQuad(triangles, vertices, 0, 3, 2, 1);
        AddQuad(triangles, vertices, 4, 5, 6, 7);
        AddQuad(triangles, vertices, 0, 1, 5, 4);
        AddQuad(triangles, vertices, 3, 7, 6, 2);
        AddQuad(triangles, vertices, 1, 2, 6, 5);
        AddQuad(triangles, vertices, 0, 4, 7, 3);

        return CreateMesh("Ray Mesh Cube", vertices, triangles);
    }

    private static Mesh CreatePyramid()
    {
        var vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(0.0f, 0.5f, 0.0f)
        };

        var triangles = new List<int>();
        AddQuad(triangles, vertices, 0, 1, 2, 3);
        AddTriangle(triangles, vertices, 0, 4, 1);
        AddTriangle(triangles, vertices, 1, 4, 2);
        AddTriangle(triangles, vertices, 2, 4, 3);
        AddTriangle(triangles, vertices, 3, 4, 0);

        return CreateMesh("Ray Mesh Pyramid", vertices, triangles);
    }

    private static Mesh CreateDodecahedron()
    {
        float phi = (1.0f + Mathf.Sqrt(5.0f)) * 0.5f;
        var icosahedronVertices = new[]
        {
            new Vector3(-1.0f, phi, 0.0f),
            new Vector3(1.0f, phi, 0.0f),
            new Vector3(-1.0f, -phi, 0.0f),
            new Vector3(1.0f, -phi, 0.0f),
            new Vector3(0.0f, -1.0f, phi),
            new Vector3(0.0f, 1.0f, phi),
            new Vector3(0.0f, -1.0f, -phi),
            new Vector3(0.0f, 1.0f, -phi),
            new Vector3(phi, 0.0f, -1.0f),
            new Vector3(phi, 0.0f, 1.0f),
            new Vector3(-phi, 0.0f, -1.0f),
            new Vector3(-phi, 0.0f, 1.0f)
        };

        var icosahedronFaces = new[]
        {
            0, 11, 5,
            0, 5, 1,
            0, 1, 7,
            0, 7, 10,
            0, 10, 11,
            1, 5, 9,
            5, 11, 4,
            11, 10, 2,
            10, 7, 6,
            7, 1, 8,
            3, 9, 4,
            3, 4, 2,
            3, 2, 6,
            3, 6, 8,
            3, 8, 9,
            4, 9, 5,
            2, 4, 11,
            6, 2, 10,
            8, 6, 7,
            9, 8, 1
        };

        var vertices = new Vector3[icosahedronFaces.Length / 3];
        var adjacentFaceIndices = new List<int>[icosahedronVertices.Length];
        for (int i = 0; i < adjacentFaceIndices.Length; i++)
        {
            adjacentFaceIndices[i] = new List<int>();
        }

        for (int i = 0; i < icosahedronFaces.Length; i += 3)
        {
            int faceIndex = i / 3;
            int a = icosahedronFaces[i];
            int b = icosahedronFaces[i + 1];
            int c = icosahedronFaces[i + 2];
            vertices[faceIndex] = ((icosahedronVertices[a] + icosahedronVertices[b] + icosahedronVertices[c]) / 3.0f).normalized * 0.75f;
            adjacentFaceIndices[a].Add(faceIndex);
            adjacentFaceIndices[b].Add(faceIndex);
            adjacentFaceIndices[c].Add(faceIndex);
        }

        var triangles = new List<int>();
        for (int i = 0; i < adjacentFaceIndices.Length; i++)
        {
            var face = adjacentFaceIndices[i];
            SortFaceVertices(face, vertices, icosahedronVertices[i].normalized);
            AddPolygon(triangles, vertices, face);
        }

        return CreateMesh("Ray Mesh Dodecahedron", vertices, triangles);
    }

    private static void SortFaceVertices(List<int> face, Vector3[] vertices, Vector3 normal)
    {
        Vector3 tangent = Vector3.Cross(Vector3.up, normal);
        if (tangent.sqrMagnitude < 0.0001f)
        {
            tangent = Vector3.Cross(Vector3.right, normal);
        }

        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        face.Sort((left, right) =>
        {
            float leftAngle = Mathf.Atan2(Vector3.Dot(vertices[left], bitangent), Vector3.Dot(vertices[left], tangent));
            float rightAngle = Mathf.Atan2(Vector3.Dot(vertices[right], bitangent), Vector3.Dot(vertices[right], tangent));
            return leftAngle.CompareTo(rightAngle);
        });
    }

    private static void AddQuad(List<int> triangles, Vector3[] vertices, int a, int b, int c, int d)
    {
        AddTriangle(triangles, vertices, a, b, c);
        AddTriangle(triangles, vertices, a, c, d);
    }

    private static void AddPolygon(List<int> triangles, Vector3[] vertices, List<int> face)
    {
        for (int i = 1; i + 1 < face.Count; i++)
        {
            AddTriangle(triangles, vertices, face[0], face[i], face[i + 1]);
        }
    }

    private static void AddTriangle(List<int> triangles, Vector3[] vertices, int a, int b, int c)
    {
        Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        Vector3 center = (vertices[a] + vertices[b] + vertices[c]) / 3.0f;

        if (Vector3.Dot(normal, center) < 0.0f)
        {
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
            return;
        }

        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    private static Mesh CreateMesh(string meshName, Vector3[] vertices, List<int> triangles)
    {
        var mesh = new Mesh
        {
            name = meshName,
            vertices = vertices,
            triangles = triangles.ToArray()
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
