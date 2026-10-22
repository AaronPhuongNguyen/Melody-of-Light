using Newtonsoft.Json;
using System.IO;
using UnityEngine;

public static class SaveSystem
{
    private static string PathFor(string slot) => Path.Combine(Application.persistentDataPath, slot + ".json");

    public static void Save(PlayerData data, string slot = "save0")
    {
        try { File.WriteAllText(PathFor(slot), JsonConvert.SerializeObject(data, Formatting.Indented)); }
        catch (System.Exception e) { Debug.LogError($"[SaveSystem] Save failed: {e.Message}"); }
    }

    public static PlayerData Load(string slot = "save0")
    {
        try
        {
            string p = PathFor(slot);
            if (File.Exists(p)) return JsonConvert.DeserializeObject<PlayerData>(File.ReadAllText(p)) ?? new PlayerData();
        }
        catch (System.Exception e) { Debug.LogError($"[SaveSystem] Load failed: {e.Message}"); }
        return new PlayerData();
    }

    public static void Delete(string slot = "save0")
    {
        string p = PathFor(slot);
        if (File.Exists(p)) File.Delete(p);
    }
}