using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CivicPressure
{
    // Community translation files (subscriber request: Russian). No game dependencies, so the checks cover it.
    //
    // Format, UTF-8:
    // {
    //   "language": "ru",            code, also matched against the game language
    //   "name": "Русский",           shown in the options
    //   "strings": { "<Korean source text>": "<translation>", ... }
    // }
    // Keys are the mod's Korean source texts. The template lists every key with its English text as the value, so a
    // translator replaces the values. Missing or empty values fall back to English.
    internal sealed class LocaleData
    {
        internal string Code = "", Name = "";
        internal readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
    }

    internal static class LocaleFile
    {
        internal const string TemplateName = "en.template.json";
        internal const string MissingSuffix = ".missing.json";

        // Language codes become file names and settings values: letters, digits and '-' only.
        internal static bool ValidCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 16) return false;
            foreach (char c in code) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-') return false;
            return true;
        }

        // Throws FormatException with a readable reason (shown in the log for the translator).
        internal static LocaleData Parse(string text)
        {
            if (text == null) throw new FormatException("empty file");
            Reader reader = new Reader(text);
            object root = reader.Value();
            reader.End();
            Dictionary<string, object> obj = root as Dictionary<string, object>;
            if (obj == null) throw new FormatException("the file must be one JSON object { ... }");
            LocaleData data = new LocaleData();
            object value;
            if (obj.TryGetValue("language", out value) && value is string) data.Code = ((string)value).Trim().ToLowerInvariant().Replace('_', '-');
            if (!ValidCode(data.Code)) throw new FormatException("\"language\" must be a code such as \"ru\" or \"pt-br\"");
            if (obj.TryGetValue("name", out value) && value is string) data.Name = ((string)value).Trim();
            if (data.Name.Length == 0) data.Name = data.Code;
            if (!obj.TryGetValue("strings", out value) || !(value is Dictionary<string, object>)) throw new FormatException("\"strings\" must be an object { \"source\": \"translation\" }");
            foreach (KeyValuePair<string, object> item in (Dictionary<string, object>)value)
            {
                string translation = item.Value as string;
                if (!string.IsNullOrEmpty(translation)) data.Strings[item.Key] = translation;
            }
            return data;
        }

        // entries: source key and the reference text to translate (English).
        internal static string Write(string code, string name, IEnumerable<KeyValuePair<string, string>> entries)
        {
            StringBuilder s = new StringBuilder();
            s.Append("{\n  \"language\": ").Append(Quote(code)).Append(",\n  \"name\": ").Append(Quote(name)).Append(",\n  \"strings\": {");
            bool first = true;
            foreach (KeyValuePair<string, string> entry in entries)
            {
                s.Append(first ? "\n    " : ",\n    ").Append(Quote(entry.Key)).Append(": ").Append(Quote(entry.Value));
                first = false;
            }
            s.Append(first ? "}\n}\n" : "\n  }\n}\n");
            return s.ToString();
        }

        internal static string Quote(string text)
        {
            StringBuilder s = new StringBuilder("\"");
            foreach (char c in text ?? "")
            {
                switch (c)
                {
                    case '"': s.Append("\\\""); break;
                    case '\\': s.Append("\\\\"); break;
                    case '\n': s.Append("\\n"); break;
                    case '\r': s.Append("\\r"); break;
                    case '\t': s.Append("\\t"); break;
                    default:
                        if (c < 0x20) s.Append("\\u").Append(((int)c).ToString("x4"));
                        else s.Append(c);
                        break;
                }
            }
            return s.Append('"').ToString();
        }

        // Minimal JSON reader: objects, arrays, strings, numbers, true/false/null. Enough for translation files.
        private sealed class Reader
        {
            private readonly string text;
            private int at;
            internal Reader(string source) { text = source; if (text.Length > 0 && text[0] == '﻿') at = 1; }

            private FormatException Error(string what)
            {
                int line = 1, column = 1;
                for (int i = 0; i < at && i < text.Length; i++) { if (text[i] == '\n') { line++; column = 1; } else column++; }
                return new FormatException(what + " (line " + line + ", column " + column + ")");
            }

            private void Space() { while (at < text.Length && char.IsWhiteSpace(text[at])) at++; }

            internal void End() { Space(); if (at != text.Length) throw Error("unexpected text after the end"); }

            internal object Value()
            {
                Space();
                if (at >= text.Length) throw Error("unexpected end of file");
                char c = text[at];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                throw Error("unexpected character '" + c + "'");
            }

            private bool Word(string word)
            {
                if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0) return false;
                at += word.Length; return true;
            }

            private Dictionary<string, object> Object()
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                at++; Space();
                if (at < text.Length && text[at] == '}') { at++; return result; }
                while (true)
                {
                    Space();
                    if (at >= text.Length || text[at] != '"') throw Error("expected a quoted name");
                    string key = String();
                    Space();
                    if (at >= text.Length || text[at] != ':') throw Error("expected ':'");
                    at++;
                    object value = Value();
                    if (result.ContainsKey(key)) throw Error("duplicate entry \"" + (key.Length > 40 ? key.Substring(0, 40) + "…" : key) + "\"");
                    result.Add(key, value);
                    Space();
                    if (at < text.Length && text[at] == ',') { at++; continue; }
                    if (at < text.Length && text[at] == '}') { at++; return result; }
                    throw Error("expected ',' or '}' (a missing comma between entries?)");
                }
            }

            private List<object> Array()
            {
                List<object> result = new List<object>();
                at++; Space();
                if (at < text.Length && text[at] == ']') { at++; return result; }
                while (true)
                {
                    result.Add(Value());
                    Space();
                    if (at < text.Length && text[at] == ',') { at++; continue; }
                    if (at < text.Length && text[at] == ']') { at++; return result; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string String()
            {
                StringBuilder s = new StringBuilder();
                at++;
                while (true)
                {
                    if (at >= text.Length) throw Error("unterminated text (missing closing quote)");
                    char c = text[at++];
                    if (c == '"') return s.ToString();
                    if (c != '\\') { s.Append(c); continue; }
                    if (at >= text.Length) throw Error("unterminated escape");
                    char e = text[at++];
                    switch (e)
                    {
                        case '"': s.Append('"'); break;
                        case '\\': s.Append('\\'); break;
                        case '/': s.Append('/'); break;
                        case 'b': s.Append('\b'); break;
                        case 'f': s.Append('\f'); break;
                        case 'n': s.Append('\n'); break;
                        case 'r': s.Append('\r'); break;
                        case 't': s.Append('\t'); break;
                        case 'u':
                            int code;
                            if (at + 4 > text.Length || !int.TryParse(text.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw Error("bad \\u escape");
                            s.Append((char)code); at += 4; break;
                        default: throw Error("unknown escape \\" + e);
                    }
                }
            }

            private double Number()
            {
                int start = at;
                while (at < text.Length && "+-0123456789.eE".IndexOf(text[at]) >= 0) at++;
                double value;
                if (!double.TryParse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw Error("bad number");
                return value;
            }
        }
    }
}
