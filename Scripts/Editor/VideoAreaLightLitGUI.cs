using System;
using UnityEditor;
using UnityEngine;

namespace VideoAreaLight.EditorTools
{
    /// <summary>
    /// Custom material editor for the VideoAreaLight/Lit shader. Wraps the
    /// (internal) URP/Lit shader GUI via reflection to keep the polished
    /// URP material inspector, then appends a foldout for the area-light
    /// specific controls (Use Video Cookie).
    ///
    /// If the URP editor type cannot be located (e.g. URP renamed it in a
    /// future version), we fall back to Unity's default ShaderGUI which
    /// shows every property as a flat list - less polished but everything
    /// stays visible.
    /// </summary>
    public class VideoAreaLightLitGUI : ShaderGUI
    {
        const string LitShaderTypeName = "UnityEditor.Rendering.Universal.ShaderGUI.LitShader";

        ShaderGUI _wrapped;
        bool _wrapAttempted;

        void EnsureWrapped()
        {
            if (_wrapAttempted) return;
            _wrapAttempted = true;

            try
            {
                Type litType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    litType = asm.GetType(LitShaderTypeName);
                    if (litType != null) break;
                }
                if (litType != null)
                    _wrapped = (ShaderGUI)Activator.CreateInstance(litType);
            }
            catch
            {
                _wrapped = null;
            }
        }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            EnsureWrapped();

            if (_wrapped != null)
                _wrapped.OnGUI(materialEditor, properties);
            else
                base.OnGUI(materialEditor, properties);

            DrawAreaLightSection(materialEditor, properties);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            EnsureWrapped();
            if (_wrapped != null)
                _wrapped.AssignNewShaderToMaterial(material, oldShader, newShader);
            else
                base.AssignNewShaderToMaterial(material, oldShader, newShader);
        }

        public override void ValidateMaterial(Material material)
        {
            EnsureWrapped();
            if (_wrapped != null)
                _wrapped.ValidateMaterial(material);
            else
                base.ValidateMaterial(material);
        }

        void DrawAreaLightSection(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            EditorGUILayout.Space();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Video Area Light", EditorStyles.boldLabel);

                var useCookie = FindProperty("_UseCookie", properties, false);
                if (useCookie != null)
                {
                    materialEditor.ShaderProperty(
                        useCookie,
                        new GUIContent(
                            "Use Video Cookie",
                            "ON: sample the video texture inside the highlight (cinematic, costs more). " +
                            "OFF: uniform colour fill (cheaper, blurs naturally as roughness rises). " +
                            "Recommended ON for the dance floor and high-gloss metals, OFF for matte materials."));
                }

                EditorGUILayout.HelpBox(
                    "This material adds a Video Area Light contribution on top of the standard URP/Lit lighting. " +
                    "The contribution comes from the active VideoAreaLightSource in the scene. If no source is " +
                    "active, this material renders identically to URP/Lit.",
                    MessageType.None);
            }
        }
    }
}
