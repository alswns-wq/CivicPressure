using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    internal static class LocalizedUI
    {
        internal static void Fit(UILabel label, float preferred)
        {
            if (L.Current == 0) return;
            label.eventTextChanged += delegate { Size(label, preferred); };
            Size(label, preferred);
        }
        private static void Size(UILabel label, float preferred)
        {
            if (label.font == null || label.width <= 0 || string.IsNullOrEmpty(label.text)) return;
            using (UIFontRenderer renderer = label.font.ObtainRenderer()) {
                renderer.textScale = preferred; renderer.pixelRatio = 1f;
                renderer.wordWrap = label.wordWrap; renderer.multiLine = true;
                renderer.maxSize = new Vector2(label.width, 10000);
                Vector2 measured = renderer.MeasureString(label.text);
                float fit = Mathf.Min(1f, Mathf.Min(label.width / Mathf.Max(1, measured.x), label.height / Mathf.Max(1, measured.y)));
                label.textScale = Mathf.Max(.65f, preferred * fit);
                if (fit < .99f) label.tooltip = label.text;
            }
        }
        internal static void Fit(UIButton button)
        {
            if (L.Current == 0) return;
            Size(button);
        }
        private static void Size(UIButton button)
        {
            if (button.font == null || string.IsNullOrEmpty(button.text)) return;
            using (UIFontRenderer renderer = button.font.ObtainRenderer()) {
                renderer.textScale = .9f; renderer.pixelRatio = 1f;
                renderer.wordWrap = false; renderer.multiLine = false;
                renderer.maxSize = new Vector2(10000, 10000);
                Vector2 measured = renderer.MeasureString(button.text);
                button.textScale = Mathf.Max(.6f, .9f * Mathf.Min(1f, (button.width - 12) / Mathf.Max(1, measured.x)));
            }
        }
    }
    // UIButton has no text-change event in CS1; refit only when its caption changes.
    internal sealed class LocalizedButton : UIButton
    {
        public override string text
        {
            get { return base.text; }
            set { base.text = value; LocalizedUI.Fit(this); }
        }
    }
}
