using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Static color / alpha gradient ACROSS an Image (head → tail). No animation.
/// Default: head = full color, tail = alpha 0. Fully customizable with a Gradient.
///
/// Works on Image, RawImage, legacy Text (as a mesh effect) and TextMeshPro text (via vertex colors).
/// Add it to the same GameObject as the Image / text. It also previews in the editor.
///
/// Gradient: the LEFT key (time 0) is the HEAD, the RIGHT key (time 1) is the TAIL.
/// The quad of an Image is sliced along the gradient axis so gradients with many keys render smoothly.
/// </summary>
[AddComponentMenu("UI/Effects/Gradient Fade")]
[RequireComponent(typeof(Graphic))]
public class UIGradientFade : BaseMeshEffect
{
    public enum Direction { LeftToRight, RightToLeft, TopToBottom, BottomToTop, Custom }
    public enum BlendMode { Multiply, Replace }

    [Header("Gradient")]
    [Tooltip("Left key = head, right key = tail. Use alpha keys to fade out, color keys to tint.")]
    [SerializeField] private Gradient gradient = CreateFade(Color.white, new Color(1f, 1f, 1f, 0f));

    [Header("Direction")]
    [Tooltip("Direction the gradient travels: head at the start side, tail at the end side.")]
    [SerializeField] private Direction direction = Direction.LeftToRight;
    [Tooltip("Used when Direction = Custom. 0 = left→right, 90 = bottom→top, 180 = right→left, -90 = top→bottom.")]
    [SerializeField, Range(-180f, 180f)] private float customAngle = 0f;
    [Tooltip("Head in the centre, tail on both edges.")]
    [SerializeField] private bool mirror = false;

    [Header("Range")]
    [Tooltip("Where the head ends: everything before this stays head color.")]
    [SerializeField, Range(0f, 1f)] private float fadeStart = 0f;
    [Tooltip("Where the tail starts: everything after this stays tail color.")]
    [SerializeField, Range(0f, 1f)] private float fadeEnd = 1f;

    [Header("Blend")]
    [Tooltip("Multiply: gradient × the existing color (keeps your Image tint). Replace: gradient overrides it.")]
    [SerializeField] private BlendMode blend = BlendMode.Multiply;
    [SerializeField, Range(0f, 1f)] private float strength = 1f;
    [Tooltip("Slices per axis for Images. Higher = smoother multi-key gradients (extra cuts are added at every key).")]
    [SerializeField, Range(4, 64)] private int resolution = 16;

    // ───────────── Internals ─────────────
    private struct PV { public UIVertex v; public float t; }

    private static readonly List<UIVertex> s_Stream = new List<UIVertex>();
    private static readonly List<UIVertex> s_Out = new List<UIVertex>();
    private static readonly List<PV> s_A = new List<PV>();
    private static readonly List<PV> s_B = new List<PV>();
    private static readonly List<float> s_Cuts = new List<float>();

    private float _dx = 1f, _dy, _pMin, _pRange = 1f;
    private TMP_Text _tmp;

    private float Angle
    {
        get
        {
            switch (direction)
            {
                case Direction.LeftToRight: return 0f;
                case Direction.BottomToTop: return 90f;
                case Direction.RightToLeft: return 180f;
                case Direction.TopToBottom: return -90f;
                default: return customAngle;
            }
        }
    }

    // ═════════════════════════ Public API ═════════════════════════

    public Gradient Gradient { get => gradient; set { gradient = value; Refresh(); } }

    public void SetGradient(Gradient g) { gradient = g; Refresh(); }

    /// <summary>Quick two-point gradient: head color → tail color (alpha included).</summary>
    public void SetColors(Color head, Color tail) { gradient = CreateFade(head, tail); Refresh(); }

    public void SetDirection(Direction d) { direction = d; Refresh(); }
    public void SetCustomAngle(float degrees) { direction = Direction.Custom; customAngle = degrees; Refresh(); }
    public void SetStrength(float s) { strength = Mathf.Clamp01(s); Refresh(); }

    public void Refresh()
    {
        if (graphic == null) return;
        if (graphic is TMP_Text t) t.SetAllDirty();
        else graphic.SetVerticesDirty();
    }

    public static Gradient CreateFade(Color head, Color tail)
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(head, 0f), new GradientColorKey(tail, 1f) },
            new[] { new GradientAlphaKey(head.a, 0f), new GradientAlphaKey(tail.a, 1f) });
        return g;
    }

    // ═════════════════════════ Lifecycle ═════════════════════════

    protected override void OnEnable()
    {
        base.OnEnable();
        _tmp = GetComponent<TMP_Text>();
        if (_tmp != null)
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTMPTextChanged);
            _tmp.SetAllDirty();
        }
    }

    protected override void OnDisable()
    {
        if (_tmp != null)
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTMPTextChanged);
            _tmp.SetAllDirty(); // regenerate without the gradient
        }
        base.OnDisable();
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        if (fadeEnd < fadeStart) fadeEnd = fadeStart;
        Refresh();
    }
#endif

    // ═════════════════════════ Image / legacy Text ═════════════════════════

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;
        if (graphic is TMP_Text) return; // TMP is handled through vertex colors below

        s_Stream.Clear();
        vh.GetUIVertexStream(s_Stream);
        if (s_Stream.Count < 3) return;

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < s_Stream.Count; i++)
        {
            Vector3 p = s_Stream[i].position;
            if (p.x < min.x) min.x = p.x;
            if (p.y < min.y) min.y = p.y;
            if (p.x > max.x) max.x = p.x;
            if (p.y > max.y) max.y = p.y;
        }

        SetupProjection(min, max);
        BuildCuts();

        s_Out.Clear();
        for (int i = 0; i + 2 < s_Stream.Count; i += 3)
            SliceTriangle(s_Stream[i], s_Stream[i + 1], s_Stream[i + 2]);

        vh.Clear();
        vh.AddUIVertexTriangleStream(s_Out);
    }

    private void SliceTriangle(UIVertex a, UIVertex b, UIVertex c)
    {
        float ta = Proj(a.position), tb = Proj(b.position), tc = Proj(c.position);
        float tmin = Mathf.Clamp01(Mathf.Min(ta, Mathf.Min(tb, tc)));
        float tmax = Mathf.Clamp01(Mathf.Max(ta, Mathf.Max(tb, tc)));
        int first = FindBand(tmin), last = FindBand(tmax);

        for (int k = first; k <= last; k++)
        {
            float lo = s_Cuts[k], hi = s_Cuts[k + 1];

            s_A.Clear();
            s_A.Add(new PV { v = a, t = ta });
            s_A.Add(new PV { v = b, t = tb });
            s_A.Add(new PV { v = c, t = tc });

            Clip(s_A, s_B, lo, true);
            if (s_B.Count < 3) continue;
            Clip(s_B, s_A, hi, false);
            if (s_A.Count < 3) continue;

            // evaluate slightly inside the band so hard (Fixed) gradient steps stay crisp
            float eps = Mathf.Min(1e-4f, (hi - lo) * 0.25f);
            for (int i = 0; i < s_A.Count; i++)
            {
                PV p = s_A[i];
                UIVertex v = p.v;
                v.color = Colorize(v.color, Mathf.Clamp(p.t, lo + eps, hi - eps));
                p.v = v;
                s_A[i] = p;
            }

            for (int i = 1; i < s_A.Count - 1; i++)
            {
                s_Out.Add(s_A[0].v);
                s_Out.Add(s_A[i].v);
                s_Out.Add(s_A[i + 1].v);
            }
        }
    }

    private static void Clip(List<PV> input, List<PV> output, float level, bool keepAbove)
    {
        output.Clear();
        int n = input.Count;
        for (int i = 0; i < n; i++)
        {
            PV cur = input[i];
            PV nxt = input[(i + 1) % n];
            bool curIn = keepAbove ? cur.t >= level : cur.t <= level;
            bool nxtIn = keepAbove ? nxt.t >= level : nxt.t <= level;

            if (curIn) output.Add(cur);
            if (curIn != nxtIn)
            {
                float k = (level - cur.t) / (nxt.t - cur.t);
                output.Add(new PV { v = LerpV(cur.v, nxt.v, k), t = level });
            }
        }
    }

    private static UIVertex LerpV(UIVertex a, UIVertex b, float k)
    {
        UIVertex r = new UIVertex();
        r.position = Vector3.Lerp(a.position, b.position, k);
        r.normal = Vector3.Lerp(a.normal, b.normal, k);
        r.tangent = Vector4.Lerp(a.tangent, b.tangent, k);
        r.color = Color32.Lerp(a.color, b.color, k);
        r.uv0 = Vector4.Lerp(a.uv0, b.uv0, k);
        r.uv1 = Vector4.Lerp(a.uv1, b.uv1, k);
        r.uv2 = Vector4.Lerp(a.uv2, b.uv2, k);
        r.uv3 = Vector4.Lerp(a.uv3, b.uv3, k);
        return r;
    }

    private static int FindBand(float t)
    {
        int k = 0;
        for (int i = 0; i < s_Cuts.Count - 1; i++)
            if (s_Cuts[i] <= t) k = i;
        return Mathf.Min(k, s_Cuts.Count - 2);
    }

    // ═════════════════════════ TextMeshPro ═════════════════════════

    private void OnTMPTextChanged(Object obj)
    {
        if (obj == _tmp) ApplyTMP();
    }

    private void ApplyTMP()
    {
        if (_tmp == null || !IsActive()) return;
        TMP_TextInfo info = _tmp.textInfo;
        if (info == null || info.characterCount == 0) return;

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        bool any = false;

        for (int i = 0; i < info.characterCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;
            any = true;
            min = Vector2.Min(min, ch.bottomLeft);
            max = Vector2.Max(max, ch.topRight);
        }
        if (!any) return;

        SetupProjection(min, max);

        for (int i = 0; i < info.characterCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;

            TMP_MeshInfo mi = info.meshInfo[ch.materialReferenceIndex];
            Color32[] cols = mi.colors32;
            Vector3[] vs = mi.vertices;

            for (int j = 0; j < 4; j++)
            {
                int idx = ch.vertexIndex + j;
                cols[idx] = Colorize(cols[idx], Proj(vs[idx]));
            }
        }

        _tmp.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }

    // ═════════════════════════ Shared math ═════════════════════════

    private void SetupProjection(Vector2 min, Vector2 max)
    {
        float rad = Angle * Mathf.Deg2Rad;
        _dx = Mathf.Cos(rad);
        _dy = Mathf.Sin(rad);

        float p0 = min.x * _dx + min.y * _dy;
        float p1 = max.x * _dx + min.y * _dy;
        float p2 = min.x * _dx + max.y * _dy;
        float p3 = max.x * _dx + max.y * _dy;

        _pMin = Mathf.Min(Mathf.Min(p0, p1), Mathf.Min(p2, p3));
        float pMax = Mathf.Max(Mathf.Max(p0, p1), Mathf.Max(p2, p3));
        _pRange = Mathf.Max(pMax - _pMin, 1e-5f);
    }

    /// <summary>Position → 0..1 along the gradient axis (0 = head side, 1 = tail side).</summary>
    private float Proj(Vector3 p) => (p.x * _dx + p.y * _dy - _pMin) / _pRange;

    /// <summary>Axis position → position inside the Gradient (applies mirror and range).</summary>
    private float Remap(float t0)
    {
        float t = mirror ? Mathf.Abs(2f * Mathf.Clamp01(t0) - 1f) : Mathf.Clamp01(t0);
        return Mathf.Clamp01((t - fadeStart) / Mathf.Max(fadeEnd - fadeStart, 1e-5f));
    }

    private Color32 Colorize(Color32 orig, float t0)
    {
        Color g = gradient.Evaluate(Remap(t0));
        Color o = orig;
        Color res = blend == BlendMode.Multiply ? o * g : g;
        return Color.Lerp(o, res, strength);
    }

    /// <summary>Slice positions along the axis: uniform steps plus an exact cut at every gradient key.</summary>
    private void BuildCuts()
    {
        s_Cuts.Clear();
        int n = Mathf.Max(1, resolution);
        for (int i = 0; i <= n; i++) s_Cuts.Add(i / (float)n);

        AddKeyCut(0f);
        AddKeyCut(1f);
        foreach (var k in gradient.colorKeys) AddKeyCut(k.time);
        foreach (var k in gradient.alphaKeys) AddKeyCut(k.time);

        s_Cuts.Sort();
        for (int i = s_Cuts.Count - 1; i > 0; i--)
        {
            if (s_Cuts[i] - s_Cuts[i - 1] < 1e-4f)
                s_Cuts.RemoveAt(i - 1 > 0 ? i - 1 : i); // keep first (0) and last (1)
        }
    }

    private void AddKeyCut(float keyTime)
    {
        float m = fadeStart + keyTime * (fadeEnd - fadeStart);
        if (mirror)
        {
            AddCut(0.5f - m * 0.5f);
            AddCut(0.5f + m * 0.5f);
        }
        else AddCut(m);
    }

    private static void AddCut(float x)
    {
        if (x > 0f && x < 1f) s_Cuts.Add(x);
    }
}