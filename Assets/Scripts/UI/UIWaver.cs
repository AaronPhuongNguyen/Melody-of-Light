using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Helper for the "UI/WaveTail" shader. Add it to the same GameObject as the Image / RawImage.
///  - Writes 0..1 local coordinates + aspect ratio into UV1, so the shader works with atlas sprites,
///    sliced images and preserve-aspect, independent of how the sprite is packed.
///  - Enables the TexCoord1 channel on the Canvas automatically.
///  - Optionally feeds unscaled time to the shader (waves keep moving when Time.timeScale = 0).
/// Order relative to UIGradientFade does not matter (UV1 is interpolated when the mesh is sliced).
/// </summary>
[AddComponentMenu("UI/Effects/Wave Tail UV (for UI/WaveTail shader)")]
[RequireComponent(typeof(Graphic))]
public class UIWaveTailUV : BaseMeshEffect
{
    [Tooltip("Drive the shader's unscaled time (enable 'Use Unscaled Time' on the material to use it).")]
    [SerializeField] private bool driveUnscaledTime = true;

    private static readonly int UnscaledTimeId = Shader.PropertyToID("_WaveUnscaledTime");

    protected override void OnEnable()
    {
        base.OnEnable();
        EnableChannel();
    }

    protected override void OnCanvasHierarchyChanged()
    {
        base.OnCanvasHierarchyChanged();
        EnableChannel();
    }

    private void Update()
    {
        if (driveUnscaledTime)
            Shader.SetGlobalFloat(UnscaledTimeId, Time.unscaledTime % 100000f);
    }

    private void EnableChannel()
    {
        if (graphic == null) return;
        Canvas c = graphic.canvas;
        if (c == null) return;

        if ((c.additionalShaderChannels & AdditionalCanvasShaderChannels.TexCoord1) == 0)
        {
            c.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            graphic.SetVerticesDirty();
        }
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;

        int count = vh.currentVertCount;
        UIVertex v = new UIVertex();

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < count; i++)
        {
            vh.PopulateUIVertex(ref v, i);
            if (v.position.x < min.x) min.x = v.position.x;
            if (v.position.y < min.y) min.y = v.position.y;
            if (v.position.x > max.x) max.x = v.position.x;
            if (v.position.y > max.y) max.y = v.position.y;
        }

        float w = Mathf.Max(max.x - min.x, 1e-5f);
        float h = Mathf.Max(max.y - min.y, 1e-5f);
        float aspect = w / h;

        for (int i = 0; i < count; i++)
        {
            vh.PopulateUIVertex(ref v, i);
            v.uv1 = new Vector4((v.position.x - min.x) / w, (v.position.y - min.y) / h, aspect, 0f);
            vh.SetUIVertex(v, i);
        }
    }
}