using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace VideoAreaLight.EditorTools
{
    // Hides shader-integration menu items when the third-party shader
    // they integrate with isn't present in the project. Without this,
    // those entries are always visible and a user without that shader
    // clicking them gets a "shader not installed" dialog — informative
    // but noisy.
    //
    // Why not validators: Unity's `[MenuItem(..., validate=true)]`
    // DISABLE menu items but don't HIDE them. To fully hide, we let
    // the items register normally (via the `[MenuItem]` attributes on
    // the integration installer), then call Menu.RemoveMenuItem after
    // editor startup if the integration's probe type isn't loadable.
    //
    // The detection runs on every domain reload via [InitializeOnLoad]:
    //   - Integration's host shader installed → entries stay visible.
    //   - Host shader missing                → entries silently removed.
    //   - Host shader added/removed         → next domain reload re-evaluates.
    //
    // This avoids touching PlayerSettings scripting-define symbols, which
    // would create churn in version control whenever a user adds or
    // removes a third-party shader package.
    //
    // Adding a new integration: add an Integration entry to the table.
    // No other changes needed.
    [InitializeOnLoad]
    internal static class IntegrationMenuVisibility
    {
        readonly struct Integration
        {
            public readonly string DisplayName;
            public readonly string TypeProbe;
            public readonly string[] MenuPaths;

            public Integration(string displayName, string typeProbe, string[] menuPaths)
            {
                DisplayName = displayName;
                TypeProbe = typeProbe;
                MenuPaths = menuPaths;
            }
        }

        static readonly Integration[] Integrations =
        {
            new Integration(
                displayName: "Poiyomi",
                typeProbe:   "Poiyomi.ModularShaderSystem.ShaderModule",
                menuPaths:   new[]
                {
                    "Tools/VideoAreaLight/Install Poiyomi Module",
                    "Tools/VideoAreaLight/Uninstall Poiyomi Module",
                    "CONTEXT/Material/VideoAreaLight - Apply to Material's Shader",
                    "Assets/VideoAreaLight - Apply to Selected Materials' Shader(s)",
                }),
        };

        static IntegrationMenuVisibility()
        {
            // delayCall: defer until after Unity has finished registering
            // the [MenuItem] attributes. Calling RemoveMenuItem during
            // static-ctor would race that registration.
            EditorApplication.delayCall += MaybeHide;
        }

        static void MaybeHide()
        {
            // UnityEditor.Menu.RemoveMenuItem(string) was public through
            // Unity 2022.x and made internal in Unity 6 (6000.x). Reflection
            // bridges both — we just need to find the static method that
            // takes a single string argument, regardless of access level.
            var removeMethod = typeof(Menu).GetMethod(
                "RemoveMenuItem",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            if (removeMethod == null)
            {
                // Unity changed the signature — the integration menu items
                // will stay visible, but they're functional, just noisy for
                // users without the host shader. Log once so future-us knows
                // to update this file rather than silently lose the guard.
                Debug.LogWarning(
                    "VideoAreaLight: UnityEditor.Menu.RemoveMenuItem(string) not found via reflection. " +
                    "Integration menu items will remain visible regardless of host-shader presence.");
                return;
            }

            bool removedAny = false;
            foreach (var integration in Integrations)
            {
                if (IsTypePresent(integration.TypeProbe)) continue;
                foreach (var path in integration.MenuPaths)
                {
                    removeMethod.Invoke(null, new object[] { path });
                    removedAny = true;
                }
            }

            // Context menus are rebuilt on each open so RemoveMenuItem takes
            // effect immediately. The main menu bar (Tools/...) caches its
            // built form, so the Tools entries linger visually until something
            // forces a rebuild. Menu.UpdateAllMenus does exactly that — also
            // internal, also reachable via reflection.
            if (removedAny) TryUpdateAllMenus();
        }

        static void TryUpdateAllMenus()
        {
            var update = typeof(Menu).GetMethod(
                "UpdateAllMenus",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            update?.Invoke(null, null);
        }

        static bool IsTypePresent(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetType(fullName, throwOnError: false) != null)
                    return true;
            }
            return false;
        }
    }
}
