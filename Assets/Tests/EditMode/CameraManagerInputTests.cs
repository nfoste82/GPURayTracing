using System;
using NUnit.Framework;

namespace GPURayTracing.Tests
{
    public class CameraManagerInputTests
    {
        [Test]
        public void OrbitFocusUsesRightMouseButtonSoLeftClickRemainsAvailableForFocus()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/Camera/CameraManager.cs");
            Assert.That(source, Does.Contain("cameraBehavior == CameraBehavior.OrbitFocusPoint ? 1 : 0"));
            Assert.That(source, Does.Contain("Input.GetMouseButtonDown(focusMouseButton)"));
        }

        [Test]
        public void OrbitDollyUsesItsDedicatedSpeed()
        {
            string source = System.IO.File.ReadAllText("Assets/Scripts/Camera/CameraManager.cs");
            Assert.That(source, Does.Contain("float dollySpeed = Mathf.Max(0.01f, cameraOrbitZoomSpeed);"));
            Assert.That(source, Does.Contain("_orbitDistance - delta * dollySpeed"));
        }
    }
}
