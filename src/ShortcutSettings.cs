using System;
using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace CivicPressure
{
    internal static class ShortcutSettings
    {
        private const string FileName = "CivicPressure";
        private static SavedInputKey binding;
        private static UIButton capture;
        private static float suppressUntil;

        internal static void Initialize()
        {
            if (binding != null) return;
            if (GameSettings.FindSettingsFileByName(FileName) == null)
                GameSettings.AddSettingsFile(new SettingsFile[] { new SettingsFile { fileName = FileName } });
            binding = new SavedInputKey("ToggleHappinessPanel", FileName, KeyCode.Slash, true, false, false, true);
        }

        internal static bool Pressed()
        {
            if (binding == null || Time.realtimeSinceStartup < suppressUntil ||
                (capture != null && capture.isVisible && capture.hasFocus) ||
                UIView.HasModalInput() || UIView.HasInputFocus()) return false;
            // SavedInputKey.IsPressed() means held, not a new press. Poll every frame before the UI refresh throttle.
            return binding.Key != KeyCode.None && Input.GetKeyDown(binding.Key) && binding.IsPressed();
        }

        private static string Caption()
        {
            string key = binding.Key == KeyCode.Slash ? "/" : binding.Key.ToString();
            return L.T("열기/닫기: ") + (binding.Control ? "Ctrl + " : "") +
                (binding.Shift ? "Shift + " : "") + (binding.Alt ? "Alt + " : "") + key + L.T("  (클릭하여 변경)");
        }

        private static bool ModifierOnly(KeyCode key)
        {
            return key == KeyCode.None || key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                key == KeyCode.LeftCommand || key == KeyCode.RightCommand || key == KeyCode.LeftWindows ||
                key == KeyCode.RightWindows || key == KeyCode.Menu;
        }

        internal static void CancelCapture()
        {
            UIButton previous = capture;
            capture = null;
            suppressUntil = Time.realtimeSinceStartup + 0.3f;
            if (previous != null) previous.text = Caption();
        }

        private static void SetBinding(InputKey value, UIButton button)
        {
            InputKey previous = binding.value;
            CancelCapture();
            try
            {
                binding.value = value;
                GameSettings.SaveAll();
                button.text = Caption();
                Debug.Log("[CivicPressure] Popup shortcut changed: " + binding.ToString());
            }
            catch (Exception error)
            {
                binding.value = previous;
                button.text = L.T("단축키 저장 실패 · 클릭하여 다시 시도");
                Debug.LogError("[CivicPressure] Could not save popup shortcut. " + error);
            }
        }

        internal static void CreateUI(UIHelperBase helper)
        {
            Initialize();
            CancelCapture();
            UIHelperBase group = helper.AddGroup(L.T("행복도 창 단축키"));
            UIButton button = null;
            button = (UIButton)group.AddButton(Caption(), delegate
            {
                CancelCapture();
                capture = button;
                button.text = L.T("원하는 키 조합을 누르세요 · Esc: 취소");
                button.Focus();
            });
            button.canFocus = true;
            button.eventKeyDown += delegate(UIComponent component, UIKeyEventParameter key)
            {
                if (capture != button) return;
                key.Use();
                if (key.keycode == KeyCode.Escape) { CancelCapture(); return; }
                if (ModifierOnly(key.keycode)) return;
                SetBinding(SavedInputKey.Encode(key.keycode, key.control, key.shift, key.alt), button);
            };
            button.eventLostFocus += delegate { if (capture == button) CancelCapture(); };
            button.eventVisibilityChanged += delegate(UIComponent component, bool visible)
            {
                if (!visible && capture == button) CancelCapture();
            };
            group.AddButton(L.T("기본값 복원 (Ctrl + /)"), delegate
            {
                SetBinding(SavedInputKey.Encode(KeyCode.Slash, true, false, false), button);
            });
        }
    }
}
