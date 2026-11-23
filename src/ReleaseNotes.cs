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
        internal const string Version = "0.6.9.2";
        private static SavedString seen;
        private static bool waiting;

        internal static string Title() { return Edition.ModName + " " + Version + " · " + L.T("업데이트 내역") + " - " + L.T("긴급 패치"); }

        internal static string Body()
        {
            return "• " + L.T("소방·경찰·의료 범위 밖 주택과 실제 대응 대기를 구분합니다. 시신 수거 경고는 현장이 속한 구의 한 민원에 모읍니다.") +
                "\n\n" + L.T("이 안내는 업데이트 후 한 번만 표시됩니다.") + "\n\n" + L.T("AI를 활용해 개발한 모드로, 놓친 오류가 있을 수 있습니다.\n버그·개선 제안은 Steam → CP → 토론의 고정 글\n「Bug Reports & Suggestions」에 남겨 주세요. 빠르게 확인하겠습니다.");
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
