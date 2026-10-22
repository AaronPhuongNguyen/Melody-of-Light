using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Points at one line inside a database: which dialogue, which index. Returned by line searches.</summary>
public readonly struct DialogueLineRef
{
    public readonly Dialogues Dialogues;
    public readonly int DialogueIndex;
    public readonly int LineIndex;

    public DialogueLineRef(Dialogues dialogues, int dialogueIndex, int lineIndex)
    {
        Dialogues = dialogues; DialogueIndex = dialogueIndex; LineIndex = lineIndex;
    }

    public DialoguesAsset Line => Dialogues != null ? Dialogues[LineIndex] : null;
    public bool IsValid => Dialogues != null && Line != null;
    public override string ToString() => $"{(Dialogues != null ? Dialogues.DialogueName : "?")}[{LineIndex}]";
}

/// <summary>A named sequence of lines (a chapter / scene). Lines can be different subclasses of DialoguesAsset.</summary>
[Serializable]
public class Dialogues
{
    public string DialogueName = "Chapter 1";

    [SerializeReference] public DialoguesAsset[] dialog = Array.Empty<DialoguesAsset>();

    public Dialogues() { }

    public Dialogues(string name, params DialoguesAsset[] lines)
    {
        DialogueName = name;
        dialog = lines ?? Array.Empty<DialoguesAsset>();
    }


    public bool IsCompleted =>
    PlayerManager.Instance != null && PlayerManager.Instance.IsDialogueCompleted(DialogueName);
    public int Count => dialog != null ? dialog.Length : 0;

    public DialoguesAsset this[int index] => dialog != null && index >= 0 && index < dialog.Length ? dialog[index] : null;

    // ───────────── searching lines of this dialogue ─────────────

    public int IndexOf(DialoguesAsset line) => dialog != null ? Array.IndexOf(dialog, line) : -1;
    public bool Contains(DialoguesAsset line) => IndexOf(line) >= 0;

    /// <summary>Index of the first line (from startIndex) that matches, or -1.</summary>
    public int IndexOfLine(Func<DialoguesAsset, bool> match, int startIndex = 0)
    {
        if (dialog == null || match == null) return -1;
        for (int i = Mathf.Max(0, startIndex); i < dialog.Length; i++)
            if (dialog[i] != null && match(dialog[i])) return i;
        return -1;
    }

    public DialoguesAsset FindLine(Func<DialoguesAsset, bool> match)
    {
        int i = IndexOfLine(match);
        return i < 0 ? null : dialog[i];
    }

    public List<DialoguesAsset> FindLines(Func<DialoguesAsset, bool> match)
    {
        var result = new List<DialoguesAsset>();
        if (dialog == null || match == null) return result;
        foreach (var l in dialog) if (l != null && match(l)) result.Add(l);
        return result;
    }

    public bool HasSpeaker(string speaker)
        => IndexOfLine(l => string.Equals(l.Speaker, speaker, StringComparison.OrdinalIgnoreCase)) >= 0;

    public bool UsesContentKey(string contentKey)
        => IndexOfLine(l => string.Equals(l.Content, contentKey, StringComparison.Ordinal)) >= 0;

    public Dialogues Add(DialoguesAsset line)
    {
        if (line == null) return this;
        if (dialog == null) dialog = Array.Empty<DialoguesAsset>();
        Array.Resize(ref dialog, dialog.Length + 1);
        dialog[dialog.Length - 1] = line;
        return this;
    }

    public Dialogues Add(string speaker, string contentKey, Sprite speakerImage = null)
        => Add(DialoguesAsset.Create(speaker, contentKey, speakerImage));

    public void Insert(int index, DialoguesAsset line)
    {
        if (line == null) return;
        var list = new List<DialoguesAsset>(dialog ?? Array.Empty<DialoguesAsset>());
        list.Insert(Mathf.Clamp(index, 0, list.Count), line);
        dialog = list.ToArray();
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= Count) return false;
        var list = new List<DialoguesAsset>(dialog);
        list.RemoveAt(index);
        dialog = list.ToArray();
        return true;
    }

    public void RemoveNulls()
    {
        if (dialog == null) return;
        dialog = Array.FindAll(dialog, l => l != null);
    }
}

[CreateAssetMenu(fileName = "DialoguesDatabase", menuName = "Database/Dialogue")]
public class DialoguesDatabase : ScriptableObject
{
    public List<Dialogues> dialogues = new List<Dialogues> { new Dialogues() };

    // Lazy search structures. Rebuilt after InvalidateLookup() (also called by Add/Remove and in the editor).
    private Dictionary<string, Dialogues> _lookup;                          // exact name        O(1)
    private Dialogues[] _sorted;                                            // by name, ignore case → binary search
    private Dictionary<string, List<DialogueLineRef>> _bySpeaker;           // speaker           O(1)
    private Dictionary<string, List<DialogueLineRef>> _byContent;           // content key       O(1)

    public int Count => dialogues.Count;
    public Dialogues this[int index] => index >= 0 && index < dialogues.Count ? dialogues[index] : null;
    public Dialogues this[string name] => TryGet(name, out Dialogues d) ? d : null;

    public IEnumerable<string> Names
    {
        get { foreach (var d in dialogues) if (d != null) yield return d.DialogueName; }
    }

    public bool TryGet(string name, out Dialogues dialogue)
    {
        if (_lookup == null) BuildLookup();
        dialogue = null;
        return !string.IsNullOrEmpty(name) && _lookup.TryGetValue(name, out dialogue);
    }

    public bool Contains(string name) => TryGet(name, out _);

    public int IndexOf(string name)
    {
        for (int i = 0; i < dialogues.Count; i++)
            if (dialogues[i] != null && dialogues[i].DialogueName == name) return i;
        return -1;
    }

    /// <summary>Runtime add (in memory; inside the editor it also modifies the asset in play mode).</summary>
    public void Add(Dialogues dialogue)
    {
        if (dialogue == null) return;
        dialogues.Add(dialogue);
        InvalidateLookup();
    }

    public bool Remove(string name)
    {
        int i = IndexOf(name);
        if (i < 0) return false;
        dialogues.RemoveAt(i);
        InvalidateLookup();
        return true;
    }

    // ═════════════════════════ Search ═════════════════════════
    // Exact name:            TryGet / Contains / IndexOf                  (hash, O(1))
    // Name, ignore case:     TryGetIgnoreCase                             (binary search, O(log n))
    // Name prefix:           FindByPrefix / FindFirstByPrefix             (binary search + range scan)
    // Speaker / content key: FindBySpeaker, FindLinesBySpeaker, FindByContentKey, FindLinesByContentKey   (hash)
    // Anything else:         Find / FindAll / FindContaining / FindLines / SearchLines                      (linear)
    // After editing dialogues or lines BY CODE, call InvalidateLookup().

    /// <summary>Exact name, case-insensitive, by binary search.</summary>
    public bool TryGetIgnoreCase(string name, out Dialogues dialogue)
    {
        dialogue = null;
        if (string.IsNullOrEmpty(name)) return false;
        EnsureSorted();
        int i = LowerBound(name);
        if (i < _sorted.Length && string.Equals(_sorted[i].DialogueName, name, StringComparison.OrdinalIgnoreCase))
        {
            dialogue = _sorted[i];
            return true;
        }
        return false;
    }

    /// <summary>All dialogues whose name starts with the prefix (ignore case), sorted by name. Empty prefix = all.</summary>
    public List<Dialogues> FindByPrefix(string prefix)
    {
        EnsureSorted();
        prefix = prefix ?? "";
        var result = new List<Dialogues>();
        for (int i = LowerBound(prefix); i < _sorted.Length; i++)
        {
            if (!_sorted[i].DialogueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
            result.Add(_sorted[i]);
        }
        return result;
    }

    public Dialogues FindFirstByPrefix(string prefix)
    {
        EnsureSorted();
        prefix = prefix ?? "";
        int i = LowerBound(prefix);
        return i < _sorted.Length && _sorted[i].DialogueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? _sorted[i] : null;
    }

    /// <summary>Dialogues whose name contains the text (ignore case).</summary>
    public List<Dialogues> FindContaining(string text)
        => FindAll(d => d.DialogueName != null && d.DialogueName.IndexOf(text ?? "", StringComparison.OrdinalIgnoreCase) >= 0);

    public Dialogues Find(Func<Dialogues, bool> match)
    {
        if (match == null) return null;
        foreach (var d in dialogues) if (d != null && match(d)) return d;
        return null;
    }

    public List<Dialogues> FindAll(Func<Dialogues, bool> match)
    {
        var result = new List<Dialogues>();
        if (match == null) return result;
        foreach (var d in dialogues) if (d != null && match(d)) result.Add(d);
        return result;
    }

    /// <summary>Every distinct speaker used in the database (as written in the assets).</summary>
    public List<string> GetSpeakers()
    {
        EnsureLineIndexes();
        var keys = new List<string>();
        foreach (var kv in _bySpeaker) keys.Add(kv.Value[0].Line != null ? kv.Value[0].Line.Speaker : kv.Key);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return keys;
    }

    /// <summary>Every line spoken by this speaker (ignore case).</summary>
    public List<DialogueLineRef> FindLinesBySpeaker(string speaker)
    {
        EnsureLineIndexes();
        return !string.IsNullOrEmpty(speaker) && _bySpeaker.TryGetValue(speaker, out var list)
            ? new List<DialogueLineRef>(list) : new List<DialogueLineRef>();
    }

    /// <summary>Dialogues in which this speaker talks (each dialogue once).</summary>
    public List<Dialogues> FindBySpeaker(string speaker) => DistinctDialogues(FindLinesBySpeaker(speaker));

    /// <summary>Every line that uses this Translator key.</summary>
    public List<DialogueLineRef> FindLinesByContentKey(string contentKey)
    {
        EnsureLineIndexes();
        return !string.IsNullOrEmpty(contentKey) && _byContent.TryGetValue(contentKey, out var list)
            ? new List<DialogueLineRef>(list) : new List<DialogueLineRef>();
    }

    public List<Dialogues> FindByContentKey(string contentKey) => DistinctDialogues(FindLinesByContentKey(contentKey));

    /// <summary>Every line that satisfies the condition.</summary>
    public List<DialogueLineRef> FindLines(Func<DialoguesAsset, bool> match)
    {
        var result = new List<DialogueLineRef>();
        if (match == null) return result;
        for (int d = 0; d < dialogues.Count; d++)
        {
            Dialogues dlg = dialogues[d];
            if (dlg == null || dlg.dialog == null) continue;
            for (int l = 0; l < dlg.dialog.Length; l++)
                if (dlg.dialog[l] != null && match(dlg.dialog[l])) result.Add(new DialogueLineRef(dlg, d, l));
        }
        return result;
    }

    /// <summary>
    /// Text search over speaker and content (raw values). With translated = true the text of the CURRENT language
    /// is searched as well (what the player reads).
    /// </summary>
    public List<DialogueLineRef> SearchLines(string text, bool translated = false, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(text)) return new List<DialogueLineRef>();
        StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return FindLines(l =>
            Has(l.Speaker, text, cmp) || Has(l.Content, text, cmp) ||
            (translated && (Has(l.GetSpeaker(), text, cmp) || Has(l.GetText(), text, cmp))));
    }

    private static bool Has(string source, string text, StringComparison cmp)
        => !string.IsNullOrEmpty(source) && source.IndexOf(text, cmp) >= 0;

    private static List<Dialogues> DistinctDialogues(List<DialogueLineRef> refs)
    {
        var result = new List<Dialogues>();
        foreach (var r in refs) if (!result.Contains(r.Dialogues)) result.Add(r.Dialogues);
        return result;
    }

    private void EnsureSorted()
    {
        if (_sorted != null) return;
        var list = new List<Dialogues>(dialogues.Count);
        foreach (var d in dialogues) if (d != null && !string.IsNullOrEmpty(d.DialogueName)) list.Add(d);
        list.Sort((a, b) => string.Compare(a.DialogueName, b.DialogueName, StringComparison.OrdinalIgnoreCase));
        _sorted = list.ToArray();
    }

    /// <summary>First index in _sorted whose name is >= key (ignore case).</summary>
    private int LowerBound(string key)
    {
        int lo = 0, hi = _sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (string.Compare(_sorted[mid].DialogueName, key, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void EnsureLineIndexes()
    {
        if (_bySpeaker != null) return;
        _bySpeaker = new Dictionary<string, List<DialogueLineRef>>(StringComparer.OrdinalIgnoreCase);
        _byContent = new Dictionary<string, List<DialogueLineRef>>(StringComparer.Ordinal);

        for (int d = 0; d < dialogues.Count; d++)
        {
            Dialogues dlg = dialogues[d];
            if (dlg == null || dlg.dialog == null) continue;
            for (int l = 0; l < dlg.dialog.Length; l++)
            {
                DialoguesAsset line = dlg.dialog[l];
                if (line == null) continue;
                var r = new DialogueLineRef(dlg, d, l);
                AddRef(_bySpeaker, line.Speaker, r);
                AddRef(_byContent, line.Content, r);
            }
        }
    }

    private static void AddRef(Dictionary<string, List<DialogueLineRef>> map, string key, DialogueLineRef r)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<DialogueLineRef>();
        list.Add(r);
    }

    /// <summary>Call after changing 'dialogues', a DialogueName or any line by code.</summary>
    public void InvalidateLookup()
    {
        _lookup = null;
        _sorted = null;
        _bySpeaker = null;
        _byContent = null;
    }

    private void BuildLookup()
    {
        _lookup = new Dictionary<string, Dialogues>(dialogues.Count, StringComparer.Ordinal);
        foreach (var d in dialogues)
        {
            if (d == null || string.IsNullOrEmpty(d.DialogueName)) continue;
            if (_lookup.ContainsKey(d.DialogueName))
            {
                Debug.LogWarning($"[DialoguesDatabase] Duplicate dialogue name '{d.DialogueName}' in '{name}' (first one is used).", this);
                continue;
            }
            _lookup.Add(d.DialogueName, d);
        }
    }

    private void OnEnable() => InvalidateLookup();
    private void OnValidate()
    {
        InvalidateLookup();
#if UNITY_EDITOR
        SeparateSharedLines();
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Unity's list "+" button copies the last element, and [SerializeReference] lines are then shared by both copies.
    /// Every line object that appears more than once gets its own clone, so editing one dialogue no longer changes another.
    /// </summary>
    private void SeparateSharedLines()
    {
        if (dialogues == null) return;

        var seen = new HashSet<DialoguesAsset>(); // DialoguesAsset has no Equals override, so this compares references
        bool changed = false;

        foreach (Dialogues d in dialogues)
        {
            if (d == null || d.dialog == null) continue;
            for (int i = 0; i < d.dialog.Length; i++)
            {
                DialoguesAsset line = d.dialog[i];
                if (line == null || seen.Add(line)) continue;

                DialoguesAsset copy = CloneLine(line);
                d.dialog[i] = copy;
                seen.Add(copy);
                changed = true;
            }
        }

        if (changed) UnityEditor.EditorUtility.SetDirty(this);
    }

    private static DialoguesAsset CloneLine(DialoguesAsset source)
    {
        var copy = (DialoguesAsset)Activator.CreateInstance(source.GetType());
        // EditorJsonUtility keeps Unity object references (Sprite, AudioClip) intact
        UnityEditor.EditorJsonUtility.FromJsonOverwrite(UnityEditor.EditorJsonUtility.ToJson(source), copy);
        return copy;
    }
#endif
}