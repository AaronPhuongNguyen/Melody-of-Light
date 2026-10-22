using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance { get; private set; }
    public PlayerData Data;
    public bool AutoSaveOnDialogueCompleted = true;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        Data = SaveSystem.Load();
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    [ContextMenu("Try Save")]
    public void Save() => SaveSystem.Save(Data);

    [ContextMenu("Try new game")]
    public void NewGame() => Data = new PlayerData();

    public bool IsDialogueCompleted(string name) => Data.IsDialogueCompleted(name);

    public void CompleteDialogue(string name)
    {
        if (Data.MarkDialogueCompleted(name) && AutoSaveOnDialogueCompleted) Save();
    }
}