using System;
using UnityEngine;

public enum WallpaperMotion
{
    RandomWaypoint, // glide to random points, pause, repeat (straight or curved paths)
    SmoothNoise,    // endless organic drift from Perlin noise
    Drift,          // wandering heading that slowly turns, steers back from edges
    Bounce,         // constant velocity, bounces off the area edge
    Lissajous       // looping figure-8 / orbit style patterns with random ratios
}

public enum WallpaperAreaShape { Rectangle, Ellipse }

/// <summary>
/// Pans a UI wallpaper (any RectTransform: Image, RawImage, a whole panel) randomly around its ROOT position.
///
/// Root = the RectTransform's anchoredPosition when it first awakes (use SetRoot / RecaptureRoot to change it).
/// Tip: make the wallpaper larger than its parent (or scale it up) and keep "Auto Fit To Parent" on,
/// so the pan range is computed automatically and the edges never show.
/// Assumes the wallpaper is centered in its parent (pivot 0.5, 0.5). Uses your static RNG class for all randomness.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class WallpaperEngine : MonoBehaviour
{
    // ????????????? General ?????????????
    [Header("General")]
    [SerializeField] private WallpaperMotion motion = WallpaperMotion.RandomWaypoint;
    [SerializeField] private bool playOnEnable = true;
    [SerializeField] private bool useUnscaledTime = true;
    [Tooltip("Global speed scale for everything (motion, zoom, sway).")]
    [SerializeField, Min(0f)] private float speedMultiplier = 1f;
    [Tooltip("Seconds to ramp up from standstill when enabled / resumed.")]
    [SerializeField, Min(0f)] private float easeInDuration = 1.5f;
    [Tooltip("Seconds to blend when the motion is restarted or switched, so there is no snapping.")]
    [SerializeField, Min(0f)] private float blendDuration = 1f;

    // ????????????? Area ?????????????
    [Header("Area (relative to root)")]
    [Tooltip("Compute pan range from (wallpaper size x scale) minus parent size, so edges never show.")]
    [SerializeField] private bool autoFitToParent = true;
    [Tooltip("Pixels kept as safety margin when auto fitting.")]
    [SerializeField, Min(0f)] private float fitMargin = 0f;
    [Tooltip("Half-size of the pan area in pixels (used when Auto Fit is off).")]
    [SerializeField] private Vector2 manualExtents = new Vector2(100f, 60f);
    [SerializeField] private WallpaperAreaShape shape = WallpaperAreaShape.Rectangle;
    [Tooltip("Uses only this fraction of the available range.")]
    [SerializeField, Range(0f, 1f)] private float rangeMultiplier = 1f;
    [Tooltip("Shifts the centre of the pan area away from the root.")]
    [SerializeField] private Vector2 areaCenter = Vector2.zero;
    [Tooltip("Parallax layer factor. 1 = full range, 0.5 = half range. Use on several wallpapers for depth.")]
    [SerializeField, Range(0f, 2f)] private float depth = 1f;

    // ????????????? Random Waypoint ?????????????
    [Header("Random Waypoint")]
    [Tooltip("Pixels per second. Also used by Drift and Bounce. A new value is rolled per leg / turn / bounce.")]
    [SerializeField] private Vector2 speedRange = new Vector2(20f, 60f);
    [SerializeField] private Vector2 pauseRange = new Vector2(0f, 1.5f);
    [Tooltip("Minimum distance between two waypoints, in pixels.")]
    [SerializeField, Min(0f)] private float minTravel = 40f;
    [SerializeField] private bool curvedPath = true;
    [SerializeField, Range(0f, 1f)] private float curveAmount = 0.35f;
    [SerializeField] private AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    // ????????????? Drift / Bounce ?????????????
    [Header("Drift / Bounce")]
    [Tooltip("Drift: seconds between picking a new heading.")]
    [SerializeField] private Vector2 turnIntervalRange = new Vector2(2f, 6f);
    [Tooltip("Drift: max heading change per pick (degrees).")]
    [SerializeField, Range(0f, 180f)] private float maxTurnAngle = 70f;
    [Tooltip("Drift: how fast the heading turns (degrees / second).")]
    [SerializeField, Min(1f)] private float turnRate = 60f;
    [Tooltip("Bounce: re-roll speed and add angle jitter after every bounce.")]
    [SerializeField] private bool randomizeOnBounce = true;
    [SerializeField, Range(0f, 90f)] private float bounceJitter = 15f;

    // ????????????? Noise / Lissajous ?????????????
    [Header("Smooth Noise / Lissajous")]
    [Tooltip("Cycles per second. Lower = slower, calmer.")]
    [SerializeField, Min(0f)] private float frequency = 0.05f;
    [Tooltip("Lissajous: X and Y frequency multipliers are rolled from this range.")]
    [SerializeField] private Vector2 lissajousRatioRange = new Vector2(1f, 3f);

    // ????????????? Effects ?????????????
    [Header("Zoom Breathing")]
    [SerializeField] private bool zoomEnabled = false;
    [Tooltip("Scale multiplier range relative to the starting scale.")]
    [SerializeField] private Vector2 zoomRange = new Vector2(1f, 1.08f);
    [SerializeField] private Vector2 zoomPeriodRange = new Vector2(12f, 25f);

    [Header("Rotation Sway")]
    [SerializeField] private bool swayEnabled = false;
    [SerializeField, Range(0f, 15f)] private float swayAngle = 1.5f;
    [SerializeField] private Vector2 swayPeriodRange = new Vector2(10f, 20f);

    [Header("Pointer Parallax")]
    [SerializeField] private bool parallaxEnabled = false;
    [Tooltip("Max pixels shifted (X, Y) when the pointer is at the screen edge. Moves opposite to the pointer.")]
    [SerializeField] private Vector2 parallaxStrength = new Vector2(30f, 20f);
    [SerializeField, Min(0.1f)] private float parallaxSmoothing = 6f;

    // ????????????? Events ?????????????
    /// <summary>Fired in RandomWaypoint mode each time a waypoint is reached.</summary>
    public event Action<Vector2> OnWaypointReached;

    // ????????????? Runtime ?????????????
    private RectTransform _rt;
    private RectTransform _parent;
    private Vector3 _baseScale = Vector3.one;
    private float _baseRotZ;
    private Vector2 _root;

    private Vector2 _extents;
    private Vector2 _offset;       // final offset from (root + areaCenter)
    private Vector2 _modePos;      // raw position produced by the current mode
    private Vector2 _blendFrom;
    private float _blend = 1f;
    private float _ramp;
    private float _time;
    private float _fxTime;
    private bool _playing;
    private Vector2 _parallaxCur;

    // waypoint
    private Vector2 _wpStart, _wpTarget, _wpCtrl;
    private float _wpT, _wpDuration, _wpWait;
    // drift / bounce
    private float _heading, _desiredHeading, _turnTimer, _speedCur;
    private Vector2 _vel;
    // noise / lissajous
    private float _noiseOx, _noiseOy, _lisFx, _lisFy, _lisPx, _lisPy;
    // effects
    private float _zoomPeriod, _zoomPhase, _swayPeriod, _swayPhase;
    // manual override
    private bool _overriding;
    private Vector2 _ovFrom, _ovTo;
    private float _ovT, _ovDur;
    private Action _ovDone;

    // ????????????? Public properties ?????????????
    public bool IsPlaying => _playing;
    public WallpaperMotion Motion => motion;
    public Vector2 Root => _root;
    public Vector2 Offset => _offset;
    public Vector2 Extents => _extents;
    public float SpeedMultiplier { get => speedMultiplier; set => speedMultiplier = Mathf.Max(0f, value); }
    public float RangeMultiplier { get => rangeMultiplier; set => rangeMultiplier = Mathf.Clamp01(value); }

    // ????????????????????????? Lifecycle ?????????????????????????

    private void Awake()
    {
        _rt = (RectTransform)transform;
        _baseScale = _rt.localScale;
        _baseRotZ = _rt.localEulerAngles.z;
        _root = _rt.anchoredPosition;
    }

    private void OnEnable()
    {
        _parent = _rt.parent as RectTransform;
        if (autoFitToParent && _parent == null)
            Debug.LogWarning("[WallpaperEngine] Auto Fit needs a RectTransform parent. Falling back to manual extents.", this);

        _offset = Vector2.zero;
        _parallaxCur = Vector2.zero;
        _ramp = 0f; _time = 0f; _fxTime = 0f;
        _overriding = false;
        _extents = ComputeExtents();

        InitEffects();
        InitMode();
        _playing = playOnEnable;
        Apply();
    }

    private void OnDisable()
    {
        // restore original transform so nothing is left shifted
        _rt.anchoredPosition = _root;
        _rt.localScale = _baseScale;
        _rt.localEulerAngles = new Vector3(0f, 0f, _baseRotZ);
    }

    private void OnValidate()
    {
        speedRange = Sorted(speedRange);
        pauseRange = Sorted(pauseRange);
        turnIntervalRange = Sorted(turnIntervalRange);
        lissajousRatioRange = Sorted(lissajousRatioRange);
        zoomRange = Sorted(zoomRange);
        zoomPeriodRange = Sorted(zoomPeriodRange);
        swayPeriodRange = Sorted(swayPeriodRange);
        zoomPeriodRange.x = Mathf.Max(0.1f, zoomPeriodRange.x);
        swayPeriodRange.x = Mathf.Max(0.1f, swayPeriodRange.x);
    }

    private void Update()
    {
        float raw = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        _extents = ComputeExtents();

        if (_overriding)
        {
            StepOverride(raw);
        }
        else if (_playing)
        {
            _ramp = easeInDuration <= 0f ? 1f : Mathf.Min(1f, _ramp + raw / easeInDuration);
            float k = _ramp * _ramp * (3f - 2f * _ramp);
            float dt = raw * speedMultiplier * k;
            _time += dt;
            _fxTime += dt;

            StepMode(dt);

            if (_blend < 1f) _blend = blendDuration <= 0f ? 1f : Mathf.Min(1f, _blend + raw / blendDuration);
            float b = _blend * _blend * (3f - 2f * _blend);
            _offset = ClampToArea(Vector2.Lerp(_blendFrom, _modePos, b));
        }

        UpdateParallax(raw);
        Apply();
    }

    // ????????????????????????? Public API ?????????????????????????

    public void Play() { if (!_playing) { _playing = true; _ramp = 0f; } }
    public void Pause() => _playing = false;
    public void Toggle() { if (_playing) Pause(); else Play(); }

    /// <summary>Change the motion type at runtime (blends smoothly).</summary>
    public void SetMotion(WallpaperMotion newMotion)
    {
        motion = newMotion;
        InitMode();
    }

    /// <summary>Roll new random values for the current motion (new heading, noise seed, ratios...).</summary>
    public void Randomize()
    {
        InitEffects();
        InitMode();
    }

    /// <summary>Set the root (centre) position of the pan area.</summary>
    public void SetRoot(Vector2 anchoredPosition) => _root = anchoredPosition;

    /// <summary>Use the current anchoredPosition (minus offset) as the new root.</summary>
    public void RecaptureRoot() => _root = _rt.anchoredPosition - areaCenter - _offset - _parallaxCur;

    /// <summary>Manually glide to an offset (relative to area centre). Auto motion resumes afterwards.</summary>
    public void PanTo(Vector2 offset, float duration = 1f, Action onDone = null)
    {
        _ovFrom = _offset;
        _ovTo = ClampToArea(offset);
        _ovT = 0f;
        _ovDur = Mathf.Max(0.01f, duration);
        _ovDone = onDone;
        _overriding = true;
    }

    public void ReturnToCenter(float duration = 1f) => PanTo(Vector2.zero, duration);

    /// <summary>Glide back to the root and stop auto motion.</summary>
    public void StopAndReturn(float duration = 1f)
    {
        _playing = false;
        PanTo(Vector2.zero, duration, () => _playing = false);
    }

    // ????????????????????????? Area ?????????????????????????

    private Vector2 ComputeExtents()
    {
        Vector2 e;
        RectTransform parent = Application.isPlaying ? _parent : transform.parent as RectTransform;

        if (autoFitToParent && parent != null)
        {
            RectTransform rt = (RectTransform)transform;
            Vector3 sc = Application.isPlaying ? _baseScale : rt.localScale;
            float zMin = zoomEnabled ? Mathf.Min(zoomRange.x, zoomRange.y) : 1f;

            Vector2 size = new Vector2(rt.rect.width * Mathf.Abs(sc.x), rt.rect.height * Mathf.Abs(sc.y)) * zMin;
            Vector2 view = parent.rect.size;
            e = (size - view) * 0.5f - Vector2.one * fitMargin;

            if (parallaxEnabled) e -= parallaxStrength;
            if (swayEnabled) e -= Vector2.one * (view.magnitude * 0.5f * Mathf.Sin(swayAngle * Mathf.Deg2Rad));

            e = Vector2.Max(e, Vector2.zero);
        }
        else e = manualExtents;

        return e * rangeMultiplier * depth;
    }

    private float Norm(Vector2 p)
    {
        float ex = Mathf.Max(_extents.x, 0.0001f), ey = Mathf.Max(_extents.y, 0.0001f);
        return shape == WallpaperAreaShape.Rectangle
            ? Mathf.Max(Mathf.Abs(p.x) / ex, Mathf.Abs(p.y) / ey)
            : Mathf.Sqrt(p.x * p.x / (ex * ex) + p.y * p.y / (ey * ey));
    }

    private Vector2 ClampToArea(Vector2 p)
    {
        if (shape == WallpaperAreaShape.Rectangle)
            return new Vector2(Mathf.Clamp(p.x, -_extents.x, _extents.x), Mathf.Clamp(p.y, -_extents.y, _extents.y));
        float n = Norm(p);
        return n > 1f ? p / n : p;
    }

    private Vector2 RandomPointInArea()
    {
        if (shape == WallpaperAreaShape.Rectangle)
            return new Vector2(RNG.GetFloat(-_extents.x, _extents.x), RNG.GetFloat(-_extents.y, _extents.y));

        float a = RNG.GetFloat(0f, Mathf.PI * 2f);
        float r = Mathf.Sqrt(RNG.GetPercent()); // uniform over the ellipse
        return new Vector2(Mathf.Cos(a) * r * _extents.x, Mathf.Sin(a) * r * _extents.y);
    }

    // ????????????????????????? Motion init ?????????????????????????

    private void InitEffects()
    {
        _zoomPeriod = Mathf.Max(0.1f, R(zoomPeriodRange));
        _zoomPhase = RNG.GetFloat(0f, Mathf.PI * 2f);
        _swayPeriod = Mathf.Max(0.1f, R(swayPeriodRange));
        _swayPhase = RNG.GetFloat(0f, Mathf.PI * 2f);
    }

    private void InitMode()
    {
        // stateful modes continue from where we are; stateless ones blend over blendDuration
        _modePos = _offset;
        _blendFrom = _offset;
        _blend = 0f;

        switch (motion)
        {
            case WallpaperMotion.RandomWaypoint:
                _wpWait = 0f;
                PickWaypoint();
                break;

            case WallpaperMotion.Drift:
                _heading = _desiredHeading = RNG.GetFloat(0f, 360f);
                _turnTimer = R(turnIntervalRange);
                _speedCur = R(speedRange);
                break;

            case WallpaperMotion.Bounce:
                float ang = RNG.GetFloat(0f, Mathf.PI * 2f);
                _vel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * R(speedRange);
                break;

            case WallpaperMotion.SmoothNoise:
                _noiseOx = RNG.GetFloat(0f, 1000f);
                _noiseOy = RNG.GetFloat(0f, 1000f);
                break;

            case WallpaperMotion.Lissajous:
                _lisFx = R(lissajousRatioRange);
                _lisFy = R(lissajousRatioRange);
                _lisPx = RNG.GetFloat(0f, Mathf.PI * 2f);
                _lisPy = RNG.GetFloat(0f, Mathf.PI * 2f);
                break;
        }
    }

    // ????????????????????????? Motion step ?????????????????????????

    private void StepMode(float dt)
    {
        switch (motion)
        {
            case WallpaperMotion.RandomWaypoint: StepWaypoint(dt); break;
            case WallpaperMotion.SmoothNoise: StepNoise(); break;
            case WallpaperMotion.Drift: StepDrift(dt); break;
            case WallpaperMotion.Bounce: StepBounce(dt); break;
            case WallpaperMotion.Lissajous: StepLissajous(); break;
        }
    }

    // ---- Random waypoint ----
    private void PickWaypoint()
    {
        _wpStart = _modePos;
        if (_extents.x < 0.01f && _extents.y < 0.01f) { _wpTarget = _wpStart; _wpCtrl = _wpStart; _wpT = 1f; _wpDuration = 1f; return; }

        Vector2 p = RandomPointInArea();
        for (int i = 0; i < 8 && (p - _wpStart).magnitude < minTravel; i++) p = RandomPointInArea();

        _wpTarget = p;
        Vector2 dir = p - _wpStart;
        float speed = Mathf.Max(1f, R(speedRange));
        _wpDuration = Mathf.Max(0.05f, dir.magnitude / speed);
        _wpT = 0f;

        Vector2 mid = (_wpStart + p) * 0.5f;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        _wpCtrl = curvedPath ? mid + perp * RNG.GetFloat(-curveAmount, curveAmount) : mid;
    }

    private void StepWaypoint(float dt)
    {
        if (_wpWait > 0f) { _wpWait -= dt; return; }

        _wpT += dt / _wpDuration;
        float t = Mathf.Clamp01(_wpT);
        float k = (ease != null && ease.length > 0) ? ease.Evaluate(t) : t;

        // quadratic bezier
        Vector2 a = Vector2.LerpUnclamped(_wpStart, _wpCtrl, k);
        Vector2 b = Vector2.LerpUnclamped(_wpCtrl, _wpTarget, k);
        _modePos = Vector2.LerpUnclamped(a, b, k);

        if (_wpT >= 1f)
        {
            _modePos = _wpTarget;
            OnWaypointReached?.Invoke(_wpTarget);
            _wpWait = Mathf.Max(0f, R(pauseRange));
            PickWaypoint();
        }
    }

    // ---- Smooth noise ----
    private void StepNoise()
    {
        float t = _time * frequency;
        float nx = (Mathf.PerlinNoise(_noiseOx + t, 0.37f) * 2f - 1f) * 1.4f; // perlin rarely hits extremes
        float ny = (Mathf.PerlinNoise(_noiseOy + t, 5.71f) * 2f - 1f) * 1.4f;
        _modePos = ClampToArea(new Vector2(nx * _extents.x, ny * _extents.y));
    }

    // ---- Lissajous ----
    private void StepLissajous()
    {
        float t = _time * frequency * Mathf.PI * 2f;
        _modePos = ClampToArea(new Vector2(
            Mathf.Sin(t * _lisFx + _lisPx) * _extents.x,
            Mathf.Sin(t * _lisFy + _lisPy) * _extents.y));
    }

    // ---- Drift ----
    private void StepDrift(float dt)
    {
        _turnTimer -= dt;
        if (_turnTimer <= 0f)
        {
            _desiredHeading = _heading + RNG.GetFloat(-maxTurnAngle, maxTurnAngle);
            _turnTimer = Mathf.Max(0.1f, R(turnIntervalRange));
            _speedCur = R(speedRange);
        }

        _heading = Mathf.MoveTowardsAngle(_heading, _desiredHeading, turnRate * dt);
        float rad = _heading * Mathf.Deg2Rad;
        _modePos += new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * (_speedCur * dt);

        if (Norm(_modePos) > 1f)
        {
            _modePos = ClampToArea(_modePos);
            // steer back toward the centre with some randomness
            _heading = Mathf.Atan2(-_modePos.y, -_modePos.x) * Mathf.Rad2Deg + RNG.GetFloat(-45f, 45f);
            _desiredHeading = _heading;
            _turnTimer = Mathf.Max(0.1f, R(turnIntervalRange));
        }
    }

    // ---- Bounce ----
    private void StepBounce(float dt)
    {
        Vector2 p = _modePos + _vel * dt;
        Vector2 n = Vector2.zero; // outward normal of the hit edge (zero = no hit)

        if (shape == WallpaperAreaShape.Rectangle)
        {
            if ((p.x > _extents.x && _vel.x > 0f) || (p.x < -_extents.x && _vel.x < 0f)) { n.x = Mathf.Sign(p.x); _vel.x = -_vel.x; }
            if ((p.y > _extents.y && _vel.y > 0f) || (p.y < -_extents.y && _vel.y < 0f)) { n.y = Mathf.Sign(p.y); _vel.y = -_vel.y; }
        }
        else if (Norm(p) > 1f)
        {
            float ex = Mathf.Max(_extents.x, 0.0001f), ey = Mathf.Max(_extents.y, 0.0001f);
            Vector2 normal = new Vector2(p.x / (ex * ex), p.y / (ey * ey)).normalized;
            if (Vector2.Dot(_vel, normal) > 0f) { _vel = Vector2.Reflect(_vel, normal); n = normal; }
        }

        p = ClampToArea(p);

        if (n != Vector2.zero && randomizeOnBounce)
        {
            Vector2 dir = (Quaternion.Euler(0f, 0f, RNG.GetFloat(-bounceJitter, bounceJitter)) * (Vector3)_vel).normalized;
            _vel = dir * Mathf.Max(1f, R(speedRange));
            Vector2 nn = n.normalized;
            if (Vector2.Dot(_vel, nn) > 0f) _vel = Vector2.Reflect(_vel, nn); // never jitter back into the wall
        }

        _modePos = p;
    }

    // ????????????????????????? Override (PanTo) ?????????????????????????

    private void StepOverride(float raw)
    {
        _ovT += raw / _ovDur;
        float t = Mathf.Clamp01(_ovT);
        float k = t * t * (3f - 2f * t);
        _offset = Vector2.Lerp(_ovFrom, _ovTo, k);

        if (_ovT >= 1f)
        {
            _overriding = false;
            Action cb = _ovDone;
            _ovDone = null;
            InitMode();
            cb?.Invoke();
        }
    }

    // ????????????????????????? Parallax / Apply ?????????????????????????

    private void UpdateParallax(float raw)
    {
        Vector2 target = Vector2.zero;
        if (parallaxEnabled && Screen.width > 0 && Screen.height > 0)
        {
            Vector2 mp = ReadPointer();
            Vector2 n = new Vector2(mp.x / Screen.width, mp.y / Screen.height) * 2f - Vector2.one;
            n = new Vector2(Mathf.Clamp(n.x, -1f, 1f), Mathf.Clamp(n.y, -1f, 1f));
            target = -Vector2.Scale(n, parallaxStrength) * depth;
        }
        _parallaxCur = Vector2.Lerp(_parallaxCur, target, 1f - Mathf.Exp(-parallaxSmoothing * raw));
    }

    private Vector2 ReadPointer()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = UnityEngine.InputSystem.Mouse.current;
        return m != null ? m.position.ReadValue() : new Vector2(Screen.width, Screen.height) * 0.5f;
#else
        return Input.mousePosition;
#endif
    }

    private void Apply()
    {
        _rt.anchoredPosition = _root + areaCenter + _offset + _parallaxCur;

        if (zoomEnabled)
        {
            float s = 0.5f + 0.5f * Mathf.Sin(_fxTime * Mathf.PI * 2f / _zoomPeriod + _zoomPhase);
            float z = Mathf.Lerp(Mathf.Min(zoomRange.x, zoomRange.y), Mathf.Max(zoomRange.x, zoomRange.y), s);
            _rt.localScale = _baseScale * z;
        }

        if (swayEnabled)
        {
            float a = swayAngle * Mathf.Sin(_fxTime * Mathf.PI * 2f / _swayPeriod + _swayPhase);
            _rt.localEulerAngles = new Vector3(0f, 0f, _baseRotZ + a);
        }
    }

    // ????????????????????????? Helpers ?????????????????????????

    private static float R(Vector2 range) => RNG.GetFloat(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    private static Vector2 Sorted(Vector2 v) => v.x <= v.y ? v : new Vector2(v.y, v.x);

    // ????????????????????????? Gizmos ?????????????????????????

    private void OnDrawGizmosSelected()
    {
        var rt = transform as RectTransform;
        if (rt == null || rt.parent == null) return;

        Vector2 ext = Application.isPlaying ? _extents : ComputeExtents();
        Vector2 root = Application.isPlaying ? _root : rt.anchoredPosition;
        Vector2 centerAnchored = root + areaCenter;

        // world position of the area centre
        Vector3 center = rt.position + rt.parent.TransformVector((Vector3)(centerAnchored - rt.anchoredPosition));
        Vector3 ax = rt.parent.TransformVector(Vector3.right);
        Vector3 ay = rt.parent.TransformVector(Vector3.up);

        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
        if (shape == WallpaperAreaShape.Rectangle)
        {
            Vector3 a = center + ax * -ext.x + ay * -ext.y;
            Vector3 b = center + ax * ext.x + ay * -ext.y;
            Vector3 c = center + ax * ext.x + ay * ext.y;
            Vector3 d = center + ax * -ext.x + ay * ext.y;
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        }
        else
        {
            const int seg = 48;
            Vector3 prev = center + ax * ext.x;
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg * Mathf.PI * 2f;
                Vector3 next = center + ax * (Mathf.Cos(t) * ext.x) + ay * (Mathf.Sin(t) * ext.y);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(center, 6f * rt.lossyScale.x);
    }
}