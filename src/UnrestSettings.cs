using ColossalFramework;
using ICities;
using ColossalFramework.UI;

namespace CivicPressure
{
    internal static class UnrestSettings
    {
        private static SavedInt protest, riot, difficulty;
        private static SavedBool oddRequests, officeDisturbances, opposition, suggestions, visitorMarkers, mapMarkers, custom;
        private static SavedFloat[] tuned;
        private static SavedFloat buttonX, buttonY;
        internal static int ButtonPositionRevision;
        // Advanced difficulty values, in DifficultyValue order.
        private static readonly string[] Keys = { "DifficultyPenalty", "DifficultyFrequency", "DifficultyPersistence", "DifficultyDeadline", "DifficultyOffice" };
        private static readonly string[] TitlesSource = { "행복도 감점", "사건 발생 빈도", "불만 지속 시간", "민원 대응 기한", "구청 농성 기한" };
        private static readonly float[] Steps = { .05f, .05f, 1, 1, 1 };
        private static UISlider[] sliders;
        private static UILabel[] sliderLabels;
        private static UIComponent advancedPanel;
        private static UILabel levelRules;
        private static UIDropDown levelDrop;
        private static readonly string[] NamesSource = { "끔", "낮음 (0.5배)", "기본 (1배)", "높음 (2배)", "매우 높음 (5배)" };
        private static string[] Names { get { return L.Array(NamesSource); } }
        private static readonly double[] Multipliers = { 0, 0.5, 1, 2, 5 };
        internal static void Initialize()
        {
            if (protest != null) return;
            ShortcutSettings.Initialize();
            protest = new SavedInt("ProtestFrequency", "CivicPressure", 2, true);
            riot = new SavedInt("RiotFrequency", "CivicPressure", 2, true);
            difficulty = new SavedInt("Difficulty", "CivicPressure", 3, true);
            oddRequests = new SavedBool("OddRequests", "CivicPressure", true, true);
            officeDisturbances = new SavedBool("OfficeDisturbances", "CivicPressure", true, true);
            opposition = new SavedBool("FacilityOpposition", "CivicPressure", true, true);
            suggestions = new SavedBool("ResidentSuggestions", "CivicPressure", true, true);
            visitorMarkers = new SavedBool("VisitorMarkers", "CivicPressure", false, true);
            mapMarkers = new SavedBool("ComplaintMapMarkers", "CivicPressure", false, true);
            custom = new SavedBool("DifficultyCustom", "CivicPressure", false, true);
            buttonX = new SavedFloat("MainButtonX", "CivicPressure", -1, true);
            buttonY = new SavedFloat("MainButtonY", "CivicPressure", -1, true);
            tuned = new SavedFloat[Keys.Length];
            for (int i = 0; i < Keys.Length; i++) tuned[i] = new SavedFloat(Keys[i], "CivicPressure", (float)Difficulty.Default(i, difficulty.value), true);
            Apply();
        }
        private static int Valid(int n) { return n < 0 || n >= Names.Length ? 2 : n; }
        private static void Apply()
        {
            Difficulty.Level = difficulty.value < 1 || difficulty.value > 5 ? 3 : difficulty.value;
            Difficulty.Custom = custom.value;
            for (int i = 0; i < Keys.Length; i++) Difficulty.Set(i, tuned[i].value);
            CivicUnrest.SetRates(Multipliers[Valid(protest.value)], Multipliers[Valid(riot.value)]);
            Features.OddRequests = oddRequests.value;
            Features.OfficeDisturbances = officeDisturbances.value;
            Features.Opposition = opposition.value;
            Features.Suggestions = suggestions.value;
            Features.VisitorMarkers = visitorMarkers.value;
        }
        internal static bool MapMarkers { get { Initialize(); return mapMarkers.value; } }
        internal static float ButtonX { get { Initialize(); return buttonX.value; } }
        internal static float ButtonY { get { Initialize(); return buttonY.value; } }
        internal static void SaveButtonPosition(float x, float y)
        {
            Initialize(); buttonX.value = x; buttonY.value = y;
            ButtonPositionRevision++; GameSettings.SaveAll();
        }
        internal static void SetMapMarkers(bool on) { Initialize(); mapMarkers.value = on; GameSettings.SaveAll(); }
        internal static void SetVisitorMarkers(bool on) { Initialize(); visitorMarkers.value = on; GameSettings.SaveAll(); Features.VisitorMarkers = on; }
        internal static void CreateUI(UIHelperBase helper)
        {
            Initialize();
            UIHelperBase levels = helper.AddGroup(L.T("도시 운영 난이도 · 1 여유 / 3 기준 / 5 극한"));
            levelDrop = levels.AddDropdown(L.T("난이도"), new string[] { L.T("1 · 여유"), L.T("2 · 쉬움"), L.T("3 · 기준"), L.T("4 · 어려움"), L.T("5 · 극한") }, Difficulty.Level - 1, delegate(int index) {
                // Difficulty 5 turns on its own rules, so it asks first and goes back when refused.
                if (index + 1 == 5 && difficulty.value != 5) { NoticePanel.Ask(L.T("경고 · 5단계는 극한 모드입니다"), ExtremeWarning(), L.T("5단계로 설정"), L.T("취소"), delegate { ChooseLevel(5); }, delegate { if (levelDrop != null) levelDrop.selectedIndex = difficulty.value - 1; }); return; }
                ChooseLevel(index + 1);
            }) as UIDropDown;
            levelRules = Caption(levels, LevelRules());
            levels.AddCheckbox(L.T("난이도 고급 설정 (값을 직접 조절)"), custom.value, delegate(bool on) {
                custom.value = on; GameSettings.SaveAll(); Apply(); CivicSimulation.Refresh();
                if (advancedPanel != null) advancedPanel.isVisible = on;
                RefreshSliders();
            });
            // Advanced values (subscriber feedback Kalamari 2026-09-18). The level keeps deciding its own rules.
            UIHelperBase advanced = helper.AddGroup(L.T("난이도 고급 설정 · 값 조절 (난이도 단계 규칙은 그대로)"));
            sliders = new UISlider[Keys.Length]; sliderLabels = new UILabel[Keys.Length];
            for (int i = 0; i < Keys.Length; i++) AddValue(advanced, i);
            advanced.AddButton(L.T("현재 난이도 기본값으로 되돌리기"), delegate {
                for (int i = 0; i < Keys.Length; i++)
                {
                    tuned[i].value = (float)Difficulty.Default(i, Difficulty.Level);
                    if (sliders[i] != null) sliders[i].value = tuned[i].value;
                }
                GameSettings.SaveAll(); Apply(); CivicSimulation.Refresh(); RefreshSliders();
            });
            advancedPanel = Host(advanced);
            if (advancedPanel != null) advancedPanel.isVisible = custom.value;
            UIHelperBase group = helper.AddGroup(L.T("시위·폭동 발생 빈도 (클릭하여 변경)"));
            Add(group, protest, L.T("시위 발생")); Add(group, riot, L.T("폭동 전환"));
            // Subscriber feedback 3: optional events.
            UIHelperBase events = helper.AddGroup(L.T("이벤트 켜기·끄기"));
            events.AddCheckbox(L.T("황당 민원 (엉뚱한 요청)"), oddRequests.value, delegate(bool on) { oddRequests.value = on; GameSettings.SaveAll(); Apply(); });
            events.AddCheckbox(L.T("행정청 소란 (방문 민원인 난동과 경찰 출동)"), officeDisturbances.value, delegate(bool on) { officeDisturbances.value = on; GameSettings.SaveAll(); Apply(); });
            events.AddCheckbox(L.T("시설 반대 (주거지 근처 기피 시설에 대한 주민 반대)"), opposition.value, delegate(bool on) { opposition.value = on; GameSettings.SaveAll(); Apply(); });
            events.AddCheckbox(L.T("주민 건의 (서비스 확충 요청)"), suggestions.value, delegate(bool on) { suggestions.value = on; GameSettings.SaveAll(); Apply(); });
            // Subscriber feedback (2026-09-18): tell the mod's office visitors apart from ordinary citizens.
            UIHelperBase display = helper.AddGroup(L.T("화면 표시"));
            display.AddButton(L.T("버튼 위치 초기화"), delegate { SaveButtonPosition(-1, -1); });
            display.AddCheckbox(L.T("방문자 표시 유지"), visitorMarkers.value, delegate(bool on) { SetVisitorMarkers(on); });
            display.AddCheckbox(L.T("민원 지도 표시 유지"), mapMarkers.value, delegate(bool on) { SetMapMarkers(on); });
        }
        private static void ChooseLevel(int level)
        {
            difficulty.value = level;
            // Keep the sliders on the new level's numbers unless the player has tuned them on purpose.
            for (int i = 0; i < Keys.Length; i++)
            {
                tuned[i].value = (float)Difficulty.Default(i, level);
                if (sliders != null && sliders[i] != null) sliders[i].value = tuned[i].value;
            }
            GameSettings.SaveAll(); Apply(); CivicSimulation.Refresh();
            if (levelRules != null) levelRules.text = LevelRules();
            RefreshSliders();
        }

        // What the chosen level switches on by itself; only difficulty 5 adds rules of its own.
        private static string LevelRules()
        {
            string head = L.T("현재 난이도 ") + Difficulty.Level + L.T("단계 · ");
            if (Difficulty.Level >= 5) return head + L.T("사이렌 시설(소방서·경찰서·병원)도 반대 대상 · 강행하면 폭동 예고");
            if (Difficulty.Level >= 3) return head + L.T("강행하면 반대 집회 예고");
            return head + L.T("강행해도 집회는 없고 신뢰 하락과 행복도 감점만");
        }

        private static string ExtremeWarning()
        {
            return L.T("5단계에서만 켜지는 규칙입니다.\n\n• 사이렌 시설 반대: 소방서·경찰서·병원을 주거지 가까이 지으면 주민이 반대합니다(반경 250m, 주택 6채 이상).\n• 강행 시 폭동: 주민 반대를 무시하면 반대 집회가 아니라 격렬한 시위(폭동)가 예고됩니다.\n• 감점·사건 빈도·대응 기한도 가장 가혹합니다.\n\n이미 지어 둔 시설은 그대로이며, 바꾼 뒤 새로 짓는 시설부터 적용됩니다.");
        }

        private static void AddValue(UIHelperBase group, int value)
        {
            int slot = value;
            UISlider slider = group.AddSlider(Title(slot), (float)Difficulty.Minimum(slot), (float)Difficulty.Maximum(slot), Steps[slot], tuned[slot].value, delegate(float amount) {
                tuned[slot].value = amount; GameSettings.SaveAll(); Apply(); CivicSimulation.Refresh(); RefreshSliders();
            }) as UISlider;
            sliders[slot] = slider;
            if (slider == null || slider.parent == null) return;
            sliderLabels[slot] = slider.parent.Find<UILabel>("Label");
            if (sliderLabels[slot] != null) { sliderLabels[slot].width = 480; sliderLabels[slot].text = Title(slot) + ": " + Amount(slot); }
        }

        private static string Title(int value) { return L.In(TitlesSource[value], L.Current); }

        private static string Amount(int value)
        {
            double amount = Difficulty.Custom ? tuned[value].value : Difficulty.Default(value, Difficulty.Level);
            return value <= (int)DifficultyValue.Frequency ? amount.ToString("0.00") + L.T("배") : amount.ToString("0") + L.T("게임 시간");
        }

        private static void RefreshSliders()
        {
            if (sliderLabels == null) return;
            for (int i = 0; i < sliderLabels.Length; i++) if (sliderLabels[i] != null) sliderLabels[i].text = Title(i) + ": " + Amount(i);
        }

        // The content panel of an options group, so the advanced values can be hidden until they are wanted.
        private static UIComponent Host(UIHelperBase group)
        {
            UIHelper helper = group as UIHelper;
            UIComponent content = helper == null ? null : helper.self as UIComponent;
            return content == null ? null : content.parent ?? content;
        }

        private static UILabel Caption(UIHelperBase group, string text)
        {
            UIComponent content = Host(group);
            UIHelper helper = group as UIHelper;
            UIComponent target = helper != null && helper.self as UIComponent != null ? (UIComponent)helper.self : content;
            if (target == null) return null;
            UILabel label = target.AddUIComponent<UILabel>();
            label.autoSize = false; label.autoHeight = true; label.wordWrap = true;
            label.width = 640; label.textScale = .85f; label.text = text;
            return label;
        }

        private static void Add(UIHelperBase group, SavedInt setting, string title)
        {
            UIButton button = null;
            button = (UIButton)group.AddButton(title + ": " + Names[Valid(setting.value)], delegate {
                setting.value = (Valid(setting.value) + 1) % Names.Length;
                GameSettings.SaveAll(); Apply(); button.text = title + ": " + Names[Valid(setting.value)];
            });
        }
    }
}
