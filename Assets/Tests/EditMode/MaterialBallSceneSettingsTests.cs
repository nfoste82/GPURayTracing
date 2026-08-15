using System;
using System.Reflection;
using NUnit.Framework;

namespace GPURayTracing.Tests
{
    public class MaterialBallSceneSettingsTests
    {
        [Test]
        public void MaterialBallGenerator_UsesMinimalLightFalloff()
        {
            string source = System.IO.File.ReadAllText("Assets/Editor/RayTracingSceneGenerator.cs");
            Assert.That(source, Does.Contain("LightFalloffScale = 0.008f"));
        }
    }
}
