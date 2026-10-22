using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.UI;

/// <summary>
/// Plays Dialogues on a canvas: typewriter, auto-play, variables, translator support, timeline (director) pause/resume.
///
///   StoryTeller.Play("Chapter 1");                                  // from the assigned database
///   StoryTeller.Instance.StartDialogue("Chapter 1", director);      // pauses the timeline, resumes it when finished
///   StoryTeller.Instance.Next();                                    // hook to a button / input
///   StoryTeller.Instance.SetVariable("player", "Ann");              // "Hello {player}" in the language files
///   StoryTeller.Instance.StartDialogue(new Dialogues("Tmp").Add("NPC", "npc.hello"));   // built in code
///
/// Timeline: a script on the director's object calls StoryTeller.Instance.RegisterDirector(director) (or assign
/// 'playableDirector'), then a Signal Receiver calls StartRegistered("Chapter 1"). The director is paused while
/// the dialogue plays and resumed in End().
/// </summary>
public class StoryTeller : MonoBehaviour
{
    public static StoryTeller Instance { get; private set; }

    [Header("Settings")]
    [Tooltip("When false, Next() is ignored and auto-play waits (cutscene lock, pause menu...).")]
    public bool IsReadingAllowed = true;
    [Tooltip("Typewriter speed. <= 0 shows the whole line at once.")]
    public float CharsPerSecond = 40f;
    [Tooltip("Next() while typing first completes the line, the following call goes to the next line.")]
    public bool CompleteLineBeforeNext = true;
    public bool AutoPlay;
    public float AutoPlayDelay = 1.5f;
    public bool HideCanvasWhenIdle = true;

    [Header("Story")]
    public DialoguesDatabase dialoguesDatabase;
    [Tooltip("Director of the cutscene that wants to talk. Used (once) by the next StartDialogue without an explicit director.")]
    public PlayableDirector playableDirector;
    [Tooltip("When a dialogue that is already completed is started again, this one plays instead (if it exists).")]
    public bool FallbackWhenCompleted = true;
    public string CompletedFallbackName = "dia.completed0";

    [Header("Visual")]
    public Canvas StoryCanvas;
    public Image SpeakerImage;
    public TextMeshProUGUI SpeakerName;   // optional
    public TextMeshProUGUI Content;
    public Image Background;              // optional, changed by MainStoryDialoguesAsset
    public Button AdvanceButton;          // optional, calls Next()

    [Header("Audio")]
    [Tooltip("Voice lines go through AudioManager.PlayVoice (ducks the music) when an AudioManager exists.")]
    public bool UseAudioManagerVoice = true;
    public AudioSource VoiceSource;       // optional, fallback for voice lines
    [Tooltip("Optional fallback for text/next sounds when there is no AudioManager in the scene.")]
    public AudioSource SoundSource;

    [Header("Text Sound (played while characters appear)")]
    public bool TextSoundEnabled = true;
    [Tooltip("One or more blips; a random one is used each time. A line's own TextSound overrides this.")]
    public AudioClip[] TextSounds;
    [Range(0f, 1f)] public float TextSoundVolume = 0.6f;
    public float TextSoundPitch = 1f;
    [Tooltip("Random ± pitch variation, keeps the blip from sounding robotic.")]
    public float TextSoundPitchRandom = 0.08f;
    [Min(1), Tooltip("Play on every Nth visible character. 1 = every character.")]
    public int TextSoundEveryChars = 1;
    [Min(0f), Tooltip("Minimum seconds between two blips. 0 = never skip.")]
    public float TextSoundMinInterval = 0f;
    [Tooltip("Also play for spaces / punctuation-only slots (every revealed character).")]
    public bool TextSoundOnSpaces = false;

    [Header("Next Sound (played when the player advances)")]
    public bool NextSoundEnabled = true;
    public AudioClip NextSound;
    [Range(0f, 1f)] public float NextSoundVolume = 1f;
    public float NextSoundPitch = 1f;
    public float NextSoundPitchRandom = 0f;
    [Tooltip("Also play it when a line advances by itself (AutoPlay / AutoAdvanceDelay).")]
    public bool PlayNextSoundOnAutoAdvance = false;


    [Header("Events")]
    public UnityEvent onDialogueStarted;
    public UnityEvent onDialogueEnded;

    [Header("Test (right-click the component header → Test/...)")]
    public string TestDialogueName = "Chapter 1";
    public string TestNamePrefix = "Ch";
    [Min(0)] public int TestLineIndex = 0;
    public string TestSpeaker = "Human";
    public string TestSearchText = "hello";
    public bool TestSearchTranslated = true;
    public string TestVariableName = "player";
    public string TestVariableValue = "Ann";
    [Tooltip("Optional: a PlayableDirector to pause/resume while the test dialogue plays.")]
    public PlayableDirector TestDirector;

    /// <summary>Dialogue started.</summary>
    public event Action<Dialogues> DialogueStarted;
    /// <summary>Dialogue ended (before the director is resumed).</summary>
    public event Action<Dialogues> DialogueEnded;
    /// <summary>A line is shown (before typing). Args: line, index.</summary>
    public event Action<DialoguesAsset, int> LineShown;
    /// <summary>The line text is fully visible.</summary>
    public event Action<DialoguesAsset> LineTyped;

    // ───────────── state ─────────────
    private struct Pending { public Dialogues dialogues; public PlayableDirector director; public int start; }

    private readonly Queue<Pending> _queue = new Queue<Pending>();
    private readonly Dictionary<string, object> _vars = new Dictionary<string, object>();
    private Dialogues _dialogues;
    private DialoguesAsset _currentLine;
    private PlayableDirector _director;
    private bool _directorPausedByUs;
    private Coroutine _routine;
    private int _index = -1;
    private int _blipCounter;
    private float _lastBlipTime = -999f;
    private bool _voiceFromManager;

    public bool IsPlaying { get; private set; }
    public bool IsTyping { get; private set; }
    public Dialogues CurrentDialogues => _dialogues;
    public DialoguesAsset CurrentLine => _currentLine;
    public int CurrentIndex => _index;
    public int LineCount => _dialogues != null ? _dialogues.Count : 0;
    public bool IsLastLine => IsPlaying && _index >= LineCount - 1;
    public bool HasNext => IsPlaying && _index < LineCount - 1;
    public int QueuedCount => _queue.Count;
    public string CurrentSpeaker => _currentLine != null ? ResolveSpeaker(_currentLine) : "";
    public string CurrentText => _currentLine != null ? ResolveText(_currentLine) : "";

    #region Unity
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (AdvanceButton != null) AdvanceButton.onClick.AddListener(OnAdvanceClicked);
        SetVisible(!HideCanvasWhenIdle);
    }

    private void OnEnable() => Translator.OnTranslationsChanged += OnTranslationsChanged;
    private void OnDisable() => Translator.OnTranslationsChanged -= OnTranslationsChanged;

    private void OnDestroy()
    {
        if (AdvanceButton != null) AdvanceButton.onClick.RemoveListener(OnAdvanceClicked);
        if (Instance == this) Instance = null;
    }

    private void OnAdvanceClicked() => Next();
    #endregion

    // ═════════════════════════ Start ═════════════════════════

    /// <summary>Static shortcut: StoryTeller.Play("Chapter 1").</summary>
    public static bool Play(string dialogueName, PlayableDirector director = null)
        => Instance != null && Instance.StartDialogue(dialogueName, director);

    /// <summary>
    /// Start a dialogue from the database. If one is already playing it is queued, unless 'interrupt' is true
    /// (the running one then ends immediately). Returns false when the dialogue can't be found / is empty.
    /// </summary>
    public bool StartDialogue(string dialogueName, PlayableDirector director = null, int startIndex = 0, bool interrupt = false)
    {
        if (dialoguesDatabase == null) { Debug.LogWarning("[StoryTeller] No DialoguesDatabase assigned.", this); return false; }
        if (!dialoguesDatabase.TryGet(dialogueName, out Dialogues d))
        {
            Debug.LogWarning($"[StoryTeller] Dialogue '{dialogueName}' not found.", this);
            return false;
        }
        return StartDialogue(d, director, startIndex, interrupt);
    }

    public bool StartDialogue(int databaseIndex, PlayableDirector director = null, int startIndex = 0, bool interrupt = false)
    {
        Dialogues d = dialoguesDatabase != null ? dialoguesDatabase[databaseIndex] : null;
        if (d == null) { Debug.LogWarning($"[StoryTeller] No dialogue at index {databaseIndex}.", this); return false; }
        return StartDialogue(d, director, startIndex, interrupt);
    }

    /// <summary>Start any Dialogues object (also ones built in code, not in the database).</summary>
    public bool StartDialogue(Dialogues dialogues, PlayableDirector director = null, int startIndex = 0, bool interrupt = false)
    {
        if (dialogues == null || dialogues.Count == 0)
        {
            Debug.LogWarning("[StoryTeller] Dialogue is null or empty.", this);
            return false;
        }

        if (director == null) { director = playableDirector; playableDirector = null; } // registered director is used once

        if (IsPlaying)
        {
            if (!interrupt)
            {
                _queue.Enqueue(new Pending { dialogues = dialogues, director = director, start = startIndex });
                return true;
            }
            EndInternal(true, false);
        }
            if (FallbackWhenCompleted && dialogues.IsCompleted
        && dialogues.DialogueName != CompletedFallbackName
        && dialoguesDatabase != null
        && dialoguesDatabase.TryGet(CompletedFallbackName, out Dialogues fallback)
        && fallback.Count > 0)
            {
                dialogues = fallback;
                startIndex = 0;
            }

        _dialogues = dialogues;
        _director = director;
        IsPlaying = true;

        if (_director != null && _director.playableGraph.IsValid() && _director.state == PlayState.Playing)
        {
            _director.Pause();
            _directorPausedByUs = true;
        }

        SetVisible(true);
        DialogueStarted?.Invoke(_dialogues);
        onDialogueStarted?.Invoke();

        ShowLine(Mathf.Clamp(startIndex, 0, dialogues.Count - 1));
        return true;
    }

    /// <summary>For Timeline Signal Receivers: start a database dialogue with the director registered by RegisterDirector.</summary>
    public void StartRegistered(string dialogueName) => StartDialogue(dialogueName);

    public void RegisterDirector(PlayableDirector director) => playableDirector = director;
    public void UnregisterDirector(PlayableDirector director) { if (playableDirector == director) playableDirector = null; }

    public void SetDatabase(DialoguesDatabase database) => dialoguesDatabase = database;

    // ═════════════════════════ Search (thin wrappers over DialoguesDatabase) ═════════════════════════

    public bool HasDialogue(string dialogueName) => dialoguesDatabase != null && dialoguesDatabase.Contains(dialogueName);

    public Dialogues GetDialogue(string dialogueName) => dialoguesDatabase != null ? dialoguesDatabase[dialogueName] : null;

    /// <summary>Name prefix search, ignore case: FindDialogues("Ch1.") → all "Ch1.*" dialogues, sorted.</summary>
    public List<Dialogues> FindDialogues(string namePrefix)
        => dialoguesDatabase != null ? dialoguesDatabase.FindByPrefix(namePrefix) : new List<Dialogues>();

    public List<Dialogues> FindDialoguesBySpeaker(string speaker)
        => dialoguesDatabase != null ? dialoguesDatabase.FindBySpeaker(speaker) : new List<Dialogues>();

    /// <summary>Text search over speaker / key and, with translated = true, the text the player reads in the current language.</summary>
    public List<DialogueLineRef> SearchLines(string text, bool translated = true)
        => dialoguesDatabase != null ? dialoguesDatabase.SearchLines(text, translated) : new List<DialogueLineRef>();

    public List<DialogueLineRef> FindLines(Func<DialoguesAsset, bool> match)
        => dialoguesDatabase != null ? dialoguesDatabase.FindLines(match) : new List<DialogueLineRef>();

    /// <summary>Start the dialogue of a search result at that exact line.</summary>
    public bool StartDialogueAt(DialogueLineRef lineRef, PlayableDirector director = null, bool interrupt = false)
        => lineRef.Dialogues != null && StartDialogue(lineRef.Dialogues, director, lineRef.LineIndex, interrupt);

    /// <summary>Start the first dialogue (by name order) whose name starts with the prefix.</summary>
    public bool StartFirstWithPrefix(string namePrefix, PlayableDirector director = null, bool interrupt = false)
    {
        Dialogues d = dialoguesDatabase != null ? dialoguesDatabase.FindFirstByPrefix(namePrefix) : null;
        return d != null && StartDialogue(d, director, 0, interrupt);
    }

    /// <summary>True while the dialogue with this name is the one playing.</summary>
    public bool IsPlayingDialogue(string dialogueName)
        => IsPlaying && _dialogues != null && _dialogues.DialogueName == dialogueName;

    // ═════════════════════════ Navigate ═════════════════════════

    /// <summary>
    /// Advance. While typing it completes the line first (CompleteLineBeforeNext). Past the last line the dialogue ends
    /// and the director resumes. Returns true while the dialogue is still running.
    /// </summary>
    public bool Next() => Next(CompleteLineBeforeNext);

    public bool Next(bool completeLineFirst) => Advance(completeLineFirst, true);

    /// <summary>Next() without the next sound (e.g. for scripted advances).</summary>
    public bool NextSilently() => Advance(CompleteLineBeforeNext, false);

    private bool Advance(bool completeLineFirst, bool withSound)
    {
        if (!IsPlaying || !IsReadingAllowed) return IsPlaying;
        if (withSound) PlayNextSound(_currentLine);
        if (IsTyping && completeLineFirst) { SkipTyping(); return true; }
        return GoTo(_index + 1);
    }

    public bool Previous() => IsPlaying && _index > 0 && GoTo(_index - 1);

    /// <summary>Jump to a line. Index past the end ends the dialogue (returns false).</summary>
    public bool GoTo(int index)
    {
        if (!IsPlaying) return false;
        if (index >= LineCount) { End(); return false; }
        ShowLine(Mathf.Max(0, index));
        return IsPlaying;
    }

    /// <summary>Reveal the whole current line immediately.</summary>
    public void SkipTyping()
    {
        if (!IsPlaying || !IsTyping) return;
        StopRoutine();
        FinishTyping(_currentLine);
    }

    /// <summary>Skip the whole dialogue (cutscene skip). The director still resumes.</summary>
    public void Skip() => End();

    /// <summary>End the current dialogue; queued dialogues start afterwards.</summary>
    public void End(bool resumeDirector = true) => EndInternal(resumeDirector, true);

    /// <summary>Drop everything: current dialogue (director resumes) and the queue.</summary>
    public void Stop()
    {
        _queue.Clear();
        End();
    }

    private void EndInternal(bool resumeDirector, bool startQueued)
    {
        if (!IsPlaying) return;

        StopRoutine();
        ExitCurrentLine();
        StopLineVoice();

        Dialogues finished = _dialogues;
        PlayableDirector dir = _director;
        bool paused = _directorPausedByUs;

        IsPlaying = false;
        IsTyping = false;
        _dialogues = null;
        _director = null;
        _directorPausedByUs = false;
        _index = -1;

        if (HideCanvasWhenIdle && !(startQueued && _queue.Count > 0)) SetVisible(false);

        DialogueEnded?.Invoke(finished);
        onDialogueEnded?.Invoke();

        if (resumeDirector && dir != null && paused && dir.playableGraph.IsValid()) dir.Resume();

        if (startQueued && _queue.Count > 0)
        {
            Pending p = _queue.Dequeue();
            StartDialogue(p.dialogues, p.director, p.start);
        }
        // Natural end or Skip() counts as completed; an interrupt does not.
        if (startQueued && finished != null && finished.DialogueName != CompletedFallbackName)
            PlayerManager.Instance?.CompleteDialogue(finished.DialogueName);
    }

    // ═════════════════════════ Variables ═════════════════════════

    /// <summary>"Hello {player}" → SetVariable("player","Ann"). Applied to speaker and text of every line.</summary>
    public void SetVariable(string name, object value)
    {
        if (string.IsNullOrEmpty(name)) return;
        _vars[name] = value;
        RefreshCurrentLine();
    }

    public bool TryGetVariable(string name, out object value) => _vars.TryGetValue(name ?? "", out value);
    public bool RemoveVariable(string name) { bool r = _vars.Remove(name ?? ""); if (r) RefreshCurrentLine(); return r; }
    public void ClearVariables() { _vars.Clear(); RefreshCurrentLine(); }

    // ═════════════════════════ Visual helpers (also used by DialoguesAsset subclasses) ═════════════════════════

    public void SetBackground(Sprite sprite, Color _color)
    {
        if (Background == null) return;
        Background.sprite = sprite;
        Background.color = _color;
        Background.enabled = sprite != null;
    }

    public void SetSpeakerImage(Sprite sprite)
    {
        if (SpeakerImage == null) return;
        SpeakerImage.sprite = sprite;
        SpeakerImage.enabled = sprite != null;
    }

    public void SetVisible(bool visible)
    {
        if (StoryCanvas != null) StoryCanvas.enabled = visible; // not SetActive: the canvas may share this object
    }

    // ═════════════════════════ Sounds ═════════════════════════

    public void SetTextSounds(params AudioClip[] clips) => TextSounds = clips;
    public void SetNextSound(AudioClip clip) => NextSound = clip;
    public void SetTextSoundEnabled(bool enabled) => TextSoundEnabled = enabled;
    public void SetNextSoundEnabled(bool enabled) => NextSoundEnabled = enabled;

    /// <summary>Play one text blip now (preview in a settings menu, custom typing effects...). Line may be null.</summary>
    public void PlayTextSound(DialoguesAsset line = null)
    {
        AudioClip clip = ResolveTextSound(line);
        PlaySound(clip, TextSoundVolume, TextSoundPitch, TextSoundPitchRandom);
    }

    /// <summary>Play the next sound now (the line's own NextSound wins over the default). Line may be null.</summary>
    public void PlayNextSound(DialoguesAsset line = null)
    {
        if (!NextSoundEnabled) return;
        AudioClip clip = line != null ? line.GetNextSound() : null;
        if (clip == null) clip = NextSound;
        PlaySound(clip, NextSoundVolume, NextSoundPitch, NextSoundPitchRandom);
    }

    private AudioClip ResolveTextSound(DialoguesAsset line)
    {
        AudioClip clip = line != null ? line.GetTextSound() : null;
        if (clip != null) return clip;
        return TextSounds != null && TextSounds.Length > 0 ? TextSounds[RNG.GetInt(0, TextSounds.Length)] : null;
    }

    private void TryPlayTextSound(DialoguesAsset line, int charIndex)
    {
        if (!TextSoundEnabled || line == null || line.MuteTextSound) return;

        var infos = Content.textInfo.characterInfo;
        if (charIndex < 0 || charIndex >= infos.Length) return;
        if (!TextSoundOnSpaces && !infos[charIndex].isVisible) return;

        _blipCounter++;
        if (TextSoundEveryChars > 1 && _blipCounter % TextSoundEveryChars != 0) return;
        if (TextSoundMinInterval > 0f && Time.unscaledTime - _lastBlipTime < TextSoundMinInterval) return;

        AudioClip clip = ResolveTextSound(line);
        if (clip == null) return;

        _lastBlipTime = Time.unscaledTime;
        PlaySound(clip, TextSoundVolume, TextSoundPitch, TextSoundPitchRandom);
    }

    private void PlaySound(AudioClip clip, float volume, float pitch, float pitchRandom)
    {
        if (clip == null) return;

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(clip, volume, pitch, pitchRandom);
        }
        else if (SoundSource != null)
        {
            SoundSource.pitch = pitchRandom > 0f ? pitch + RNG.GetFloat(-pitchRandom, pitchRandom) : pitch;
            SoundSource.PlayOneShot(clip, volume);
        }
    }

    private void PlayLineVoice(DialoguesAsset line)
    {
        StopLineVoice();
        if (line.Voice == null) return;

        if (UseAudioManagerVoice && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayVoice(line.Voice, true);   // interrupts the previous line, ducks the music
            _voiceFromManager = true;
        }
        else if (VoiceSource != null)
        {
            VoiceSource.clip = line.Voice;
            VoiceSource.Play();
        }
    }

    private void StopLineVoice()
    {
        if (_voiceFromManager && AudioManager.Instance != null) AudioManager.Instance.StopVoice();
        _voiceFromManager = false;
        if (VoiceSource != null) VoiceSource.Stop();
    }

    // ═════════════════════════ Context menu tests ═════════════════════════
    // Right-click the StoryTeller component header (or the ⋮ button) in Play mode.
    // Edit the "Test" fields in the inspector to change what the tests use.

    #region Context Menu Tests

    private bool TestPlaying()
    {
        if (Application.isPlaying) return true;
        Debug.LogWarning("[StoryTeller/Test] Enter Play mode first.", this);
        return false;
    }

    private bool TestDb()
    {
        if (dialoguesDatabase != null) return true;
        Debug.LogWarning("[StoryTeller/Test] No DialoguesDatabase assigned.", this);
        return false;
    }

    private static void TLog(string message) => Debug.Log("[StoryTeller/Test] " + message);

    // ───────────── start ─────────────

    [ContextMenu("Test/Start/By TestDialogueName")]
    private void TestStartByName() { if (TestPlaying()) TLog($"Start '{TestDialogueName}' → {StartDialogue(TestDialogueName)}"); }

    [ContextMenu("Test/Start/First dialogue in database")]
    private void TestStartFirst() { if (TestPlaying()) TLog($"Start index 0 → {StartDialogue(0)}"); }

    [ContextMenu("Test/Start/First with TestNamePrefix")]
    private void TestStartPrefix() { if (TestPlaying()) TLog($"Start prefix '{TestNamePrefix}' → {StartFirstWithPrefix(TestNamePrefix)}"); }

    [ContextMenu("Test/Start/At TestLineIndex")]
    private void TestStartAtLine() { if (TestPlaying()) TLog($"Start '{TestDialogueName}' at line {TestLineIndex} → {StartDialogue(TestDialogueName, null, TestLineIndex)}"); }

    [ContextMenu("Test/Start/Interrupt current")]
    private void TestStartInterrupt() { if (TestPlaying()) TLog($"Start '{TestDialogueName}' with interrupt → {StartDialogue(TestDialogueName, null, 0, true)}"); }

    [ContextMenu("Test/Start/Queue it twice")]
    private void TestStartQueue()
    {
        if (!TestPlaying()) return;
        StartDialogue(TestDialogueName);
        StartDialogue(TestDialogueName);
        TLog($"Started + queued '{TestDialogueName}'. Queued = {QueuedCount}");
    }

    [ContextMenu("Test/Start/With TestDirector (pause/resume timeline)")]
    private void TestStartWithDirector()
    {
        if (!TestPlaying()) return;
        if (TestDirector == null) { Debug.LogWarning("[StoryTeller/Test] Assign TestDirector first.", this); return; }
        TestDirector.Play();
        TLog($"Director playing, start '{TestDialogueName}' with it → {StartDialogue(TestDialogueName, TestDirector)}. It resumes when the dialogue ends.");
    }

    [ContextMenu("Test/Start/First search result of TestSearchText")]
    private void TestStartFromSearch()
    {
        if (!TestPlaying() || !TestDb()) return;
        var r = SearchLines(TestSearchText, TestSearchTranslated);
        if (r.Count == 0) { TLog($"No line matches '{TestSearchText}'."); return; }
        TLog($"Start at {r[0]} → {StartDialogueAt(r[0])}");
    }

    [ContextMenu("Test/Start/Runtime demo (built in code, no database needed)")]
    private void TestStartRuntimeDemo()
    {
        if (!TestPlaying()) return;

        // temporary translations (kept until the language is switched)
        Translator.Set("test.demo.1", "Hello {player}! This dialogue was built in code.");
        Translator.Set("test.demo.2", "This line is long on purpose so you can test the typewriter, the text sound and the Next sound. Press Next while it is typing to complete it first.");
        Translator.Set("test.demo.3", "(This line moves on by itself after 1.5 seconds.)");
        Translator.Set("test.demo.4", "Narration: the text sound is muted on this line.");
        Translator.Set("test.demo.5", "Last line. Next ends the dialogue and resumes the director, if any.");

        if (!TryGetVariable("player", out _)) SetVariable("player", string.IsNullOrEmpty(TestVariableValue) ? "Ann" : TestVariableValue);

        var mute = DialoguesAsset.Create("Narrator", "test.demo.4");
        mute.MuteTextSound = true;

        var demo = new Dialogues("Test.Runtime");
        demo.Add("Ann", "test.demo.1");
        demo.Add("Ann", "test.demo.2");
        demo.Add(DialoguesAsset.Create("System", "test.demo.3", null, 1.5f));
        demo.Add(mute);
        demo.Add(new MainStoryDialoguesAsset { Speaker = "Ann", Content = "test.demo.5" });

        TLog($"Start runtime demo → {StartDialogue(demo, TestDirector)}");
    }

    // ───────────── navigate ─────────────

    [ContextMenu("Test/Navigate/Next")]
    private void TestNext() { if (TestPlaying()) TLog($"Next → still playing: {Next()}"); }

    [ContextMenu("Test/Navigate/Next (no sound)")]
    private void TestNextSilently() { if (TestPlaying()) TLog($"NextSilently → still playing: {NextSilently()}"); }

    [ContextMenu("Test/Navigate/Previous")]
    private void TestPrevious() { if (TestPlaying()) TLog($"Previous → {Previous()}"); }

    [ContextMenu("Test/Navigate/Go to TestLineIndex")]
    private void TestGoTo() { if (TestPlaying()) TLog($"GoTo({TestLineIndex}) → {GoTo(TestLineIndex)}"); }

    [ContextMenu("Test/Navigate/Skip typing")]
    private void TestSkipTyping() { if (TestPlaying()) SkipTyping(); }

    [ContextMenu("Test/Navigate/Skip dialogue (End)")]
    private void TestSkip() { if (TestPlaying()) Skip(); }

    [ContextMenu("Test/Navigate/Stop (end + clear queue)")]
    private void TestStop() { if (TestPlaying()) Stop(); }

    // ───────────── toggles ─────────────

    [ContextMenu("Test/Toggle/AutoPlay")]
    private void TestToggleAutoPlay() { AutoPlay = !AutoPlay; TLog($"AutoPlay = {AutoPlay} (delay {AutoPlayDelay}s)"); }

    [ContextMenu("Test/Toggle/IsReadingAllowed")]
    private void TestToggleReading() { IsReadingAllowed = !IsReadingAllowed; TLog($"IsReadingAllowed = {IsReadingAllowed}"); }

    [ContextMenu("Test/Toggle/CompleteLineBeforeNext")]
    private void TestToggleComplete() { CompleteLineBeforeNext = !CompleteLineBeforeNext; TLog($"CompleteLineBeforeNext = {CompleteLineBeforeNext}"); }

    [ContextMenu("Test/Toggle/Text sound")]
    private void TestToggleTextSound() { TextSoundEnabled = !TextSoundEnabled; TLog($"TextSoundEnabled = {TextSoundEnabled}"); }

    [ContextMenu("Test/Toggle/Next sound")]
    private void TestToggleNextSound() { NextSoundEnabled = !NextSoundEnabled; TLog($"NextSoundEnabled = {NextSoundEnabled}"); }

    [ContextMenu("Test/Toggle/Hide canvas when idle")]
    private void TestToggleHide()
    {
        HideCanvasWhenIdle = !HideCanvasWhenIdle;
        if (!IsPlaying) SetVisible(!HideCanvasWhenIdle);
        TLog($"HideCanvasWhenIdle = {HideCanvasWhenIdle}");
    }

    [ContextMenu("Test/Toggle/Cycle typing speed (15 → 40 → 100 → instant)")]
    private void TestCycleSpeed()
    {
        float[] speeds = { 15f, 40f, 100f, 0f };
        int i = Array.FindIndex(speeds, v => Mathf.Approximately(v, CharsPerSecond));
        CharsPerSecond = speeds[(i + 1) % speeds.Length];
        TLog($"CharsPerSecond = {CharsPerSecond}" + (CharsPerSecond <= 0f ? " (instant)" : ""));
    }

    // ───────────── sounds ─────────────

    [ContextMenu("Test/Sound/Play text sound")]
    private void TestPlayTextSound() { if (TestPlaying()) PlayTextSound(_currentLine); }

    [ContextMenu("Test/Sound/Play next sound")]
    private void TestPlayNextSound() { if (TestPlaying()) PlayNextSound(_currentLine); }

    // ───────────── variables ─────────────

    [ContextMenu("Test/Variables/Set (TestVariableName = TestVariableValue)")]
    private void TestSetVariable() { SetVariable(TestVariableName, TestVariableValue); TLog($"{{{TestVariableName}}} = '{TestVariableValue}'"); }

    [ContextMenu("Test/Variables/Remove TestVariableName")]
    private void TestRemoveVariable() => TLog($"Remove '{TestVariableName}' → {RemoveVariable(TestVariableName)}");

    [ContextMenu("Test/Variables/Clear all")]
    private void TestClearVariables() { ClearVariables(); TLog("Variables cleared."); }

    // ───────────── language ─────────────

    [ContextMenu("Test/Language/Next language")]
    private void TestNextLanguage() { Translator.NextLanguage(); TLog($"Language = {Translator.CurrentLanguage} ({Translator.KeyCount} keys)"); }

    [ContextMenu("Test/Language/Previous language")]
    private void TestPreviousLanguage() { Translator.PreviousLanguage(); TLog($"Language = {Translator.CurrentLanguage} ({Translator.KeyCount} keys)"); }

    // ───────────── search ─────────────

    [ContextMenu("Test/Search/SearchLines (TestSearchText)")]
    private void TestSearchLines()
    {
        if (!TestDb()) return;
        var r = SearchLines(TestSearchText, TestSearchTranslated);
        TLog($"SearchLines('{TestSearchText}', translated: {TestSearchTranslated}) → {r.Count} line(s)");
        foreach (var x in r) TLog($"   {x}  [{x.Line.Speaker}] {x.Line.Content}");
    }

    [ContextMenu("Test/Search/FindDialogues by prefix (binary search)")]
    private void TestFindPrefix()
    {
        if (!TestDb()) return;
        var r = FindDialogues(TestNamePrefix);
        TLog($"FindDialogues('{TestNamePrefix}') → {r.Count}: {string.Join(", ", r.ConvertAll(d => d.DialogueName))}");
    }

    [ContextMenu("Test/Search/TryGetIgnoreCase (binary search, upper-cased name)")]
    private void TestFindIgnoreCase()
    {
        if (!TestDb()) return;
        string probe = (TestDialogueName ?? "").ToUpperInvariant();
        bool found = dialoguesDatabase.TryGetIgnoreCase(probe, out Dialogues d);
        TLog($"TryGetIgnoreCase('{probe}') → {found}" + (found ? $" ('{d.DialogueName}', {d.Count} lines)" : ""));
    }

    [ContextMenu("Test/Search/Dialogues of TestSpeaker")]
    private void TestFindBySpeaker()
    {
        if (!TestDb()) return;
        var d = FindDialoguesBySpeaker(TestSpeaker);
        var lines = dialoguesDatabase.FindLinesBySpeaker(TestSpeaker);
        TLog($"Speaker '{TestSpeaker}' → {lines.Count} line(s) in {d.Count} dialogue(s): {string.Join(", ", d.ConvertAll(x => x.DialogueName))}");
    }

    [ContextMenu("Test/Search/List all speakers")]
    private void TestListSpeakers()
    {
        if (!TestDb()) return;
        TLog("Speakers: " + string.Join(", ", dialoguesDatabase.GetSpeakers()));
    }

    [ContextMenu("Test/Search/List all dialogues")]
    private void TestListDialogues()
    {
        if (!TestDb()) return;
        TLog($"{dialoguesDatabase.Count} dialogue(s):");
        for (int i = 0; i < dialoguesDatabase.Count; i++)
        {
            Dialogues d = dialoguesDatabase[i];
            if (d != null) TLog($"   [{i}] '{d.DialogueName}' — {d.Count} line(s)");
        }
    }

    [ContextMenu("Test/Search/Invalidate search cache")]
    private void TestInvalidate() { if (TestDb()) { dialoguesDatabase.InvalidateLookup(); TLog("Search indexes will be rebuilt on next use."); } }

    // ───────────── info ─────────────

    [ContextMenu("Test/Info/Log state")]
    private void TestLogState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"IsPlaying: {IsPlaying}   IsTyping: {IsTyping}   Queued: {QueuedCount}");
        if (IsPlaying)
        {
            sb.AppendLine($"Dialogue: '{(_dialogues != null ? _dialogues.DialogueName : "?")}'   line {_index + 1}/{LineCount}");
            sb.AppendLine($"Speaker: {CurrentSpeaker}");
            sb.AppendLine($"Text: {CurrentText}");
        }
        sb.AppendLine($"Auto: {AutoPlay}   Reading allowed: {IsReadingAllowed}   Speed: {CharsPerSecond}   Complete first: {CompleteLineBeforeNext}");
        sb.AppendLine($"Text sound: {TextSoundEnabled}   Next sound: {NextSoundEnabled}   AudioManager: {(AudioManager.Instance != null)}");
        sb.AppendLine($"Language: {Translator.CurrentLanguage}   Director: {(_director != null ? _director.name : "none")}");
        TLog(sb.ToString().TrimEnd());
    }

    #endregion

    // ═════════════════════════ Internals ═════════════════════════

    private void ShowLine(int index)
    {
        ExitCurrentLine();
        StopRoutine();

        while (index < _dialogues.Count && _dialogues[index] == null) index++; // skip empty slots
        if (index >= _dialogues.Count) { End(); return; }

        _index = index;
        _currentLine = _dialogues[index];
        _currentLine.OnEnter(this);

        SetSpeakerImage(_currentLine.SpeakerImage);
        ApplyTexts(_currentLine);

        PlayLineVoice(_currentLine);

        LineShown?.Invoke(_currentLine, _index);
        BeginTyping(_currentLine);
    }

    private void ExitCurrentLine()
    {
        if (_currentLine == null) return;
        DialoguesAsset line = _currentLine;
        _currentLine = null;
        line.OnExit(this);
    }

    private void ApplyTexts(DialoguesAsset line)
    {
        if (SpeakerName != null) SpeakerName.text = ResolveSpeaker(line);
        if (Content != null) Content.text = ResolveText(line);
    }

    private string ResolveSpeaker(DialoguesAsset line) => ApplyVariables(line.GetSpeaker());
    private string ResolveText(DialoguesAsset line) => ApplyVariables(line.GetText());

    private string ApplyVariables(string text)
    {
        if (string.IsNullOrEmpty(text) || _vars.Count == 0) return text;
        foreach (var kv in _vars) text = text.Replace("{" + kv.Key + "}", kv.Value?.ToString() ?? "");
        return text;
    }

    private void BeginTyping(DialoguesAsset line)
    {
        if (Content == null || CharsPerSecond <= 0f) { FinishTyping(line); return; }
        IsTyping = true;
        _blipCounter = 0;
        _lastBlipTime = -999f;
        _routine = StartCoroutine(TypeRoutine(line));
    }

    private IEnumerator TypeRoutine(DialoguesAsset line)
    {
        Content.ForceMeshUpdate();
        int total = Content.textInfo.characterCount;
        if (total == 0) { _routine = null; FinishTyping(line); yield break; }

        float shown = 0f;
        int lastVisible = 0;
        Content.maxVisibleCharacters = 0;
        while (shown < total)
        {
            shown += CharsPerSecond * Time.unscaledDeltaTime;
            int visible = Mathf.Min(total, (int)shown);

            // one check per newly revealed character → text sound
            for (int c = lastVisible; c < visible; c++) TryPlayTextSound(line, c);
            lastVisible = visible;

            Content.maxVisibleCharacters = visible;
            yield return null;
        }

        _routine = null;
        FinishTyping(line);
    }

    private void FinishTyping(DialoguesAsset line)
    {
        IsTyping = false;
        if (Content != null) Content.maxVisibleCharacters = int.MaxValue;
        LineTyped?.Invoke(line);

        float delay = line.AutoAdvanceDelay > 0f ? line.AutoAdvanceDelay : (AutoPlay ? AutoPlayDelay : -1f);
        if (delay > 0f && IsPlaying) _routine = StartCoroutine(AutoAdvanceRoutine(delay));
    }

    private IEnumerator AutoAdvanceRoutine(float delay)
    {
        float t = 0f;
        while (t < delay) { t += Time.unscaledDeltaTime; yield return null; }
        while (!IsReadingAllowed) yield return null;

        _routine = null;
        Advance(false, PlayNextSoundOnAutoAdvance);
    }

    private void StopRoutine()
    {
        if (_routine != null) { StopCoroutine(_routine); _routine = null; }
    }

    /// <summary>Re-apply the texts of the current line (language or variable changed). Shows it fully.</summary>
    private void RefreshCurrentLine()
    {
        if (!IsPlaying || _currentLine == null) return;
        bool wasTyping = IsTyping;
        ApplyTexts(_currentLine);
        if (wasTyping) { StopRoutine(); IsTyping = false; }
        if (Content != null) Content.maxVisibleCharacters = int.MaxValue;
    }

    private void OnTranslationsChanged() => RefreshCurrentLine();
}