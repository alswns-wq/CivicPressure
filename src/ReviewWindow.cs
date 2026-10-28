using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // Administration review every 7 game days (CivicStanding): opened from the "평가" button in the administration panel.
    // A short Chirper notice and the panel button badge point here, so reviews never interrupt play with a popup.
    internal sealed class ReviewWindow : UIPanel
    {
        private static ReviewWindow instance;
        private static readonly Color32 Ink = new Color32(230, 238, 245, 255);
        private static readonly Color32 Muted = new Color32(169, 190, 205, 255);
        private static readonly Color32 Accent = new Color32(120, 214, 224, 255);
        private const float Width = 600, Height = 560;
        private Texture2D flat;
        private UILabel heading, grade, score, period, counts, advice, current, empty, historyTitle;
        private readonly UILabel[] partNames = new UILabel[4], partValues = new UILabel[4];
        private readonly UITextureSprite[] partBars = new UITextureSprite[4];
        private readonly UILabel[] history = new UILabel[StandingRules.MaxReviews];
        private float nextRefresh;

        internal static void Toggle()
        {
            UIView view = UIView.GetAView();
            if (view == null) return;
            if (instance == null)
            {
                instance = (ReviewWindow)view.AddUIComponent(typeof(ReviewWindow));
                instance.Build(view);
            }
            else instance.isVisible = !instance.isVisible;
            if (instance.isVisible) { instance.BringToFront(); instance.Refresh(); }
        }

        internal static void Release()
        {
            if (instance != null) UnityEngine.Object.Destroy(instance.gameObject);
            instance = null;
        }

        private void Build(UIView view)
        {
            name = "CivicPressureReview";
            backgroundSprite = "MenuPanel2";
            color = new Color32(184, 202, 217, 255);
            size = new Vector2(Width, Height);
            relativePosition = new Vector3(Mathf.Round((view.fixedWidth - Width) / 2), Mathf.Round((view.fixedHeight - Height) / 2));
            flat = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            flat.name = "CivicPressureReviewFlat";
            flat.SetPixel(0, 0, Color.white); flat.Apply();

            Surface(8, 6, Width - 16, 36, new Color32(140, 170, 192, 255));
            heading = Text(L.T("행정 평가"), 20, 14, 420, 1f); heading.textColor = Accent;
            UIDragHandle drag = AddUIComponent<UIDragHandle>();
            drag.relativePosition = Vector3.zero; drag.size = new Vector2(Width - 48, 42); drag.target = this;
            UIButton close = AddUIComponent<UIButton>();
            close.normalBgSprite = "buttonclose"; close.hoveredBgSprite = "buttonclosehover"; close.pressedBgSprite = "buttonclosepressed";
            close.size = new Vector2(32, 32); close.relativePosition = new Vector3(Width - 42, 8);
            close.eventClick += delegate { isVisible = false; };

            Surface(12, 50, Width - 24, 118, new Color32(150, 189, 211, 255));
            grade = Text("", 28, 58, 110, 4f);
            score = Text("", 140, 66, 430, 1.3f);
            period = Text("", 140, 100, 440, .8f); period.textColor = Muted;
            current = Text("", 140, 124, 440, .8f); current.textColor = Muted;
            empty = Text("", 28, 70, 540, .95f); empty.wordWrap = true; empty.autoHeight = true;

            Surface(12, 176, Width - 24, 176, new Color32(205, 218, 229, 255));
            string[] names = { L.T("행복도"), L.T("민원 처리율"), L.T("구청 신뢰"), L.T("평온 (시위·폭동·강행)") };
            string[] weights = { " 40%", " 25%", " 20%", " 15%" };
            for (int i = 0; i < 4; i++)
            {
                float y = 188 + i * 40;
                partNames[i] = Text(names[i] + weights[i], 24, y, 210, .8f);
                Card(240, y + 4, 280, 14, new Color32(90, 110, 125, 255));
                partBars[i] = Card(240, y + 4, 1, 14, Accent);
                partValues[i] = Text("", 528, y, 50, .85f);
            }

            counts = Text("", 22, 360, Width - 44, .8f); counts.wordWrap = true; counts.autoHeight = true;
            advice = Text("", 22, 404, Width - 44, .85f); advice.wordWrap = true; advice.autoHeight = true; advice.textColor = new Color32(255, 220, 150, 255);

            Surface(12, 462, Width - 24, 86, new Color32(150, 170, 188, 255));
            historyTitle = Text(L.T("지난 평가 (오래된 것 → 최근)"), 22, 468, 400, .8f);
            for (int i = 0; i < history.Length; i++)
            {
                history[i] = Text("", 24 + i * 46, 496, 44, 1.6f);
                history[i].textAlignment = UIHorizontalAlignment.Center;
                history[i].isInteractive = true;
            }
        }

        private UIPanel Surface(float x, float y, float w, float h, Color32 tint)
        {
            UIPanel panel = AddUIComponent<UIPanel>();
            panel.relativePosition = new Vector3(x, y); panel.size = new Vector2(w, h);
            panel.backgroundSprite = "MenuPanel2"; panel.color = tint; panel.isInteractive = false;
            return panel;
        }

        private UITextureSprite Card(float x, float y, float w, float h, Color32 tint)
        {
            UITextureSprite card = AddUIComponent<UITextureSprite>();
            card.relativePosition = new Vector3(x, y); card.size = new Vector2(w, h);
            card.texture = flat; card.color = tint; card.isInteractive = false;
            return card;
        }

        private UILabel Text(string text, float x, float y, float w, float scale)
        {
            UILabel label = AddUIComponent<UILabel>();
            label.autoSize = false; label.width = w; label.height = 24 * scale; label.textScale = scale;
            label.relativePosition = new Vector3(x, y); label.text = text; label.textColor = Ink;
            return label;
        }

        private static Color32 GradeColor(string g)
        {
            switch (g)
            {
                case "S": return new Color32(120, 214, 224, 255);
                case "A": return new Color32(142, 212, 177, 255);
                case "B": return new Color32(235, 204, 143, 255);
                case "C": return new Color32(245, 170, 120, 255);
                default: return new Color32(245, 130, 110, 255);
            }
        }

        public override void Update()
        {
            base.Update();
            if (!isVisible || Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + 1f;
            Refresh();
        }

        private void Refresh()
        {
            StandingView v;
            try { v = CivicSimulation.Standing(); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Review window: " + error.Message); return; }
            CivicStanding.UnseenGrade = "";
            Review shown = v.Reviews.Length > 0 ? v.Reviews[v.Reviews.Length - 1] : null;
            bool has = shown != null;
            grade.isVisible = score.isVisible = period.isVisible = has;
            empty.isVisible = !has;
            string left = L.T("다음 평가까지 ") + v.HoursLeft.ToString("0") + L.T(" 게임 시간");
            if (!v.Available) { empty.text = CivicStanding.Error; empty.isVisible = true; return; }
            if (!has)
            {
                empty.text = L.T("아직 행정 평가가 없습니다. 게임 7일마다 행복도, 민원 처리율, 구청 신뢰, 평온을 종합해 S~D 등급을 매깁니다.") + "\n" + left;
                shown = v.Current;
            }
            else
            {
                grade.text = shown.Grade; grade.textColor = GradeColor(shown.Grade);
                score.text = shown.Score.ToString("0") + L.T("점 · 지난 7일");
                period.text = new DateTime(shown.Ended).ToString("yyyy-MM-dd") + L.T(" 평가 · ") + left;
            }
            current.text = L.T("진행 중인 7일 예상: ") + v.Current.Grade + " (" + v.Current.Score.ToString("0") + L.T("점)");
            current.isVisible = true;
            current.relativePosition = has ? new Vector3(140, 124) : new Vector3(28, 150);
            double[] parts = { shown.Happiness, shown.Handling, shown.Trust, shown.Calm };
            for (int i = 0; i < 4; i++)
            {
                double value = Math.Max(0, Math.Min(100, parts[i]));
                partBars[i].width = Math.Max(1, (float)(280 * value / 100));
                partBars[i].color = value < 45 ? new Color32(245, 145, 120, 255) : value < 75 ? new Color32(235, 204, 143, 255) : new Color32(142, 212, 177, 255);
                partValues[i].text = value.ToString("0");
            }
            counts.text = L.T("민원 접수 ") + shown.Opened + L.T(" · 해결 ") + shown.Resolved + L.T(" · 기한 초과 ") + shown.Missed +
                L.T(" · 시위 ") + shown.Protests + L.T(" · 폭동 ") + shown.Riots + L.T(" · 강행한 시설 ") + shown.Enforced;
            advice.text = L.T(StandingRules.Advice(shown));
            for (int i = 0; i < history.Length; i++)
            {
                Review r = i < v.Reviews.Length ? v.Reviews[i] : null;
                history[i].text = r == null ? "·" : r.Grade;
                history[i].textColor = r == null ? Muted : GradeColor(r.Grade);
                history[i].tooltip = r == null ? "" : new DateTime(r.Ended).ToString("yyyy-MM-dd") + " · " + r.Score.ToString("0") + L.T("점");
            }
        }
    }
}
