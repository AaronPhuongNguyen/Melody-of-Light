using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CONFIG ONLY. Holds no translations (they are loaded per language at runtime by Translator).
///
/// Folder layout (inside any Resources folder):
///   Resources/Localization/VI/*.json
///   Resources/Localization/EN/*.json      (sub-folders are scanned too)
///
/// Keep the asset at Assets/Resources/LanguageDatabase.asset so Translator finds it automatically.
/// The asset is optional: Translator.Initialize(...) can configure everything from code.
/// </summary>
[CreateAssetMenu(fileName = "LanguageDatabase", menuName = "Database/Localization")]
public class LanguageDatabase : ScriptableObject
{
    [Tooltip("Resources sub-path that contains one folder per language.")]
    public string resourcesRoot = "Localization";

    [Tooltip("ON: key = fileName + '.' + key  (common.json {\"ok\":\"OK\"} → 'common.ok').\nOFF: keys are used exactly as written in the files.")]
    public bool prefixKeysWithFileName = false;

    [Tooltip("Used on first run when the saved / system language is not available.")]
    public string defaultLanguage = "VI";

    [Tooltip("Used when the current language has no value for a key. Empty = no fallback.")]
    public string fallbackLanguage = "VI";

    public List<LanguageInfo> languages = new List<LanguageInfo>
    {
        new LanguageInfo("VI", "Tiếng Việt"),
        new LanguageInfo("EN", "English")
    };

    public int LanguageCount => languages.Count;

    public int IndexOf(string code)
    {
        if (string.IsNullOrEmpty(code)) return -1;
        for (int i = 0; i < languages.Count; i++)
            if (languages[i] != null && string.Equals(languages[i].code, code, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public string GetResourcePath(LanguageInfo info)
        => (resourcesRoot ?? "").Trim().Trim('/') + "/" + info.FolderName;
}


/// <summary>One supported language. Its JSON files live in Resources/{resourcesRoot}/{folder or code}/.</summary>
[Serializable]
public class LanguageInfo
{
    public string code;         // "VI", "EN" ...
    public string displayName;  // "Tiếng Việt", "English" (for language buttons)
    public string folder;       // optional: folder name under the root. Empty = code.

    public string FolderName => string.IsNullOrWhiteSpace(folder) ? (code ?? "").Trim() : folder.Trim();
    public string DisplayNameOrCode => string.IsNullOrWhiteSpace(displayName) ? code : displayName;

    public LanguageInfo() { }
    public LanguageInfo(string code, string displayName = null, string folder = null)
    {
        this.code = code; this.displayName = displayName; this.folder = folder;
    }
}