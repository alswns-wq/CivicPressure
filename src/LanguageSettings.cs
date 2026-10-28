using System;
using System.IO;
using System.Text;
using ColossalFramework;
using ColossalFramework.IO;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using ColossalFramework.Globalization;
using ICities;
using UnityEngine;

namespace CivicPressure
{
    internal static class LanguageSettings
    {
        private static SavedInt language;
        // A community language chosen in the options (file code such as "ru"); empty for built-in or automatic.
        private static SavedString languageCode;
        internal static string Folder = "";

        internal static void Initialize()
        {
            if (language != null) return;
            if (GameSettings.FindSettingsFileByName("CivicPressure") == null)
                GameSettings.AddSettingsFile(new SettingsFile[] { new SettingsFile { fileName = "CivicPressure" } });
            language = new SavedInt("DisplayLanguage", "CivicPressure", -1, true);
            languageCode = new SavedString("DisplayLanguageCode", "CivicPressure", "", true);
            LoadTranslations();
            int community = L.IndexOf(languageCode.value);
            L.Current = community >= L.BuiltIn ? community : L.Resolve(language.value, LocaleManager.instance.language);
        }

        // Translation files: the mod's own Locale folder (shipped with a Workshop update), then the player's folder
        // %LOCALAPPDATA%\Colossal Order\Cities_Skylines\CivicPressure\Locale, which survives updates and overrides.
        // The player's folder also receives en.template.json and <code>.missing.json on every start.
        private static void LoadTranslations()
        {
            try
            {
                string user = Path.Combine(Path.Combine(DataLocation.localApplicationData, "CivicPressure"), "Locale");
                Folder = user;
                Directory.CreateDirectory(user);
                UTF8Encoding utf8 = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(user, LocaleFile.TemplateName), LocaleFile.Write("xx", "Language name", L.EnglishCatalog()), utf8);
                File.WriteAllText(Path.Combine(user, "README.txt"), Readme, utf8);
                string mod = ModFolder();
                foreach (string folder in new string[] { mod == null ? null : Path.Combine(mod, "Locale"), user })
                {
                    if (folder == null || !Directory.Exists(folder)) continue;
                    foreach (string file in Directory.GetFiles(folder, "*.json"))
                    {
                        string name = Path.GetFileName(file);
                        if (name == LocaleFile.TemplateName || name.EndsWith(LocaleFile.MissingSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            LocaleData data = LocaleFile.Parse(File.ReadAllText(file, Encoding.UTF8));
                            if (L.Register(data) < 0) { Debug.LogWarning("[CivicPressure] Translation " + file + " skipped: built-in language or too many languages."); continue; }
                            int missing = L.Missing(data).Count;
                            string report = Path.Combine(user, data.Code + LocaleFile.MissingSuffix);
                            if (missing > 0) File.WriteAllText(report, LocaleFile.Write(data.Code, data.Name, L.Missing(data)), utf8);
                            else if (File.Exists(report)) File.Delete(report);
                            Debug.Log("[CivicPressure] Translation loaded: " + data.Code + " (" + data.Name + ") from " + file + ", " + data.Strings.Count + " texts, " + missing + " missing.");
                        }
                        catch (Exception error) { Debug.LogWarning("[CivicPressure] Translation " + file + " skipped: " + error.Message); }
                    }
                }
            }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Translation files unavailable: " + error.Message); }
        }

        private static string ModFolder()
        {
            foreach (PluginManager.PluginInfo plugin in Singleton<PluginManager>.instance.GetPluginsInfo())
                if (plugin != null && plugin.userModInstance != null && plugin.userModInstance.GetType().Assembly == typeof(LanguageSettings).Assembly)
                    return plugin.modPath;
            return null;
        }

        private const string Readme =
            "Civic Pressure - adding a translation\r\n\r\n" +
            "1. Copy en.template.json and rename the copy after your language code, for example ru.json.\r\n" +
            "2. In the copy, set \"language\" to the code (\"ru\") and \"name\" to the language name (\"Русский\").\r\n" +
            "3. Under \"strings\", replace each English value on the right with your translation.\r\n" +
            "   Do not change the text on the left (the key). Keep \\n (line breaks) and numbers/placeholders as they are.\r\n" +
            "   Untranslated entries may stay in English or be removed; they are shown in English.\r\n" +
            "4. Save as UTF-8, restart the game, then choose your language in Options > Civic Pressure > Language.\r\n\r\n" +
            "After each mod update, en.template.json is rewritten with the current texts, and <code>.missing.json lists\r\n" +
            "the texts your file does not translate yet. Translate those and add them to your file.\r\n" +
            "Problems with a file are written to output_log.txt (search for [CivicPressure] Translation).\r\n" +
            "To include your translation in the mod for everyone, send the file to the mod author.\r\n";
        internal static void CreateUI(UIHelperBase helper)
        {
            Initialize();
            UIComponent root = ((UIHelper)helper).self as UIComponent;
            UIPanel host = root.AddUIComponent<UIPanel>();
            host.size = new Vector2(Math.Max(540, root.width - 20), 700);
            UITabstrip tabs = host.AddUIComponent<UITabstrip>();
            tabs.size = new Vector2(host.width, 36); tabs.relativePosition = Vector3.zero;
            // Separate owned panels: no template-cloned tab pages or inherited visibility.
            // The gameplay tab scrolls: advanced difficulty adds enough rows to push the rest out of view
            // (user report 2026-09-18: "고급 옵션창을 키면 그 밑에 있는 항목을 확인 할 수가 없어").
            UIPanel gameplay = host.AddUIComponent<UIPanel>();
            UIPanel languages = host.AddUIComponent<UIPanel>();
            gameplay.size = new Vector2(host.width, 652); gameplay.autoLayout = false; gameplay.clipChildren = true;
            UIScrollablePanel gameplayView = Scrollable(gameplay, host.width, 652);
            Configure(languages, host.width);
            languages.autoLayout = false;
            gameplay.relativePosition = languages.relativePosition = new Vector3(0, 46);
            string[] titles = { L.T("게임플레이"), L.Current == 1 ? "Language" : L.T("언어") + " / Language" };
            for (int i = 0; i < titles.Length; i++) {
                UIButton button = tabs.AddTab(titles[i]);
                button.font = UIView.GetAView().defaultFont; button.size = new Vector2(240, 36);
                button.normalBgSprite = "ButtonMenu"; button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed"; button.focusedBgSprite = "ButtonMenuFocused";
                button.textScale = .9f;
            }
            tabs.eventSelectedIndexChanged += delegate(UIComponent component, int index) {
                ShortcutSettings.CancelCapture();
                gameplay.isVisible = index == 0; languages.isVisible = index == 1;
            };
            UIHelper gameHelper = new UIHelper(gameplayView);
            ShortcutSettings.CreateUI(gameHelper); UnrestSettings.CreateUI(gameHelper);
            UIPanel selector = languages.AddUIComponent<UIPanel>();
            selector.size = new Vector2(host.width - 24, 90);
            selector.relativePosition = Vector3.zero;
            UIHelper localeHelper = new UIHelper(selector);
            UIDropDown dropdown = null;
            UILabel notice = languages.AddUIComponent<UILabel>();
            notice.font = UIView.GetAView().defaultFont; notice.autoSize = false;
            notice.size = new Vector2(host.width - 24, 90); notice.wordWrap = true; notice.textScale = .9f;
            notice.relativePosition = new Vector3(0, 108);
            notice.text = L.T("언어 변경은 게임을 다시 시작한 뒤 적용됩니다. 기존 기록과 도시·도로·건물 이름은 그대로 유지됩니다.");
            // Where translators put their files (see README.txt there).
            UILabel files = languages.AddUIComponent<UILabel>();
            files.font = UIView.GetAView().defaultFont; files.autoSize = false;
            files.size = new Vector2(host.width - 24, 110); files.wordWrap = true; files.textScale = .8f;
            files.relativePosition = new Vector3(0, 206);
            files.text = L.T("번역 추가: 아래 폴더에 언어 파일(.json)을 넣고 게임을 다시 시작하세요. 만드는 방법은 같은 폴더의 README.txt에 있습니다.") + "\n" + Folder;
            bool restoring = false;
            int languageCount = L.Count;
            string[] choices = new string[languageCount + 1]; choices[0] = L.T("게임 언어에 맞춤 (자동)");
            for (int i = 0; i < languageCount; i++) choices[i + 1] = L.Name(i);
            int community = L.IndexOf(languageCode.value);
            int selected = community >= L.BuiltIn ? community + 1 : language.value >= 0 && language.value < L.BuiltIn ? language.value + 1 : 0;
            dropdown = (UIDropDown)localeHelper.AddDropdown(L.T("모드 표시 언어"), choices, selected, delegate(int index) {
                if (restoring || index < 0 || index >= choices.Length) return;
                int previous = language.value;
                string previousCode = languageCode.value;
                try {
                    bool file = index - 1 >= L.BuiltIn;
                    language.value = file ? -1 : index - 1;
                    languageCode.value = file ? L.Code(index - 1) : "";
                    GameSettings.SaveAll();
                    notice.text = L.In("언어 변경은 게임을 다시 시작한 뒤 적용됩니다. 기존 기록과 도시·도로·건물 이름은 그대로 유지됩니다.", file ? index - 1 : L.Resolve(language.value, LocaleManager.instance.language));
                } catch (Exception error) {
                    language.value = previous; languageCode.value = previousCode;
                    int back = L.IndexOf(previousCode);
                    restoring = true; dropdown.selectedIndex = back >= L.BuiltIn ? back + 1 : previous >= 0 && previous < L.BuiltIn ? previous + 1 : 0; restoring = false;
                    notice.text = L.T("언어 설정을 저장하지 못했습니다. 다시 시도해 주세요.");
                    Debug.LogError("[CivicPressure] Could not save display language. " + error);
                }
            });
            dropdown.parent.relativePosition = Vector3.zero;
            tabs.selectedIndex = 0;
            gameplay.isVisible = true; languages.isVisible = false;
        }
        // A scrolling list inside holder, with the game's own scrollbar sprites.
        private static UIScrollablePanel Scrollable(UIPanel holder, float width, float height)
        {
            UIScrollablePanel view = holder.AddUIComponent<UIScrollablePanel>();
            view.size = new Vector2(width - 20, height);
            view.relativePosition = Vector3.zero;
            view.autoLayout = true;
            view.autoLayoutDirection = LayoutDirection.Vertical;
            view.autoLayoutPadding = new RectOffset(0, 0, 8, 8);
            view.clipChildren = true;
            view.scrollWheelDirection = UIOrientation.Vertical;
            view.scrollWheelAmount = 40;
            view.builtinKeyNavigation = true;

            UIScrollbar bar = holder.AddUIComponent<UIScrollbar>();
            bar.orientation = UIOrientation.Vertical;
            bar.pivot = UIPivotPoint.TopLeft;
            bar.size = new Vector2(16, height);
            bar.relativePosition = new Vector3(width - 18, 0);
            bar.minValue = 0; bar.value = 0; bar.incrementAmount = 40;
            UISlicedSprite track = bar.AddUIComponent<UISlicedSprite>();
            track.relativePosition = Vector3.zero; track.autoSize = true;
            track.size = new Vector2(16, height); track.spriteName = "ScrollbarTrack";
            bar.trackObject = track;
            UISlicedSprite thumb = track.AddUIComponent<UISlicedSprite>();
            thumb.relativePosition = Vector3.zero; thumb.fillDirection = UIFillDirection.Vertical;
            thumb.autoSize = true; thumb.width = 16; thumb.spriteName = "ScrollbarThumb";
            bar.thumbObject = thumb;
            view.verticalScrollbar = bar;
            return view;
        }

        private static void Configure(UIPanel panel, float width)
        {
            panel.size = new Vector2(width, 652); panel.autoLayout = true;
            panel.autoLayoutDirection = LayoutDirection.Vertical;
            panel.autoLayoutPadding = new RectOffset(0, 0, 8, 8);
        }
    }
}
