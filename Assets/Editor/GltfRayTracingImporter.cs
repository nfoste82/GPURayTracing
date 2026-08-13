using System;
using UnityEditor;
using UnityEngine;

public class GltfRayTracingImporter : AssetPostprocessor
{
    private const string GeneratedPrefabSuffix = ".RayTracing.prefab";
    private const string GeneratedPrefabLabel = "RayTracingGeneratedGltf";

    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        foreach (string assetPath in importedAssets)
        {
            if (!IsGltfAsset(assetPath))
            {
                continue;
            }

            CreateRayTracingPrefab(assetPath);
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
        var mesh = new Mesh
        {
            name = $"{sourceMesh.name} Ray Tracing Submesh {submeshIndex}",
            indexFormat = sourceMesh.indexFormat
        };
        mesh.vertices = sourceMesh.vertices;
        mesh.normals = sourceMesh.normals;
        mesh.tangents = sourceMesh.tangents;
        mesh.uv = sourceMesh.uv;
        mesh.SetTriangles(sourceMesh.GetTriangles(submeshIndex), 0);
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

    private static void CreateRayTracingPrefab(string gltfAssetPath)
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
            ConfigureRayTracingHierarchy(instance);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            AssetDatabase.SetLabels(prefab, new[] { GeneratedPrefabLabel });
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
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
