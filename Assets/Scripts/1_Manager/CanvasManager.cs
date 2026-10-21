using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Self-building top-most canvas for UI created from code (images, text, buttons, fades, toasts, HUD bits...).
/// No setup needed: the first call to CanvasManager.Instance creates it (Screen Space Overlay, highest sorting order,
/// 1920x1080 reference resolution). You can also drop it on a GameObject and tweak it in the Inspector.
///
///   CanvasManager.Instance.AddImage("logo", sprite, anchor: CanvasAnchor.TopRight, offset: new Vector2(-40, -40));
///   CanvasManager.Instance.AddText("hint", "Press E", 40, Color.white, CanvasAnchor.BottomCenter, new Vector2(0, 100));
///   CanvasManager.Instance.AddButton("play", "Play", () => Debug.Log("Hi"), anchor: CanvasAnchor.Center);
///   CanvasManager.Toast("Saved!");                                    // fades in/out and removes itself
///   CanvasManager.Instance.FadeOut(0.5f, null, () => LoadScene());    // black screen fade (blocks input)
///   CanvasManager.Instance.ShowFullscreenImage(sprite, hold: 2f);     // splash / cutscene card
///   CanvasManager.Instance.Remove("logo");  Get&lt;Image&gt;("logo");  Clear("Foreground");  ClearAll();
///
/// Layers: named containers ordered inside the canvas ("Background" &lt; "Default" &lt; "Foreground" &lt; "Overlay" &lt; "Toast" &lt; "Fade").
///   CreateLayer("Cinematic", 250);  SetLayerVisible("Cinematic", false);  AddImage(..., layer: "Cinematic");
///
/// Dynamic: Tween(...) / MoveTo / ScaleTo / FadeElement / PopIn, Adopt(existing UI), Spawn(prefab), Follow(world target),
/// runtime changes of resolution / sorting order / render mode, BringToFront() above any other canvas.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster))]
public class CanvasManager : MonoBehaviour
{
    // ????????????? singleton (auto-created) ?????????????
    private static CanvasManager _instance;
    private static bool _quitting;

    /// <summary>The manager. Created automatically on first access (Play mode only).</summary>
    public static CanvasManager Instance
    {
        get
        {
            if (_instance == null && !_quitting && Application.isPlaying)
                new GameObject("[CanvasManager]").AddComponent<CanvasManager>(); // Awake assigns _instance
            return _instance;
        }
    }

    public static bool Exists => _instance != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // supports "Enter Play Mode without domain reload"
        _instance = null;
        _quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    private static void OnQuitting() => _quitting = true;

    public const string DefaultLayerName = "Default";
    public const string ToastLayerName = "Toast";
    public const string FadeLayerName = "Fade";
    private const string FadeElementName = "__Fade";

    // ????????????? settings ?????????????
    [Header("Canvas")]
    public RenderMode renderMode = RenderMode.ScreenSpaceOverlay;
    [Tooltip("Only for Screen Space - Camera. Empty = Camera.main.")]
    public Camera worldCamera;
    public float planeDistance = 100f;
    [Tooltip("Sorting order of the canvas. Highest allowed by Unity is 32767.")]
    public int sortingOrder = short.MaxValue;
    [Tooltip("Optional sorting layer name. Empty = keep the default sorting layer.")]
    public string sortingLayerName = "";
    [Tooltip("After every scene load, raise the sorting order above any other canvas on the same sorting layer.")]
    public bool keepOnTop = true;

    [Header("Scaler")]
    public Vector2 referenceResolution = new Vector2(1920f, 1080f);
    public CanvasScaler.ScreenMatchMode matchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
    [Range(0f, 1f), Tooltip("0 = match width, 1 = match height.")]
    public float match = 0.5f;
    public float referencePixelsPerUnit = 100f;

    [Header("Behaviour")]
    public bool dontDestroyOnLoad = true;
    [Tooltip("Create an EventSystem when the scene has none (needed for buttons).")]
    public bool ensureEventSystem = true;
    [Tooltip("Layers created at startup, in order (bottom ? top). 'Default' is created on demand when missing.")]
    public string[] defaultLayers = { "Background", "Default", "Foreground", "Overlay" };

    // ????????????? events ?????????????
    public event Action<GameObject> ElementCreated;
    public event Action<string> ElementRemoved;

    // ????????????? state ?????????????
    private readonly Dictionary<string, GameObject> _elements = new Dictionary<string, GameObject>(StringComparer.Ordinal);
    private readonly Dictionary<string, CanvasLayer> _layers = new Dictionary<string, CanvasLayer>(StringComparer.Ordinal);
    private readonly List<CanvasLayer> _layerList = new List<CanvasLayer>();
    private Canvas _canvas;
    private CanvasScaler _scaler;
    private GraphicRaycaster _raycaster;
    private CanvasGroup _rootGroup;
    private Image _fade;
    private Coroutine _fadeRoutine;
    private int _layerSeq;
    private int _autoId;
    private bool _ready;

    public Canvas Canvas => _canvas;
    public CanvasScaler Scaler => _scaler;
    public GraphicRaycaster Raycaster => _raycaster;
    public CanvasGroup RootGroup => _rootGroup;
    public RectTransform RootRect => (RectTransform)transform;
    public IReadOnlyList<CanvasLayer> Layers => _layerList;

    /// <summary>Canvas size in canvas units (? reference resolution, depends on the match mode and the screen aspect).</summary>
    public Vector2 CanvasSize => RootRect.rect.size;
    public float ScaleFactor => _canvas != null ? _canvas.scaleFactor : 1f;
    public bool IsVisible => _canvas != null && _canvas.enabled;
    /// <summary>Camera to use for screen ? canvas conversions (null for Overlay).</summary>
    public Camera UICamera => _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;

    #region Unity
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;

        _canvas = GetComponent<Canvas>();
        _scaler = GetComponent<CanvasScaler>();
        _raycaster = GetComponent<GraphicRaycaster>();
        _rootGroup = GetComponent<CanvasGroup>();
        if (_rootGroup == null) _rootGroup = gameObject.AddComponent<CanvasGroup>();

        if (dontDestroyOnLoad)
        {
            if (transform.parent != null) transform.SetParent(null, false);
            DontDestroyOnLoad(gameObject);
        }

        ApplyCanvasSettings();
        BuildDefaultLayers();
        StartCoroutine(EnsureEventSystemRoutine());
        _ready = true;
    }

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void OnValidate()
    {
        if (Application.isPlaying && _ready) ApplyCanvasSettings();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (keepOnTop) BringToFront();
        StartCoroutine(EnsureEventSystemRoutine());
    }
    #endregion

    // ????????????????????????? Canvas settings ?????????????????????????

    /// <summary>Apply the public fields to the Canvas and the CanvasScaler. Called automatically by the setters below.</summary>
    public void ApplyCanvasSettings()
    {
        if (_canvas == null) return;

        _canvas.renderMode = renderMode;
        if (renderMode != RenderMode.ScreenSpaceOverlay)
        {
            _canvas.worldCamera = worldCamera != null ? worldCamera : Camera.main;
            _canvas.planeDistance = planeDistance;
        }

        _canvas.sortingOrder = Mathf.Clamp(sortingOrder, short.MinValue, short.MaxValue);
        if (!string.IsNullOrEmpty(sortingLayerName))
        {
            if (SortingLayer.IsValid(SortingLayer.NameToID(sortingLayerName))) _canvas.sortingLayerName = sortingLayerName;
            else Debug.LogWarning($"[CanvasManager] Sorting layer '{sortingLayerName}' does not exist.", this);
        }

        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _scaler.referenceResolution = referenceResolution;
        _scaler.screenMatchMode = matchMode;
        _scaler.matchWidthOrHeight = match;
        _scaler.referencePixelsPerUnit = referencePixelsPerUnit;
    }

    public void SetReferenceResolution(Vector2 resolution) { referenceResolution = resolution; ApplyCanvasSettings(); }
    public void SetReferenceResolution(float width, float height) => SetReferenceResolution(new Vector2(width, height));
    public void SetMatch(float value) { match = Mathf.Clamp01(value); ApplyCanvasSettings(); }
    public void SetMatchMode(CanvasScaler.ScreenMatchMode mode) { matchMode = mode; ApplyCanvasSettings(); }
    public void SetSortingOrder(int order) { sortingOrder = order; ApplyCanvasSettings(); }
    public void SetSortingLayer(string layerName) { sortingLayerName = layerName; ApplyCanvasSettings(); }

    public void SetRenderMode(RenderMode mode, Camera cam = null)
    {
        renderMode = mode;
        if (cam != null) worldCamera = cam;
        ApplyCanvasSettings();
    }

    /// <summary>Show / hide the whole canvas (nothing is destroyed).</summary>
    public void SetVisible(bool visible) { if (_canvas != null) _canvas.enabled = visible; }
    /// <summary>Global alpha of everything on the canvas.</summary>
    public void SetAlpha(float alpha) { if (_rootGroup != null) _rootGroup.alpha = Mathf.Clamp01(alpha); }
    /// <summary>Enable / disable ALL pointer interaction with this canvas.</summary>
    public void SetInteractable(bool interactable) { if (_raycaster != null) _raycaster.enabled = interactable; }

    /// <summary>Raise the sorting order above every other root canvas on the same sorting layer (up to 32767).</summary>
    public void BringToFront()
    {
        if (_canvas == null) return;
        int target = Mathf.Clamp(sortingOrder, short.MinValue, short.MaxValue);
        int maxOther = int.MinValue;

#if UNITY_2022_2_OR_NEWER
        Canvas[] all = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
#else
        Canvas[] all = FindObjectsOfType<Canvas>();
#endif
        foreach (Canvas c in all)
        {
            if (c == _canvas || !c.isRootCanvas || c.renderMode != _canvas.renderMode) continue;
            if (c.sortingLayerID != _canvas.sortingLayerID) continue;
            maxOther = Mathf.Max(maxOther, c.sortingOrder);
        }
        if (maxOther >= target) target = maxOther + 1;
        _canvas.sortingOrder = Mathf.Min(target, short.MaxValue);
    }

    private IEnumerator EnsureEventSystemRoutine()
    {
        if (!ensureEventSystem) yield break;
        yield return null; // let the scene's own EventSystem register itself first
        if (EventSystem.current != null) yield break;

        var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
        if (dontDestroyOnLoad) DontDestroyOnLoad(go);
    }

    // ????????????????????????? Layers ?????????????????????????

    private void BuildDefaultLayers()
    {
        if (defaultLayers != null)
            for (int i = 0; i < defaultLayers.Length; i++)
                if (!string.IsNullOrWhiteSpace(defaultLayers[i])) CreateLayer(defaultLayers[i].Trim(), i * 100);
        GetLayer(DefaultLayerName);
    }

    public bool HasLayer(string layerName) => !string.IsNullOrEmpty(layerName) && _layers.ContainsKey(layerName);

    /// <summary>Get a layer (null / empty = "Default"). Created on demand unless createIfMissing is false.</summary>
    public CanvasLayer GetLayer(string layerName = null, bool createIfMissing = true)
    {
        if (string.IsNullOrEmpty(layerName)) layerName = DefaultLayerName;
        if (_layers.TryGetValue(layerName, out CanvasLayer layer)) return layer;
        return createIfMissing ? CreateLayer(layerName) : null;
    }

    /// <summary>Create a layer. Higher order = drawn on top. Omit the order to put it above every existing layer.</summary>
    public CanvasLayer CreateLayer(string layerName, int order = int.MinValue)
    {
        if (string.IsNullOrEmpty(layerName)) layerName = DefaultLayerName;

        if (_layers.TryGetValue(layerName, out CanvasLayer existing))
        {
            if (order != int.MinValue) SetLayerOrder(layerName, order);
            return existing;
        }

        // built-in system layers always sit above user layers
        if (order == int.MinValue)
        {
            if (layerName == FadeLayerName) order = 10000;
            else if (layerName == ToastLayerName) order = 9000;
            else
            {
                order = 0;
                foreach (CanvasLayer l in _layerList)
                    if (l.Order < 9000) order = Mathf.Max(order, l.Order + 100);
            }
        }

        var go = new GameObject("Layer_" + layerName, typeof(RectTransform), typeof(CanvasGroup));
        var rt = (RectTransform)go.transform;
        rt.SetParent(transform, false);
        StretchToParent(rt);

        var layer = new CanvasLayer(layerName, order, rt, go.GetComponent<CanvasGroup>(), _layerSeq++);
        _layers[layerName] = layer;
        _layerList.Add(layer);
        SortLayers();
        return layer;
    }

    public void SetLayerOrder(string layerName, int order)
    {
        CanvasLayer l = GetLayer(layerName, false);
        if (l == null) return;
        l.Order = order;
        SortLayers();
    }

    public void SetLayerVisible(string layerName, bool visible)
    {
        CanvasLayer l = GetLayer(layerName, false);
        if (l != null) l.Root.gameObject.SetActive(visible);
    }

    public void SetLayerAlpha(string layerName, float alpha)
    {
        CanvasLayer l = GetLayer(layerName, false);
        if (l != null) l.Group.alpha = Mathf.Clamp01(alpha);
    }

    /// <summary>Enable / disable pointer interaction for everything inside one layer.</summary>
    public void SetLayerInteractable(string layerName, bool interactable)
    {
        CanvasLayer l = GetLayer(layerName, false);
        if (l == null) return;
        l.Group.interactable = interactable;
        l.Group.blocksRaycasts = interactable;
    }

    /// <summary>Destroy a layer and everything inside it.</summary>
    public bool RemoveLayer(string layerName)
    {
        CanvasLayer l = GetLayer(layerName, false);
        if (l == null) return false;
        Clear(layerName);
        _layers.Remove(l.Name);
        _layerList.Remove(l);
        Destroy(l.Root.gameObject);
        return true;
    }

    private void SortLayers()
    {
        _layerList.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        for (int i = 0; i < _layerList.Count; i++) _layerList[i].Root.SetSiblingIndex(i);
    }

    // ????????????????????????? Create elements ?????????????????????????

    /// <summary>Empty RectTransform to build your own UI hierarchy under (add Images, layout groups... yourself).</summary>
    public RectTransform AddEmpty(string name = null, CanvasAnchor anchor = CanvasAnchor.Stretch, Vector2 offset = default,
                                  Vector2? size = null, string layer = null)
        => NewElement("Empty", name, layer, anchor, offset, size ?? Vector2.zero, out _);

    /// <summary>Image from a sprite. No size = the sprite's native size. Not a raycast target by default (decoration).</summary>
    public Image AddImage(string name, Sprite sprite, Color? color = null, Vector2? size = null,
                          CanvasAnchor anchor = CanvasAnchor.Center, Vector2 offset = default, string layer = null,
                          bool raycastTarget = false, bool preserveAspect = true)
    {
        RectTransform rt = NewElement("Image", name, layer, anchor, offset, size ?? Vector2.zero, out _);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color ?? Color.white;
        img.raycastTarget = raycastTarget;
        img.preserveAspect = preserveAspect;
        if (size == null && sprite != null && anchor != CanvasAnchor.Stretch) img.SetNativeSize();
        return img;
    }

    /// <summary>Image from Resources/{path} (sprite).</summary>
    public Image AddImageFromResources(string name, string resourcesPath, Vector2? size = null,
                                       CanvasAnchor anchor = CanvasAnchor.Center, Vector2 offset = default, string layer = null)
    {
        Sprite s = Resources.Load<Sprite>(resourcesPath);
        if (s == null) Debug.LogWarning($"[CanvasManager] No Sprite at Resources/{resourcesPath}.", this);
        return AddImage(name, s, null, size, anchor, offset, layer);
    }

    /// <summary>Colored rectangle. Default = full-screen (dim background, flash, letterbox...).</summary>
    public Image AddPanel(string name = null, Color? color = null, Vector2? size = null, CanvasAnchor anchor = CanvasAnchor.Stretch,
                          Vector2 offset = default, string layer = null, bool raycastTarget = false)
    {
        Vector2 s = size ?? (anchor == CanvasAnchor.Stretch ? Vector2.zero : new Vector2(400f, 300f));
        RectTransform rt = NewElement("Panel", name, layer, anchor, offset, s, out _);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color ?? new Color(0f, 0f, 0f, 0.5f);
        img.raycastTarget = raycastTarget;
        return img;
    }

    /// <summary>RawImage from any Texture (RenderTexture, webcam, generated texture...). No size = texture size.</summary>
    public RawImage AddRawImage(string name, Texture texture, Vector2? size = null, CanvasAnchor anchor = CanvasAnchor.Center,
                                Vector2 offset = default, string layer = null, bool raycastTarget = false)
    {
        Vector2 s = size ?? (texture != null ? new Vector2(texture.width, texture.height) : new Vector2(256f, 256f));
        RectTransform rt = NewElement("RawImage", name, layer, anchor, offset, s, out _);
        var raw = rt.gameObject.AddComponent<RawImage>();
        raw.texture = texture;
        raw.raycastTarget = raycastTarget;
        return raw;
    }

    /// <summary>TextMeshPro label.</summary>
    public TextMeshProUGUI AddText(string name, string text, float fontSize = 36f, Color? color = null,
                                   CanvasAnchor anchor = CanvasAnchor.Center, Vector2 offset = default, Vector2? size = null,
                                   TextAlignmentOptions alignment = TextAlignmentOptions.Center, string layer = null,
                                   TMP_FontAsset font = null)
    {
        RectTransform rt = NewElement("Text", name, layer, anchor, offset, size ?? new Vector2(800f, 120f), out _);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = fontSize;
        t.color = color ?? Color.white;
        t.alignment = alignment;
        t.raycastTarget = false;
        if (font != null) t.font = font;
        return t;
    }

    /// <summary>Button with a background Image and a centered TMP label. Is a raycast target.</summary>
    public Button AddButton(string name, string label, UnityAction onClick = null, Vector2? size = null,
                            CanvasAnchor anchor = CanvasAnchor.Center, Vector2 offset = default, Color? background = null,
                            Color? textColor = null, float fontSize = 32f, Sprite sprite = null, string layer = null)
    {
        RectTransform rt = NewElement("Button", name, layer, anchor, offset, size ?? new Vector2(320f, 90f), out _);

        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        img.color = background ?? new Color(0.15f, 0.15f, 0.18f, 0.92f);

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        if (onClick != null) btn.onClick.AddListener(onClick);

        var labelGo = new GameObject("Label", typeof(RectTransform));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        StretchToParent(lrt);
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.color = textColor ?? Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return btn;
    }

    /// <summary>Instantiate a prefab (any component on it) under a layer and track it by name.</summary>
    public T Spawn<T>(T prefab, string name = null, string layer = null) where T : Component
    {
        if (prefab == null) return null;
        string n = string.IsNullOrEmpty(name) ? prefab.name + "_" + (++_autoId) : name;
        Remove(n);
        T inst = Instantiate(prefab, GetLayer(layer).Root, false);
        inst.name = n;
        _elements[n] = inst.gameObject;
        ElementCreated?.Invoke(inst.gameObject);
        return inst;
    }

    /// <summary>Move an existing UI object under a layer and track it (name defaults to its own name).</summary>
    public RectTransform Adopt(RectTransform existing, string layer = null, string name = null, bool worldPositionStays = false)
    {
        if (existing == null) return null;
        string n = string.IsNullOrEmpty(name) ? existing.name : name;
        if (_elements.TryGetValue(n, out GameObject old) && old != null && old != existing.gameObject) Remove(n);
        existing.SetParent(GetLayer(layer).Root, worldPositionStays);
        existing.name = n;
        _elements[n] = existing.gameObject;
        ElementCreated?.Invoke(existing.gameObject);
        return existing;
    }

    /// <summary>
    /// Base for every element: a RectTransform under the layer, anchored and registered by name.
    /// Using a name that already exists REPLACES the old element.
    /// </summary>
    private RectTransform NewElement(string prefix, string name, string layer, CanvasAnchor anchor, Vector2 offset, Vector2 size,
                                     out string finalName)
    {
        finalName = string.IsNullOrEmpty(name) ? prefix + "_" + (++_autoId) : name;
        Remove(finalName);

        var go = new GameObject(finalName, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(GetLayer(layer).Root, false);
        SetAnchor(rt, anchor, offset, size);

        _elements[finalName] = go;
        ElementCreated?.Invoke(go);
        return rt;
    }

    // ????????????????????????? Find / remove ?????????????????????????

    public GameObject Get(string name)
        => !string.IsNullOrEmpty(name) && _elements.TryGetValue(name, out GameObject go) && go != null ? go : null;

    public T Get<T>(string name) where T : Component
    {
        GameObject go = Get(name);
        return go != null ? go.GetComponent<T>() : null;
    }

    public bool Has(string name) => Get(name) != null;

    public IEnumerable<string> Names
    {
        get { foreach (var kv in _elements) if (kv.Value != null) yield return kv.Key; }
    }

    public int ElementCount
    {
        get { int n = 0; foreach (var kv in _elements) if (kv.Value != null) n++; return n; }
    }

    public bool Show(string name) => SetElementActive(name, true);
    public bool Hide(string name) => SetElementActive(name, false);

    public bool Toggle(string name)
    {
        GameObject go = Get(name);
        return go != null && SetElementActive(name, !go.activeSelf);
    }

    private bool SetElementActive(string name, bool active)
    {
        GameObject go = Get(name);
        if (go == null) return false;
        go.SetActive(active);
        return true;
    }

    /// <summary>Destroy a tracked element (optionally after a delay in scaled seconds).</summary>
    public bool Remove(string name, float delay = 0f)
    {
        if (string.IsNullOrEmpty(name) || !_elements.TryGetValue(name, out GameObject go)) return false;
        _elements.Remove(name);
        if (go != null)
        {
            if (delay > 0f) Destroy(go, delay); else Destroy(go);
        }
        ElementRemoved?.Invoke(name);
        return true;
    }

    /// <summary>Destroy a tracked element by reference. Returns false when it is not tracked (nothing destroyed).</summary>
    public bool Remove(GameObject go)
    {
        if (go == null) return false;
        foreach (var kv in _elements)
            if (kv.Value == go) return Remove(kv.Key);
        return false;
    }

    /// <summary>Destroy everything inside a layer. Returns the number of tracked elements removed.</summary>
    public int Clear(string layerName)
    {
        CanvasLayer layer = GetLayer(layerName, false);
        if (layer == null) return 0;

        var names = new List<string>();
        foreach (var kv in _elements)
            if (kv.Value != null && kv.Value.transform.parent == layer.Root) names.Add(kv.Key);

        int n = 0;
        foreach (string s in names) if (Remove(s)) n++;

        for (int i = layer.Root.childCount - 1; i >= 0; i--) Destroy(layer.Root.GetChild(i).gameObject); // untracked leftovers
        PurgeDead();
        return n;
    }

    /// <summary>Destroy all user content. The Fade and Toast layers are kept unless includeSystem is true.</summary>
    public void ClearAll(bool includeSystem = false)
    {
        foreach (CanvasLayer l in new List<CanvasLayer>(_layerList))
        {
            if (!includeSystem && (l.Name == FadeLayerName || l.Name == ToastLayerName)) continue;
            Clear(l.Name);
        }
    }

    /// <summary>Move a tracked element to another layer.</summary>
    public bool MoveToLayer(string elementName, string layerName)
    {
        GameObject go = Get(elementName);
        if (go == null) return false;
        go.transform.SetParent(GetLayer(layerName).Root, false);
        return true;
    }

    private void PurgeDead()
    {
        List<string> dead = null;
        foreach (var kv in _elements)
            if (kv.Value == null) { if (dead == null) dead = new List<string>(); dead.Add(kv.Key); }
        if (dead != null) foreach (string k in dead) _elements.Remove(k);
    }

    // ????????????????????????? Positioning helpers ?????????????????????????

    /// <summary>Anchor + offset + size in one call. Stretch ignores offset and size; StretchHorizontal/Vertical use one axis of size.</summary>
    public static void SetAnchor(RectTransform rt, CanvasAnchor anchor, Vector2 offset, Vector2 size)
    {
        if (rt == null) return;
        Vector2 min, max, pivot;
        switch (anchor)
        {
            case CanvasAnchor.TopLeft: min = max = pivot = new Vector2(0f, 1f); break;
            case CanvasAnchor.TopCenter: min = max = pivot = new Vector2(0.5f, 1f); break;
            case CanvasAnchor.TopRight: min = max = pivot = new Vector2(1f, 1f); break;
            case CanvasAnchor.MiddleLeft: min = max = pivot = new Vector2(0f, 0.5f); break;
            case CanvasAnchor.Center: min = max = pivot = new Vector2(0.5f, 0.5f); break;
            case CanvasAnchor.MiddleRight: min = max = pivot = new Vector2(1f, 0.5f); break;
            case CanvasAnchor.BottomLeft: min = max = pivot = new Vector2(0f, 0f); break;
            case CanvasAnchor.BottomCenter: min = max = pivot = new Vector2(0.5f, 0f); break;
            case CanvasAnchor.BottomRight: min = max = pivot = new Vector2(1f, 0f); break;
            case CanvasAnchor.StretchHorizontal:
                min = new Vector2(0f, 0.5f); max = new Vector2(1f, 0.5f); pivot = new Vector2(0.5f, 0.5f); break;
            case CanvasAnchor.StretchVertical:
                min = new Vector2(0.5f, 0f); max = new Vector2(0.5f, 1f); pivot = new Vector2(0.5f, 0.5f); break;
            default:
                min = Vector2.zero; max = Vector2.one; pivot = new Vector2(0.5f, 0.5f); break;
        }

        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot = pivot;

        switch (anchor)
        {
            case CanvasAnchor.Stretch:
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; break;
            case CanvasAnchor.StretchHorizontal:
                rt.anchoredPosition = offset; rt.sizeDelta = new Vector2(0f, size.y); break;
            case CanvasAnchor.StretchVertical:
                rt.anchoredPosition = offset; rt.sizeDelta = new Vector2(size.x, 0f); break;
            default:
                rt.anchoredPosition = offset; rt.sizeDelta = size; break;
        }
    }

    public static void StretchToParent(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
    }

    /// <summary>Screen pixels ? anchoredPosition inside a layer (exact for elements anchored at Center).</summary>
    public Vector2 ScreenToLayerPoint(Vector2 screenPoint, string layer = null)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(GetLayer(layer).Root, screenPoint, UICamera, out Vector2 local);
        return local;
    }

    /// <summary>World position ? anchoredPosition inside a layer. Returns false when the point is behind the camera.</summary>
    public bool TryWorldToLayerPoint(Camera cam, Vector3 world, out Vector2 point, string layer = null)
    {
        point = Vector2.zero;
        if (cam == null) cam = Camera.main;
        if (cam == null) return false;
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0f) return false;
        point = ScreenToLayerPoint(new Vector2(sp.x, sp.y), layer);
        return true;
    }

    /// <summary>Make a UI element follow a world transform (health bars, name tags, markers). Destroyed with its target.</summary>
    public CanvasWorldFollower Follow(RectTransform ui, Transform target, Camera cam = null, Vector3 worldOffset = default,
                                      Vector2 screenOffset = default)
    {
        if (ui == null || target == null) return null;
        var f = ui.GetComponent<CanvasWorldFollower>();
        if (f == null) f = ui.gameObject.AddComponent<CanvasWorldFollower>();
        f.Target = target;
        f.WorldCamera = cam;
        f.WorldOffset = worldOffset;
        f.ScreenOffset = screenOffset;
        return f;
    }

    // ????????????????????????? Tweens ?????????????????????????

    /// <summary>
    /// Generic tween: onUpdate receives 0 ? 1 (eased). Uses unscaled time by default so it works while paused.
    /// Stop it with StopTween(coroutine); onDone is not called when stopped.
    /// </summary>
    public Coroutine Tween(float duration, Action<float> onUpdate, Action onDone = null, CanvasEase ease = CanvasEase.Linear,
                           bool unscaled = true, float delay = 0f)
        => StartCoroutine(TweenRoutine(duration, onUpdate, onDone, ease, unscaled, delay));

    public void StopTween(Coroutine tween) { if (tween != null) StopCoroutine(tween); }

    private IEnumerator TweenRoutine(float duration, Action<float> onUpdate, Action onDone, CanvasEase ease, bool unscaled, float delay)
    {
        if (delay > 0f) yield return WaitRoutine(delay, unscaled);

        if (duration <= 0f)
        {
            onUpdate?.Invoke(1f);
            onDone?.Invoke();
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            t += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
            onUpdate?.Invoke(Evaluate(ease, Mathf.Clamp01(t / duration)));
            yield return null;
        }
        onUpdate?.Invoke(1f);
        onDone?.Invoke();
    }

    private static IEnumerator WaitRoutine(float seconds, bool unscaled)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
            yield return null;
        }
    }

    private static float Evaluate(CanvasEase ease, float t)
    {
        switch (ease)
        {
            case CanvasEase.EaseIn: return t * t;
            case CanvasEase.EaseOut: return 1f - (1f - t) * (1f - t);
            case CanvasEase.EaseInOut: return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
            case CanvasEase.EaseOutBack:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float u = t - 1f;
                    return 1f + c3 * u * u * u + c1 * u * u;
                }
            default: return t;
        }
    }

    public Coroutine FadeGroup(CanvasGroup group, float to, float duration = 0.3f, CanvasEase ease = CanvasEase.Linear,
                               Action onDone = null, float delay = 0f)
    {
        if (group == null) return null;
        float from = group.alpha;
        return Tween(duration, t => { if (group != null) group.alpha = Mathf.Lerp(from, to, t); }, onDone, ease, true, delay);
    }

    /// <summary>Fade any tracked element (adds a CanvasGroup when needed).</summary>
    public Coroutine FadeElement(string name, float to, float duration = 0.3f, CanvasEase ease = CanvasEase.Linear,
                                 Action onDone = null, float delay = 0f)
    {
        GameObject go = Get(name);
        return go == null ? null : FadeGroup(GetOrAddGroup(go), to, duration, ease, onDone, delay);
    }

    public Coroutine FadeGraphic(Graphic graphic, float to, float duration = 0.3f, CanvasEase ease = CanvasEase.Linear,
                                 Action onDone = null, float delay = 0f)
    {
        if (graphic == null) return null;
        float from = graphic.color.a;
        return Tween(duration, t => { if (graphic != null) SetAlpha(graphic, Mathf.Lerp(from, to, t)); }, onDone, ease, true, delay);
    }

    public Coroutine MoveTo(RectTransform rt, Vector2 anchoredPosition, float duration = 0.3f, CanvasEase ease = CanvasEase.EaseOut,
                            Action onDone = null, float delay = 0f)
    {
        if (rt == null) return null;
        Vector2 from = rt.anchoredPosition;
        return Tween(duration, t => { if (rt != null) rt.anchoredPosition = Vector2.LerpUnclamped(from, anchoredPosition, t); },
                     onDone, ease, true, delay);
    }

    public Coroutine ScaleTo(RectTransform rt, Vector3 scale, float duration = 0.3f, CanvasEase ease = CanvasEase.EaseOut,
                             Action onDone = null, float delay = 0f)
    {
        if (rt == null) return null;
        Vector3 from = rt.localScale;
        return Tween(duration, t => { if (rt != null) rt.localScale = Vector3.LerpUnclamped(from, scale, t); }, onDone, ease, true, delay);
    }

    /// <summary>Scale 0 ? 1 with a small overshoot.</summary>
    public Coroutine PopIn(RectTransform rt, float duration = 0.35f, Action onDone = null)
    {
        if (rt == null) return null;
        rt.localScale = Vector3.zero;
        return ScaleTo(rt, Vector3.one, duration, CanvasEase.EaseOutBack, onDone);
    }

    // ????????????????????????? Ready-made effects ?????????????????????????

    /// <summary>Fade in ? wait ? fade out ? destroy. Works on any element (tracked or not).</summary>
    public Coroutine ShowTemporarily(GameObject go, float hold, float fade = 0.25f, Action onDone = null)
        => go == null ? null : StartCoroutine(TempRoutine(go, hold, fade, onDone));

    public Coroutine ShowTemporarily(string name, float hold, float fade = 0.25f, Action onDone = null)
        => ShowTemporarily(Get(name), hold, fade, onDone);

    private IEnumerator TempRoutine(GameObject go, float hold, float fade, Action onDone)
    {
        CanvasGroup g = GetOrAddGroup(go);
        g.alpha = 0f;
        yield return TweenRoutine(fade, t => { if (g != null) g.alpha = t; }, null, CanvasEase.Linear, true, 0f);
        yield return WaitRoutine(hold, true);

        if (g == null) yield break;
        float start = g.alpha;
        yield return TweenRoutine(fade, t => { if (g != null) g.alpha = Mathf.Lerp(start, 0f, t); }, null, CanvasEase.Linear, true, 0f);

        if (go != null && !Remove(go)) Destroy(go);
        onDone?.Invoke();
    }

    /// <summary>
    /// Full-screen picture (splash, cutscene card). hold &lt; 0 = fade in and stay until you Remove it,
    /// otherwise it fades in, holds, fades out and destroys itself.
    /// </summary>
    public Image ShowFullscreenImage(Sprite sprite, float hold = 2f, float fade = 0.5f, string layer = null, string name = null,
                                     bool preserveAspect = true, Action onDone = null)
    {
        Image img = AddImage(name, sprite, Color.white, null, CanvasAnchor.Stretch, default, layer, true, preserveAspect);
        if (hold < 0f)
        {
            CanvasGroup g = GetOrAddGroup(img.gameObject);
            g.alpha = 0f;
            FadeGroup(g, 1f, fade, CanvasEase.Linear, onDone);
        }
        else ShowTemporarily(img.gameObject, hold, fade, onDone);
        return img;
    }

    /// <summary>Static shortcut: CanvasManager.Toast("Saved!").</summary>
    public static GameObject Toast(string message, float duration = 2f)
        => Instance != null ? Instance.ShowToast(message, duration) : null;

    /// <summary>Small auto-sizing message box that fades in, stays, fades out and removes itself.</summary>
    public GameObject ShowToast(string message, float duration = 2f, CanvasAnchor anchor = CanvasAnchor.BottomCenter,
                                Vector2? offset = null, float fontSize = 32f, float maxWidth = 1200f)
    {
        float oy = 0f;
        if (anchor == CanvasAnchor.BottomLeft || anchor == CanvasAnchor.BottomCenter || anchor == CanvasAnchor.BottomRight) oy = 120f;
        else if (anchor == CanvasAnchor.TopLeft || anchor == CanvasAnchor.TopCenter || anchor == CanvasAnchor.TopRight) oy = -120f;

        RectTransform rt = NewElement("Toast", null, ToastLayerName, anchor, offset ?? new Vector2(0f, oy), Vector2.zero, out _);

        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.78f);
        bg.raycastTarget = false;

        var layout = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(36, 36, 18, 18);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var fit = rt.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(rt, false);
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text = message;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        var le = textGo.AddComponent<LayoutElement>();
        le.preferredWidth = Mathf.Min(maxWidth, tmp.GetPreferredValues(message).x + 1f);

        ShowTemporarily(rt.gameObject, duration, 0.25f);
        return rt.gameObject;
    }

    // ????????????? screen fade ?????????????

    private Image EnsureFade()
    {
        if (_fade == null)
            _fade = AddPanel(FadeElementName, new Color(0f, 0f, 0f, 0f), null, CanvasAnchor.Stretch, default, FadeLayerName, false);
        return _fade;
    }

    private void StopFade()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
    }

    /// <summary>Fade the full-screen overlay to an alpha (1 = fully covered). Blocks clicks while it is visible.</summary>
    public Coroutine FadeTo(float alpha, float duration = 0.5f, Color? color = null, bool blockInput = true, Action onDone = null,
                            CanvasEase ease = CanvasEase.Linear)
    {
        Image img = EnsureFade();
        StopFade();

        if (color.HasValue)
        {
            Color c = color.Value;
            c.a = img.color.a;
            img.color = c;
        }

        float from = img.color.a;
        if (blockInput) img.raycastTarget = true;

        _fadeRoutine = Tween(duration,
            t => { if (img != null) SetAlpha(img, Mathf.Lerp(from, alpha, t)); },
            () =>
            {
                if (img != null) img.raycastTarget = blockInput && alpha > 0.001f;
                _fadeRoutine = null;
                onDone?.Invoke();
            }, ease);
        return _fadeRoutine;
    }

    /// <summary>Screen goes to the color (black by default).</summary>
    public Coroutine FadeOut(float duration = 0.5f, Color? color = null, Action onDone = null) => FadeTo(1f, duration, color, true, onDone);

    /// <summary>Screen returns from the color to transparent.</summary>
    public Coroutine FadeIn(float duration = 0.5f, Color? color = null, Action onDone = null) => FadeTo(0f, duration, color, true, onDone);

    /// <summary>Quick colored flash (damage, lightning, photo). Never blocks input.</summary>
    public Coroutine Flash(Color color, float duration = 0.25f, float peakAlpha = 1f)
    {
        Image img = EnsureFade();
        StopFade();
        img.raycastTarget = false;
        color.a = peakAlpha;
        img.color = color;
        _fadeRoutine = Tween(duration, t => { if (img != null) SetAlpha(img, Mathf.Lerp(peakAlpha, 0f, t)); },
                             () => _fadeRoutine = null, CanvasEase.EaseOut);
        return _fadeRoutine;
    }

    // ????????????????????????? Small utilities ?????????????????????????

    public static CanvasGroup GetOrAddGroup(GameObject go)
    {
        var g = go.GetComponent<CanvasGroup>();
        return g != null ? g : go.AddComponent<CanvasGroup>();
    }

    public static void SetAlpha(Graphic graphic, float alpha)
    {
        if (graphic == null) return;
        Color c = graphic.color;
        c.a = alpha;
        graphic.color = c;
    }

    /// <summary>Create a sprite from a texture (centered pivot).</summary>
    public static Sprite ToSprite(Texture2D texture, float pixelsPerUnit = 100f)
        => texture == null ? null : Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);

    // ????????????????????????? Context menu tests ?????????????????????????
    // Right-click the CanvasManager component header in Play mode.

    #region Context Menu Tests
    private bool TestPlaying()
    {
        if (Application.isPlaying) return true;
        Debug.LogWarning("[CanvasManager/Test] Enter Play mode first.", this);
        return false;
    }

    [ContextMenu("Test/Panel + text")]
    private void TestPanelText()
    {
        if (!TestPlaying()) return;
        AddPanel("TestPanel", new Color(0f, 0f, 0f, 0.6f), new Vector2(700f, 260f), CanvasAnchor.Center);
        AddText("TestText", "Hello from CanvasManager", 48f, Color.white, CanvasAnchor.Center);
    }

    [ContextMenu("Test/Button")]
    private void TestButton()
    {
        if (!TestPlaying()) return;
        int clicks = 0;
        Button b = null;
        b = AddButton("TestButton", "Click me", () => b.GetComponentInChildren<TextMeshProUGUI>().text = "Clicked " + (++clicks),
                      null, CanvasAnchor.BottomCenter, new Vector2(0f, 200f));
        PopIn((RectTransform)b.transform);
    }

    [ContextMenu("Test/Toast")]
    private void TestToast() { if (TestPlaying()) ShowToast("This is a toast message"); }

    [ContextMenu("Test/Fade out then in")]
    private void TestFade() { if (TestPlaying()) FadeOut(0.6f, null, () => FadeIn(0.6f)); }

    [ContextMenu("Test/Flash red")]
    private void TestFlash() { if (TestPlaying()) Flash(Color.red, 0.4f, 0.6f); }

    [ContextMenu("Test/Move + fade element")]
    private void TestMove()
    {
        if (!TestPlaying()) return;
        Image img = AddPanel("TestBox", new Color(0.2f, 0.7f, 1f, 1f), new Vector2(160f, 160f), CanvasAnchor.MiddleLeft, new Vector2(80f, 0f));
        MoveTo((RectTransform)img.transform, new Vector2(1700f, 0f), 1.2f, CanvasEase.EaseInOut,
               () => Remove("TestBox"));
    }

    [ContextMenu("Test/Bring to front")]
    private void TestBringToFront() { if (TestPlaying()) { BringToFront(); Debug.Log($"[CanvasManager/Test] sortingOrder = {_canvas.sortingOrder}"); } }

    [ContextMenu("Test/Clear all")]
    private void TestClearAll() { if (TestPlaying()) ClearAll(); }

    [ContextMenu("Test/Log state")]
    private void TestLogState()
    {
        if (!TestPlaying()) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Canvas: {renderMode}, order {_canvas.sortingOrder}, size {CanvasSize}, scale {ScaleFactor:0.###}, visible {IsVisible}");
        sb.AppendLine($"Elements: {ElementCount}   EventSystem: {EventSystem.current != null}");
        foreach (CanvasLayer l in _layerList) sb.AppendLine($"  Layer '{l.Name}' order {l.Order}, {l.Root.childCount} child(ren), active {l.Root.gameObject.activeSelf}");
        Debug.Log("[CanvasManager/Test] " + sb.ToString().TrimEnd());
    }
    #endregion
}

/// <summary>Where an element sits inside its parent. Offset is measured from that anchor, in canvas units.</summary>
public enum CanvasAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    Stretch, StretchHorizontal, StretchVertical
}

public enum CanvasEase { Linear, EaseIn, EaseOut, EaseInOut, EaseOutBack }

/// <summary>A named container inside the canvas. Higher Order is drawn on top.</summary>
public sealed class CanvasLayer
{
    public readonly string Name;
    public int Order;
    public readonly RectTransform Root;
    public readonly CanvasGroup Group;
    internal readonly int Seq;

    internal CanvasLayer(string name, int order, RectTransform root, CanvasGroup group, int seq)
    {
        Name = name; Order = order; Root = root; Group = group; Seq = seq;
    }
}

/// <summary>Keeps a UI element over a world transform. Added by CanvasManager.Follow().</summary>
public class CanvasWorldFollower : MonoBehaviour
{
    public Transform Target;
    [Tooltip("Empty = Camera.main.")] public Camera WorldCamera;
    public Vector3 WorldOffset;
    public Vector2 ScreenOffset;
    public bool HideWhenBehindCamera = true;
    public bool DestroyWithTarget = true;

    private RectTransform _rt;
    private bool _hadTarget;

    private void Awake() => _rt = (RectTransform)transform;

    private void LateUpdate()
    {
        if (Target == null)
        {
            if (_hadTarget && DestroyWithTarget)
            {
                _hadTarget = false;
                if (!(CanvasManager.Exists && CanvasManager.Instance.Remove(gameObject))) Destroy(gameObject);
            }
            return;
        }
        _hadTarget = true;

        Camera cam = WorldCamera != null ? WorldCamera : Camera.main;
        var parent = _rt.parent as RectTransform;
        if (cam == null || parent == null || !CanvasManager.Exists) return;

        Vector3 sp = cam.WorldToScreenPoint(Target.position + WorldOffset);
        if (sp.z <= 0f && HideWhenBehindCamera)
        {
            _rt.anchoredPosition = new Vector2(-99999f, -99999f);
            return;
        }

        _rt.anchorMin = _rt.anchorMax = new Vector2(0.5f, 0.5f);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, new Vector2(sp.x, sp.y) + ScreenOffset,
                                                               CanvasManager.Instance.UICamera, out Vector2 local))
            _rt.anchoredPosition = local;
    }
}