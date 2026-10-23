using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Playables;
using UnityEngine.UI;

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

/// <summary>What a bound Button does when clicked.</summary>
public enum StoryButtonAction
{
    Play,                // by PickMode
    PlayNext,
    PlayRandom,
    PlayFirstAvailable,
    PlayHighestPriority,
    PlayKey,             // Key = entry id or dialogue name (ignores conditions)
    PlayIndex,           // Index = entry index (ignores conditions)
    PlayKeyIfAvailable,  // Key, but only if its conditions pass
    Interrupt,           // Key, ends whatever is playing first
    Stop,
    Next,
    ResetProgress,
    EnableEntry,         // Key
    DisableEntry,        // Key
    ToggleEntry          // Key
}

[Serializable]
public class StoryVariable
{
    public string Name;
    public string Value;
}

/// <summary>A Button wired to a commander action without touching the OnClick list.</summary>
[Serializable]
public class StoryButtonBinding
{
    public Button Button;
    public StoryButtonAction Action = StoryButtonAction.Play;
    [Tooltip("Entry id / dialogue name for the *Key actions.")]
    public string Key;
    [Tooltip("Entry index for PlayIndex.")]
    public int Index;
    [Tooltip("Button is only interactable while the action can actually do something.")]
    public bool AutoInteractable = true;

    [NonSerialized] public UnityAction Listener;
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

    [Header("Conditions: Day (checked against PlayerData)")]
    [Min(0)] public int MinDay = 0;
    [Tooltip("-1 = no upper limit.")] public int MaxDay = -1;
    [Tooltip("Only available when Day is a multiple of this (e.g. 6 = day 0, 6, 12...). 0 = off.")]
    [Min(0)] public int DayEvery = 0;

    [Header("Conditions: Energy")]
    [Min(0)] public int MinEnergy = 0;
    [Tooltip("-1 = no upper limit.")] public int MaxEnergy = -1;
    [Tooltip("Needs at least this much energy, and it is spent when the story starts.")]
    [Min(0)] public int EnergyCost = 0;

    [Header("Conditions: Money")]
    public int MinMoney = 0;
    [Tooltip("-1 = no upper limit.")] public int MaxMoney = -1;
    [Tooltip("Needs at least this much money, and it is spent when the story starts.")]
    [Min(0)] public int MoneyCost = 0;

    [Header("Conditions: Progress")]
    [Tooltip("Total number of completed dialogues in the save needed.")]
    [Min(0)] public int MinCompletedDialogues = 0;
    public string[] RequireCompleted;
    public string[] RequireNotCompleted;

    [Header("Events")]
    public UnityEvent OnStarted;
    public UnityEvent OnEnded;

    /// <summary>Extra condition set from code: entry.Condition = () => hasKey;</summary>
    [NonSerialized] public Func<bool> Condition;
    [NonSerialized] public int PlayCount;

    public string Key => string.IsNullOrEmpty(Id) ? DialogueName : Id;

    public bool IsAvailable() => Check(false, out _);

    /// <summary>Null when available, otherwise a short text saying which condition failed.</summary>
    public string WhyUnavailable() => Check(true, out string reason) ? null : reason;

    private bool Check(bool wantReason, out string reason)
    {
        reason = null;

        if (!Enabled) { if (wantReason) reason = "disabled"; return false; }
        if (string.IsNullOrEmpty(DialogueName)) { if (wantReason) reason = "no dialogue name"; return false; }
        if (OncePerSession && PlayCount > 0) { if (wantReason) reason = "already played (OncePerSession)"; return false; }

        PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
        if (data != null)
        {
            if (data.Day < MinDay) { if (wantReason) reason = $"day {data.Day} < MinDay {MinDay}"; return false; }
            if (MaxDay >= 0 && data.Day > MaxDay) { if (wantReason) reason = $"day {data.Day} > MaxDay {MaxDay}"; return false; }
            if (DayEvery > 0 && data.Day % DayEvery != 0) { if (wantReason) reason = $"day {data.Day} is not a multiple of {DayEvery}"; return false; }

            if (data.Energy < MinEnergy) { if (wantReason) reason = $"energy {data.Energy} < MinEnergy {MinEnergy}"; return false; }
            if (MaxEnergy >= 0 && data.Energy > MaxEnergy) { if (wantReason) reason = $"energy {data.Energy} > MaxEnergy {MaxEnergy}"; return false; }
            if (data.Energy < EnergyCost) { if (wantReason) reason = $"energy {data.Energy} < cost {EnergyCost}"; return false; }

            if (data.Money < MinMoney) { if (wantReason) reason = $"money {data.Money} < MinMoney {MinMoney}"; return false; }
            if (MaxMoney >= 0 && data.Money > MaxMoney) { if (wantReason) reason = $"money {data.Money} > MaxMoney {MaxMoney}"; return false; }
            if (data.Money < MoneyCost) { if (wantReason) reason = $"money {data.Money} < cost {MoneyCost}"; return false; }

            if (data.CompletedCount < MinCompletedDialogues)
            {
                if (wantReason) reason = $"completed {data.CompletedCount} < {MinCompletedDialogues}";
                return false;
            }

            if (RequireCompleted != null)
                foreach (string n in RequireCompleted)
                    if (!string.IsNullOrEmpty(n) && !data.IsDialogueCompleted(n))
                    { if (wantReason) reason = $"requires '{n}' completed"; return false; }

            if (RequireNotCompleted != null)
                foreach (string n in RequireNotCompleted)
                    if (!string.IsNullOrEmpty(n) && data.IsDialogueCompleted(n))
                    { if (wantReason) reason = $"requires '{n}' NOT completed"; return false; }
        }

        if (Condition != null && !Condition()) { if (wantReason) reason = "code Condition returned false"; return false; }
        return true;
    }

    /// <summary>Spends EnergyCost / MoneyCost. Called by the commander when the story really starts.</summary>
    public void ApplyCosts()
    {
        if (EnergyCost <= 0 && MoneyCost <= 0) return;
        PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
        if (data == null) return;
        if (EnergyCost > 0) data.Energy = Mathf.Max(0, data.Energy - EnergyCost);
        if (MoneyCost > 0) data.Money = Mathf.Max(0, data.Money - MoneyCost);
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
/// Buttons (two ways):
///   1) Button OnClick -> drag the commander -> pick any "UI_..." method (UI_Play, UI_PlayKey(string), UI_Stop...).
///   2) Fill the "Button Bindings" list: Button + Action (+ Key / Index). No OnClick wiring needed.
///
/// Triggers (optional): on start, on enable, on trigger enter (3D/2D), on click (UI or physics raycaster).
/// Right-click the component header in Play mode -> Test/... to try everything.
/// </summary>
[DisallowMultipleComponent]
public class StoryCommander : MonoBehaviour, IPointerClickHandler
{
    // ------------- settings -------------
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
    [Tooltip("Sets {player}, {day}, {money} and {energy} from PlayerData.")]
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

    [Header("Button Bindings")]
    public List<StoryButtonBinding> ButtonBindings = new List<StoryButtonBinding>();

    [Header("Events")]
    public UnityEvent onStarted;
    public UnityEvent onEnded;
    public UnityEvent onNothingAvailable;

    [Header("Test (right-click the component header -> Test/...)")]
    public string TestKey = "";
    public string TestDialogueName = "Chapter 1";
    [Min(0f)] public float TestDelay = 2f;

    /// <summary>A story of this commander started. Entry is null for ad-hoc dialogues.</summary>
    public event Action<StoryEntry> Started;
    /// <summary>A story of this commander ended (finished, skipped or interrupted).</summary>
    public event Action<StoryEntry> Ended;
    public event Action NothingAvailable;

    // ------------- state -------------
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
    private bool _hasAutoButtons;
    private float _nextButtonRefresh;
    private StoryEntry _lastEntry;

    public StoryTeller Teller { get => teller != null ? teller : StoryTeller.Instance; set => teller = value; }
    /// <summary>Something of this commander is queued or playing.</summary>
    public bool IsActive => _active.Count > 0;
    /// <summary>Something of this commander is on screen right now.</summary>
    public bool IsPlaying => _active.Exists(a => a.Started);
    public int EntryCount => Entries.Count;
    /// <summary>The last entry this commander played (null if none / ad-hoc).</summary>
    public StoryEntry LastEntry => _lastEntry;

    public int AvailableCount
    {
        get { int n = 0; foreach (StoryEntry e in Entries) if (e != null && e.IsAvailable()) n++; return n; }
    }

    public bool HasAvailable => AvailableCount > 0;

    // ========================= Unity =========================

    private void OnEnable()
    {
        BindButtons();
        if (PlayOnEnable) StartCoroutine(AutoRoutine());
    }

    private void OnDisable()
    {
        UnbindButtons();
    }

    private void Start()
    {
        if (PlayOnStart) StartCoroutine(AutoRoutine());
    }

    private void Update()
    {
        if (!_hasAutoButtons || Time.unscaledTime < _nextButtonRefresh) return;
        _nextButtonRefresh = Time.unscaledTime + 0.25f;
        RefreshButtons();
    }

    private void OnDestroy()
    {
        UnbindButtons();
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

    // ========================= Play: by pick mode =========================

    /// <summary>Pick an entry by PickMode and play it. Returns false when nothing was started.</summary>
    public bool Play(Action onDone = null) => PlayPicked(PickMode, onDone);

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

    // ========================= Play: explicit =========================

    /// <summary>
    /// Play a specific entry by its Id / dialogue name. Conditions are ignored (you asked for it explicitly).
    /// If no entry has that key, the name is treated as a dialogue of the database.
    /// </summary>
    public bool Play(string key, Action onDone = null)
    {
        StoryEntry e = FindEntry(key);
        return e != null ? RunEntry(e, onDone, Busy) : PlayDialogue(key, null, 0, onDone);
    }

    /// <summary>Play an entry only if its conditions pass.</summary>
    public bool PlayIfAvailable(string key, Action onDone = null)
    {
        StoryEntry e = FindEntry(key);
        return e != null && e.IsAvailable() && RunEntry(e, onDone, Busy);
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

    /// <summary>Play the entry that was played last again (ignores conditions).</summary>
    public bool Replay(Action onDone = null) => _lastEntry != null && RunEntry(_lastEntry, onDone, Busy);

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

    // ========================= Control =========================

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

    // ========================= Button-friendly methods =========================
    // UnityEvent (Button OnClick, etc.) only lists public void methods with 0 or 1 parameter
    // of type string / int / float / bool / Object, so these are thin wrappers of the API above.

    public void UI_Play() => Play();
    public void UI_PlayNext() => PlayNext();
    public void UI_PlayRandom() => PlayRandom();
    public void UI_PlayFirstAvailable() => PlayFirstAvailable();
    public void UI_PlayHighestPriority() => PlayHighestPriority();
    public void UI_Replay() => Replay();

    /// <summary>Entry id or dialogue name. Ignores conditions.</summary>
    public void UI_PlayKey(string key) => Play(key);
    /// <summary>Entry id or dialogue name, only when its conditions pass.</summary>
    public void UI_PlayKeyIfAvailable(string key) => PlayIfAvailable(key);
    public void UI_PlayIndex(int entryIndex) => Play(entryIndex);
    /// <summary>A dialogue of the database, without an entry.</summary>
    public void UI_PlayDialogue(string dialogueName) => PlayDialogue(dialogueName);
    public void UI_Interrupt(string key) => Interrupt(key);
    public void UI_PlayAfterSeconds(float seconds) => PlayAfter(seconds);

    public void UI_Stop() => Stop();
    public void UI_Next() => Next();
    public void UI_ResetProgress() => ResetProgress();
    public void UI_ResetTrigger() => _triggered = false;

    public void UI_EnableEntry(string key) => SetEntryEnabled(key, true);
    public void UI_DisableEntry(string key) => SetEntryEnabled(key, false);
    public void UI_ToggleEntry(string key) => ToggleEntry(key);
    public void UI_EnableAllEntries() => SetAllEntriesEnabled(true);
    public void UI_DisableAllEntries() => SetAllEntriesEnabled(false);

    /// <summary>0 = Sequence, 1 = FirstAvailable, 2 = Priority, 3 = Random.</summary>
    public void UI_SetPickMode(int mode) => PickMode = (StoryPickMode)Mathf.Clamp(mode, 0, 3);
    /// <summary>0 = Stop, 1 = Loop, 2 = RepeatLast.</summary>
    public void UI_SetEndOfList(int mode) => EndOfList = (StoryEndOfList)Mathf.Clamp(mode, 0, 2);
    /// <summary>0 = Queue, 1 = Ignore, 2 = Interrupt.</summary>
    public void UI_SetBusyBehaviour(int mode) => Busy = (StoryBusyBehaviour)Mathf.Clamp(mode, 0, 2);
    public void UI_SetCooldown(float seconds) => Cooldown = Mathf.Max(0f, seconds);

    /// <summary>One text field "name=value", e.g. "npc=Ann". Applied before every play.</summary>
    public void UI_SetVariable(string pair)
    {
        if (string.IsNullOrEmpty(pair)) return;
        int i = pair.IndexOf('=');
        if (i <= 0) return;
        SetVariable(pair.Substring(0, i).Trim(), pair.Substring(i + 1).Trim());
    }

    public void UI_ClearVariables() => ClearVariables();

    // ========================= Button bindings =========================

    private void BindButtons()
    {
        _hasAutoButtons = false;
        foreach (StoryButtonBinding b in ButtonBindings)
        {
            if (b == null || b.Button == null) continue;
            StoryButtonBinding captured = b;
            if (b.Listener != null) b.Button.onClick.RemoveListener(b.Listener);
            b.Listener = () => ExecuteBinding(captured);
            b.Button.onClick.AddListener(b.Listener);
            if (b.AutoInteractable) _hasAutoButtons = true;
        }
        RefreshButtons();
    }

    private void UnbindButtons()
    {
        foreach (StoryButtonBinding b in ButtonBindings)
        {
            if (b == null || b.Button == null || b.Listener == null) continue;
            b.Button.onClick.RemoveListener(b.Listener);
            b.Listener = null;
        }
        _hasAutoButtons = false;
    }

    private void ExecuteBinding(StoryButtonBinding b)
    {
        switch (b.Action)
        {
            case StoryButtonAction.Play: Play(); break;
            case StoryButtonAction.PlayNext: PlayNext(); break;
            case StoryButtonAction.PlayRandom: PlayRandom(); break;
            case StoryButtonAction.PlayFirstAvailable: PlayFirstAvailable(); break;
            case StoryButtonAction.PlayHighestPriority: PlayHighestPriority(); break;
            case StoryButtonAction.PlayKey: Play(b.Key); break;
            case StoryButtonAction.PlayIndex: Play(b.Index); break;
            case StoryButtonAction.PlayKeyIfAvailable: PlayIfAvailable(b.Key); break;
            case StoryButtonAction.Interrupt: Interrupt(b.Key); break;
            case StoryButtonAction.Stop: Stop(); break;
            case StoryButtonAction.Next: Next(); break;
            case StoryButtonAction.ResetProgress: ResetProgress(); break;
            case StoryButtonAction.EnableEntry: SetEntryEnabled(b.Key, true); break;
            case StoryButtonAction.DisableEntry: SetEntryEnabled(b.Key, false); break;
            case StoryButtonAction.ToggleEntry: ToggleEntry(b.Key); break;
        }
        RefreshButtons();
    }

    /// <summary>Updates Button.interactable of the bindings that have AutoInteractable.</summary>
    public void RefreshButtons()
    {
        foreach (StoryButtonBinding b in ButtonBindings)
        {
            if (b == null || b.Button == null || !b.AutoInteractable) continue;
            b.Button.interactable = CanRun(b);
        }
    }

    private bool CanRun(StoryButtonBinding b)
    {
        switch (b.Action)
        {
            case StoryButtonAction.Play:
            case StoryButtonAction.PlayNext:
            case StoryButtonAction.PlayRandom:
            case StoryButtonAction.PlayFirstAvailable:
            case StoryButtonAction.PlayHighestPriority:
                return HasAvailable || !string.IsNullOrEmpty(NothingAvailableDialogue);

            case StoryButtonAction.PlayKeyIfAvailable:
                return IsAvailable(b.Key);

            case StoryButtonAction.PlayKey:
            case StoryButtonAction.Interrupt:
            case StoryButtonAction.EnableEntry:
            case StoryButtonAction.DisableEntry:
            case StoryButtonAction.ToggleEntry:
                return !string.IsNullOrEmpty(b.Key);

            case StoryButtonAction.PlayIndex:
                return b.Index >= 0 && b.Index < Entries.Count && Entries[b.Index] != null;

            case StoryButtonAction.Stop:
            case StoryButtonAction.Next:
                return IsPlaying;

            default:
                return true;
        }
    }

    // ========================= Entries =========================

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
                               int priority = 0, bool oncePerSession = false,
                               int minEnergy = 0, int minMoney = 0, int energyCost = 0, int moneyCost = 0)
    {
        var e = new StoryEntry
        {
            DialogueName = dialogueName,
            Id = id,
            MinDay = minDay,
            MaxDay = maxDay,
            Priority = priority,
            OncePerSession = oncePerSession,
            MinEnergy = minEnergy,
            MinMoney = minMoney,
            EnergyCost = energyCost,
            MoneyCost = moneyCost
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

    public bool ToggleEntry(string key)
    {
        StoryEntry e = FindEntry(key);
        if (e == null) return false;
        e.Enabled = !e.Enabled;
        return true;
    }

    public void SetAllEntriesEnabled(bool enabled)
    {
        foreach (StoryEntry e in Entries) if (e != null) e.Enabled = enabled;
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

    // ========================= Variables =========================

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
                t.SetVariable("energy", data.Energy);
                t.SetVariable("maxenergy", data.MaxEnergy);
            }
        }
        foreach (StoryVariable v in Variables)
            if (v != null && !string.IsNullOrEmpty(v.Name)) t.SetVariable(v.Name, v.Value);
        foreach (var kv in _runtimeVars) t.SetVariable(kv.Key, kv.Value);
    }

    // ========================= Picking =========================

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

    // ========================= Running =========================

    private bool RunEntry(StoryEntry e, Action onDone, StoryBusyBehaviour busy)
    {
        if (e == null) return false;
        StoryTeller t = Teller;
        if (t == null || t.dialoguesDatabase == null || !t.dialoguesDatabase.TryGet(e.DialogueName, out Dialogues d))
        {
            Debug.LogWarning($"[StoryCommander] Dialogue '{e.DialogueName}' not found.", this);
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
        if (entry != null)
        {
            entry.PlayCount++;
            entry.ApplyCosts();
            _lastEntry = entry;
        }
        RefreshButtons();
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

    // ========================= StoryTeller hooks =========================

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
        RefreshButtons();
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
        RefreshButtons();
    }

    // ========================= Context menu tests =========================

    #region Context Menu Tests
    private bool TestPlaying()
    {
        if (Application.isPlaying) return true;
        Debug.LogWarning("[StoryCommander/Test] Enter Play mode first.", this);
        return false;
    }

    private static void TLog(string m) => Debug.Log("[StoryCommander/Test] " + m);

    [ContextMenu("Test/Play (by PickMode)")]
    private void TestPlay() { if (TestPlaying()) TLog($"Play ({PickMode}) -> {Play(() => TLog("   done"))}"); }

    [ContextMenu("Test/Play/Next in sequence")]
    private void TestNextInSequence() { if (TestPlaying()) TLog($"PlayNext -> {PlayNext()}"); }

    [ContextMenu("Test/Play/Random")]
    private void TestRandom() { if (TestPlaying()) TLog($"PlayRandom -> {PlayRandom()}"); }

    [ContextMenu("Test/Play/First available")]
    private void TestFirst() { if (TestPlaying()) TLog($"PlayFirstAvailable -> {PlayFirstAvailable()}"); }

    [ContextMenu("Test/Play/Highest priority")]
    private void TestPriority() { if (TestPlaying()) TLog($"PlayHighestPriority -> {PlayHighestPriority()}"); }

    [ContextMenu("Test/Play/By TestKey")]
    private void TestByKey() { if (TestPlaying()) TLog($"Play('{TestKey}') -> {Play(TestKey)}"); }

    [ContextMenu("Test/Play/By TestKey (only if available)")]
    private void TestByKeyIfAvailable() { if (TestPlaying()) TLog($"PlayIfAvailable('{TestKey}') -> {PlayIfAvailable(TestKey)}"); }

    [ContextMenu("Test/Play/Interrupt with TestKey")]
    private void TestInterruptKey() { if (TestPlaying()) TLog($"Interrupt('{TestKey}') -> {Interrupt(TestKey)}"); }

    [ContextMenu("Test/Play/Database dialogue (TestDialogueName)")]
    private void TestRaw() { if (TestPlaying()) TLog($"PlayDialogue('{TestDialogueName}') -> {PlayDialogue(TestDialogueName)}"); }

    [ContextMenu("Test/Play/Replay last")]
    private void TestReplay() { if (TestPlaying()) TLog($"Replay -> {Replay()}"); }

    [ContextMenu("Test/Play/After TestDelay")]
    private void TestAfter() { if (!TestPlaying()) return; PlayAfter(TestDelay); TLog($"Play() in {TestDelay}s"); }

    [ContextMenu("Test/Play/Runtime demo (built in code)")]
    private void TestDemo()
    {
        if (!TestPlaying()) return;
        Translator.Set("commander.demo.1", "Hello {player}! This story was built by a StoryCommander.");
        Translator.Set("commander.demo.2", "Today is day {day}. You have {money} coins and {energy} energy.");
        TLog($"PlayLines -> {PlayLines("Commander.Demo", ("Ann", "commander.demo.1"), ("Ann", "commander.demo.2"))}");
    }

    [ContextMenu("Test/Control/Stop")]
    private void TestStop() { if (TestPlaying()) Stop(); }

    [ContextMenu("Test/Control/Next")]
    private void TestNext() { if (TestPlaying()) TLog($"Next -> {Next()}"); }

    [ContextMenu("Test/Control/Reset progress")]
    private void TestReset() { ResetProgress(); TLog("Progress reset."); }

    [ContextMenu("Test/Control/Refresh buttons")]
    private void TestRefreshButtons() { RefreshButtons(); TLog("Buttons refreshed."); }

    [ContextMenu("Test/Info/List entries")]
    private void TestList()
    {
        if (!TestPlaying()) return;
        TLog($"{Entries.Count} entries, {AvailableCount} available, {CompletedEntryCount} completed in the save:");
        for (int i = 0; i < Entries.Count; i++)
        {
            StoryEntry e = Entries[i];
            if (e == null) continue;
            string why = e.WhyUnavailable();
            TLog($"   [{i}] {e}  available: {why == null}{(why != null ? " (" + why + ")" : "")}  played: {e.PlayCount}");
        }
    }

    [ContextMenu("Test/Info/Log state")]
    private void TestState()
    {
        if (!TestPlaying()) return;
        PlayerData data = PlayerManager.Instance != null ? PlayerManager.Instance.Data : null;
        string player = data != null ? $"Day {data.Day}, Energy {data.Energy}/{data.MaxEnergy}, Money {data.Money}" : "no PlayerData";
        TLog($"Mode: {PickMode}/{EndOfList}  Busy: {Busy}  Cursor: {_cursor}  Active: {_active.Count}  Playing: {IsPlaying}  " +
             $"Teller: {(Teller != null ? "ok" : "missing")}  Player: {player}");
    }
    #endregion
}