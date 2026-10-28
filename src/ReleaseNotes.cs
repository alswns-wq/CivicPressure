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
        internal const string Version = "0.6.7";
        private static SavedString seen;
        private static bool waiting;

        internal static string Title() { return Edition.ModName + " " + Version + " · " + L.T("업데이트 내역"); }

        internal static string Body()
        {
            return "• " + L.T("시위·폭동의 개인별 이동과 차량 습격을 개선했습니다. 피해 차량은 승객 하차 후 화재·전복 상태로 남으며, 종료 후 순차 정리됩니다. 구급차는 습격에서 제외합니다.") +
                "\n\n• " + L.T("경찰 저지선·진압·후퇴·연행과 구급대 치료·증원을 추가했습니다. 승패는 현장 전투로 결정되며, 실탄 사격은 유혈 없는 사망을 일으킬 수 있습니다.") +
                "\n\n• " + L.T("군중·진압 효과음과 사망 표시를 추가하고, 사건 보고서에 양측 부상자·사망자 수를 기록합니다.") +
                "\n\n• " + L.T("건강·범죄·쓰레기·소방 위험을 정보 보기의 건물 상태값에 맞췄습니다. 불량 방향 60%부터 경고, 80%부터 심각으로 집계합니다.") +
                "\n\n• " + L.T("교육 민원은 해당 교육 단계의 건물 학력 상태가 나쁘고 실제 학교 배정도 부족할 때만 집계합니다. 낮은 학력만으로 민원을 만들지 않습니다.") +
                "\n\n• " + L.T("도시 진행에서 아직 해금하지 않은 의료·치안·소방 서비스는 부족 민원과 감점에서 제외합니다.") +
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
