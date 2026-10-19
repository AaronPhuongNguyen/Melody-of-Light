using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stores every translation: one Entry per key, with one value per language (aligned with 'languages').
/// Filled by the Language Importer tool (Tools > Localization > Language Importer). Read by Translator.
/// Keep the asset at Assets/Resources/LanguageDatabase.asset so Translator can find it automatically.
/// </summary>
[CreateAssetMenu(fileName = "LanguageDatabase", menuName = "Database/Localization")]
public class LanguageDatabase : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string key;
        public string[] values = Array.Empty<string>(); // index = language index
    }

    [Tooltip("Language codes. The first one is the default / fallback language.")]
    public List<string> languages = new List<string> { "EN", "VI" };
    public List<Entry> entries = new List<Entry>();

#if UNITY_EDITOR
    // Editor-only: JSON source per language, remembered by the importer tool.
    // Not compiled into builds, so the TextAssets are not shipped.
    [Serializable]
    public class Source
    {
        public string code;
        public TextAsset json;
    }

    public List<Source> sources = new List<Source>
    {
        new Source { code = "EN" },
        new Source { code = "VI" }
    };
#endif

    private Dictionary<string, Entry> _lookup;

    public int LanguageCount => languages.Count;
    public int KeyCount => entries.Count;

    public int IndexOf(string code)
    {
        if (string.IsNullOrEmpty(code)) return -1;
        for (int i = 0; i < languages.Count; i++)
            if (string.Equals(languages[i], code, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public bool TryGetEntry(string key, out Entry entry)
    {
        if (_lookup == null) BuildLookup();
        if (string.IsNullOrEmpty(key)) { entry = null; return false; }
        return _lookup.TryGetValue(key, out entry);
    }

    /// <summary>Returns the raw value or null if the key / language does not exist.</summary>
    public string GetValue(string key, int languageIndex)
    {
        if (!TryGetEntry(key, out Entry e)) return null;
        if (languageIndex < 0 || languageIndex >= e.values.Length) return null;
        return e.values[languageIndex];
    }

    /// <summary>Call after changing 'entries' by code.</summary>
    public void InvalidateLookup() => _lookup = null;

    private void BuildLookup()
    {
        _lookup = new Dictionary<string, Entry>(entries.Count, StringComparer.Ordinal);
        foreach (Entry e in entries)
            if (e != null && !string.IsNullOrEmpty(e.key)) _lookup[e.key] = e;
    }

    private void OnEnable() => _lookup = null;
    private void OnValidate() => _lookup = null;
}