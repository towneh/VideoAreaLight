using System.IO;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class VideoAreaLightProbeVolumeBaker
{
    [MenuItem("Tools/VideoAreaLight/Bake Visibility")]
    public static void BakeMenu()
    {
        var volumes = Object.FindObjectsByType<VideoAreaLightProbeVolume>(FindObjectsSortMode.None);
        if (volumes.Length == 0)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                "No VideoAreaLightProbeVolume in the scene. Add one first, position it to cover the lit area, then bake.",
                "OK");
            return;
        }
        var src = Object.FindFirstObjectByType<VideoAreaLightSource>();
        if (src == null)
        {
            EditorUtility.DisplayDialog("VideoAreaLight",
                "No VideoAreaLightSource in the scene to bake against.",
                "OK");
            return;
        }

        // Bake every volume in the scene. Stable order: prefer the same priority/instance
        // sort the runtime uses, so the baked asset ordering matches the slot ordering.
        System.Array.Sort(volumes, (a, b) =>
        {
            int byPriority = b.priority.CompareTo(a.priority);
            if (byPriority != 0) return byPriority;
            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        });

        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int baked = 0;
        for (int i = 0; i < volumes.Length; i++)
        {
            if (!Bake(volumes[i], src, i + 1, volumes.Length))
            {
                Debug.LogWarning($"VideoAreaLight: bake-all aborted after volume {i + 1}/{volumes.Length} ({volumes[i].name}).");
                return;
            }
            baked++;
        }
        totalStopwatch.Stop();
        if (baked > 1)
            Debug.Log($"VideoAreaLight: baked all {baked} volume(s) in {totalStopwatch.Elapsed.TotalSeconds:F1}s total.");
    }

    /// <summary>
    /// Bakes a single volume. Returns true on success, false if the user cancelled.
    /// volumeIdx/volumeCount are used to format the progress bar text when
    /// part of a multi-volume bake; defaults make this a single-volume bake.
    /// </summary>
    public static bool Bake(VideoAreaLightProbeVolume volume, VideoAreaLightSource src, int volumeIdx = 1, int volumeCount = 1)
    {
        Vector3 size = volume.size;
        float vs = Mathf.Max(volume.voxelSize, 0.01f);
        int dx = Mathf.Max(2, Mathf.CeilToInt(size.x / vs));
        int dy = Mathf.Max(2, Mathf.CeilToInt(size.y / vs));
        int dz = Mathf.Max(2, Mathf.CeilToInt(size.z / vs));

        long total = (long)dx * dy * dz;

        Vector3[] corners = GetScreenCorners(src);
        Vector2[] sampleUVs = StratifyRectSamples(volume.samplesPerVoxel);
        int sampleCount = sampleUVs.Length;
        long totalRays = total * sampleCount;
        string volumeTag = volumeCount > 1 ? $"[{volumeIdx}/{volumeCount} {volume.name}] " : "";
        Debug.Log($"VideoAreaLight: {volumeTag}baking visibility {dx}×{dy}×{dz} = {total:N0} voxels × {sampleCount} rays = {totalRays:N0} raycasts (batched).");

        Transform t = volume.transform;
        Vector3 origin = t.TransformPoint(volume.center - 0.5f * size);
        Vector3 stepX = t.TransformVector(new Vector3(size.x / dx, 0f, 0f));
        Vector3 stepY = t.TransformVector(new Vector3(0f, size.y / dy, 0f));
        Vector3 stepZ = t.TransformVector(new Vector3(0f, 0f, size.z / dz));

        var data = new byte[dx * dy * dz];

        int sliceVoxels = dx * dy;
        int sliceRays = sliceVoxels * sampleCount;
        var commands = new NativeArray<RaycastCommand>(sliceRays, Allocator.Persistent);
        var hits = new NativeArray<RaycastHit>(sliceRays, Allocator.Persistent);

        var queryParams = new QueryParameters(
            volume.occluderMask,
            hitMultipleFaces: false,
            hitTriggers: QueryTriggerInteraction.Ignore,
            hitBackfaces: false);

        bool cancelled = false;
        long processed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string progressPrefix = volumeCount > 1 ? $"Volume {volumeIdx}/{volumeCount} ({volume.name}) — " : "";

        try
        {
            for (int z = 0; z < dz && !cancelled; z++)
            {
                int idx = 0;
                for (int y = 0; y < dy; y++)
                for (int x = 0; x < dx; x++)
                {
                    Vector3 vp = origin
                               + (x + 0.5f) * stepX
                               + (y + 0.5f) * stepY
                               + (z + 0.5f) * stepZ;
                    for (int s = 0; s < sampleCount; s++)
                    {
                        Vector3 sp = SampleRect(corners, sampleUVs[s].x, sampleUVs[s].y);
                        Vector3 dir = sp - vp;
                        float len = dir.magnitude;
                        if (len < 1e-4f)
                        {
                            // Degenerate (voxel coincides with screen sample). Zero distance
                            // skips the cast — matches the serial baker's `continue` path
                            // where the ray contributes 0 to blocked count.
                            commands[idx++] = new RaycastCommand(vp, Vector3.up, queryParams, 0f);
                        }
                        else
                        {
                            commands[idx++] = new RaycastCommand(vp, dir / len, queryParams, len - 0.001f);
                        }
                    }
                }

                var handle = RaycastCommand.ScheduleBatch(commands, hits, 64, default(JobHandle));
                handle.Complete();

                idx = 0;
                for (int y = 0; y < dy; y++)
                for (int x = 0; x < dx; x++)
                {
                    int blocked = 0;
                    for (int s = 0; s < sampleCount; s++)
                    {
                        // Distance > 0 filter preserves the serial baker's degenerate-ray
                        // semantics: skipped rays count as not-blocked.
                        if (commands[idx].distance > 0f && hits[idx].colliderInstanceID != 0)
                            blocked++;
                        idx++;
                    }
                    float vis = 1f - (float)blocked / sampleCount;
                    data[x + y * dx + z * dx * dy] = (byte)Mathf.Clamp(Mathf.RoundToInt(vis * 255f), 0, 255);
                }

                processed += sliceVoxels;

                if (EditorUtility.DisplayCancelableProgressBar(
                        "VideoAreaLight Bake",
                        $"{progressPrefix}Slice {z + 1}/{dz} — {processed:N0}/{total:N0} voxels — {stopwatch.Elapsed.TotalSeconds:F0}s elapsed",
                        (float)processed / total))
                {
                    cancelled = true;
                }
            }
        }
        finally
        {
            commands.Dispose();
            hits.Dispose();
            EditorUtility.ClearProgressBar();
        }

        if (cancelled)
        {
            Debug.LogWarning($"VideoAreaLight: {volumeTag}bake cancelled.");
            return false;
        }

        stopwatch.Stop();
        Debug.Log($"VideoAreaLight: {volumeTag}bake completed in {stopwatch.Elapsed.TotalSeconds:F1}s ({totalRays:N0} raycasts).");

        var tex = new Texture3D(dx, dy, dz, TextureFormat.R8, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 0,
            name = $"VAL_Visibility_{volume.gameObject.scene.name}_{volume.gameObject.name}",
        };
        tex.SetPixelData(data, 0);
        tex.Apply(false, true);

        if (volume.bakedVisibility != null && AssetDatabase.Contains(volume.bakedVisibility))
        {
            string oldPath = AssetDatabase.GetAssetPath(volume.bakedVisibility);
            AssetDatabase.DeleteAsset(oldPath);
        }

        string scenePath = volume.gameObject.scene.path;
        string assetDir = !string.IsNullOrEmpty(scenePath) ? Path.GetDirectoryName(scenePath) : "Assets";
        if (string.IsNullOrEmpty(assetDir)) assetDir = "Assets";
        // Per-volume asset name so multiple volumes in one scene don't collide.
        string sanitized = string.Join("_", volume.gameObject.name.Split(Path.GetInvalidFileNameChars()));
        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{assetDir}/VAL_Visibility_{sanitized}.asset");
        AssetDatabase.CreateAsset(tex, assetPath);
        AssetDatabase.SaveAssets();

        Undo.RecordObject(volume, "Bake VideoAreaLight Visibility");
        volume.bakedVisibility = tex;
        EditorUtility.SetDirty(volume);

        Debug.Log($"VideoAreaLight: {volumeTag}saved to {assetPath} ({dx * dy * dz / 1024f:F1} KB).");
        return true;
    }

    static Vector3 SampleRect(Vector3[] c, float u, float v)
    {
        // c is BL, BR, TR, TL; bilerp via the bottom and top edges.
        Vector3 bottom = Vector3.LerpUnclamped(c[0], c[1], u);
        Vector3 top    = Vector3.LerpUnclamped(c[3], c[2], u);
        return Vector3.LerpUnclamped(bottom, top, v);
    }

    static Vector3[] GetScreenCorners(VideoAreaLightSource src)
    {
        Transform t = src.transform;
        Vector2 hs = src.localHalfSize;
        Vector3 lBL, lBR, lTR, lTL;
        if (src.screenAxis == VideoAreaLightSource.Axis.XY)
        {
            lBL = new Vector3(-hs.x, -hs.y, 0f);
            lBR = new Vector3( hs.x, -hs.y, 0f);
            lTR = new Vector3( hs.x,  hs.y, 0f);
            lTL = new Vector3(-hs.x,  hs.y, 0f);
        }
        else
        {
            lBL = new Vector3(-hs.x, 0f, -hs.y);
            lBR = new Vector3( hs.x, 0f, -hs.y);
            lTR = new Vector3( hs.x, 0f,  hs.y);
            lTL = new Vector3(-hs.x, 0f,  hs.y);
        }
        return new[]
        {
            t.TransformPoint(lBL),
            t.TransformPoint(lBR),
            t.TransformPoint(lTR),
            t.TransformPoint(lTL),
        };
    }

    static Vector2[] StratifyRectSamples(int requested)
    {
        // Round to a perfect square so each cell gets exactly one jittered sample.
        int side = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(requested)));
        int n = side * side;
        var samples = new Vector2[n];
        var rng = new System.Random(12345);
        float invSide = 1f / side;
        int idx = 0;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            float jx = (float)rng.NextDouble();
            float jy = (float)rng.NextDouble();
            samples[idx++] = new Vector2((x + jx) * invSide, (y + jy) * invSide);
        }
        return samples;
    }
}
