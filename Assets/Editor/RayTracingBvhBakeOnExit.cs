using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class RayTracingBvhBakeOnExit
{
    [Serializable]
    private sealed class PendingBakeList
    {
        public List<PendingBake> items = new List<PendingBake>();
    }

    [Serializable]
    private struct PendingBake
    {
        public string managerId;
        public string signature;
        public string originalBakeAssetPath;
        public string generatedBakeAssetPath;
    }

    private const string SessionKey = "GPURayTracing.PendingBvhBakeOnExit";

    static RayTracingBvhBakeOnExit()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            CapturePendingBakes();
        }
        else if (state == PlayModeStateChange.ExitingPlayMode)
        {
            SaveRuntimeBakes();
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.delayCall += AssignSavedBakes;
        }
    }

    private static void CapturePendingBakes()
    {
        var pending = new PendingBakeList();
        foreach (GameManager manager in UnityEngine.Object.FindObjectsByType<GameManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!manager.EditorBakeBvhUponExit || RayTracingBvhBakeUtility.IsBakeCurrent(manager))
            {
                continue;
            }

            var entries = RayTracingBvhBakeUtility.GetMeshEntries(manager);
            pending.items.Add(new PendingBake
            {
                managerId = GlobalObjectId.GetGlobalObjectIdSlow(manager).ToString(),
                signature = RayTracingBvhBakeUtility.CalculateSignature(entries),
                originalBakeAssetPath = AssetDatabase.GetAssetPath(manager.EditorBvhBake)
            });
        }
        SessionState.SetString(SessionKey, JsonUtility.ToJson(pending));
    }

    private static void SaveRuntimeBakes()
    {
        PendingBakeList pending = LoadPendingBakes();
        for (int i = 0; i < pending.items.Count; i++)
        {
            PendingBake item = pending.items[i];
            if (!GlobalObjectId.TryParse(item.managerId, out GlobalObjectId managerId))
            {
                continue;
            }

            var manager = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(managerId) as GameManager;
            if (manager == null || !manager.EditorBakeBvhUponExit)
            {
                continue;
            }

            var entries = RayTracingBvhBakeUtility.GetMeshEntries(manager);
            var originalBake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(item.originalBakeAssetPath);
            if (RayTracingBvhBakeUtility.IsBakeCurrent(originalBake, entries))
            {
                continue;
            }
            if (RayTracingBvhBakeUtility.CalculateSignature(entries) != item.signature)
            {
                Debug.LogWarning("Skipped automatic BVH bake because ray-traced geometry changed while Play mode was active.", manager);
                continue;
            }

            item.generatedBakeAssetPath = RayTracingBvhBakeUtility.SaveBuiltTemplates(manager, entries, item.signature);
            pending.items[i] = item;
        }
        SessionState.SetString(SessionKey, JsonUtility.ToJson(pending));
    }

    private static void AssignSavedBakes()
    {
        PendingBakeList pending = LoadPendingBakes();
        SessionState.EraseString(SessionKey);
        foreach (PendingBake item in pending.items)
        {
            if (string.IsNullOrEmpty(item.generatedBakeAssetPath)
                || !GlobalObjectId.TryParse(item.managerId, out GlobalObjectId managerId))
            {
                continue;
            }

            var manager = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(managerId) as GameManager;
            var bake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(item.generatedBakeAssetPath);
            if (manager == null || bake == null)
            {
                continue;
            }

            var entries = RayTracingBvhBakeUtility.GetMeshEntries(manager);
            if (RayTracingBvhBakeUtility.CalculateSignature(entries) != item.signature)
            {
                Debug.LogWarning("Skipped assigning the automatic BVH bake because the restored scene no longer matches its pre-Play state.", manager);
                continue;
            }

            RayTracingBvhBakeUtility.AssignBake(manager, bake, "Assign automatic ray tracing BVH bake");
        }
    }

    private static PendingBakeList LoadPendingBakes()
    {
        string json = SessionState.GetString(SessionKey, string.Empty);
        return string.IsNullOrEmpty(json) ? new PendingBakeList() : JsonUtility.FromJson<PendingBakeList>(json);
    }
}
