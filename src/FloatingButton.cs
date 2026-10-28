using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // Positions use the available UI span, including the grip, rather than physical pixels.
    internal static class ButtonPosition
    {
        internal static float Clamp(float position, float screen, float size)
        {
            return Math.Max(0, Math.Min(position, Math.Max(0, screen - size)));
        }
        internal static float Resolve(float saved, float fallback, float screen, float size)
        {
            return Clamp(saved < 0 || float.IsNaN(saved) || float.IsInfinity(saved)
                ? fallback : saved * Math.Max(0, screen - size), screen, size);
        }
        internal static float Normalize(float position, float screen, float size)
        {
            return screen <= size ? 0 : Clamp(position, screen, size) / (screen - size);
        }
    }

    public sealed class FloatingButton : UIPanel
    {
        private UIView view;
        private float screenWidth, screenHeight;
        private int revision;
        private bool dragging;
        private Vector3 dragStart;

        internal void Initialize(UIView ui)
        {
            view = ui;
            size = new Vector2(176, 32);
            UIPanel grip = AddUIComponent<UIPanel>();
            grip.relativePosition = new Vector3(152, 0);
            grip.size = new Vector2(24, 32);
            grip.backgroundSprite = "ButtonMenu";
            UILabel dots = grip.AddUIComponent<UILabel>();
            dots.text = ":";
            dots.relativePosition = new Vector3(9, 5);
            dots.isInteractive = false;
            // Keep the grip compact while making its surrounding drag target easier to catch.
            UIDragHandle handle = AddUIComponent<UIDragHandle>();
            handle.relativePosition = new Vector3(120, 0);
            handle.size = new Vector2(56, 32);
            handle.target = this;
            handle.tooltip = L.T("손잡이를 끌어 버튼 위치 이동");
            handle.eventMouseDown += delegate(UIComponent component, UIMouseEventParameter p) {
                if (p.buttons != UIMouseButton.Left) return;
                dragging = true;
                dragStart = relativePosition;
            };
            Restore();
        }

        private void Restore()
        {
            screenWidth = view.fixedWidth;
            screenHeight = view.fixedHeight;
            revision = UnrestSettings.ButtonPositionRevision;
            relativePosition = new Vector3(
                ButtonPosition.Resolve(UnrestSettings.ButtonX, 16, screenWidth, width),
                ButtonPosition.Resolve(UnrestSettings.ButtonY, 96, screenHeight, height));
        }

        public override void Update()
        {
            base.Update();
            if (view == null) return;
            if (revision != UnrestSettings.ButtonPositionRevision || screenWidth != view.fixedWidth || screenHeight != view.fixedHeight)
            {
                dragging = false;
                Restore();
            }
            if (!dragging) return;
            if (Input.GetMouseButton(0)) return;
            dragging = false;
            Vector3 p = relativePosition;
            p.x = ButtonPosition.Clamp(p.x, screenWidth, width);
            p.y = ButtonPosition.Clamp(p.y, screenHeight, height);
            relativePosition = p;
            if (p != dragStart)
            {
                UnrestSettings.SaveButtonPosition(ButtonPosition.Normalize(p.x, screenWidth, width),
                    ButtonPosition.Normalize(p.y, screenHeight, height));
                revision = UnrestSettings.ButtonPositionRevision;
            }
        }
    }
}
