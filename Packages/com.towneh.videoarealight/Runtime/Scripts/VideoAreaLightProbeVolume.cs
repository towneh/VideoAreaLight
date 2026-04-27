using System.Collections.Generic;
using UnityEngine;

public enum VALVisibilityEncoding
{
    /// <summary>
    /// Single scalar value per voxel = fraction of the screen rectangle
    /// visible. 1 byte/voxel; cheapest and the default. Loses information
    /// about *which part* of the screen is occluded, so partial-occlusion
    /// shadows can over-extend on receiving surfaces (a wall in scalar shadow
    /// often reaches taller than the actual occluder) and the floor's
    /// specular reflection can't track which point of the screen is blocked
    /// for the reflected ray.
    /// </summary>
    Scalar,

    /// <summary>
    /// Visibility per screen quadrant — R = bottom-left, G = bottom-right,
    /// B = top-left, A = top-right. 4 bytes/voxel (4× the storage of Scalar).
    /// At runtime the shader bilerps the four channels at the fragment's
    /// relevant screen UV (projection of worldPos for diffuse, MRP UV for
    /// specular). Produces correct directional shadows for surfaces facing
    /// the source — the typical wall-shadow case where Scalar fails. Bake
    /// time also ~4× because samplesPerVoxel needs to be higher (~64) to
    /// keep per-quadrant variance low.
    /// </summary>
    Quadrant,
}

[ExecuteAlways]
[DisallowMultipleComponent]
public class VideoAreaLightProbeVolume : MonoBehaviour
{
    public const int MaxActive = 4;

    [Header("Region")]
    [Tooltip("Where the box sits relative to this GameObject. Usually leave at (0, 0, 0) and move the GameObject itself.")]
    public Vector3 center = Vector3.zero;
    [Tooltip("How big the box is, in metres. Resize to cover the area you want refined occlusion in. The GameObject's rotation and scale apply on top, so you can tilt the box to match angled geometry.")]
    public Vector3 size = new Vector3(10f, 3f, 10f);

    [Header("Bake Quality")]
    [Tooltip("Voxel size in metres. Smaller means sharper detail and longer bakes. 0.1 is great for room-scale; drop to 0.05 around fine geometry like steps or thin walls.")]
    [Min(0.01f)] public float voxelSize = 0.1f;
    [Tooltip("How many rays each voxel fires at the screen during baking. More gives smoother edges; 16 is a good default for Scalar encoding. For Quadrant, use ~64 (rays distribute across 4 buckets).")]
    [Range(1, 256)] public int samplesPerVoxel = 16;
    [Tooltip("Which physics layers count as light blockers — walls, floors, props. If your screen mesh has a collider, exclude its layer here.")]
    public LayerMask occluderMask = ~0;

    [Header("Slot")]
    [Tooltip("Which volumes win when more than four are in the scene. Higher-priority volumes are guaranteed to contribute; the rest are skipped. Useful for protecting fine-detail volumes from being dropped.")]
    public int priority = 0;

    [Header("Encoding")]
    [Tooltip("Scalar (default) is 1 byte per voxel and treats every direction equally. Quadrant is 4 bytes per voxel and stores per-screen-quadrant visibility, which produces correct directional shadows on surfaces facing the source. Mix per volume — typical pattern is Scalar for the venue-wide coarse volume and Quadrant for the fine volumes around problem geometry where directional accuracy matters.")]
    public VALVisibilityEncoding encoding = VALVisibilityEncoding.Scalar;

    [Header("Bake Result")]
    [Tooltip("The baked visibility texture. Click 'Bake This Volume' below to generate it, or use Tools > VideoAreaLight > Bake Visibility to bake every volume in the scene.")]
    public Texture3D bakedVisibility;

    static readonly int _VisCountId = Shader.PropertyToID("_VAL_VisCount");

    static readonly int[] _MatrixIds =
    {
        Shader.PropertyToID("_VAL_WorldToVisUVW0"),
        Shader.PropertyToID("_VAL_WorldToVisUVW1"),
        Shader.PropertyToID("_VAL_WorldToVisUVW2"),
        Shader.PropertyToID("_VAL_WorldToVisUVW3"),
    };
    static readonly int[] _TexIds =
    {
        Shader.PropertyToID("_VAL_VisTex0"),
        Shader.PropertyToID("_VAL_VisTex1"),
        Shader.PropertyToID("_VAL_VisTex2"),
        Shader.PropertyToID("_VAL_VisTex3"),
    };
    static readonly int[] _ModeIds =
    {
        Shader.PropertyToID("_VAL_VisMode0"),
        Shader.PropertyToID("_VAL_VisMode1"),
        Shader.PropertyToID("_VAL_VisMode2"),
        Shader.PropertyToID("_VAL_VisMode3"),
    };

    // Active volume registry. The "leader" (priority-sorted head) is the only
    // instance that pushes globals each frame; everyone else early-outs in
    // LateUpdate. This keeps SetGlobal calls O(1) per frame regardless of how
    // many volumes exist, and resolves push-order non-determinism that would
    // otherwise depend on Unity's MonoBehaviour update ordering.
    static readonly List<VideoAreaLightProbeVolume> _Active = new List<VideoAreaLightProbeVolume>();

    void OnEnable()
    {
        if (!_Active.Contains(this)) _Active.Add(this);
    }

    void OnDisable()
    {
        _Active.Remove(this);
        if (_Active.Count == 0)
        {
            // Last volume gone: zero the count so stale slot textures don't
            // get sampled. Texture references stay bound but VisCount = 0
            // skips all four slot blocks in the shader.
            Shader.SetGlobalInteger(_VisCountId, 0);
        }
    }

    void LateUpdate()
    {
        if (_Active.Count == 0) return;

        _Active.Sort(CompareForSlot);

        // Only the leader pushes. Cheap early-out for the other N-1 volumes.
        if (_Active[0] != this) return;

        int n = Mathf.Min(_Active.Count, MaxActive);
        int pushed = 0;
        for (int i = 0; i < n; i++)
        {
            var v = _Active[i];
            if (v.bakedVisibility == null) continue;
            v.PushSlot(pushed);
            pushed++;
        }
        Shader.SetGlobalInteger(_VisCountId, pushed);
    }

    static int CompareForSlot(VideoAreaLightProbeVolume a, VideoAreaLightProbeVolume b)
    {
        int byPriority = b.priority.CompareTo(a.priority); // descending
        if (byPriority != 0) return byPriority;
        return a.GetInstanceID().CompareTo(b.GetInstanceID());
    }

    void PushSlot(int slot)
    {
        // Compose world → 0..1 UVW. In column-vector convention (HLSL mul(M, v)):
        //   uvw = translate(0.5) * scale(1/size) * translate(-center) * worldToLocal * worldPos
        Vector3 invSize = new Vector3(
            1f / Mathf.Max(size.x, 1e-5f),
            1f / Mathf.Max(size.y, 1e-5f),
            1f / Mathf.Max(size.z, 1e-5f));
        Matrix4x4 m = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f))
                    * Matrix4x4.Scale(invSize)
                    * Matrix4x4.Translate(-center)
                    * transform.worldToLocalMatrix;

        Shader.SetGlobalTexture(_TexIds[slot], bakedVisibility);
        Shader.SetGlobalMatrix(_MatrixIds[slot], m);
        Shader.SetGlobalFloat(_ModeIds[slot], (float)encoding);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = bakedVisibility != null
            ? new Color(0.4f, 1f, 0.6f, 0.8f)   // baked: green
            : new Color(1f, 0.6f, 0.2f, 0.8f);  // unbaked: orange
        Gizmos.DrawWireCube(center, size);
    }
}
