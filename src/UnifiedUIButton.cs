using System;
using System.IO;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // Optional Unified UI toolbar button, reached only by reflection. Nothing from Unified UI is referenced or
    // bundled, so players without it keep the floating button unchanged. Every entry point is guarded: a missing
    // or changed Unified UI must never stop the administration panel from opening.
    internal static class UnifiedUIButton
    {
        internal const string ApiTypeName = "UnifiedUI.API.UUIAPI";
        internal const string LibraryName = "UnifiedUILib";
        internal const string IconResource = "CivicPressure.UnifiedUIIcon.png";
        private const string ButtonName = "CivicPressureButton";
        private static UIComponent button;
        private static Texture2D ownedIcon;
        private static bool warned;

        // True when the toolbar button was created; false means keep the floating button.
        internal static bool Register(string tooltip, Action<bool> onToggle)
        {
            Release();
            warned = false;
            try
            {
                Assembly library = EnabledLibrary();
                if (library == null) { Warn("an existing Unified UI panel was not detected"); return false; }
                MethodInfo register = FindRegister(library.GetType(ApiTypeName, false));
                if (register == null) { Warn("its button API has changed"); return false; }
                LoadIcon();
                if (ownedIcon == null) { Warn("the toolbar icon is missing from the mod"); return false; }
                button = register.Invoke(null, new object[] { ButtonName, null, tooltip, ownedIcon, onToggle, null, null, null }) as UIComponent;
                if (button == null) Release();
                return button != null;
            }
            catch (Exception error)
            {
                Release();
                Warn((error.InnerException ?? error).Message);
                return false;
            }
        }

        // The Unified UI mod itself (UnifiedUI.LifeCycle.LifeCycle), not the copies of its library that other mods
        // bundle, such as TM:PE or Move It. Those would otherwise create a toolbar for players who never chose one.
        internal static bool IsUnifiedUIMod(string userModTypeName)
        {
            return userModTypeName != null && userModTypeName.StartsWith("UnifiedUI.", StringComparison.Ordinal);
        }

        private static Assembly EnabledLibrary()
        {
            // Each bundled library can own a separate static MainPanel.Instance. Registering with an arbitrary
            // plugin's copy can create a second toolbar containing only CP. Use an already existing panel only;
            // if UUI has not initialized yet, keep the floating button rather than creating another toolbar.
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != LibraryName || FindRegister(assembly.GetType(ApiTypeName, false)) == null) continue;
                Type panel = assembly.GetType("UnifiedUI.GUI.MainPanel", false);
                PropertyInfo exists = panel == null ? null : panel.GetProperty("Exists", BindingFlags.Public | BindingFlags.Static);
                if (exists != null && exists.PropertyType == typeof(bool) && (bool)exists.GetValue(null, null)) return assembly;
            }
            return null;
        }

        // Matches UUIAPI.Register(name, group, tooltip, Texture2D, Action<bool>, Action<ToolBase>, SavedInputKey, hotkeys).
        // The last parameter uses Func<bool> from System.Core; it is matched by position only, so this mod keeps no
        // System.Core reference.
        internal static MethodInfo FindRegister(Type api)
        {
            if (api == null) return null;
            foreach (MethodInfo method in api.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Register" || !typeof(UIComponent).IsAssignableFrom(method.ReturnType)) continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 8 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string) &&
                    p[2].ParameterType == typeof(string) && p[3].ParameterType == typeof(Texture2D) &&
                    p[4].ParameterType == typeof(Action<bool>) && p[5].ParameterType == typeof(Action<ToolBase>) &&
                    p[6].ParameterType == typeof(SavedInputKey)) return method;
            }
            return null;
        }

        // Keeps the toolbar button in step when the panel opens or closes some other way. Unified UI's IsActive
        // setter only swaps sprites, so this never calls back into the panel.
        internal static void SetPressed(bool pressed)
        {
            if (button == null) return;
            try
            {
                PropertyInfo active = button.GetType().GetProperty("IsActive");
                if (active != null && active.CanRead && active.CanWrite && (bool)active.GetValue(button, null) != pressed)
                    active.SetValue(button, pressed, null);
            }
            catch (Exception error) { Warn(error.Message); }
        }

        // The floating button shows live status (complaints, protests); the toolbar icon carries it as a tooltip.
        internal static void SetTooltip(string text)
        {
            if (button == null) return;
            try { if (button.tooltip != text) button.tooltip = text; }
            catch (Exception error) { Warn(error.Message); }
        }

        internal static void Release()
        {
            UIComponent old = button;
            Texture2D icon = ownedIcon;
            button = null;
            ownedIcon = null;
            if (ReferenceEquals(old, null) && ReferenceEquals(icon, null)) return;
            try { DestroyButton(old, icon); }
            catch (Exception error) { Warn(error.Message); }
        }

        // Unity's native calls stay in this separate method so Release itself compiles and returns quietly where
        // Unity is unavailable (the automated checks), and any failure lands inside Release's catch.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DestroyButton(UIComponent target, Texture2D icon)
        {
            try { if (target != null) UnityEngine.Object.Destroy(target.gameObject); }
            finally { if (icon != null) UnityEngine.Object.Destroy(icon); }
        }

        private static Texture2D LoadIcon()
        {
            using (Stream stream = typeof(UnifiedUIButton).Assembly.GetManifestResourceStream(IconResource))
            {
                if (stream == null) return null;
                byte[] bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count <= 0) break;
                    read += count;
                }
                Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                ownedIcon = texture;
                texture.name = "CivicPressure";
                if (texture.LoadImage(bytes)) return texture;
                UnityEngine.Object.Destroy(texture);
                ownedIcon = null;
                return null;
            }
        }

        // Logged once per registration; logging must never become a failure (Unity logging is absent in tests).
        private static void Warn(string reason)
        {
            if (warned) return;
            warned = true;
            try { Debug.LogWarning("[CivicPressure] Unified UI toolbar button unavailable, using the floating button: " + reason); }
            catch { }
        }
    }
}
