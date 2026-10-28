using System;
using System.Collections.Generic;
using System.Text;

namespace CivicPressure
{
    // No game dependencies: exact source literals only, never player-provided names.
    internal static partial class L
    {
        internal const int BuiltIn = 5, English = 1;
        internal static int Current = 0;
        internal static readonly string[] LanguageNames = { "한국어", "English", "简体中文", "日本語", "Español" };
        private static readonly Dictionary<string, string[]> Texts = Build();
        private static readonly Dictionary<string[], string[][]> Arrays = new Dictionary<string[], string[][]>();
        // Community translation files (LocaleFile), indexes BuiltIn and up. Registered before Current is chosen.
        private static readonly List<LocaleData> External = new List<LocaleData>();
        internal const int MaxLanguages = 32;

        internal static int Count { get { lock (External) return BuiltIn + External.Count; } }
        internal static int Valid(int language) { return language >= 0 && language < Count ? language : 0; }

        internal static string Name(int language)
        {
            if (language >= 0 && language < BuiltIn) return LanguageNames[language];
            lock (External) return language >= BuiltIn && language - BuiltIn < External.Count ? External[language - BuiltIn].Name : "";
        }

        internal static string Code(int language)
        {
            string[] codes = { "ko", "en", "zh", "ja", "es" };
            if (language >= 0 && language < BuiltIn) return codes[language];
            lock (External) return language >= BuiltIn && language - BuiltIn < External.Count ? External[language - BuiltIn].Code : "";
        }

        internal static int IndexOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            code = code.Trim().ToLowerInvariant().Replace('_', '-');
            for (int i = 0; i < Count; i++) if (Code(i) == code) return i;
            return -1;
        }

        // A file for a built-in language code is ignored (the built-in text is the reviewed one); a later file with
        // the same community code replaces the earlier one (the player's own folder is loaded after the mod folder).
        internal static int Register(LocaleData data)
        {
            if (data == null || !LocaleFile.ValidCode(data.Code)) return -1;
            lock (External)
            {
                int existing = IndexOf(data.Code);
                if (existing >= 0 && existing < BuiltIn) return -1;
                lock (Arrays) Arrays.Clear();
                if (existing >= BuiltIn) { External[existing - BuiltIn] = data; return existing; }
                if (BuiltIn + External.Count >= MaxLanguages) return -1;
                External.Add(data);
                return BuiltIn + External.Count - 1;
            }
        }

        internal static void ClearExternal() { lock (External) { External.Clear(); lock (Arrays) Arrays.Clear(); } }

        internal static int Resolve(int preference, string gameLanguage)
        {
            if (preference >= 0 && preference < Count) return preference;
            string code = (gameLanguage ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            if (code == "ko" || code == "kr" || code.StartsWith("ko-")) return 0;
            if (code == "zh" || code == "cn" || code.StartsWith("zh-")) return 2;
            if (code == "ja" || code == "jp" || code.StartsWith("ja-")) return 3;
            if (code == "es" || code.StartsWith("es-")) return 4;
            int external = IndexOf(code);
            if (external < 0 && code.IndexOf('-') > 0) external = IndexOf(code.Substring(0, code.IndexOf('-')));
            return external >= BuiltIn ? external : 1; // Unsupported game locales use English.
        }

        internal static string T(string source) { return In(source, Current); }
        internal static string In(string source, int language)
        {
            string[] translations;
            int index = Valid(language);
            if (source == null || index == 0 || !Texts.TryGetValue(source, out translations)) return source;
            if (index < BuiltIn) return translations[index - 1];
            string community;
            lock (External) if (External[index - BuiltIn].Strings.TryGetValue(source, out community)) return community;
            return translations[English - 1];
        }

        // Every source text with its English translation: the template translators start from.
        internal static List<KeyValuePair<string, string>> EnglishCatalog()
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string[]> item in Texts) list.Add(new KeyValuePair<string, string>(item.Key, item.Value[English - 1]));
            return list;
        }

        // Source texts a community file does not translate yet (with English), for "<code>.missing.json".
        internal static List<KeyValuePair<string, string>> Missing(LocaleData data)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string[]> item in Texts)
                if (data == null || !data.Strings.ContainsKey(item.Key)) list.Add(new KeyValuePair<string, string>(item.Key, item.Value[English - 1]));
            return list;
        }

        internal static string[] Array(string[] source)
        {
            int language = Valid(Current);
            if (language == 0) return source;
            lock (Arrays) {
                string[][] cached;
                if (!Arrays.TryGetValue(source, out cached)) { cached = new string[MaxLanguages][]; Arrays.Add(source, cached); }
                if (cached[language] == null) {
                    string[] result = new string[source.Length];
                    for (int i = 0; i < result.Length; i++) result[i] = In(source[i], language);
                    cached[language] = result;
                }
                return cached[language];
            }
        }
        // Bound help pages conservatively by visual width, retaining every character.
        // East Asian glyphs count as two units. Break at words when possible.
        internal static string[] Pages(string[] chapters)
        {
            List<string> pages = new List<string>();
            foreach (string chapter in chapters) {
                StringBuilder page = new StringBuilder();
                int rows = 0;
                foreach (string original in chapter.Split('\n')) {
                    string line = original;
                    do {
                        int end = 0, units = 0, space = -1;
                        while (end < line.Length) {
                            int width = line[end] >= 0x2e80 ? 2 : 1;
                            if (units + width > 64) break;
                            if (line[end] == ' ') space = end;
                            units += width; end++;
                        }
                        if (end < line.Length && space > 24) end = space + 1;
                        if (rows == 23) { pages.Add(page.ToString()); page.Length = 0; rows = 0; }
                        if (rows > 0) page.Append('\n');
                        page.Append(line.Substring(0, end)); rows++;
                        line = line.Substring(end);
                    } while (line.Length > 0);
                }
                if (page.Length > 0) pages.Add(page.ToString());
            }
            return pages.ToArray();
        }
    }
}
