using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace GPURayTracing.Tests
{
    public class GltfRayTracingImporterTests
    {
        [Test]
        public void RayTracingPrefabPath_ReplacesGltfExtension()
        {
            Type importerType = GetImporterType();
            MethodInfo getPrefabPath = importerType.GetMethod("GetRayTracingPrefabPath");
            Assert.That(
                getPrefabPath.Invoke(null, new object[] { "Assets/Models/FlightHelmet.glb" }),
                Is.EqualTo("Assets/Models/FlightHelmet.RayTracing.prefab"));
        }

        [Test]
        public void Importer_PersistsTransientMeshesBeforeSavingPrefab()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/GltfRayTracingImporter.cs");

            Assert.That(source.IndexOf("PersistGeneratedMeshes(instance, gltfAssetPath)", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("PrefabUtility.SaveAsPrefabAsset(instance, prefabPath)", StringComparison.Ordinal)));
            Assert.That(source, Does.Contain("AssetDatabase.CreateAsset(persistedMesh, meshAssetPath)"));
            Assert.That(source, Does.Contain("ValidatePersistedMeshes(prefab, prefabPath)"));
            Assert.That(source, Does.Contain("EditorUtility.DisplayProgressBar"));
            Assert.That(source, Does.Not.Contain("new GltfImport("));
        }

        [Test]
        public void ConfigureHierarchy_AddsRayTracingComponentsAndCopiesMaterial()
        {
            var root = new GameObject("glTF Root");
            var meshObject = new GameObject("Mesh");
            meshObject.transform.SetParent(root.transform);
            var meshFilter = meshObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateTriangleMesh();
            var meshRenderer = meshObject.AddComponent<MeshRenderer>();
            var unityMaterial = new Material(Shader.Find("Standard"))
            {
                color = new Color(0.2f, 0.4f, 0.6f)
            };
            meshRenderer.sharedMaterial = unityMaterial;

            try
            {
                Type.GetType("GltfRayTracingSetup, Assembly-CSharp")
                    .GetMethod("ConfigureHierarchy")
                    .Invoke(null, new object[] { root, false, null });

                Component rayMaterial = meshObject.GetComponent("RayMaterial");
                Assert.That(rayMaterial, Is.Not.Null);
                Assert.That(meshObject.GetComponent("PathTracingObject"), Is.Not.Null);
                Type rayMaterialType = rayMaterial.GetType();
                Assert.That(rayMaterialType.GetField("Color").GetValue(rayMaterial), Is.EqualTo((Color32)unityMaterial.color));
                Assert.That((float)rayMaterialType.GetField("Metallic").GetValue(rayMaterial), Is.Zero);
                Assert.That((bool)rayMaterialType.GetField("InterpolateNormals").GetValue(rayMaterial), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unityMaterial);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConfigureHierarchy_RegistersMeshWithoutARasterRenderer()
        {
            var root = new GameObject("glTF Root");
            var meshObject = new GameObject("Mesh Without Renderer");
            meshObject.transform.SetParent(root.transform);
            meshObject.AddComponent<MeshFilter>().sharedMesh = CreateTriangleMesh();

            try
            {
                Type.GetType("GltfRayTracingSetup, Assembly-CSharp")
                    .GetMethod("ConfigureHierarchy")
                    .Invoke(null, new object[] { root, false, null });

                Assert.That(meshObject.GetComponent("RayMaterial"), Is.Not.Null);
                Assert.That(meshObject.GetComponent("PathTracingObject"), Is.Not.Null);
                Assert.That(meshObject.GetComponent<MeshRenderer>(), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConfigureHierarchy_CompactsVerticesForEachMaterialSubmesh()
        {
            var root = new GameObject("glTF Root");
            var meshObject = new GameObject("Multi-material Mesh");
            meshObject.transform.SetParent(root.transform);
            meshObject.AddComponent<MeshFilter>().sharedMesh = CreateTwoSubmeshMesh();
            var meshRenderer = meshObject.AddComponent<MeshRenderer>();
            var firstMaterial = new Material(Shader.Find("Standard"));
            var secondMaterial = new Material(Shader.Find("Standard"));
            meshRenderer.sharedMaterials = new[] { firstMaterial, secondMaterial };

            try
            {
                Type.GetType("GltfRayTracingSetup, Assembly-CSharp")
                    .GetMethod("ConfigureHierarchy")
                    .Invoke(null, new object[] { root, false, null });

                MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>();
                int compactSubmeshCount = 0;
                foreach (MeshFilter filter in filters)
                {
                    if (filter.gameObject.name.StartsWith("Ray Tracing Multi-material Mesh"))
                    {
                        Assert.That(filter.sharedMesh.vertexCount, Is.EqualTo(3));
                        Assert.That(filter.sharedMesh.triangles, Is.EqualTo(new[] { 0, 1, 2 }));
                        Assert.That(filter.sharedMesh.indexFormat, Is.EqualTo(IndexFormat.UInt16));
                        compactSubmeshCount++;
                    }
                }

                Assert.That(compactSubmeshCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstMaterial);
                UnityEngine.Object.DestroyImmediate(secondMaterial);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConfigureHierarchy_IgnoresNonTriangleMeshPrimitives()
        {
            var root = new GameObject("glTF Root");
            var meshObject = new GameObject("Line Primitive");
            meshObject.transform.SetParent(root.transform);
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right };
            mesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;

            try
            {
                Type.GetType("GltfRayTracingSetup, Assembly-CSharp")
                    .GetMethod("ConfigureHierarchy")
                    .Invoke(null, new object[] { root, false, null });

                Assert.That(meshObject.GetComponent("RayMaterial"), Is.Null);
                Assert.That(meshObject.GetComponent("PathTracingObject"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MeshBvhBuilder_ProcessesOnlyTriangleSubmeshes()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/AccelerationStructures/MeshBvhBuilder.cs");

            Assert.That(source, Does.Contain("mesh.GetTopology(submeshIndex) != MeshTopology.Triangles"));
            Assert.That(source, Does.Contain("mesh.GetIndices(submeshIndex)"));
        }

        [Test]
        public void GltfTransmission_DoesNotForceOpacityBelowOne()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GltfRayTracingSetup.cs");

            Assert.That(source, Does.Contain("rayMaterial.Opacity = 1.0f"));
            Assert.That(source, Does.Not.Contain("rayMaterial.Opacity = 0.0f"));
        }

        [Test]
        public void TransformedMeshBvhBounds_RetainWorldSpacePadding()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");

            Assert.That(source, Does.Contain("Vector3 padding = Vector3.one * SceneBvhManager.BoundsPadding"));
            Assert.That(source, Does.Contain("boundsMin -= padding"));
            Assert.That(source, Does.Contain("boundsMax += padding"));
        }

        [Test]
        public void ApplyMaterial_MapsGltfTransmissionAndIorToGlass()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/GltfRayTracingSetup.cs");

            Assert.That(source, Does.Contain("KHR_materials_transmission?.transmissionFactor"));
            Assert.That(source, Does.Contain("rayMaterial.Type = RayMaterial.MaterialType.Glass"));
            Assert.That(source, Does.Contain("rayMaterial.Transmission = Mathf.Clamp01(transmission)"));
            Assert.That(source, Does.Not.Contain("rayMaterial.Opacity = 0.0f"));
            Assert.That(source, Does.Contain("KHR_materials_ior?.ior ?? 1.5f"));
            Assert.That(source, Does.Contain("rayMaterial.Smoothness = Mathf.Clamp01(1.0f - pbr.roughnessFactor)"));
            Assert.That(source, Does.Contain("rayMaterial.Metallic = pbr.metallicRoughnessTexture != null ? 1.0f : 0.0f"));
            Assert.That(source, Does.Contain("rayMaterial.Color = pbr.baseColorTexture != null ? Color.white : pbr.BaseColor"));
            Assert.That(source, Does.Contain("\"metallicRoughnessTexture\""));
            Assert.That(source, Does.Contain("\"normalTexture\""));
            Assert.That(source, Does.Contain("\"roughnessFactor\""));
            Assert.That(source, Does.Contain("IsKeywordEnabled(\"_ALPHATEST_ON\")"));
            Assert.That(source, Does.Contain("gltfMaterial.GetAlphaMode() == GLTFast.Schema.MaterialBase.AlphaMode.Mask"));
            Assert.That(source, Does.Contain("GetGltfTexture(gltfImport, pbr.baseColorTexture?.index ?? -1)"));
            Assert.That(source, Does.Contain("rayMaterial.NormalStrength = Mathf.Clamp(gltfMaterial.normalTexture?.scale ?? 1.0f, 0.0f, 2.0f)"));
            Assert.That(source, Does.Contain("rayMaterial.TextureUvScale = new Vector2(scale[0], scale[1])"));
            Assert.That(source, Does.Contain("TextureUvRotation = textureInfo?.Extensions?.KHR_texture_transform?.rotation * Mathf.Rad2Deg"));
        }


        private static Mesh CreateTriangleMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateTwoSubmeshMesh()
        {
            var mesh = new Mesh { name = "Multi-material Mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
            return mesh;
        }

        private static Type GetImporterType()
        {
            Type importerType = Type.GetType("GltfRayTracingImporter, Assembly-CSharp-Editor");
            Assert.That(importerType, Is.Not.Null);
            return importerType;
        }
    }
}
