using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // .NET 3.5 in this project resolves "Action" to a generic type, so the notice buttons use their own delegate.
    internal delegate void NoticeAction();

    // Update notice window in the administration window's style. Replaces the game's ExceptionPanel, whose warning
    // icon made the notice look like an error.
    internal sealed class NoticePanel : UIPanel
    {
        private static readonly Color32 Ink = new Color32(230, 238, 245, 255);
        private static readonly Color32 Accent = new Color32(120, 214, 224, 255);
        private const float Width = 600;

        internal static void Open(string title, string message)
        {
            Ask(title, message, null, null, null, null);
        }

        // Two-button variant (difficulty 5 warning): confirm runs accept, everything else runs cancel.
        internal static void Ask(string title, string message, string acceptText, string cancelText, NoticeAction accept, NoticeAction cancel)
        {
            UIView view = UIView.GetAView();
            if (view == null) { if (accept != null) accept(); return; }
            NoticePanel old = view.FindUIComponent<NoticePanel>("CivicPressureNotice");
            if (old != null) old.Close();
            NoticePanel panel = (NoticePanel)view.AddUIComponent(typeof(NoticePanel));
            panel.onAccept = accept; panel.onCancel = cancel;
            panel.Build(view, title, message, acceptText, cancelText);
        }

        private NoticeAction onAccept, onCancel;
        private bool decided;

        private void Build(UIView view, string title, string message, string acceptText, string cancelText)
        {
            name = "CivicPressureNotice";
            backgroundSprite = "MenuPanel2";
            color = new Color32(184, 202, 217, 255);
            width = Width;

            Surface(8, 6, Width - 16, 36, new Color32(140, 170, 192, 255));
            UILabel heading = Text(title, 20, 15, Width - 80, L.Current == 0 ? .9f : .8f);
            heading.textColor = Accent;
            UIDragHandle drag = AddUIComponent<UIDragHandle>();
            drag.relativePosition = Vector3.zero; drag.size = new Vector2(Width - 48, 42); drag.target = this;

            UIButton close = AddUIComponent<UIButton>();
            close.normalBgSprite = "buttonclose"; close.hoveredBgSprite = "buttonclosehover"; close.pressedBgSprite = "buttonclosepressed";
            close.size = new Vector2(32, 32); close.relativePosition = new Vector3(Width - 42, 8);
            close.eventClick += delegate { Decide(false); };

            UILabel body = Text(message, 28, 60, Width - 56, .85f);
            body.textColor = Ink;
            float bodyHeight = Math.Max(40, body.height);
            Surface(12, 48, Width - 24, bodyHeight + 24, new Color32(150, 170, 188, 255)).SendToBack();

            bool two = cancelText != null;
            UIButton ok = AddUIComponent<UIButton>();
            ok.text = acceptText ?? L.T("확인");
            ok.normalBgSprite = "ButtonMenu"; ok.hoveredBgSprite = "ButtonMenuHovered"; ok.pressedBgSprite = "ButtonMenuPressed";
            ok.textScale = .9f; ok.size = new Vector2(two ? 200 : 140, 36);
            ok.relativePosition = new Vector3(two ? Width / 2 + 10 : (Width - 140) / 2, 48 + bodyHeight + 36);
            ok.eventClick += delegate { Decide(true); };
            if (two)
            {
                UIButton no = AddUIComponent<UIButton>();
                no.text = cancelText;
                no.normalBgSprite = "ButtonMenu"; no.hoveredBgSprite = "ButtonMenuHovered"; no.pressedBgSprite = "ButtonMenuPressed";
                no.textScale = .9f; no.size = new Vector2(200, 36);
                no.relativePosition = new Vector3(Width / 2 - 210, 48 + bodyHeight + 36);
                no.eventClick += delegate { Decide(false); };
            }

            height = 48 + bodyHeight + 36 + 36 + 18;
            relativePosition = new Vector3(Mathf.Round((view.fixedWidth - width) / 2), Mathf.Round((view.fixedHeight - height) / 2));
            eventKeyDown += delegate(UIComponent component, UIKeyEventParameter key)
            {
                if (key.used || (key.keycode != KeyCode.Escape && key.keycode != KeyCode.Return)) return;
                key.Use(); Decide(key.keycode == KeyCode.Return);
            };
            UIView.PushModal(this);
            BringToFront();
            Focus();
        }

        private UIPanel Surface(float x, float y, float w, float h, Color32 tint)
        {
            UIPanel panel = AddUIComponent<UIPanel>();
            panel.relativePosition = new Vector3(x, y); panel.size = new Vector2(w, h);
            panel.backgroundSprite = "MenuPanel2"; panel.color = tint;
            panel.isInteractive = false;
            return panel;
        }

        private UILabel Text(string text, float x, float y, float w, float scale)
        {
            UILabel label = AddUIComponent<UILabel>();
            label.autoSize = false; label.autoHeight = true; label.wordWrap = true;
            label.width = w; label.textScale = scale;
            label.relativePosition = new Vector3(x, y);
            label.text = text;
            return label;
        }

        // Closing the window any other way counts as cancelling.
        private void Decide(bool accepted)
        {
            if (decided) { Close(); return; }
            decided = true;
            NoticeAction run = accepted ? onAccept : onCancel;
            Close();
            try { if (run != null) run(); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Notice action: " + error.Message); }
        }

        private void Close()
        {
            try { if (UIView.GetModalComponent() == this) UIView.PopModal(); }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Notice close: " + error.Message); }
            UnityEngine.Object.Destroy(gameObject);
        }
    }
}
