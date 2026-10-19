using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Localization > Language Importer
/// Reads one JSON file per language, gathers every key, and REBUILDS the LanguageDatabase:
/// all existing keys are removed first, then every key from the JSON files is added again.
///
/// JSON format (one file per language, nested objects are flattened with dots):
///   { "game.title": "My Game", "menu": { "play": "Play" } }   →   keys: game.title, menu.play
/// Put this file inside an "Editor" folder.
/// </summary>
public class LanguageImporterWindow : EditorWindow
{
    private const string DbGuidPref = "LanguageImporter.DatabaseGuid";
    private const string DefaultDbPath = "Assets/Resources/LanguageDatabase.asset";

    private LanguageDatabase _db;
    private Vector2 _scroll;
    private string _report = "";
    private MessageType _reportType = MessageType.None;

    [MenuItem("Tools/Localization/Language Importer")]
    private static void Open()
    {
        var w = GetWindow<LanguageImporterWindow>("Language Importer");
        w.minSize = new Vector2(420, 320);
    }

    private void OnEnable()
    {
        string guid = EditorPrefs.GetString(DbGuidPref, "");
        if (!string.IsNullOrEmpty(guid))
            _db = AssetDatabase.LoadAssetAtPath<LanguageDatabase>(AssetDatabase.GUIDToAssetPath(guid));

        if (_db == null)
        {
            string[] found = AssetDatabase.FindAssets("t:LanguageDatabase");
            if (found.Length > 0)
                _db = AssetDatabase.LoadAssetAtPath<LanguageDatabase>(AssetDatabase.GUIDToAssetPath(found[0]));
        }
    }

    // ═════════════════════════ GUI ═════════════════════════

    private void OnGUI()
    {
        EditorGUILayout.Space(6);

        EditorGUI.BeginChangeCheck();
        _db = (LanguageDatabase)EditorGUILayout.ObjectField("Database", _db, typeof(LanguageDatabase), false);
        if (EditorGUI.EndChangeCheck() && _db != null)
            EditorPrefs.SetString(DbGuidPref, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_db)));

        if (_db == null)
        {
            EditorGUILayout.HelpBox("No LanguageDatabase selected. Create one (default languages: EN, VI).", MessageType.Info);
            if (GUILayout.Button("Create Database in Assets/Resources", GUILayout.Height(28))) CreateDatabase();
            return;
        }

        DrawSources();

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField(
            $"Database: {_db.KeyCount} keys, {_db.LanguageCount} language(s): {string.Join(", ", _db.languages)}",
            EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Auto-Find JSON by Code")) AutoFind();
        if (GUILayout.Button("Ping Database")) EditorGUIUtility.PingObject(_db);
        EditorGUILayout.EndHorizontal();

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.9f, 0.55f);
        if (GUILayout.Button("Clear All Keys & Rebuild From JSON", GUILayout.Height(34))) Rebuild();
        GUI.backgroundColor = prev;

        if (!string.IsNullOrEmpty(_report))
        {
            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox(_report, _reportType);
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawSources()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Languages  (first = default / fallback)", EditorStyles.boldLabel);

        var so = new SerializedObject(_db);
        so.Update();
        SerializedProperty list = so.FindProperty("sources");
        int removeAt = -1;

        for (int i = 0; i < list.arraySize; i++)
        {
            SerializedProperty el = list.GetArrayElementAtIndex(i);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(el.FindPropertyRelative("code"), GUIContent.none, GUILayout.Width(64));
            EditorGUILayout.PropertyField(el.FindPropertyRelative("json"), GUIContent.none);
            GUI.enabled = list.arraySize > 1;
            if (GUILayout.Button("−", GUILayout.Width(24))) removeAt = i;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        if (removeAt >= 0)
        {
            list.GetArrayElementAtIndex(removeAt).FindPropertyRelative("json").objectReferenceValue = null;
            list.DeleteArrayElementAtIndex(removeAt);
        }

        if (GUILayout.Button("+ Add Language"))
        {
            list.arraySize++;
            SerializedProperty n = list.GetArrayElementAtIndex(list.arraySize - 1);
            n.FindPropertyRelative("code").stringValue = "";
            n.FindPropertyRelative("json").objectReferenceValue = null;
        }

        so.ApplyModifiedProperties();
    }

    // ═════════════════════════ Actions ═════════════════════════

    private void CreateDatabase()
    {
        var existing = AssetDatabase.LoadAssetAtPath<LanguageDatabase>(DefaultDbPath);
        if (existing == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            existing = CreateInstance<LanguageDatabase>(); // defaults: EN, VI
            AssetDatabase.CreateAsset(existing, DefaultDbPath);
            AssetDatabase.SaveAssets();
        }
        _db = existing;
        EditorPrefs.SetString(DbGuidPref, AssetDatabase.AssetPathToGUID(DefaultDbPath));
        EditorGUIUtility.PingObject(_db);
    }

    private void AutoFind()
    {
        Undo.RecordObject(_db, "Auto-Find Language JSON");
        int assigned = 0;

        foreach (var s in _db.sources)
        {
            if (s.json != null || string.IsNullOrWhiteSpace(s.code)) continue;

            foreach (string guid in AssetDatabase.FindAssets(s.code.Trim() + " t:TextAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), s.code.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                s.json = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                assigned++;
                break;
            }
        }

        EditorUtility.SetDirty(_db);
        _report = assigned > 0 ? $"Assigned {assigned} JSON file(s) by name (e.g. en.json, vi.json)." : "No new JSON files matched a language code.";
        _reportType = assigned > 0 ? MessageType.Info : MessageType.Warning;
    }

    private void Rebuild()
    {
        var log = new StringBuilder();
        bool parseError = false;

        var codes = new List<string>();
        var maps = new List<Dictionary<string, string>>();
        var order = new List<string>();      // union of keys, first-seen order
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in _db.sources)
        {
            string code = (s.code ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0) { log.AppendLine("• Skipped a row with an empty language code."); continue; }
            if (codes.Contains(code)) { log.AppendLine($"• Duplicate language code '{code}' skipped."); continue; }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (s.json == null)
            {
                log.AppendLine($"• [{code}] no JSON assigned: this language will be empty.");
            }
            else
            {
                try
                {
                    var flat = new List<KeyValuePair<string, string>>();
                    Flatten("", MiniJson.Parse(s.json.text), flat);

                    foreach (var kv in flat)
                    {
                        if (map.ContainsKey(kv.Key)) log.AppendLine($"• [{code}] duplicate key '{kv.Key}' (last one wins).");
                        map[kv.Key] = kv.Value;
                        if (seen.Add(kv.Key)) order.Add(kv.Key);
                    }
                }
                catch (Exception ex)
                {
                    parseError = true;
                    log.AppendLine($"✖ [{code}] '{s.json.name}': {ex.Message}");
                }
            }

            codes.Add(code);
            maps.Add(map);
        }

        if (codes.Count == 0)
        {
            Report("Nothing to import: add at least one language.\n" + log, MessageType.Error);
            return;
        }

        if (parseError)
        {
            Report("Aborted: fix the JSON error(s) first. The database was NOT changed.\n\n" + log, MessageType.Error);
            return;
        }

        // ── remove all keys, then add everything again ──
        Undo.RecordObject(_db, "Rebuild Language Database");
        _db.languages = new List<string>(codes);
        _db.entries = new List<LanguageDatabase.Entry>(order.Count);

        var missingCount = new int[codes.Count];
        var missingKeys = new List<string>[codes.Count];
        for (int l = 0; l < codes.Count; l++) missingKeys[l] = new List<string>();

        foreach (string key in order)
        {
            var entry = new LanguageDatabase.Entry { key = key, values = new string[codes.Count] };
            for (int l = 0; l < codes.Count; l++)
            {
                if (maps[l].TryGetValue(key, out string v) && !string.IsNullOrEmpty(v)) entry.values[l] = v;
                else
                {
                    entry.values[l] = "";
                    missingCount[l]++;
                    if (missingKeys[l].Count < 8) missingKeys[l].Add(key);
                }
            }
            _db.entries.Add(entry);
        }

        _db.InvalidateLookup();
        EditorUtility.SetDirty(_db);
        AssetDatabase.SaveAssets();

        var sb = new StringBuilder();
        sb.AppendLine($"Rebuilt: {order.Count} keys, {codes.Count} language(s) [{string.Join(", ", codes)}].");
        bool anyMissing = false;
        for (int l = 0; l < codes.Count; l++)
        {
            if (missingCount[l] == 0) continue;
            anyMissing = true;
            sb.AppendLine($"• [{codes[l]}] {missingCount[l]} key(s) missing, e.g. {string.Join(", ", missingKeys[l])}" +
                          (missingCount[l] > missingKeys[l].Count ? ", ..." : ""));
        }
        if (log.Length > 0) { sb.AppendLine(); sb.Append(log); }

        Report(sb.ToString().TrimEnd(), anyMissing || log.Length > 0 ? MessageType.Warning : MessageType.Info);
        Debug.Log($"[LanguageImporter] {sb}");
    }

    private void Report(string text, MessageType type)
    {
        _report = text;
        _reportType = type;
        Repaint();
    }

    // ═════════════════════════ JSON → flat keys ═════════════════════════

    private static void Flatten(string prefix, object node, List<KeyValuePair<string, string>> output)
    {
        if (node is List<KeyValuePair<string, object>> obj)
        {
            foreach (var kv in obj)
                Flatten(prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, kv.Value, output);
        }
        else if (node is List<object> arr)
        {
            for (int i = 0; i < arr.Count; i++)
                Flatten(prefix.Length == 0 ? i.ToString() : prefix + "." + i, arr[i], output);
        }
        else if (prefix.Length > 0)
        {
            output.Add(new KeyValuePair<string, string>(prefix, node as string ?? ""));
        }
    }

    /// <summary>Tiny JSON parser (keeps key order). Objects → List of pairs, arrays → List of object, everything else → string.</summary>
    private static class MiniJson
    {
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
                    if (Peek == ']') { i++; return list; } // tolerate trailing comma
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
}