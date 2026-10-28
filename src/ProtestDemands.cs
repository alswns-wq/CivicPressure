using System;
using System.Collections.Generic;

namespace CivicPressure
{
    internal static class ProtestDemands
    {
        internal static bool Resolved(RegionView region)
        {
            if (region == null || region.Residents <= 0 || region.Affected == null || region.Severe == null || region.Abandoned > 0) return false;
            for (int reason = 0; reason < region.Affected.Length; reason++)
                if (!ComplaintRules.Recovered(reason, region.Affected[reason], region.Severe[reason], region.Residents)) return false;
            return true;
        }
        internal static void Capture(IncidentReport report, CityView city, DateTime now, bool late)
        {
            if (report == null || report.DemandBaseline != null) return;
            RegionView region = report.Origin < 0 ? city.All : city.Regions[report.Origin];
            if (region == null || region.Affected == null) return;
            report.DemandBaseline = Counts(region);
            report.DemandCaptured = now.Ticks; report.DemandLate = late;
        }
        private static int[] Counts(RegionView region)
        {
            int[] counts = new int[Happiness.Weights.Length + 3];
            Array.Copy(region.Affected, counts, Math.Min(region.Affected.Length, Happiness.Weights.Length));
            counts[counts.Length - 3] = region.WarningBuildings;
            counts[counts.Length - 2] = region.SevereBuildings;
            counts[counts.Length - 1] = region.Abandoned;
            return counts;
        }
        internal static string Change(int before, int current)
        {
            if (current == 0) return L.T("해결");
            if (before == 0) return L.T("추가 발생");
            if (current == before) return L.T("변화 없음");
            double percent = Math.Abs((current - (double)before) / before) * 100;
            return percent.ToString("0") + "% " + (current < before ? L.T("감소 · 개선 중") : L.T("증가 · 악화"));
        }
        internal static string[] Pages(IncidentReport report, CityView city, UnrestView unrest)
        {
            if (!unrest.Active || report == null) return new string[] { L.T("진행 중인 시위·폭동이 없습니다.") };
            RegionView region = report.Origin < 0 ? city.All : city.Regions[report.Origin];
            if (region == null || region.Affected == null || report.DemandBaseline == null)
                return new string[] { L.T("발생 지역의 집계를 기다리고 있습니다.\n요구사항과 개선 정도는 집계 완료 후 표시됩니다.") };
            int[] current = Counts(region), baseline = report.DemandBaseline;
            List<string> rows = new List<string>();
            for (int i = 0; i < baseline.Length; i++) {
                if (baseline[i] == 0 && current[i] == 0) continue;
                string name = i < Happiness.Reasons.Length ? Happiness.Reasons[i] :
                    i == baseline.Length - 3 ? L.T("경고가 남은 건물") : i == baseline.Length - 2 ? L.T("심각 문제가 남은 건물") : L.T("버려진 건물");
                string unit = i < Happiness.Reasons.Length ? L.T("명") : L.T("곳");
                rows.Add(name + "  ·  " + Change(baseline[i], current[i]) + "\n" +
                    baseline[i].ToString("N0") + unit + "  →  " + current[i].ToString("N0") + unit);
            }
            if (rows.Count == 0) rows.Add(L.T("집계된 생활 불만과 문제 건물이 없습니다."));
            string condition = unrest.Riot ? L.T("폭동은 요구 개선과 별도로 경찰 진압 성공이 필요합니다.") :
                unrest.Manual ? L.T("수동 시위는 문제 개선과 별도로 시간 조건을 적용합니다.") :
                Resolved(region) ? L.T("지역 문제 해소 확인 · 다음 시위 처리 시 해산 판정") :
                L.T("자진 해산: 모든 사유가 통계상 개선 기준 충족·버려진 건물 복구 또는 시간 경과");
            string heading = report.Area + L.T("\n발생 원인: ") + report.Cause + "\n" +
                (report.DemandLate ? L.T("비교 기준: 업데이트 후 최초 집계") : L.T("비교 기준: 시위 발생 당시 집계")) + "\n" +
                L.T("현재 집계: ") + city.CensusUpdatedUtc.ToLocalTime().ToString("HH:mm:ss") + "\n\n";
            List<string> pages = new List<string>();
            for (int start = 0; start < rows.Count; start += 5) {
                string body = heading;
                for (int i = start; i < Math.Min(start + 5, rows.Count); i++) body += rows[i] + "\n\n";
                pages.Add(body + condition);
            }
            return pages.ToArray();
        }
    }
}
