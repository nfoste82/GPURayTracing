using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

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
            unityMaterial.SetFloat("_Metallic", 0.7f);
            meshRenderer.sharedMaterial = unityMaterial;

            try
            {
                Type.GetType("GltfRayTracingSetup, Assembly-CSharp")
                    .GetMethod("ConfigureHierarchy")
                    .Invoke(null, new object[] { root });

                Component rayMaterial = meshObject.GetComponent("RayMaterial");
                Assert.That(rayMaterial, Is.Not.Null);
                Assert.That(meshObject.GetComponent("PathTracingObject"), Is.Not.Null);
                Type rayMaterialType = rayMaterial.GetType();
                Assert.That(rayMaterialType.GetField("Color").GetValue(rayMaterial), Is.EqualTo((Color32)unityMaterial.color));
                Assert.That((float)rayMaterialType.GetField("Metallic").GetValue(rayMaterial), Is.EqualTo(0.7f).Within(0.0001f));
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
                    .Invoke(null, new object[] { root });

                Assert.That(meshObject.GetComponent("RayMaterial"), Is.Not.Null);
                Assert.That(meshObject.GetComponent("PathTracingObject"), Is.Not.Null);
                Assert.That(meshObject.GetComponent<MeshRenderer>(), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }


        private static Mesh CreateTriangleMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
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
