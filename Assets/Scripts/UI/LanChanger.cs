using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Add to a TMP_Dropdown. Fills it with the languages from the LanguageDatabase,
/// changes the language on selection and raises EventBus.RaiseLanguageChanged().
/// </summary>
public class LanguageDropdown : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown dropdown;

    // Display names shown in the dropdown. Unknown codes show the code itself.
    private static readonly Dictionary<string, string> NativeNames = new Dictionary<string, string>
    {
        { "EN", "English" },
        { "VI", "Tiếng Việt" },
        { "FR", "Français" },
        { "DE", "Deutsch" },
        { "ES", "Español" },
        { "PT", "Português" },
        { "IT", "Italiano" },
        { "RU", "Русский" },
        { "JA", "日本語" },
        { "KO", "한국어" },
        { "ZH", "简体中文" },
        { "ZH-TW", "繁體中文" },
        { "TH", "ไทย" },
        { "ID", "Bahasa Indonesia" }
    };

    private void Reset() => dropdown = GetComponent<TMP_Dropdown>();

    private void Awake()
    {
        if (dropdown == null) dropdown = GetComponent<TMP_Dropdown>();
    }

    private void OnEnable()
    {
        BuildOptions();
        dropdown.onValueChanged.AddListener(OnDropdownChanged);
        EventBus.OnLanguageChanged += SyncValue; // keeps it correct if the language is changed elsewhere
    }

    private void OnDisable()
    {
        dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
        EventBus.OnLanguageChanged -= SyncValue;
    }

    private void BuildOptions()
    {
        var options = new List<TMP_Dropdown.OptionData>();
        foreach (string code in Translator.Languages)
        {
            string label = NativeNames.TryGetValue(code.ToUpperInvariant(), out string n) ? n : code;
            options.Add(new TMP_Dropdown.OptionData(label));
        }

        dropdown.ClearOptions();
        dropdown.AddOptions(options);
        SyncValue();
    }

    private void SyncValue()
    {
        int index = Translator.CurrentIndex;
        if (index < 0) return;
        dropdown.SetValueWithoutNotify(index);
        dropdown.RefreshShownValue();
    }

    private void OnDropdownChanged(int index)
    {
        if (Translator.SetLanguage(index))
            EventBus.RaiseLanguageChanged();
    }
}