using System;
using System.Collections;
using System.Reflection;
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
            Type rayTracingObjectType = GetRuntimeType("RayTracingObject");
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
    public void GameManager_ClassifiesMaterialEditsWithoutGeometryRebuild()
    {
        var root = new GameObject("Game Manager mesh change test");
        var meshObject = new GameObject("Registered mesh");
        Mesh mesh = null;
        try
        {
            Type managerType = GetRuntimeType("GameManager");
            Type materialType = GetRuntimeType("RayMaterial");
            Type rayTracingObjectType = GetRuntimeType("RayTracingObject");
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
            Type rayTracingObjectType = GetRuntimeType("RayTracingObject");
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

    private static Type GetRuntimeType(string typeName)
    {
        Type type = Type.GetType($"{typeName}, Assembly-CSharp");
        Assert.That(type, Is.Not.Null, $"Could not load {typeName} from Assembly-CSharp");
        return type;
    }

    private static T GetField<T>(Component component, string fieldName)
    {
        FieldInfo field = component.GetType().GetField(fieldName);
        Assert.That(field, Is.Not.Null, $"Could not find {component.GetType().Name}.{fieldName}");
        return (T)field.GetValue(component);
    }

    private static int GetCollectionCount(Component component, string fieldName)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Could not find {component.GetType().Name}.{fieldName}");
        return ((ICollection)field.GetValue(component)).Count;
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
