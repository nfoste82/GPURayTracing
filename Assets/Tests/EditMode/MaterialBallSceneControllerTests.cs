using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class MaterialBallSceneControllerTests
    {
        [Test]
        public void MaterialBallController_ConfiguresImportedMaterialSurface()
        {
            var root = new GameObject("Remote glTF Content");
            var surface = new GameObject("material_surface");
            surface.transform.SetParent(root.transform);
            Type materialType = Type.GetType("RayMaterial, Assembly-CSharp");
            Component material = surface.AddComponent(materialType);

            try
            {
                Type controllerType = Type.GetType("MaterialBallSceneController, Assembly-CSharp");
                MethodInfo configure = controllerType.GetMethod("ConfigureMaterial", BindingFlags.NonPublic | BindingFlags.Static);
                configure.Invoke(null, new object[] { root });

                Assert.That(materialType.GetField("Color").GetValue(material), Is.EqualTo(new Color32(255, 202, 130, 255)));
                Assert.That((float)materialType.GetField("Metallic").GetValue(material), Is.EqualTo(1.0f));
                Assert.That((float)materialType.GetField("Smoothness").GetValue(material), Is.EqualTo(1.0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MaterialBallController_UsesImportedCameraPoseProperties()
        {
            Type controllerType = Type.GetType("MaterialBallSceneController, Assembly-CSharp");
            MethodInfo copyCamera = controllerType.GetMethod("CopyImportedCamera", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(copyCamera, Is.Not.Null);
        }

        [Test]
        public void MaterialBallController_RemovesNamedSourceCameraNode()
        {
            var root = new GameObject("Remote glTF Content");
            var cameraNode = new GameObject("camera");
            cameraNode.transform.SetParent(root.transform);
            try
            {
                Type controllerType = Type.GetType("MaterialBallSceneController, Assembly-CSharp");
                MethodInfo removeCamera = controllerType.GetMethod("RemoveSourceCameraNode", BindingFlags.Static | BindingFlags.NonPublic);
                removeCamera.Invoke(null, new object[] { root });
                Assert.That(root.transform.Find("camera"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MaterialBallAreaLightQuad_HasTheThreeJsLocalZNormal()
        {
            Type controllerType = Type.GetType("MaterialBallSceneController, Assembly-CSharp");
            MethodInfo createQuad = controllerType.GetMethod("CreateRectAreaLightQuad", BindingFlags.Static | BindingFlags.NonPublic);
            Mesh mesh = (Mesh)createQuad.Invoke(null, new object[] { 2.0f });
            try
            {
                Assert.That(mesh.normals[0], Is.EqualTo(Vector3.forward));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MaterialBallController_UsesTheThreeJsForwardOrbitTargetDistance()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/MaterialBallSceneController.cs");
            Assert.That(source, Does.Contain("Vector3.forward * 25.0f"));
            Assert.That(source, Does.Contain("SetOrbitState"));
        }

        [Test]
        public void MaterialBallController_UsesCalibratedPhotometricAreaLightConversion()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/MaterialBallSceneController.cs");
            Assert.That(source, Does.Contain("LumensPerWatt = 683.0f"));
            Assert.That(source, Does.Contain("AreaLightRadianceScale = 0.00684f"));
            Assert.That(source, Does.Contain("size * size * 4.0f * Mathf.PI"));
        }
    }
}
