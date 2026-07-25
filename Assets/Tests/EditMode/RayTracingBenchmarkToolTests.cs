using System;
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
}
