using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class RayMaterialEditorTests
    {
        [Test]
        public void EmissiveMaterialType_IsAvailableForAuthoring()
        {
            Type materialType = Type.GetType("RayMaterial, Assembly-CSharp");
            Type enumType = materialType.GetNestedType("MaterialType");
            Assert.That(Enum.IsDefined(enumType, "Emissive"), Is.True);
        }

        [Test]
        public void RayMaterialInspector_UsesRayLightAsTheEmissionDataSource()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayMaterialEditor.cs");
            Assert.That(source, Does.Contain("Undo.AddComponent<RayLight>"));
            Assert.That(source, Does.Contain("Undo.AddComponent<Light>"));
            Assert.That(source, Does.Contain("Undo.DestroyObjectImmediate(light)"));
            Assert.That(source, Does.Contain("Undo.DestroyObjectImmediate(unityLight)"));
            Assert.That(source, Does.Contain("FindProperty(\"Intensity\")"));
        }

        [Test]
        public void RayLight_ReclassifiesTheRegisteredPathTracingObject()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/RayLight.cs");
            Assert.That(source, Does.Contain("private void OnEnable()"));
            Assert.That(source, Does.Contain("private void OnDisable()"));
            Assert.That(source, Does.Contain("RefreshRegistration()"));
        }
    }
}
