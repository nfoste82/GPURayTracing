using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using GLTFast;
using GltfMaterial = GLTFast.Schema.Material;

public static class GltfRayTracingSetup
{
    public static void ConfigureHierarchy(GameObject root, bool showPreviewsInPlayMode = false, GltfImport gltfImport = null)
    {
        foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!IsTriangleMesh(meshFilter.sharedMesh))
            {
                continue;
            }

            MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
            {
                ConfigureObject(meshFilter.gameObject, null, showPreviewsInPlayMode);
                continue;
            }

            Material[] materials = meshRenderer.sharedMaterials;
            if (materials.Length > 1 && meshFilter.sharedMesh.subMeshCount > 1)
            {
                CreateSubmeshObjects(meshFilter, materials, showPreviewsInPlayMode, gltfImport);
                meshRenderer.enabled = false;
                continue;
            }

            ConfigureObject(meshFilter.gameObject, meshRenderer.sharedMaterial, showPreviewsInPlayMode, FindGltfMaterial(meshRenderer.sharedMaterial, gltfImport), gltfImport);
        }
    }

    public static void ApplyMaterial(RayMaterial rayMaterial, Material unityMaterial, GltfMaterial gltfMaterial = null, GltfImport gltfImport = null)
    {
        rayMaterial.Type = RayMaterial.MaterialType.Diffuse;
        rayMaterial.Color = GetColor(unityMaterial, Color.white, "_BaseColor", "_Color", "baseColorFactor");
        rayMaterial.AlbedoTexture = GetTexture(unityMaterial, "_BaseMap", "_MainTex", "baseColorTexture");
        rayMaterial.AlphaMasked = unityMaterial != null && unityMaterial.IsKeywordEnabled("_ALPHATEST_ON");
        rayMaterial.AlphaCutoff = Mathf.Clamp01(GetFloat(unityMaterial, 0.5f, "alphaCutoff", "_Cutoff", "_AlphaClipThreshold"));
        rayMaterial.MetallicRoughnessTexture = GetTexture(unityMaterial, "_MetallicRoughnessMap", "_MetallicGlossMap", "metallicRoughnessTexture");
        rayMaterial.NormalTexture = GetTexture(unityMaterial, "_BumpMap", "_NormalMap", "normalTexture");
        rayMaterial.Metallic = rayMaterial.MetallicRoughnessTexture != null ? 1.0f : 0.0f;
        rayMaterial.Smoothness = Mathf.Clamp01(1.0f - GetFloat(unityMaterial, 0.5f, "_Roughness", "_RoughnessFactor", "roughnessFactor", "_Smoothness", "_Glossiness"));
        if (rayMaterial.AlbedoTexture != null)
        {
            rayMaterial.Color = Color.white;
        }
        rayMaterial.Opacity = 1.0f;
        rayMaterial.Transmission = 1.0f;
        rayMaterial.RefractionIndex = 1.5f;
        ApplyGltfMaterial(rayMaterial, gltfMaterial, gltfImport);
        GltfMaterial transmissionMaterial = gltfMaterial;
        float transmission = transmissionMaterial?.extensions?.KHR_materials_transmission?.transmissionFactor ?? 0.0f;
        if (transmission > 0.0f)
        {
            rayMaterial.Type = RayMaterial.MaterialType.Glass;
            rayMaterial.Transmission = Mathf.Clamp01(transmission);
            rayMaterial.RefractionIndex = Mathf.Clamp(
                transmissionMaterial.extensions.KHR_materials_ior?.ior ?? 1.5f,
                1.0f,
                4.0f);
        }
        rayMaterial.InterpolateNormals = true;
    }

    private static void CreateSubmeshObjects(MeshFilter sourceFilter, Material[] materials, bool showPreviewsInPlayMode, GltfImport gltfImport)
    {
        Mesh sourceMesh = sourceFilter.sharedMesh;
        int submeshCount = Mathf.Min(sourceMesh.subMeshCount, materials.Length);
        for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
        {
            GameObject child = new GameObject($"Ray Tracing {sourceMesh.name} {submeshIndex}");
            child.transform.SetParent(sourceFilter.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = CreateSubmesh(sourceMesh, submeshIndex);
            ConfigureObject(child, materials[submeshIndex], showPreviewsInPlayMode, FindGltfMaterial(materials[submeshIndex], gltfImport), gltfImport);
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

    private static bool IsTriangleMesh(Mesh mesh)
    {
        if (mesh == null)
        {
            return false;
        }

        for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
        {
            if (mesh.GetTopology(submeshIndex) == MeshTopology.Triangles)
            {
                return true;
            }
        }

        return false;
    }

    private static void ConfigureObject(GameObject gameObject, Material unityMaterial, bool showPreviewsInPlayMode, GltfMaterial gltfMaterial = null, GltfImport gltfImport = null)
    {
        RayMaterial rayMaterial = gameObject.GetComponent<RayMaterial>();
        if (rayMaterial == null)
        {
            rayMaterial = gameObject.AddComponent<RayMaterial>();
        }

        ApplyMaterial(rayMaterial, unityMaterial, gltfMaterial, gltfImport);
        // Scene previews need a renderer, but ray tracing only requires this MeshFilter.
        if (gameObject.GetComponent<MeshRenderer>() == null)
        {
            gameObject.AddComponent<MeshRenderer>();
        }
        if (gameObject.GetComponent<PathTracingObject>() == null)
        {
            gameObject.AddComponent<PathTracingObject>();
        }

        RayObjectPreview preview = gameObject.GetComponent<RayObjectPreview>();
        if (preview != null)
        {
            preview.HideRendererInPlayMode = !showPreviewsInPlayMode;
        }
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

    private static void ApplyGltfMaterial(RayMaterial rayMaterial, GltfMaterial gltfMaterial, GltfImport gltfImport)
    {
        if (gltfMaterial?.pbrMetallicRoughness == null)
        {
            return;
        }

        var pbr = gltfMaterial.pbrMetallicRoughness;
        rayMaterial.Color = pbr.baseColorTexture != null ? Color.white : pbr.BaseColor;
        rayMaterial.Metallic = pbr.metallicRoughnessTexture != null ? 1.0f : 0.0f;
        rayMaterial.Smoothness = Mathf.Clamp01(1.0f - pbr.roughnessFactor);
        rayMaterial.AlbedoTexture = GetGltfTexture(gltfImport, pbr.baseColorTexture?.index ?? -1);
        rayMaterial.MetallicRoughnessTexture = GetGltfTexture(gltfImport, pbr.metallicRoughnessTexture?.index ?? -1);
        rayMaterial.NormalTexture = GetGltfTexture(gltfImport, gltfMaterial.normalTexture?.index ?? -1);
        rayMaterial.NormalStrength = Mathf.Clamp(gltfMaterial.normalTexture?.scale ?? 1.0f, 0.0f, 2.0f);
        rayMaterial.AlphaMasked = gltfMaterial.GetAlphaMode() == GLTFast.Schema.MaterialBase.AlphaMode.Mask;
        rayMaterial.AlphaCutoff = Mathf.Clamp01(gltfMaterial.alphaCutoff);
        ApplyTextureScale(rayMaterial, gltfMaterial.normalTexture);
    }

    private static void ApplyTextureScale(RayMaterial rayMaterial, GLTFast.Schema.TextureInfoBase textureInfo)
    {
        float[] scale = textureInfo?.Extensions?.KHR_texture_transform?.scale;
        if (scale?.Length >= 2)
        {
            rayMaterial.TextureUvScale = new Vector2(scale[0], scale[1]);
        }
        rayMaterial.TextureUvRotation = textureInfo?.Extensions?.KHR_texture_transform?.rotation * Mathf.Rad2Deg ?? 0.0f;
    }

    private static Texture2D GetGltfTexture(GltfImport gltfImport, int textureIndex)
    {
        return gltfImport != null && textureIndex >= 0 ? gltfImport.GetTexture(textureIndex) : null;
    }

    private static GltfMaterial FindGltfMaterial(Material unityMaterial, GltfImport gltfImport)
    {
        if (unityMaterial == null || gltfImport == null)
        {
            return null;
        }

        GltfMaterial[] gltfMaterials = gltfImport.GetSourceRoot()?.materials;
        if (gltfMaterials == null)
        {
            return null;
        }

        for (int index = 0; index < gltfMaterials.Length; index++)
        {
            Material sourceMaterial = gltfImport.GetMaterial(index);
            if (unityMaterial == sourceMaterial
                || (sourceMaterial != null && unityMaterial.name == sourceMaterial.name))
            {
                return gltfMaterials[index];
            }
        }
        return null;
    }
}
