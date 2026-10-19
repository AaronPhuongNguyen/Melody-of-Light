using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static translator, callable from anywhere:
///     Translator.Get("menu.play");                 // "Play" / "Chơi"
///     Translator.Get("hud.score", 120);            // string.Format: "Score: 120"
///     "menu.play".Tr();                            // extension shortcut
///     Translator.SetLanguage("VI");                // switch (saved in PlayerPrefs, fires OnLanguageChanged)
///
/// Database: loaded automatically from Resources/LanguageDatabase.asset, or call Translator.SetDatabase(db).
/// First run language: saved choice → system language (if available) → first language in the database.
/// Missing translation falls back to FallbackLanguage, then to the key itself (see MissingKeyFormat).
/// </summary>
public static class Translator
{
    public const string ResourceName = "LanguageDatabase";
    private const string PrefKey = "Translator.Language";

    /// <summary>Fired after the language changed. Argument = new language code.</summary>
    public static event Action<string> OnLanguageChanged;

    /// <summary>What is returned when a key is missing. {0} = the key, e.g. "[{0}]" to spot gaps in UI.</summary>
    public static string MissingKeyFormat = "{0}";

    /// <summary>Warn once per missing key in the console.</summary>
    public static bool LogMissingKeys = true;

    /// <summary>Used when the current language has no value for a key. Null/unknown = first language.</summary>
    public static string FallbackLanguage = "EN";

    private static LanguageDatabase _db;
    private static int _index = -1;
    private static bool _initialized;
    private static readonly HashSet<string> _warned = new HashSet<string>();
    private static readonly List<string> _noLanguages = new List<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // supports "Enter Play Mode without domain reload"
        _db = null;
        _index = -1;
        _initialized = false;
        _warned.Clear();
        OnLanguageChanged = null;
    }

    // ═════════════════════════ Setup ═════════════════════════

    public static bool IsReady { get { EnsureInit(); return _db != null && _index >= 0; } }

    public static LanguageDatabase Database { get { EnsureInit(); return _db; } }

    /// <summary>Use a database that is not in Resources.</summary>
    public static void SetDatabase(LanguageDatabase db)
    {
        _db = db;
        _initialized = true;
        _warned.Clear();
        ChooseInitialLanguage();
    }

    /// <summary>Reload the database from Resources (and its lookup cache).</summary>
    public static void Reload()
    {
        _initialized = false;
        _db = null;
        EnsureInit();
    }

    private static void EnsureInit()
    {
        if (_initialized) return;
        _initialized = true;

        _db = Resources.Load<LanguageDatabase>(ResourceName);
        if (_db == null)
            Debug.LogWarning($"[Translator] No LanguageDatabase found at Resources/{ResourceName}. " +
                             "Create one with Tools > Localization > Language Importer, or call Translator.SetDatabase().");
        ChooseInitialLanguage();
    }

    private static void ChooseInitialLanguage()
    {
        _index = -1;
        if (_db == null || _db.LanguageCount == 0) return;

        _index = _db.IndexOf(PlayerPrefs.GetString(PrefKey, ""));
        if (_index < 0) _index = _db.IndexOf(FromSystemLanguage(Application.systemLanguage));
        if (_index < 0) _index = 0;
    }

    // ═════════════════════════ Language ═════════════════════════

    public static IReadOnlyList<string> Languages { get { EnsureInit(); return _db != null ? _db.languages : _noLanguages; } }
    public static int LanguageCount { get { EnsureInit(); return _db != null ? _db.LanguageCount : 0; } }
    public static int CurrentIndex { get { EnsureInit(); return _index; } }

    public static string CurrentLanguage
    {
        get
        {
            EnsureInit();
            return _db != null && _index >= 0 && _index < _db.LanguageCount ? _db.languages[_index] : "";
        }
    }

    public static bool SetLanguage(string code, bool save = true)
    {
        EnsureInit();
        if (_db == null) return false;

        int idx = _db.IndexOf(code);
        if (idx < 0)
        {
            Debug.LogWarning($"[Translator] Language '{code}' is not in the database.");
            return false;
        }
        return ApplyLanguage(idx, save);
    }

    public static bool SetLanguage(int index, bool save = true)
    {
        EnsureInit();
        if (_db == null || index < 0 || index >= _db.LanguageCount) return false;
        return ApplyLanguage(index, save);
    }

    /// <summary>Cycle to the next language (wraps around). Handy for a language button.</summary>
    public static void NextLanguage(bool save = true)
    {
        EnsureInit();
        if (_db == null || _db.LanguageCount == 0) return;
        ApplyLanguage((_index + 1) % _db.LanguageCount, save);
    }

    private static bool ApplyLanguage(int idx, bool save)
    {
        if (idx == _index) return true;
        _index = idx;
        string code = _db.languages[idx];
        if (save) PlayerPrefs.SetString(PrefKey, code);
        OnLanguageChanged?.Invoke(code);
        return true;
    }

    // ═════════════════════════ Translate ═════════════════════════

    /// <summary>Translate a key into the current language.</summary>
    public static string Get(string key)
    {
        EnsureInit();
        if (string.IsNullOrEmpty(key)) return "";
        if (_db != null && TryLookup(key, _index, out string value)) return value;
        return Missing(key);
    }

    /// <summary>Translate and string.Format with args: Get("hud.score", 120) → "Score: 120".</summary>
    public static string Get(string key, params object[] args)
    {
        string text = Get(key);
        if (args == null || args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException)
        {
            Debug.LogWarning($"[Translator] Bad format string for key '{key}': \"{text}\"");
            return text;
        }
    }

    /// <summary>Translate a key into a specific language (ignores the current language).</summary>
    public static string GetIn(string key, string languageCode)
    {
        EnsureInit();
        if (string.IsNullOrEmpty(key)) return "";
        if (_db != null && TryLookup(key, _db.IndexOf(languageCode), out string value)) return value;
        return Missing(key);
    }

    /// <summary>True when the key has a usable value (current language or fallback).</summary>
    public static bool TryGet(string key, out string value)
    {
        EnsureInit();
        value = null;
        return _db != null && TryLookup(key, _index, out value);
    }

    public static bool HasKey(string key)
    {
        EnsureInit();
        return _db != null && _db.TryGetEntry(key, out _);
    }

    public static IEnumerable<string> AllKeys()
    {
        EnsureInit();
        if (_db == null) yield break;
        foreach (var e in _db.entries) yield return e.key;
    }

    // Extension shortcuts: "menu.play".Tr()
    public static string Tr(this string key) => Get(key);
    public static string Tr(this string key, params object[] args) => Get(key, args);

    // ═════════════════════════ Internals ═════════════════════════

    private static bool TryLookup(string key, int langIndex, out string value)
    {
        value = null;
        if (!_db.TryGetEntry(key, out LanguageDatabase.Entry entry)) return false;

        value = ValueAt(entry, langIndex);
        if (!string.IsNullOrEmpty(value)) return true;

        int fb = _db.IndexOf(FallbackLanguage);
        if (fb < 0) fb = 0;
        value = ValueAt(entry, fb);
        return !string.IsNullOrEmpty(value);
    }

    private static string ValueAt(LanguageDatabase.Entry e, int i)
        => i >= 0 && i < e.values.Length ? e.values[i] : null;

    private static string Missing(string key)
    {
        if (LogMissingKeys && _warned.Add(key))
            Debug.LogWarning($"[Translator] Missing translation for key '{key}' (language: {CurrentLanguage}).");
        return MissingKeyFormat.Replace("{0}", key);
    }

    private static string FromSystemLanguage(SystemLanguage lang)
    {
        switch (lang)
        {
            case SystemLanguage.Vietnamese: return "VI";
            case SystemLanguage.English: return "EN";
            case SystemLanguage.French: return "FR";
            case SystemLanguage.German: return "DE";
            case SystemLanguage.Spanish: return "ES";
            case SystemLanguage.Portuguese: return "PT";
            case SystemLanguage.Italian: return "IT";
            case SystemLanguage.Russian: return "RU";
            case SystemLanguage.Japanese: return "JA";
            case SystemLanguage.Korean: return "KO";
            case SystemLanguage.Chinese:
            case SystemLanguage.ChineseSimplified: return "ZH";
            case SystemLanguage.ChineseTraditional: return "ZH-TW";
            case SystemLanguage.Thai: return "TH";
            case SystemLanguage.Indonesian: return "ID";
            default: return "";
        }
    }
}