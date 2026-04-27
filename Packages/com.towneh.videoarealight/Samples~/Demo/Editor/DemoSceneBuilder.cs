#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Video;

namespace VideoAreaLight.Samples.Demo
{
    // One-shot builder for the VideoAreaLight demo scene.
    //
    // Construct from the menu after importing the sample. Idempotent:
    // re-run to regenerate after a dimensions tweak. The script lives
    // alongside the scene it produces, so it doubles as the spec.
    //
    // Layout (top-down, origin at main-room floor center):
    //
    //                        +Z
    //                         ^
    //          +--------------+--------------+
    //          |          MAIN ROOM          |    interior 8 x 5 x 3.5
    //          |                             |    screen on N wall
    //          |       (DJ booth here)       |    glossy floor
    //          |                             |
    //          +-------------+-+-------------+
    //                        | | doorway     -> -X
    //                        | |
    //              corridor A| |  (south leg)
    //                        | |
    //          +-------------+ +-------------+
    //          |          corridor B          |    east leg, opens to spawn
    //          +------------------------------+
    //                                         |
    //                                  (spawn alcove, lit by separate fill)
    //
    public static class DemoSceneBuilder
    {
        const string SceneName = "Demo";
        const string MenuPath = "Tools/VideoAreaLight/Build Demo Scene";

        [MenuItem(MenuPath)]
        public static void Build()
        {
            string sampleRoot = LocateSampleRoot();
            if (sampleRoot == null)
            {
                Debug.LogError("DemoSceneBuilder: cannot locate own asset folder.");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                "Build Demo Scene",
                $"Generate materials, textures, and the demo scene at:\n\n{sampleRoot}\n\n" +
                "Will overwrite any existing scene/materials/textures at that path.",
                "Build", "Cancel")) return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string materialsPath = sampleRoot + "/Materials";
            string texturesPath = sampleRoot + "/Textures";
            EnsureFolder(materialsPath);
            EnsureFolder(texturesPath);

            var mats = CreateMaterials(materialsPath);
            if (mats.walls == null) return; // shader lookup failed; error logged inside

            var screenRT = CreateScreenRT(texturesPath);
            var placeholder = CreatePlaceholderTexture(texturesPath);
            var volumeProfile = CreateVolumeProfile(sampleRoot);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            ConfigureRenderSettings();

            var arch = new GameObject("Architecture").transform;
            BuildMainRoom(arch, mats);
            BuildCorridor(arch, mats);
            BuildSpawn(arch, mats);

            var props = new GameObject("Props").transform;
            BuildDJBooth(props, mats);

            var lighting = new GameObject("Lighting").transform;
            BuildPostFXVolume(lighting, volumeProfile);
            var src = BuildVALSource(lighting, mats, screenRT, placeholder);
            BuildZoneMask(lighting, src);
            // Encoding choice per probe is the demo's headline showcase of
            // the mix-and-match pattern: venue-wide volumes stay Scalar
            // (cheap, the package's drop-in promise) while localized fine
            // volumes around problem geometry use Quadrant (4× memory but
            // correct directional shadows on walls and per-screen-UV
            // accuracy in the floor's specular reflection).
            BuildProbeVolume(lighting, "VAL_Probe_Coarse",
                center: new Vector3(1.5f, 1.75f, -2.5f),
                size:   new Vector3(13.5f, 3.7f, 10.5f),
                voxelSize: 0.1f, priority: 0,
                encoding: VALVisibilityEncoding.Scalar);
            // Fine probe Y range extends slightly below the floor so that
            // floor fragments (at Y=0) read between two voxel layers via
            // trilinear instead of clamping to a single layer — adds soft
            // bilinear smoothing on the Y axis at the floor surface.
            // Quadrant encoding here gives the speaker shadows correct
            // height-tracking on the south wall behind the booth.
            BuildProbeVolume(lighting, "VAL_Probe_Fine",
                center: new Vector3(0f, 1.45f, -1.5f),
                size:   new Vector3(4.0f, 3.1f, 4.0f),
                voxelSize: 0.025f, priority: 10,
                encoding: VALVisibilityEncoding.Quadrant);
            // Third probe targeting the corridor's near-doorway visible
            // region — the corridor's back wall (L_WallS) and the floor/walls
            // of corridor A. The coarse probe covers this at 10cm but
            // produces muddy shadow edges; this 5cm probe tightens them.
            // Stays Scalar — directional accuracy matters less here, and
            // the coarse + corridor pair is the venue's load-bearing
            // occlusion infrastructure.
            BuildProbeVolume(lighting, "VAL_Probe_Corridor",
                center: new Vector3(0f, 1.5f, -4.5f),
                size:   new Vector3(3.0f, 3.0f, 5.0f),
                voxelSize: 0.05f, priority: 5,
                encoding: VALVisibilityEncoding.Scalar);
            // Fourth probe specifically for the booth-floor interface.
            // The fine probe at 2.5cm produces visible voxel-grid stepping
            // on the floor's shadow boundary at close viewing distance;
            // this 1cm probe sharpens that boundary in the small region
            // that actually needs it. Y range slightly below the floor for
            // bilinear smoothing. Priority 15 claims the highest slot —
            // these shadows are the most visually prominent and most
            // expensive to lose if the slot count maxes out. Quadrant
            // encoding here gives the floor's specular reflection accurate
            // per-screen-UV occlusion for camera-near floor positions.
            BuildProbeVolume(lighting, "VAL_Probe_BoothFloor",
                center: new Vector3(0f, 0.1f, -1.0f),
                size:   new Vector3(3.2f, 0.4f, 1.5f),
                voxelSize: 0.01f, priority: 15,
                encoding: VALVisibilityEncoding.Quadrant);
            BuildSpawnFill(lighting);

            BuildCamera();

            // Save first so the baker knows where to put VAL_Visibility_*.asset
            // files (it derives the asset directory from the active scene's path).
            string scenePath = sampleRoot + "/" + SceneName + ".unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            // Force the physics scene to ingest the transforms we just set.
            // In edit mode, GameObject.CreatePrimitive creates colliders but
            // Unity doesn't auto-sync transform positions to the physics
            // scene until Play starts (or this call). Without it, the
            // RaycastCommand-based bake fires against stale collider
            // positions and produces wrong visibility — shadow in the wrong
            // place that only fixes itself on a later manual re-bake.
            Physics.SyncTransforms();

            // Auto-bake both probe volumes so the scene is ready to play
            // immediately. Without this the user has to remember to run
            // Tools > VideoAreaLight > Bake Visibility separately.
            VideoAreaLightProbeVolumeBaker.BakeMenu();

            // Save again to persist each volume's bakedVisibility reference.
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"VideoAreaLight: built and baked demo scene at {scenePath}. " +
                      "Open the scene and hit Play. Screen ships with a SMPTE-bars " +
                      "placeholder; see the sample README for how to swap in your " +
                      "own video clip.");
            EditorUtility.FocusProjectWindow();
        }

        // --------------------------------------------------------------
        // Folder / asset helpers
        // --------------------------------------------------------------

        static string LocateSampleRoot()
        {
            // The script's own asset path tells us where the sample lives.
            // After import via Package Manager, that's Assets/Samples/<pkg>/<ver>/<sample>.
            var guids = AssetDatabase.FindAssets(nameof(DemoSceneBuilder) + " t:MonoScript");
            if (guids == null || guids.Length == 0) return null;
            string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            string editorFolder = Path.GetDirectoryName(scriptPath).Replace('\\', '/');
            return Path.GetDirectoryName(editorFolder).Replace('\\', '/');
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        // --------------------------------------------------------------
        // Materials
        // --------------------------------------------------------------

        struct Mats
        {
            public Material walls;
            public Material ceiling;
            public Material floor;
            public Material screen;
            public Material booth;
            public Material boothMatte;
        }

        static Mats CreateMaterials(string folder)
        {
            // Surfaces that receive the area light MUST use VideoAreaLight/Lit
            // (or Shader Graph / Poiyomi with the VAL hook). URP/Lit alone
            // doesn't read the _VAL_* globals — surfaces would render dark.
            var lit = Shader.Find("VideoAreaLight/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (lit == null)
            {
                Debug.LogError("DemoSceneBuilder: VideoAreaLight/Lit shader not found. " +
                               "Make sure the Video Area Light package is installed and compiled.");
                return default;
            }
            if (unlit == null)
            {
                Debug.LogError("DemoSceneBuilder: URP/Unlit shader not found. " +
                               "This sample requires URP 17 (Unity 6).");
                return default;
            }

            return new Mats
            {
                walls      = MakeLit(folder, "VAL_Walls",       lit, new Color(0.40f, 0.40f, 0.42f), 0.15f),
                ceiling    = MakeLit(folder, "VAL_Ceiling",     lit, new Color(0.05f, 0.05f, 0.06f), 0.05f),
                floor      = MakeLit(folder, "VAL_Floor_Gloss", lit, new Color(0.14f, 0.14f, 0.16f), 0.70f),
                booth      = MakeLit(folder, "VAL_Booth",       lit, new Color(0.04f, 0.04f, 0.05f), 0.55f),
                boothMatte = MakeLit(folder, "VAL_BoothMatte",  lit, new Color(0.02f, 0.02f, 0.02f), 0.10f),
                screen     = MakeUnlit(folder, "VAL_Screen",    unlit, Color.white),
            };
        }

        static Material MakeLit(string folder, string name, Shader s, Color baseColor, float smoothness)
        {
            string path = $"{folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            var m = existing != null ? existing : new Material(s) { name = name };
            m.shader = s;
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            if (existing == null) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            return m;
        }

        static Material MakeUnlit(string folder, string name, Shader s, Color color)
        {
            string path = $"{folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            var m = existing != null ? existing : new Material(s) { name = name };
            m.shader = s;
            m.SetColor("_BaseColor", color);
            if (existing == null) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            return m;
        }

        // --------------------------------------------------------------
        // Screen render texture + placeholder image
        // --------------------------------------------------------------

        // Always generate a fresh render texture inside the sample's
        // Textures/ folder. The sample owns all of its assets; no lookups
        // for pre-existing render textures elsewhere in the project.
        //
        // Parameters chosen for typical video content + clean reflections:
        //   1920×1080 (matches HD video clips),
        //   mipmaps on + trilinear (smooth reflections at distance/grazing),
        //   linear colour space, Clamp wrap.
        static RenderTexture CreateScreenRT(string folder)
        {
            string path = $"{folder}/VAL_Screen_RT.renderTexture";
            var existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (existing != null) return existing;

            var rt = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "VAL_Screen_RT",
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            AssetDatabase.CreateAsset(rt, path);
            return rt;
        }

        // SMPTE-style 7-bar test pattern. Recognisable as a placeholder,
        // averages to a near-neutral grey so the room reads as lit but not
        // tinted before a real video clip is wired up.
        static Texture2D CreatePlaceholderTexture(string folder)
        {
            string path = $"{folder}/VAL_ScreenPlaceholder.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
            {
                EnforceCookieTextureSettings(path);
                return existing;
            }

            const int w = 320, h = 180;

            Color32[] bars =
            {
                new Color32(192, 192, 192, 255), // grey
                new Color32(192, 192,   0, 255), // yellow
                new Color32(  0, 192, 192, 255), // cyan
                new Color32(  0, 192,   0, 255), // green
                new Color32(192,   0, 192, 255), // magenta
                new Color32(192,   0,   0, 255), // red
                new Color32(  0,   0, 192, 255), // blue
            };

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int barIdx = Mathf.Min((x * bars.Length) / w, bars.Length - 1);
                    pixels[y * w + x] = bars[barIdx];
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string absPath = Path.Combine(projectRoot, path);
            File.WriteAllBytes(absPath, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            EnforceCookieTextureSettings(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Force the texture's wrap mode to Clamp. Unity's default texture
        // import wrap mode is Repeat, which causes bilinear sampling at
        // UVs near 0/1 to pull in colours from the opposite edge of the
        // texture. For a cookie texture sampled by the area light, that
        // bleeds visible bands of unrelated colour past the reflection's
        // actual footprint on receiving surfaces. Same caution applies to
        // any user-supplied video texture or render texture.
        static void EnforceCookieTextureSettings(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            if (importer.wrapMode == TextureWrapMode.Clamp) return;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        // --------------------------------------------------------------
        // Post-processing volume profile
        //
        // ACES tonemap + a touch of bloom: the screen is HDR-bright and
        // we want it to read as "lit from within" without washing out
        // the surrounding venue. Threshold 1.0 keeps the bloom confined
        // to the emissive screen surface; the dim ambient and matte
        // walls don't contribute. Intensity 0.1 stays subtle — bloom
        // is a finishing touch here, not the headline effect.
        // --------------------------------------------------------------

        static VolumeProfile CreateVolumeProfile(string folder)
        {
            string path = $"{folder}/Demo Volume Profile.asset";
            if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(path) != null)
                AssetDatabase.DeleteAsset(path);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);

            var tonemap = profile.Add<Tonemapping>(false);
            tonemap.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            AssetDatabase.AddObjectToAsset(tonemap, profile);
            tonemap.mode.Override(TonemappingMode.ACES);

            var bloom = profile.Add<Bloom>(false);
            bloom.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            AssetDatabase.AddObjectToAsset(bloom, profile);
            bloom.threshold.Override(1f);
            bloom.intensity.Override(0.1f);

            EditorUtility.SetDirty(profile);
            return profile;
        }

        static void BuildPostFXVolume(Transform parent, VolumeProfile profile)
        {
            var go = new GameObject("PostFX_Volume");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 0;
            v.weight = 1f;
            v.sharedProfile = profile;
        }

        // --------------------------------------------------------------
        // Render settings (dark club ambient — the screen IS the light)
        // --------------------------------------------------------------

        static void ConfigureRenderSettings()
        {
            // Dark club ambient — the screen IS the light.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor     = new Color(0.03f, 0.03f, 0.04f);
            RenderSettings.ambientEquatorColor = new Color(0.02f, 0.02f, 0.025f);
            RenderSettings.ambientGroundColor  = new Color(0.01f, 0.01f, 0.012f);
            RenderSettings.ambientIntensity = 0.5f;
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            // Kill environment reflections. With a null skybox but the default
            // reflection mode still set to Skybox, Unity falls back to a
            // built-in cubemap for ambient reflections — produces a faint
            // sky-blue smear on the gloss floor that competes with the
            // screen's reflection. Zero it so the only specular reflection
            // a user sees is the area light.
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.reflectionIntensity = 0f;
        }

        // --------------------------------------------------------------
        // Architecture — interior dimensions in metres, walls 0.2m thick.
        // Walls extend outward from each room's interior face; where two
        // rooms share a boundary plane, a single wall straddles it.
        // --------------------------------------------------------------

        static GameObject MakeBox(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            // BoxCollider comes by default on a Cube primitive — required for the
            // probe volume's Physics.Raycast bake.
            return go;
        }

        // Main room interior: X=[-4,+4], Z=[-2.5,+2.5], Y=[0,3.5].
        // S wall has a doorway at X=[-1,+1], Y=[0,2.2].
        static void BuildMainRoom(Transform arch, Mats m)
        {
            var root = new GameObject("MainRoom").transform;
            root.SetParent(arch, false);

            MakeBox(root, "Floor",         new Vector3(0,    -0.1f,  0),    new Vector3(8.0f, 0.2f, 5.0f),  m.floor);
            MakeBox(root, "Ceiling",       new Vector3(0,     3.6f,  0),    new Vector3(8.0f, 0.2f, 5.0f),  m.ceiling);
            MakeBox(root, "WallN",         new Vector3(0,     1.75f, 2.6f), new Vector3(8.0f, 3.5f, 0.2f),  m.walls);
            MakeBox(root, "WallE",         new Vector3(4.1f,  1.75f, 0),    new Vector3(0.2f, 3.5f, 5.0f),  m.walls);
            MakeBox(root, "WallW",         new Vector3(-4.1f, 1.75f, 0),    new Vector3(0.2f, 3.5f, 5.0f),  m.walls);
            MakeBox(root, "WallS_Left",    new Vector3(-2.5f, 1.75f, -2.6f),new Vector3(3.0f, 3.5f, 0.2f),  m.walls);
            MakeBox(root, "WallS_Right",   new Vector3( 2.5f, 1.75f, -2.6f),new Vector3(3.0f, 3.5f, 0.2f),  m.walls);
            MakeBox(root, "WallS_Header",  new Vector3(0,     2.85f, -2.6f),new Vector3(2.0f, 1.3f, 0.2f),  m.walls);
        }

        // L-shaped corridor:
        //   A (south leg):  X=[-1,+1], Z=[-6.5,-2.5], Y=[0,3]
        //   B (east leg):   X=[+1,+5], Z=[-6.5,-4.5], Y=[0,3]
        // Corner cell shared between them at X=[-1,+1], Z=[-6.5,-4.5].
        static void BuildCorridor(Transform arch, Mats m)
        {
            var root = new GameObject("Corridor").transform;
            root.SetParent(arch, false);

            // Corridor A. The ceiling and side walls are trimmed at Z=-2.7
            // (the south face of the main room's S wall) rather than Z=-2.5
            // (the interior face). Without the trim, the corridor structure
            // pushes into the main-room wall body and z-fights along the
            // doorway band — most visibly on the ceiling viewed through
            // the doorway. The floor stays at Z=-2.5 because the two floors
            // only touch at that plane (no volume overlap with any wall).
            MakeBox(root, "A_Floor",   new Vector3(0,    -0.1f, -4.5f), new Vector3(2.0f, 0.2f, 4.0f), m.floor);
            MakeBox(root, "A_Ceiling", new Vector3(0,     3.1f, -4.6f), new Vector3(2.0f, 0.2f, 3.8f), m.ceiling);
            MakeBox(root, "A_WallW",   new Vector3(-1.1f, 1.5f, -4.6f), new Vector3(0.2f, 3.0f, 3.8f), m.walls);
            // A's east wall covers only the segment north of the corner. Its
            // south end is also trimmed (Z=-4.3) to abut B_WallN's interior
            // face rather than its body, avoiding z-fight at the L corner.
            MakeBox(root, "A_WallE",   new Vector3(1.1f,  1.5f, -3.5f), new Vector3(0.2f, 3.0f, 1.6f), m.walls);

            // Corridor B
            MakeBox(root, "B_Floor",   new Vector3(3.0f, -0.1f, -5.5f), new Vector3(4.0f, 0.2f, 2.0f), m.floor);
            MakeBox(root, "B_Ceiling", new Vector3(3.0f,  3.1f, -5.5f), new Vector3(4.0f, 0.2f, 2.0f), m.ceiling);
            MakeBox(root, "B_WallN",   new Vector3(3.0f,  1.5f, -4.4f), new Vector3(4.0f, 3.0f, 0.2f), m.walls);

            // Shared south wall of the L: spans X=[-1,+5] at Z=-6.6.
            MakeBox(root, "L_WallS",   new Vector3(2.0f,  1.5f, -6.6f), new Vector3(6.0f, 3.0f, 0.2f), m.walls);
        }

        // Spawn alcove: X=[+5,+8], Z=[-7,-4], Y=[0,3].
        // West wall opens onto corridor B at Z=[-6.5,-4.5], Y=[0,2.2].
        static void BuildSpawn(Transform arch, Mats m)
        {
            var root = new GameObject("Spawn").transform;
            root.SetParent(arch, false);

            MakeBox(root, "Floor",       new Vector3(6.5f, -0.1f, -5.5f), new Vector3(3.0f, 0.2f, 3.0f), m.walls); // matte, intentionally not gloss — different room aesthetic
            MakeBox(root, "Ceiling",     new Vector3(6.5f,  3.1f, -5.5f), new Vector3(3.0f, 0.2f, 3.0f), m.ceiling);
            MakeBox(root, "WallE",       new Vector3(8.1f,  1.5f, -5.5f), new Vector3(0.2f, 3.0f, 3.0f), m.walls);
            MakeBox(root, "WallN",       new Vector3(6.5f,  1.5f, -3.9f), new Vector3(3.0f, 3.0f, 0.2f), m.walls);
            MakeBox(root, "WallS",       new Vector3(6.5f,  1.5f, -7.1f), new Vector3(3.0f, 3.0f, 0.2f), m.walls);

            // West wall remnants around the opening to corridor B. The North
            // and South remnants are trimmed at the boundaries with corridor
            // B's north wall (Z=-4.3) and the L's south wall (Z=-6.7) so
            // their bodies abut rather than overlap, eliminating z-fight.
            MakeBox(root, "WallW_Header",new Vector3(4.9f,  2.6f, -5.5f), new Vector3(0.2f, 0.8f, 2.0f), m.walls);
            MakeBox(root, "WallW_South", new Vector3(4.9f,  1.1f, -6.85f),new Vector3(0.2f, 2.2f, 0.3f), m.walls);
            MakeBox(root, "WallW_North", new Vector3(4.9f,  1.1f, -4.15f),new Vector3(0.2f, 2.2f, 0.3f), m.walls);
        }

        // --------------------------------------------------------------
        // DJ booth — three primitives; concave silhouette so the fine
        // probe volume has something to cast a shadow with.
        // --------------------------------------------------------------

        static void BuildDJBooth(Transform props, Mats m)
        {
            var root = new GameObject("DJBooth").transform;
            root.SetParent(props, false);

            MakeBox(root, "Lectern",  new Vector3( 0,    0.5f,  -1.0f), new Vector3(1.5f, 1.0f, 0.6f), m.booth);
            MakeBox(root, "SpeakerL", new Vector3(-1.2f, 0.75f, -1.0f), new Vector3(0.5f, 1.5f, 0.5f), m.boothMatte);
            MakeBox(root, "SpeakerR", new Vector3( 1.2f, 0.75f, -1.0f), new Vector3(0.5f, 1.5f, 0.5f), m.boothMatte);
        }

        // --------------------------------------------------------------
        // Lighting
        // --------------------------------------------------------------

        static VideoAreaLightSource BuildVALSource(Transform parent, Mats m, RenderTexture screenRT, Texture2D placeholder)
        {
            // CRITICAL: VideoAreaLightSource and the Quad MeshRenderer MUST
            // live on the same GameObject. The broadcaster builds its
            // world-space corners via transform.TransformPoint(localHalfSize)
            // using ITS OWN transform — so its scale has to match the Quad
            // mesh it's emitting from. Splitting them onto parent + child
            // silently desyncs the lit area from the rendered screen
            // (broadcaster reads parent scale 1×1 while the Quad renders at
            // child scale; reflection footprint shrinks to 1m).
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "VAL_Source";
            quad.transform.SetParent(parent, false);
            // Slightly inside the N wall (interior face at Z=+2.5) so the
            // screen quad doesn't z-fight with the wall surface.
            quad.transform.position = new Vector3(0, 1.6f, 2.4f);
            // Default Unity Quad's visible face is on -transform.forward, so
            // identity rotation already faces -Z (into the room). The
            // broadcaster's flipNormal=true mirrors its emit direction to
            // match the visible face.
            quad.transform.localRotation = Quaternion.identity;
            quad.transform.localScale = new Vector3(4.8f, 2.7f, 1.5f); // 16:9, sized for the room
            quad.GetComponent<MeshRenderer>().sharedMaterial = m.screen;
            // Strip the Quad's MeshCollider so probe-bake rays don't hit it.
            var quadCollider = quad.GetComponent<Collider>();
            if (quadCollider != null) Object.DestroyImmediate(quadCollider);

            var val = quad.AddComponent<VideoAreaLightSource>();
            val.localHalfSize = new Vector2(0.5f, 0.5f); // default Quad mesh extents
            val.screenAxis = VideoAreaLightSource.Axis.XY;
            val.flipNormal = true;
            val.maxIntensity = 60f;       // club-bright
            val.intensityCurve = 1.5f;
            val.saturationBoost = 1.4f;
            val.zoneFeather = 0.3f;

            // Both fields point at the placeholder so the screen has visible
            // signal at import even before any video is wired up. Average
            // colour of the SMPTE bars produces neutral-ish lighting in the
            // room. When the user adds a clip, they swap both fields to the
            // render texture (documented in the sample README).
            val.videoTexture = placeholder;
            m.screen.SetTexture("_BaseMap", placeholder);

            // Pre-configured VideoPlayer waiting on a clip. Drop a clip on
            // VideoPlayer.clip and hit play — it'll write to screenRT
            // automatically.
            var vp = quad.AddComponent<VideoPlayer>();
            vp.playOnAwake = true;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = screenRT;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.source = VideoSource.VideoClip; // clip stays null until user provides one

            return val;
        }

        static void BuildZoneMask(Transform parent, VideoAreaLightSource src)
        {
            var go = new GameObject("VAL_ZoneMask");
            go.transform.SetParent(parent, false);
            // Sized to cover the venue's bounding rectangle (main room +
            // corridor + spawn, with a small overscan). This matches the
            // package's design intent: zone mask = venue boundary, probe
            // volumes handle intra-venue visibility. With this sizing, the
            // corridor's far wall correctly lights up where it has line of
            // sight through the doorway (the coarse probe + analytic light
            // do the work) and stays dark deeper in where the bake records
            // no visibility.
            go.transform.position = new Vector3(2f, 1.75f, -2.25f);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(12.4f, 3.7f, 9.7f);
            src.zoneVolume = box;
        }

        static void BuildProbeVolume(Transform parent, string name,
            Vector3 center, Vector3 size, float voxelSize, int priority,
            VALVisibilityEncoding encoding = VALVisibilityEncoding.Scalar)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            var v = go.AddComponent<VideoAreaLightProbeVolume>();
            v.center = Vector3.zero; // we move via transform.position, not local center
            v.size = size;
            v.voxelSize = voxelSize;
            // Quadrant encoding's per-quadrant variance scales as 1/sqrt(N/4)
            // — needs roughly 4× the sample count of Scalar to match per-bucket
            // density. Tune up further if directional artifacts surface.
            v.samplesPerVoxel = encoding == VALVisibilityEncoding.Quadrant ? 64 : 16;
            v.priority = priority;
            v.encoding = encoding;
        }

        static void BuildSpawnFill(Transform parent)
        {
            var go = new GameObject("SpawnFill");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(6.5f, 2.8f, -5.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0, 0); // point straight down
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = new Color(1f, 0.85f, 0.65f);
            // Calibrated for URP 17's default Physical Light Units (lumens).
            // ~120 lumens reads as a small accent fixture. If your project
            // has Use Physical Light Units = OFF, drop this to 1.5–3.
            l.intensity = 120f;
            l.range = 4f;
            l.spotAngle = 110f;
            l.innerSpotAngle = 70f;
            l.shadows = LightShadows.Soft;
        }

        static void BuildCamera()
        {
            var go = new GameObject("Camera");
            go.tag = "MainCamera";
            // Just inside the main-room doorway, looking N at the booth + screen.
            // Walk to the corridor at runtime to see the no-leak demo.
            go.transform.position = new Vector3(0f, 1.7f, -2.0f);
            go.transform.rotation = Quaternion.Euler(5f, 0f, 0f);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            // URP cameras default to renderPostProcessing = false, so the
            // PostFX_Volume's profile would be inert without this flag.
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            go.AddComponent<AudioListener>();
        }
    }
}
#endif
