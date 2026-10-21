using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>Key → text for ONE language. Built by loading every JSON file in that language's folder.</summary>
public sealed class LanguageTable
{
    private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Code { get; }
    public int Count => _map.Count;
    public IEnumerable<string> Keys => _map.Keys;
    public IReadOnlyDictionary<string, string> Entries => _map;

    public LanguageTable(string code) { Code = code ?? ""; }

    public bool Contains(string key) => key != null && _map.ContainsKey(key);
    public bool TryGet(string key, out string value) { value = null; return key != null && _map.TryGetValue(key, out value); }
    public void Set(string key, string value) { if (!string.IsNullOrEmpty(key)) _map[key] = value ?? ""; }
    public bool Remove(string key) => key != null && _map.Remove(key);
    public void Clear() => _map.Clear();

    /// <summary>Parse one JSON text (nested objects flattened with dots) and add its keys. Returns keys added/changed.</summary>
    public int AddJson(string json, string keyPrefix = "", bool overwrite = true, string sourceName = "json", List<string> issues = null)
    {
        var flat = new List<KeyValuePair<string, string>>();
        try { LocalizationJson.Flatten(json, keyPrefix, flat); }
        catch (Exception ex)
        {
            issues?.Add($"✖ [{Code}] {sourceName}: {ex.Message}");
            return 0;
        }

        int n = 0;
        foreach (var kv in flat)
        {
            if (_map.ContainsKey(kv.Key))
            {
                if (!overwrite) continue;
                issues?.Add($"• [{Code}] key '{kv.Key}' redefined in '{sourceName}' (last one wins).");
            }
            _map[kv.Key] = kv.Value;
            n++;
        }
        return n;
    }

    public int AddDictionary(IDictionary<string, string> source, bool overwrite = true)
    {
        if (source == null) return 0;
        int n = 0;
        foreach (var kv in source)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (!overwrite && _map.ContainsKey(kv.Key)) continue;
            _map[kv.Key] = kv.Value ?? "";
            n++;
        }
        return n;
    }

    /// <summary>
    /// Load every TextAsset under Resources/{resourcePath} (sub-folders included, sorted by name).
    /// Returns the number of files found.
    /// </summary>
    public int LoadFolder(string resourcePath, bool prefixWithFileName = false, bool overwrite = true, List<string> issues = null)
    {
        TextAsset[] files = Resources.LoadAll<TextAsset>(resourcePath);
        Array.Sort(files, (a, b) => string.CompareOrdinal(a.name, b.name));

        foreach (TextAsset f in files)
        {
            AddJson(f.text, prefixWithFileName ? f.name : "", overwrite, f.name, issues);
            Resources.UnloadAsset(f); // we only keep the parsed strings
        }
        return files.Length;
    }
}

/// <summary>Tiny JSON reader that keeps key order. Nested objects/arrays are flattened into dotted keys.</summary>
public static class LocalizationJson
{
    /// <summary>{ "a": { "b": "x" } } → ("a.b","x"). Arrays use their index: "list.0", "list.1".</summary>
    public static void Flatten(string json, string prefix, List<KeyValuePair<string, string>> output)
        => FlattenNode(string.IsNullOrEmpty(prefix) ? "" : prefix, Parse(json), output);

    private static void FlattenNode(string prefix, object node, List<KeyValuePair<string, string>> output)
    {
        if (node is List<KeyValuePair<string, object>> obj)
        {
            foreach (var kv in obj)
                FlattenNode(prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, kv.Value, output);
        }
        else if (node is List<object> arr)
        {
            for (int i = 0; i < arr.Count; i++)
                FlattenNode(prefix.Length == 0 ? i.ToString() : prefix + "." + i, arr[i], output);
        }
        else if (prefix.Length > 0)
        {
            output.Add(new KeyValuePair<string, string>(prefix, node as string ?? ""));
        }
    }

    public static object Parse(string text)
    {
        var p = new Parser(text);
        object v = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd) throw p.Error("Unexpected content after the JSON value");
        return v;
    }

    private sealed class Parser
    {
        private readonly string s;
        private int i;

        public Parser(string text) { s = (text ?? "").TrimStart('\uFEFF'); }
        public bool AtEnd => i >= s.Length;
        private char Peek => i < s.Length ? s[i] : '\0';

        public void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        public FormatException Error(string msg)
        {
            int line = 1, col = 1;
            for (int k = 0; k < i && k < s.Length; k++)
            {
                if (s[k] == '\n') { line++; col = 1; } else col++;
            }
            return new FormatException($"{msg} (line {line}, column {col})");
        }

        public object ParseValue()
        {
            SkipWs();
            if (AtEnd) throw Error("Unexpected end of file");

            char c = Peek;
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (Match("true")) return "true";
            if (Match("false")) return "false";
            if (Match("null")) return null;
            return ParseNumber();
        }

        private bool Match(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private List<KeyValuePair<string, object>> ParseObject()
        {
            i++; // {
            var list = new List<KeyValuePair<string, object>>();
            SkipWs();
            if (Peek == '}') { i++; return list; }

            while (true)
            {
                SkipWs();
                if (Peek == '}') { i++; return list; } // tolerate trailing comma
                if (Peek != '"') throw Error("Expected a quoted key");
                string key = ParseString();
                SkipWs();
                if (Peek != ':') throw Error("Expected ':' after key");
                i++;
                object value = ParseValue();
                list.Add(new KeyValuePair<string, object>(key, value));
                SkipWs();
                if (Peek == ',') { i++; continue; }
                if (Peek == '}') { i++; return list; }
                throw Error("Expected ',' or '}'");
            }
        }

        private List<object> ParseArray()
        {
            i++; // [
            var list = new List<object>();
            SkipWs();
            if (Peek == ']') { i++; return list; }

            while (true)
            {
                SkipWs();
                if (Peek == ']') { i++; return list; }
                list.Add(ParseValue());
                SkipWs();
                if (Peek == ',') { i++; continue; }
                if (Peek == ']') { i++; return list; }
                throw Error("Expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (AtEnd) throw Error("Bad escape sequence");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !ushort.TryParse(s.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort code))
                            throw Error("Bad \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Error("Unknown escape '\\" + e + "'");
                }
            }
        }

        private string ParseNumber()
        {
            int start = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            if (i == start) throw Error("Unexpected character '" + Peek + "'");
            return s.Substring(start, i - start);
        }
    }
}