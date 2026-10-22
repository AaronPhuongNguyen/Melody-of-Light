using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Put it on an Image that uses a material with the "UI/EnergyCubes" shader.
/// Max = number of cubes. Value = current energy (can be fractional).
/// The cubes are always square and fit inside the Image's rect; the rect is never resized.
///   energyBar.SetMax(6);  energyBar.SetValue(4);
/// </summary>
[RequireComponent(typeof(Image))]
public class EnergyBar : MonoBehaviour
{
    [Tooltip("Material using UI/EnergyCubes. A private copy is made so several bars don't share values.")]
    public Material Source;

    [Min(1)] public int Max = 3;
    [Min(0)] public float Value = 3;

    [Header("Behaviour")]
    [Range(0f, 1f), Tooltip("Where the cubes sit when the rect is wider than they need: 0 left, 0.5 center, 1 right.")]
    public float AlignX = 0.5f;
    [Tooltip("Units per second the display moves toward Value. <= 0 = instant.")]
    public float SmoothSpeed = 6f;

    private static readonly int SegmentsId = Shader.PropertyToID("_Segments");
    private static readonly int ValueId = Shader.PropertyToID("_Value");
    private static readonly int AspectId = Shader.PropertyToID("_Aspect");
    private static readonly int AlignXId = Shader.PropertyToID("_AlignX");

    private Image _image;
    private Material _mat;
    private RectTransform _rt;
    private float _shown;

    private void Awake()
    {
        _image = GetComponent<Image>();
        _rt = (RectTransform)transform;
        if (Source == null) Source = _image.material;

        _mat = new Material(Source);
        _image.material = _mat;
        _shown = Mathf.Clamp(Value, 0f, Max);
        Apply();
    }

    private void OnDestroy() { if (_mat != null) Destroy(_mat); }

    private void OnRectTransformDimensionsChange() { if (_mat != null) Apply(); }

    private void OnValidate() { if (_mat != null) Apply(); }

    private void Update()
    {
        float target = Mathf.Clamp(Value, 0f, Max);
        if (Mathf.Approximately(_shown, target)) return;
        _shown = SmoothSpeed <= 0f ? target : Mathf.MoveTowards(_shown, target, SmoothSpeed * Time.unscaledDeltaTime);
        Apply();
    }

    public void SetMax(int max, bool refill = false)
    {
        Max = Mathf.Max(1, max);
        Value = refill ? Max : Mathf.Min(Value, Max);
        Apply();
    }

    public void SetValue(float value) => Value = Mathf.Clamp(value, 0f, Max);

    public void Add(float amount) => SetValue(Value + amount);

    private void Apply()
    {
        if (_mat == null) return;

        float h = Mathf.Max(1f, _rt.rect.height);
        _mat.SetFloat(SegmentsId, Max);
        _mat.SetFloat(ValueId, _shown);
        _mat.SetFloat(AspectId, _rt.rect.width / h);
        _mat.SetFloat(AlignXId, AlignX);
    }
}