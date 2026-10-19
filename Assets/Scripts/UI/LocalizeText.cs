using UnityEngine;
using TMPro;

/// <summary>
/// Add to any object with a TextMeshPro text. Type the key (e.g. "menu.play").
/// It translates automatically on enable and whenever EventBus.OnLanguageChanged is raised.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(TextMeshProUGUI))]
public class LocalizedText : MonoBehaviour
{
    [SerializeField] private string key;
    [SerializeField] private TextMeshProUGUI target;

    private object[] _args;

    public string Key => key;

    private void Reset() => target = GetComponent<TextMeshProUGUI>();

    private void Awake()
    {
        if (target == null) target = GetComponent<TextMeshProUGUI>();
    }

    private void OnEnable()
    {
        EventBus.OnLanguageChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        EventBus.OnLanguageChanged -= Refresh;
    }

    /// <summary>Change the key from code.</summary>
    public void SetKey(string newKey)
    {
        key = newKey;
        Refresh();
    }

    /// <summary>Set format arguments, e.g. SetArgs(score) for "Score: {0}".</summary>
    public void SetArgs(params object[] args)
    {
        _args = args;
        Refresh();
    }

    public void Refresh()
    {
        if (target == null || string.IsNullOrEmpty(key)) return;
        target.text = (_args != null && _args.Length > 0)
            ? Translator.Get(key, _args)
            : Translator.Get(key);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (target == null) target = GetComponent<TextMeshProUGUI>();
    }
#endif
}