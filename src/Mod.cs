using ICities;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    public sealed class Mod : IUserMod
    {
        public string Name
        {
            get { return Edition.ModName; }
        }

        public string Description
        {
            get { return Edition.Version(); }
        }

        // Called by the game when the mod is enabled (also at start-up): shows the one-time update notice in the main menu.
        public void OnEnabled()
        {
            ReleaseNotes.OnEnabled();
        }

        public void OnDisabled()
        {
            ReleaseNotes.OnDisabled();
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            LanguageSettings.CreateUI(helper);
        }
    }

    public sealed class Loading : LoadingExtensionBase
    {
        private bool cityLoaded;
        private CivicPanel panel;
#if DEV
        private DevPanel devPanel;
#endif
        private GameObject crowd;
        private bool patched;

        public override void OnCreated(ILoading loading)
        {
            LanguageSettings.Initialize();
            base.OnCreated(loading);
            Debug.Log("[CivicPressure] Loading extension created. Version " + Edition.Version() + ".");
        }

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);
            cityLoaded = mode == LoadMode.NewGame || mode == LoadMode.LoadGame
                || mode == LoadMode.NewGameFromScenario;

            // The DEV build does nothing while the Workshop build is also enabled.
            if (cityLoaded && Edition.StandDown()) cityLoaded = false;

            if (cityLoaded)
            {
                LanguageSettings.Initialize();
                CivicSimulation.Start();
                try
                {
                    ShortcutSettings.Initialize();
                    UnrestSettings.Initialize();
                    patched = UnrestBootstrap.Start();
                    CivicUnrest.Enable(patched);
                    crowd = new GameObject("CivicPressureCrowdPreview");
                    crowd.AddComponent<CrowdPreview>();
                    panel = (CivicPanel)UIView.GetAView().AddUIComponent(typeof(CivicPanel));
                    panel.Initialize();
                    if (patched)
                    {
                        DevVehicleAssault.Install();
                        crowd.AddComponent<DevAssaultActors>();
                    }
#if DEV
                    devPanel = (DevPanel)UIView.GetAView().AddUIComponent(typeof(DevPanel));
                    devPanel.Initialize();
#endif
                    // Launcher "Continue" skips the main menu: show the update notice once in the city instead.
                    ReleaseNotes.OnCityLoaded();
                    Debug.Log("[CivicPressure] City loaded: " + mode + ". Phase 3 UI ready; unrest hooks=" + patched +
                        (patched ? "" : " (" + UnrestBootstrap.Problem + ")") +
                        (UnrestBootstrap.TmpeDetourMissing ? "; TM:PE detour unavailable, vanilla routing hook only" : ""));
                }
                catch (System.Exception error)
                {
                    Cleanup();
                    Debug.LogError("[CivicPressure] Could not initialize administration UI. " + error);
                }
            }
        }

        public override void OnLevelUnloading()
        {
            if (cityLoaded)
            {
                Debug.Log("[CivicPressure] City unloading.");
            }

            cityLoaded = false;
            Cleanup();
            base.OnLevelUnloading();
        }

        public override void OnReleased()
        {
            cityLoaded = false;
            Cleanup();
            Debug.Log("[CivicPressure] Loading extension released.");
            base.OnReleased();
        }

        private void Cleanup()
        {
            ShortcutSettings.CancelCapture();
            CivicSimulation.Stop();
            DevVehicleAssault.Remove();
            if (patched) { UnrestPatches.Remove(); patched = false; }
            if (crowd != null) UnityEngine.Object.Destroy(crowd);
            crowd = null;
            UnifiedUIButton.Release();
            ReviewWindow.Release();
#if DEV
            DevPanel.Release();
            if (devPanel != null) UnityEngine.Object.Destroy(devPanel.gameObject);
            devPanel = null;
#endif
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null;
        }
    }
}
