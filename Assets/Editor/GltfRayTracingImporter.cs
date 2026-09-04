using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class GltfRayTracingImporter : AssetPostprocessor
{
    private const string GeneratedPrefabSuffix = ".RayTracing.prefab";
    private const string GeneratedMeshesSuffix = ".RayTracingMeshes.asset";
    private const string GeneratedPrefabLabel = "RayTracingGeneratedGltf";

    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        foreach (string assetPath in importedAssets)
        {
            if (!IsGltfAsset(assetPath))
            {
                continue;
            }

            CreateRayTracingPrefab(assetPath, false);
        }
    }

    public static string GetRayTracingPrefabPath(string gltfAssetPath)
    {
        return gltfAssetPath.Substring(0, gltfAssetPath.LastIndexOf('.')) + GeneratedPrefabSuffix;
    }

    public static void ConfigureRayTracingHierarchy(GameObject root)
    {
        GltfRayTracingSetup.ConfigureHierarchy(root);
    }

    [MenuItem("Assets/Generate glTF Prefab", true)]
    private static bool ValidateGenerateSelectedPrefab()
    {
        return IsGltfAsset(AssetDatabase.GetAssetPath(Selection.activeObject));
    }

    [MenuItem("Assets/Generate glTF Prefab")]
    private static void GenerateSelectedPrefab()
    {
        CreateRayTracingPrefab(AssetDatabase.GetAssetPath(Selection.activeObject), true);
    }

    private static void CreateRayTracingSubmeshObjects(MeshFilter sourceFilter, Material[] materials)
    {
        Mesh sourceMesh = sourceFilter.sharedMesh;
        int submeshCount = Mathf.Min(sourceMesh.subMeshCount, materials.Length);
        for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
        {
            GameObject child = new GameObject($"Ray Tracing {sourceMesh.name} {submeshIndex}");
            child.transform.SetParent(sourceFilter.transform, false);
            var childFilter = child.AddComponent<MeshFilter>();
            childFilter.sharedMesh = CreateSubmesh(sourceMesh, submeshIndex);
            ConfigureRayTracingObject(child, materials[submeshIndex]);
        }
    }

    private static Mesh CreateSubmesh(Mesh sourceMesh, int submeshIndex)
    {
        int[] sourceIndices = sourceMesh.GetTriangles(submeshIndex);
        Vector3[] sourceVertices = sourceMesh.vertices;
        Vector3[] sourceNormals = sourceMesh.normals;
        Vector4[] sourceTangents = sourceMesh.tangents;
        Vector2[] sourceUv = sourceMesh.uv;
        bool copyNormals = sourceNormals.Length == sourceVertices.Length;
        bool copyTangents = sourceTangents.Length == sourceVertices.Length;
        bool copyUv = sourceUv.Length == sourceVertices.Length;
        var vertexMap = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var normals = copyNormals ? new List<Vector3>() : null;
        var tangents = copyTangents ? new List<Vector4>() : null;
        var uv = copyUv ? new List<Vector2>() : null;
        var indices = new int[sourceIndices.Length];

        for (int index = 0; index < sourceIndices.Length; index++)
        {
            int sourceIndex = sourceIndices[index];
            if (!vertexMap.TryGetValue(sourceIndex, out int compactIndex))
            {
                compactIndex = vertices.Count;
                vertexMap.Add(sourceIndex, compactIndex);
                vertices.Add(sourceVertices[sourceIndex]);
                if (copyNormals)
                {
                    normals.Add(sourceNormals[sourceIndex]);
                }
                if (copyTangents)
                {
                    tangents.Add(sourceTangents[sourceIndex]);
                }
                if (copyUv)
                {
                    uv.Add(sourceUv[sourceIndex]);
                }
            }

            indices[index] = compactIndex;
        }

        var mesh = new Mesh
        {
            name = $"{sourceMesh.name} Ray Tracing Submesh {submeshIndex}"
        };
        mesh.SetVertices(vertices);
        if (copyNormals)
        {
            mesh.SetNormals(normals);
        }
        if (copyTangents)
        {
            mesh.SetTangents(tangents);
        }
        if (copyUv)
        {
            mesh.SetUVs(0, uv);
        }
        mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void ConfigureRayTracingObject(GameObject gameObject, Material unityMaterial)
    {
        RayMaterial rayMaterial = gameObject.GetComponent<RayMaterial>();
        if (rayMaterial == null)
        {
            rayMaterial = gameObject.AddComponent<RayMaterial>();
        }

        ApplyUnityMaterial(rayMaterial, unityMaterial);
        if (gameObject.GetComponent<PathTracingObject>() == null)
        {
            gameObject.AddComponent<PathTracingObject>();
        }
    }

    public static void ApplyUnityMaterial(RayMaterial rayMaterial, Material unityMaterial)
    {
        GltfRayTracingSetup.ApplyMaterial(rayMaterial, unityMaterial);
    }

    private static void CreateRayTracingPrefab(string gltfAssetPath, bool showProgress)
    {
        GameObject importedRoot = AssetDatabase.LoadAssetAtPath<GameObject>(gltfAssetPath);
        if (importedRoot == null)
        {
            Debug.LogWarning($"Could not create ray-tracing prefab because glTF import produced no root GameObject: {gltfAssetPath}");
            return;
        }

        string prefabPath = GetRayTracingPrefabPath(gltfAssetPath);
        GameObject instance = PrefabUtility.InstantiatePrefab(importedRoot) as GameObject;
        if (instance == null)
        {
            Debug.LogWarning($"Could not instantiate imported glTF asset: {gltfAssetPath}", importedRoot);
            return;
        }

        try
        {
            ReportProgress(showProgress, "Configuring ray-tracing materials", 0.2f);
            ConfigureRayTracingHierarchy(instance);
            ReportProgress(showProgress, "Persisting generated meshes", 0.55f);
            PersistGeneratedMeshes(instance, gltfAssetPath);
            ReportProgress(showProgress, "Saving prefab", 0.8f);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            ValidatePersistedMeshes(prefab, prefabPath);
            AssetDatabase.SetLabels(prefab, new[] { GeneratedPrefabLabel });
            ReportProgress(showProgress, "Complete", 1.0f);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
            if (showProgress)
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    private static void ReportProgress(bool showProgress, string status, float progress)
    {
        if (showProgress)
        {
            EditorUtility.DisplayProgressBar("Generating glTF Ray-Tracing Prefab", status, progress);
        }
    }

    private static void PersistGeneratedMeshes(GameObject instance, string gltfAssetPath)
    {
        string meshAssetPath = gltfAssetPath.Substring(0, gltfAssetPath.LastIndexOf('.')) + GeneratedMeshesSuffix;
        AssetDatabase.DeleteAsset(meshAssetPath);

        bool createdMeshAsset = false;
        foreach (MeshFilter meshFilter in instance.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = meshFilter.sharedMesh;
            if (mesh == null || AssetDatabase.Contains(mesh))
            {
                continue;
            }

            Mesh persistedMesh = UnityEngine.Object.Instantiate(mesh);
            persistedMesh.name = mesh.name;
            meshFilter.sharedMesh = persistedMesh;
            if (!createdMeshAsset)
            {
                AssetDatabase.CreateAsset(persistedMesh, meshAssetPath);
                createdMeshAsset = true;
            }
            else
            {
                AssetDatabase.AddObjectToAsset(persistedMesh, meshAssetPath);
            }
        }

        AssetDatabase.SaveAssets();
    }

    private static void ValidatePersistedMeshes(GameObject prefab, string prefabPath)
    {
        foreach (MeshFilter meshFilter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter.sharedMesh == null)
            {
                throw new InvalidOperationException(
                    $"Generated ray-tracing prefab contains a MeshFilter without a persisted mesh: {prefabPath}/{meshFilter.name}");
            }
        }
    }

    private static bool IsGltfAsset(string assetPath)
    {
        return assetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
    }

    private static Color GetColor(Material material, Color fallback, params string[] propertyNames)
    {
        if (material == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName))
            {
                return material.GetColor(propertyName);
            }
        }
        return fallback;
    }

    private static float GetFloat(Material material, float fallback, params string[] propertyNames)
    {
        if (material == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName))
            {
                return material.GetFloat(propertyName);
            }
        }
        return fallback;
    }

    private static Texture2D GetTexture(Material material, params string[] propertyNames)
    {
        if (material == null)
        {
            return null;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName) && material.GetTexture(propertyName) is Texture2D texture)
            {
                return texture;
            }
        }
        return null;
    }
}
