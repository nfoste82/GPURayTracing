using System;
using System.Collections;
using System.Reflection;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class RayTracingBenchmarkToolTests
{
    [Test]
    public void BenchmarkOverlays_DefaultToHiddenWithExpectedHotkeys()
    {
        var gameObject = new GameObject("Benchmark tools test");
        try
        {
            Type debugOverlayType = GetRuntimeType("RayTracingBenchmarkOverlay");
            Type runnerType = GetRuntimeType("RayTracingBenchmarkRunner");
            Component debugOverlay = gameObject.AddComponent(debugOverlayType);
            Component runner = gameObject.AddComponent(runnerType);

            Assert.That(GetField<bool>(debugOverlay, "showOverlay"), Is.False);
            Assert.That(GetField<KeyCode>(debugOverlay, "toggleKey"), Is.EqualTo(KeyCode.Z));
            Assert.That(GetField<bool>(runner, "showOverlay"), Is.False);
            Assert.That(GetField<KeyCode>(runner, "toggleKey"), Is.EqualTo(KeyCode.X));
            Assert.That(GetField<KeyCode>(runner, "runKey"), Is.EqualTo(KeyCode.B));
            Assert.That(GetField<bool>(runner, "sweepCausticPhotonCounts"), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [TestCase(5.0f, 1.0f / 30.0f, 150)]
    [TestCase(5.0f, 1.0f / 60.0f, 300)]
    [TestCase(1.01f, 0.5f, 3)]
    [TestCase(0.0f, 0.5f, 0)]
    public void VideoCapture_CalculatesOutputFrameCount(float duration, float timeStep, int expected)
    {
        Type managerType = GetRuntimeType("VideoCaptureManager");
        MethodInfo method = managerType.GetMethod("CalculateFrameCount", BindingFlags.Public | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        Assert.That(method.Invoke(null, new object[] { duration, timeStep }), Is.EqualTo(expected));
    }

    [Test]
    public void VideoCapture_EstimatesRenderTimeFromCurrentSampleRate()
    {
        Type managerType = GetRuntimeType("VideoCaptureManager");
        MethodInfo method = managerType.GetMethod(
            "EstimateCaptureSeconds",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(float), typeof(bool) },
            null);

        Assert.That(method, Is.Not.Null);
        double seconds = (double)method.Invoke(null, new object[] { 150, 128, 4, 20.0f, false });
        Assert.That(seconds, Is.EqualTo(96.0).Within(0.0001));
    }

    [Test]
    public void VideoCapture_WithCaustics_UsesOnePhotonBatchPerRequestedSample()
    {
        Type managerType = GetRuntimeType("VideoCaptureManager");
        MethodInfo dispatchSamplesMethod = managerType.GetMethod(
            "GetSamplesPerDispatch",
            BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo estimateMethod = managerType.GetMethod(
            "EstimateCaptureSeconds",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(float), typeof(bool) },
            null);

        Assert.That(dispatchSamplesMethod, Is.Not.Null);
        Assert.That(dispatchSamplesMethod.Invoke(null, new object[] { 128, true }), Is.EqualTo(1));
        Assert.That(dispatchSamplesMethod.Invoke(null, new object[] { 128, false }), Is.EqualTo(32));

        Assert.That(estimateMethod, Is.Not.Null);
        double seconds = (double)estimateMethod.Invoke(null, new object[] { 150, 128, 4, 20.0f, true });
        Assert.That(seconds, Is.EqualTo(384.0).Within(0.0001));
    }

    [Test]
    public void VideoCapture_EncoderUsesTimestepAsFrameRateAndNumberedPngInput()
    {
        Type managerType = GetRuntimeType("VideoCaptureManager");
        MethodInfo method = managerType.GetMethod(
            "BuildEncoderArguments",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        string directory = Path.Combine("capture root", "frames");
        string output = Path.Combine(directory, "video.mp4");
        string arguments = (string)method.Invoke(null, new object[] { directory, output, 1.0f / 30.0f });

        Assert.That(arguments, Does.Contain("-framerate 30"));
        Assert.That(arguments, Does.Contain("frame_%06d.png"));
        Assert.That(arguments, Does.Contain("-c:v libx264"));
        Assert.That(arguments, Does.Contain("-pix_fmt yuv420p"));
        Assert.That(arguments, Does.EndWith("\"" + output + "\""));
    }

    [Test]
    public void GameManager_EnsuresBenchmarkToolsOnItsOwnObject()
    {
        var gameObject = new GameObject("Game Manager test");
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type debugOverlayType = GetRuntimeType("RayTracingBenchmarkOverlay");
            Type runnerType = GetRuntimeType("RayTracingBenchmarkRunner");
            Component manager = gameObject.AddComponent(managerType);
            MethodInfo ensureMethod = managerType.GetMethod(
                "EnsureBenchmarkComponents",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(ensureMethod, Is.Not.Null);
            ensureMethod.Invoke(manager, null);
            ensureMethod.Invoke(manager, null);

            Component[] debugOverlays = gameObject.GetComponents(debugOverlayType);
            Component[] runners = gameObject.GetComponents(runnerType);
            Assert.That(debugOverlays, Has.Length.EqualTo(1));
            Assert.That(runners, Has.Length.EqualTo(1));
            Assert.That(GetField<Component>(debugOverlays[0], "gameManager"), Is.SameAs(manager));
            Assert.That(GetField<Component>(runners[0], "gameManager"), Is.SameAs(manager));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void GameManager_DefersMeshDataBuildUntilBufferRebuild()
    {
        var root = new GameObject("Game Manager mesh registration test");
        var meshObject = new GameObject("Registered mesh");
        Mesh mesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type materialType = GetRuntimeType("RayMaterial");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            meshObject.transform.SetParent(root.transform);

            mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            meshObject.AddComponent(materialType);

            Component rayTracingObject = meshObject.AddComponent(rayTracingObjectType);
            MethodInfo registerMethod = managerType.GetMethod("RegisterObject");
            Assert.That(registerMethod, Is.Not.Null);
            registerMethod.Invoke(manager, new object[] { rayTracingObject });

            Assert.That(GetCollectionCount(manager, "_meshObjects"), Is.EqualTo(1));
            Assert.That(GetCollectionCount(manager, "_triangles"), Is.Zero);

            MethodInfo rebuildMethod = managerType.GetMethod(
                "RebuildTriangleData",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(rebuildMethod, Is.Not.Null);
            rebuildMethod.Invoke(manager, null);

            Assert.That(GetCollectionCount(manager, "_triangles"), Is.EqualTo(1));
            Assert.That(rayTracingObject, Is.Not.Null);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(meshObject);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    [Test]
    public void GameManager_RegistersColliderBackedLightAsSphereWhenPreviewMeshExists()
    {
        var root = new GameObject("Game Manager sphere light registration test");
        var lightObject = new GameObject("Collider-backed light with preview mesh");
        Mesh previewMesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type lightType = GetRuntimeType("RayLight");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            lightObject.transform.SetParent(root.transform);

            previewMesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            lightObject.AddComponent<SphereCollider>().radius = 1.5f;
            lightObject.AddComponent<MeshFilter>().sharedMesh = previewMesh;
            lightObject.AddComponent(lightType);
            Component rayTracingObject = lightObject.AddComponent(rayTracingObjectType);

            managerType.GetMethod("RegisterObject").Invoke(manager, new object[] { rayTracingObject });

            Assert.That(GetCollectionCount(manager, "_lightObjects"), Is.EqualTo(1));
            Assert.That(GetCollectionCount(manager, "_lights"), Is.EqualTo(1));
            Assert.That(GetCollectionCount(manager, "_meshObjects"), Is.Zero);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(previewMesh);
        }
    }

    [Test]
    public void GameManager_RegistersDirectionalLightWithoutSceneGeometry()
    {
        var root = new GameObject("Game Manager directional light registration test");
        var lightObject = new GameObject("Directional light");
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type directionalLightType = GetRuntimeType("RayDirectionalLight");
            Component manager = root.AddComponent(managerType);
            lightObject.transform.SetParent(root.transform);
            lightObject.transform.rotation = Quaternion.Euler(45.0f, 30.0f, 0.0f);
            Component directionalLight = lightObject.AddComponent(directionalLightType);

            managerType.GetMethod("RegisterDirectionalLight").Invoke(manager, new object[] { directionalLight });

            Assert.That(GetCollectionCount(manager, "_directionalLights"), Is.EqualTo(1));
            Assert.That(GetCollectionCount(manager, "_lights"), Is.EqualTo(2));
            Assert.That(GetCollectionCount(manager, "_lightObjects"), Is.Zero);
            Assert.That(GetCollectionCount(manager, "_meshObjects"), Is.Zero);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GameManager_ClassifiesMaterialEditsWithoutGeometryRebuild()
    {
        var root = new GameObject("Game Manager mesh change test");
        var meshObject = new GameObject("Registered mesh");
        Mesh mesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type materialType = GetRuntimeType("RayMaterial");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            meshObject.transform.SetParent(root.transform);

            mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            Component material = meshObject.AddComponent(materialType);
            Component rayTracingObject = meshObject.AddComponent(rayTracingObjectType);
            managerType.GetMethod("RegisterObject").Invoke(manager, new object[] { rayTracingObject });

            FieldInfo smoothnessField = materialType.GetField("Smoothness");
            Assert.That(smoothnessField, Is.Not.Null);
            smoothnessField.SetValue(material, 0.25f);

            object[] changeFlags = { false, false };
            MethodInfo updateCacheMethod = managerType.GetMethod(
                "UpdateMeshChangeCache",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(updateCacheMethod, Is.Not.Null);
            updateCacheMethod.Invoke(manager, changeFlags);

            Assert.That((bool)changeFlags[0], Is.False, "Material edits must not invalidate mesh geometry.");
            Assert.That((bool)changeFlags[1], Is.True);

            meshObject.transform.position = Vector3.right;
            changeFlags[0] = false;
            changeFlags[1] = false;
            updateCacheMethod.Invoke(manager, changeFlags);
            Assert.That((bool)changeFlags[0], Is.True, "Transform edits must invalidate world-space geometry.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(meshObject);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    [Test]
    public void GameManager_ReusesMeshBvhTemplateAcrossInstancesAndTransforms()
    {
        var root = new GameObject("Game Manager BVH cache test");
        var firstObject = new GameObject("First mesh");
        var secondObject = new GameObject("Second mesh");
        Mesh mesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type materialType = GetRuntimeType("RayMaterial");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            mesh = CreateTwoTriangleMesh();

            Component first = AddRegisteredMesh(firstObject, root.transform, mesh, materialType, rayTracingObjectType);
            Component second = AddRegisteredMesh(secondObject, root.transform, mesh, materialType, rayTracingObjectType);
            MethodInfo registerMethod = managerType.GetMethod("RegisterObject");
            registerMethod.Invoke(manager, new object[] { first });
            registerMethod.Invoke(manager, new object[] { second });

            InvokePrivate(manager, "RebuildTriangleData");
            Assert.That(GetCollectionCount(manager, "_meshBvhTemplates"), Is.EqualTo(1));
            Assert.That(GetCollectionCount(manager, "_triangles"), Is.EqualTo(4));

            secondObject.transform.position = new Vector3(5.0f, 2.0f, -1.0f);
            InvokePrivate(manager, "RebuildTriangleData");
            Assert.That(GetCollectionCount(manager, "_meshBvhTemplates"), Is.EqualTo(1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(firstObject);
            UnityEngine.Object.DestroyImmediate(secondObject);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    [Test]
    public void GameManager_LightRadianceEdit_DoesNotInvalidateTopLevelBvh()
    {
        var root = new GameObject("Game Manager light BVH invalidation test");
        var lightObject = new GameObject("Sphere light");
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type lightType = GetRuntimeType("RayLight");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            lightObject.transform.SetParent(root.transform);
            lightObject.AddComponent<SphereCollider>();
            Component light = lightObject.AddComponent(lightType);
            Component rayTracingObject = lightObject.AddComponent(rayTracingObjectType);
            managerType.GetMethod("RegisterObject").Invoke(manager, new object[] { rayTracingObject });

            SetField(manager, "_topLevelBvhDirty", false);
            lightType.GetField("Intensity").SetValue(light, 2.0f);

            InvokePrivate(manager, "UpdateSpheres");

            Assert.That(GetField<bool>(manager, "_topLevelBvhDirty"), Is.False,
                "Changing light radiance must update the light buffer without rebuilding the TLAS.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void GameManager_EmissiveMesh_UsesOneGlobalLightAndAreaCdf()
    {
        var root = new GameObject("Game Manager mesh light test");
        var lightObject = new GameObject("Emissive mesh");
        Mesh mesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type lightType = GetRuntimeType("RayLight");
            Type rayTracingObjectType = GetRuntimeType("PathTracingObject");
            Component manager = root.AddComponent(managerType);
            lightObject.transform.SetParent(root.transform);
            mesh = CreateTwoTriangleMesh();
            lightObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            lightObject.AddComponent(lightType);
            Component rayTracingObject = lightObject.AddComponent(rayTracingObjectType);
            managerType.GetMethod("RegisterObject").Invoke(manager, new object[] { rayTracingObject });

            InvokePrivate(manager, "RebuildTriangleData");

            Assert.That(GetCollectionCount(manager, "_lights"), Is.EqualTo(1),
                "A mesh light must occupy one global light slot regardless of triangle count.");
            ICollection cdf = GetCollection(manager, "_meshLightTriangleCdf");
            Assert.That(cdf.Count, Is.EqualTo(mesh.triangles.Length / 3));
            float lastCdf = 0.0f;
            foreach (object value in cdf)
            {
                lastCdf = (float)value;
            }
            Assert.That(lastCdf, Is.EqualTo(1.0f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static Type GetRuntimeType(string typeName)
    {
        Type type = Type.GetType($"{typeName}, Assembly-CSharp");
        Assert.That(type, Is.Not.Null, $"Could not load {typeName} from Assembly-CSharp");
        return type;
    }

    private static T GetField<T>(Component component, string fieldName)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Could not find {component.GetType().Name}.{fieldName}");
        return (T)field.GetValue(component);
    }

    private static void SetField(Component component, string fieldName, object value)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Could not find {component.GetType().Name}.{fieldName}");
        field.SetValue(component, value);
    }

    private static int GetCollectionCount(Component component, string fieldName)
    {
        return GetCollection(component, fieldName).Count;
    }

    private static ICollection GetCollection(Component component, string fieldName)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Could not find {component.GetType().Name}.{fieldName}");
        return (ICollection)field.GetValue(component);
    }

    private static Mesh CreateTwoTriangleMesh()
    {
        return new Mesh
        {
            vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one },
            triangles = new[] { 0, 1, 2, 1, 3, 2 }
        };
    }

    private static Component AddRegisteredMesh(
        GameObject gameObject,
        Transform parent,
        Mesh mesh,
        Type materialType,
        Type rayTracingObjectType)
    {
        gameObject.transform.SetParent(parent);
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        gameObject.AddComponent(materialType);
        return gameObject.AddComponent(rayTracingObjectType);
    }

    private static void InvokePrivate(Component component, string methodName)
    {
        MethodInfo method = component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method.Invoke(component, null);
    }
}
