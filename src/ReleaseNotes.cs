using System;
using ColossalFramework;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // "What's new" notice shown once in the main menu after the mod updates. The last version shown is kept in
    // CivicPressure.cgs; the DEV build keeps its own record so previewing it does not hide the Workshop notice.
    internal static class ReleaseNotes
    {
        // Must match the assembly version (Major.Minor.Build). Update the notes below with every release.
        internal const string Version = "0.6.8";
        private static SavedString seen;
        private static bool waiting;

        internal static string Title() { return Edition.ModName + " " + Version + " · " + L.T("업데이트 내역"); }

        internal static string Body()
        {
            return "• " + L.T("일부 환경에서 Unified UI에 도시 행정 아이콘이 표시되지 않던 문제를 수정했습니다.") +
                "\n\n" + L.T("이 안내는 업데이트 후 한 번만 표시됩니다.");
        }

        internal static bool ShouldShow(string lastShown, string current)
        {
            return !string.IsNullOrEmpty(current) && lastShown != current;
        }

        // Workshop build: once per version. DEV build: once per installed build, so each preview shows the notice.
        internal static string Stamp
        {
            get { return Edition.IsDev ? Version + "+" + typeof(ReleaseNotes).Assembly.ManifestModule.ModuleVersionId.ToString("N") : Version; }
        }

        internal static string SettingName { get { return Edition.IsDev ? "LastReleaseNotesDev" : "LastReleaseNotes"; } }

        internal static void OnEnabled()
        {
            try
            {
                if (UIView.GetAView() != null) Show();
                else if (!waiting) { LoadingManager.instance.m_introLoaded += Show; waiting = true; }
            }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Release notes skipped: " + error.Message); }
        }

        internal static void OnDisabled()
        {
            try { if (waiting) LoadingManager.instance.m_introLoaded -= Show; }
            catch { }
            waiting = false;
        }

        // The launcher's "Continue" starts the game with --continuelastsave and goes straight into the last city, so a
        // main-menu notice would vanish unseen (confirmed in output_log "Raw Command Line", 2026-09-17).
        internal static bool ContinueLaunch(string[] args)
        {
            if (args == null) return false;
            foreach (string arg in args)
                if (arg != null && string.Equals(arg.TrimStart('-'), "continuelastsave", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Show()
        {
            OnDisabled();
            try
            {
                // Only in the main menu: enabling the mod inside a city waits for the next game start.
                if (Singleton<LoadingManager>.instance.m_loadingComplete) return;
                if (ContinueLaunch(Environment.GetCommandLineArgs()))
                {
                    Debug.Log("[CivicPressure] Launched with Continue: update notice deferred to the city.");
                    return;
                }
                Present();
            }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Release notes skipped: " + error.Message); }
        }

        // Once per update inside a city, for players who have not seen it in the main menu (mainly "Continue").
        internal static void OnCityLoaded()
        {
            try { Present(); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Release notes skipped in city: " + error.Message); }
        }

        private static void Present()
        {
            LanguageSettings.Initialize();
            if (seen == null) seen = new SavedString(SettingName, "CivicPressure", "", true);
            if (!ShouldShow(seen.value, Stamp)) return;
            NoticePanel.Open(Title(), Body());
            seen.value = Stamp;
        }
    }
}
