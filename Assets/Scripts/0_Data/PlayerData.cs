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

    //advanced/gameplay Data
    public int Day = 0; // how many days have passed in game, very important for game events and logic
    public int AtPeakDayInterval = 6; //in this day, player works 200% Faster and doubled the max energy

    public int Energy = 3;//resource for many actions
    public int MaxEnergy = 3;

    public int Money = 1000; //Main currency in game

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