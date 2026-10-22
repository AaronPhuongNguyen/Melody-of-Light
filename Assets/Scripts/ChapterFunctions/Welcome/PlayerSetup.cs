using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// First-launch setup: language → player name → save. Put it on any object in the first scene.
/// If the player was already created it destroys itself; otherwise it builds its UI through
/// CanvasManager, and removes all of it after the name is confirmed and saved.
/// </summary>
public class PlayerSetUp : MonoBehaviour
{
    [Header("Style")]
    [Tooltip("Your own font, applied to every text, the dropdown and the input field.")]
    public TMP_FontAsset Font;
    public Color TextColor = Color.white;
    public Color PanelColor = new Color(0f, 0f, 0f, 0.85f);

    [Header("Name")]
    public int MaxNameLength = 16;

    [Header("Behaviour")]
    [Tooltip("Destroy the whole GameObject when finished (otherwise only this component).")]
    public bool DestroyGameObjectWhenDone = true;

    [Header("Timing")]
    [Tooltip("Seconds to wait before the setup UI appears.")]
    public float StartDelay = 1.25f;

    /// <summary>Raised after the name is saved and the UI is gone. Arg: the player's name.</summary>
    public event Action<string> SetupCompleted;

    private const string LayerName = "PlayerSetup";
    private const string BgName = "Setup_Bg";

    private struct Bound { public TMP_Text Text; public string Key; }

    private readonly List<string> _stepElements = new List<string>();
    private readonly List<Bound> _bound = new List<Bound>();
    private CanvasManager _cm;
    private TMP_InputField _input;

    private void OnEnable() => EventBus.OnLanguageChanged += RefreshTexts;
    private void OnDisable() => EventBus.OnLanguageChanged -= RefreshTexts;

    private IEnumerator Start()
    {
        PlayerManager pm = PlayerManager.Instance;
        if (pm == null)
        {
            Debug.LogError("[PlayerSetUp] No PlayerManager in the scene.", this);
            yield break;
        }

        if (pm.Data.IsCreated)
        {
            Destroy(gameObject);
            yield break;
        }

        if (StartDelay > 0f) yield return new WaitForSecondsRealtime(StartDelay);

        _cm = CanvasManager.Instance;
        _cm.CreateLayer(LayerName);
        _cm.AddPanel(BgName, PanelColor, null, CanvasAnchor.Stretch, default, LayerName, true);
        BuildLanguageStep();
    }

    private void OnDestroy() => CloseUI();

    // ───────────── step 1: language ─────────────

    private static readonly Dictionary<string, string> NativeNames = new Dictionary<string, string>
    {
        { "EN", "English" }, { "VI", "Tiếng Việt" }
    };

    private TextMeshProUGUI _langLabel;

    private static string CurrentLanguageName()
    {
        string code = Translator.CurrentLanguage;
        if (string.IsNullOrEmpty(code)) return "";
        return NativeNames.TryGetValue(code.ToUpperInvariant(), out string n) ? n : code;
    }

    private void BuildLanguageStep()
    {
        ClearStep();

        Label("Setup_LanguageTitle", "settings.language", new Vector2(0f, 300f));

        Button lang = _cm.AddButton("Setup_Language", CurrentLanguageName(), OnLanguageClicked,
                                    new Vector2(800f, 120f), CanvasAnchor.Center, new Vector2(0f, 100f),
                                    null, null, 44f, null, LayerName);
        _langLabel = lang.GetComponentInChildren<TextMeshProUGUI>();
        if (Font != null) _langLabel.font = Font;
        Track("Setup_Language");

        MakeButton("Setup_Continue", "menu.continue", BuildNameStep, new Vector2(0f, -200f));
    }

    private void OnLanguageClicked()
    {
        int count = 0;
        foreach (string _ in Translator.Languages) count++;
        if (count == 0) return;

        int next = (Translator.CurrentIndex + 1) % count;
        if (Translator.SetLanguage(next)) EventBus.RaiseLanguageChanged();
    }
    // ───────────── step 2: name ─────────────

    private void BuildNameStep()
    {
        ClearStep();

        Label("Setup_NameTitle", Translator.Get("social.enter_name"), new Vector2(0f, 300f));

        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        RectTransform rt = _cm.Adopt(go.GetComponent<RectTransform>(), LayerName, "Setup_NameInput");
        Track("Setup_NameInput");
        CanvasManager.SetAnchor(rt, CanvasAnchor.Center, new Vector2(0f, 100f), new Vector2(800f, 120f));

        _input = go.GetComponent<TMP_InputField>();
        _input.lineType = TMP_InputField.LineType.SingleLine;
        _input.characterLimit = MaxNameLength;
        _input.textComponent.fontSize = 44f;
        _input.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
        if (_input.placeholder is TMP_Text ph)
        {
            ph.fontSize = 44f;
            ph.text = L(Translator.Get("social.enter_name"));
            Bind(ph, "social.enter_name");
        }
        ApplyFont(go);

        MakeButton("Setup_Confirm", Translator.Get("common.confirm"), OnConfirm, new Vector2(0f, -200f));

        _input.ActivateInputField();
    }

    private void OnConfirm()
    {
        string playerName = _input != null ? _input.text.Trim() : "";
        if (string.IsNullOrEmpty(playerName))
        {
            CanvasManager.Toast(L(Translator.Get("msg.name_empty")));
            return;
        }

        PlayerData data = PlayerManager.Instance.Data;
        data.PlayerName = playerName;
        data.IsCreated = true;
        PlayerManager.Instance.Save();

        CloseUI();
        SetupCompleted?.Invoke(playerName);

        if (DestroyGameObjectWhenDone) Destroy(gameObject);
        else Destroy(this);
    }

    // ───────────── helpers ─────────────

    private TextMeshProUGUI Label(string name, string key, Vector2 offset)
    {
        TextMeshProUGUI t = _cm.AddText(name, L(key), 56f, TextColor, CanvasAnchor.Center, offset,
                                        new Vector2(1400f, 140f), TextAlignmentOptions.Center, LayerName, Font);
        Track(name);
        Bind(t, key);
        return t;
    }

    private Button MakeButton(string name, string key, UnityAction onClick, Vector2 offset)
    {
        Button b = _cm.AddButton(name, L(key), onClick, new Vector2(600f, 130f), CanvasAnchor.Center, offset,
                                 null, null, 48f, null, LayerName);
        TextMeshProUGUI label = b.GetComponentInChildren<TextMeshProUGUI>();
        if (Font != null) label.font = Font;
        Track(name);
        Bind(label, key);
        return b;
    }

    private void ApplyFont(GameObject root)
    {
        if (Font == null) return;
        foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true)) t.font = Font; // true = includes the hidden dropdown template
    }

    private static string L(string key) => Translator.GetOr(key, key);

    private void Track(string name) => _stepElements.Add(name);
    private void Bind(TMP_Text text, string key) => _bound.Add(new Bound { Text = text, Key = key });

    private void RefreshTexts()
    {
        foreach (Bound b in _bound)
            if (b.Text != null) b.Text.text = L(b.Key);
        if (_langLabel != null) _langLabel.text = CurrentLanguageName();
    }

    /// <summary>Removes the elements of the current step (the background stays).</summary>
    private void ClearStep()
    {
        foreach (string n in _stepElements) _cm.Remove(n);
        _stepElements.Clear();
        _bound.Clear();
        _input = null;
        _langLabel = null;
    }

    /// <summary>Destroys everything this setup created, including its layer. Safe to call more than once.</summary>
    private void CloseUI()
    {
        _stepElements.Clear();
        _bound.Clear();
        _input = null;
        if (CanvasManager.Exists && CanvasManager.Instance.HasLayer(LayerName))
            CanvasManager.Instance.RemoveLayer(LayerName);
    }
}