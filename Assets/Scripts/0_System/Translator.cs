using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static translator, callable from anywhere. Only ONE language is kept in memory:
/// switching language clears the table and loads every JSON file of the new language's folder.
///
///   Resources/Localization/VI/*.json      Resources/Localization/EN/*.json
///
///   Translator.Get("menu.play");                       // "Play" / "Chơi"
///   Translator.Get("hud.score", 120);                  // string.Format → "Score: 120"
///   Translator.GetNamed("hud.hello", "name", "Ann");   // "Hello {name}" → "Hello Ann"
///   Translator.GetOr("maybe.key", "default text");
///   "menu.play".Tr();   "hud.score".Tr(120);   "x".TrOr("fallback");
///   Translator.SetLanguage("VI");  NextLanguage();  PreviousLanguage();
///
/// Dynamic:
///   Translator.Initialize("Localization", "VI", "EN", "JA");   // no asset needed
///   Translator.AddLanguage("FR", "Français");                  // register at runtime
///   Translator.LoadExtra("DLC/{lang}");                        // extra folder, re-loaded on every switch
///   Translator.AddFromJson(json); Set("k","v"); Remove("k");   // edit the current table
///   Translator.GetIn("menu.play", "EN");                       // peek another language (1-slot cache)
///
/// Config: Resources/LanguageDatabase.asset (optional). First run: saved choice → system language → default.
/// </summary>
public static class Translator
{
    public const string ResourceName = "LanguageDatabase";
    private const string PrefKey = "Translator.Language";

    // ───────────── events ─────────────
    /// <summary>Before the table is swapped. Args: old code, new code.</summary>
    public static event Action<string, string> OnLanguageChanging;
    /// <summary>After the new language is loaded. Arg: new code.</summary>
    public static event Action<string> OnLanguageChanged;
    /// <summary>Any change of content: language switch, Refresh, AddFromJson, Set, Remove, LoadExtra...</summary>
    public static event Action OnTranslationsChanged;

    // ───────────── settings (initialised from the database, can be changed from code, then call Refresh()) ─────────────
    /// <summary>What is returned when a key is missing. {0} = the key, e.g. "[{0}]" to spot gaps in the UI.</summary>
    public static string MissingKeyFormat = "{0}";
    public static bool LogMissingKeys = true;
    public static bool LogLoadIssues = true;
    public static string ResourcesRoot = "Localization";
    public static bool PrefixKeysWithFileName = false;
    public static string DefaultLanguage = "VI";

    /// <summary>Language used when the current one has no value. Empty = none. It is loaded lazily on the first miss.</summary>
    public static string FallbackLanguage
    {
        get => _fallbackCode;
        set { _fallbackCode = value ?? ""; _fallbackTable = null; }
    }

    // ───────────── state ─────────────
    private static readonly List<LanguageInfo> _languages = new List<LanguageInfo>();
    private static readonly List<string> _extras = new List<string>();
    private static readonly HashSet<string> _warned = new HashSet<string>();
    private static List<string> _codes;
    private static LanguageTable _table;
    private static LanguageTable _fallbackTable;
    private static LanguageTable _peek;
    private static string _fallbackCode = "VI";
    private static int _index = -1;
    private static bool _initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // supports "Enter Play Mode without domain reload"
        _languages.Clear(); _extras.Clear(); _warned.Clear();
        _codes = null; _table = null; _fallbackTable = null; _peek = null;
        _index = -1; _initialized = false;
        OnLanguageChanging = null; OnLanguageChanged = null; OnTranslationsChanged = null;
    }

    // ═════════════════════════ Setup ═════════════════════════

    public static bool IsReady { get { EnsureInit(); return _table != null && _index >= 0; } }

    /// <summary>Configure from code (no asset needed). Loads the initial language.</summary>
    public static void Initialize(string resourcesRoot, params string[] languageCodes)
    {
        _initialized = true;
        ResourcesRoot = string.IsNullOrWhiteSpace(resourcesRoot) ? "Localization" : resourcesRoot;
        _languages.Clear(); _codes = null;
        foreach (string c in languageCodes ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(c)) _languages.Add(new LanguageInfo(c.Trim()));
        if (_languages.Count > 0 && IndexOf(DefaultLanguage) < 0) DefaultLanguage = _languages[0].code;
        ChooseInitialLanguage();
    }

    /// <summary>Use a database that is not in Resources (or a different one).</summary>
    public static void Configure(LanguageDatabase db, string preferredLanguage = null)
    {
        _initialized = true;
        ApplyConfig(db, preferredLanguage);
    }

    /// <summary>Re-read the database config and re-load the current language from disk.</summary>
    public static void Reload()
    {
        string keep = CurrentLanguage;
        _initialized = false;
        _languages.Clear(); _codes = null;
        _table = _fallbackTable = _peek = null; _index = -1;
        _initialized = true;
        ApplyConfig(Resources.Load<LanguageDatabase>(ResourceName), keep);
    }

    /// <summary>Rebuild the current table from its files (+ extras). Runtime edits (Set/AddFromJson) are discarded.</summary>
    public static void Refresh()
    {
        EnsureInit();
        _fallbackTable = null; _peek = null;
        if (_index >= 0) _table = BuildTable(_languages[_index]);
        OnTranslationsChanged?.Invoke();
    }

    private static void EnsureInit()
    {
        if (_initialized) return;
        _initialized = true;
        ApplyConfig(Resources.Load<LanguageDatabase>(ResourceName), null);
    }

    private static void ApplyConfig(LanguageDatabase db, string preferred)
    {
        _languages.Clear(); _codes = null;

        if (db != null)
        {
            ResourcesRoot = db.resourcesRoot;
            PrefixKeysWithFileName = db.prefixKeysWithFileName;
            DefaultLanguage = db.defaultLanguage;
            _fallbackCode = db.fallbackLanguage ?? "";
            foreach (var l in db.languages)
                if (l != null && !string.IsNullOrWhiteSpace(l.code))
                    _languages.Add(new LanguageInfo(l.code.Trim(), l.displayName, l.folder)); // copy: runtime edits never touch the asset
        }
        else
        {
            Debug.LogWarning($"[Translator] No LanguageDatabase at Resources/{ResourceName}. Using defaults (VI, EN, Resources/{ResourcesRoot}). " +
                             "Create one with Tools > Localization > Language Manager, or call Translator.Initialize().");
            _languages.Add(new LanguageInfo("VI", "Tiếng Việt"));
            _languages.Add(new LanguageInfo("EN", "English"));
        }

        ChooseInitialLanguage(preferred);
    }

    private static void ChooseInitialLanguage(string preferred = null)
    {
        _table = _fallbackTable = _peek = null;
        _index = -1;
        if (_languages.Count == 0) return;

        int idx = IndexOf(preferred);
        if (idx < 0) idx = IndexOf(PlayerPrefs.GetString(PrefKey, ""));
        if (idx < 0) idx = IndexOfSystemLanguage(Application.systemLanguage);
        if (idx < 0) idx = IndexOf(DefaultLanguage);
        if (idx < 0) idx = 0;

        _index = idx;
        _table = BuildTable(_languages[idx]);
    }

    // ═════════════════════════ Languages ═════════════════════════

    public static IReadOnlyList<LanguageInfo> LanguageInfos { get { EnsureInit(); return _languages; } }

    public static IReadOnlyList<string> Languages
    {
        get
        {
            EnsureInit();
            if (_codes == null) { _codes = new List<string>(_languages.Count); foreach (var l in _languages) _codes.Add(l.code); }
            return _codes;
        }
    }

    public static int LanguageCount { get { EnsureInit(); return _languages.Count; } }
    public static int CurrentIndex { get { EnsureInit(); return _index; } }

    public static string CurrentLanguage
    {
        get { EnsureInit(); return _index >= 0 && _index < _languages.Count ? _languages[_index].code : ""; }
    }

    public static string CurrentDisplayName
    {
        get { EnsureInit(); return _index >= 0 && _index < _languages.Count ? _languages[_index].DisplayNameOrCode : ""; }
    }

    /// <summary>Number of keys in the current language.</summary>
    public static int KeyCount { get { EnsureInit(); return _table != null ? _table.Count : 0; } }

    public static int IndexOf(string code)
    {
        if (string.IsNullOrEmpty(code)) return -1;
        for (int i = 0; i < _languages.Count; i++)
            if (string.Equals(_languages[i].code, code, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static bool HasLanguage(string code) { EnsureInit(); return IndexOf(code) >= 0; }

    public static string GetDisplayName(string code)
    {
        EnsureInit();
        int i = IndexOf(code);
        return i >= 0 ? _languages[i].DisplayNameOrCode : code;
    }

    /// <summary>Register a language at runtime (its files must exist in Resources/{root}/{folder or code}).</summary>
    public static bool AddLanguage(string code, string displayName = null, string folder = null)
    {
        EnsureInit();
        if (string.IsNullOrWhiteSpace(code) || IndexOf(code) >= 0) return false;
        _languages.Add(new LanguageInfo(code.Trim(), displayName, folder));
        _codes = null;
        if (_index < 0) ChooseInitialLanguage();
        return true;
    }

    /// <summary>Unregister a language. The current one is replaced by another first. The last language can't be removed.</summary>
    public static bool RemoveLanguage(string code)
    {
        EnsureInit();
        int idx = IndexOf(code);
        if (idx < 0 || _languages.Count <= 1) return false;

        if (idx == _index)
        {
            int alt = IndexOf(DefaultLanguage);
            if (alt < 0 || alt == idx) alt = idx == 0 ? 1 : 0;
            ApplyLanguage(alt, false);
        }
        _languages.RemoveAt(idx);
        if (idx < _index) _index--;
        _codes = null;
        return true;
    }

    public static bool SetLanguage(string code, bool save = true)
    {
        EnsureInit();
        int idx = IndexOf(code);
        if (idx < 0)
        {
            Debug.LogWarning($"[Translator] Language '{code}' is not registered.");
            return false;
        }
        return ApplyLanguage(idx, save);
    }

    public static bool SetLanguage(int index, bool save = true)
    {
        EnsureInit();
        if (index < 0 || index >= _languages.Count) return false;
        return ApplyLanguage(index, save);
    }

    /// <summary>Use the device language if registered; returns false otherwise.</summary>
    public static bool SetSystemLanguage(bool save = false)
    {
        EnsureInit();
        int idx = IndexOfSystemLanguage(Application.systemLanguage);
        return idx >= 0 && ApplyLanguage(idx, save);
    }

    /// <summary>Cycle to the next language (wraps around). Handy for a language button.</summary>
    public static void NextLanguage(bool save = true)
    {
        EnsureInit();
        if (_languages.Count == 0) return;
        ApplyLanguage(((_index < 0 ? -1 : _index) + 1) % _languages.Count, save);
    }

    public static void PreviousLanguage(bool save = true)
    {
        EnsureInit();
        if (_languages.Count == 0) return;
        ApplyLanguage(((_index < 0 ? 0 : _index) - 1 + _languages.Count) % _languages.Count, save);
    }

    private static bool ApplyLanguage(int idx, bool save)
    {
        if (idx == _index && _table != null) return true;

        string oldCode = CurrentLanguage;
        string newCode = _languages[idx].code;
        OnLanguageChanging?.Invoke(oldCode, newCode);

        _index = idx;
        _table = BuildTable(_languages[idx]);   // old keys are dropped, new files loaded
        _fallbackTable = null;
        _peek = null;
        _warned.Clear();

        if (save) { PlayerPrefs.SetString(PrefKey, newCode); }
        OnLanguageChanged?.Invoke(newCode);
        OnTranslationsChanged?.Invoke();
        return true;
    }

    // ═════════════════════════ Translate ═════════════════════════

    /// <summary>Translate a key into the current language.</summary>
    public static string Get(string key)
    {
        EnsureInit();
        if (string.IsNullOrEmpty(key)) return "";
        return Resolve(key, out string value) ? value : Missing(key);
    }

    /// <summary>Translate and string.Format with args: Get("hud.score", 120) → "Score: 120".</summary>
    public static string Get(string key, params object[] args)
    {
        string text = Get(key);
        return Format(key, text, args);
    }

    /// <summary>Like Get, but returns 'defaultText' (no warning) when the key is missing.</summary>
    public static string GetOr(string key, string defaultText)
    {
        EnsureInit();
        return !string.IsNullOrEmpty(key) && Resolve(key, out string value) ? value : defaultText;
    }

    /// <summary>Named placeholders: "Hello {name}" + {"name":"Ann"} → "Hello Ann".</summary>
    public static string GetNamed(string key, IDictionary<string, object> values)
    {
        string text = Get(key);
        if (values == null) return text;
        foreach (var kv in values) text = text.Replace("{" + kv.Key + "}", kv.Value?.ToString() ?? "");
        return text;
    }

    public static string GetNamed(string key, string name, object value)
        => Get(key).Replace("{" + name + "}", value?.ToString() ?? "");

    /// <summary>Translate into a specific language (ignores the current one). Loads that language temporarily (1-slot cache).</summary>
    public static string GetIn(string key, string languageCode)
    {
        EnsureInit();
        if (string.IsNullOrEmpty(key)) return "";
        if (string.Equals(languageCode, CurrentLanguage, StringComparison.OrdinalIgnoreCase)) return Get(key);

        int idx = IndexOf(languageCode);
        if (idx < 0) return Missing(key);

        if (_peek == null || !string.Equals(_peek.Code, _languages[idx].code, StringComparison.OrdinalIgnoreCase))
            _peek = BuildTable(_languages[idx]);

        return _peek.TryGet(key, out string v) && !string.IsNullOrEmpty(v) ? v : Missing(key);
    }

    public static string GetIn(string key, string languageCode, params object[] args)
        => Format(key, GetIn(key, languageCode), args);

    /// <summary>True when the key has a usable value (current language or fallback).</summary>
    public static bool TryGet(string key, out string value)
    {
        EnsureInit();
        value = null;
        return !string.IsNullOrEmpty(key) && Resolve(key, out value);
    }

    /// <summary>True when the key exists in the current language table (fallback ignored).</summary>
    public static bool HasKey(string key) { EnsureInit(); return _table != null && _table.Contains(key); }

    public static IEnumerable<string> AllKeys()
    {
        EnsureInit();
        if (_table == null) yield break;
        foreach (string k in _table.Keys) yield return k;
    }

    public static IEnumerable<string> KeysWithPrefix(string prefix)
    {
        foreach (string k in AllKeys())
            if (string.IsNullOrEmpty(prefix) || k.StartsWith(prefix, StringComparison.Ordinal)) yield return k;
    }

    /// <summary>All translated pairs under a prefix, e.g. GetAll("shop.item.") → { "shop.item.sword": "Sword", ... }.</summary>
    public static Dictionary<string, string> GetAll(string prefix = "")
    {
        var result = new Dictionary<string, string>();
        foreach (string k in KeysWithPrefix(prefix)) result[k] = Get(k);
        return result;
    }

    // Extension shortcuts: "menu.play".Tr()
    public static string Tr(this string key) => Get(key);
    public static string Tr(this string key, params object[] args) => Get(key, args);
    public static string TrOr(this string key, string defaultText) => GetOr(key, defaultText);

    // ═════════════════════════ Dynamic content ═════════════════════════

    /// <summary>Add (or replace) one entry in the current language. Lost on language switch / Refresh.</summary>
    public static void Set(string key, string value)
    {
        EnsureInit();
        if (_table == null || string.IsNullOrEmpty(key)) return;
        _table.Set(key, value);
        OnTranslationsChanged?.Invoke();
    }

    public static bool Remove(string key)
    {
        EnsureInit();
        bool removed = _table != null && _table.Remove(key);
        if (removed) OnTranslationsChanged?.Invoke();
        return removed;
    }

    /// <summary>Merge a JSON text into the current language. Lost on language switch / Refresh (use LoadExtra to persist).</summary>
    public static int AddFromJson(string json, bool overwrite = true, string keyPrefix = "")
    {
        EnsureInit();
        if (_table == null) return 0;
        var issues = new List<string>();
        int n = _table.AddJson(json, keyPrefix, overwrite, "AddFromJson", issues);
        ReportIssues(issues);
        if (n > 0) OnTranslationsChanged?.Invoke();
        return n;
    }

    public static int AddFromTextAsset(TextAsset asset, bool overwrite = true, bool prefixWithFileName = false)
        => asset == null ? 0 : AddFromJson(asset.text, overwrite, prefixWithFileName ? asset.name : "");

    public static int AddFromDictionary(IDictionary<string, string> values, bool overwrite = true)
    {
        EnsureInit();
        if (_table == null) return 0;
        int n = _table.AddDictionary(values, overwrite);
        if (n > 0) OnTranslationsChanged?.Invoke();
        return n;
    }

    /// <summary>
    /// Load an extra Resources folder into the current language NOW and after every language switch.
    /// Use "{lang}" for the language folder: LoadExtra("DLC/{lang}") → Resources/DLC/VI, Resources/DLC/EN...
    /// </summary>
    public static int LoadExtra(string resourcePathPattern)
    {
        EnsureInit();
        if (string.IsNullOrWhiteSpace(resourcePathPattern)) return 0;
        string pattern = resourcePathPattern.Trim().Trim('/');
        if (!_extras.Contains(pattern)) _extras.Add(pattern);
        if (_table == null || _index < 0) return 0;

        var issues = new List<string>();
        int files = _table.LoadFolder(ExpandExtra(pattern, _languages[_index]), PrefixKeysWithFileName, true, issues);
        ReportIssues(issues);
        _fallbackTable = null; _peek = null;
        OnTranslationsChanged?.Invoke();
        return files;
    }

    /// <summary>Stop loading an extra folder. Its keys disappear (the table is rebuilt).</summary>
    public static bool UnloadExtra(string resourcePathPattern)
    {
        EnsureInit();
        if (!_extras.Remove((resourcePathPattern ?? "").Trim().Trim('/'))) return false;
        Refresh();
        return true;
    }

    // ═════════════════════════ Internals ═════════════════════════

    private static bool Resolve(string key, out string value)
    {
        if (_table != null && _table.TryGet(key, out value) && !string.IsNullOrEmpty(value)) return true;

        LanguageTable fb = GetFallbackTable();
        if (fb != null && fb.TryGet(key, out value) && !string.IsNullOrEmpty(value)) return true;

        value = null;
        return false;
    }

    private static LanguageTable GetFallbackTable()
    {
        if (string.IsNullOrEmpty(_fallbackCode)) return null;
        if (string.Equals(_fallbackCode, CurrentLanguage, StringComparison.OrdinalIgnoreCase)) return null;

        if (_fallbackTable == null)
        {
            int idx = IndexOf(_fallbackCode);
            if (idx < 0) return null;
            _fallbackTable = BuildTable(idx >= 0 ? _languages[idx] : null);
        }
        return _fallbackTable;
    }

    private static LanguageTable BuildTable(LanguageInfo info)
    {
        var table = new LanguageTable(info.code);
        var issues = new List<string>();

        string root = (ResourcesRoot ?? "").Trim().Trim('/');
        string path = root.Length == 0 ? info.FolderName : root + "/" + info.FolderName;
        int files = table.LoadFolder(path, PrefixKeysWithFileName, true, issues);
        if (files == 0) issues.Add($"✖ [{info.code}] no JSON files found in Resources/{path}");

        foreach (string pattern in _extras)
            table.LoadFolder(ExpandExtra(pattern, info), PrefixKeysWithFileName, true, issues);

        ReportIssues(issues);
        return table;
    }

    private static string ExpandExtra(string pattern, LanguageInfo info)
        => pattern.Replace("{lang}", info.FolderName);

    private static void ReportIssues(List<string> issues)
    {
        if (!LogLoadIssues || issues == null) return;
        foreach (string s in issues) Debug.LogWarning("[Translator] " + s);
    }

    private static string Format(string key, string text, object[] args)
    {
        if (args == null || args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException)
        {
            Debug.LogWarning($"[Translator] Bad format string for key '{key}': \"{text}\"");
            return text;
        }
    }

    private static string Missing(string key)
    {
        if (LogMissingKeys && _warned.Add(key))
            Debug.LogWarning($"[Translator] Missing translation for key '{key}' (language: {CurrentLanguage}).");
        return MissingKeyFormat.Replace("{0}", key);
    }

    private static int IndexOfSystemLanguage(SystemLanguage lang)
    {
        string[] candidates;
        switch (lang)
        {
            case SystemLanguage.Vietnamese: candidates = new[] { "VI", "VN" }; break;
            case SystemLanguage.English: candidates = new[] { "EN" }; break;
            case SystemLanguage.French: candidates = new[] { "FR" }; break;
            case SystemLanguage.German: candidates = new[] { "DE" }; break;
            case SystemLanguage.Spanish: candidates = new[] { "ES" }; break;
            case SystemLanguage.Portuguese: candidates = new[] { "PT" }; break;
            case SystemLanguage.Italian: candidates = new[] { "IT" }; break;
            case SystemLanguage.Russian: candidates = new[] { "RU" }; break;
            case SystemLanguage.Japanese: candidates = new[] { "JA", "JP" }; break;
            case SystemLanguage.Korean: candidates = new[] { "KO", "KR" }; break;
            case SystemLanguage.Thai: candidates = new[] { "TH" }; break;
            case SystemLanguage.Indonesian: candidates = new[] { "ID" }; break;
            case SystemLanguage.Chinese:
            case SystemLanguage.ChineseSimplified:
            case SystemLanguage.ChineseTraditional: candidates = new[] { "ZH", "CN" }; break;
            default: return -1;
        }
        foreach (string c in candidates)
        {
            int i = IndexOf(c);
            if (i >= 0) return i;
        }
        return -1;
    }
}