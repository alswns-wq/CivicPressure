using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using UnityEngine;

namespace CivicPressure
{
    // Two builds share this source: the Workshop build (CivicPressure.dll) and a local developer build
    // (CivicPressureDev.dll, compiled with DEV by "build.ps1 -Dev"). Different assembly names keep their types apart,
    // and both use the same save keys and settings so a city can move between them.
    internal static class Edition
    {
        internal const string WorkshopAssembly = "CivicPressure";
        internal const string DevAssembly = "CivicPressureDev";
        internal const string WorkshopName = "Civic Pressure (Prototype)";
        internal const string DevName = "Civic Pressure [DEV]";

#if DEV
        internal static readonly bool IsDev = true;
#else
        internal static readonly bool IsDev = false;
#endif

        internal static string ModName
        {
            get { return IsDev ? DevName : WorkshopName; }
        }

        internal static string Version()
        {
            System.Version version = typeof(Edition).Assembly.GetName().Version;
            return version.Major + "." + version.Minor + "." + version.Build;
        }

        // The other build of this mod, recognized by its assembly name and entry type.
        internal static bool IsOtherEdition(string assemblyName, string userModTypeName)
        {
            return userModTypeName == "CivicPressure.Mod" && assemblyName ==
                (IsDev ? WorkshopAssembly : DevAssembly);
        }

        // Running both builds at once would patch the game twice and write the same save data twice. The developer
        // build steps aside, so the Workshop build keeps working even though older Workshop releases have no check.
        internal static bool StandDown()
        {
            if (!IsDev) return false;
            try
            {
                foreach (PluginManager.PluginInfo plugin in Singleton<PluginManager>.instance.GetPluginsInfo())
                {
                    if (plugin == null || !plugin.isEnabled || plugin.userModInstance == null) continue;
                    System.Type type = plugin.userModInstance.GetType();
                    if (!IsOtherEdition(type.Assembly.GetName().Name, type.FullName)) continue;
                    Debug.LogError("[CivicPressure] DEV build disabled for this city: the Workshop Civic Pressure is " +
                        "also enabled. Enable only one of them in Content Manager > Mods.");
                    return true;
                }
            }
            catch (System.Exception error)
            {
                Debug.LogWarning("[CivicPressure] Could not check for the Workshop build: " + error.Message);
            }
            return false;
        }
    }
}
