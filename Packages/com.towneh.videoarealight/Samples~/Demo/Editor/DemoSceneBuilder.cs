#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Video;

namespace VideoAreaLight.Samples.Demo
{
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

            var floorNormal = CreateNoiseNormalMap(texturesPath, "VAL_Floor_Normal",
                frequency: 18f, strength: 1.2f, turbulenceMix: 0f, seed: 1337);
            var wallNormal = CreateNoiseNormalMap(texturesPath, "VAL_Wall_Normal",
                frequency: 14f, strength: 1.5f, turbulenceMix: 0.6f, seed: 7331);
            var mats = CreateMaterials(materialsPath, floorNormal, wallNormal);
            if (mats.walls == null) return;

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
            BuildPlayer(props, materialsPath);

            var lighting = new GameObject("Lighting").transform;
            BuildPostFXVolume(lighting, volumeProfile);
            var src = BuildVALSource(lighting, mats, screenRT, placeholder);
            BuildZoneMask(lighting, src);
            // Demo intentionally mixes encodings: Scalar for venue-wide,
            // Quadrant for problem geometry where directional accuracy matters.
            BuildProbeVolume(lighting, "VAL_Probe_Coarse",
                center: new Vector3(1.5f, 1.75f, -2.5f),
                size:   new Vector3(13.5f, 3.7f, 10.5f),
                voxelSize: 0.1f, priority: 0,
                encoding: VALVisibilityEncoding.Scalar);
            // Y range extends below floor so floor fragments read between two
            // voxel layers via trilinear instead of clamping to one.
            BuildProbeVolume(lighting, "VAL_Probe_Fine",
                center: new Vector3(0f, 1.45f, -1.5f),
                size:   new Vector3(4.0f, 3.1f, 4.0f),
                voxelSize: 0.025f, priority: 10,
                encoding: VALVisibilityEncoding.Quadrant);
            BuildProbeVolume(lighting, "VAL_Probe_Corridor",
                center: new Vector3(0f, 1.5f, -4.5f),
                size:   new Vector3(3.0f, 3.0f, 5.0f),
                voxelSize: 0.05f, priority: 5,
                encoding: VALVisibilityEncoding.Scalar);
            BuildProbeVolume(lighting, "VAL_Probe_BoothFloor",
                center: new Vector3(0f, 0.1f, -1.0f),
                size:   new Vector3(3.2f, 0.4f, 1.5f),
                voxelSize: 0.01f, priority: 15,
                encoding: VALVisibilityEncoding.Quadrant);
            BuildSpawnFill(lighting);

            BuildCamera();

            // Save before bake — baker derives VAL_Visibility_*.asset directory
            // from the active scene's path.
            string scenePath = sampleRoot + "/" + SceneName + ".unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            // Edit-mode CreatePrimitive doesn't auto-sync transforms into the
            // physics scene; without this the bake hits stale collider positions.
            Physics.SyncTransforms();

            VideoAreaLightProbeVolumeBaker.BakeMenu();

            // Persist each volume's bakedVisibility reference.
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"VideoAreaLight: built and baked demo scene at {scenePath}. " +
                      "Open the scene and hit Play. Screen ships with a SMPTE-bars " +
                      "placeholder; see the sample README for how to swap in your " +
                      "own video clip.");
            EditorUtility.FocusProjectWindow();
        }

        static string LocateSampleRoot()
        {
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

        struct Mats
        {
            public Material walls;
            public Material ceiling;
            public Material floor;
            public Material screen;
            public Material booth;
            public Material boothMatte;
        }

        static Mats CreateMaterials(string folder, Texture2D floorNormal, Texture2D wallNormal)
        {
            // Surfaces receiving the area light MUST use VideoAreaLight/Lit
            // (or SG / Poiyomi with the VAL hook); URP/Lit alone doesn't read
            // the _VAL_* globals.
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

            var mats = new Mats
            {
                walls      = MakeLit(folder, "VAL_Walls",       lit, new Color(0.40f, 0.40f, 0.42f), 0.15f),
                ceiling    = MakeLit(folder, "VAL_Ceiling",     lit, new Color(0.05f, 0.05f, 0.06f), 0.05f),
                floor      = MakeLit(folder, "VAL_Floor_Gloss", lit, new Color(0.14f, 0.14f, 0.16f), 0.70f),
                booth      = MakeLit(folder, "VAL_Booth",       lit, new Color(0.04f, 0.04f, 0.05f), 0.55f),
                boothMatte = MakeLit(folder, "VAL_BoothMatte",  lit, new Color(0.02f, 0.02f, 0.02f), 0.10f),
                screen     = MakeUnlit(folder, "VAL_Screen",    unlit, Color.white),
            };

            mats.floor.SetTexture("_BumpMap", floorNormal);
            mats.floor.SetFloat("_BumpScale", 0.15f);
            mats.floor.SetTextureScale("_BumpMap", new Vector2(8f, 5f));
            mats.floor.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(mats.floor);

            mats.walls.SetTexture("_BumpMap", wallNormal);
            mats.walls.SetFloat("_BumpScale", 0.2f);
            mats.walls.SetTextureScale("_BumpMap", new Vector2(2f, 0.875f));
            mats.walls.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(mats.walls);

            return mats;
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

        // R16G16B16A16_UNorm: cookie sample is multiplied through HDR intensity;
        // 8-bit pushes smooth gradients into visible banding.
        static RenderTexture CreateScreenRT(string folder)
        {
            string path = $"{folder}/VAL_Screen_RT.renderTexture";
            if (AssetDatabase.LoadAssetAtPath<RenderTexture>(path) != null)
                AssetDatabase.DeleteAsset(path);

            var rt = new RenderTexture(1920, 1080, 0, GraphicsFormat.R16G16B16A16_UNorm)
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

        // Cookie textures must use Clamp wrap. Default Repeat causes bilinear
        // sampling near UV 0/1 to bleed colour from the opposite edge into
        // reflections.
        static void EnforceCookieTextureSettings(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            if (importer.wrapMode == TextureWrapMode.Clamp) return;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        // Procedural normal map: domain-warped multi-octave noise + central-
        // difference Sobel. turbulenceMix blends abs(2x-1) ridges with smooth
        // fBm: 0 = smooth, 1 = pure turbulence.
        static Texture2D CreateNoiseNormalMap(string folder, string fileName,
            float frequency, float strength, float turbulenceMix, int seed)
        {
            string path = $"{folder}/{fileName}.png";

            const int size = 512;
            const int octaves = 5;
            const float lacunarity = 2f;
            const float gain = 0.5f;
            const float warpAmount = 0.04f;

            var rng = new System.Random(seed);
            Vector2 noiseOffset = new Vector2(
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 1000f);
            Vector2 warpOffset = new Vector2(
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 1000f);

            float[] hgt = new float[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    float v = (float)y / size;

                    float wx = Mathf.PerlinNoise(u * 2f + warpOffset.x,         v * 2f + warpOffset.y)         - 0.5f;
                    float wy = Mathf.PerlinNoise(u * 2f + warpOffset.x + 100f,  v * 2f + warpOffset.y + 100f)  - 0.5f;
                    float wu = u + wx * warpAmount;
                    float wv = v + wy * warpAmount;

                    float amp = 1f, freq = frequency, sum = 0f, norm = 0f;
                    for (int o = 0; o < octaves; o++)
                    {
                        float n = Mathf.PerlinNoise(wu * freq + noiseOffset.x,
                                                    wv * freq + noiseOffset.y);
                        sum  += amp * Mathf.Lerp(n, Mathf.Abs(n * 2f - 1f), turbulenceMix);
                        norm += amp;
                        amp  *= gain;
                        freq *= lacunarity;
                    }
                    hgt[y * size + x] = sum / norm;
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int xm = (x - 1 + size) % size, xp = (x + 1) % size;
                    int ym = (y - 1 + size) % size, yp = (y + 1) % size;
                    float dx = hgt[y * size + xp] - hgt[y * size + xm];
                    float dy = hgt[yp * size + x] - hgt[ym * size + x];
                    Vector3 n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                    px[y * size + x] = new Color32(
                        (byte)((n.x * 0.5f + 0.5f) * 255f),
                        (byte)((n.y * 0.5f + 0.5f) * 255f),
                        (byte)((n.z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string absPath = Path.Combine(projectRoot, path);
            File.WriteAllBytes(absPath, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // NormalMap import type: BC5/DXT5nm + UnpackNormal in the shader.
            // sRGB off — direction data, not perceptual colour.
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

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

            // Threshold 1.0 confines bloom to the emissive screen surface;
            // dim ambient walls don't contribute.
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

            // Kill env reflections. With a null skybox but the default
            // reflection mode still Skybox, Unity falls back to a built-in
            // cubemap that smears sky-blue on the gloss floor.
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.reflectionIntensity = 0f;
        }

        // Architecture — interior dimensions in metres, walls 0.2m thick.

        static GameObject MakeBox(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // Main room: X=[-4,+4], Z=[-2.5,+2.5], Y=[0,3.5]. S wall doorway X=[-1,+1], Y=[0,2.2].
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

        // L-shaped corridor: A leg X=[-1,+1] Z=[-6.5,-2.5], B leg X=[+1,+5] Z=[-6.5,-4.5].
        static void BuildCorridor(Transform arch, Mats m)
        {
            var root = new GameObject("Corridor").transform;
            root.SetParent(arch, false);

            // Corridor A. Ceiling/side walls trimmed at Z=-2.7 (south face of
            // the main-room S wall) so bodies abut cleanly without z-fight.
            // Floor stays at Z=-2.5 — only touches at that plane.
            MakeBox(root, "A_Floor",   new Vector3(0,    -0.1f, -4.5f), new Vector3(2.0f, 0.2f, 4.0f), m.floor);
            MakeBox(root, "A_Ceiling", new Vector3(0,     3.1f, -4.6f), new Vector3(2.0f, 0.2f, 3.8f), m.ceiling);
            MakeBox(root, "A_WallW",   new Vector3(-1.1f, 1.5f, -4.6f), new Vector3(0.2f, 3.0f, 3.8f), m.walls);
            // South end trimmed (Z=-4.3) to abut B_WallN's interior face.
            MakeBox(root, "A_WallE",   new Vector3(1.1f,  1.5f, -3.5f), new Vector3(0.2f, 3.0f, 1.6f), m.walls);

            MakeBox(root, "B_Floor",   new Vector3(3.0f, -0.1f, -5.5f), new Vector3(4.0f, 0.2f, 2.0f), m.floor);
            MakeBox(root, "B_Ceiling", new Vector3(3.0f,  3.1f, -5.5f), new Vector3(4.0f, 0.2f, 2.0f), m.ceiling);
            MakeBox(root, "B_WallN",   new Vector3(3.0f,  1.5f, -4.4f), new Vector3(4.0f, 3.0f, 0.2f), m.walls);

            MakeBox(root, "L_WallS",   new Vector3(2.0f,  1.5f, -6.6f), new Vector3(6.0f, 3.0f, 0.2f), m.walls);
        }

        // Spawn alcove: X=[+5,+8], Z=[-7,-4], Y=[0,3]. W wall opens onto corridor B at Z=[-6.5,-4.5], Y=[0,2.2].
        static void BuildSpawn(Transform arch, Mats m)
        {
            var root = new GameObject("Spawn").transform;
            root.SetParent(arch, false);

            MakeBox(root, "Floor",       new Vector3(6.5f, -0.1f, -5.5f), new Vector3(3.0f, 0.2f, 3.0f), m.walls); // matte, intentionally not gloss
            MakeBox(root, "Ceiling",     new Vector3(6.5f,  3.1f, -5.5f), new Vector3(3.0f, 0.2f, 3.0f), m.ceiling);
            MakeBox(root, "WallE",       new Vector3(8.1f,  1.5f, -5.5f), new Vector3(0.2f, 3.0f, 3.0f), m.walls);
            MakeBox(root, "WallN",       new Vector3(6.5f,  1.5f, -3.9f), new Vector3(3.0f, 3.0f, 0.2f), m.walls);
            MakeBox(root, "WallS",       new Vector3(6.5f,  1.5f, -7.1f), new Vector3(3.0f, 3.0f, 0.2f), m.walls);

            // West wall remnants around the corridor-B opening, trimmed to
            // abut neighbouring wall bodies without overlap.
            MakeBox(root, "WallW_Header",new Vector3(4.9f,  2.6f, -5.5f), new Vector3(0.2f, 0.8f, 2.0f), m.walls);
            MakeBox(root, "WallW_South", new Vector3(4.9f,  1.1f, -6.85f),new Vector3(0.2f, 2.2f, 0.3f), m.walls);
            MakeBox(root, "WallW_North", new Vector3(4.9f,  1.1f, -4.15f),new Vector3(0.2f, 2.2f, 0.3f), m.walls);
        }

        static void BuildDJBooth(Transform props, Mats m)
        {
            var root = new GameObject("DJBooth").transform;
            root.SetParent(props, false);

            MakeBox(root, "Lectern",  new Vector3( 0,    0.5f,  -1.0f), new Vector3(1.5f, 1.0f, 0.6f), m.booth);
            MakeBox(root, "SpeakerL", new Vector3(-1.2f, 0.75f, -1.0f), new Vector3(0.5f, 1.5f, 0.5f), m.boothMatte);
            MakeBox(root, "SpeakerR", new Vector3( 1.2f, 0.75f, -1.0f), new Vector3(0.5f, 1.5f, 0.5f), m.boothMatte);
        }

        static void BuildPlayer(Transform props, string materialsPath)
        {
            BuildPlayerCapsule(props, materialsPath, "Player_1", "VAL_Player_1", new Vector3( 0.5f, 0.75f, 0.5f));
            BuildPlayerCapsule(props, materialsPath, "Player_2", "VAL_Player_2", new Vector3(-0.5f, 0.75f, 0.5f));
        }

        static void BuildPlayerCapsule(Transform props, string materialsPath, string name, string matName, Vector3 localPos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.transform.SetParent(props, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);

            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var lit = Shader.Find("VideoAreaLight/Lit");
            string matPath = $"{materialsPath}/{matName}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            var mat = existing != null ? existing : new Material(lit) { name = matName };
            mat.shader = lit;
            mat.SetColor("_BaseColor", new Color(0.4f, 0.4f, 0.4f));
            mat.SetFloat("_Smoothness", 0.5f);
            if (existing == null) AssetDatabase.CreateAsset(mat, matPath);
            else EditorUtility.SetDirty(mat);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            go.SetActive(false);
        }

        static VideoAreaLightSource BuildVALSource(Transform parent, Mats m, RenderTexture screenRT, Texture2D placeholder)
        {
            // CRITICAL: VideoAreaLightSource and the Quad MeshRenderer MUST live
            // on the same GameObject. The broadcaster computes world-space
            // corners via its own transform; splitting them desyncs the lit
            // area from the rendered screen.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "VAL_Source";
            quad.transform.SetParent(parent, false);
            // Slightly inside the N wall (interior face Z=+2.5) to avoid z-fight.
            quad.transform.position = new Vector3(0, 1.6f, 2.4f);
            // Default Quad's visible face is on -transform.forward; flipNormal=true
            // mirrors the broadcaster's emit direction to match.
            quad.transform.localRotation = Quaternion.identity;
            quad.transform.localScale = new Vector3(4.8f, 2.7f, 1.5f);
            quad.GetComponent<MeshRenderer>().sharedMaterial = m.screen;
            // Strip MeshCollider so probe-bake rays don't hit the screen.
            var quadCollider = quad.GetComponent<Collider>();
            if (quadCollider != null) Object.DestroyImmediate(quadCollider);

            var val = quad.AddComponent<VideoAreaLightSource>();
            val.localHalfSize = new Vector2(0.5f, 0.5f);
            val.screenAxis = VideoAreaLightSource.Axis.XY;
            val.flipNormal = true;
            val.maxIntensity = 20f;
            val.intensityCurve = 1.5f;
            val.saturationBoost = 1.4f;
            val.zoneFeather = 0.3f;

            // Both fields point at the placeholder so the screen has signal at
            // import. User swaps both to the render texture when adding a clip.
            val.videoTexture = placeholder;
            m.screen.SetTexture("_BaseMap", placeholder);

            var vp = quad.AddComponent<VideoPlayer>();
            vp.playOnAwake = true;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = screenRT;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.source = VideoSource.VideoClip;

            return val;
        }

        static void BuildZoneMask(Transform parent, VideoAreaLightSource src)
        {
            var go = new GameObject("VAL_ZoneMask");
            go.transform.SetParent(parent, false);
            // Sized to the venue's bounding rectangle. Zone mask = venue
            // boundary; probe volumes handle intra-venue visibility.
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
            v.center = Vector3.zero;
            v.size = size;
            v.voxelSize = voxelSize;
            // Quadrant variance scales 1/sqrt(N/4) — needs ~4× Scalar's samples
            // to match per-bucket density.
            v.samplesPerVoxel = encoding == VALVisibilityEncoding.Quadrant ? 64 : 16;
            v.priority = priority;
            v.encoding = encoding;
        }

        static void BuildSpawnFill(Transform parent)
        {
            var go = new GameObject("SpawnFill");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(6.5f, 2.8f, -5.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0, 0);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = new Color(1f, 0.85f, 0.65f);
            // Calibrated for URP 17 Physical Light Units (lumens). With PLU
            // off, drop this to 1.5–3.
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
            // Just inside the main-room doorway, looking N at booth + screen.
            go.transform.position = new Vector3(0f, 1.7f, -2.0f);
            go.transform.rotation = Quaternion.Euler(5f, 0f, 0f);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            // URP cameras default renderPostProcessing=false; without this the
            // PostFX_Volume profile is inert.
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            go.AddComponent<AudioListener>();
        }
    }
}
#endif
