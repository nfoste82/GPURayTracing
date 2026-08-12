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
                signature = RayTracingBvhBakeUtility.CalculateSignature(entries)
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
            if (RayTracingBvhBakeUtility.IsBakeCurrent(manager, entries))
            {
                continue;
            }
            if (RayTracingBvhBakeUtility.CalculateSignature(entries) != item.signature)
            {
                Debug.LogWarning("Skipped automatic BVH bake because ray-traced geometry changed while Play mode was active.", manager);
                continue;
            }

            RayTracingBvhBakeUtility.SaveBuiltTemplates(manager, entries, item.signature);
        }
        SessionState.EraseString(SessionKey);
    }

    private static PendingBakeList LoadPendingBakes()
    {
        string json = SessionState.GetString(SessionKey, string.Empty);
        return string.IsNullOrEmpty(json) ? new PendingBakeList() : JsonUtility.FromJson<PendingBakeList>(json);
    }
}
