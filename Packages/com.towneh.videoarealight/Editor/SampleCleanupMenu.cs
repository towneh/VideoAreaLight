#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

// Unity Package Manager doesn't auto-clean a package's previously-imported
// samples on upgrade — it stashes each version's samples under
// Assets/Samples/<displayName>/<version>/, and old version folders linger
// indefinitely. This menu finds every <version> subfolder under the
// package's samples root that isn't the current version and offers to
// delete them after explicit confirmation.
public static class SampleCleanupMenu
{
    const string PackageName = "com.towneh.videoarealight";
    const string MenuPath = "Tools/VideoAreaLight/Clean Up Old Sample Imports";

    [MenuItem(MenuPath)]
    public static void Run()
    {
        var info = PackageInfo.FindForPackageName(PackageName);
        if (info == null)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                $"Could not resolve package info for '{PackageName}'. Is the package installed?",
                "OK");
            return;
        }

        string samplesRoot = $"Assets/Samples/{info.displayName}";
        if (!AssetDatabase.IsValidFolder(samplesRoot))
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                $"No imported samples found at {samplesRoot}. Nothing to clean.",
                "OK");
            return;
        }

        // Each version of the package has its samples under
        // Assets/Samples/<displayName>/<version>/. Anything whose folder
        // name doesn't match the current version is a candidate for
        // cleanup.
        var subfolders = AssetDatabase.GetSubFolders(samplesRoot);
        var stale = new List<string>();
        foreach (var sub in subfolders)
        {
            string name = Path.GetFileName(sub);
            if (name != info.version) stale.Add(sub);
        }

        if (stale.Count == 0)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                $"No old sample imports found. The current version ({info.version}) " +
                "is the only one present.",
                "OK");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {stale.Count} old sample import(s) under {samplesRoot}:");
        sb.AppendLine();
        foreach (var path in stale)
            sb.AppendLine($"  - {Path.GetFileName(path)}");
        sb.AppendLine();
        sb.AppendLine($"Current package version is {info.version}. The folders above were imported by previous versions.");
        sb.AppendLine();
        sb.AppendLine("If you've made local edits to any imported sample copy, cancel and back them up first.");

        if (!EditorUtility.DisplayDialog("Clean Up Old Sample Imports",
            sb.ToString(),
            "Delete", "Cancel")) return;

        int deleted = 0;
        foreach (var path in stale)
        {
            if (AssetDatabase.DeleteAsset(path)) deleted++;
            else Debug.LogWarning($"VideoAreaLight: failed to delete {path}.");
        }
        AssetDatabase.Refresh();

        Debug.Log($"VideoAreaLight: removed {deleted}/{stale.Count} old sample import folder(s) from {samplesRoot}.");
    }
}
#endif
