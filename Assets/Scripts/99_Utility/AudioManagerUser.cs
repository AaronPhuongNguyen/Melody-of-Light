using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drag onto any Button (or leave it on a plain object and use AutoPlayOnStart / the public methods).
/// Pick an action in the Inspector and it calls AudioManager.Instance on click.
/// Also exposes public methods so you can wire them manually in the Button's OnClick list.
///
/// Playlist actions need the named playlists defined in the AudioManager inspector (or registered from code):
///   PlayPlaylist      → replaces the active playlist with the named one and plays it
///   PushPlaylist      → temporary music (boss, shop...), PopPlaylist brings the old music back
///   ReplaceWithClips  → clears the active playlist and uses the 'playlistClips' below
/// </summary>
[DisallowMultipleComponent]
public class AudioManagerUser : MonoBehaviour
{
    // New actions are appended at the end so existing components in your scenes keep their chosen action.
    public enum ActionType
    {
        PlaySFX,
        PlayRandomSFX,
        PlayVoice,
        PlayMusicClip,
        NextTrack,
        PreviousTrack,
        PlayRandomTrack,
        TogglePauseMusic,
        StopMusic,
        ToggleShuffle,
        CycleRepeatMode,
        ToggleMuteMaster,
        ToggleMuteMusic,
        ToggleMuteSFX,
        ToggleMuteVoice,
        StopAll,

        // ── playlists ──
        PlayPlaylist,        // named playlist, replaces the active one
        PushPlaylist,        // save current music, play the named playlist
        PopPlaylist,         // back to the music saved by PushPlaylist
        ReplaceWithClips,    // clear active playlist, use 'playlistClips'
        ClearPlaylist,       // empty the active playlist
        ClearAllPlaylists,   // empty the active playlist AND the push/pop stack
        AddClipToPlaylist,   // append 'clip' to the active playlist
        PlayTrackIndex,      // play track number 'trackIndex' of the active playlist

        // ── music control ──
        PauseMusic,
        ResumeMusic,
        SeekMusic,           // jump to 'seekSeconds' in the current track
        SetShuffleOn,
        SetShuffleOff,
        SetRepeatNone,
        SetRepeatTrack,
        SetRepeatPlaylist,
        SetCrossfade         // sets the crossfade duration to 'crossfadeSeconds'
    }

    [SerializeField] private ActionType action = ActionType.PlaySFX;

    [Header("Clips")]
    [Tooltip("Used by PlaySFX / PlayVoice / PlayMusicClip / AddClipToPlaylist.")]
    [SerializeField] private AudioClip clip;
    [Tooltip("Used by PlayRandomSFX.")]
    [SerializeField] private AudioClip[] clips;

    [Header("Playlists")]
    [Tooltip("Used by PlayPlaylist / PushPlaylist. Must match a playlist name in the AudioManager.")]
    [SerializeField] private string playlistName = "";
    [Tooltip("Used by ReplaceWithClips.")]
    [SerializeField] private AudioClip[] playlistClips;
    [Tooltip("ReplaceWithClips: start the new list now. Off = the current song finishes first, then the new list starts.")]
    [SerializeField] private bool autoPlay = true;
    [Tooltip("PlayPlaylist: restart it even if that playlist is already playing.")]
    [SerializeField] private bool restartIfSame = false;
    [Tooltip("ClearPlaylist / ClearAllPlaylists: also stop the song that is playing.")]
    [SerializeField] private bool stopMusicOnClear = false;
    [Tooltip("Used by PlayTrackIndex.")]
    [SerializeField, Min(0)] private int trackIndex = 0;

    [Header("Values")]
    [SerializeField] private bool AutoPlayOnStart = false;
    [SerializeField, Range(0f, 1f)] private float volume = 1f;
    [SerializeField, Range(0f, 0.5f)] private float pitchRandom = 0f;
    [SerializeField] private float fade = -1f; // -1 = AudioManager's default crossfade
    [Tooltip("Used by SeekMusic.")]
    [SerializeField, Min(0f)] private float seekSeconds = 0f;
    [Tooltip("Used by SetCrossfade.")]
    [SerializeField, Min(0f)] private float crossfadeSeconds = 1.5f;

    private Button _button;

    private void Awake()
    {
        _button = GetComponent<Button>();
        if (_button != null) _button.onClick.AddListener(Execute);
    }

    private void Start()
    {
        if (AutoPlayOnStart) Execute();
    }

    private void OnDestroy()
    {
        if (_button != null) _button.onClick.RemoveListener(Execute);
    }

    /// <summary>Runs the action chosen in the Inspector.</summary>
    public void Execute()
    {
        var am = AudioManager.Instance;
        if (am == null) { Debug.LogWarning("[AudioManagerUser] No AudioManager in scene.", this); return; }

        switch (action)
        {
            case ActionType.PlaySFX: am.PlaySFX(clip, volume, 1f, pitchRandom); break;
            case ActionType.PlayRandomSFX: am.PlayRandomSFX(clips, volume, 1f, pitchRandom); break;
            case ActionType.PlayVoice: am.PlayVoice(clip, true, volume); break;
            case ActionType.PlayMusicClip: am.PlayMusic(clip, fade); break;
            case ActionType.NextTrack: am.NextTrack(fade); break;
            case ActionType.PreviousTrack: am.PreviousTrack(fade); break;
            case ActionType.PlayRandomTrack: am.PlayRandomTrack(fade); break;
            case ActionType.TogglePauseMusic: am.ToggleMusicPause(); break;
            case ActionType.StopMusic: am.StopMusic(fade < 0f ? 0.5f : fade); break;
            case ActionType.ToggleShuffle: am.Shuffle = !am.Shuffle; break;
            case ActionType.CycleRepeatMode:
                am.RepeatMode = (MusicRepeatMode)(((int)am.RepeatMode + 1) % 3); break;
            case ActionType.ToggleMuteMaster: am.MasterMuted = !am.MasterMuted; break;
            case ActionType.ToggleMuteMusic: am.MusicMuted = !am.MusicMuted; break;
            case ActionType.ToggleMuteSFX: am.SFXMuted = !am.SFXMuted; break;
            case ActionType.ToggleMuteVoice: am.VoiceMuted = !am.VoiceMuted; break;
            case ActionType.StopAll: am.StopAll(); break;

            // playlists
            case ActionType.PlayPlaylist: am.PlayPlaylist(playlistName, fade, restartIfSame); break;
            case ActionType.PushPlaylist: am.PushPlaylist(playlistName, fade); break;
            case ActionType.PopPlaylist: am.PopPlaylist(fade); break;
            case ActionType.ReplaceWithClips: am.ReplacePlaylist(playlistClips, autoPlay, fade, playlistName); break;
            case ActionType.ClearPlaylist: am.ClearPlaylist(stopMusicOnClear, ClearFade()); break;
            case ActionType.ClearAllPlaylists: am.ClearAllPlaylists(stopMusicOnClear, ClearFade()); break;
            case ActionType.AddClipToPlaylist: am.AddToPlaylist(clip); break;
            case ActionType.PlayTrackIndex: am.PlayTrack(trackIndex, fade); break;

            // music control
            case ActionType.PauseMusic: am.PauseMusic(); break;
            case ActionType.ResumeMusic: am.ResumeMusic(); break;
            case ActionType.SeekMusic: am.SeekMusic(seekSeconds); break;
            case ActionType.SetShuffleOn: am.Shuffle = true; break;
            case ActionType.SetShuffleOff: am.Shuffle = false; break;
            case ActionType.SetRepeatNone: am.RepeatMode = MusicRepeatMode.None; break;
            case ActionType.SetRepeatTrack: am.RepeatMode = MusicRepeatMode.Track; break;
            case ActionType.SetRepeatPlaylist: am.RepeatMode = MusicRepeatMode.Playlist; break;
            case ActionType.SetCrossfade: am.CrossfadeDuration = crossfadeSeconds; break;
        }
    }

    private float ClearFade() => fade < 0f ? 0.5f : fade;

    // ───────── Manual OnClick / Slider wiring (optional) ─────────

    public void PlayClipSFX(AudioClip c) => AudioManager.Instance?.PlaySFX(c, volume, 1f, pitchRandom);
    public void PlayClipVoice(AudioClip c) => AudioManager.Instance?.PlayVoice(c, true, volume);
    public void PlayClipMusic(AudioClip c) => AudioManager.Instance?.PlayMusic(c, fade);

    public void Next() => AudioManager.Instance?.NextTrack(fade);
    public void Previous() => AudioManager.Instance?.PreviousTrack(fade);
    public void TogglePause() => AudioManager.Instance?.ToggleMusicPause();

    // Playlists: these take the name from the OnClick list, so one component can serve many buttons.
    public void PlayNamedPlaylist(string name) => AudioManager.Instance?.PlayPlaylist(name, fade, restartIfSame);
    public void PushNamedPlaylist(string name) => AudioManager.Instance?.PushPlaylist(name, fade);
    public void PopPlaylist() => AudioManager.Instance?.PopPlaylist(fade);
    public void ReplaceWithClips(AudioClip[] newClips) => AudioManager.Instance?.ReplacePlaylist(newClips, autoPlay, fade);
    public void ClearPlaylist() => AudioManager.Instance?.ClearPlaylist(stopMusicOnClear, ClearFade());
    public void ClearAllPlaylists() => AudioManager.Instance?.ClearAllPlaylists(stopMusicOnClear, ClearFade());
    public void PlayTrack(int index) => AudioManager.Instance?.PlayTrack(index, fade);
    public void Seek(float seconds) => AudioManager.Instance?.SeekMusic(seconds);

    public void SetShuffle(bool on) { if (AudioManager.Instance) AudioManager.Instance.Shuffle = on; }
    public void SetRepeat(int mode)
    {
        if (AudioManager.Instance) AudioManager.Instance.RepeatMode = (MusicRepeatMode)Mathf.Clamp(mode, 0, 2);
    }
    public void SetCrossfadeDuration(float seconds) { if (AudioManager.Instance) AudioManager.Instance.CrossfadeDuration = seconds; }

    // Hook these to a Slider's OnValueChanged (Dynamic float) for volume controls.
    public void SetMasterVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.MasterVolume = v; }
    public void SetMusicVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.MusicVolume = v; }
    public void SetSFXVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.SFXVolume = v; }
    public void SetVoiceVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.VoiceVolume = v; }
}