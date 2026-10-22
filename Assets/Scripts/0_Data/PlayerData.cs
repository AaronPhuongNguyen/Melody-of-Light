using System;
using System.Collections.Generic;
using Newtonsoft.Json;

[Serializable]
public class PlayerData
{
    #region Player Data
    //basic Data
    public bool IsCreated = false;
    public string PlayerName;

    //advanced Data that would be added later, just ignore now.
    #endregion

    // Saved: names of every completed dialogue.
    #region Save  
    public List<string> CompletedDialogues = new List<string>();

    [JsonIgnore] private HashSet<string> _lookup;

    [JsonIgnore] public int CompletedCount => CompletedDialogues.Count;

    private HashSet<string> Lookup => _lookup ??= new HashSet<string>(CompletedDialogues);

    public bool IsDialogueCompleted(string name) => !string.IsNullOrEmpty(name) && Lookup.Contains(name);

    /// <summary>Returns true if it was newly added.</summary>
    public bool MarkDialogueCompleted(string name)
    {
        if (string.IsNullOrEmpty(name) || !Lookup.Add(name)) return false;
        CompletedDialogues.Add(name);
        return true;
    }

    public void ResetDialogue(string name)
    {
        if (Lookup.Remove(name)) CompletedDialogues.Remove(name);
    }
    #endregion
}