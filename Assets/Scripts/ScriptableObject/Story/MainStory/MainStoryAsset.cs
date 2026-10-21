using System;
using UnityEngine;

/// <summary>Main-story line: can change the background. Lines without a Background keep the previous one.</summary>
[Serializable]
public class MainStoryDialoguesAsset : DialoguesAsset
{
    public Sprite Background; // main story can change background
    public Color BackgroundColor;

    public override void OnEnter(StoryTeller teller)
    {
        base.OnEnter(teller);
        if (Background != null) teller.SetBackground(Background,BackgroundColor);
    }
}