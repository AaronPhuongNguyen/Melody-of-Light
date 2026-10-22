using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

public enum MusicRepeatMode { None, Track, Playlist }
public enum MusicState { Stopped, Playing, Paused }

/// <summary>
/// A named set of tracks with its own settings. Define them in the AudioManager inspector, or register them from code.
/// Add new per-playlist options here (e.g. crossfade override, pitch) and apply them in LoadPlaylist().
/// </summary>
[Serializable]
public class MusicPlaylist
{
    public string Name = "New Playlist";
    public List<AudioClip> Clips = new List<AudioClip>();
    public bool Shuffle = true;
    public MusicRepeatMode RepeatMode = MusicRepeatMode.Playlist;
    [Range(0f, 1f), Tooltip("Volume multiplier for this playlist (on top of the Music mixer volume).")]
    public float Volume = 1f;

    public MusicPlaylist() { }

    public MusicPlaylist(string name, IEnumerable<AudioClip> clips, bool shuffle = true,
                         MusicRepeatMode repeat = MusicRepeatMode.Playlist, float volume = 1f)
    {
        Name = name;
        if (clips != null) Clips.AddRange(clips);
        Shuffle = shuffle;
        RepeatMode = repeat;
        Volume = volume;
    }
}

/// <summary>
/// Architecture: Music Player (2 sources for crossfade) + SFX Player (pooled) + Voice Player (queued, ducks music).
/// Mixer setup: create an AudioMixer with groups Music / SFX / Voice (children of Master) and expose these params:
/// MasterVolume, MusicVolume, SFXVolume, VoiceVolume (names are configurable below).
///
/// Music playlists:
///   AudioManager.Instance.PlayPlaylist("Battle");                       // named playlist from the inspector / RegisterPlaylist
///   AudioManager.Instance.ReplacePlaylist(clips);                       // clear the current one and play these clips
///   AudioManager.Instance.ReplacePlaylist(clips, autoPlay: false);      // current song finishes, then the new list starts
///   AudioManager.Instance.ClearPlaylist(stopMusic: true, fade: 1f);
///   AudioManager.Instance.PushPlaylist("Boss");  ... PopPlaylist();     // boss music, then back to where it was
/// </summary>
[DefaultExecutionOrder(-99)]
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    // ───────────── Mixer ─────────────
    [Header("Mixer")]
    [SerializeField] private AudioMixer mixer;
    [SerializeField] private AudioMixerGroup musicGroup;
    [SerializeField] private AudioMixerGroup sfxGroup;
    [SerializeField] private AudioMixerGroup voiceGroup;
    [SerializeField] private string masterParam = "Master";
    [SerializeField] private string musicParam = "Music";
    [SerializeField] private string sfxParam = "SFX";
    [SerializeField] private string voiceParam = "Voice";

    // ───────────── Music ─────────────
    [Header("Music")]
    [Tooltip("The ACTIVE clip list. Filled by the start playlist, PlayPlaylist(name), ReplacePlaylist(...) or SetPlaylist(...).")]
    [SerializeField] private List<AudioClip> playlist = new List<AudioClip>();
    [Tooltip("Named playlists you can switch to with PlayPlaylist(\"name\").")]
    [SerializeField] private List<MusicPlaylist> playlists = new List<MusicPlaylist>();
    [Tooltip("Loaded at start instead of the clip list above. Empty = use the clip list above.")]
    [SerializeField] private string startPlaylistName = "";
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private bool shuffle = true;
    [SerializeField] private MusicRepeatMode repeatMode = MusicRepeatMode.Playlist;
    [SerializeField, Min(0f)] private float crossfadeDuration = 1.5f;

    [Header("SFX")]
    [SerializeField, Min(1)] private int sfxPoolSize = 16;

    [Header("Voice")]
    [SerializeField, Range(0f, 1f)] private float musicDuckVolume = 0.35f; // music multiplier while voice plays
    [SerializeField, Min(0.1f)] private float duckSpeed = 4f;

    public event Action<AudioClip> OnMusicChanged;
    public event Action OnMusicStopped;
    /// <summary>The active playlist was replaced / cleared / restored. Arg: its name ("" for unnamed).</summary>
    public event Action<string> OnPlaylistChanged;
    public event Action<AudioClip> OnVoiceStarted;
    public event Action<AudioClip> OnVoiceEnded;

    private const float MinDb = -80f;
    private const string PrefPrefix = "Audio_";

    private AudioSource[] _music = new AudioSource[2];
    private float[] _fade = new float[2];
    private int _active;
    private Coroutine _fadeRoutine;
    private MusicState _musicState = MusicState.Stopped;

    private readonly List<int> _order = new List<int>();
    private int _orderPos = -1;
    private int _lastPlayedIndex = -1;
    private int _currentIndex = -1;   // -1 = ad-hoc clip not in playlist
    private bool _autoAdvancing;
    private float _playlistVolume = 1f;

    // saved music states for PushPlaylist / PopPlaylist
    private class MusicSnapshot
    {
        public string Name;
        public List<AudioClip> Clips;
        public bool Shuffle;
        public MusicRepeatMode Repeat;
        public float Volume;
        public AudioClip Clip;
        public float Time;
    }
    private readonly Stack<MusicSnapshot> _stack = new Stack<MusicSnapshot>();

    private AudioSource[] _sfxPool;
    private int _sfxCursor;

    private AudioSource _voice;
    private struct VoiceLine { public AudioClip clip; public float volume; }
    private readonly Queue<VoiceLine> _voiceQueue = new Queue<VoiceLine>();
    private bool _voicePlaying;
    private bool _voicePaused;
    private float _duck = 1f;

    private bool _appPaused;

    private readonly Dictionary<string, float> _volumes = new Dictionary<string, float>();
    private readonly Dictionary<string, bool> _muted = new Dictionary<string, bool>();

    // ───────────── properties ─────────────
    public MusicState MusicState => _musicState;
    public AudioClip CurrentMusic => _music[_active].clip;
    public int CurrentTrackIndex => _currentIndex;
    public bool IsVoicePlaying => _voicePlaying;
    public IReadOnlyList<AudioClip> Playlist => playlist;
    public float MusicTime => _music[_active].time;
    public float MusicLength => _music[_active].clip != null ? _music[_active].clip.length : 0f;

    /// <summary>Name of the active playlist. Empty when it was built from loose clips.</summary>
    public string CurrentPlaylistName { get; private set; } = "";
    public int PlaylistCount => playlist.Count;
    public int PlaylistStackDepth => _stack.Count;
    public IReadOnlyList<MusicPlaylist> NamedPlaylists => playlists;

    public bool Shuffle
    {
        get => shuffle;
        set { if (shuffle == value) return; shuffle = value; RebuildOrder(keepCurrent: true); }
    }

    public MusicRepeatMode RepeatMode
    {
        get => repeatMode;
        set { repeatMode = value; _music[_active].loop = value == MusicRepeatMode.Track; }
    }

    public float CrossfadeDuration { get => crossfadeDuration; set => crossfadeDuration = Mathf.Max(0f, value); }

    // ═════════════════════════ Unity ═════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[AudioManager] Duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;

        CreateSources();
        LoadVolumes();
    }

    private void Start()
    {
        if (Instance != this) return;
        ApplyAllVolumes(); // set mixer params in Start (more reliable than Awake)

        if (!string.IsNullOrEmpty(startPlaylistName) && TryGetPlaylist(startPlaylistName, out MusicPlaylist start))
        {
            LoadPlaylist(start);
            if (playOnStart && playlist.Count > 0) PlayPlaylist();
            return;
        }

        RebuildOrder(false);
        if (playOnStart && playlist.Count > 0) PlayPlaylist();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnApplicationPause(bool paused) => _appPaused = paused;
    private void OnApplicationFocus(bool focus) => _appPaused = !focus;

    private void Update()
    {
        UpdateVoice();
        UpdateMusic();
        UpdateMusicVolumes();
    }

    private void CreateSources()
    {
        for (int i = 0; i < 2; i++)
        {
            var go = new GameObject($"Music Source {i}");
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.outputAudioMixerGroup = musicGroup;
            s.spatialBlend = 0f;
            _music[i] = s;
        }

        var poolRoot = new GameObject("SFX Pool").transform;
        poolRoot.SetParent(transform, false);
        _sfxPool = new AudioSource[sfxPoolSize];
        for (int i = 0; i < sfxPoolSize; i++)
        {
            var s = new GameObject($"SFX {i}").AddComponent<AudioSource>();
            s.transform.SetParent(poolRoot, false);
            s.playOnAwake = false;
            s.outputAudioMixerGroup = sfxGroup;
            _sfxPool[i] = s;
        }

        var v = new GameObject("Voice Source");
        v.transform.SetParent(transform, false);
        _voice = v.AddComponent<AudioSource>();
        _voice.playOnAwake = false;
        _voice.outputAudioMixerGroup = voiceGroup;
        _voice.spatialBlend = 0f;
    }

    // ═════════════════════════ Volumes ═════════════════════════

    public float MasterVolume { get => GetVolume(masterParam); set => SetVolume(masterParam, value); }
    public float MusicVolume { get => GetVolume(musicParam); set => SetVolume(musicParam, value); }
    public float SFXVolume { get => GetVolume(sfxParam); set => SetVolume(sfxParam, value); }
    public float VoiceVolume { get => GetVolume(voiceParam); set => SetVolume(voiceParam, value); }

    public bool MasterMuted { get => IsMuted(masterParam); set => SetMuted(masterParam, value); }
    public bool MusicMuted { get => IsMuted(musicParam); set => SetMuted(musicParam, value); }
    public bool SFXMuted { get => IsMuted(sfxParam); set => SetMuted(sfxParam, value); }
    public bool VoiceMuted { get => IsMuted(voiceParam); set => SetMuted(voiceParam, value); }

    private float GetVolume(string p) => _volumes.TryGetValue(p, out var v) ? v : 1f;
    private bool IsMuted(string p) => _muted.TryGetValue(p, out var m) && m;

    private void SetVolume(string param, float linear)
    {
        linear = Mathf.Clamp01(linear);
        _volumes[param] = linear;
        ApplyVolume(param);
        PlayerPrefs.SetFloat(PrefPrefix + param, linear);
    }

    private void SetMuted(string param, bool muted)
    {
        _muted[param] = muted;
        ApplyVolume(param);
        PlayerPrefs.SetInt(PrefPrefix + param + "_Mute", muted ? 1 : 0);
    }

    private void ApplyVolume(string param)
    {
        if (mixer == null) return;
        float v = IsMuted(param) ? 0f : GetVolume(param);
        mixer.SetFloat(param, LinearToDb(v));
    }

    private void ApplyAllVolumes()
    {
        ApplyVolume(masterParam); ApplyVolume(musicParam);
        ApplyVolume(sfxParam); ApplyVolume(voiceParam);
    }

    private void LoadVolumes()
    {
        foreach (var p in new[] { masterParam, musicParam, sfxParam, voiceParam })
        {
            _volumes[p] = PlayerPrefs.GetFloat(PrefPrefix + p, 1f);
            _muted[p] = PlayerPrefs.GetInt(PrefPrefix + p + "_Mute", 0) == 1;
        }
    }

    public void SavePrefs() => PlayerPrefs.Save();

    public static float LinearToDb(float linear) => linear <= 0.0001f ? MinDb : Mathf.Log10(linear) * 20f;
    public static float DbToLinear(float db) => db <= MinDb ? 0f : Mathf.Pow(10f, db / 20f);

    /// <summary>Animate any exposed mixer parameter (e.g. low-pass cutoff for a pause-menu muffle).</summary>
    public void SetMixerParam(string param, float value) { if (mixer != null) mixer.SetFloat(param, value); }

    // ═════════════════════════ Playlist: named ═════════════════════════

    public bool HasPlaylist(string name) => TryGetPlaylist(name, out _);

    public bool TryGetPlaylist(string name, out MusicPlaylist result)
    {
        result = null;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (MusicPlaylist p in playlists)
            if (p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { result = p; return true; }
        return false;
    }

    public IEnumerable<string> PlaylistNames
    {
        get { foreach (MusicPlaylist p in playlists) if (p != null) yield return p.Name; }
    }

    /// <summary>Add (or overwrite) a named playlist from code. It is not played until you call PlayPlaylist(name).</summary>
    public MusicPlaylist RegisterPlaylist(string name, IEnumerable<AudioClip> clips, bool shuffle = true,
                                          MusicRepeatMode repeat = MusicRepeatMode.Playlist, float volume = 1f)
    {
        var p = new MusicPlaylist(name, clips, shuffle, repeat, volume);
        RegisterPlaylist(p);
        return p;
    }

    public void RegisterPlaylist(MusicPlaylist p)
    {
        if (p == null || string.IsNullOrEmpty(p.Name)) return;
        int i = playlists.FindIndex(x => x != null && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) playlists[i] = p; else playlists.Add(p);
    }

    public bool UnregisterPlaylist(string name)
    {
        int i = playlists.FindIndex(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        playlists.RemoveAt(i);
        return true;
    }

    /// <summary>
    /// Replace the active playlist with a named one and play it from the start (crossfades from the current track).
    /// Does nothing if it is already the one playing, unless restartIfSame is true. Returns false when the name is unknown.
    /// </summary>
    public bool PlayPlaylist(string name, float fade = -1f, bool restartIfSame = false)
    {
        if (!TryGetPlaylist(name, out MusicPlaylist p))
        {
            Debug.LogWarning($"[AudioManager] Playlist '{name}' not found.");
            return false;
        }
        if (!restartIfSame && _musicState == MusicState.Playing &&
            string.Equals(CurrentPlaylistName, p.Name, StringComparison.OrdinalIgnoreCase)) return true;

        LoadPlaylist(p);
        if (playlist.Count == 0) { StopMusic(fade < 0f ? crossfadeDuration : fade); return true; }
        PlayPlaylist(fade);
        return true;
    }

    /// <summary>Replace the active playlist with a MusicPlaylist object (not required to be registered).</summary>
    public void ReplacePlaylist(MusicPlaylist p, bool autoPlay = true, float fade = -1f)
    {
        if (p == null) return;
        LoadPlaylist(p);
        FinishReplace(autoPlay, fade);
    }

    // ═════════════════════════ Playlist: replace / clear ═════════════════════════

    /// <summary>
    /// Clear the active playlist and replace it with these clips (keeps the current shuffle / repeat settings).
    /// autoPlay = true: start the new list now (crossfade). autoPlay = false: the song that is playing finishes
    /// first, then the new list starts.
    /// </summary>
    public void ReplacePlaylist(IEnumerable<AudioClip> clips, bool autoPlay = true, float fade = -1f, string name = "")
    {
        playlist.Clear();
        if (clips != null)
            foreach (AudioClip c in clips) if (c != null) playlist.Add(c);

        CurrentPlaylistName = name ?? "";
        _playlistVolume = 1f;
        _lastPlayedIndex = -1;
        RebuildOrder(false);
        OnPlaylistChanged?.Invoke(CurrentPlaylistName);
        FinishReplace(autoPlay, fade);
    }

    /// <summary>Same as ReplacePlaylist(clips, autoPlay). Kept for older code.</summary>
    public void SetPlaylist(IEnumerable<AudioClip> clips, bool autoPlay = true) => ReplacePlaylist(clips, autoPlay);

    /// <summary>
    /// Empty the active playlist. The song that is playing is left alone unless stopMusic is true;
    /// when it ends nothing follows.
    /// </summary>
    public void ClearPlaylist(bool stopMusic = false, float fade = 0f)
    {
        playlist.Clear();
        _order.Clear();
        _orderPos = -1;
        _currentIndex = -1;
        _lastPlayedIndex = -1;
        CurrentPlaylistName = "";
        _playlistVolume = 1f;
        OnPlaylistChanged?.Invoke("");
        if (stopMusic) StopMusic(fade);
    }

    /// <summary>Clear the playlist AND the push/pop stack.</summary>
    public void ClearAllPlaylists(bool stopMusic = false, float fade = 0f)
    {
        _stack.Clear();
        ClearPlaylist(stopMusic, fade);
    }

    private void FinishReplace(bool autoPlay, float fade)
    {
        if (autoPlay)
        {
            if (playlist.Count > 0) PlayPlaylist(fade);
            else StopMusic(fade < 0f ? crossfadeDuration : fade);
            return;
        }

        // keep the current song; the new list starts when it ends
        AudioClip playing = _musicState != MusicState.Stopped ? _music[_active].clip : null;
        _currentIndex = playing != null ? playlist.IndexOf(playing) : -1;
        if (_currentIndex >= 0) _orderPos = Mathf.Max(0, _order.IndexOf(_currentIndex));
        _music[_active].loop = repeatMode == MusicRepeatMode.Track;
    }

    /// <summary>Copy a MusicPlaylist into the active state. Does not start playing.</summary>
    private void LoadPlaylist(MusicPlaylist p)
    {
        playlist.Clear();
        foreach (AudioClip c in p.Clips) if (c != null) playlist.Add(c);

        shuffle = p.Shuffle;
        repeatMode = p.RepeatMode;
        _playlistVolume = Mathf.Clamp01(p.Volume);
        CurrentPlaylistName = p.Name ?? "";
        _lastPlayedIndex = -1;
        _currentIndex = -1;
        RebuildOrder(false);
        OnPlaylistChanged?.Invoke(CurrentPlaylistName);
    }

    // ═════════════════════════ Playlist: push / pop ═════════════════════════

    /// <summary>
    /// Save what is playing (playlist, track and position), switch to a named playlist.
    /// PopPlaylist() later brings the old music back exactly where it was. Returns false when the name is unknown.
    /// </summary>
    public bool PushPlaylist(string name, float fade = -1f)
    {
        if (!HasPlaylist(name))
        {
            Debug.LogWarning($"[AudioManager] Playlist '{name}' not found.");
            return false;
        }

        _stack.Push(new MusicSnapshot
        {
            Name = CurrentPlaylistName,
            Clips = new List<AudioClip>(playlist),
            Shuffle = shuffle,
            Repeat = repeatMode,
            Volume = _playlistVolume,
            Clip = _musicState != MusicState.Stopped ? _music[_active].clip : null,
            Time = _musicState != MusicState.Stopped ? _music[_active].time : 0f
        });

        return PlayPlaylist(name, fade, true);
    }

    /// <summary>Return to the music saved by the last PushPlaylist. Returns false when the stack is empty.</summary>
    public bool PopPlaylist(float fade = -1f)
    {
        if (_stack.Count == 0) return false;
        MusicSnapshot s = _stack.Pop();

        playlist.Clear();
        playlist.AddRange(s.Clips);
        shuffle = s.Shuffle;
        repeatMode = s.Repeat;
        _playlistVolume = s.Volume;
        CurrentPlaylistName = s.Name;
        _lastPlayedIndex = -1;
        RebuildOrder(false);
        OnPlaylistChanged?.Invoke(CurrentPlaylistName);

        if (s.Clip == null)
        {
            if (playlist.Count > 0) PlayPlaylist(fade); else StopMusic(fade < 0f ? crossfadeDuration : fade);
            return true;
        }

        _currentIndex = playlist.IndexOf(s.Clip);
        if (_currentIndex >= 0)
        {
            _orderPos = Mathf.Max(0, _order.IndexOf(_currentIndex));
            _lastPlayedIndex = _currentIndex;
        }
        StartClip(s.Clip, fade);
        SeekMusic(s.Time);
        return true;
    }

    // ═════════════════════════ Playlist: building / navigation ═════════════════════════

    public void AddToPlaylist(AudioClip clip)
    {
        if (clip == null) return;
        playlist.Add(clip);
        int idx = playlist.Count - 1;
        if (shuffle && _order.Count > _orderPos + 1)
            _order.Insert(RNG.GetInt(_orderPos + 1, _order.Count + 1), idx);
        else _order.Add(idx);
    }

    /// <summary>Start playlist from the beginning of the current order.</summary>
    public void PlayPlaylist(float fade = -1f)
    {
        if (playlist.Count == 0) return;
        RebuildOrder(false);
        _orderPos = 0;
        PlayAtOrderPos(fade);
    }

    public void PlayTrack(int playlistIndex, float fade = -1f)
    {
        if (playlistIndex < 0 || playlistIndex >= playlist.Count) return;
        if (_order.Count != playlist.Count) RebuildOrder(false);
        int pos = _order.IndexOf(playlistIndex);
        _orderPos = pos < 0 ? 0 : pos;
        PlayAtOrderPos(fade);
    }

    public void NextTrack(float fade = -1f)
    {
        if (playlist.Count == 0) return;
        _orderPos++;
        if (_orderPos >= _order.Count)
        {
            if (repeatMode == MusicRepeatMode.None) { StopMusic(fade); return; }
            RebuildOrder(false);   // new shuffle each cycle
            _orderPos = 0;
        }
        PlayAtOrderPos(fade);
    }

    public void PreviousTrack(float fade = -1f)
    {
        if (playlist.Count == 0) return;
        if (_music[_active].time > 3f) { _music[_active].time = 0f; return; } // restart first
        _orderPos = _orderPos <= 0 ? _order.Count - 1 : _orderPos - 1;
        PlayAtOrderPos(fade);
    }

    public void PlayRandomTrack(float fade = -1f)
    {
        if (playlist.Count == 0) return;
        int idx = RNG.GetInt(0, playlist.Count);
        if (playlist.Count > 1 && idx == _currentIndex) idx = (idx + 1 + RNG.GetInt(0, playlist.Count - 1)) % playlist.Count;
        PlayTrack(idx, fade);
    }

    private void PlayAtOrderPos(float fade)
    {
        int idx = _order[_orderPos];
        _currentIndex = idx;
        _lastPlayedIndex = idx;
        StartClip(playlist[idx], fade);
    }

    private void RebuildOrder(bool keepCurrent)
    {
        int current = keepCurrent && _orderPos >= 0 && _orderPos < _order.Count ? _order[_orderPos] : -1;
        _order.Clear();
        for (int i = 0; i < playlist.Count; i++) _order.Add(i);

        if (shuffle)
        {
            // Fisher-Yates using own RNG
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = RNG.GetInt(0, i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            // avoid repeating last played track at the cycle boundary
            if (_order.Count > 1 && !keepCurrent && _order[0] == _lastPlayedIndex)
            {
                int j = RNG.GetInt(1, _order.Count);
                (_order[0], _order[j]) = (_order[j], _order[0]);
            }
        }

        if (current >= 0)
        {
            int pos = _order.IndexOf(current);
            if (pos > 0) { (_order[0], _order[pos]) = (_order[pos], _order[0]); }
            _orderPos = 0;
        }
        else _orderPos = -1;
    }

    // ═════════════════════════ Music playback ═════════════════════════

    /// <summary>Play a clip that isn't necessarily in the playlist. When it ends, the playlist continues (if any).</summary>
    public void PlayMusic(AudioClip clip, float fade = -1f, bool loop = false)
    {
        if (clip == null) return;
        _currentIndex = playlist.IndexOf(clip);
        StartClip(clip, fade, loop);
    }

    private void StartClip(AudioClip clip, float fade, bool forceLoop = false)
    {
        if (fade < 0f) fade = crossfadeDuration;
        int prev = _active;
        int next = 1 - _active;

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);

        // if "next" source is still fading out from a previous crossfade, cut it
        _music[next].Stop();
        _music[next].clip = clip;
        _music[next].loop = forceLoop || repeatMode == MusicRepeatMode.Track;
        _music[next].time = 0f;
        _fade[next] = fade > 0f ? 0f : 1f;
        _music[next].volume = _fade[next] * _duck * _playlistVolume;
        _music[next].Play();

        _active = next;
        _musicState = MusicState.Playing;
        _autoAdvancing = false;

        if (fade > 0f && _music[prev].isPlaying)
            _fadeRoutine = StartCoroutine(CrossfadeRoutine(prev, next, fade));
        else
        {
            _music[prev].Stop();
            _fade[prev] = 0f;
            _fade[next] = 1f;
        }

        OnMusicChanged?.Invoke(clip);
    }

    private IEnumerator CrossfadeRoutine(int from, int to, float duration)
    {
        float startFrom = _fade[from];
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            _fade[from] = Mathf.Lerp(startFrom, 0f, k);
            _fade[to] = k;
            yield return null;
        }
        _fade[from] = 0f;
        _fade[to] = 1f;
        _music[from].Stop();
        _fadeRoutine = null;
    }

    public void StopMusic(float fade = 0f)
    {
        if (_musicState == MusicState.Stopped) return;
        _musicState = MusicState.Stopped;
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        if (fade > 0f) _fadeRoutine = StartCoroutine(FadeOutRoutine(fade));
        else
        {
            _music[0].Stop(); _music[1].Stop();
            _fade[0] = _fade[1] = 0f;
        }
        OnMusicStopped?.Invoke();
    }

    private IEnumerator FadeOutRoutine(float duration)
    {
        float s0 = _fade[0], s1 = _fade[1], t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = 1f - Mathf.Clamp01(t / duration);
            _fade[0] = s0 * k; _fade[1] = s1 * k;
            yield return null;
        }
        _music[0].Stop(); _music[1].Stop();
        _fade[0] = _fade[1] = 0f;
        _fadeRoutine = null;
    }

    public void PauseMusic()
    {
        if (_musicState != MusicState.Playing) return;
        _musicState = MusicState.Paused;
        _music[0].Pause(); _music[1].Pause();
    }

    public void ResumeMusic()
    {
        if (_musicState != MusicState.Paused) return;
        _musicState = MusicState.Playing;
        _music[0].UnPause(); _music[1].UnPause();
    }

    public void ToggleMusicPause() { if (_musicState == MusicState.Playing) PauseMusic(); else ResumeMusic(); }

    public void SeekMusic(float seconds)
    {
        var s = _music[_active];
        if (s.clip != null) s.time = Mathf.Clamp(seconds, 0f, s.clip.length - 0.01f);
    }

    private void UpdateMusic()
    {
        if (_musicState != MusicState.Playing || _appPaused) return;
        var src = _music[_active];
        if (src.clip == null || src.loop) return;

        bool ended = !src.isPlaying;
        bool nearEnd = crossfadeDuration > 0f && _fadeRoutine == null &&
                       src.clip.length - src.time <= crossfadeDuration;

        if (_autoAdvancing) return;
        if (ended || nearEnd)
        {
            _autoAdvancing = true;
            AdvanceAfterTrack(nearEnd && !ended ? crossfadeDuration : 0f);
        }
    }

    private void AdvanceAfterTrack(float fade)
    {
        if (playlist.Count == 0) { StopMusic(); return; }

        // ad-hoc clip finished → continue playlist from start
        if (_currentIndex < 0)
        {
            if (_order.Count != playlist.Count) RebuildOrder(false);
            _orderPos = 0;
            PlayAtOrderPos(fade);
            return;
        }
        NextTrack(fade);
    }

    private void UpdateMusicVolumes()
    {
        for (int i = 0; i < 2; i++)
            if (_music[i].isPlaying || _musicState == MusicState.Paused)
                _music[i].volume = _fade[i] * _duck * _playlistVolume;
    }

    // ═════════════════════════ SFX ═════════════════════════

    /// <summary>Play a 2D sound effect. pitchRandom adds ± variation using your RNG.</summary>
    public AudioSource PlaySFX(AudioClip clip, float volume = 1f, float pitch = 1f, float pitchRandom = 0f)
    {
        if (clip == null) return null;
        var s = GetFreeSFXSource();
        ConfigureSFX(s, clip, volume, pitch, pitchRandom);
        s.spatialBlend = 0f;
        s.Play();
        return s;
    }

    /// <summary>Play a 3D sound effect at a world position.</summary>
    public AudioSource PlaySFXAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f,
                                 float pitchRandom = 0f, float minDistance = 1f, float maxDistance = 25f)
    {
        if (clip == null) return null;
        var s = GetFreeSFXSource();
        ConfigureSFX(s, clip, volume, pitch, pitchRandom);
        s.transform.position = position;
        s.spatialBlend = 1f;
        s.minDistance = minDistance;
        s.maxDistance = maxDistance;
        s.Play();
        return s;
    }

    /// <summary>Play a random clip from an array (uses your RNG).</summary>
    public AudioSource PlayRandomSFX(IList<AudioClip> clips, float volume = 1f, float pitch = 1f, float pitchRandom = 0f)
    {
        if (clips == null || clips.Count == 0) return null;
        return PlaySFX(clips[RNG.GetInt(0, clips.Count)], volume, pitch, pitchRandom);
    }

    public void StopAllSFX()
    {
        foreach (var s in _sfxPool) s.Stop();
    }

    public void PauseSFX() { foreach (var s in _sfxPool) s.Pause(); }
    public void ResumeSFX() { foreach (var s in _sfxPool) s.UnPause(); }

    private void ConfigureSFX(AudioSource s, AudioClip clip, float volume, float pitch, float pitchRandom)
    {
        s.clip = clip;
        s.volume = Mathf.Clamp01(volume);
        s.pitch = pitchRandom > 0f ? pitch + RNG.GetFloat(-pitchRandom, pitchRandom) : pitch;
        s.loop = false;
    }

    private AudioSource GetFreeSFXSource()
    {
        // round-robin search for an idle source; otherwise steal the next one in line
        for (int i = 0; i < _sfxPool.Length; i++)
        {
            _sfxCursor = (_sfxCursor + 1) % _sfxPool.Length;
            if (!_sfxPool[_sfxCursor].isPlaying) return _sfxPool[_sfxCursor];
        }
        _sfxCursor = (_sfxCursor + 1) % _sfxPool.Length;
        _sfxPool[_sfxCursor].Stop();
        return _sfxPool[_sfxCursor];
    }

    // ═════════════════════════ Voice ═════════════════════════

    /// <summary>Play a voice line. interrupt=true clears the queue and cuts the current line.</summary>
    public void PlayVoice(AudioClip clip, bool interrupt = false, float volume = 1f)
    {
        if (clip == null) return;
        if (interrupt) StopVoice();
        _voiceQueue.Enqueue(new VoiceLine { clip = clip, volume = volume });
    }

    public void StopVoice()
    {
        _voiceQueue.Clear();
        if (_voicePlaying)
        {
            var c = _voice.clip;
            _voice.Stop();
            _voicePlaying = false;
            OnVoiceEnded?.Invoke(c);
        }
    }

    public void PauseVoice() { _voicePaused = true; _voice.Pause(); }
    public void ResumeVoice() { _voicePaused = false; _voice.UnPause(); }

    private void UpdateVoice()
    {
        if (!_voicePaused && !_appPaused)
        {
            if (_voicePlaying && !_voice.isPlaying)
            {
                _voicePlaying = false;
                OnVoiceEnded?.Invoke(_voice.clip);
            }

            if (!_voicePlaying && _voiceQueue.Count > 0)
            {
                var line = _voiceQueue.Dequeue();
                _voice.clip = line.clip;
                _voice.volume = Mathf.Clamp01(line.volume);
                _voice.Play();
                _voicePlaying = true;
                OnVoiceStarted?.Invoke(line.clip);
            }
        }

        // duck music while voice is active
        float target = (_voicePlaying || _voiceQueue.Count > 0) ? musicDuckVolume : 1f;
        _duck = Mathf.MoveTowards(_duck, target, duckSpeed * Time.unscaledDeltaTime);
    }

    // ═════════════════════════ Global ═════════════════════════

    public void PauseAll() { PauseMusic(); PauseSFX(); PauseVoice(); }
    public void ResumeAll() { ResumeMusic(); ResumeSFX(); ResumeVoice(); }
    public void StopAll() { StopMusic(); StopAllSFX(); StopVoice(); }
}