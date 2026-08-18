using NUnit.Framework;

namespace GPURayTracing.Tests
{
    public class KhronosGltfBrowserSceneGeneratorTests
    {
        [Test]
        public void BrowserScene_UsesMaterialBallRoomPrefabAndItsLightingCalibration()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneGenerator.cs");

            Assert.That(source, Does.Contain("Assets/Prefabs/Material_Ball_Room.prefab"));
            Assert.That(source, Does.Contain("PrefabUtility.InstantiatePrefab(roomPrefab, context.Root)"));
            Assert.That(source, Does.Contain("SetOrbitState(MaterialBallRoomFocusPosition, MaterialBallRoomCameraPosition)"));
            Assert.That(source, Does.Contain("CameraBehavior = CameraBehavior.OrbitFocusPoint"));
            Assert.That(source, Does.Contain("browser.initialRoomCameraPosition = MaterialBallRoomCameraPosition"));
            Assert.That(source, Does.Contain("LightFalloffScale = 0.005f"));
            Assert.That(source, Does.Contain("DirectionalLightIntensity = 0.0f"));
            Assert.That(System.IO.File.ReadAllText("Assets/Scripts/KhronosGltfModelBrowser.cs"), Does.Contain("manager.RebuildBuffers()"));
        }
    }
}
