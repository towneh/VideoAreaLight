using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VideoAreaLightProbeVolume))]
[CanEditMultipleObjects]
public class VideoAreaLightProbeVolumeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Top help box: the 60-second mental model, mirroring the broadcaster's
        // help box from the other end of the relationship.
        EditorGUILayout.HelpBox(
            "Probe Volumes catch occlusion from awkward indoor geometry — steps, mezzanines, pillars — that a Zone Mask alone can't handle.\n\n" +
            "Reach for one only when a Zone Mask leaves visible leaks. Up to four can run at once; you can mix one large coarse volume with smaller fine-detail volumes around the spots that need it.",
            MessageType.Info);

        EditorGUILayout.Space();

        DrawDefaultInspector();

        EditorGUILayout.Space();

        int selected = targets != null ? targets.Length : 0;
        if (selected == 0) return;

        string label = selected > 1
            ? $"Bake {selected} Selected Volumes"
            : "Bake This Volume";
        if (GUILayout.Button(label, GUILayout.Height(28)))
        {
            BakeSelected();
        }

        // For single-select, surface the existing asset path so users can see
        // where the bake will land (or already lives) without hunting through
        // the Project window.
        if (selected == 1)
        {
            var v = (VideoAreaLightProbeVolume)targets[0];
            if (v.bakedVisibility != null)
            {
                string path = AssetDatabase.GetAssetPath(v.bakedVisibility);
                if (!string.IsNullOrEmpty(path))
                {
                    EditorGUILayout.LabelField("Bake asset", path, EditorStyles.miniLabel);
                }
            }
        }
    }

    void BakeSelected()
    {
        var src = Object.FindFirstObjectByType<VideoAreaLightSource>();
        if (src == null)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                "No VideoAreaLightSource in the scene to bake against.",
                "OK");
            return;
        }

        // Sort with the same key the runtime uses so slot assignment is stable
        // — baking volumes in the order they'll be pushed avoids confusion when
        // diagnosing which asset corresponds to which slot.
        var typed = new VideoAreaLightProbeVolume[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            typed[i] = (VideoAreaLightProbeVolume)targets[i];
        System.Array.Sort(typed, (a, b) =>
        {
            int byPriority = b.priority.CompareTo(a.priority);
            if (byPriority != 0) return byPriority;
            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < typed.Length; i++)
        {
            if (!VideoAreaLightProbeVolumeBaker.Bake(typed[i], src, i + 1, typed.Length))
            {
                Debug.LogWarning($"VideoAreaLight: bake aborted after volume {i + 1}/{typed.Length}.");
                return;
            }
        }
        sw.Stop();
        if (typed.Length > 1)
            Debug.Log($"VideoAreaLight: baked {typed.Length} selected volume(s) in {sw.Elapsed.TotalSeconds:F1}s total.");
    }
}
