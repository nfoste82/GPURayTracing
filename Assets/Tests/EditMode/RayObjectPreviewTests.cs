using System;
using NUnit.Framework;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class RayObjectPreviewTests
    {
        [Test]
        public void PreviewMaterial_UsesRayMaterialOpacityAsAlpha()
        {
            var gameObject = new GameObject("Ray Object Preview Opacity Test");
            try
            {
                Type rayMaterialType = Type.GetType("RayMaterial, Assembly-CSharp");
                Type rayTracingObjectType = Type.GetType("RayTracingObject, Assembly-CSharp");
                Assert.That(rayMaterialType, Is.Not.Null);
                Assert.That(rayTracingObjectType, Is.Not.Null);

                Component rayMaterial = gameObject.AddComponent(rayMaterialType);
                rayMaterialType.GetField("Color").SetValue(rayMaterial, new Color32(51, 102, 153, 255));
                rayMaterialType.GetField("Opacity").SetValue(rayMaterial, 0.25f);
                gameObject.AddComponent(rayTracingObjectType);

                Material previewMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(previewMaterial, Is.Not.Null);
                Assert.That(previewMaterial.shader.name, Is.EqualTo("Hidden/RayTracing/ScenePreview"));
                Assert.That(previewMaterial.GetColor("_Color").a, Is.EqualTo(0.25f).Within(0.0001f));
                Assert.That(previewMaterial.renderQueue, Is.EqualTo((int)UnityEngine.Rendering.RenderQueue.Transparent));
                Assert.That(previewMaterial.GetFloat("_ZWrite"), Is.EqualTo(0.0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
