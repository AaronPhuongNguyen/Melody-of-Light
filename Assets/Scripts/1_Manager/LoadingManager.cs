using System;
using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Loading screen + step tracker.
///
/// Generators inside the loaded scene report their work like this:
///     LoadingManager.ReportTotal(100);                 // in Awake/Start: "I will do 100 steps"
///     LoadingManager.ReportStep("Trees");              // after each unit of work (label shows "Loading Trees: 42%")
/// Both are null-safe, so the scene still runs when played directly without a LoadingManager.
///
/// Flow of a scene load:
///   1. show canvas  2. load async (activation held at 90%)  3. activate scene
///   4. wait `prepareFrames` frames so Awake/Start of generators run and register their steps
///   5. wait until CurrentStep >= TotalStep  6. fill bar to 100%, fade out, hide canvas.
///
/// Setup: root GameObject with this script, a child Canvas (optionally with CanvasGroup),
/// a Slider and a TMP_Text. Do not put this script on the same object as something you
/// want to keep deactivated. If the manager lives inside the scene that gets replaced,
/// keep "Dont Destroy On Load" enabled (the manager must be a root object for that).
/// </summary>
[DefaultExecutionOrder(-98)]
public class LoadingManager : MonoBehaviour
{
    public const string DefaultAsset = "Asset";
    public static LoadingManager Instance { get; private set; }

    // ????????????? UI ?????????????
    [Header("UI")]
    [SerializeField] private Canvas loadingCanvas;
    [Tooltip("Optional. Used for fade in / out.")]
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private Slider slider;
    [SerializeField] private TMP_Text label;
    [Tooltip("{0} = current asset, {1} = percent. Avoid raw < > (TextMeshPro treats them as tags; wrap in <noparse> if needed).")]
    [SerializeField] private string labelFormat = "Loading <{0}>: {1}%";
    [SerializeField] private int sortingOrder = 999;

    // ????????????? Behaviour ?????????????
    [Header("Behaviour")]
    [SerializeField] private bool dontDestroyOnLoad = true;
    [Tooltip("Frames to wait after the scene is activated so scene generators can Awake/Start and register steps.")]
    [SerializeField, Min(0)] private int prepareFrames = 2;
    [Tooltip("Share of the bar used by the scene load itself. The rest is driven by steps.")]
    [SerializeField, Range(0f, 1f)] private float sceneWeight = 0.3f;
    [SerializeField, Min(0.1f)] private float smoothSpeed = 1.5f;
    [SerializeField, Min(0f)] private float minimumDisplayTime = 0.5f;
    [SerializeField, Min(0f)] private float fadeDuration = 0.25f;
    [Tooltip("If no step changes for this many seconds, loading finishes anyway. 0 = wait forever.")]
    [SerializeField, Min(0f)] private float stallTimeout = 30f;

    // ????????????? Events ?????????????
    public event Action OnLoadStarted;
    public event Action<float> OnProgressChanged;
    public event Action<string> OnAssetChanged;
    public event Action OnLoadCompleted;

    // ????????????? State ?????????????
    private int _totalStep;
    private int _currentStep;
    private string _asset = DefaultAsset;
    private float _sceneFrac;
    private bool _sceneMode;
    private bool _finishing;
    private bool _isLoading;
    private float _display;
    private int _lastPct = -1;
    private string _lastAsset;

    public int TotalStep => _totalStep;
    public int CurrentStep => _currentStep;
    public string CurrentAsset => _asset;
    public bool IsLoading => _isLoading;
    public float DisplayProgress => _display;

    /// <summary>Raw (unsmoothed) progress 0..1.</summary>
    public float Progress
    {
        get
        {
            float step = _totalStep > 0 ? Mathf.Clamp01((float)_currentStep / _totalStep) : (_finishing ? 1f : 0f);
            float w = _sceneMode ? sceneWeight : 0f;
            return Mathf.Clamp01(w * _sceneFrac + (1f - w) * step);
        }
    }

    // ????????????????????????? Lifecycle ?????????????????????????

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (loadingCanvas == null) loadingCanvas = GetComponentInChildren<Canvas>(true);
        if (canvasGroup == null && loadingCanvas != null) canvasGroup = loadingCanvas.GetComponent<CanvasGroup>();
        if (loadingCanvas != null) loadingCanvas.sortingOrder = sortingOrder;
        if (slider != null) { slider.minValue = 0f; slider.maxValue = 1f; slider.wholeNumbers = false; slider.interactable = false; }

        SetVisible(false);

        if (dontDestroyOnLoad)
        {
            if (transform.parent != null) transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (!_isLoading) return;

        float target = Progress;
        if (target > _display) // bar only moves forward
        {
            _display = Mathf.MoveTowards(_display, target, smoothSpeed * Time.unscaledDeltaTime);
            OnProgressChanged?.Invoke(_display);
        }
        ApplyUI(false);
    }

    // ????????????????????????? Steps ?????????????????????????

    /// <summary>Register more work. Optionally announce what is about to load.</summary>
    public void AddTotalStep(int amount = 1, string asset = null)
    {
        if (amount <= 0) return;
        _totalStep += amount;
        if (asset != null) SetAsset(asset);
    }

    /// <summary>Report finished work. The asset name is shown in the label.</summary>
    public void AddCurrentStep(string asset = DefaultAsset, int amount = 1)
    {
        if (amount <= 0) return;
        _currentStep += amount;
        if (_currentStep > _totalStep) _totalStep = _currentStep; // never let current exceed total
        SetAsset(asset);
    }

    public void AddCurrentStep(int amount) => AddCurrentStep(DefaultAsset, amount);

    public void SetAsset(string asset)
    {
        string a = string.IsNullOrEmpty(asset) ? DefaultAsset : asset;
        if (a == _asset) return;
        _asset = a;
        OnAssetChanged?.Invoke(_asset);
    }

    public void ResetSteps()
    {
        _totalStep = 0;
        _currentStep = 0;
        _sceneFrac = 0f;
        _asset = DefaultAsset;
    }

    // Null-safe statics for generators / any script.
    public static void ReportTotal(int amount = 1, string asset = null)
    {
        if (Instance != null) Instance.AddTotalStep(amount, asset);
    }

    public static void ReportStep(string asset = DefaultAsset, int amount = 1)
    {
        if (Instance != null) Instance.AddCurrentStep(asset, amount);
    }

    // ????????????????????????? Public load API ?????????????????????????
    // All return false if a load is already running or the target is invalid.

    /// <summary>Load by scene name or path.</summary>
    public bool LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single, bool setActive = false)
    {
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError($"[LoadingManager] Scene '{sceneName}' can't be loaded. Is it in Build Settings?");
            return false;
        }
        string name = Path.GetFileNameWithoutExtension(sceneName);
        return Begin(SceneWork(() => SceneManager.LoadSceneAsync(sceneName, mode), name, mode == LoadSceneMode.Additive && setActive), name, true);
    }

    /// <summary>Load by Build Settings index.</summary>
    public bool LoadScene(int buildIndex, LoadSceneMode mode = LoadSceneMode.Single, bool setActive = false)
    {
        if (!Application.CanStreamedLevelBeLoaded(buildIndex))
        {
            Debug.LogError($"[LoadingManager] Build index {buildIndex} can't be loaded.");
            return false;
        }
        string name = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(buildIndex));
        return Begin(SceneWork(() => SceneManager.LoadSceneAsync(buildIndex, mode), name, mode == LoadSceneMode.Additive && setActive), name, true);
    }

    /// <summary>Load by Scene struct (uses its path, so it also reloads an already loaded scene).</summary>
    public bool LoadScene(Scene scene, LoadSceneMode mode = LoadSceneMode.Single, bool setActive = false)
    {
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
        {
            Debug.LogError("[LoadingManager] Invalid Scene (no path to load from).");
            return false;
        }
        return LoadScene(scene.path, mode, setActive);
    }

    public bool ReloadCurrentScene() => LoadScene(SceneManager.GetActiveScene(), LoadSceneMode.Single);

    public bool LoadNextScene(bool wrap = true)
    {
        int next = SceneManager.GetActiveScene().buildIndex + 1;
        if (next >= SceneManager.sceneCountInBuildSettings)
        {
            if (!wrap) { Debug.LogWarning("[LoadingManager] No next scene in Build Settings."); return false; }
            next = 0;
        }
        return LoadScene(next, LoadSceneMode.Single);
    }

    public bool LoadPreviousScene(bool wrap = true)
    {
        int prev = SceneManager.GetActiveScene().buildIndex - 1;
        if (prev < 0)
        {
            if (!wrap) { Debug.LogWarning("[LoadingManager] No previous scene in Build Settings."); return false; }
            prev = SceneManager.sceneCountInBuildSettings - 1;
        }
        return LoadScene(prev, LoadSceneMode.Single);
    }

    public bool UnloadScene(string sceneName)
    {
        if (!SceneManager.GetSceneByName(sceneName).isLoaded)
        {
            Debug.LogWarning($"[LoadingManager] Scene '{sceneName}' is not loaded.");
            return false;
        }
        return Begin(UnloadWork(() => SceneManager.UnloadSceneAsync(sceneName), sceneName), sceneName, true);
    }

    public bool UnloadScene(int buildIndex)
    {
        var s = SceneManager.GetSceneByBuildIndex(buildIndex);
        return s.isLoaded ? UnloadScene(s.name) : false;
    }

    /// <summary>
    /// Run any custom work behind the loading screen (no scene involved). The routine can call
    /// AddTotalStep / AddCurrentStep to drive the bar.
    /// </summary>
    public bool Load(IEnumerator work, string asset = DefaultAsset) => Begin(work, asset, false);

    // ????????????????????????? Core routines ?????????????????????????

    private bool Begin(IEnumerator work, string asset, bool sceneMode)
    {
        if (_isLoading)
        {
            Debug.LogWarning("[LoadingManager] Already loading.");
            return false;
        }
        _isLoading = true; // set now so a second call in the same frame is rejected
        StartCoroutine(MainRoutine(work, asset, sceneMode));
        return true;
    }

    private IEnumerator MainRoutine(IEnumerator work, string asset, bool sceneMode)
    {
        ResetSteps();
        _asset = string.IsNullOrEmpty(asset) ? DefaultAsset : asset;
        _sceneMode = sceneMode;
        _finishing = false;
        _display = 0f;
        _lastPct = -1;
        float startTime = Time.unscaledTime;

        SetVisible(true);
        ApplyUI(true);
        yield return Fade(0f, 1f);
        OnLoadStarted?.Invoke();

        if (work != null) yield return StartCoroutine(work);

        // let the freshly activated scene Awake/Start and register its steps
        for (int i = 0; i < prepareFrames; i++) yield return null;

        // wait for every registered step
        int lastCur = _currentStep, lastTot = _totalStep;
        float lastChange = Time.unscaledTime;
        while (_currentStep < _totalStep)
        {
            if (_currentStep != lastCur || _totalStep != lastTot)
            {
                lastCur = _currentStep; lastTot = _totalStep;
                lastChange = Time.unscaledTime;
            }
            else if (stallTimeout > 0f && Time.unscaledTime - lastChange > stallTimeout)
            {
                Debug.LogWarning($"[LoadingManager] No progress for {stallTimeout}s ({_currentStep}/{_totalStep}). Finishing anyway.");
                break;
            }
            yield return null;
        }

        // finish
        _finishing = true;
        while (_display < 0.999f || Time.unscaledTime - startTime < minimumDisplayTime) yield return null;
        _display = 1f;
        ApplyUI(false);
        OnProgressChanged?.Invoke(1f);

        yield return Fade(1f, 0f);
        SetVisible(false);
        _isLoading = false;
        _sceneMode = false;
        OnLoadCompleted?.Invoke();
    }

    private IEnumerator SceneWork(Func<AsyncOperation> start, string sceneLabel, bool setActive)
    {
        SetAsset(sceneLabel);
        AsyncOperation op = start();
        if (op == null) { _sceneFrac = 1f; yield break; }

        op.allowSceneActivation = false;
        while (op.progress < 0.9f)
        {
            _sceneFrac = Mathf.Clamp01(op.progress / 0.9f);
            yield return null;
        }
        _sceneFrac = 1f;

        op.allowSceneActivation = true;   // activate: Awake of scene objects runs here
        while (!op.isDone) yield return null;

        if (setActive)
            SceneManager.SetActiveScene(SceneManager.GetSceneAt(SceneManager.sceneCount - 1));
    }

    private IEnumerator UnloadWork(Func<AsyncOperation> start, string sceneLabel)
    {
        SetAsset(sceneLabel);
        AsyncOperation op = start();
        if (op == null) { _sceneFrac = 1f; yield break; }
        while (!op.isDone)
        {
            _sceneFrac = Mathf.Clamp01(op.progress);
            yield return null;
        }
        _sceneFrac = 1f;
    }

    // ????????????????????????? UI helpers ?????????????????????????

    private void ApplyUI(bool force)
    {
        int pct = Mathf.RoundToInt(_display * 100f);
        if (slider != null) slider.value = _display;
        if (label != null && (force || pct != _lastPct || _asset != _lastAsset))
            label.text = string.Format(labelFormat, _asset, pct);
        _lastPct = pct;
        _lastAsset = _asset;
    }

    private void SetVisible(bool visible)
    {
        if (loadingCanvas != null)
        {
            // if the canvas is on the same object as this script, never deactivate the object
            if (loadingCanvas.gameObject == gameObject) loadingCanvas.enabled = visible;
            else loadingCanvas.gameObject.SetActive(visible);
        }
        if (canvasGroup != null)
        {
            canvasGroup.blocksRaycasts = visible;
            if (!visible) canvasGroup.alpha = 0f;
        }
    }

    private IEnumerator Fade(float from, float to)
    {
        if (canvasGroup == null) yield break;
        if (fadeDuration <= 0f) { canvasGroup.alpha = to; yield break; }

        float t = 0f;
        canvasGroup.alpha = from;
        while (t < fadeDuration)
        {
            t += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, t / fadeDuration);
            yield return null;
        }
        canvasGroup.alpha = to;
    }
}