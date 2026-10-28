using System;

using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    public sealed class CivicPanel : UIPanel
    {
        private UIButton toggle;
        private FloatingButton floatingButton;
        private UIButton assignCity;
        private UIButton assignDistrict;
        private UIButton clear;
        private UILabel selection;
        private UILabel hallHeading, administrationSummary;
        private UIButton editAdministration;
        private UIPanel administrationEditor;
        private bool editingAdministration;
        private UILabel city;
        private UIPanel unrestBody;
        private UILabel unrestTitle, unrestOrigin, unrestTime, unrestRepair, unrestPolice, unrestMessage, manualTarget;
        private UILabel scoreLabel, legend, categorySummary, unrestSection;
        private UITextureSprite complaintStripe, unrestStripe;
        private readonly UIPanel[] tableBands = new UIPanel[8];
        private readonly UITextureSprite[] rowMarks = new UITextureSprite[8];
        private Texture2D flatTexture;
        private UIButton happinessTab, unrestTab;
        private UIPanel happinessBody;
        private UILabel regionTitle, regionSubtitle, buildingSummary, abandonedSummary;
        private readonly UILabel[,] cells = new UILabel[8, 5];
        private int needsPageIndex;
        private readonly UIButton[] categoryTabs = new UIButton[5];
        private static readonly int[][] categoryReasons = {
            new int[] { 8, 0, 1, 4, 16, 25 },
            new int[] { 3, 2, 15, 17, 18 },
            new int[] { 6, 11, 13, 5, 12, 14 },
            new int[] { 9, 10, 7, 22, 23 }, new int[] { 19, 20, 21, 24 }
        };
        private UILabel status;
        private float nextPoll;
        private ushort observed;
        private ushort openedFor;
        private int browse = -2;
        private bool initialized;
        private bool showUnrest;
        private UIButton police;
        private UIButton locate;
        private UIButton locatePolice;
        private bool followingPolice;
        private UIButton spawnProtest, spawnRiot;
        private int eventOrigin = -1;
        private bool observedUnrest;
        private bool showComplaints = true;
        private UIButton complaintsTab, promise, reject, agree, complaintLocation;
        private UIPanel complaintsBody, helpWindow;
        private UILabel complaintTitle, complaintStage, complaintDetail, complaintRoad, complaintCount, complaintTimer;
        private int complaintIndex;
        private ComplaintView currentComplaint;
        private UIPanel reportWindow, demandWindow, trendWindow;
        private UILabel trendHeading, trendSummary, trendDetails, trendDelta;
        private readonly UITextureSprite[] trendBars = new UITextureSprite[25];
        private UILabel demandText, demandPage;
        private int demandIndex;
        private UIButton demandButton, meetingYes, meetingNo;
        private long shownMeetingStamp;
        private long cachedTrendRevision=-1, drawnTrendRevision=-1, drawnReportRevision=-1;
        private int cachedTrendRegion=-999, drawnTrendRegion=-999, drawnReportRegion=-999, drawnReportIndex;
        private bool drawnHistoryMode;
        private string drawnTrendName, cachedTrendSummary="";
        private TrendPoint[] cachedTrendPoints=new TrendPoint[0];
        private UILabel reportText, reportPage;
        private int reportIndex;
        private bool historyMode = true;
        private int historyRegion = -1;
        private UIButton reportMode;
        private bool mapVisible { get { return UnrestSettings.MapMarkers; } set { UnrestSettings.SetMapMarkers(value); } }
        private string mapStatus = "";
        private UIButton mapToggle;
        private UIPanel toolsMenu;
        private readonly UIButton[] mapPins = new UIButton[24];
        // Visitor pins: the mod's own office visitors, so they can be told apart from ordinary citizens.
        private bool visitorsVisible { get { return Features.VisitorMarkers; } set { UnrestSettings.SetVisitorMarkers(value); } }
        private UIButton visitorToggle;
        private readonly UIButton[] visitorPins = new UIButton[16];
        private readonly uint[] visitorTargets = new uint[16];
        private readonly ushort[] visitorOffices = new ushort[16];
        private readonly ComplaintMarker[] mapTargets = new ComplaintMarker[24];
        private readonly Rect[] occupiedPins = new Rect[24];
        private UITextureSprite scoreFill;
        private static readonly Color32 Ink = new Color32(230, 238, 245, 255);
        private static readonly Color32 Muted = new Color32(169, 190, 205, 255);
        private static readonly Color32 Accent = new Color32(120, 214, 224, 255);

        // Decorative panels never intercept map/popup input.
        private UITextureSprite Card(UIComponent parent, float x, float y, float w, float h, Color32 tint)
        {
            if (flatTexture == null) {
                flatTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                flatTexture.name = "CivicPressureFlatUI";
                flatTexture.SetPixel(0, 0, Color.white); flatTexture.Apply();
            }
            UITextureSprite panel = parent.AddUIComponent<UITextureSprite>();
            panel.relativePosition = new Vector3(x, y); panel.size = new Vector2(w, h);
            panel.texture = flatTexture;
            panel.color = new Color32((byte)(tint.r / 4), (byte)(tint.g / 4), (byte)(tint.b / 4), tint.a);
            panel.isInteractive = false;
            return panel;
        }

        // Use the game's textured dark surface for broad backgrounds; thin indicators stay flat.
        private static void Surface(UIComponent parent, float x, float y, float w, float h, Color32 tint)
        {
            UIPanel panel = parent.AddUIComponent<UIPanel>();
            panel.relativePosition = new Vector3(x, y); panel.size = new Vector2(w, h);
            panel.backgroundSprite = "MenuPanel2"; panel.color = tint;
            panel.isInteractive = false;
        }
        private static void TabStyle(UIButton button, bool selected)
        {
            button.isEnabled = true;
            button.color = selected ? new Color32(85, 164, 184, 255) : new Color32(170, 187, 200, 255);
            button.textColor = selected ? new Color32(255, 255, 255, 255) : Muted;
        }

        internal void Initialize()
        {
            UIView ui = UIView.GetAView();
            name = "CivicPressurePanel";
            size = new Vector2(660, 690);
            relativePosition = new Vector3(Math.Max(8, ui.fixedWidth - width - 24), Math.Max(8, Math.Min(70, ui.fixedHeight - height - 12)));
            backgroundSprite = "MenuPanel2";
            color = new Color32(184, 202, 217, 255);
            Surface(this, 8, 6, 644, 36, new Color32(140, 170, 192, 255));
            Surface(this, 12, 44, 636, 84, new Color32(205, 218, 229, 255));
            Surface(this, 12, 132, 636, 69, new Color32(150, 189, 211, 255));
            Surface(this, 12, 244, 636, 346, new Color32(210, 224, 233, 255));
            Surface(this, 12, 623, 636, 40, new Color32(145, 166, 185, 255));
            UILabel heading = Label(L.T("CIVIC PRESSURE  /  도시 행정"), 20, 15, 330, 24, L.Current == 0 ? .95f : .78f);
            heading.textColor = Accent;
#if DEV
            heading.width = 290;
            UIButton devTools = Button(this, "DEV", 310, 12, 42, 28);
            devTools.textScale = .72f;
            devTools.tooltip = "개발자 창 열기/닫기";
            devTools.eventClick += delegate { DevPanel.Toggle(); };
#endif
            Card(this, 22, 194, 616, 4, new Color32(90, 110, 125, 255));
            scoreFill = Card(this, 22, 194, 616, 4, Accent);
            UIDragHandle drag = AddUIComponent<UIDragHandle>();
            drag.relativePosition = Vector3.zero; drag.size = new Vector2(350, 42); drag.target = this;
#if DEV
            drag.width = 300;
#endif
            Button(this, L.T("도구"), 464, 12, 100, 28).eventClick += delegate { toolsMenu.isVisible = !toolsMenu.isVisible; };
            Button(this, "?", 572, 12, 30, 28).eventClick += delegate { toolsMenu.Hide(); OpenHelp(); };
            Button(this, "X", 612, 12, 30, 28).eventClick += delegate { ClosePopup(); };
            hallHeading = Cell(this, L.T("행정청 지정"), 20, 46, 620, 30, 1.35f, false);
            selection = Cell(this, L.T("건물을 선택하세요."), 20, 76, 620, 16, 0.8f, false);
            selection.textColor = Muted;
            administrationSummary = Cell(this, "", 20, 102, 438, 24, 0.9f, false);
            editAdministration = Button(this, L.T("행정청 변경"), 470, 96, 170, 30);
            editAdministration.eventClick += delegate { editingAdministration = !editingAdministration; Render(CivicSimulation.View); };
            administrationEditor = AddUIComponent<UIPanel>();
            administrationEditor.relativePosition = new Vector3(20, 248); administrationEditor.size = new Vector2(620, 342);
            Cell(administrationEditor, L.T("시청 · 구청 변경"), 0, 0, 620, 30, 1.15f, false);
            UILabel editHelp = Cell(administrationEditor, L.T("지도에서 변경할 건물을 선택한 뒤 아래 버튼을 누르세요.\n구청은 해당 일반 지구 안의 건물을 선택해야 합니다."), 0, 48, 620, 68, 0.95f, false);
            editHelp.wordWrap = true;
            Cell(administrationEditor, L.T("지정 해제는 현재 선택한 행정청에만 적용됩니다."), 0, 190, 620, 26, 0.85f, false);
            assignCity = Button(this, L.T("시청으로 지정"), 20, 92, 200, 32);
            assignDistrict = Button(this, L.T("구청으로 지정"), 230, 92, 200, 32);
            clear = Button(this, L.T("지정 해제"), 440, 92, 200, 32);
            assignCity.eventClick += delegate { Send(0); };
            assignDistrict.eventClick += delegate { Send(1); };
            clear.eventClick += delegate { Send(2); };
            scoreLabel = Cell(this, L.T("집계 중…"), 20, 136, 620, 32, 1.3f, false);
            city = Cell(this, "", 20, 172, 365, 18, 0.85f, false);
            trendDelta = Cell(this, "", 385, 172, 150, 18, .72f, true);
            Button(this, L.T("추이 보기"), 542, 168, 98, 24).eventClick += delegate { OpenTrend(); };
            city.textColor = Muted;
            Button(this, L.T("시 전체"), 20, 206, 148, 32).eventClick += delegate { browse = -2; Render(CivicSimulation.View); };
            Button(this, L.T("이전 구"), 177, 206, 148, 32).eventClick += delegate { Cycle(-1); };
            Button(this, L.T("다음 구"), 334, 206, 148, 32).eventClick += delegate { Cycle(1); };
            Button(this, L.T("다시 집계"), 491, 206, 149, 32).eventClick += delegate { CivicSimulation.Refresh(); };
            happinessBody = AddUIComponent<UIPanel>();
            happinessBody.relativePosition = new Vector3(16, 248); happinessBody.size = new Vector2(628, 342);
            regionTitle = Cell(happinessBody, L.T("집계 중…"), 4, 0, 330, 26, 1.0f, false);
            regionSubtitle = Cell(happinessBody, "", 4, 28, 620, 24, 0.82f, false);
            buildingSummary = Cell(happinessBody, "", 338, 3, 282, 22, 0.72f, true);
            string[] categories = { L.T("주거·환경"), L.T("기반시설·교통"), L.T("보건·안전"), L.T("고용·교육"), L.T("상업·물류") };
            for (int i = 0; i < categoryTabs.Length; i++) {
                int page = i;
                categoryTabs[i] = Button(happinessBody, categories[i], 4 + i * 124, 55, 120, 27);
                categoryTabs[i].eventClick += delegate { needsPageIndex = page; Render(CivicSimulation.View); };
            }
            
            string[] titles = { L.T("불만 항목"), L.T("경고(명)"), L.T("심각(명)"), L.T("피해 비율"), L.T("감점") };
            int[] xs = { 8, 208, 310, 412, 516 }, ws = { 194, 96, 96, 98, 98 };
            for (int column = 0; column < 5; column++)
                Cell(happinessBody, titles[column], xs[column], 84, ws[column], 24, 0.82f, column > 0).textColor = new Color32(172, 206, 225, 255);
            for (int row = 0; row < 8; row++)
            {
                UIPanel band = happinessBody.AddUIComponent<UIPanel>();
                tableBands[row] = band;
                band.relativePosition = new Vector3(0, 111 + row * 30); band.size = new Vector2(628, 29);
                band.clipChildren = true;
                Surface(band, 0, 0, 628, 29, row % 2 == 0 ? new Color32(132, 164, 184, 255) : new Color32(100, 128, 148, 255));
                band.color = Color.white;
                rowMarks[row] = Card(band, 0, 4, 3, 21, Muted);
                for (int column = 0; column < 5; column++) {
                    cells[row, column] = Cell(band, "—", xs[column], 0, ws[column], 29, 0.88f, column > 0);
                    cells[row, column].verticalAlignment = UIVerticalAlignment.Middle;
                    cells[row, column].padding = new RectOffset(0, 0, 0, 0);
                }
                cells[row, 0].text = Happiness.Reasons[row];
                if (row == 3) cells[row, 0].text = L.T("전기·수도 등 기반시설");
            }
            categorySummary = Cell(happinessBody, "", 8, 296, 612, 23, .92f, false);
            abandonedSummary = Cell(happinessBody, "", 8, 323, 612, 19, 0.78f, false);
            abandonedSummary.textColor = Muted;
            unrestBody = AddUIComponent<UIPanel>();
            unrestBody.relativePosition = new Vector3(20, 248); unrestBody.size = new Vector2(620, 300);
            Surface(unrestBody, -2, 97, 624, 31, new Color32(164, 193, 213, 255));
            Surface(unrestBody, -2, 129, 624, 31, new Color32(198, 217, 231, 255));
            Surface(unrestBody, -2, 161, 624, 32, new Color32(164, 193, 213, 255));
            unrestStripe = Card(unrestBody, 0, 4, 4, 53, Accent);
            unrestTitle = Cell(unrestBody, "", 12, 0, 608, 30, 1.2f, false);
            unrestOrigin = Cell(unrestBody, "", 12, 36, 438, 24, 0.88f, false);
            demandButton = Button(unrestBody, L.T("요구사항 보기"), 466, 32, 154, 30);
            demandButton.eventClick += delegate { OpenDemands(); };
            unrestOrigin.textColor = Muted;
            unrestSection = Cell(unrestBody, L.T("진행 상황"), 8, 72, 612, 24, 0.9f, false);
            unrestSection.textColor = Accent;
            meetingYes=Button(unrestBody,L.T("면담 수락"),352,68,128,28);
            meetingNo=Button(unrestBody,L.T("거절"),492,68,128,28);
            meetingYes.eventClick+=delegate{CivicUnrest.RequestMeeting(shownMeetingStamp,true);};
            meetingNo.eventClick+=delegate{CivicUnrest.RequestMeeting(shownMeetingStamp,false);};
            unrestTime = Cell(unrestBody, "", 8, 103, 604, 24, 0.92f, false);
            unrestRepair = Cell(unrestBody, "", 8, 134, 604, 24, 0.92f, false);
            unrestPolice = Cell(unrestBody, "", 8, 165, 604, 24, 0.88f, false);
            unrestMessage = Cell(unrestBody, "", 0, 197, 620, 34, 0.8f, false);
            unrestMessage.wordWrap = true;
            manualTarget = Cell(unrestBody, "", 0, 271, 620, 24, 0.85f, false);
            spawnProtest = Button(this, L.T("시위 발생"), 20, 552, 300, 32);
            spawnRiot = Button(this, L.T("폭동 발생"), 340, 552, 300, 32);
            spawnProtest.tooltip = L.T("현재 조회 지역에서 수동 발생 · 행복도/빈도 조건 무시 · 48 게임 시간 유지");
            spawnRiot.tooltip = L.T("시위를 폭동으로 전환하거나 새 폭동 발생 · 실제 건물/도로 피해 · 경찰 도착 후 진압 필요");
            spawnProtest.eventClick += delegate { CivicUnrest.RequestEvent(CivicSimulation.View.Generation, eventOrigin, false); };
            spawnRiot.eventClick += delegate { CivicUnrest.RequestEvent(CivicSimulation.View.Generation, eventOrigin, true); };
            legend = Cell(this, "", 20, 596, 620, 24, 0.85f, false);
            complaintsTab = Button(this, L.T("민원"), 20, 628, 200, 32);
            happinessTab = Button(this, L.T("행복도"), 230, 628, 200, 32);
            unrestTab = Button(this, L.T("시위 · 폭동"), 440, 628, 200, 32);
            complaintsTab.eventClick += delegate { showComplaints = true; showUnrest = false; editingAdministration = false; Render(CivicSimulation.View); };
            happinessTab.eventClick += delegate { showComplaints = false; showUnrest = false; editingAdministration = false; Render(CivicSimulation.View); };
            unrestTab.eventClick += delegate { showComplaints = false; showUnrest = true; editingAdministration = false; Render(CivicSimulation.View); };
            complaintsBody = AddUIComponent<UIPanel>();
            complaintsBody.relativePosition = new Vector3(20, 248); complaintsBody.size = new Vector2(620, 342);
            Surface(complaintsBody, -2, 39, 624, 135, new Color32(166, 196, 217, 255));
            Surface(complaintsBody, -2, 178, 624, 103, new Color32(196, 215, 230, 255));
            complaintCount = Cell(complaintsBody, "", 0, 0, 380, 26, 1.0f, false);
            Button(complaintsBody, L.T("이전"), 400, 0, 100, 28).eventClick += delegate { complaintIndex--; Render(CivicSimulation.View); };
            Button(complaintsBody, L.T("다음"), 510, 0, 100, 28).eventClick += delegate { complaintIndex++; Render(CivicSimulation.View); };
            complaintStripe = Card(complaintsBody, 0, 47, 4, 117, Accent);
            complaintTitle = Cell(complaintsBody, "", 10, 45, 600, 28, 1.05f, false);
            complaintStage = Cell(complaintsBody, "", 10, 77, 600, 24, .88f, false);
            complaintTimer = Cell(complaintsBody, "", 10, 104, 600, 26, 1.08f, false);
            complaintDetail = Cell(complaintsBody, "", 10, 134, 600, 38, .8f, false);
            complaintDetail.wordWrap=true;
            complaintRoad = Cell(complaintsBody, "", 10, 185, 600, 94, .88f, false);
            complaintRoad.wordWrap = true;
            complaintLocation = Button(complaintsBody, L.T("문제 장소 보기"), 0, 292, 200, 32);
            complaintLocation.eventClick += delegate {
                CameraController camera = ToolsModifierControl.cameraController;
                if (currentComplaint == null || !currentComplaint.HasRoad || camera == null) return;
                if (currentComplaint.Visitor != 0) { FollowVisitor(camera, currentComplaint.Visitor); return; }
                if (currentComplaint.Kind == ComplaintKind.Opposition) { followingPolice = false; camera.ClearTarget(); camera.m_targetPosition = currentComplaint.Position; camera.m_targetSize = 150; return; }
                Vector3 destination;
                if(currentComplaint.Hotspot!=null) {
                    if(!currentComplaint.Hotspot.Live()) { status.text=L.T("현장이 변경되었습니다. 다시 집계해 주세요.");return; }
                    destination=currentComplaint.Hotspot.Position;
                }
                else if(currentComplaint.Site!=null) {
                    if(!currentComplaint.Site.Position(out destination)) { status.text=L.T("현장이 변경되었습니다. 다시 집계해 주세요.");return; }
                }
                else {
                    if(currentComplaint.Home==null || !currentComplaint.Home.Position(out destination))return;
                }
                followingPolice = false; camera.ClearTarget(); camera.m_targetPosition = destination; camera.m_targetSize = currentComplaint.Home!=null || currentComplaint.Site!=null ? 60 : 120;
            };
            promise = Button(complaintsBody, L.T("수락"), 210, 292, 200, 32);
            promise.eventClick += delegate { DecideComplaint(true); };
            reject = Button(complaintsBody, L.T("거절"), 420, 292, 200, 32);
            promise.color = new Color32(105, 190, 181, 255);
            reject.textColor = new Color32(255, 182, 167, 255);
            spawnRiot.color = new Color32(206, 128, 118, 255);
            reject.eventClick += delegate { DecideComplaint(false); };
            // Facility opposition only: conditional agreement (parks, trees or health care instead of money).
            agree = Button(complaintsBody, L.T("조건부 합의"), 310, 292, 150, 32);
            agree.color = new Color32(160, 225, 180, 255);
            agree.isVisible = false;
            agree.eventClick += delegate { DecideComplaint(false, true); };
            locate = Button(unrestBody, L.T("현장 보기"), 0, 234, 200, 30);
            locate.eventClick += delegate {
                UnrestView unrest = CivicUnrest.View;
                CameraController camera = ToolsModifierControl.cameraController;
                if (!unrest.Active || camera == null) return;
                followingPolice = false;
                camera.ClearTarget(); camera.m_targetPosition = unrest.Position; camera.m_targetSize = 120;
                showUnrest = true;
            };
            locatePolice = Button(unrestBody, L.T("경찰차 보기"), 210, 234, 200, 30);
            locatePolice.tooltip = L.T("출동 차량 중 대표 차량을 따라봅니다. 다시 누르면 추적을 멈춥니다.");
            locatePolice.eventClick += delegate {
                UnrestView unrest = CivicUnrest.View;
                CameraController camera = ToolsModifierControl.cameraController;
                if (unrest.PoliceVehicle == 0 || camera == null) return;
                followingPolice = !followingPolice;
                if (followingPolice) { camera.ClearTarget(); camera.m_targetPosition = unrest.PolicePosition; camera.m_targetSize = 60; }
            };
            police = Button(unrestBody, L.T("경찰 출동 요청"), 420, 234, 200, 30);
            police.eventClick += delegate { showUnrest = true; CivicUnrest.RequestPolice(); };
            status = Label("", 20, 668, 620, 20, 0.75f);
            status.wordWrap = false;
            status.textColor = Muted;
            floatingButton = (FloatingButton)ui.AddUIComponent(typeof(FloatingButton));
            floatingButton.Initialize(ui);
            toggle = (UIButton)floatingButton.AddUIComponent(typeof(LocalizedButton));
            ConfigureButton(toggle, L.T("행복도 관리"), 0, 0, 150, 32);
            toggle.eventClick += delegate { TogglePopup(); };
            // Add last so the menu appears above the administration content.
            toolsMenu = AddUIComponent<UIPanel>();
            toolsMenu.relativePosition = new Vector3(430, 44); toolsMenu.size = new Vector2(170, 152);
            toolsMenu.backgroundSprite = "MenuPanel2";
            toolsMenu.color = new Color32(184, 202, 217, 255);
            UIButton reviewTool = Button(toolsMenu, L.T("평가"), 10, 6, 150, 30);
            reviewTool.eventClick += delegate { toolsMenu.Hide(); ReviewWindow.Toggle(); };
            visitorToggle = Button(toolsMenu, L.T("방문자"), 10, 42, 150, 30);
            visitorToggle.tooltip = L.T("이 모드가 보낸 행정청 방문자 표시 · 표식을 누르면 따라갑니다");
            visitorToggle.eventClick += delegate { visitorsVisible = !visitorsVisible; RefreshToolButtons(); };
            mapToggle = Button(toolsMenu, L.T("지도"), 10, 78, 150, 30);
            mapToggle.tooltip = L.T("주요 피해 도로 최대 12곳과 담당 행정청 표시 · 표식 클릭으로 민원 열기");
            mapToggle.eventClick += delegate {
                mapVisible = !mapVisible;
                RefreshToolButtons();
                Debug.Log("[CivicPressure] Complaint map: enabled=" + mapVisible + ", language=" + L.Current + ", markers=" + CivicSimulation.View.Markers.Length);
            };
            UIButton recordsTool = Button(toolsMenu, L.T("기록"), 10, 114, 150, 30);
            recordsTool.eventClick += delegate { toolsMenu.Hide(); OpenReports(); };
            // The menu uses one neutral background; toggle state is written in each caption.
            foreach (UIButton tool in new UIButton[] { reviewTool, visitorToggle, mapToggle, recordsTool }) {
                tool.hoveredBgSprite = "ButtonMenu";
                tool.focusedBgSprite = "ButtonMenu";
                tool.pressedBgSprite = "ButtonMenu";
            }
            RefreshToolButtons();
            toolsMenu.Hide();
            // Unified UI replaces the entire floating control, including its drag grip.
            if (UnifiedUIButton.Register(L.T("도시 행정"), delegate(bool on) { if (on != isVisible) TogglePopup(); }))
            {
                toggle.isVisible = false;
                floatingButton.Hide();
                eventVisibilityChanged += delegate(UIComponent component, bool visible) { UnifiedUIButton.SetPressed(visible); };
            }
            initialized = true;
            Render(CivicSimulation.View);
            Show();
        }

        private static UILabel Cell(UIComponent parent, string text, float x, float y, float w, float h, float scale, bool right)
        {
            UILabel label = parent.AddUIComponent<UILabel>();
            label.font = UIView.GetAView().defaultFont;
            label.autoSize = false; label.wordWrap = false;
            label.relativePosition = new Vector3(x, y); label.size = new Vector2(w, h);
            label.textScale = scale; label.text = text;
            LocalizedUI.Fit(label, scale);
            label.textColor = Ink;
            label.textAlignment = right ? UIHorizontalAlignment.Right : UIHorizontalAlignment.Left;
            return label;
        }

        private void RenderTable(RegionView region, bool wholeCity)
        {
            regionTitle.text = region == null ? L.T("시민을 집계하고 있습니다…") : region.Name + " · " + region.Residents.ToString("N0") + L.T("명");
            regionTitle.tooltip = regionTitle.text;
            regionTitle.textScale = regionTitle.text.Length > 23 ? .82f : 1f;
            regionSubtitle.text = region == null ? L.T("완료된 결과를 표시합니다.") : (wholeCity ? L.T("전체 시민 기준: ") : L.T("지역 행복도: ")) + Score(region.Score);
            if(region!=null && !wholeCity && region.OfficeId!=0) regionSubtitle.text += L.T(" · 구청 신뢰 ") + region.Trust + "/100";
            if(region!=null && wholeCity && region.Unlocated>0) regionSubtitle.text += L.T(" · 거주지 미확인 ") + region.Unlocated.ToString("N0") + L.T("명");
            regionSubtitle.tooltip = region == null ? regionSubtitle.text : regionSubtitle.text + L.T("\n구청: ") + region.Office +
                (region.OfficeTransferred ? L.T("\n이관 담당: ") + region.ServiceOffice + " · " + region.TransferStage : "") +
                L.T("\n주거지 기준 집계 · 직장과 현재 방문지 피해 포함\n도시 점수는 지구별 산술평균이므로 전체 시민 점수와 다를 수 있습니다.");
            for (int i = 0; i < categoryTabs.Length; i++) TabStyle(categoryTabs[i], needsPageIndex == i);
            buildingSummary.text = region == null ? "" : L.T("경고 ") + region.WarningBuildings.ToString("N0") + L.T("   ·   심각 ") + region.SevereBuildings.ToString("N0") + L.T("   ·   버려짐 ") + region.Abandoned.ToString("N0");
            double categoryPenalty = 0; int activeReasons = 0;
            for (int row = 0; row < 8; row++)
            {
                for (int col = 1; col < 5; col++)
                {
                    cells[row, col].text = "—";
                    cells[row, col].textColor = new Color32(175, 185, 192, 255);
                }
                int[] reasons = categoryReasons[needsPageIndex];
                int reason = row < reasons.Length ? reasons[row] : -1;
                bool visibleRow = row < reasons.Length;
                tableBands[row].isVisible = visibleRow;
                cells[row, 0].textColor = Ink;
                rowMarks[row].color = Muted;
                for (int col = 0; col < 5; col++) cells[row, col].isVisible = visibleRow && reason < Happiness.Weights.Length;
                if (!visibleRow || reason >= Happiness.Weights.Length) continue;
                cells[row, 0].text = reason == 3 ? L.T("전기·수도 등 기반시설") : Happiness.Reasons[reason];
                if (region == null) continue;
                int affected = region.Affected[reason], severe = region.Severe[reason];
                double weighted = Happiness.Weighted(affected, severe, region.Residents);
                categoryPenalty += Happiness.Penalty(reason, affected, severe, region.Residents);
                if (affected > 0) activeReasons++;
                rowMarks[row].color = severe > 0 ? new Color32(255, 155, 130, 255) : affected > 0 ? new Color32(255, 215, 155, 255) : new Color32(140, 198, 178, 255);
                cells[row, 0].textColor = affected > 0 ? Ink : Muted;
                cells[row, 1].text = Math.Max(0, affected - severe).ToString("N0");
                cells[row, 2].text = severe.ToString("N0");
                cells[row, 3].text = region.Residents <= 0 ? "—" : (100.0 * affected / region.Residents).ToString("0.0") + "%";
                cells[row, 4].text = "−" + Happiness.Penalty(reason, affected, severe, region.Residents).ToString("0.0");
                if (affected > 0) { cells[row, 1].textColor = new Color32(245, 245, 245, 255); cells[row, 4].textColor = new Color32(255, 207, 147, 255); }
                if (severe > 0) cells[row, 2].textColor = new Color32(255, 155, 130, 255);
                for (int col = 0; col < 5; col++) cells[row, col].tooltip = Happiness.Reasons[reason] + L.T(": 미충족/피해 ") + affected.ToString("N0") + L.T("명 중 심각 ") + severe.ToString("N0") + L.T("명");
                if (reason == 17) for (int col = 0; col < 5; col++) cells[row, col].tooltip += L.T("\n실제 대중교통 대기 시민 · 대기 카운터 64 이상 경고, 192 이상 또는 대기 포기 직전 심각 · 분 단위 시간 아님");
                if (reason == 2) for (int col = 0; col < 5; col++) cells[row, col].tooltip += L.T("\n연결도로 교통 밀도 80 이상 경고 · 95 이상 심각 · 실제 통행속도 지표는 아님");
                if (reason == 4 || reason == 5 || reason == 6 || reason == 14)
                    for (int col = 0; col < 5; col++) cells[row, col].tooltip += L.T("\n건물 상태 판정: 불량 방향 60%부터 경고, 80%부터 심각입니다. 실제 화재는 항상 심각입니다.");
                if (reason >= 15 && reason <= 16) for (int col = 0; col < 5; col++) cells[row, col].tooltip += reason == 15 ? L.T("\n완성된 운영 노선의 정류장·역 반경 800m (직선거리) · 낮/밤 전용 노선 인정") : L.T("\n운영 중인 공원 시설 반경 600m (직선거리) · 장식용 나무·빈 공원 구역 제외");
                if (reason >= 8 && reason < 13)
                {
                    string[] requirements = { L.T("거주 가능한 완공 주택 필요 · 주거 상실은 심각"), L.T("학생을 제외한 청년·성인의 게임 실업 상태 기준 · 상업·공업·사무 수요와는 별개"),
                        L.T("해당 교육 단계 이수율 40% 이하와 학교 배정 부족을 함께 확인 · 20% 이하는 심각"), L.T("의료 서비스 범위 부족·불량 건물 건강·시민 건강 문제를 함께 확인"),
                        L.T("치안 서비스 범위 부족과 건물 범죄 지표 60% 이상을 함께 확인 · 80% 이상은 심각") };
                    for (int col = 0; col < 5; col++) cells[row, col].tooltip += "\n" + requirements[reason - 8];
                }
                cells[row, 4].tooltip += L.T("\n환산 ") + weighted.ToString("N1") + ((ComplaintRules.Slow(reason) || reason >= 15) ? L.T("명분 · 완화 항목: 기본 2점 + 완화된 비율 감점") : L.T("명분 · 기본 12점 + 비율 감점")) + L.T("\n심각 인원은 1.5배 반영 · 종결 기준 이하 소수 피해는 비율에 따라 감점 축소");
            }
            categorySummary.text = region == null ? L.T("집계가 완료되면 분야별 요약이 표시됩니다.") : activeReasons == 0 ? L.T("이 분야에서 확인된 불만이 없습니다") : L.T("불만 ") + activeReasons + L.T("개 항목    ·    분야 감점  −") + categoryPenalty.ToString("0.0") + L.T("점");
            categorySummary.textColor = region == null ? Muted : activeReasons == 0 ? new Color32(160, 225, 180, 255) : new Color32(255, 215, 155, 255);
            categorySummary.tooltip = L.T("현재 조회 지역의 선택 분야 감점 합계입니다. 도시 상단 행복도는 지구별 평균일 수 있습니다. 버려진 건물 감점은 별도입니다.");
            abandonedSummary.text = region == null ? "" : L.T("버려진 건물 추가 감점  −") + Happiness.AbandonmentPenalty(region.Abandoned, region.Buildings).ToString("0.0") + L.T("점");
            abandonedSummary.tooltip = L.T("전체 도시 행의 감점은 전체 시민 기준입니다. 상단 도시 점수는 지구 점수의 산술평균이라 합계가 다를 수 있습니다.");
        }

        private UILabel Label(string text, float x, float y, float w, float h, float scale)
        {
            UILabel label = AddUIComponent<UILabel>();
            label.font = UIView.GetAView().defaultFont;
            label.autoSize = false;
            label.wordWrap = true;
            label.relativePosition = new Vector3(x, y);
            label.size = new Vector2(w, h);
            label.textScale = scale;
            label.text = text;
            LocalizedUI.Fit(label, scale);
            return label;
        }

        private static UIButton Button(UIComponent parent, string text, float x, float y, float w, float h)
        {
            UIButton button = parent.AddUIComponent<LocalizedButton>();
            ConfigureButton(button, text, x, y, w, h);
            return button;
        }

        private static void ConfigureButton(UIButton button, string text, float x, float y, float w, float h)
        {
            button.font = UIView.GetAView().defaultFont;
            button.relativePosition = new Vector3(x, y);
            button.size = new Vector2(w, h);
            button.textScale = 0.9f;
            button.color = new Color32(175, 199, 215, 255);
            button.textColor = Ink;
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.disabledBgSprite = "ButtonMenuDisabled";
            button.text = text;
            LocalizedUI.Fit(button);
        }

        private void Send(int action)
        {
            CityView view = CivicSimulation.View;
            if (view.Selected != observed) return;
            CivicSimulation.Enqueue(view, action);
        }

        private void Cycle(int direction)
        {
            CityView view = CivicSimulation.View;
            int index = browse < 0 ? 0 : browse;
            for (int count = 0; count < view.Regions.Length; count++)
            {
                index = (index + direction + view.Regions.Length) % view.Regions.Length;
                if (view.Regions[index] != null) { browse = index; break; }
            }
            Render(view);
        }

        public override void Update()
        {
            base.Update();
            if (!initialized) return;
            UpdateMap();
            UpdateVisitors();
            if (followingPolice)
            {
                UnrestView tracking = CivicUnrest.View;
                CameraController camera = ToolsModifierControl.cameraController;
                if (!isVisible || !showUnrest || tracking.PoliceVehicle == 0 || camera == null) followingPolice = false;
                else camera.m_targetPosition = tracking.PolicePosition;
            }
            bool pressed = ShortcutSettings.Pressed();
            if (Time.realtimeSinceStartup < nextPoll)
            {
                if (pressed) TogglePopup();
                return;
            }
            nextPoll = Time.realtimeSinceStartup + 0.25f;
            RefreshToolButtons();
            if (demandWindow != null && demandWindow.isVisible) RenderDemands();
            if (reportWindow != null && reportWindow.isVisible) RenderReport();
            if (trendWindow != null && trendWindow.isVisible) RenderTrend();
            ushort id = WorldInfoPanel.AnyWorldInfoPanelOpen() ? WorldInfoPanel.GetCurrentInstanceID().Building : (ushort)0;
            if (id != observed)
            {
                observed = id;
                if (openedFor != id) openedFor = 0;
                browse = -1;
                CivicSimulation.Select(id);
            }
            CityView view = CivicSimulation.View;
            if (id != 0 && view.Selected == id && view.Registered && openedFor != id)
            {
                openedFor = id;
                Show();
            }
            if (pressed) TogglePopup();
            UnrestView unrest = CivicUnrest.View;
            toggle.text = unrest.Active ? unrest.Riot ? L.T("폭동 발생") : L.T("시위 진행 중") : view.Complaints.Length > 0 ? L.T("미해결 민원 ") + view.Complaints.Length : L.T("도시 행정");
            if (!unrest.Active && CivicStanding.UnseenGrade.Length > 0) toggle.text = L.T("새 행정 평가 ") + CivicStanding.UnseenGrade;
            UnifiedUIButton.SetTooltip(toggle.text);
            if (unrest.Active && !observedUnrest) { showUnrest = true; showComplaints = false; }
            observedUnrest = unrest.Active;
            if (isVisible) Render(view);
        }

        private void ClosePopup()
        {
            // Keep a pending selection response from immediately reopening a manually closed popup.
            openedFor = WorldInfoPanel.AnyWorldInfoPanelOpen() ? WorldInfoPanel.GetCurrentInstanceID().Building : observed;
            toolsMenu.Hide();
            Hide();
        }

        private void TogglePopup()
        {
            if (isVisible) ClosePopup();
            else { Render(CivicSimulation.View); Show(); }
        }

        private void RefreshToolButtons()
        {
            string visitorCaption = L.T("방문자") + " · " + (visitorsVisible ? L.T("켜짐") : L.T("꺼짐"));
            string mapCaption = L.T("지도") + " · " + (mapVisible ? L.T("켜짐") : L.T("꺼짐"));
            if (visitorToggle.text != visitorCaption) visitorToggle.text = visitorCaption;
            if (mapToggle.text != mapCaption) mapToggle.text = mapCaption;
        }

        private static string Score(double value)
        {
            return double.IsNaN(value) ? L.T("집계 대상 없음") :
                (Math.Floor(value * 10) / 10).ToString("0.0") + " / 100 · " + Happiness.Level(value);
        }

        private void Render(CityView view)
        {
            bool currentSelection = view.Selected != 0 && view.Selected == observed;
            string regionName = view.Regions[view.SelectedDistrict] == null ? L.T("지역 집계 중") : view.Regions[view.SelectedDistrict].Name;
            hallHeading.text = currentSelection && view.Registered ? view.HallTitle : currentSelection ? L.T("선택한 건물 · 행정청 미지정") : L.T("도시 행정 · ") + view.CityName;
            hallHeading.tooltip = hallHeading.text;
            hallHeading.textScale = hallHeading.text.Length > 28 ? 1.05f : 1.35f;
            hallHeading.textColor = currentSelection && view.Registered ? new Color32(172, 220, 245, 255) : new Color32(240, 240, 240, 255);
            selection.text = currentSelection ? L.T("건물: ") + view.SelectedName + "  ·  " + regionName : L.T("시청 ") + (view.HasCityHall ? L.T("지정됨") : L.T("미지정")) + L.T(" · 구청 ") + view.AssignedOffices + "/" + view.RequiredOffices;
            selection.tooltip = selection.text;
            if (!view.AdministrationComplete) editingAdministration = false;
            bool editorOpen = view.AdministrationComplete && editingAdministration;
            editAdministration.isVisible = view.AdministrationComplete;
            editAdministration.text = editorOpen ? L.T("변경 닫기") : L.T("행정청 변경");
            administrationSummary.isVisible = view.AdministrationComplete;
            administrationSummary.text = L.T("지정 완료  ·  시청 1곳  /  구청 ") + view.AssignedOffices + L.T("곳");
            administrationSummary.tooltip = L.T("일반 지구 ") + view.RequiredOffices + L.T("곳의 구청 지정 완료. 새 지구를 만들거나 지정을 해제하면 초기 지정 버튼이 다시 표시됩니다.");
            administrationEditor.isVisible = editorOpen;
            assignCity.isVisible = assignDistrict.isVisible = clear.isVisible = !view.AdministrationComplete || editorOpen;
            assignCity.relativePosition = new Vector3(20, editorOpen ? 380 : 92);
            assignDistrict.relativePosition = new Vector3(230, editorOpen ? 380 : 92);
            clear.relativePosition = new Vector3(440, editorOpen ? 380 : 92);
            assignCity.isEnabled = currentSelection && view.CanAssign;
            assignDistrict.isEnabled = currentSelection && view.CanAssign && view.HasCityHall &&
                view.SelectedDistrict != 0 && view.Selected != view.CityHallId;
            clear.isEnabled = currentSelection && view.CanAssign && view.Registered;
            scoreLabel.text = L.T("도시 행복도   ") + Score(view.CityScore);
            scoreLabel.textColor = double.IsNaN(view.CityScore) ? new Color32(190, 200, 210, 255) : view.CityScore < 40 ? new Color32(255, 155, 130, 255) : view.CityScore < 80 ? new Color32(255, 215, 155, 255) : new Color32(160, 225, 180, 255);
            city.text = L.T("시청: ") + view.CityHall + "  ·  " + (view.AveragedDistricts > 0 ? L.T("지구 ") + view.AveragedDistricts + L.T("곳 평균") : L.T("전체 시민 기준"));
            city.tooltip = city.text;
            RegionView region = null;
            bool wholeCity = browse == -2 || (browse == -1 && (view.Selected == 0 || view.Selected == view.CityHallId));
            if (wholeCity) region = view.All;
            else
            {
                int index = browse >= 0 ? browse : view.SelectedDistrict;
                region = view.Regions[index];
                if (browse >= 0 && region == null && view.All != null)
                {
                    browse = -2;
                    wholeCity = true;
                    region = view.All;
                }
            }
            RenderTable(region, wholeCity);
            if (!wholeCity && region != null) scoreLabel.text = region.Name + L.T(" 행복도   ") + Score(region.Score);
            double displayedScore = wholeCity || region == null ? view.CityScore : region.Score;
            scoreLabel.textColor = double.IsNaN(displayedScore) ? new Color32(190, 200, 210, 255) : displayedScore < 40 ? new Color32(255, 155, 130, 255) : displayedScore < 80 ? new Color32(255, 215, 155, 255) : new Color32(160, 225, 180, 255);
            scoreLabel.textScale = scoreLabel.text.Length > 35 ? 1.05f : 1.3f;
            scoreLabel.tooltip = scoreLabel.text;
            scoreFill.isVisible = !double.IsNaN(displayedScore) && displayedScore > 0;
            scoreFill.width = (float)(616 * Math.Max(0, Math.Min(100, double.IsNaN(displayedScore) ? 0 : displayedScore)) / 100);
            scoreFill.color = scoreLabel.textColor;
            eventOrigin = wholeCity ? -1 : browse >= 0 ? browse : view.SelectedDistrict;
            trendDelta.text = TrendSummary();
            trendDelta.tooltip = trendDelta.text;
            spawnProtest.isVisible = showUnrest && !editorOpen; spawnRiot.isVisible = showUnrest && !editorOpen;
            happinessBody.isVisible = !showUnrest && !showComplaints && !editorOpen;
            unrestBody.isVisible = showUnrest && !editorOpen;
            complaintsBody.isVisible = showComplaints && !editorOpen;
            legend.isVisible = !editorOpen;
            TabStyle(happinessTab, !showUnrest && !showComplaints);
            TabStyle(unrestTab, showUnrest);
            TabStyle(complaintsTab, showComplaints);
            complaintsTab.text = L.T("민원 (") + view.Complaints.Length + ")";
            legend.text = L.T("난이도 ") + Difficulty.Level + L.T(" / 5  ·  행복도 감점 ") + Difficulty.PenaltyScale.ToString("0.##") + L.T("배");
            legend.tooltip = showUnrest ? L.T("수동 발생은 행복도와 자동 발생 빈도를 무시합니다. 폭동은 실제 건물과 도로에 피해를 줍니다.") : L.T("경고와 심각 인원은 서로 겹치지 않습니다. 피해 비율은 두 인원의 합계 / 지역 인구입니다. 감점은 제곱근 곡선으로 계산하며 최종 점수는 0~100입니다.");
            status.text = (view.Scanning ? L.T("집계 ") + view.ScanProgress + "% · " : L.T("갱신 ") + view.CensusUpdatedUtc.ToLocalTime().ToString("HH:mm:ss") + " · ") + view.Status;
            if(mapVisible) status.text=mapStatus;
            status.tooltip = status.text;
            UnrestView unrest = CivicUnrest.View;
            RenderComplaints(view, wholeCity, eventOrigin);
            spawnProtest.isEnabled = !unrest.Active && view.HasCityHall && region != null && region.Residents > 0;
            spawnRiot.isEnabled = !unrest.Riot && (unrest.Active || (view.HasCityHall && region != null && region.Residents > 0));
            locate.isEnabled = unrest.Active;
            demandButton.isEnabled = unrest.Active;
            meetingYes.isVisible=meetingNo.isVisible=unrest.MeetingPending; shownMeetingStamp=unrest.MeetingStamp;
            police.isEnabled = unrest.Active && unrest.Riot && !unrest.Police;
            locatePolice.isEnabled = unrest.Active && unrest.PoliceVehicle != 0;
            locatePolice.text = followingPolice ? L.T("경찰차 추적 중지") : L.T("경찰차 보기");
            if (showUnrest)
            {
                unrestSection.text = unrest.Announced ? L.T("집회 예정 안내") : unrest.Riot ? L.T("진압 · 피해 현황") : unrest.Active ? L.T("해산 조건") : L.T("현재 상태");
                unrestStripe.color = unrest.Riot ? new Color32(255, 155, 130, 255) : unrest.Active || unrest.Announced ? new Color32(255, 215, 155, 255) : Accent;
                unrestTitle.text = unrest.Active ? unrest.Stage : L.T("진행 중인 시위 없음");
                unrestTitle.textColor = unrest.Riot ? new Color32(255, 155, 130, 255) : new Color32(255, 215, 155, 255);
                unrestOrigin.text = unrest.Active ? L.T("발생 지역: ") + unrest.Origin + "  ·  " + (unrest.Manual ? L.T("수동 발생") : L.T("자연 발생")) : L.T("대기 중");
                unrestOrigin.tooltip = unrestOrigin.text;
                unrestTime.text = !unrest.Active ? L.T("시간 / 문제 해결    사건 발생 후 표시") :
                    (unrest.Elapsed || unrest.Resolved ? L.T("[충족] ") : L.T("[대기] ")) + L.T("시간 / 문제 해결    ") +
                    (unrest.Elapsed ? L.T("48시간 경과") : unrest.Resolved ? L.T("지역 문제 해결") : unrest.RemainingHours.ToString("0.0") + L.T(" 게임 시간 남음"));
                unrestTime.tooltip = unrest.Manual ? L.T("수동 사건은 48 게임 시간 경과가 필요합니다.") : L.T("48 게임 시간 경과 또는 발생 지역의 모든 문제 해결 중 하나를 충족해야 합니다.");
                if (!unrest.Riot && unrest.LocalStage) unrestTime.text = L.T("구청 대응 중 · 시청 이관 대기");
                if (unrest.Riot)
                {
                    unrestTime.text = unrest.Police ? L.T("현장 진압 중 · 남은 저항 인원과 경찰 상태로 승패 결정") : L.T("경찰 도착 대기 · 현장 전투로 진압 판정");
                    unrestTime.tooltip = L.T("경찰은 저지선을 형성하며 진압합니다. 남은 폭동대가 모두 저항을 멈추면 경찰 승리, 대응 가능한 경찰과 대기 증원이 없으면 경찰이 철수합니다. 활동 중인 폭동대의 피해는 계속될 수 있습니다.");
                }
                unrestRepair.text = !unrest.Riot ? L.T("파괴 피해 없음") : L.T("미복구 건물 ") + unrest.Repairs + L.T("곳");
                unrestPolice.text = !unrest.Riot ? L.T("경찰 출동 대상 아님") : unrest.Police ? L.T("[도착] 경찰차 현장 대치 중") : L.T("[대기] 경찰 출동 요청");
                if (unrest.PoliceVehicle != 0) unrestPolice.text += " · " + unrest.PoliceCount + L.T("대 출동 · 차량 #") + unrest.PoliceVehicle;
                unrestMessage.text = unrest.Active ? unrest.DispatchStatus : unrest.Text;
                unrestMessage.tooltip = unrest.Text;
                manualTarget.text = L.T("수동 발생 지역: ") + (region == null ? L.T("집계 중") : region.Name);
                manualTarget.tooltip = manualTarget.text;
                spawnRiot.text = unrest.Active && !unrest.Riot ? L.T("폭동으로 전환") : L.T("폭동 발생");
                if (unrest.Announced) {
                    string[] lines = unrest.Announcement.Split('\n');
                    unrestTitle.text = L.T("집회 사전 예고");
                    unrestOrigin.text = lines[0]; unrestTime.text = lines.Length > 2 ? lines[2] : "";
                    unrestRepair.text = lines.Length > 1 ? lines[1] : "";
                    unrestPolice.text = L.T("원인 개선·행복도 회복 시 예고 취소");
                    unrestMessage.text = L.T("예고 중에는 군중 생성과 도로 통제가 없습니다.");
                }
            }
        }

        private static string GameTime(double hours)
        {
            if (hours <= 0) return L.T("다음 집계에서 확인");
            int minutes = (int)Math.Ceiling(hours * 60);
            return (minutes >= 60 ? (minutes / 60) + L.T("시간 ") : "") + (minutes % 60) + L.T("분 · 게임 시간");
        }

        private void RenderComplaints(CityView view, bool all, int origin)
        {
            System.Collections.Generic.List<ComplaintView> list = new System.Collections.Generic.List<ComplaintView>();
            foreach (ComplaintView c in view.Complaints) if (all || c.Region == origin) list.Add(c);
            if (list.Count == 0)
            {
                currentComplaint = null; complaintCount.text = L.T("접수된 민원 없음");
                complaintTitle.text = L.T("현재 접수된 민원이 없습니다.");
                complaintTitle.textScale = 1.05f;
                complaintStripe.color = new Color32(160, 225, 180, 255);
                complaintStage.text = complaintDetail.text = complaintRoad.text = complaintTimer.text = "";
                promise.isEnabled = reject.isEnabled = complaintLocation.isEnabled = false; LayoutDecisions(false); return;
            }
            complaintIndex = (complaintIndex % list.Count + list.Count) % list.Count;
            currentComplaint = list[complaintIndex];
            complaintCount.text = L.T("미해결 민원 ") + list.Count + L.T("건 · ") + (complaintIndex + 1) + "/" + list.Count;
            complaintTitle.text = currentComplaint.Title; complaintTitle.tooltip = currentComplaint.Title;
            complaintStage.text = currentComplaint.Stage;
            complaintStage.tooltip = complaintStage.text;
            complaintStage.textColor = currentComplaint.Improving ? new Color32(160, 225, 180, 255) : currentComplaint.Hours <= 0 ? new Color32(255, 150, 130, 255) : new Color32(255, 220, 150, 255);
            complaintTimer.text = (currentComplaint.Improving ? L.T("개선 확인 완료까지  ") : currentComplaint.Odd ? L.T("답변 기한까지  ") : L.T("대응 기한까지  ")) + GameTime(currentComplaint.Hours);
            if (!currentComplaint.Improving && currentComplaint.Hours <= 0) complaintTimer.text = L.T("대응 기한이 지났습니다");
            complaintTimer.textColor = complaintStage.textColor;
            complaintStripe.color = complaintStage.textColor;
            if (currentComplaint.Visitor != 0)
            {
                complaintTimer.text = L.T("방문 접수 대기 최대  ") + GameTime(currentComplaint.Hours);
                complaintStage.textColor = complaintTimer.textColor = complaintStripe.color = new Color32(150, 205, 255, 255);
            }
            // Facility opposition and residents' suggestions (CivicStanding).
            if (currentComplaint.Kind == ComplaintKind.Opposition)
            {
                complaintTimer.text = (currentComplaint.Improving ? L.T("합의 이행 기한까지  ") : currentComplaint.CanReject ? L.T("주민 반대 응답 기한까지  ") : L.T("집회 예고까지  ")) + GameTime(currentComplaint.Hours);
                complaintStage.textColor = complaintTimer.textColor = complaintStripe.color = new Color32(255, 185, 120, 255);
            }
            else if (currentComplaint.Kind == ComplaintKind.Suggestion)
            {
                complaintTimer.text = (currentComplaint.Improving ? L.T("건의 이행 기한까지  ") : L.T("건의 답변 기한까지  ")) + GameTime(currentComplaint.Hours);
                complaintStage.textColor = complaintTimer.textColor = complaintStripe.color = new Color32(160, 225, 180, 255);
            }
            complaintDetail.text = currentComplaint.Detail;
            complaintLocation.text=string.IsNullOrEmpty(currentComplaint.LocationLabel) ? L.T("문제 장소 보기") : currentComplaint.LocationLabel;
            complaintTitle.textScale = currentComplaint.Title.Length > 34 ? .88f : 1.05f;
            complaintDetail.tooltip = complaintDetail.text;
            complaintRoad.text = currentComplaint.Road; complaintRoad.tooltip = currentComplaint.Road;
            promise.isEnabled = currentComplaint.CanPromise;
            reject.isEnabled = currentComplaint.CanReject;
            promise.text = currentComplaint.Odd ? L.T("수락 · 검토 접수") : L.T("수락 · 개선 약속");
            reject.tooltip = currentComplaint.Odd ? L.T("불이익 없이 기타 요구를 종결합니다.") : L.T("민원을 종결해도 생활 문제의 행복도 감점은 남습니다.");
            reject.text = L.T("거절");
            if (currentComplaint.Kind == ComplaintKind.Opposition)
            {
                promise.text = L.T("설명회·보상"); promise.tooltip = L.T("표시된 비용을 지출해 주민 반대를 해소합니다. 예고된 집회도 취소됩니다.");
                reject.text = L.T("강행"); reject.tooltip = L.T("보상 없이 시설 운영을 계속합니다. 난이도에 따라 신뢰 하락과 행복도 감점, 집회 또는 폭동 예고가 따릅니다.");
            }
            else if (currentComplaint.Kind == ComplaintKind.Suggestion)
            {
                promise.text = L.T("수락 · 추진 약속"); promise.tooltip = L.T("기한 안에 서비스를 개선하면 구청 신뢰가 오릅니다.");
                reject.text = L.T("보류"); reject.tooltip = L.T("불이익 없이 건의를 닫습니다.");
            }
            else promise.tooltip = "";
            LayoutDecisions(currentComplaint.Kind == ComplaintKind.Opposition);
            agree.isEnabled = currentComplaint.CanAgree;
            agree.text = L.T("조건부 합의");
            agree.tooltip = L.T("비용 없이 주민이 요구한 조건(공원·나무·병원)을 72시간 안에 지키기로 합니다. 지키면 구청 신뢰가 오르고, 못 지키면 강행과 같은 결과가 됩니다.");
            complaintLocation.isEnabled = currentComplaint.HasRoad;
        }

        // Three decision buttons for facility opposition, two for everything else.
        private void LayoutDecisions(bool opposition)
        {
            if (agree.isVisible == opposition) return;
            agree.isVisible = opposition;
            float width = opposition ? 150 : 200;
            complaintLocation.width = promise.width = reject.width = width;
            promise.relativePosition = new Vector3(opposition ? 155 : 210, 292);
            reject.relativePosition = new Vector3(opposition ? 465 : 420, 292);
        }

        // In-person complaint still on its way: follow the resident walking or riding to the office.
        private void FollowVisitor(CameraController camera, uint citizen) { FollowVisitor(camera, citizen, 0); }

        // Follows the resident on foot, the vehicle they are riding, or the office they are inside.
        private void FollowVisitor(CameraController camera, uint citizen, ushort office)
        {
            Vector3 spot; InstanceID id;
            if (!VisitorSpot(citizen, office, out spot, out id)) { status.text = L.T("방문자가 이미 도착했거나 이동을 멈췄습니다."); return; }
            followingPolice = false;
            camera.SetTarget(id, spot, true);
        }

        // Where a visitor is right now: walking (citizen instance), riding (their vehicle, including transit), or
        // inside the office (the building). Riding used to hide the pin and drop the camera (user report 2026-09-18).
        private static bool VisitorSpot(uint citizen, ushort office, out Vector3 spot, out InstanceID id)
        {
            spot = Vector3.zero; id = default(InstanceID);
            CitizenManager citizens = ColossalFramework.Singleton<CitizenManager>.instance;
            if (citizen != 0 && citizen < citizens.m_citizens.m_buffer.Length)
            {
                Citizen person = citizens.m_citizens.m_buffer[citizen];
                // The vehicle comes first: boarding does not release the walking instance at once, so it would sit frozen
                // at the kerb while the car drives away (user report 2026-09-18).
                ushort vehicle = person.m_vehicle;
                VehicleManager vehicles = ColossalFramework.Singleton<VehicleManager>.instance;
                if (vehicle != 0 && vehicle < vehicles.m_vehicles.m_buffer.Length && (vehicles.m_vehicles.m_buffer[vehicle].m_flags & Vehicle.Flags.Created) != 0)
                {
                    spot = vehicles.m_vehicles.m_buffer[vehicle].GetLastFramePosition();
                    id.Vehicle = vehicle;
                    return true;
                }
                ushort instance = person.m_instance;
                if (instance != 0 && instance < citizens.m_instances.m_buffer.Length)
                {
                    spot = citizens.m_instances.m_buffer[instance].GetLastFramePosition();
                    id.CitizenInstance = instance;
                    return true;
                }
            }
            if (office == 0) return false;
            Building[] buildings = ColossalFramework.Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (office >= buildings.Length || !CivicSimulation.Live(buildings[office])) return false;
            spot = buildings[office].m_position;
            id.Building = office;
            return true;
        }

        private void DecideComplaint(bool accept) { DecideComplaint(accept, false); }

        private void DecideComplaint(bool accept, bool agreement)
        {
            if (currentComplaint == null) return;
            CivicSimulation.Decide(currentComplaint.Key, currentComplaint.Stamp, accept, agreement, CivicSimulation.View.Generation);
            promise.isEnabled = reject.isEnabled = agree.isEnabled = false;
        }

        private void UpdateMap()
        {
            if(!mapVisible) { foreach(UIButton pin in mapPins) if(pin!=null && pin.isVisible) pin.Hide(); return; }
            Camera camera=Camera.main; UIView ui=UIView.GetAView();
            ComplaintMarker[] markers=CivicSimulation.View.Markers;
            int occupied=0;
            for(int i=0;i<mapPins.Length;i++) {
                bool visible=mapVisible && camera!=null && i<markers.Length;
                if(!visible) {if(mapPins[i]!=null)mapPins[i].Hide();continue;}
                ComplaintMarker target=markers[i]; Vector3 point=camera.WorldToScreenPoint(target.Position+Vector3.up*8);
                if(point.z<=0) {if(mapPins[i]!=null)mapPins[i].Hide();continue;}
                float x=point.x/Screen.width*ui.fixedWidth-95, y=(1-point.y/Screen.height)*ui.fixedHeight-44;
                Rect box=new Rect(x,y,190,44);
                for(int attempt=0;attempt<5;attempt++) {
                    bool overlap=false;for(int j=0;j<occupied;j++) if(box.Overlaps(occupiedPins[j])) {overlap=true;break;}
                    if(!overlap) break;box.y-=46;
                }
                bool blocked=box.x<0 || box.y<0 || box.xMax>ui.fixedWidth || box.yMax>ui.fixedHeight;
                if(isVisible && box.Overlaps(new Rect(relativePosition.x,relativePosition.y,width,height)))blocked=true;
                if(trendWindow!=null && trendWindow.isVisible && box.Overlaps(new Rect(trendWindow.relativePosition.x,trendWindow.relativePosition.y,trendWindow.width,trendWindow.height)))blocked=true;
                if(demandWindow!=null && demandWindow.isVisible && box.Overlaps(new Rect(demandWindow.relativePosition.x,demandWindow.relativePosition.y,demandWindow.width,demandWindow.height)))blocked=true;
                if(reportWindow!=null && reportWindow.isVisible && box.Overlaps(new Rect(reportWindow.relativePosition.x,reportWindow.relativePosition.y,reportWindow.width,reportWindow.height)))blocked=true;
                if(helpWindow!=null && helpWindow.isVisible && box.Overlaps(new Rect(helpWindow.relativePosition.x,helpWindow.relativePosition.y,helpWindow.width,helpWindow.height)))blocked=true;
                for(int j=0;j<occupied;j++)if(box.Overlaps(occupiedPins[j]))blocked=true;
                if(blocked){if(mapPins[i]!=null)mapPins[i].Hide();continue;}
                if(mapPins[i]==null) {
                    // Map labels use a fixed two-line layout in every language. Do not run
                    // the single-line LocalizedButton font fitter on world-space markers.
                    int slot=i;mapPins[i]=(UIButton)ui.AddUIComponent(typeof(UIButton));ConfigureButton(mapPins[i],"",0,0,190,44);mapPins[i].autoSize=false;mapPins[i].textScale=.72f;mapPins[i].wordWrap=true;
                    mapPins[i].eventClick+=delegate{SelectMarker(mapTargets[slot]);};
                }
                mapTargets[i]=target;mapPins[i].relativePosition=new Vector3(box.x,box.y);mapPins[i].text=target.Text;mapPins[i].tooltip=target.Text;
                mapPins[i].textColor=target.Office ? new Color32(140,210,255,255) : target.Severe ? new Color32(255,140,120,255) : new Color32(255,215,130,255);
                mapPins[i].Show();occupiedPins[occupied++]=box;
            }
            if(mapVisible) {
                mapStatus=markers.Length==0 ? L.T("민원 지도: 표시할 현장이 없습니다. 집계 완료와 일반 민원을 확인하세요.") :
                    L.T("민원 지도: 표시 ")+occupied+" / "+markers.Length+(occupied==0 ? L.T(" · 시점을 옮기거나 행정 창을 닫아 가려진 현장을 확인하세요.") : "");
                mapToggle.tooltip=mapStatus;
                if(status!=null) {status.text=mapStatus;status.tooltip=mapStatus;}
            }
        }
        // Pins over the residents this mod sent to an office, so they are not mistaken for ordinary citizens
        // (subscriber feedback 2026-09-18). Clicking one follows that resident with the camera.
        private void UpdateVisitors()
        {
            Camera camera = Camera.main; UIView ui = UIView.GetAView();
            VisitorMark[] marks = visitorsVisible ? CivicVisits.Marks : new VisitorMark[0];
            int shown = 0;
            for (int i = 0; i < visitorPins.Length; i++)
            {
                bool visible = visitorsVisible && camera != null && i < marks.Length;
                Vector3 spot = Vector3.zero;
                if (visible)
                {
                    InstanceID unused;
                    // Walking, riding (car or transit), or inside the office during a disturbance.
                    visible = VisitorSpot(marks[i].Citizen, marks[i].Office, out spot, out unused);
                }
                if (!visible) { if (visitorPins[i] != null) visitorPins[i].Hide(); continue; }
                Vector3 point = camera.WorldToScreenPoint(spot + Vector3.up * 14);
                if (point.z <= 0) { if (visitorPins[i] != null) visitorPins[i].Hide(); continue; }
                float x = point.x / Screen.width * ui.fixedWidth - 90, y = (1 - point.y / Screen.height) * ui.fixedHeight - 38;
                if (x < 0 || y < 0 || x + 180 > ui.fixedWidth || y + 38 > ui.fixedHeight) { if (visitorPins[i] != null) visitorPins[i].Hide(); continue; }
                if (visitorPins[i] == null)
                {
                    int slot = i;
                    visitorPins[i] = (UIButton)ui.AddUIComponent(typeof(UIButton));
                    ConfigureButton(visitorPins[i], "", 0, 0, 180, 38);
                    visitorPins[i].autoSize = false; visitorPins[i].textScale = .62f; visitorPins[i].wordWrap = true;
                    visitorPins[i].eventClick += delegate {
                        CameraController control = ToolsModifierControl.cameraController;
                        if (control != null && visitorTargets[slot] != 0) FollowVisitor(control, visitorTargets[slot], visitorOffices[slot]);
                    };
                }
                visitorTargets[i] = marks[i].Citizen; visitorOffices[i] = marks[i].Office;
                visitorPins[i].relativePosition = new Vector3(x, y);
                visitorPins[i].text = VisitorText(marks[i]);
                visitorPins[i].textColor = marks[i].Kind == 0 ? new Color32(255, 215, 130, 255) : marks[i].Kind == 1 ? new Color32(150, 205, 255, 255) : new Color32(255, 140, 120, 255);
                visitorPins[i].tooltip = visitorPins[i].text + L.T(" · 이 모드가 보낸 주민입니다. 누르면 따라갑니다.");
                visitorPins[i].Show(); shown++;
            }
            if (!visitorsVisible) return;
            visitorStatus = marks.Length == 0 ? L.T("방문자 표시: 지금 행정청으로 가는 주민이 없습니다.")
                : L.T("방문자 표시: 화면 안 ") + shown + " / " + marks.Length + L.T("명 · 표식을 누르면 따라갑니다.");
            visitorToggle.tooltip = visitorStatus;
            if (status != null && !mapVisible) { status.text = visitorStatus; status.tooltip = visitorStatus; }
        }

        private string visitorStatus = "";

        // Flavour only: why this resident says they came. Everyday errands differ between the city hall and a district office.
        private static readonly string[] HallErrands = { "여권을 만들기 위해", "사업자 등록을 하러", "건축 허가를 문의하러", "세금 문제를 상의하러", "도시 계획 설명을 들으러", "봉사 활동을 신청하러", "반려동물 등록을 하러" };
        private static readonly string[] OfficeErrands = { "주민등록증을 발급받으러", "전입 신고를 하러", "등본을 떼러", "출생 신고를 하러", "혼인 신고를 하러", "복지 상담을 받으러", "쓰레기 배출 신고를 하러", "주차 과태료를 내러", "주차 허가증을 받으러", "이사 폐기물 수거를 신청하러" };

        private string VisitorText(VisitorMark mark)
        {
            string place = OfficeName(mark.Office);
            if (mark.Kind == 2) return place + L.T("에 불만을 품고\n난동을 피우러 방문") ;
            if (mark.Kind == 0) return L.T("민원을 접수하러\n") + place + L.T(" 행정 방문");
            string[] errands = mark.Hall ? HallErrands : OfficeErrands;
            int index = mark.Purpose < 0 || mark.Purpose >= errands.Length ? 0 : mark.Purpose;
            return L.In(errands[index], L.Current) + "\n" + place + L.T(" 행정 방문");
        }

        private readonly System.Collections.Generic.Dictionary<ushort, string> officeNames = new System.Collections.Generic.Dictionary<ushort, string>();
        private float nextNameSweep;

        private string OfficeName(ushort building)
        {
            if (building == 0) return L.T("행정청");
            // Building names change rarely; the cache is dropped every 30 seconds instead of asking the game every frame.
            if (Time.realtimeSinceStartup >= nextNameSweep) { officeNames.Clear(); nextNameSweep = Time.realtimeSinceStartup + 30f; }
            string name;
            if (officeNames.TryGetValue(building, out name)) return name;
            try { name = ColossalFramework.Singleton<BuildingManager>.instance.GetBuildingName(building, InstanceID.Empty); }
            catch (Exception) { name = null; }
            if (string.IsNullOrEmpty(name)) name = L.T("행정청");
            officeNames[building] = name;
            return name;
        }

        private void SelectMarker(ComplaintMarker marker)
        {
            if(marker==null)return;
            CityView view=CivicSimulation.View;int index=0;
            foreach(ComplaintView complaint in view.Complaints) if(complaint.Region==marker.Region) {
                if(complaint.Key==marker.Key && complaint.Stamp==marker.Stamp) {
                    browse=marker.Region;complaintIndex=index;showComplaints=true;showUnrest=false;editingAdministration=false;Show();Render(view);return;
                }
                index++;
            }
        }

        private void OpenDemands()
        {
            if (demandWindow != null) { demandWindow.isVisible = !demandWindow.isVisible; RenderDemands(); return; }
            UIView ui = UIView.GetAView();
            demandWindow = (UIPanel)ui.AddUIComponent(typeof(UIPanel));
            demandWindow.backgroundSprite = "MenuPanel2"; demandWindow.color = new Color32(184, 202, 217, 255);
            demandWindow.size = new Vector2(640, 600);
            demandWindow.relativePosition = new Vector3(Math.Max(8, (ui.fixedWidth - 640) / 2), Math.Max(8, (ui.fixedHeight - 600) / 2));
            Cell(demandWindow, L.T("시위 요구사항 · 개선 현황"), 20, 16, 550, 30, 1.2f, false).textColor = Accent;
            UIDragHandle drag = demandWindow.AddUIComponent<UIDragHandle>(); drag.size = new Vector2(570, 46); drag.target = demandWindow;
            Button(demandWindow, "X", 590, 12, 30, 28).eventClick += delegate { demandWindow.Hide(); };
            demandText = Cell(demandWindow, "", 20, 62, 600, 474, .86f, false); demandText.wordWrap = true;
            demandPage = Cell(demandWindow, "", 20, 550, 310, 26, .85f, false);
            Button(demandWindow, L.T("이전"), 370, 546, 110, 32).eventClick += delegate { demandIndex--; RenderDemands(); };
            Button(demandWindow, L.T("다음"), 490, 546, 110, 32).eventClick += delegate { demandIndex++; RenderDemands(); };
            RenderDemands();
        }
        private void RenderDemands()
        {
            string[] pages = CivicSimulation.Demands();
            demandIndex = (demandIndex % pages.Length + pages.Length) % pages.Length;
            demandText.text = pages[demandIndex];
            demandText.tooltip = demandText.text;
            demandPage.text = (demandIndex + 1) + " / " + pages.Length + L.T(" · 최근 완료된 집계 기준");
        }
        private TrendPoint[] TrendPoints()
        {
            long revision=CivicSimulation.TrendRevision();
            if(revision!=cachedTrendRevision || eventOrigin!=cachedTrendRegion){
                cachedTrendPoints=CivicSimulation.Trend(eventOrigin);
                cachedTrendRevision=revision;cachedTrendRegion=eventOrigin;
                cachedTrendSummary=HappinessTrend.Summary(cachedTrendPoints);
            }
            return cachedTrendPoints;
        }
        private string TrendSummary(){TrendPoints();return cachedTrendSummary;}
        private void OpenTrend()
        {
            if(trendWindow!=null){trendWindow.isVisible=!trendWindow.isVisible;RenderTrend();return;}
            UIView ui=UIView.GetAView();trendWindow=(UIPanel)ui.AddUIComponent(typeof(UIPanel));
            trendWindow.backgroundSprite="MenuPanel2";trendWindow.size=new Vector2(660,510);
            trendWindow.color=new Color32(184,202,217,255);
            trendWindow.relativePosition=new Vector3(Math.Max(8,(ui.fixedWidth-660)/2),Math.Max(8,(ui.fixedHeight-510)/2));
            Surface(trendWindow,10,8,640,38,new Color32(140,170,192,255));
            trendHeading=Cell(trendWindow,L.T("행복도 추이"),20,14,590,28,1.1f,false);
            UIDragHandle handle=trendWindow.AddUIComponent<UIDragHandle>();handle.size=new Vector2(600,44);handle.target=trendWindow;
            Button(trendWindow,"X",612,12,28,28).eventClick+=delegate{trendWindow.Hide();};
            trendSummary=Cell(trendWindow,"",22,60,616,28,1.1f,false);
            for(int i=0;i<3;i++){
                int value=100-i*50;float y=110+i*105;
                Cell(trendWindow,value.ToString(),16,y-8,34,20,.75f,true);
                Card(trendWindow,58,y,575,1,new Color32(160,180,190,255));
            }
            for(int i=0;i<25;i++){trendBars[i]=Card(trendWindow,58,110,14,1,Accent);trendBars[i].isInteractive=true;}
            Cell(trendWindow,L.T("24시간 전"),58,330,160,24,.8f,false);
            Cell(trendWindow,L.T("최근 집계"),473,330,160,24,.8f,true);
            trendDetails=Cell(trendWindow,"",22,372,616,106,.9f,false);trendDetails.wordWrap=true;
            trendDetails.tooltip=L.T("시간대별 마지막 집계. 비어 있는 시간대는 미기록입니다. 감점 변화에는 난이도·인구·지구 구성 변화도 반영됩니다. 자세한 내용은 도움말에서 확인하세요.");
            RenderTrend();
        }
        private void RenderTrend()
        {
            CityView view=CivicSimulation.View;
            RegionView region=eventOrigin<0 ? view.All : view.Regions[eventOrigin];
            string name=(eventOrigin<0 ? L.T("도시 전체") : region==null ? L.T("선택한 구") : region.Name);
            TrendPoint[] points=TrendPoints();
            if(drawnTrendRevision==cachedTrendRevision && drawnTrendRegion==eventOrigin && drawnTrendName==name)return;
            drawnTrendRevision=cachedTrendRevision;drawnTrendRegion=eventOrigin;drawnTrendName=name;
            trendHeading.text=name+L.T(" · 행복도 추이");
            trendSummary.text=cachedTrendSummary;
            trendDetails.text=HappinessTrend.Details(points);
            for(int i=0;i<25;i++){
                UITextureSprite bar=trendBars[i];bar.isVisible=i<points.Length;if(!bar.isVisible)continue;
                TrendPoint p=points[i];float x=58+(float)((p.Time-points[points.Length-1].Time)/(double)GameClock.DayTicks+1)*560;
                float height=Math.Max(2,(float)p.Score*2.1f);
                bar.relativePosition=new Vector3(x,320-height);bar.height=height;
                bar.color=p.Score<40 ? new Color32(245,145,120,255) : p.Score<80 ? new Color32(235,204,143,255) : new Color32(142,212,177,255);
                bar.tooltip=new DateTime(p.Time).ToString("MM-dd HH:mm")+" · "+p.Score.ToString("0.0")+L.T("점");
            }
        }
        private void OpenReports()
        {
            historyRegion = eventOrigin; reportIndex = 0;
            if (reportWindow != null) { reportWindow.isVisible = !reportWindow.isVisible; RenderReport(); return; }
            UIView ui=UIView.GetAView(); reportWindow=(UIPanel)ui.AddUIComponent(typeof(UIPanel));
            reportWindow.backgroundSprite="MenuPanel2"; reportWindow.size=new Vector2(640,500);
            reportWindow.color = new Color32(184, 202, 217, 255);
            Surface(reportWindow, 10, 8, 620, 38, new Color32(140, 170, 192, 255));
            Surface(reportWindow, 12, 54, 616, 374, new Color32(196, 215, 230, 255));
            reportWindow.relativePosition=new Vector3(Math.Max(8,(ui.fixedWidth-640)/2),Math.Max(8,(ui.fixedHeight-500)/2));
            Cell(reportWindow,L.T("도시 행정 · 기록"),20,14,380,30,1.2f,false);
            UIDragHandle handle=reportWindow.AddUIComponent<UIDragHandle>();handle.size=new Vector2(420,44);handle.target=reportWindow;
            reportMode = Button(reportWindow,"",430,12,145,28);
            reportMode.tooltip = L.T("클릭하여 민원 해결 이력 / 시위·폭동 보고서 전환");
            reportMode.eventClick += delegate { historyMode = !historyMode; reportIndex = 0; RenderReport(); };
            Button(reportWindow,"X",590,12,30,28).eventClick+=delegate{reportWindow.Hide();};
            reportText=Cell(reportWindow,"",20,62,600,360,.95f,false); reportText.wordWrap=true;
            reportPage=Cell(reportWindow,"",20,450,340,28,.8f,false);
            Button(reportWindow,L.T("이전"),370,444,110,32).eventClick+=delegate{reportIndex--;RenderReport();};
            Button(reportWindow,L.T("다음"),490,444,110,32).eventClick+=delegate{reportIndex++;RenderReport();};
            RenderReport();
        }
        private void RenderReport()
        {
            long revision=CivicSimulation.ReportsRevision(historyMode);
            if(drawnReportRevision==revision && drawnHistoryMode==historyMode && drawnReportRegion==historyRegion && drawnReportIndex==reportIndex)return;
            drawnReportRevision=revision;drawnHistoryMode=historyMode;drawnReportRegion=historyRegion;drawnReportIndex=reportIndex;
            string[] reports=historyMode ? CivicSimulation.History(historyRegion) : CivicSimulation.Reports();
            reportMode.text = historyMode ? L.T("민원 이력") : L.T("시위 · 폭동");
            string scope = historyMode ? (historyRegion < 0 ? L.T("도시 전체") : L.T("선택한 구")) : L.T("도시 전체");
            string limit = historyMode ? L.T("전체 최근 100건") : L.T("최근 20건");
            if(reports.Length==0) {reportText.text=historyMode ? L.T("이 지역의 해결된 민원이 없습니다.\n\n업데이트 이후 해결한 생활 민원부터 기록됩니다.") : L.T("아직 종료된 사건이 없습니다.");reportPage.text=scope+" · "+limit;return;}
            reportIndex=(reportIndex%reports.Length+reports.Length)%reports.Length; drawnReportIndex=reportIndex;
            reportText.text=reports[reportIndex]; reportText.tooltip=reportText.text;
            reportPage.text=(reportIndex+1)+" / "+reports.Length+" · "+scope+" · "+limit;
        }

        private void OpenHelp()
        {
            if (helpWindow != null) { helpWindow.isVisible = !helpWindow.isVisible; return; }
            UIView ui = UIView.GetAView();
            helpWindow = (UIPanel)ui.AddUIComponent(typeof(UIPanel));
            helpWindow.backgroundSprite = "MenuPanel2"; helpWindow.size = new Vector2(640, 700);
            helpWindow.color = new Color32(184, 202, 217, 255);
            Surface(helpWindow, 10, 8, 620, 40, new Color32(140, 170, 192, 255));
            helpWindow.relativePosition = new Vector3(Math.Max(8, (ui.fixedWidth - 640) / 2), Math.Max(8, (ui.fixedHeight - 700) / 2));
            Cell(helpWindow, L.T("도시 행정 · 도움말"), 20, 16, 560, 30, 1.2f, false);
            UIDragHandle handle = helpWindow.AddUIComponent<UIDragHandle>(); handle.size = new Vector2(580, 48); handle.target = helpWindow;
            Button(helpWindow, "X", 590, 12, 30, 28).eventClick += delegate { helpWindow.Hide(); };
            string[] helpPages = {
                L.T("건물 상태 · 민원 기준\n\n도로의 서비스 범위 대신 건물 상태의 계산값을 사용합니다.\n불량 방향 60%부터 경고, 80%부터 심각입니다.\n중간색과 양호 상태는 이번 항목의 피해로 세지 않습니다.\n\n건강: 건물 평균 건강 50 이하 / 심각 25 이하\n범죄: 건물 범죄 지표 60% 이상 / 심각 80% 이상\n쓰레기: 적체량 3,000 이상 / 심각 4,000 이상\n소방: 건물 안전 지표 40 이하 / 심각 20 이하\n실제 화재는 안전 지표와 관계없이 심각입니다.\n\n교육: 해당 단계 이수율 40% 이하이고 학교 배정도 부족할 때.\n이수율 20% 이하는 심각이며, 유효한 재학생은 제외합니다.\n의료·치안 서비스 부족은 불량 건물 상태도 함께 요구합니다.\n미해금 서비스 부족은 제외하고, 구별 피해를 집계합니다.\n\n60%·80%는 모드의 기준이며 게임의 공식 경고선은 아닙니다.\n테마·모드에 따라 실제 색조는 다를 수 있습니다.\n건설 후 상태 갱신과 다음 집계까지 기다려 주세요."),
                L.T("시작하기 · 시청과 구청 지정\n\n시청과 구청은 각각 별도 건물로 지정하는 것을 권장합니다.\n같은 건물에 역할을 겹치면 구청 농성과 시청 이관의 구분이\n불편할 수 있습니다. 이미 겸용 중이라면 별도 건물을 마련한 뒤\n‘행정청 변경’에서 지정 상태를 정리해 주세요.\n\n시청은 도시 전체의 행정을, 구청은 해당 일반 지구의 민원을\n담당합니다. 건물을 선택하고 ‘시청으로 지정’ 또는\n‘구청으로 지정’을 누른 뒤 상단의 지정 현황을 확인하세요.\n\n구청을 지정하지 않은 지역과 일반 지구 밖의 민원은\n시청이 직접 담당합니다. 지구 경계를 바꾸거나 행정청을\n철거했다면 지정 현황도 다시 확인하는 것이 좋습니다.\n\n권장 순서\n① 시청 지정 → ② 일반 지구 확인 → ③ 구별 구청 지정\n④ 도시를 잠시 진행해 집계 완료 → ⑤ 민원·행복도 확인"),
                L.T("도시 행정 창 · 화면 읽는 순서\n\n기본 단축키 Ctrl+/로 창을 열고 닫습니다.\n단축키는 모드 옵션에서 변경할 수 있습니다.\n지정한 행정청 건물을 클릭해서 창을 열 수도 있습니다.\n\n상단에서는 선택한 행정청과 도시·지구 행복도를 확인합니다.\n‘시 전체 / 이전 구 / 다음 구’로 조회 범위를 바꾸세요.\n하단의 ‘민원 / 행복도 / 시위·폭동’은 서로 다른 화면입니다.\n\n민원: 담당 지역, 처리 단계, 남은 시간, 피해 인원과 기준\n행복도: 분야별 경고·심각 인원, 피해 비율과 감점\n시위·폭동: 진행 단계, 요구 개선, 현장·경찰차 이동과 출동\n\n‘다시 집계’는 새로운 조사를 요청하는 기능입니다.\n누른 즉시 결과가 바뀌는 것은 아니므로 집계 완료를 기다리세요.\n도움말은 이 별도 창에서 이전·다음으로 읽을 수 있습니다."),
                L.T("행복도 · 5단계와 감점\n\n행복도는 0~100점이며 단계는 다음과 같습니다.\n0~20 미만: 최악 / 20~40 미만: 나쁨\n40~60 미만: 보통 / 60~80 미만: 좋음 / 80~100: 만족\n\n도시 점수는 집계 가능한 일반 지구 점수의 산술평균입니다.\n인구가 많은 구에 더 큰 비중을 두는 평균은 아닙니다.\n해당 지구가 없으면 전체 시민 집계를 사용합니다.\n\n피해가 넓게 퍼질수록 감점이 커집니다. 심각 피해는 1.5배로\n반영하며, 같은 시민의 같은 사유는 중복해서 세지 않습니다.\n소수의 잔여 피해는 고정 감점을 비율에 따라 줄입니다.\n버려진 건물은 별도 감점이 있으므로 복구도 확인하세요.\n\n게임의 평균 건강은 시민의 건강 상태를, 이 모드의 행복도는\n주거·교통·치안 등 여러 생활 여건을 함께 나타냅니다.\n‘추이 보기’에서는 최근 24게임 시간의 변화를 확인합니다.\n난이도를 바꾸거나 인구와 지구 경계가 달라지면 행복도에도 반영됩니다."),
                L.T("생활 불만 · 26개 항목\n\n주거·환경\n안정된 주거, 오염·오염수, 소음, 쓰레기, 공원 접근성, 침수\n\n기반시설·교통\n전기·수도·하수·난방, 연결도로 혼잡, 도로 연결 단절,\n대중교통 접근성, 대중교통 대기·혼잡\n\n보건·안전\n건강 악화, 의료 서비스, 시신 수거 지연,\n범죄, 치안 서비스, 소방 서비스·화재\n\n고용·교육\n일자리, 교육 기회, 높은 세금, 사업장 인력, 숙련 인력 부족\n\n상업·물류\n상품 공급, 원자재·연료 부족, 상품 반출 정체, 상권 고객 부족\n\n생활권 접근성은 대중교통 800m·공원 600m 직선거리 기준입니다.\n시설 경고와 시민 상태 등을 집계하므로 설치 직후에는\n게임의 상태 갱신과 다음 집계까지 기다려야 할 수 있습니다."),
                L.T("민원 접수 · 통계와 관할\n\n일반 민원은 환산 피해율이 접수 기준을 넘는 상태가\n항목별 대기시간 동안 이어질 때 접수됩니다.\n환산 피해율 = (피해 인원 + 심각 인원 × 0.5) ÷ 기준 인구\n\n접수 기준 초과 / 종결 기준 이하\n필수시설: 0.1% / 0.05%\n건강 악화: 2% / 1%\n일자리: 5% / 3%\n사업장 인력·숙련 인력 부족: 3% / 1.5%\n나머지 일반 항목: 1% / 0.5%\n\n일자리의 비율도 전체 거주 인구 기준이므로\n게임 통계창의 실업률과는 분모가 다릅니다.\n필수시설은 전기·수도·하수·난방, 시신 수거,\n소방·화재, 도로 연결 단절, 침수입니다.\n\n교통 혼잡은 도로 구간 중앙점, 대기 혼잡은 정류장 위치로\n관할을 정하며 다른 구에서 온 피해 시민도 합산합니다.\n교통 민원의 기준 인구는 관할 거주 인구와 현장 피해 인원 중\n큰 값입니다. 행복도 표의 거주 시민 집계와 다를 수 있습니다."),
                L.T("수락·거절 · 개선 확인과 종결\n\n‘수락’은 개선 약속입니다. 시설을 자동으로 설치하거나\n문제를 없애 주지 않습니다. 기한 전에 수락하면 기본 대응\n기한의 절반만큼 한 차례 연장됩니다. 재발 시 더 짧아집니다.\n\n거절하면 해당 민원의 처리가 끝납니다. 실제 피해와 행복도 감점은\n남으며, 문제가 지속되면 유예 후 다시 접수될 수 있습니다.\n약속한 민원은 중복 수락·거절할 수 없습니다.\n\n일반 민원은 피해가 0명이면 종결됩니다.\n소수 피해가 남아도 종결 기준 이하를 3게임 시간 유지하면\n‘개선 확인 후 종결’됩니다. 확인 중에는 시위 확대와\n기한 불이익을 멈춥니다. 다시 악화되면 확인 시간이 초기화됩니다.\n\n일자리·교육·인력·고객 부족은 변화가 느려 접수도 늦게 합니다.\n민원 종료와 행복도 100점 회복은 같은 뜻이 아닙니다.\n잔여 피해, 다른 불만, 버려진 건물의 감점을 함께 확인하세요."),
                L.T("문제 장소 · 지도와 짹짹이\n\n‘문제 장소 보기’는 민원 유형에 맞춰 이동합니다.\n사업장 인력·상품·고객 문제는 문제가 확인된 사업장,\n연결도로 혼잡은 실제 혼잡 도로,\n대중교통 대기·혼잡은 실제 정류장으로 이동합니다.\n교육·주거·대중교통 접근성 등 생활 문제는\n대표 피해 시민의 집을 보여 줍니다.\n\n민원은 지역 통계로 접수되며, 대표 시민이 직접 제출한 것은 아닙니다.\n집이나 사업장이 철거되거나 상황이 바뀌면 이동할 수 없습니다.\n집 주변에 문제가 보이지 않으면 직장·방문지에서 겪은 피해일 수 있습니다.\n민원 지도는 피해 현장과 담당 행정청을 보여 줍니다.\n\n새 공식 짹짹이 공지의 발신자를 누르면 담당 행정청으로 이동합니다.\n황당 민원은 실제 현장이 없어 위치를 표시하지 않습니다."),
                L.T("구청 신뢰 · 재발과 황당 민원\n\n구청 신뢰는 50에서 시작하며 0~100 범위입니다.\n일반 민원 해결 +2 / 약속 이행 +4\n거절 -2 / 대응 기한 초과 -3 / 약속 불이행 -8\n신뢰는 구청의 대응 여유와 집회 예고 시간에 영향을 줍니다.\n\n해결 후 7게임 일 안에 같은 지역·사유가 재접수되면\n재발 민원으로 표시합니다. 재발 1/2/3회부터 신뢰 -2/-4/-6,\n대응 기한 10/20/30% 단축을 적용하며 3회 수준이 상한입니다.\n일자리·교육·인력·고객 부족은 이 불이익을 절반 적용합니다.\n\n황당 민원은 통계 기반 생활 민원과 별도로 발생하는 기타 요구입니다.\n수락하면 6게임 시간 검토 후 답변하고, 거절하면 종결합니다.\n수락해도 태양·신호등 같은 게임 규칙을 바꾸지는 않습니다.\n황당 민원은 신뢰·시위 확대·재발 불이익 대상이 아닙니다.\n\n‘기록’에서 최근 해결 민원 100건과 사건 보고서 20건을\n살펴볼 수 있습니다. 같은 문제가 반복되는 지역을 확인하세요."),
                L.T("시위 · 예고부터 시청 이관까지\n\n행복도가 40점 미만이고 해결되지 않은 일반 민원이 기한을\n넘기면 자연 시위의 대상이 됩니다. 조건 충족 즉시 반드시\n발생하는 것은 아니며, 발생 빈도 설정도 영향을 줍니다.\n\n자연 집회는 기본 1게임 시간 전에 예고합니다.\n구청 신뢰에 따라 0.5~1.5시간이며, 원인 개선이나 행복도\n회복이 확인되면 예고가 취소될 수 있습니다.\n\n군중은 담당 구청으로 행진해 농성하고, 개선되지 않으면\n시청으로 이관됩니다. 구청이 없으면 시청이 직접 담당합니다.\n‘요구사항’에서 발생 당시와 현재 피해를 비교하세요.\n\n구청 농성 시 3% 확률로 대표단 면담 요청이 발생합니다.\n수락하면 시청 행진을 3게임 시간 유예합니다.\n시위당 한 번이며, 면담 자체가 문제 해결은 아닙니다.\n\n문제 개선·복구 또는 시간 조건을 충족하면 자진 해산합니다.\n수동 시위 발생은 시험용이며 자연 발생과 일부 조건이 다릅니다."),
                L.T("폭동·경찰 · 현장 대응\n\n시위는 느슨하게 행진하며 폭동대는 각자 이동·저항합니다.\n폭동은 차량 습격·화재·건물과 도로 피해를 일으킵니다.\n구급차는 습격하지 않습니다. 일반 자가용 일부는 통제 도로를 통과합니다.\n\n첫 경찰 출동은 최대 3대이며, 지원과 치료에는 실제 경찰차·구급차가 필요합니다.\n경찰은 저지선·최루탄·곤봉·고무탄으로 대응하며 일부는 실탄을 사용합니다.\n실탄은 사망을 일으킬 수 있으며 유혈은 표시하지 않습니다.\n부상 경관은 치료 후 한 사건에서 최대 2회 복귀합니다.\n\n모든 폭동대가 저항을 멈추면 경찰 승리입니다.\n대응 가능한 경관과 대기 증원이 없으면 경찰이 철수합니다.\n패배한 폭동대는 흩어져 도주하고, 승리하면 주변 차량을 습격하거나 행진을 이어갑니다.\n사망자는 해골 표시로 구분하며, 시신은 사건 종료까지 유지합니다.\n피해 차량은 사건 종료 후 순차 정리합니다.\n\n기존 도시를 별도 저장한 뒤 업데이트를 시험해 주세요."),
                L.T("시설을 설치했는데 민원이 남을 때\n\n전기·수도·하수·난방\n건물만 설치하지 말고 공급 여유와 연결 상태를 확인하세요.\n게임의 부족 경고가 사라진 뒤 모드 집계가 갱신되어야 합니다.\n\n의료·소방·치안\n시설 수만으로 모든 항목이 해소되는 것은 아닙니다.\n건강 악화와 의료 서비스는 서로 다른 불만입니다.\n범죄와 치안 서비스, 화재와 주변 서비스 상태도 함께 살피세요.\n\n쓰레기·시신 수거\n시설을 설치한 뒤 차량이 실제로 수거할 시간을 주세요.\n도로가 끊기거나 심하게 막히면 시설이 있어도 경고가 남습니다.\n\n게임을 잠시 진행하고 ‘다시 집계’ 완료 후 피해율을 확인하세요.\n남은 피해율이 종결 기준 이하면 개선 확인 3시간을 기다립니다.\n창을 닫거나 민원을 수락하는 것만으로 피해가 지워지지는 않습니다."),
                L.T("일자리·교육·상업을 개선하는 방법\n\n실업과 사업장 구인난은 동시에 생길 수 있습니다.\n일자리 수뿐 아니라 요구 학력, 시민의 학력과 통근 경로가\n맞아야 합니다. 대중교통을 연결해도 취업과 경고 갱신까지\n시간이 필요하며, 학교도 시민을 즉시 졸업시키지는 않습니다.\n\n일자리·사업장 인력·숙련 인력 민원은 일반 항목보다\n접수 대기 4배, 대응 기한과 구청 대응 시간 3배를 줍니다.\n접수 당시보다 환산 피해율이 20% 이상 줄면 한 차례\n해당 기본 대응 기한의 절반만큼 추가 시간을 줍니다.\n아직 피해가 크면 자동 해결로 처리하지 않습니다.\n기존 저장의 고용 민원에는 첫 집계 때 한 번 대응 여유를 줍니다.\n\n이 세 항목의 행복도 감점은 피해 비율에 비례합니다.\n작은 잔여 문제마다 고정 감점을 더하지 않습니다.\n실제 실업·사업장 경고는 그대로 표시하며,\n수요 막대나 교통시설 설치만으로 수치를 지우지는 않습니다.\n\n상품 부족은 공급·배송, 원자재 부족은 산업 물류,\n반출 정체는 수출 경로, 고객 부족은 상업 규모와 접근성을\n확인하세요. 교육과 구인 문제는 다음 통계에서도 살펴보세요."),
                L.T("대중교통·혼잡 · 서로 다른 세 가지 문제\n\n접근성 부족: 집에서 대중교통이 너무 멉니다. 800m 직선거리 생활권으로 판단하며, ‘문제 장소 보기’는 대표 피해 시민의 집으로 이동합니다.\n\n대기·혼잡: 실제로 대중교통을 기다리는 시민을 집계합니다. 기다림의 심각도는 게임 내부 카운터 기준입니다. 버튼은 실제 혼잡 정류장으로 이동합니다. 노선·차량·수송력을 확인하세요.\n\n연결도로 혼잡: 집·직장·방문지에 연결된 도로의 교통 밀도를 확인합니다. 시민 한 명은 가장 혼잡한 도로 한 곳에 집계됩니다. 밀도 80 이상은 경고, 95 이상은 심각이며, 버튼은 실제 혼잡 도로로 이동합니다."),
                L.T("시간·난이도 · 기한을 읽는 방법\n\n모드의 게임 시간은 도시의 낮·밤 주기에 맞춰 계산합니다.\nReal Time이 없으면 화면 달력보다 느리게 기한이 진행됩니다.\n현실 시간과 다르며 일시정지하면 기한도 멈춥니다.\n\n일반 접수 대기: 난이도 1~5에서 24 / 16 / 12 / 8 / 4시간.\n일자리·사업장 인력·숙련 인력은 그 4배,\n교육·고객 부족은 2배, 필수시설은 별도의 짧은 대기입니다.\n고용 관련 대응 기한과 구청 대응 시간은 일반 항목의 3배입니다.\n고급 설정의 값에도 이 배수가 적용됩니다.\n\n접수 대기, 대응 기한, 개선 확인 3시간은 서로 다릅니다.\n접수·종결 기준 사이에서는 기존 처리 상태를 유지합니다.\n시위·폭동 빈도 설정은 자연 발생·전환에 적용됩니다.\n\n이 버전부터 저장 당시 시간 배율을 기록합니다.\nReal Time 사용 여부나 시간 배율이 바뀌면 진행 중인\n기한의 남은 시간을 맞춥니다. 배율 기록이 없는 과거 저장은\n현재 배율을 기준으로 시작하며 과거 배율은 추정하지 않습니다.\n배율 변경 시 행복도 추이 표본은 새로 모읍니다."),
                L.T("폭동 이후 · 피해 복구와 경로 점검\n\n폭동은 시위보다 인원이 많고 이동도 빠릅니다.\n행진 중과 현장 주변에서 건물 화재·붕괴, 도로 피해가 생길 수 있습니다.\n경찰이 도착해 현장을 유지하는 동안 행진과 추가 피해를 멈춥니다.\n\n진압 성공은 군중 해산을 의미합니다.\n불이 난 건물과 파괴된 도로가 자동으로 복구되는 것은 아닙니다.\n소방 대응, 건물 재건과 도로 연결을 따로 확인하세요.\n현재 버전은 모든 건물 재건을 경찰 진압 성공의 조건으로 요구하지 않습니다.\n\n행진 경로는 인도가 있는 지상 일반 도로가 필요합니다.\n경로를 찾지 못하면 행정청 진입 도로와 연결 상태를 확인하세요.\n교량·고가·터널·고속도로·보행자 구역만으로 연결된 배치는\n행진 경로를 구성하지 못할 수 있습니다.\n\n경찰차 보기와 현장 보기로 위치를 각각 확인하세요.\n출동 요청 자체가 경찰 도착은 아니며, 길이 막혀 있으면 지연됩니다.\n현재 상태·실패 안내·남은 진압 시간을 시위·폭동 화면에서 확인하세요."),
                L.T("첫 플레이 점검 · 저장과 자주 묻는 상황\n\n처음에는 난이도 3에서 별도 시험 도시로 흐름을 확인해 보세요.\n시청·구청 지정 → 집계 완료 → 민원 기준 확인 → 실제 문제 개선\n→ 개선 확인·종결 → 기록 확인 순서로 진행하면 이해하기 쉽습니다.\n\n민원이 없는데 행복도가 낮나요?\n버려진 건물, 잔여 피해, 아직 접수 대기 중인 불만을 확인하세요.\n도시 점수와 특정 구의 점수를 혼동하지 않았는지도 살펴보세요.\n\n건강 화면은 좋은데 건강 민원이 있나요?\n도시 평균과 구별 피해 통계는 범위가 다릅니다.\n민원 상세의 환산 피해율이 종결 기준 이하인지 먼저 확인하세요.\n\n대표 민원인의 집에 아이콘이 없나요?\n직장·방문지 피해일 수 있습니다. 집 보기는 시민의 거주지 안내입니다.\n교통 관련 민원은 도로·정류장 보기와 민원 지도로 현장을 확인하세요.\n\n업데이트 전에는 도시를 별도 이름으로 저장하는 것을 권장합니다.\nHarmony를 활성화해야 시위·폭동·교통 통제 등 관련 기능을 사용할 수 있습니다.\n모드를 업데이트할 때는 게임을 먼저 종료해 주세요. 이전 버전으로 되돌릴 때는\n업데이트 전 저장본을 사용하세요.")
            };
            helpPages = L.Pages(helpPages);
            int helpPage = 0;
            UILabel text = Cell(helpWindow, helpPages[0], 20, 60, 600, 570, .9f, false); text.wordWrap = true;
            UILabel pageNumber = Cell(helpWindow, "1 / " + helpPages.Length, 260, 654, 120, 28, .95f, true);
            UIButton helpPrevious = Button(helpWindow, L.T("이전"), 20, 650, 130, 32);
            UIButton helpNext = Button(helpWindow, L.T("다음"), 490, 650, 130, 32);
            helpPrevious.isEnabled = false;
            Action<int> refreshHelp = delegate(int unused) {
                text.text = helpPages[helpPage]; pageNumber.text = (helpPage + 1) + " / " + helpPages.Length;
                helpPrevious.isEnabled = helpPage > 0; helpNext.isEnabled = helpPage < helpPages.Length - 1;
            };
            helpPrevious.eventClick += delegate { if(helpPage>0)helpPage--; refreshHelp(0); };
            helpNext.eventClick += delegate { if(helpPage<helpPages.Length-1)helpPage++; refreshHelp(0); };

        }

        public override void OnDestroy()
        {
            initialized = false;
            if (flatTexture != null) UnityEngine.Object.Destroy(flatTexture);
            if (floatingButton != null) UnityEngine.Object.Destroy(floatingButton.gameObject);
            if (helpWindow != null) UnityEngine.Object.Destroy(helpWindow.gameObject);
            if (reportWindow != null) UnityEngine.Object.Destroy(reportWindow.gameObject);
            if (demandWindow != null) UnityEngine.Object.Destroy(demandWindow.gameObject);
            if (trendWindow != null) UnityEngine.Object.Destroy(trendWindow.gameObject);
            foreach(UIButton pin in mapPins) if(pin!=null) UnityEngine.Object.Destroy(pin.gameObject);
            foreach(UIButton pin in visitorPins) if(pin!=null) UnityEngine.Object.Destroy(pin.gameObject);
            base.OnDestroy();
        }
    }
}
