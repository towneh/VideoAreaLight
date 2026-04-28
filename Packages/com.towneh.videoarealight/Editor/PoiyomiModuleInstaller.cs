using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VideoAreaLight.EditorTools
{
    // Installs the VideoAreaLight module into the user's Poiyomi Modular
    // Shader system. Reflection-only — we never compile-link against
    // Poiyomi.ModularShaderSystem, so this DLL stays valid whether Poiyomi
    // is installed or not.
    //
    // Two-step user flow (the second step is per-material to keep recompile
    // cost proportional to what the user actually uses):
    //
    //   1. Tools > VideoAreaLight > Install Poiyomi Module
    //      Wires up the module: copies the template, builds the ShaderModule
    //      asset, registers it in ModuleCollectionPro. Fast — no shader
    //      regeneration. Run once per project.
    //
    //   2. Right-click a Material (in the inspector gear or the Project window)
    //      > VideoAreaLight: Apply to Material's Shader
    //      Regenerates only the Poiyomi shader(s) those materials use, so the
    //      Video Area Light section starts appearing on those materials. Cost
    //      scales with selection, not project size.
    //
    // The module artifacts are:
    //   - VRLTC_VideoAreaLight.poiTemplateCollection : the shader code,
    //     shipped verbatim from this package's Samples~ folder.
    //   - VRLM_VideoAreaLight.asset : a ShaderModule ScriptableObject that
    //     binds named templates from the collection to Poiyomi's injection
    //     keywords. We construct this in-memory and let Unity serialize it.
    internal static class PoiyomiModuleInstaller
    {
        const string MenuPath          = "Tools/VideoAreaLight/Install Poiyomi Module";
        const string UninstallPath     = "Tools/VideoAreaLight/Uninstall Poiyomi Module";
        const string ApplyContextPath  = "CONTEXT/Material/VideoAreaLight - Apply to Material's Shader";
        const string ApplyAssetsPath   = "Assets/VideoAreaLight - Apply to Selected Materials' Shader(s)";
        const string ModuleId          = "VideoAreaLight";
        const string ModuleFolder      = "VideoAreaLight";
        const string ModuleAssetName   = "VRLM_VideoAreaLight.asset";
        const string TemplateName      = "VRLTC_VideoAreaLight.poiTemplateCollection";
        const string PackageName       = "com.towneh.videoarealight";
        const string SampleSubpath     = "Samples~/PoiyomiIntegration/" + TemplateName;

        // Maps Poiyomi injection keyword → template name inside the collection.
        // Order is the order Templates are listed on the ShaderModule asset.
        //
        // The function-call template binds to FRAGMENT_BASE_LIGHTING_EARLY so
        // its writes to poiLight.finalLighting land before Poi_Shading's
        // `finalColor = baseColor * finalLighting` snapshot, which is bound
        // to FRAGMENT_BASE_LIGHTING. EARLY is emitted strictly before
        // FRAGMENT_BASE_LIGHTING by the pass templates regardless of
        // module registration order.
        static readonly (string keyword, string templateName, int queue)[] Bindings =
        {
            ("LIGHTING_PROPERTIES",            "PoiVideoAreaLightProperties",   101),
            ("SHADER_KEYWORDS",                "PoiVideoAreaLightKeywords",     100),
            ("BASE_PROPERTY_VARIABLES_EXPOSED","PoiVideoAreaLightVariables",    100),
            ("FRAGMENT_BASE_FUNCTIONS",        "PoiVideoAreaLightFunction",     100),
            ("FRAGMENT_BASE_LIGHTING_EARLY",   "PoiVideoAreaLightFunctionCall", 100),
        };

        // ============================================================
        // Install / Uninstall — wire the module in or out of Poiyomi.
        // No shader regeneration happens here; that's per-material.
        // ============================================================

        [MenuItem(MenuPath)]
        static void Install()
        {
            try
            {
                var (shaderModuleType, moduleTemplateType, templateCollectionType, moduleCollectionType)
                    = ResolvePoiyomiTypes();

                string moduleCollectionPath = FindModuleCollectionAssetPath(moduleCollectionType, "ModuleCollectionPro");
                if (string.IsNullOrEmpty(moduleCollectionPath))
                {
                    EditorUtility.DisplayDialog("VideoAreaLight",
                        "Could not locate Poiyomi's ModuleCollectionPro.asset in the project. " +
                        "Install Poiyomi Pro Modular Shader first, then try again.",
                        "OK");
                    return;
                }

                string poiyomiRoot = LocatePoiyomiModularShaderRoot(moduleCollectionPath);
                string targetFolder = Path.Combine(poiyomiRoot, "Editor/Poi_FeatureModules", ModuleFolder).Replace('\\', '/');
                Directory.CreateDirectory(targetFolder);

                string targetTemplatePath = $"{targetFolder}/{TemplateName}";
                if (!CopyTemplateFromPackage(targetTemplatePath))
                {
                    EditorUtility.DisplayDialog("VideoAreaLight",
                        $"Could not find the bundled template file at {SampleSubpath} inside the {PackageName} package.",
                        "OK");
                    return;
                }

                AssetDatabase.ImportAsset(targetTemplatePath, ImportAssetOptions.ForceUpdate);

                var collectionAsset = AssetDatabase.LoadMainAssetAtPath(targetTemplatePath);
                if (collectionAsset == null || !templateCollectionType.IsInstanceOfType(collectionAsset))
                {
                    EditorUtility.DisplayDialog("VideoAreaLight",
                        $"Failed to import the template collection at {targetTemplatePath}. Check the Console for errors.",
                        "OK");
                    return;
                }

                var subAssets = AssetDatabase.LoadAllAssetsAtPath(targetTemplatePath);

                // Idempotent install: if the module asset already exists, mutate it
                // in place so its GUID is preserved and the existing reference in
                // ModuleCollectionPro stays valid. If we deleted-and-recreated, the
                // old reference would become a missing-ref AND the new GUID would
                // be appended, leaving a stale entry in the collection.
                string moduleAssetPath = $"{targetFolder}/{ModuleAssetName}";
                var module = AssetDatabase.LoadMainAssetAtPath(moduleAssetPath) as ScriptableObject;
                bool isNewAsset = module == null || !shaderModuleType.IsInstanceOfType(module);

                if (isNewAsset)
                {
                    if (module != null) AssetDatabase.DeleteAsset(moduleAssetPath);
                    module = BuildModuleAsset(shaderModuleType, moduleTemplateType, subAssets);
                    if (module == null) return;
                    AssetDatabase.CreateAsset(module, moduleAssetPath);
                }
                else
                {
                    if (!PopulateModuleAsset(module, moduleTemplateType, subAssets)) return;
                    EditorUtility.SetDirty(module);
                }

                CleanMissingReferencesFromModuleCollection(moduleCollectionPath);

                if (!RegisterInModuleCollection(moduleCollectionPath, module))
                {
                    EditorUtility.DisplayDialog("VideoAreaLight",
                        "Created the module asset but could not register it in ModuleCollectionPro.asset. " +
                        "Open that asset and add the new module manually.",
                        "OK");
                    return;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                EditorUtility.DisplayDialog("VideoAreaLight",
                    "Module wired into Poiyomi Pro.\n\n" +
                    "Existing Poiyomi materials won't show the Video Area Light section yet — " +
                    "their shaders are template-generated and need to be regenerated to pick up the new module.\n\n" +
                    "Right-click a Material (or select several in the Project window) and choose " +
                    "\"VideoAreaLight - Apply to Material's Shader\" to regenerate just those shaders. " +
                    "Cost scales with selection, not with project size.",
                    "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("VideoAreaLight",
                    "Install failed: " + e.Message + "\nSee the Console for details.",
                    "OK");
            }
        }

        [MenuItem(UninstallPath)]
        static void Uninstall()
        {
            try
            {
                var (_, _, _, moduleCollectionType) = ResolvePoiyomiTypes();
                string moduleCollectionPath = FindModuleCollectionAssetPath(moduleCollectionType, "ModuleCollectionPro");
                if (!string.IsNullOrEmpty(moduleCollectionPath))
                {
                    string poiyomiRoot = LocatePoiyomiModularShaderRoot(moduleCollectionPath);
                    string moduleAssetPath = $"{poiyomiRoot}/Editor/Poi_FeatureModules/{ModuleFolder}/{ModuleAssetName}";
                    string templatePath    = $"{poiyomiRoot}/Editor/Poi_FeatureModules/{ModuleFolder}/{TemplateName}";

                    var module = AssetDatabase.LoadMainAssetAtPath(moduleAssetPath);
                    if (module != null) UnregisterFromModuleCollection(moduleCollectionPath, module);

                    AssetDatabase.DeleteAsset(moduleAssetPath);
                    AssetDatabase.DeleteAsset(templatePath);
                    string folder = Path.GetDirectoryName(moduleAssetPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) &&
                        Directory.GetFileSystemEntries(folder).Length == 0)
                    {
                        AssetDatabase.DeleteAsset(folder);
                    }

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
                EditorUtility.DisplayDialog("VideoAreaLight",
                    "Module unwired from Poiyomi Pro.\n\n" +
                    "Materials whose shaders were regenerated while the module was installed still contain " +
                    "the Video Area Light hooks. Right-click those materials and \"Apply to Material's Shader\" " +
                    "to regenerate them clean (the section will simply disappear).",
                    "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("VideoAreaLight", "Uninstall failed: " + e.Message, "OK");
            }
        }

        // ============================================================
        // Apply — regenerate Poiyomi shaders for specific materials.
        // This is where the variant-recompile cost lives. Limit it.
        // ============================================================

        // Inspector gear-menu / right-click on a single material in inspector.
        [MenuItem(ApplyContextPath)]
        static void ApplyContext(MenuCommand cmd)
        {
            var mat = cmd.context as Material;
            if (mat == null) return;
            ApplyToMaterials(new[] { mat });
        }

        // Project-window context menu (multi-select supported via Selection).
        [MenuItem(ApplyAssetsPath)]
        static void ApplyAssets()
        {
            var mats = Selection.objects.OfType<Material>().ToArray();
            if (mats.Length == 0) return;
            ApplyToMaterials(mats);
        }

        [MenuItem(ApplyAssetsPath, validate = true)]
        static bool ApplyAssetsValidate()
        {
            // Only show the menu when at least one Material is selected.
            return Selection.objects.OfType<Material>().Any();
        }

        static void ApplyToMaterials(IList<Material> materials)
        {
            try
            {
                if (materials == null || materials.Count == 0) return;

                var (modularShaderType, generateMethod, lastGeneratedField, shaderPathField, modularShaders)
                    = ResolveRegenerationContext();
                if (modularShaders == null) return;

                // Build shader-name → ModularShader asset map once.
                var byShaderName = new Dictionary<string, ScriptableObject>();
                foreach (var asset in modularShaders)
                {
                    string sp = shaderPathField?.GetValue(asset) as string;
                    if (!string.IsNullOrEmpty(sp) && !byShaderName.ContainsKey(sp))
                        byShaderName[sp] = asset;
                }

                // Collect the unique set of ModularShader assets the selection
                // touches. Materials sharing a shader contribute once.
                var targets = new HashSet<ScriptableObject>();
                var materialsByShaderName = new Dictionary<string, int>();
                var skippedNonPoiyomi = new List<Material>();

                foreach (var mat in materials)
                {
                    if (mat == null || mat.shader == null) continue;
                    string sname = mat.shader.name;
                    if (byShaderName.TryGetValue(sname, out var ms))
                    {
                        targets.Add(ms);
                        materialsByShaderName.TryGetValue(sname, out int n);
                        materialsByShaderName[sname] = n + 1;
                    }
                    else
                    {
                        skippedNonPoiyomi.Add(mat);
                    }
                }

                if (targets.Count == 0)
                {
                    EditorUtility.DisplayDialog("VideoAreaLight",
                        "None of the selected materials use a Poiyomi Modular shader. Nothing to do.",
                        "OK");
                    return;
                }

                // Confirmation: show the actual blast radius. Regenerating a
                // shader updates the Properties block visible to every
                // material in the project that uses it, not just the ones the
                // user selected. This catches misclicks and makes the scope
                // explicit. Counted once and reused for the post-apply log so
                // the summary numbers match what the dialog promised.
                var totalsByShader = CountMaterialsByShader();
                if (!ConfirmApplyScope(targets, materialsByShaderName, totalsByShader, shaderPathField))
                    return;

                int regenerated = 0;
                try
                {
                    AssetDatabase.StartAssetEditing();
                    foreach (var asset in targets)
                    {
                        string shaderDir = ResolveShaderOutputDir(asset, lastGeneratedField, shaderPathField);
                        if (string.IsNullOrEmpty(shaderDir))
                        {
                            Debug.LogWarning($"VideoAreaLight: could not resolve output dir for " +
                                             $"{AssetDatabase.GetAssetPath(asset)}. Skipping.");
                            continue;
                        }

                        try
                        {
                            generateMethod.Invoke(null, new object[] { shaderDir, asset, false });
                            regenerated++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"VideoAreaLight: regenerate failed for " +
                                             $"{AssetDatabase.GetAssetPath(asset)}: " +
                                             $"{ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                AssetDatabase.Refresh();

                // Sum the actual project-wide impact across the regenerated
                // shaders. This matches the count the confirmation dialog
                // showed the user, not just the size of their selection.
                int totalMaterialsAffected = 0;
                foreach (var asset in targets)
                {
                    string sname = shaderPathField?.GetValue(asset) as string;
                    if (sname != null && totalsByShader.TryGetValue(sname, out int n))
                        totalMaterialsAffected += n;
                }

                string summary =
                    $"Regenerated {regenerated} Poiyomi shader(s); the Video Area Light section is now " +
                    $"visible on {totalMaterialsAffected} material(s) in this project " +
                    $"({materials.Count} selected, {Math.Max(0, totalMaterialsAffected - materials.Count)} other(s) share the same shader(s)).";
                if (skippedNonPoiyomi.Count > 0)
                    summary += $"\n{skippedNonPoiyomi.Count} non-Poiyomi material(s) skipped.";
                Debug.Log("VideoAreaLight: " + summary);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("VideoAreaLight",
                    "Apply failed: " + e.Message + "\nSee the Console for details.",
                    "OK");
            }
        }

        // Builds the scope-confirmation dialog. Caller passes the
        // shader-name → materials-in-project count map so the same numbers
        // can be reused for the post-apply log message.
        static bool ConfirmApplyScope(
            HashSet<ScriptableObject> targets,
            Dictionary<string, int> materialsByShaderName,
            Dictionary<string, int> totalsByShader,
            FieldInfo shaderPathField)
        {
            var lines = new List<string>();
            foreach (var asset in targets)
            {
                string sname = shaderPathField?.GetValue(asset) as string ?? "(unknown)";
                materialsByShaderName.TryGetValue(sname, out int selectedCount);
                totalsByShader.TryGetValue(sname, out int totalCount);

                int otherCount = Mathf.Max(0, totalCount - selectedCount);
                string otherText = otherCount == 0
                    ? "no other materials in this project use it"
                    : otherCount == 1
                        ? "1 other material in this project also uses it"
                        : $"{otherCount} other materials in this project also use it";
                lines.Add($"• {sname}\n    selected: {selectedCount}, {otherText}");
            }

            string body =
                $"Selected materials use {targets.Count} Poiyomi shader(s). The selected SHADERS will be " +
                "regenerated, which updates the Properties block visible to every material bound to them — " +
                "not just the ones you selected:\n\n" +
                string.Join("\n", lines) +
                "\n\nMaterials that don't have the Video Area Light toggle enabled (default OFF) pay " +
                "nothing at runtime; the section just appears in their inspector.\n\n" +
                "Continue?";

            return EditorUtility.DisplayDialog("VideoAreaLight - Apply", body, "Apply", "Cancel");
        }

        // Project-wide scan: shaderName -> how many .mat assets bind to it.
        // Used only by the confirmation dialog. One AssetDatabase.FindAssets
        // walk; the inner LoadAssetAtPath calls are cheap because materials
        // are small assets.
        static Dictionary<string, int> CountMaterialsByShader()
        {
            var counts = new Dictionary<string, int>();
            var guids = AssetDatabase.FindAssets("t:Material");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                string name = mat.shader.name;
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
            return counts;
        }

        // Resolves all the reflection handles + asset list needed to regenerate.
        // Returns (null, null, null, null, null) if anything is missing — which
        // means Poiyomi isn't installed or its API has shifted.
        static (Type modularShaderType,
                MethodInfo generateMethod,
                FieldInfo lastGeneratedField,
                FieldInfo shaderPathField,
                List<ScriptableObject> modularShaders) ResolveRegenerationContext()
        {
            var modularShaderType = FindType("Poiyomi.ModularShaderSystem.ModularShader");
            var shaderGeneratorType = FindType("Poiyomi.ModularShaderSystem.ShaderGenerator");
            if (modularShaderType == null || shaderGeneratorType == null)
            {
                Debug.LogWarning("VideoAreaLight: Poiyomi.ModularShaderSystem types not resolved — is Poiyomi installed?");
                return default;
            }

            var generateMethod = shaderGeneratorType.GetMethod(
                "GenerateShader",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), modularShaderType, typeof(bool) },
                null);
            if (generateMethod == null)
            {
                Debug.LogWarning("VideoAreaLight: ShaderGenerator.GenerateShader(string, ModularShader, bool) not found.");
                return default;
            }

            var lastGeneratedField = modularShaderType.GetField("LastGeneratedShaders", BindingFlags.Public | BindingFlags.Instance);
            var shaderPathField    = modularShaderType.GetField("ShaderPath",           BindingFlags.Public | BindingFlags.Instance);
            if (lastGeneratedField == null || shaderPathField == null)
            {
                Debug.LogWarning("VideoAreaLight: ModularShader.LastGeneratedShaders / ShaderPath fields not found.");
                return default;
            }

            // Find the Poiyomi root via ModuleCollectionPro, then walk it for ModularShader assets.
            var moduleCollectionType = FindType("Poiyomi.ModularShaderSystem.CibbiExtensions.ModuleCollection");
            string moduleCollectionPath = moduleCollectionType != null
                ? FindModuleCollectionAssetPath(moduleCollectionType, "ModuleCollectionPro")
                : null;
            if (string.IsNullOrEmpty(moduleCollectionPath))
            {
                Debug.LogWarning("VideoAreaLight: ModuleCollectionPro.asset not found — can't locate Poiyomi root.");
                return default;
            }
            string poiyomiRoot = LocatePoiyomiModularShaderRoot(moduleCollectionPath);

            var modularShaders = new List<ScriptableObject>();
            var assetGuids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { poiyomiRoot });
            foreach (var guid in assetGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                if (asset != null && modularShaderType.IsInstanceOfType(asset))
                    modularShaders.Add(asset);
            }
            if (modularShaders.Count == 0)
            {
                Debug.LogWarning($"VideoAreaLight: found 0 ModularShader assets under '{poiyomiRoot}'.");
                return default;
            }

            return (modularShaderType, generateMethod, lastGeneratedField, shaderPathField, modularShaders);
        }

        // Locates the directory we should pass to ShaderGenerator.GenerateShader.
        //
        // Primary signal: ModularShader.LastGeneratedShaders[0]. Reliable when
        // present, but Poiyomi sometimes ships these references stale (the GUID
        // points at an asset that no longer exists). When that's the case we
        // fall back to Shader.Find(asset.ShaderPath) — the user-facing shader
        // name like ".poiyomi/Poiyomi Pro URP" — which is a runtime lookup
        // immune to GUID drift.
        static string ResolveShaderOutputDir(ScriptableObject asset, FieldInfo lastGeneratedField, FieldInfo shaderPathField)
        {
            var lastGenerated = lastGeneratedField.GetValue(asset) as System.Collections.IList;
            if (lastGenerated != null && lastGenerated.Count > 0)
            {
                var firstShader = lastGenerated[0] as UnityEngine.Object;
                if (firstShader != null)
                {
                    string p = AssetDatabase.GetAssetPath(firstShader);
                    if (!string.IsNullOrEmpty(p))
                        return Path.GetDirectoryName(p)?.Replace('\\', '/');
                }
            }

            string shaderPath = shaderPathField?.GetValue(asset) as string;
            if (string.IsNullOrEmpty(shaderPath)) return null;

            var shader = Shader.Find(shaderPath);
            if (shader == null) return null;

            string foundPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(foundPath)) return null;

            return Path.GetDirectoryName(foundPath)?.Replace('\\', '/');
        }

        // ============================================================
        // Poiyomi reflection + asset construction helpers
        // ============================================================

        static (Type shaderModule, Type moduleTemplate, Type templateCollection, Type moduleCollection) ResolvePoiyomiTypes()
        {
            Type shaderModule       = FindType("Poiyomi.ModularShaderSystem.ShaderModule");
            Type moduleTemplate     = FindType("Poiyomi.ModularShaderSystem.ModuleTemplate");
            Type templateCollection = FindType("Poiyomi.ModularShaderSystem.TemplateCollectionAsset");
            Type moduleCollection   = FindType("Poiyomi.ModularShaderSystem.CibbiExtensions.ModuleCollection");

            if (shaderModule == null || moduleTemplate == null || templateCollection == null || moduleCollection == null)
            {
                throw new InvalidOperationException(
                    "Poiyomi Modular Shader System types not found. Install Poiyomi Pro and try again.");
            }
            return (shaderModule, moduleTemplate, templateCollection, moduleCollection);
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        static string FindModuleCollectionAssetPath(Type moduleCollectionType, string assetName)
        {
            var guids = AssetDatabase.FindAssets($"t:{moduleCollectionType.Name} {assetName}");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == assetName) return path;
            }
            // Fallback: search by name only.
            guids = AssetDatabase.FindAssets($"{assetName} t:ScriptableObject");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == assetName)
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(path);
                    if (obj != null && moduleCollectionType.IsInstanceOfType(obj)) return path;
                }
            }
            return null;
        }

        static string LocatePoiyomiModularShaderRoot(string moduleCollectionPath)
        {
            string editorFolder = Path.GetDirectoryName(moduleCollectionPath)?.Replace('\\', '/');
            string root = Path.GetDirectoryName(editorFolder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException("Could not derive Poiyomi root from " + moduleCollectionPath);
            return root;
        }

        static bool CopyTemplateFromPackage(string targetAssetPath)
        {
            var pkg = UpmPackageInfo.FindForAssetPath($"Packages/{PackageName}/package.json");
            if (pkg == null) pkg = UpmPackageInfo.FindForAssetPath($"Packages/{PackageName}");
            if (pkg == null) return false;

            string sourceFile = Path.Combine(pkg.resolvedPath, SampleSubpath).Replace('\\', '/');
            if (!File.Exists(sourceFile)) return false;

            string targetAbsolute = Path.GetFullPath(targetAssetPath);
            File.Copy(sourceFile, targetAbsolute, overwrite: true);
            return true;
        }

        static ScriptableObject BuildModuleAsset(Type shaderModuleType, Type moduleTemplateType, UnityEngine.Object[] subAssets)
        {
            var module = ScriptableObject.CreateInstance(shaderModuleType);
            if (!PopulateModuleAsset(module, moduleTemplateType, subAssets))
            {
                UnityEngine.Object.DestroyImmediate(module);
                return null;
            }
            return module;
        }

        static bool PopulateModuleAsset(ScriptableObject module, Type moduleTemplateType, UnityEngine.Object[] subAssets)
        {
            SetField(module, "Id", ModuleId);
            SetField(module, "Name", "Video Area Light");
            SetField(module, "Version", "1.0");
            SetField(module, "Author", "Matt Town");
            SetField(module, "Description",
                "Adds VideoAreaLight contribution (analytic polygon irradiance + Karis MRP specular) " +
                "from a single VideoAreaLightSource broadcaster in the scene.");
            SetField(module, "AllowDuplicates", false);
            SetField(module, "ForceDuplicateLogic", false);
            SetField(module, "EnableProperties", MakeEmptyList("Poiyomi.ModularShaderSystem.EnableProperty"));
            SetField(module, "Properties",       MakeEmptyList("Poiyomi.ModularShaderSystem.Property"));
            SetField(module, "ModuleDependencies", new List<string>());
            SetField(module, "IncompatibleWith",   new List<string>());
            SetField(module, "Functions",        MakeEmptyList("Poiyomi.ModularShaderSystem.ShaderFunction"));
            SetField(module, "AdditionalSerializedData", "");

            var listType = typeof(List<>).MakeGenericType(moduleTemplateType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType);

            foreach (var (keyword, templateName, queue) in Bindings)
            {
                var templateAsset = subAssets.FirstOrDefault(a => a != null && a.name == templateName);
                if (templateAsset == null)
                {
                    Debug.LogError($"VideoAreaLight: template named '{templateName}' was not found in the imported collection. " +
                                   "The .poiTemplateCollection text may be malformed.");
                    return false;
                }

                var moduleTemplate = Activator.CreateInstance(moduleTemplateType);
                SetField(moduleTemplate, "Template", templateAsset);
                SetField(moduleTemplate, "Keywords", new List<string> { keyword });
                SetField(moduleTemplate, "NeedsVariant", false);
                SetField(moduleTemplate, "Queue", queue);

                list.Add(moduleTemplate);
            }
            SetField(module, "Templates", list);
            return true;
        }

        static object MakeEmptyList(string elementTypeName)
        {
            var t = FindType(elementTypeName);
            if (t == null) return null;
            var listType = typeof(List<>).MakeGenericType(t);
            return Activator.CreateInstance(listType);
        }

        static void SetField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f == null)
                throw new InvalidOperationException($"Field '{name}' not found on {target.GetType().FullName}.");
            f.SetValue(target, value);
        }

        // ============================================================
        // ModuleCollection registration
        // ============================================================

        static bool RegisterInModuleCollection(string moduleCollectionPath, UnityEngine.Object module)
        {
            var collection = AssetDatabase.LoadMainAssetAtPath(moduleCollectionPath);
            if (collection == null) return false;

            var so = new SerializedObject(collection);
            var modules = so.FindProperty("Modules");
            var enabled = so.FindProperty("ModulesEnabled");
            if (modules == null) return false;

            for (int i = 0; i < modules.arraySize; i++)
            {
                if (modules.GetArrayElementAtIndex(i).objectReferenceValue == module)
                    return true;
            }

            modules.arraySize++;
            modules.GetArrayElementAtIndex(modules.arraySize - 1).objectReferenceValue = module;

            if (enabled != null)
            {
                enabled.arraySize++;
                enabled.GetArrayElementAtIndex(enabled.arraySize - 1).boolValue = true;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(collection);
            return true;
        }

        // Strip missing-reference entries from ModuleCollectionPro.Modules (and
        // their corresponding ModulesEnabled bytes). Re-running the installer
        // before this helper existed would have left stale "Missing" entries.
        static void CleanMissingReferencesFromModuleCollection(string moduleCollectionPath)
        {
            var collection = AssetDatabase.LoadMainAssetAtPath(moduleCollectionPath);
            if (collection == null) return;

            var so = new SerializedObject(collection);
            var modules = so.FindProperty("Modules");
            var enabled = so.FindProperty("ModulesEnabled");
            if (modules == null) return;

            bool changed = false;
            for (int i = modules.arraySize - 1; i >= 0; i--)
            {
                var prop = modules.GetArrayElementAtIndex(i);
                // objectReferenceValue is null both for "never assigned" slots
                // and for missing-references (asset was deleted). Both should
                // be cleaned. Unity's "delete twice" quirk only applies to
                // slots whose ref is currently non-null.
                if (prop.objectReferenceValue == null)
                {
                    modules.DeleteArrayElementAtIndex(i);
                    if (enabled != null && i < enabled.arraySize)
                        enabled.DeleteArrayElementAtIndex(i);
                    changed = true;
                }
            }
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(collection);
            }
        }

        static void UnregisterFromModuleCollection(string moduleCollectionPath, UnityEngine.Object module)
        {
            var collection = AssetDatabase.LoadMainAssetAtPath(moduleCollectionPath);
            if (collection == null) return;

            var so = new SerializedObject(collection);
            var modules = so.FindProperty("Modules");
            var enabled = so.FindProperty("ModulesEnabled");
            if (modules == null) return;

            for (int i = modules.arraySize - 1; i >= 0; i--)
            {
                if (modules.GetArrayElementAtIndex(i).objectReferenceValue == module)
                {
                    modules.DeleteArrayElementAtIndex(i);
                    if (enabled != null && i < enabled.arraySize)
                        enabled.DeleteArrayElementAtIndex(i);
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(collection);
        }
    }
}
