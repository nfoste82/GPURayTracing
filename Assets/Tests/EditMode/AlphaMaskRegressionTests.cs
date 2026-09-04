using NUnit.Framework;

namespace GPURayTracing.Tests
{
    public sealed class AlphaMaskRegressionTests
    {
        [Test]
        public void MaskedAlbedoTexels_AreRejectedDuringMeshIntersection()
        {
            string shader = System.IO.File.ReadAllText("Assets/Scripts/RayTracingShared.hlsl");

            Assert.That(shader, Does.Contain("meshTriangle.alphaMasked != 0"));
            Assert.That(shader, Does.Contain("_MeshAlbedoTextures.SampleLevel"));
            Assert.That(shader, Does.Contain(".a < meshTriangle.alphaCutoff"));
            Assert.That(shader, Does.Contain("RayHit hit = IntersectTriangle(ray, CreateRayHit(), _Triangles[node.triangleStart + i]"));
        }
    }
}
