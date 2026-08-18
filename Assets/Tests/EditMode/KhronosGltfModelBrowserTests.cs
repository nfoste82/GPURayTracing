using System;
using NUnit.Framework;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class KhronosGltfModelBrowserTests
    {
        [Test]
        public void ModelUrl_UsesEscapedKhronosBinaryPath()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            string url = (string)browserType.GetMethod("BuildModelUrl", new[] { typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Unicode\u2665Test", "Model File.glb" });

            Assert.That(url, Is.EqualTo(
                "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/Unicode%E2%99%A5Test/glTF-Binary/Model%20File.glb"));
        }

        [Test]
        public void LoadedModel_IsCenteredHorizontallyAndPlacedOnFloor()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            Vector3 offset = (Vector3)browserType.GetMethod("GetFloorPlacementOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { new Bounds(new Vector3(4.0f, 6.0f, 8.0f), new Vector3(2.0f, 10.0f, 6.0f)) });

            Assert.That(offset, Is.EqualTo(new Vector3(4.0f, 1.0f, 8.0f)));
        }

        [Test]
        public void LoadedModel_ScalesSmallestModelsToHalfAUnitAlongTheirLargestAxis()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            float scale = (float)browserType.GetMethod("GetMinimumUniformScaleFactor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { new Bounds(Vector3.zero, new Vector3(0.1f, 0.2f, 0.125f)) });

            Assert.That(scale, Is.EqualTo(25.0f).Within(0.0001f));
        }

        [Test]
        public void LoadedModel_DoesNotShrinkModelsThatAlreadyMeetMinimumBounds()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            float scale = (float)browserType.GetMethod("GetMinimumUniformScaleFactor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { new Bounds(Vector3.zero, new Vector3(0.5f, 0.1f, 0.1f)) });

            Assert.That(scale, Is.EqualTo(10.0f));
        }

        [Test]
        public void LoadedModel_UsesAspectAwareBoundsFramingDistance()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            float distance = (float)browserType.GetMethod("GetFramingDistance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { new Bounds(Vector3.zero, new Vector3(8.0f, 4.0f, 2.0f)), 60.0f, 16.0f / 9.0f });

            Assert.That(distance, Is.EqualTo(5.8714f).Within(0.0001f));
        }

        [Test]
        public void LoadedModel_ScalesOrbitDollySpeedToBounds()
        {
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            float speed = (float)browserType.GetMethod("GetOrbitZoomSpeed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { new Bounds(Vector3.zero, new Vector3(6.0f, 8.0f, 12.0f)) });

            Assert.That(speed, Is.EqualTo(7.81025f).Within(0.0001f));
        }

        [Test]
        public void Browser_DefaultsToReplacingExistingModels()
        {
            var browserObject = new GameObject("Khronos Browser");
            Type browserType = Type.GetType("KhronosGltfModelBrowser, Assembly-CSharp");
            browserObject.AddComponent(Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp"));
            Component browser = browserObject.AddComponent(browserType);

            Assert.That((bool)browserType.GetField("replaceExistingModels").GetValue(browser), Is.True);

            UnityEngine.Object.DestroyImmediate(browserObject);
        }

        [Test]
        public void Browser_UsesAnExplicitCheckboxAndOmitsRoomDiagnostics()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/KhronosGltfModelBrowser.cs");

            Assert.That(source, Does.Contain("replaceExistingModels ? \"X\" : string.Empty"));
            Assert.That(source, Does.Not.Contain("\"Room: \""));
        }

    }
}
