using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Localization > Language Manager   (put this file inside an "Editor" folder; replaces LanguageImporterWindow)
/// There is nothing to import any more: JSON files are read straight from Resources/{root}/{language}/.
/// This window edits the config, creates the language folders and validates all languages against each other.
/// </summary>
public class LocalizationToolWindow : EditorWindow
{
    private const string DbGuidPref = "LanguageManager.DatabaseGuid";
    private const string DefaultDbPath = "Assets/Resources/LanguageDatabase.asset";

    private LanguageDatabase _db;
    private Vector2 _scroll;
    private string _report = "";
    private MessageType _reportType = MessageType.None;

    [MenuItem("Tools/Localization/Language Manager")]
    private static void Open()
    {
        var w = GetWindow<LocalizationToolWindow>("Language Manager");
        w.minSize = new Vector2(420, 360);
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
            EditorGUILayout.HelpBox("No LanguageDatabase selected. Create one (default languages: VI, EN).", MessageType.Info);
            if (GUILayout.Button("Create Database in Assets/Resources", GUILayout.Height(28))) CreateDatabase();
            return;
        }

        DrawConfig();

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Create Language Folders", GUILayout.Height(26))) CreateFolders();
        if (GUILayout.Button("Ping Database", GUILayout.Height(26))) EditorGUIUtility.PingObject(_db);
        EditorGUILayout.EndHorizontal();

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.9f, 0.55f);
        if (GUILayout.Button("Validate All Languages", GUILayout.Height(34))) Validate();
        GUI.backgroundColor = prev;

        if (!string.IsNullOrEmpty(_report))
        {
            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox(_report, _reportType);
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawConfig()
    {
        var so = new SerializedObject(_db);
        so.Update();

        EditorGUILayout.Space(4);
        EditorGUILayout.PropertyField(so.FindProperty("resourcesRoot"));
        EditorGUILayout.PropertyField(so.FindProperty("prefixKeysWithFileName"));
        EditorGUILayout.PropertyField(so.FindProperty("defaultLanguage"));
        EditorGUILayout.PropertyField(so.FindProperty("fallbackLanguage"));

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Languages   (code | display name | folder, empty = code)", EditorStyles.boldLabel);

        SerializedProperty list = so.FindProperty("languages");
        int removeAt = -1;
        for (int i = 0; i < list.arraySize; i++)
        {
            SerializedProperty el = list.GetArrayElementAtIndex(i);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(el.FindPropertyRelative("code"), GUIContent.none, GUILayout.Width(56));
            EditorGUILayout.PropertyField(el.FindPropertyRelative("displayName"), GUIContent.none);
            EditorGUILayout.PropertyField(el.FindPropertyRelative("folder"), GUIContent.none, GUILayout.Width(90));
            GUI.enabled = list.arraySize > 1;
            if (GUILayout.Button("−", GUILayout.Width(24))) removeAt = i;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }
        if (removeAt >= 0) list.DeleteArrayElementAtIndex(removeAt);

        if (GUILayout.Button("+ Add Language"))
        {
            list.arraySize++;
            SerializedProperty n = list.GetArrayElementAtIndex(list.arraySize - 1);
            n.FindPropertyRelative("code").stringValue = "";
            n.FindPropertyRelative("displayName").stringValue = "";
            n.FindPropertyRelative("folder").stringValue = "";
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
            existing = CreateInstance<LanguageDatabase>();
            AssetDatabase.CreateAsset(existing, DefaultDbPath);
            AssetDatabase.SaveAssets();
        }
        _db = existing;
        EditorPrefs.SetString(DbGuidPref, AssetDatabase.AssetPathToGUID(DefaultDbPath));
        EditorGUIUtility.PingObject(_db);
    }

    private void CreateFolders()
    {
        int created = 0;
        foreach (var info in _db.languages)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.code)) continue;

            string dir = Path.Combine(Application.dataPath, "Resources", _db.GetResourcePath(info));
            Directory.CreateDirectory(dir);

            string starter = Path.Combine(dir, "common.json");
            if (File.Exists(starter)) continue;
            File.WriteAllText(starter, "{\n}\n", new UTF8Encoding(false));
            created++;
        }

        AssetDatabase.Refresh();
        Report(created > 0 ? $"Created {created} starter file(s) (common.json)." : "All language folders already have files.",
               MessageType.Info);
    }

    private void Validate()
    {
        var issues = new List<string>();
        var codes = new List<string>();
        var tables = new List<LanguageTable>();
        var union = new HashSet<string>();
        var order = new List<string>();

        foreach (var info in _db.languages)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.code)) { issues.Add("• Skipped a row with an empty language code."); continue; }
            if (codes.Contains(info.code.Trim())) { issues.Add($"• Duplicate language code '{info.code}' skipped."); continue; }

            var table = new LanguageTable(info.code.Trim());
            string path = _db.GetResourcePath(info);
            int files = table.LoadFolder(path, _db.prefixKeysWithFileName, true, issues);
            if (files == 0) issues.Add($"✖ [{table.Code}] no JSON files in Resources/{path}");

            foreach (string k in table.Keys) if (union.Add(k)) order.Add(k);
            codes.Add(table.Code);
            tables.Add(table);
        }

        if (tables.Count == 0) { Report("Nothing to validate: add at least one language.", MessageType.Error); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"{union.Count} distinct key(s) across {tables.Count} language(s).");

        bool problems = issues.Count > 0;
        foreach (var t in tables)
        {
            var missing = new List<string>();
            int missingCount = 0, emptyCount = 0;
            foreach (string k in order)
            {
                if (!t.TryGet(k, out string v)) { missingCount++; if (missing.Count < 8) missing.Add(k); }
                else if (string.IsNullOrEmpty(v)) emptyCount++;
            }

            sb.AppendLine($"• [{t.Code}] {t.Count} key(s)" +
                          (missingCount > 0 ? $", {missingCount} missing e.g. {string.Join(", ", missing)}{(missingCount > missing.Count ? ", ..." : "")}" : "") +
                          (emptyCount > 0 ? $", {emptyCount} empty value(s)" : ""));
            if (missingCount > 0 || emptyCount > 0) problems = true;
        }

        if (issues.Count > 0) { sb.AppendLine(); foreach (string s in issues) sb.AppendLine(s); }

        Report(sb.ToString().TrimEnd(), problems ? MessageType.Warning : MessageType.Info);
        Debug.Log("[LanguageManager] " + sb);
    }

    private void Report(string text, MessageType type)
    {
        _report = text;
        _reportType = type;
        Repaint();
    }
}