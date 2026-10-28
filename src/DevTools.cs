#if DEV
using System;
using System.Threading;
using ColossalFramework;
using ColossalFramework.UI;
using UnityEngine;

// Developer build only (build.ps1 -Dev). Nothing in this file exists in the Workshop DLL.
// Text here is for the developer and is intentionally not translated.
namespace CivicPressure
{
    internal static partial class CivicComplaints
    {
        internal const int DevOpen = 1, DevVisit = 2, DevVisitTrouble = 3, DevOdd = 4;
        private static int devRequest, devOdd;
        private static volatile string devStatus = "";
        internal static string DevStatus { get { return devStatus; } }

        // UI thread: queued for the next complaint tick (about one real second).
        internal static void DevRequest(int kind)
        {
            if (kind == DevOdd) { Interlocked.Exchange(ref devOdd, 1); devStatus = "황당 민원 요청됨: 다음 처리 때 발생"; return; }
            Interlocked.Exchange(ref devRequest, kind);
            devStatus = "요청됨: 다음 처리 때 실행";
        }

        // Simulation thread, under CivicSimulation.Gate, before the regular complaint loop.
        private static void DevTick(CityView city, DateTime now)
        {
            int kind = Interlocked.Exchange(ref devRequest, 0);
            if (kind == 0) return;
            bool visit = kind == DevVisit || kind == DevVisitTrouble;
            int bestKey = -1, bestAffected = 0;
            for (int region = 0; region < city.Regions.Length; region++)
            {
                RegionView area = city.Regions[region];
                if (area == null) continue;
                for (int reason = 0; reason < ComplaintRules.ReasonCount; reason++)
                {
                    int key = region * ComplaintRules.Slots + reason;
                    int affected = Affected(area, region, reason);
                    if (affected <= 0 || affected <= bestAffected) continue;
                    ComplaintRecord existing;
                    if (records.TryGetValue(key, out existing) && existing.Open) continue;
                    if (visit)
                    {
                        ComplaintHome home;
                        if (!homes.TryGetValue(key, out home) || CivicVisits.Visiting(key) || !CivicVisits.CanVisit(region, city, home)) continue;
                        // The visitor's arrival opens the complaint through the normal rules, which need this ratio.
                        if (ComplaintRules.Rate(affected, Severe(area, region, reason), Population(area, region, reason)) <= ComplaintRules.OpenRatio(reason)) continue;
                    }
                    bestKey = key; bestAffected = affected;
                }
            }
            if (bestKey < 0)
            {
                devStatus = visit ? "방문 민원 후보 없음: 구청이 지정된 구나 시청이 있는 구에 접수 기준 이상의 피해와 대표 민원인 집이 필요합니다. 집계 완료 후 다시 시도하세요."
                    : "접수할 민원 후보 없음: 피해 주민이 있는 미접수 민원이 없습니다.";
                return;
            }
            int bestRegion = bestKey / ComplaintRules.Slots, bestReason = bestKey % ComplaintRules.Slots;
            ComplaintRecord c;
            if (!records.TryGetValue(bestKey, out c)) { c = new ComplaintRecord { Region = bestRegion, Reason = bestReason }; records.Add(bestKey, c); }
            c.QuietUntil = 0; c.ImprovingSince = 0;
            string name = city.Regions[bestRegion].Name + " · " + Happiness.Reasons[bestReason] + " (피해 " + bestAffected + "명)";
            if (!visit)
            {
                c.Open = true; c.Warned = false; c.Promised = false;
                if (c.Since == 0) c.Since = now.Ticks;
                c.Deadline = now.Ticks + GameClock.Hours(Difficulty.DeadlineHours);
                News(c, city, "[DEV] 민원을 강제로 접수했습니다.");
                devStatus = "강제 접수: " + name;
                return;
            }
            // Make the intake due now; the regular loop right after this sends the visitor.
            c.Since = now.Ticks - GameClock.Hours(ComplaintRules.IntakeHours(bestReason)) - 1;
            CivicVisits.ForceTrouble = kind == DevVisitTrouble;
            devStatus = (kind == DevVisitTrouble ? "방문 + 난동 예약: " : "방문 민원 예약: ") + name + " · 결과는 아래 '최근'에 표시";
        }
    }

    internal static partial class CivicUnrest
    {
        // DEV button: consumed on the next unrest step (simulation thread, inside CivicUnrest.Gate).
        internal static volatile bool DevEnd;
        internal static volatile string DevStatus = "";

        // Ends the protest or riot at once with the same cleanup as a normal disbanding (police released, traffic reopened,
        // complaints told), and also drops a pending march search or protest notice.
        private static void DevForceEnd(CityView city, DateTime now)
        {
            bool notice = CivicRecords.Notice != null, searching = search != null;
            search = null; manualRequest = 0; manualSearch = false; riotSearch = false; meetingTarget = null;
            CivicRecords.Notice = null;
            UnrestEvent e = state.Event;
            if (e == null)
            {
                closure = null;
                DevStatus = notice || searching ? "[DEV] 집회 예고·경로 탐색을 취소했습니다." : "[DEV] 진행 중인 시위·폭동이 없습니다.";
                message = notice || searching ? DevStatus : message;
                return;
            }
            bool riot = e.Riot;
            PreserveFires(e);
            CivicRecords.Finish(e, city, now, "[DEV] 강제 종료");
            PoliceDeployment.Release();
            state.Event = null; state.NextEligible = now.Ticks + GameClock.Hours(24); closure = null;
            policeTarget = 0; policeVehicle = 0; policeStation = 0; dispatch = false; nextDispatch = 0;
            passedRoad = 0; passedIndex = 0; onward = null; nextOnward = 0;
            CivicComplaints.End(e.Origin, now.Ticks);
            message = DevStatus = riot ? "[DEV] 폭동을 강제 종료했습니다." : "[DEV] 시위를 강제 종료했습니다.";
            view = new UnrestView { Text = message };
            Debug.Log("[CivicPressure] " + message);
        }
    }

    // Small draggable window with the developer tools. Created by Loading.OnLevelLoaded in the DEV build.
    internal sealed class DevPanel : UIPanel
    {
        private static DevPanel instance;
        private UILabel state;
        private UIButton riotWins, policeWins;
        private int assaultOutcome = -1;
        private static readonly string[] assaultOutcomes = { "무작위", "전복 없이 화재", "90도", "180도" };

        internal void Initialize()
        {
            instance = this;
            name = "CivicPressureDevPanel";
            backgroundSprite = "MenuPanel2";
            color = new Color32(214, 180, 120, 255);
            size = new Vector2(300, 834);
            relativePosition = new Vector3(12, 140);

            UILabel title = AddUIComponent<UILabel>();
            title.text = "Civic Pressure DEV 시험 도구";
            title.textScale = .85f; title.relativePosition = new Vector3(12, 12);
            UIDragHandle drag = AddUIComponent<UIDragHandle>();
            drag.relativePosition = Vector3.zero; drag.size = new Vector2(300, 36); drag.target = this;

            UIButton close = AddUIComponent<UIButton>();
            close.text = "X"; close.textScale = .8f;
            close.normalBgSprite = "ButtonMenu"; close.hoveredBgSprite = "ButtonMenuHovered"; close.pressedBgSprite = "ButtonMenuPressed";
            close.size = new Vector2(26, 26); close.relativePosition = new Vector3(266, 7);
            close.tooltip = "개발자 창 숨기기 · 도시 행정 창의 DEV 버튼으로 다시 열기";
            close.eventClick += delegate { Hide(); };

            Button("민원 즉시 접수 (피해 가장 큰 곳)", 44, delegate { CivicComplaints.DevRequest(CivicComplaints.DevOpen); });
            Button("방문 민원 발생", 82, delegate { CivicComplaints.DevRequest(CivicComplaints.DevVisit); });
            Button("방문 민원 + 난동 발생", 120, delegate { CivicComplaints.DevRequest(CivicComplaints.DevVisitTrouble); });
            Button("황당 민원 발생", 158, delegate { CivicComplaints.DevRequest(CivicComplaints.DevOdd); });
            Button("일상 행정 방문 1명 (아무 행정청)", 196, delegate { CivicVisits.ForceRoutineKind = 1; });
            Button("일상 행정 방문 1명 (시청으로)", 234, delegate { CivicVisits.ForceRoutineKind = 2; });
            Button("일상 행정 방문 1명 (구청으로)", 272, delegate { CivicVisits.ForceRoutineKind = 3; });
            Button("이동 중인 방문자 따라가기", 310, Follow);
            Button("시설 반대 발생 (주변 주택 가장 많은 시설)", 348, delegate { CivicStanding.DevOppose = true; });
            Button("주민 건의 발생", 386, delegate { CivicStanding.DevSuggest = true; });
            Button("7일 행정 평가 바로 받기", 424, delegate { CivicStanding.DevReview = true; });
            Button("시위·폭동 강제 종료", 462, delegate { CivicUnrest.DevStatus = "요청됨: 다음 처리 때 종료"; CivicUnrest.DevEnd = true; });
            UIButton outcomeButton = null;
            outcomeButton = Button("피해 결과: 무작위 (눌러 변경)", 500, delegate
            {
                assaultOutcome++; if (assaultOutcome > 2) assaultOutcome = -1;
                outcomeButton.text = "피해 결과: " + assaultOutcomes[assaultOutcome + 1] + " (눌러 변경)";
            });
            Button("선택 차량 습격 시험 (구급차 제외)", 538, delegate { DevVehicleAssault.RequestSelected(assaultOutcome); });
            Button("차량 시험 정리 (빈 잔해만 삭제)", 576, delegate { DevVehicleAssault.RequestClear(); });
            riotWins=Button("시위대 승리",614,delegate { DevCombatResult.Request(false); });
            policeWins=Button("경찰 승리",614,delegate { DevCombatResult.Request(true); });
            riotWins.width=policeWins.width=134;
            policeWins.relativePosition=new Vector3(154,614);
            riotWins.isEnabled=policeWins.isEnabled=false;

            state = AddUIComponent<UILabel>();
            state.autoSize = false; state.autoHeight = false; state.wordWrap = true;
            state.width = 276; state.height = 170; state.textScale = .7f;
            state.relativePosition = new Vector3(12, 654);
        }

        internal static void Toggle()
        {
            if (instance == null) return;
            if (instance.isVisible) instance.Hide();
            else { instance.Show(); instance.BringToFront(); }
        }

        internal static void Release() { instance = null; }

        private UIButton Button(string text, float y, MouseEventHandler click)
        {
            UIButton button = AddUIComponent<UIButton>();
            button.text = text; button.textScale = .8f;
            button.normalBgSprite = "ButtonMenu"; button.hoveredBgSprite = "ButtonMenuHovered"; button.pressedBgSprite = "ButtonMenuPressed";
            button.size = new Vector2(276, 32); button.relativePosition = new Vector3(12, y);
            button.eventClick += delegate { note = ""; };
            button.eventClick += click;
            return button;
        }

        private void Follow(UIComponent component, UIMouseEventParameter e)
        {
            try
            {
                uint citizen = CivicVisits.FollowCitizen;
                CitizenManager manager = Singleton<CitizenManager>.instance;
                ushort instance = citizen == 0 ? (ushort)0 : manager.m_citizens.m_buffer[citizen].m_instance;
                CameraController camera = ToolsModifierControl.cameraController;
                if (instance == 0 || camera == null) { CivicComplaintsStatus("따라갈 방문자가 없습니다. 방문 민원을 먼저 발생시키세요."); return; }
                InstanceID id = default(InstanceID);
                id.CitizenInstance = instance;
                camera.SetTarget(id, manager.m_instances.m_buffer[instance].GetLastFramePosition(), true);
            }
            catch (Exception error) { CivicComplaintsStatus("따라가기 실패: " + error.Message); }
        }

        private string note = "";
        private void CivicComplaintsStatus(string text) { note = text; }

        public override void Update()
        {
            base.Update();
            if (state == null || !isVisible) return;
            bool combat=!DevCombatResult.Resolving && DevCombatResult.CanRequest(CivicUnrest.View) && DevCombatResult.Current==null && DevCombatResult.Requested==null;
            riotWins.isEnabled=policeWins.isEnabled=combat;
            string text = (note.Length > 0 ? note : CivicComplaints.DevStatus) + "\n민원 방문자 " + CivicVisits.Travelling + "명 · 일상 방문자 " + CivicVisits.RoutineTravelling + "명 · 난동 " +
                CivicVisits.Disturbances + "건\n최근: " + CivicVisits.LastEvent + (CivicStanding.DevStatus.Length > 0 ? "\n" + CivicStanding.DevStatus : "") +
                (CivicStanding.Error.Length > 0 ? "\n" + CivicStanding.Error : "") +
                (CivicUnrest.DevStatus.Length > 0 ? "\n" + CivicUnrest.DevStatus : "") +
                (DevVehicleAssault.Status.Length > 0 ? "\n" + DevVehicleAssault.Status : "");
            if (DevRiotSuppression.Status.Length > 0) text += "\n" + DevRiotSuppression.Status;
            if (state.text != text) state.text = text;
        }
    }
}
#endif
