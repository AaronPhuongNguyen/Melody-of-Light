using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drag onto any Button. Pick an action in the Inspector and it calls AudioManager.Instance on click.
/// Also exposes public methods so you can wire them manually in the Button's OnClick list.
/// </summary>
[DisallowMultipleComponent]
public class AudioManagerUser : MonoBehaviour
{
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
        StopAll
    }

    [SerializeField] private ActionType action = ActionType.PlaySFX;
    [Tooltip("Used by PlaySFX / PlayVoice / PlayMusicClip.")]
    [SerializeField] private AudioClip clip;
    [Tooltip("Used by PlayRandomSFX.")]
    [SerializeField] private AudioClip[] clips;
    [SerializeField, Range(0f, 1f)] private float volume = 1f;
    [SerializeField, Range(0f, 0.5f)] private float pitchRandom = 0f;
    [SerializeField] private float fade = -1f; // -1 = AudioManager's default crossfade

    private Button _button;

    private void Awake()
    {
        _button = GetComponent<Button>();
        if (_button != null) _button.onClick.AddListener(Execute);
    }

    private void OnDestroy()
    {
        if (_button != null) _button.onClick.RemoveListener(Execute);
    }

    /// <summary>Runs the action chosen in the Inspector.</summary>
    public void Execute()
    {
        var am = AudioManager.Instance;
        if (am == null) { Debug.LogWarning("[AudioManagerUser] No AudioManager in scene."); return; }

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
        }
    }

    // ───────── Manual OnClick / Slider wiring (optional) ─────────

    public void PlayClipSFX(AudioClip c) => AudioManager.Instance?.PlaySFX(c, volume, 1f, pitchRandom);
    public void PlayClipVoice(AudioClip c) => AudioManager.Instance?.PlayVoice(c, true, volume);
    public void PlayClipMusic(AudioClip c) => AudioManager.Instance?.PlayMusic(c, fade);

    public void Next() => AudioManager.Instance?.NextTrack(fade);
    public void Previous() => AudioManager.Instance?.PreviousTrack(fade);
    public void TogglePause() => AudioManager.Instance?.ToggleMusicPause();

    // Hook these to a Slider's OnValueChanged (Dynamic float) for volume controls.
    public void SetMasterVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.MasterVolume = v; }
    public void SetMusicVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.MusicVolume = v; }
    public void SetSFXVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.SFXVolume = v; }
    public void SetVoiceVolume(float v) { if (AudioManager.Instance) AudioManager.Instance.VoiceVolume = v; }
}