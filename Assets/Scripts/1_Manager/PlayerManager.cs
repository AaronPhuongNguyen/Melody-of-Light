using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance { get; private set; }
    public PlayerData Data { get; private set; }
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

    public bool IsDialogueCompleted(string name) => Data.IsDialogueCompleted(name);

    public void CompleteDialogue(string name)
    {
        if (Data.MarkDialogueCompleted(name) && AutoSaveOnDialogueCompleted) Save();
    }
}