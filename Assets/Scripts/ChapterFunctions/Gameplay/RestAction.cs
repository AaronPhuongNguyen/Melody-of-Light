using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Put it on a Button. Click → screen slowly fades to black → "gameplay.isResting" appears →
/// Day++ and save → everything fades away again. The overlay is built once by CanvasManager and
/// cached for the whole run (CanvasManager survives scene loads).
/// </summary>
[RequireComponent(typeof(Button))]
public class SleepHandler : MonoBehaviour
{
    [SerializeField] private bool DebugMode;

    [Header("Text")]
    [Tooltip("Your own font. Only used when the overlay is first built.")]
    public TMP_FontAsset Font;
    public string RestingKey = "gameplay.isResting";
    public float TextSize = 64f;

    [Header("Timing (seconds)")]
    public float FadeToBlack = 2f;
    public float TextFade = 0.6f;
    [Tooltip("How long the resting text stays on the black screen.")]
    public float RestingHold = 2f;
    public float FadeFromBlack = 1.5f;

    [Header("Rules")]
    public int DaysPerSleep = 1;
    public bool SaveAfterSleep = true;

    /// <summary>Raised after the day was added. Arg: the new day.</summary>
    public static event Action<int> Slept;

    public static bool IsSleeping { get; private set; }

    private const string LayerName = "Sleep";
    private const string BgName = "Sleep_Bg";
    private const string TextName = "Sleep_Text";

    private Button _button;
    private CanvasManager _cm;

    private void Awake()
    {
        _button = GetComponent<Button>();
        _button.onClick.AddListener(Sleep);
    }

    private void OnDestroy()
    {
        if (_button != null) _button.onClick.RemoveListener(Sleep);
    }

    private void OnDisable()
    {
        // Scene unloaded mid-sleep: don't leave a black screen behind.
        if (!IsSleeping) return;
        IsSleeping = false;
        if (CanvasManager.Exists) CanvasManager.Instance.SetLayerVisible(LayerName, false);
    }
    private void LateUpdate()
    {
        if (_button == null) return;
        if (PlayerManager.Instance == null) return;

        if(!DebugMode) _button.interactable = PlayerManager.Instance.Data.Energy < PlayerManager.Instance.Data.MaxEnergy * (1/3);
    }

    public void Sleep()
    {
        if (IsSleeping) return;
        StartCoroutine(SleepRoutine());
    }

    private IEnumerator SleepRoutine()
    {
        IsSleeping = true;
        _button.interactable = false;

        _cm = CanvasManager.Instance;
        Image bg;
        TextMeshProUGUI label;
        EnsureOverlay(out bg, out label);

        // reset the cached overlay and show it
        CanvasManager.SetAlpha(bg, 0f);
        CanvasManager.SetAlpha(label, 0f);
        label.text = Translator.GetOr(RestingKey, RestingKey);
        _cm.SetLayerVisible(LayerName, true);

        // 1. slowly dark (the panel blocks clicks while it is visible)
        yield return _cm.FadeGraphic(bg, 1f, FadeToBlack, CanvasEase.EaseInOut);

        // 2. fully dark → resting text, day passes
        yield return _cm.FadeGraphic(label, 1f, TextFade);
        AdvanceDay();
        yield return new WaitForSecondsRealtime(RestingHold);

        // 3. wake up
        yield return _cm.FadeGraphic(label, 0f, TextFade);
        yield return _cm.FadeGraphic(bg, 0f, FadeFromBlack, CanvasEase.EaseInOut);

        _cm.SetLayerVisible(LayerName, false);
        _button.interactable = true;
        IsSleeping = false;
    }

    private void AdvanceDay()
    {
        PlayerManager pm = PlayerManager.Instance;
        if (pm == null)
        {
            Debug.LogWarning("[SleepHandler] No PlayerManager, the day was not advanced.", this);
            return;
        }

        pm.Data.Day += DaysPerSleep;
        pm.Data.Energy = pm.Data.MaxEnergy;
        if (SaveAfterSleep) pm.Save();
        Slept?.Invoke(pm.Data.Day);
    }

    /// <summary>Builds the overlay the first time; afterwards the cached elements are reused.</summary>
    private void EnsureOverlay(out Image bg, out TextMeshProUGUI label)
    {
        _cm.GetLayer(LayerName); // created above the other user layers, below Toast / Fade

        bg = _cm.Get<Image>(BgName);
        if (bg == null)
            bg = _cm.AddPanel(BgName, new Color(0f, 0f, 0f, 0f), null, CanvasAnchor.Stretch, default, LayerName, true);

        label = _cm.Get<TextMeshProUGUI>(TextName);
        if (label == null)
            label = _cm.AddText(TextName, "", TextSize, Color.white, CanvasAnchor.Center, default,
                                new Vector2(1400f, 200f), TextAlignmentOptions.Center, LayerName, Font);
    }
}