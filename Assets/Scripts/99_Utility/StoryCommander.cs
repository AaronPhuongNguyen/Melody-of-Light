using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Playables;

/// <summary>How Play() chooses between the entries.</summary>
public enum StoryPickMode
{
    Sequence,        // one after the other (skips entries whose conditions fail)
    FirstAvailable,  // always the first entry that passes its conditions
    Priority,        // the available entry with the highest Priority
    Random           // weighted random among the available entries
}

/// <summary>What Sequence mode does after the last entry.</summary>
public enum StoryEndOfList { Stop, Loop, RepeatLast }

/// <summary>What happens when the StoryTeller is already playing something.</summary>
public enum StoryBusyBehaviour { Queue, Ignore, Interrupt }

[Serializable]
public class StoryVariable
{
    public string Name;
    public string Value;
}

/// <summary>One playable story of a commander: a database dialogue plus the conditions for it to be available.</summary>
[Serializable]
public class StoryEntry
{
    [Tooltip("Optional label used by Play(\"id\"). Empty = the dialogue name.")]
    public string Id;
    [Tooltip("Name of a dialogue in the StoryTeller's database.")]
    public string DialogueName;
    public bool Enabled = true;
    [Min(0)] public int StartLine = 0;
    [Tooltip("Timeline to pause while this plays. Empty = the commander's DefaultDirector.")]
    public PlayableDirector Director;

    [Header("Picking")]
    public int Priority = 0;
    [Min(0f)] public float Weight = 1f;
    [Tooltip("After it has been played once, it is no longer available until ResetProgress().")]
    public bool OncePerSession = false;

    [Header("Conditions (checked against PlayerData)")]
    [Min(0)] public int MinDay = 0;
    [Tooltip("-1 = no upper limit.")] public int MaxDay = -1;
    public string[] RequireCompleted;
    public string[] RequireNotCompleted;

    [Header("Events")]
    public UnityEvent OnStarted;
    public UnityEvent OnEnded;

    /// <summary>Extra condition set from code: entry.Condition = () => hasKey;</summary>
    [NonSerialized] public Func<bool> Condition;
    [NonSerialized] public int PlayCount;

    public string Key => string.IsNullOrEmpty(Id) ? DialogueName : Id;

    public bool IsAvailable()
    {
        if (!Enabled || string.IsNullOrEmpty(DialogueName)) return false;
        if (OncePerSession && PlayCount > 0) return false;

        PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
        if (data != null)
        {
            if (data.Day < MinDay) return false;
            if (MaxDay >= 0 && data.Day > MaxDay) return false;
            if (RequireCompleted != null)
                foreach (string n in RequireCompleted)
                    if (!string.IsNullOrEmpty(n) && !data.IsDialogueCompleted(n)) return false;
            if (RequireNotCompleted != null)
                foreach (string n in RequireNotCompleted)
                    if (!string.IsNullOrEmpty(n) && data.IsDialogueCompleted(n)) return false;
        }

        return Condition == null || Condition();
    }

    public override string ToString() => $"{Key} (p{Priority})";
}

/// <summary>
/// Add to any object that has a story to tell. It owns a list of StoryEntry and commands the StoryTeller.
///
///   commander.Play();                                  // picks an entry by PickMode
///   commander.Play("ask_about_town");                  // entry id or dialogue name, ignores conditions
///   commander.PlayRandom();  commander.PlayNext();  commander.PlayHighestPriority();
///   commander.PlayDialogue("Chapter 2", onDone: () => OpenDoor());
///   commander.PlayLines("Tmp", ("Ann", "key.a"), ("Bob", "key.b"));   // built in code
///   commander.PlayAfter(2f);                           // delay, then Play()
///   commander.AddEntry("night_talk", minDay: 3).Condition = () => hasKey;
///   yield return commander.WaitUntilFinished();        // inside a coroutine
///
/// Triggers (optional): on start, on enable, on trigger enter (3D/2D), on click (UI or physics raycaster).
/// Right-click the component header in Play mode ? Test/... to try everything.
/// </summary>
[DisallowMultipleComponent]
public class StoryCommander : MonoBehaviour
{
    // ????????????? settings ?????????????
    [Header("Teller")]
    [Tooltip("Empty = StoryTeller.Instance.")]
    [SerializeField] private StoryTeller teller;
    public PlayableDirector DefaultDirector;

    [Header("Story")]
    public List<StoryEntry> Entries = new List<StoryEntry>();
    public StoryPickMode PickMode = StoryPickMode.Sequence;
    public StoryEndOfList EndOfList = StoryEndOfList.Stop;
    [Tooltip("What to do when the StoryTeller is already playing something.")]
    public StoryBusyBehaviour Busy = StoryBusyBehaviour.Queue;
    [Tooltip("Played when Play() finds no available entry (e.g. \"dia.nothing\"). Empty = nothing happens.")]
    public string NothingAvailableDialogue = "";
    [Min(0f), Tooltip("Minimum seconds between two plays from this commander.")]
    public float Cooldown = 0f;

    [Header("Variables (set on the StoryTeller before every play)")]
    [Tooltip("Sets {player}, {day} and {money} from PlayerData.")]
    public bool AutoVariables = true;
    public List<StoryVariable> Variables = new List<StoryVariable>();

    [Header("Triggers")]
    public bool PlayOnStart;
    public bool PlayOnEnable;
    public bool PlayOnClick;
    public bool PlayOnTriggerEnter;
    [Tooltip("Only this tag starts the trigger. Empty = anything.")]
    public string TriggerTag = "Player";
    public bool TriggerOnce = true;
    [Min(0f), Tooltip("Delay applied to the automatic triggers above.")]
    public float TriggerDelay = 0f;

    [Header("Events")]
    public UnityEvent onStarted;
    public UnityEvent onEnded;
    public UnityEvent onNothingAvailable;

    [Header("Test (right-click the component header ? Test/...)")]
    public string TestKey = "";
    public string TestDialogueName = "Chapter 1";
    [Min(0f)] public float TestDelay = 2f;

    /// <summary>A story of this commander started. Entry is null for ad-hoc dialogues.</summary>
    public event Action<StoryEntry> Started;
    /// <summary>A story of this commander ended (finished, skipped or interrupted).</summary>
    public event Action<StoryEntry> Ended;
    public event Action NothingAvailable;

    // ????????????? state ?????????????
    private class Active
    {
        public Dialogues Original, Effective;
        public StoryEntry Entry;
        public Action Done;
        public bool Started;
    }

    private readonly List<Active> _active = new List<Active>();
    private readonly Dictionary<string, object> _runtimeVars = new Dictionary<string, object>();
    private StoryTeller _hooked;
    private int _cursor;
    private float _lastPlay = -999f;
    private bool _triggered;

    public StoryTeller Teller { get => teller != null ? teller : StoryTeller.Instance; set => teller = value; }
    /// <summary>Something of this commander is queued or playing.</summary>
    public bool IsActive => _active.Count > 0;
    /// <summary>Something of this commander is on screen right now.</summary>
    public bool IsPlaying => _active.Exists(a => a.Started);
    public int EntryCount => Entries.Count;

    public int AvailableCount
    {
        get { int n = 0; foreach (StoryEntry e in Entries) if (e != null && e.IsAvailable()) n++; return n; }
    }

    public bool HasAvailable => AvailableCount > 0;

    // ????????????????????????? Unity ?????????????????????????

    private void OnEnable()
    {
        if (PlayOnEnable) StartCoroutine(AutoRoutine());
    }

    private void Start()
    {
        if (PlayOnStart) StartCoroutine(AutoRoutine());
    }

    private void OnDestroy()
    {
        Unhook();
        _active.Clear();
    }

    private void OnTriggerEnter(Collider other) => HandleTrigger(other != null ? other.tag : null);
    private void OnTriggerEnter2D(Collider2D other) => HandleTrigger(other != null ? other.tag : null);

    private void HandleTrigger(string otherTag)
    {
        if (!PlayOnTriggerEnter || (TriggerOnce && _triggered)) return;
        if (!string.IsNullOrEmpty(TriggerTag) && otherTag != TriggerTag) return;
        _triggered = true;
        StartCoroutine(AutoRoutine());
    }

    /// <summary>Needs an EventSystem; works on UI elements and on colliders with a Physics Raycaster.</summary>
    public void OnPointerClick(PointerEventData eventData)
    {
        if (PlayOnClick) Play();
    }

    private IEnumerator AutoRoutine()
    {
        yield return null; // let the StoryTeller finish its own Awake
        if (TriggerDelay > 0f) yield return new WaitForSecondsRealtime(TriggerDelay);
        Play();
    }

    // ????????????????????????? Play: by pick mode ?????????????????????????

    /// <summary>Pick an entry by PickMode and play it. Returns false when nothing was started.</summary>
    public bool Play(Action onDone = null)
    {
        StoryEntry e = Pick(PickMode, true);
        return e != null ? RunEntry(e, onDone, Busy) : HandleNothing(onDone);
    }

    public bool PlayNext(Action onDone = null) => PlayPicked(StoryPickMode.Sequence, onDone);
    public bool PlayRandom(Action onDone = null) => PlayPicked(StoryPickMode.Random, onDone);
    public bool PlayFirstAvailable(Action onDone = null) => PlayPicked(StoryPickMode.FirstAvailable, onDone);
    public bool PlayHighestPriority(Action onDone = null) => PlayPicked(StoryPickMode.Priority, onDone);

    private bool PlayPicked(StoryPickMode mode, Action onDone)
    {
        StoryEntry e = Pick(mode, true);
        return e != null ? RunEntry(e, onDone, Busy) : HandleNothing(onDone);
    }

    /// <summary>Wait <paramref name="seconds"/> (unscaled), then Play().</summary>
    public void PlayAfter(float seconds, Action onDone = null) => StartCoroutine(DelayRoutine(seconds, () => Play(onDone)));

    public void PlayAfter(float seconds, string key, Action onDone = null) => StartCoroutine(DelayRoutine(seconds, () => Play(key, onDone)));

    private static IEnumerator DelayRoutine(float seconds, Action then)
    {
        if (seconds > 0f) yield return new WaitForSecondsRealtime(seconds);
        then?.Invoke();
    }

    // ????????????????????????? Play: explicit ?????????????????????????

    /// <summary>
    /// Play a specific entry by its Id / dialogue name. Conditions are ignored (you asked for it explicitly).
    /// If no entry has that key, the name is treated as a dialogue of the database.
    /// </summary>
    public bool Play(string key, Action onDone = null)
    {
        StoryEntry e = FindEntry(key);
        return e != null ? RunEntry(e, onDone, Busy) : PlayDialogue(key, null, 0, onDone);
    }

    public bool Play(int entryIndex, Action onDone = null)
    {
        if (entryIndex < 0 || entryIndex >= Entries.Count || Entries[entryIndex] == null)
        {
            Debug.LogWarning($"[StoryCommander] No entry at index {entryIndex}.", this);
            return false;
        }
        return RunEntry(Entries[entryIndex], onDone, Busy);
    }

    public bool Play(StoryEntry entry, Action onDone = null) => RunEntry(entry, onDone, Busy);

    /// <summary>Play any Dialogues object (also ones built in code).</summary>
    public bool Play(Dialogues dialogues, Action onDone = null) => RunDialogue(dialogues, null, null, 0, onDone, Busy);

    /// <summary>Play a database dialogue directly, without an entry.</summary>
    public bool PlayDialogue(string dialogueName, PlayableDirector director = null, int startLine = 0, Action onDone = null)
    {
        StoryTeller t = Teller;
        if (t == null || t.dialoguesDatabase == null || !t.dialoguesDatabase.TryGet(dialogueName, out Dialogues d))
        {
            Debug.LogWarning($"[StoryCommander] Dialogue '{dialogueName}' not found.", this);
            return false;
        }
        return RunDialogue(d, null, director, startLine, onDone, Busy);
    }

    /// <summary>Like Play(key) but ends whatever is playing first.</summary>
    public bool Interrupt(string key, Action onDone = null)
    {
        StoryEntry e = FindEntry(key);
        if (e != null) return RunEntry(e, onDone, StoryBusyBehaviour.Interrupt);

        StoryTeller t = Teller;
        if (t == null || t.dialoguesDatabase == null || !t.dialoguesDatabase.TryGet(key, out Dialogues d)) return false;
        return RunDialogue(d, null, null, 0, onDone, StoryBusyBehaviour.Interrupt);
    }

    /// <summary>Build a dialogue in code from ready lines. Use a unique name, it is saved as completed by name.</summary>
    public bool PlayLines(string dialogueName, params DialoguesAsset[] lines)
        => RunDialogue(new Dialogues(dialogueName, lines), null, null, 0, null, Busy);

    /// <summary>Build a dialogue in code from (speaker, translator key) pairs.</summary>
    public bool PlayLines(string dialogueName, params (string speaker, string key)[] lines)
    {
        var d = new Dialogues(dialogueName);
        foreach (var l in lines) d.Add(l.speaker, l.key);
        return RunDialogue(d, null, null, 0, null, Busy);
    }

    // ????????????????????????? Control ?????????????????????????

    /// <summary>End the dialogue of this commander if it is the one on screen, and cancel delayed plays.</summary>
    public void Stop()
    {
        StopAllCoroutines();
        StoryTeller t = Teller;
        if (t == null || !t.IsPlaying) return;
        Active mine = _active.Find(a => a.Started);
        if (mine != null) t.End();
    }

    /// <summary>Advance the dialogue (only if it is ours).</summary>
    public bool Next() => IsPlaying && Teller != null && Teller.Next();

    /// <summary>Coroutine helper: yield return commander.WaitUntilFinished();</summary>
    public WaitUntil WaitUntilFinished() => new WaitUntil(() => !IsActive);

    // ????????????????????????? Entries ?????????????????????????

    public StoryEntry FindEntry(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return Entries.Find(e => e != null && (e.Id == key || e.DialogueName == key));
    }

    public bool HasEntry(string key) => FindEntry(key) != null;

    public bool IsAvailable(string key)
    {
        StoryEntry e = FindEntry(key);
        return e != null && e.IsAvailable();
    }

    public List<StoryEntry> GetAvailable()
    {
        var result = new List<StoryEntry>();
        foreach (StoryEntry e in Entries) if (e != null && e.IsAvailable()) result.Add(e);
        return result;
    }

    public StoryEntry AddEntry(string dialogueName, string id = null, int minDay = 0, int maxDay = -1,
                               int priority = 0, bool oncePerSession = false)
    {
        var e = new StoryEntry
        {
            DialogueName = dialogueName,
            Id = id,
            MinDay = minDay,
            MaxDay = maxDay,
            Priority = priority,
            OncePerSession = oncePerSession
        };
        Entries.Add(e);
        return e;
    }

    public bool RemoveEntry(string key)
    {
        StoryEntry e = FindEntry(key);
        return e != null && Entries.Remove(e);
    }

    public bool SetEntryEnabled(string key, bool enabled)
    {
        StoryEntry e = FindEntry(key);
        if (e == null) return false;
        e.Enabled = enabled;
        return true;
    }

    /// <summary>Back to the first entry; clears the OncePerSession counters.</summary>
    public void ResetProgress()
    {
        _cursor = 0;
        _triggered = false;
        foreach (StoryEntry e in Entries) if (e != null) e.PlayCount = 0;
    }

    /// <summary>How many of the entries are completed in the save (PlayerData).</summary>
    public int CompletedEntryCount
    {
        get
        {
            PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
            if (data == null) return 0;
            int n = 0;
            foreach (StoryEntry e in Entries) if (e != null && data.IsDialogueCompleted(e.DialogueName)) n++;
            return n;
        }
    }

    // ????????????????????????? Variables ?????????????????????????

    /// <summary>Set from code; applied to the StoryTeller before every play of this commander.</summary>
    public void SetVariable(string name, object value)
    {
        if (!string.IsNullOrEmpty(name)) _runtimeVars[name] = value;
    }

    public bool RemoveVariable(string name) => _runtimeVars.Remove(name ?? "");
    public void ClearVariables() => _runtimeVars.Clear();

    private void ApplyVariables(StoryTeller t)
    {
        if (AutoVariables)
        {
            PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
            if (data != null)
            {
                if (!string.IsNullOrEmpty(data.PlayerName)) t.SetVariable("player", data.PlayerName);
                t.SetVariable("day", data.Day);
                t.SetVariable("money", data.Money);
            }
        }
        foreach (StoryVariable v in Variables)
            if (v != null && !string.IsNullOrEmpty(v.Name)) t.SetVariable(v.Name, v.Value);
        foreach (var kv in _runtimeVars) t.SetVariable(kv.Key, kv.Value);
    }

    // ????????????????????????? Picking ?????????????????????????

    /// <summary>Choose an entry without playing it. 'advance' moves the Sequence cursor.</summary>
    public StoryEntry Pick(StoryPickMode mode, bool advance = false)
    {
        switch (mode)
        {
            case StoryPickMode.FirstAvailable:
                foreach (StoryEntry e in Entries) if (e != null && e.IsAvailable()) return e;
                return null;

            case StoryPickMode.Priority:
                {
                    StoryEntry best = null;
                    foreach (StoryEntry e in Entries)
                        if (e != null && e.IsAvailable() && (best == null || e.Priority > best.Priority)) best = e;
                    return best;
                }

            case StoryPickMode.Random:
                {
                    List<StoryEntry> list = GetAvailable();
                    float total = 0f;
                    foreach (StoryEntry e in list) total += Mathf.Max(0f, e.Weight);
                    if (list.Count == 0) return null;
                    if (total <= 0f) return list[UnityEngine.Random.Range(0, list.Count)];

                    float roll = UnityEngine.Random.value * total;
                    foreach (StoryEntry e in list)
                    {
                        roll -= Mathf.Max(0f, e.Weight);
                        if (roll <= 0f) return e;
                    }
                    return list[list.Count - 1];
                }

            default:
                return PickSequence(advance);
        }
    }

    private StoryEntry PickSequence(bool advance)
    {
        int c = Entries.Count;
        if (c == 0) return null;

        for (int i = _cursor; i < c; i++)
            if (Entries[i] != null && Entries[i].IsAvailable()) { if (advance) _cursor = i + 1; return Entries[i]; }

        if (EndOfList == StoryEndOfList.Loop)
        {
            for (int i = 0; i < Mathf.Min(_cursor, c); i++)
                if (Entries[i] != null && Entries[i].IsAvailable()) { if (advance) _cursor = i + 1; return Entries[i]; }
        }
        else if (EndOfList == StoryEndOfList.RepeatLast)
        {
            StoryEntry last = Entries[c - 1];
            if (last != null && last.IsAvailable()) return last;
        }
        return null;
    }

    private bool HandleNothing(Action onDone)
    {
        onNothingAvailable?.Invoke();
        NothingAvailable?.Invoke();
        return !string.IsNullOrEmpty(NothingAvailableDialogue) && PlayDialogue(NothingAvailableDialogue, null, 0, onDone);
    }

    // ????????????????????????? Running ?????????????????????????

    private bool RunEntry(StoryEntry e, Action onDone, StoryBusyBehaviour busy)
    {
        if (e == null) return false;
        StoryTeller t = Teller;
        if (t == null || t.dialoguesDatabase == null || !t.dialoguesDatabase.TryGet(e.DialogueName, out Dialogues d))
        {
            Debug.LogWarning($"[StoryCommander] Dialogue '{(e != null ? e.DialogueName : "?")}' not found.", this);
            return false;
        }
        return RunDialogue(d, e, e.Director != null ? e.Director : DefaultDirector, e.StartLine, onDone, busy);
    }

    private bool RunDialogue(Dialogues d, StoryEntry entry, PlayableDirector director, int startLine, Action onDone,
                             StoryBusyBehaviour busy)
    {
        StoryTeller t = Teller;
        if (t == null) { Debug.LogWarning("[StoryCommander] No StoryTeller in the scene.", this); return false; }
        if (d == null || d.Count == 0) return false;
        if (Cooldown > 0f && Time.unscaledTime - _lastPlay < Cooldown) return false;
        if (t.IsPlaying && busy == StoryBusyBehaviour.Ignore) return false;

        Hook(t);
        ApplyVariables(t);

        var rec = new Active { Original = d, Effective = Resolve(t, d), Entry = entry, Done = onDone };
        _active.Add(rec); // added first: StoryTeller raises DialogueStarted synchronously
        if (director == null) director = DefaultDirector;

        if (!t.StartDialogue(d, director, startLine, busy == StoryBusyBehaviour.Interrupt))
        {
            _active.Remove(rec);
            return false;
        }

        _lastPlay = Time.unscaledTime;
        if (entry != null) entry.PlayCount++;
        return true;
    }

    /// <summary>The dialogue the StoryTeller will really play (it swaps completed ones for its fallback).</summary>
    private static Dialogues Resolve(StoryTeller t, Dialogues d)
    {
        if (t.FallbackWhenCompleted && d.IsCompleted && d.DialogueName != t.CompletedFallbackName
            && t.dialoguesDatabase != null && t.dialoguesDatabase.TryGet(t.CompletedFallbackName, out Dialogues fb) && fb.Count > 0)
            return fb;
        return d;
    }

    // ????????????????????????? StoryTeller hooks ?????????????????????????

    private void Hook(StoryTeller t)
    {
        if (_hooked == t) return;
        Unhook();
        _hooked = t;
        t.DialogueStarted += OnTellerStarted;
        t.DialogueEnded += OnTellerEnded;
    }

    private void Unhook()
    {
        if (_hooked == null) return;
        _hooked.DialogueStarted -= OnTellerStarted;
        _hooked.DialogueEnded -= OnTellerEnded;
        _hooked = null;
    }

    private bool Matches(Active a, Dialogues d)
        => d == a.Original || d == a.Effective
           || (_hooked != null && d.DialogueName == _hooked.CompletedFallbackName && a.Original.IsCompleted);

    private void OnTellerStarted(Dialogues d)
    {
        Active a = _active.Find(x => !x.Started && Matches(x, d));
        if (a == null) return;
        a.Started = true;

        a.Entry?.OnStarted?.Invoke();
        onStarted?.Invoke();
        Started?.Invoke(a.Entry);
    }

    private void OnTellerEnded(Dialogues d)
    {
        Active a = _active.Find(x => x.Started && Matches(x, d));
        if (a == null) return;
        _active.Remove(a);

        a.Entry?.OnEnded?.Invoke();
        onEnded?.Invoke();
        Ended?.Invoke(a.Entry);
        a.Done?.Invoke();
    }

    // ????????????????????????? Context menu tests ?????????????????????????

    #region Context Menu Tests
    private bool TestPlaying()
    {
        if (Application.isPlaying) return true;
        Debug.LogWarning("[StoryCommander/Test] Enter Play mode first.", this);
        return false;
    }

    private static void TLog(string m) => Debug.Log("[StoryCommander/Test] " + m);

    [ContextMenu("Test/Play (by PickMode)")]
    private void TestPlay() { if (TestPlaying()) TLog($"Play ({PickMode}) ? {Play(() => TLog("   done"))}"); }

    [ContextMenu("Test/Play/Next in sequence")]
    private void TestNextInSequence() { if (TestPlaying()) TLog($"PlayNext ? {PlayNext()}"); }

    [ContextMenu("Test/Play/Random")]
    private void TestRandom() { if (TestPlaying()) TLog($"PlayRandom ? {PlayRandom()}"); }

    [ContextMenu("Test/Play/First available")]
    private void TestFirst() { if (TestPlaying()) TLog($"PlayFirstAvailable ? {PlayFirstAvailable()}"); }

    [ContextMenu("Test/Play/Highest priority")]
    private void TestPriority() { if (TestPlaying()) TLog($"PlayHighestPriority ? {PlayHighestPriority()}"); }

    [ContextMenu("Test/Play/By TestKey")]
    private void TestByKey() { if (TestPlaying()) TLog($"Play('{TestKey}') ? {Play(TestKey)}"); }

    [ContextMenu("Test/Play/Interrupt with TestKey")]
    private void TestInterruptKey() { if (TestPlaying()) TLog($"Interrupt('{TestKey}') ? {Interrupt(TestKey)}"); }

    [ContextMenu("Test/Play/Database dialogue (TestDialogueName)")]
    private void TestRaw() { if (TestPlaying()) TLog($"PlayDialogue('{TestDialogueName}') ? {PlayDialogue(TestDialogueName)}"); }

    [ContextMenu("Test/Play/After TestDelay")]
    private void TestAfter() { if (!TestPlaying()) return; PlayAfter(TestDelay); TLog($"Play() in {TestDelay}s"); }

    [ContextMenu("Test/Play/Runtime demo (built in code)")]
    private void TestDemo()
    {
        if (!TestPlaying()) return;
        Translator.Set("commander.demo.1", "Hello {player}! This story was built by a StoryCommander.");
        Translator.Set("commander.demo.2", "Today is day {day}.");
        TLog($"PlayLines ? {PlayLines("Commander.Demo", ("Ann", "commander.demo.1"), ("Ann", "commander.demo.2"))}");
    }

    [ContextMenu("Test/Control/Stop")]
    private void TestStop() { if (TestPlaying()) Stop(); }

    [ContextMenu("Test/Control/Next")]
    private void TestNext() { if (TestPlaying()) TLog($"Next ? {Next()}"); }

    [ContextMenu("Test/Control/Reset progress")]
    private void TestReset() { ResetProgress(); TLog("Progress reset."); }

    [ContextMenu("Test/Info/List entries")]
    private void TestList()
    {
        if (!TestPlaying()) return;
        TLog($"{Entries.Count} entries, {AvailableCount} available, {CompletedEntryCount} completed in the save:");
        for (int i = 0; i < Entries.Count; i++)
        {
            StoryEntry e = Entries[i];
            if (e != null) TLog($"   [{i}] {e}  available: {e.IsAvailable()}  played: {e.PlayCount}");
        }
    }

    [ContextMenu("Test/Info/Log state")]
    private void TestState()
    {
        if (!TestPlaying()) return;
        TLog($"Mode: {PickMode}/{EndOfList}  Busy: {Busy}  Cursor: {_cursor}  Active: {_active.Count}  Playing: {IsPlaying}  " +
             $"Teller: {(Teller != null ? "ok" : "missing")}");
    }
    #endregion
}