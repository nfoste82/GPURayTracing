using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(GameManager))]
public sealed class GameManagerEditor : Editor
{
    private enum BakeStatus
    {
        NotBaked,
        Baked,
        OutOfDate
    }

    private void OnEnable()
    {
        EditorApplication.projectChanged += Repaint;
        EditorApplication.hierarchyChanged += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.projectChanged -= Repaint;
        EditorApplication.hierarchyChanged -= Repaint;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var manager = (GameManager)target;
        List<RayTracingBvhBakeAsset.MeshEntry> entries = RayTracingBvhBakeUtility.GetMeshEntries(manager);
        string signature = RayTracingBvhBakeUtility.CalculateSignature(entries);
        BakeStatus status = GetBakeStatus(manager.EditorBvhBake, signature);

        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || string.IsNullOrEmpty(manager.gameObject.scene.path)))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake BVH"))
                {
                    serializedObject.ApplyModifiedProperties();
                    RayTracingBvhBakeUtility.Bake(manager, entries, signature);
                    serializedObject.Update();
                }

                GUILayout.Label(GetStatusLabel(status), EditorStyles.boldLabel, GUILayout.Width(130.0f));
            }
        }

        EditorGUILayout.PropertyField(serializedObject.FindProperty("bakeBvhUponExit"), new GUIContent("Bake upon exit"));
        EditorGUILayout.Space();
        DrawPropertiesExcluding(serializedObject, "m_Script", "bvhBake", "bakeBvhUponExit");
        serializedObject.ApplyModifiedProperties();
    }

    private static BakeStatus GetBakeStatus(RayTracingBvhBakeAsset bake, string signature)
    {
        if (bake == null)
        {
            return BakeStatus.NotBaked;
        }

        return bake.formatVersion == GameManager.BvhBakeFormatVersion
            && bake.sceneSignature == signature
            && RayTracingBvhBakeUtility.IsBakeBinaryUsable(bake)
            ? BakeStatus.Baked
            : BakeStatus.OutOfDate;
    }

    private static string GetStatusLabel(BakeStatus status)
    {
        switch (status)
        {
            case BakeStatus.Baked: return "Baked";
            case BakeStatus.OutOfDate: return "Bake is out-of-date";
            default: return "Not baked";
        }
    }
}

public static class RayTracingBvhBakeUtility
{
    private const string BakeFolder = "Assets/Generated/RayTracingBvhBakes";
    private const string StreamingBakeFolder = "Assets/StreamingAssets/RayTracingBvhBakes";

    public static List<RayTracingBvhBakeAsset.MeshEntry> GetMeshEntries(GameManager manager)
    {
        var entriesByKey = new Dictionary<string, RayTracingBvhBakeAsset.MeshEntry>();
        foreach (RayTracingObject rayObject in manager.GetComponentsInChildren<RayTracingObject>(true))
        {
            if (!rayObject.isActiveAndEnabled)
            {
                continue;
            }

            var filter = rayObject.GetComponent<MeshFilter>();
            var material = rayObject.GetComponent<RayMaterial>();
            var light = rayObject.GetComponent<RayLight>();
            if (filter == null || filter.sharedMesh == null || (material == null && light == null))
            {
                continue;
            }

            bool interpolateNormals = material != null && material.InterpolateNormals;
            string identity = GetMeshIdentity(filter.sharedMesh);
            string key = identity + (interpolateNormals ? ":smooth" : ":flat");
            entriesByKey[key] = new RayTracingBvhBakeAsset.MeshEntry
            {
                mesh = filter.sharedMesh,
                meshIdentity = identity,
                interpolateNormals = interpolateNormals,
                dependencyHash = GetMeshDependencyHash(filter.sharedMesh),
                vertexCount = filter.sharedMesh.vertexCount,
                indexCount = GetMeshIndexCount(filter.sharedMesh)
            };
        }

        var keys = new List<string>(entriesByKey.Keys);
        keys.Sort(StringComparer.Ordinal);
        var entries = new List<RayTracingBvhBakeAsset.MeshEntry>(keys.Count);
        foreach (string key in keys)
        {
            entries.Add(entriesByKey[key]);
        }
        return entries;
    }

    public static string CalculateSignature(List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        var source = new StringBuilder(entries.Count * 80);
        source.Append(GameManager.BvhBakeFormatVersion).Append('|');
        foreach (var entry in entries)
        {
            source.Append(entry.meshIdentity).Append('|');
            source.Append(entry.interpolateNormals ? '1' : '0').Append('|');
            source.Append(entry.dependencyHash);
            source.Append(';');
        }

        using (var hash = SHA256.Create())
        {
            byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()));
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }
    }

    public static bool IsBakeCurrent(GameManager manager)
    {
        var entries = GetMeshEntries(manager);
        return IsBakeCurrent(manager.EditorBvhBake, entries);
    }

    public static bool IsBakeCurrent(RayTracingBvhBakeAsset bake, List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        if (bake == null)
        {
            return false;
        }
        return bake.formatVersion == GameManager.BvhBakeFormatVersion
            && bake.sceneSignature == CalculateSignature(entries)
            && IsBakeBinaryUsable(bake);
    }

    public static void Bake(GameManager manager, List<RayTracingBvhBakeAsset.MeshEntry> entries = null, string signature = null)
    {
        if (string.IsNullOrEmpty(manager.gameObject.scene.path))
        {
            Debug.LogError("Save the scene before baking its ray tracing BVH.", manager);
            return;
        }

        entries = entries ?? GetMeshEntries(manager);
        signature = signature ?? CalculateSignature(entries);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Baking ray tracing BVH", entries[i].mesh.name, entries.Count == 0 ? 1.0f : i / (float)entries.Count);
                manager.EditorBuildMeshBvhTemplate(entries[i].mesh, entries[i].interpolateNormals);
                manager.EditorGetMeshBvhTemplateCounts(entries[i].mesh, entries[i].interpolateNormals, out int triangleCount, out int nodeCount);
                var entry = entries[i];
                entry.triangleCount = triangleCount;
                entry.nodeCount = nodeCount;
                entries[i] = entry;
            }

            string assetPath = SaveBuiltTemplates(manager, entries, signature);
            var bake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(assetPath);
            AssignBake(manager, bake, "Assign baked ray tracing BVH");
            Debug.Log($"Baked {entries.Count:N0} ray tracing mesh BVHs.", manager);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    public static string SaveBuiltTemplates(GameManager manager, List<RayTracingBvhBakeAsset.MeshEntry> entries, string signature)
    {
        EnsureFolder(BakeFolder);
        EnsureFolder(StreamingBakeFolder);
        string sceneGuid = AssetDatabase.AssetPathToGUID(manager.gameObject.scene.path);
        string managerId = GlobalObjectId.GetGlobalObjectIdSlow(manager).targetObjectId.ToString();
        string fileStem = $"{sceneGuid}_{managerId}";
        string assetPath = $"{BakeFolder}/{fileStem}.asset";
        string binaryAssetPath = $"{StreamingBakeFolder}/{fileStem}.bytes";
        var bake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(assetPath);
        if (bake == null)
        {
            bake = ScriptableObject.CreateInstance<RayTracingBvhBakeAsset>();
            AssetDatabase.CreateAsset(bake, assetPath);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            manager.EditorGetMeshBvhTemplateCounts(entries[i].mesh, entries[i].interpolateNormals, out int triangleCount, out int nodeCount);
            var entry = entries[i];
            entry.triangleCount = triangleCount;
            entry.nodeCount = nodeCount;
            entries[i] = entry;
        }

        bake.formatVersion = GameManager.BvhBakeFormatVersion;
        bake.sceneSignature = signature;
        bake.streamingAssetsRelativePath = $"RayTracingBvhBakes/{fileStem}.bytes";
        bake.meshes = entries;
        manager.EditorWriteMeshBvhBake(Path.GetFullPath(binaryAssetPath), bake);
        EditorUtility.SetDirty(bake);
        AssetDatabase.ImportAsset(binaryAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        DeleteStaleSceneBakes(manager, sceneGuid, assetPath, binaryAssetPath);
        return assetPath;
    }

    private static void DeleteStaleSceneBakes(
        GameManager bakedManager,
        string sceneGuid,
        string currentAssetPath,
        string currentBinaryAssetPath)
    {
        string sceneBakePrefix = sceneGuid + "_";
        var preservedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            currentAssetPath,
            currentBinaryAssetPath
        };

        foreach (GameObject root in bakedManager.gameObject.scene.GetRootGameObjects())
        {
            foreach (GameManager manager in root.GetComponentsInChildren<GameManager>(true))
            {
                if (manager == bakedManager || manager.EditorBvhBake == null)
                {
                    continue;
                }

                string referencedAssetPath = AssetDatabase.GetAssetPath(manager.EditorBvhBake);
                if (!string.IsNullOrEmpty(referencedAssetPath))
                {
                    preservedPaths.Add(referencedAssetPath);
                }
                if (!string.IsNullOrEmpty(manager.EditorBvhBake.streamingAssetsRelativePath))
                {
                    preservedPaths.Add("Assets/StreamingAssets/" + manager.EditorBvhBake.streamingAssetsRelativePath);
                }
            }
        }

        int deletedCount = DeleteStaleBakeAssets(BakeFolder, ".asset", sceneBakePrefix, preservedPaths);
        deletedCount += DeleteStaleBakeAssets(StreamingBakeFolder, ".bytes", sceneBakePrefix, preservedPaths);
        if (deletedCount > 0)
        {
            Debug.Log($"Deleted {deletedCount:N0} stale ray tracing BVH bake files for scene '{bakedManager.gameObject.scene.name}'.", bakedManager);
        }
    }

    private static int DeleteStaleBakeAssets(
        string folder,
        string extension,
        string sceneBakePrefix,
        HashSet<string> preservedPaths)
    {
        int deletedCount = 0;
        foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileName(path);
            if (preservedPaths.Contains(path)
                || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !fileName.StartsWith(sceneBakePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (AssetDatabase.DeleteAsset(path))
            {
                deletedCount++;
            }
        }
        return deletedCount;
    }

    public static void AssignBake(GameManager manager, RayTracingBvhBakeAsset bake, string undoName)
    {
        Undo.RecordObject(manager, undoName);
        var serializedManager = new SerializedObject(manager);
        serializedManager.FindProperty("bvhBake").objectReferenceValue = bake;
        serializedManager.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        EditorSceneManager.SaveScene(manager.gameObject.scene);
    }

    public static bool IsBakeBinaryUsable(RayTracingBvhBakeAsset bake)
    {
        if (bake == null || string.IsNullOrEmpty(bake.streamingAssetsRelativePath))
        {
            return false;
        }

        string path = Path.Combine(Application.streamingAssetsPath, bake.streamingAssetsRelativePath);
        if (!File.Exists(path))
        {
            return false;
        }

        long expectedLength = 12L;
        foreach (var entry in bake.meshes)
        {
            expectedLength += 8L + entry.triangleCount * 160L + entry.nodeCount * 40L;
        }
        return new FileInfo(path).Length == expectedLength;
    }

    private static string GetMeshIdentity(Mesh mesh)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
        {
            return guid + ":" + localId;
        }
        return "scene:" + mesh.name + ":" + mesh.vertexCount + ":" + GetMeshIndexCount(mesh);
    }

    private static string GetMeshDependencyHash(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        return string.IsNullOrEmpty(path)
            ? $"scene:{mesh.vertexCount}:{GetMeshIndexCount(mesh)}"
            : AssetDatabase.GetAssetDependencyHash(path).ToString();
    }

    private static int GetMeshIndexCount(Mesh mesh)
    {
        int count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            count += checked((int)mesh.GetIndexCount(i));
        }
        return count;
    }

    private static void EnsureFolder(string path)
    {
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(path))
        {
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
