using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VideoAreaLightSource))]
[CanEditMultipleObjects]
public class VideoAreaLightSourceEditor : Editor
{
    // GUIDs of the package's prefabs. Hard-coding by GUID survives renames and
    // doesn't depend on AssetDatabase folder paths (which differ between
    // package-installed and embedded layouts).
    const string ZoneMaskPrefabGuid    = "1cdb50d4bc69f2341990e8fffae3e03f";
    const string ProbeVolumePrefabGuid = "0f2410a1e74b630408b29fd1a8de7b4f";

    public override void OnInspectorGUI()
    {
        // Top help box: the 60-second mental model, same wording as the
        // README and Occlusion.md so users hear the same story everywhere.
        EditorGUILayout.HelpBox(
            "This is the screen broadcaster. Drop in a video texture below to start lighting your scene.\n\n" +
            "To stop reflections leaking into other rooms, add a Zone Mask — that handles most cases. Add Probe Volumes only if specific spots inside your room still leak.",
            MessageType.Info);

        EditorGUILayout.Space();

        DrawDefaultInspector();

        // Affordances are single-target only — multi-selecting broadcasters
        // (rare; the package is single-emitter anyway) just gets the bare
        // default fields.
        if (targets.Length != 1) return;
        var src = (VideoAreaLightSource)target;

        EditorGUILayout.Space(8);
        DrawZoneMaskAffordance(src);

        EditorGUILayout.Space(8);
        DrawProbeVolumeListing(src);
    }

    void DrawZoneMaskAffordance(VideoAreaLightSource src)
    {
        if (src.zoneVolume != null)
        {
            // Already assigned: subtle hint that the zone is the heavy lifter,
            // plus a quick-jump for editing it.
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Zone Mask active.", EditorStyles.miniLabel);
                if (GUILayout.Button("Select Zone", GUILayout.MaxWidth(100)))
                    Selection.activeGameObject = src.zoneVolume.gameObject;
            }
            return;
        }

        EditorGUILayout.HelpBox(
            "No Zone Mask yet. Without one, reflections from this screen can land on surfaces anywhere in the world. Click below to add one.",
            MessageType.Warning);

        if (GUILayout.Button("Create Zone Mask", GUILayout.Height(24)))
        {
            CreateZoneMaskFor(src);
        }
    }

    void CreateZoneMaskFor(VideoAreaLightSource src)
    {
        var prefab = LoadPrefab(ZoneMaskPrefabGuid);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                "Couldn't find the Zone Mask prefab in the package. Add a BoxCollider GameObject manually and assign it to Zone Volume.",
                "OK");
            return;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Create VAL Zone Mask");

        // Parent next to the broadcaster (sibling) so the venue's overall
        // hierarchy doesn't get cluttered under the screen mesh, but spawn
        // at the broadcaster's world position for an immediately-visible
        // gizmo box.
        if (src.transform.parent != null)
            go.transform.SetParent(src.transform.parent, false);
        go.transform.position = src.transform.position;

        Undo.RecordObject(src, "Assign Zone Mask");
        src.zoneVolume = go.GetComponent<BoxCollider>();
        EditorUtility.SetDirty(src);

        Selection.activeGameObject = go;
        SceneView.lastActiveSceneView?.FrameSelected();
    }

    void DrawProbeVolumeListing(VideoAreaLightSource src)
    {
        EditorGUILayout.LabelField("Probe Volumes (scene-wide)", EditorStyles.boldLabel);

        var volumes = Object.FindObjectsByType<VideoAreaLightProbeVolume>(FindObjectsSortMode.None);
        if (volumes.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "No Probe Volumes in this scene. Add one only if a Zone Mask leaves visible leaks around indoor geometry like steps, mezzanines, or pillars.",
                MessageType.None);
        }
        else
        {
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < volumes.Length; i++)
                {
                    var v = volumes[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.ObjectField(v.gameObject.name, v, typeof(VideoAreaLightProbeVolume), true);
                        if (GUILayout.Button("Select", GUILayout.MaxWidth(60)))
                            Selection.activeGameObject = v.gameObject;
                    }
                }
            }
            EditorGUILayout.LabelField($"{volumes.Length} in scene (up to 4 contribute at once).", EditorStyles.miniLabel);
        }

        if (GUILayout.Button("Add Probe Volume", GUILayout.Height(24)))
        {
            VideoAreaLightMenu.CreateProbeVolumeAt(src.transform.position, src.transform.parent);
        }
    }

    static GameObject LoadPrefab(string guid)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }
}
