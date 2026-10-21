using System;
using UnityEngine;

/// <summary>
/// One line of dialogue. Inherit from it to add data/behaviour (see MainStoryDialoguesAsset):
/// override GetSpeaker / GetText for custom text, OnEnter / OnExit for side effects (background, sfx, quests...).
/// Subclasses must be [Serializable]; they show up automatically in the "Add Line" menu of the database inspector.
/// </summary>
[Serializable]
public class DialoguesAsset
{
    public Sprite SpeakerImage;

    [Tooltip("Plain name or a Translator key. If the key exists it is translated, otherwise the text is shown as written.")]
    public string Speaker = "Human";

    [Tooltip("Translator key. Supports {0}-style args from the language file and {name} variables set with StoryTeller.SetVariable.")]
    public string Content = "dia.humanIntroduce";

    [Tooltip("Seconds to wait after the line is fully shown before moving on by itself. <= 0 : wait for Next() (or StoryTeller.AutoPlay).")]
    public float AutoAdvanceDelay = -1f;

    [Tooltip("Optional voice clip (AudioManager voice channel, or StoryTeller.VoiceSource as fallback).")]
    public AudioClip Voice;

    [Tooltip("Per-character blip for this line (e.g. a different 'voice' per speaker). Empty = StoryTeller.TextSounds.")]
    public AudioClip TextSound;

    [Tooltip("Silence the per-character blip on this line (narration, whispers...).")]
    public bool MuteTextSound;

    [Tooltip("Sound played when the player presses Next on this line. Empty = StoryTeller.NextSound.")]
    public AudioClip NextSound;

    // ───────────── overridable ─────────────

    public virtual string GetSpeaker() => string.IsNullOrEmpty(Speaker) ? "" : Translator.GetOr(Speaker, Speaker);

    public virtual string GetText() => string.IsNullOrEmpty(Content) ? "" : Translator.Get(Content);

    /// <summary>Return null to use the StoryTeller default.</summary>
    public virtual AudioClip GetTextSound() => TextSound;
    public virtual AudioClip GetNextSound() => NextSound;

    /// <summary>Called when the line is about to be shown (before the visuals are applied).</summary>
    public virtual void OnEnter(StoryTeller teller) { }

    /// <summary>Called when the line is left (next line, GoTo, Skip or end of dialogue).</summary>
    public virtual void OnExit(StoryTeller teller) { }

    // ───────────── code helpers ─────────────

    public static DialoguesAsset Create(string speaker, string contentKey, Sprite speakerImage = null, float autoAdvanceDelay = -1f)
        => new DialoguesAsset { Speaker = speaker, Content = contentKey, SpeakerImage = speakerImage, AutoAdvanceDelay = autoAdvanceDelay };
}