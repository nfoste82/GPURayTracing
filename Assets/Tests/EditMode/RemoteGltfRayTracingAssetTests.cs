using System;
using NUnit.Framework;

namespace GPURayTracing.Tests
{
    public class RemoteGltfRayTracingAssetTests
    {
        [Test]
        public void RemoteAsset_ExposesDetachedLoadedAssetMethod()
        {
            Type componentType = Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp");

            Assert.That(componentType.GetMethod("DetachLoadedAsset"), Is.Not.Null);
        }

        [TestCase("https://assets.example.com/models/helmet.glb", true)]
        [TestCase("http://assets.example.com/models/helmet.gltf", true)]
        [TestCase("file:///tmp/helmet.glb", false)]
        [TestCase("Assets/Models/helmet.glb", false)]
        public void RemoteGltfUrl_OnlyAcceptsAbsoluteHttpGltfUrls(string url, bool expectedToStart)
        {
            Type componentType = Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp");
            bool result = (bool)componentType.GetMethod("IsRemoteGltfUrl").Invoke(null, new object[] { url });
            Assert.That(result, Is.EqualTo(expectedToStart));
        }

        [Test]
        public void RemoteAsset_ExposesImportedCameraPoseWithoutCameraSceneObject()
        {
            Type componentType = Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp");
            Assert.That(componentType.GetProperty("HasImportedCameraPose"), Is.Not.Null);
            Assert.That(componentType.GetProperty("ImportedCameraPosition"), Is.Not.Null);
            Assert.That(componentType.GetProperty("ImportedCameraRotation"), Is.Not.Null);
            Assert.That(componentType.GetProperty("ImportedCameraFieldOfView"), Is.Not.Null);
        }

        [Test]
        public void RemoteAsset_ExposesPreviewVisibilitySetting()
        {
            Type componentType = Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp");
            Assert.That(componentType.GetField("ShowPreviewsInPlayMode"), Is.Not.Null);
        }

        [Test]
        public void CachePath_IsStableAndDoesNotExposeUrlComponents()
        {
            Type componentType = Type.GetType("RemoteGltfRayTracingAsset, Assembly-CSharp");
            var getCachePath = componentType.GetMethod("GetCachePath");
            const string url = "https://assets.example.com/models/Shader Ball.glb?version=2";
            string first = (string)getCachePath.Invoke(null, new object[] { url });
            string second = (string)getCachePath.Invoke(null, new object[] { url });

            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Does.EndWith(".glb"));
            Assert.That(first, Does.Not.Contain("Shader Ball"));
        }
    }
}
