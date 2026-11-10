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
        internal const string Version = "0.6.9";
        private static SavedString seen;
        private static bool waiting;

        internal static string Title() { return Edition.ModName + " " + Version + " · " + L.T("업데이트 내역"); }

        internal static string Body()
        {
            return "• " + L.T("민원 안내와 재발 비교를 개선했습니다. AI 시험창은 보류하여 숨겼습니다.") +
                "\n\n" + L.T("출동 경찰차의 경로 탐색 실패·장시간 정체 시 재탐색을 시도합니다. 반복 실패하면 출동 배정을 해제하고 복귀 처리합니다.") +
                "\n\n" + L.T("도로 변경 후 행진 경로를 재탐색하며, 설정에서 현재 시위·폭동을 강제종료할 수 있습니다.") +
                "\n\n" + L.T("가까운 인물의 뼈대 자세 사이를 보간하고 걷기·정지 전환 시간을 일정하게 맞춰 움직임의 끊김을 줄였습니다.") +
                "\n\n" + L.T("경찰과 폭동대의 과한 몸통 흔들림·기울기·앞뒤 움직임을 줄이고 부드러운 동작 연결을 유지했습니다.") +
                "\n\n" + L.T("연행 시 현장의 가까운 경찰차로 호송하도록 개선했습니다. 대상 차량이 철수하면 다른 차량을 다시 선택합니다.") +
                "\n\n" + L.T("시위에서 폭동으로 전환할 때 기존 군중 위치를 유지하도록 수정했습니다. 경로 확인 실패 시 마지막 유효 위치를 유지하고, 배치되지 않은 인원을 잘못된 위치에 표시하지 않습니다.") +
                "\n\n" + L.T("저장 시 진행 중인 차량 습격을 끝내고 비어 있는 피해 차량을 정리합니다. 승객 상태를 확인할 수 없으면 저장을 중단해 보호합니다.") +
                "\n\n" + L.T("민원 제목 옆 [?]에서 안내를 확인합니다. 난이도 1~3은 판정 기준과 해결 방법, 4는 판정 기준만 표시하며 5는 [?]를 숨깁니다.") +
                "\n\n" + L.T("범죄·치료 대기는 같은 대상에서 3게임 시간 지속 관찰 후 집계합니다. 치료 중인 시민은 제외하며, 도시를 불러오면 관찰을 다시 시작합니다.") +
                "\n\n" + L.T("경찰차 재사용 시 승무원 상태 혼선, 도로 철거 후 진압 정지, 이전 평가 기간 보상의 집계 오류를 수정했습니다.") +
                "\n\n" + L.T("행정 평가 저장 형식이 갱신됩니다. 이전 저장은 불러올 수 있습니다. 구버전으로 돌아갈 수 있도록 업데이트 전 도시를 별도로 저장해 주세요.") +
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
