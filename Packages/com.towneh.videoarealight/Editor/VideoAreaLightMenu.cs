using UnityEditor;
using UnityEngine;

public static class VideoAreaLightMenu
{
    // Mirrors the GUIDs in VideoAreaLightSourceEditor. Keeping them duplicated
    // (rather than centralised) avoids a tight coupling between the menu and
    // the broadcaster editor — either could be removed without breaking the
    // other.
    const string ZoneMaskPrefabGuid    = "1cdb50d4bc69f2341990e8fffae3e03f";
    const string ProbeVolumePrefabGuid = "0f2410a1e74b630408b29fd1a8de7b4f";

    [MenuItem("GameObject/Video Area Light/Zone Mask", false, 10)]
    static void CreateZoneMaskMenu(MenuCommand cmd)
    {
        var go = InstantiatePackagePrefab(ZoneMaskPrefabGuid, "VAL_ZoneMask");
        if (go == null) return;
        Place(go, cmd);
    }

    [MenuItem("GameObject/Video Area Light/Probe Volume", false, 11)]
    static void CreateProbeVolumeMenu(MenuCommand cmd)
    {
        var go = InstantiatePackagePrefab(ProbeVolumePrefabGuid, "VAL_ProbeVolume");
        if (go == null) return;
        Place(go, cmd);
    }

    /// <summary>
    /// Instantiates the Probe Volume prefab at a specific world position under a
    /// chosen parent. Used by the broadcaster's inspector "Add Probe Volume" button
    /// so the new volume drops near the screen instead of at world origin.
    /// </summary>
    public static GameObject CreateProbeVolumeAt(Vector3 worldPos, Transform parent)
    {
        var go = InstantiatePackagePrefab(ProbeVolumePrefabGuid, "VAL_ProbeVolume");
        if (go == null) return null;
        Undo.RegisterCreatedObjectUndo(go, "Create VAL Probe Volume");
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        Selection.activeGameObject = go;
        SceneView.lastActiveSceneView?.FrameSelected();
        return go;
    }

    static GameObject InstantiatePackagePrefab(string guid, string fallbackName)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError($"VideoAreaLight: prefab '{fallbackName}' (guid {guid}) not found in package.");
            return null;
        }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogError($"VideoAreaLight: failed to load prefab at {path}.");
            return null;
        }
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
    }

    static void Place(GameObject go, MenuCommand cmd)
    {
        // SetParentAndAlign respects the user's current selection: if they
        // right-clicked a GameObject in the Hierarchy, the new object becomes
        // its child; otherwise it lands at scene root.
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
        Selection.activeObject = go;
    }
}
