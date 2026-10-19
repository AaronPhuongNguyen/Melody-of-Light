// UI/WaveTail
// Makes the TAIL of a UI image ripple like waves. The head stays still, the motion grows toward the tail.
// Use with Image / RawImage. For sprites in an atlas (and for correct 0..1 coordinates) add the
// UIWaveTailUV component to the same GameObject. It composes with UIGradientFade (vertex colors).
//
// Features: 3 wave layers (sine / triangle / soft square / noise), crest sharpness, cross ripples,
// organic warp, amplitude pulse, along-axis stretch, slope shading + crest highlight, tail tint,
// tail alpha, animated hue shift, chromatic split, noise dissolve with glowing rim, soft edges.
Shader "UI/WaveTail"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        [Header(Tail Axis)]
        [Toggle] _UseLocalUV ("Use Local UV (needs UIWaveTailUV)", Float) = 1
        _Aspect ("Aspect W/H (only without helper)", Float) = 1
        _Angle ("Angle: 0 L>R, 90 B>T, 180 R>L, -90 T>B", Range(-180, 180)) = 0
        _TailStart ("Tail Start", Range(0, 1)) = 0.2
        _TailEnd ("Tail End", Range(0, 1)) = 1
        _TailPower ("Tail Falloff Power", Range(0.2, 6)) = 1.5

        [Header(Wave Global)]
        [Enum(Sine,0,Triangle,1,SoftSquare,2,Noise,3)] _WaveType ("Wave Type", Float) = 0
        _Sharp ("Crest Sharpness", Range(0.3, 3)) = 1
        _SpeedMul ("Speed Multiplier", Float) = 1
        _TimeOffset ("Time Offset", Float) = 0
        [Toggle] _UseUnscaledTime ("Use Unscaled Time (needs UIWaveTailUV)", Float) = 0
        _Pulse ("Amplitude Pulse", Range(0, 1)) = 0
        _PulseSpeed ("Pulse Speed", Float) = 1
        _Warp ("Organic Warp", Range(0, 2)) = 0
        _CrossFreq ("Cross Ripple Frequency", Float) = 0.6
        _CrossSpeed ("Cross Ripple Speed", Float) = 0.4
        _AlongAmp ("Along-axis Stretch", Range(0, 1)) = 0

        [Header(Layer 1 Main)]
        _Amp1 ("Amplitude", Range(0, 0.5)) = 0.05
        _Freq1 ("Frequency", Float) = 1.5
        _Speed1 ("Speed", Float) = 0.8
        _Phase1 ("Phase", Range(0, 1)) = 0

        [Header(Layer 2 Secondary)]
        _Amp2 ("Amplitude", Range(0, 0.5)) = 0.025
        _Freq2 ("Frequency", Float) = 3.2
        _Speed2 ("Speed", Float) = -1.3
        _Phase2 ("Phase", Range(0, 1)) = 0.3

        [Header(Layer 3 Flutter)]
        _Amp3 ("Amplitude", Range(0, 0.5)) = 0.01
        _Freq3 ("Frequency", Float) = 7
        _Speed3 ("Speed", Float) = 2.1
        _Phase3 ("Phase", Range(0, 1)) = 0.6

        [Header(Surface Look)]
        _Shading ("Slope Shading", Range(-2, 2)) = 0.6
        [HDR] _HighlightColor ("Crest Highlight Color", Color) = (1,1,1,1)
        _Highlight ("Crest Highlight", Range(0, 2)) = 0.25
        _HighlightPower ("Crest Highlight Power", Range(1, 16)) = 4

        [Header(Tail Color)]
        [HDR] _TailColor ("Tail Tint (A = amount)", Color) = (1,1,1,0)
        _TailAlpha ("Tail Alpha", Range(0, 1)) = 1
        _HueRange ("Hue Shift Along Tail (deg)", Range(-360, 360)) = 0
        _HueSpeed ("Hue Shift Speed (deg/s)", Range(-360, 360)) = 0
        [Toggle(_CHROMA)] _ChromaToggle ("Chromatic Split", Float) = 0
        _Chroma ("Chroma Amount", Range(0, 0.05)) = 0.01

        [Header(Dissolve)]
        _Dissolve ("Dissolve Amount", Range(0, 1)) = 0
        _DissolveSoft ("Dissolve Softness", Range(0.001, 0.5)) = 0.08
        _NoiseScale ("Noise Scale", Float) = 6
        _NoiseSpeed ("Noise Scroll Speed", Float) = 0.6
        [HDR] _EdgeColor ("Rim Glow Color", Color) = (1,0.55,0.1,1)
        _EdgeWidth ("Rim Width", Range(0.001, 0.4)) = 0.1

        [Header(Edges)]
        _EdgeSoftness ("Silhouette Softness", Range(0, 0.05)) = 0.003

        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [HideInInspector] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #pragma shader_feature_local _CHROMA

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1 : TEXCOORD1; // local uv (xy) + aspect (z), written by UIWaveTailUV
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                float3 uv1 : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            float _UseLocalUV, _Aspect, _Angle, _TailStart, _TailEnd, _TailPower;
            float _WaveType, _Sharp, _SpeedMul, _TimeOffset, _UseUnscaledTime;
            float _Pulse, _PulseSpeed, _Warp, _CrossFreq, _CrossSpeed, _AlongAmp;
            float _Amp1, _Freq1, _Speed1, _Phase1;
            float _Amp2, _Freq2, _Speed2, _Phase2;
            float _Amp3, _Freq3, _Speed3, _Phase3;
            float _Shading, _Highlight, _HighlightPower;
            fixed4 _HighlightColor;
            fixed4 _TailColor;
            float _TailAlpha, _HueRange, _HueSpeed, _Chroma;
            float _Dissolve, _DissolveSoft, _NoiseScale, _NoiseSpeed, _EdgeWidth;
            fixed4 _EdgeColor;
            float _EdgeSoftness;
            float _WaveUnscaledTime; // global, set by UIWaveTailUV

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = v.texcoord;
                OUT.uv1 = v.texcoord1.xyz;
                OUT.color = v.color * _Color;
                return OUT;
            }

            // ───────────── helpers ─────────────

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float vnoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1.0, 0.0));
                float c = hash21(i + float2(0.0, 1.0));
                float d = hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // x is the phase in cycles, result in [-1, 1]
            float Shape(float x)
            {
                float y;
                int ty = (int)(_WaveType + 0.5);
                if (ty == 1)
                    y = 4.0 * abs(frac(x) - 0.5) - 1.0;                  // triangle
                else if (ty == 2)
                {
                    float s = sin(6.2831853 * x);
                    y = s / (abs(s) + 0.15) * 1.15;                      // soft square
                }
                else if (ty == 3)
                    y = vnoise(float2(x, 7.13)) * 2.0 - 1.0;             // smooth noise
                else
                    y = sin(6.2831853 * x);                              // sine

                return sign(y) * pow(abs(y), _Sharp);
            }

            // returns (perpendicular displacement, along-axis displacement), before tail envelope
            float2 Waves(float a, float b, float t)
            {
                float crossPh = b * _CrossFreq + t * _CrossSpeed;
                float warp = 0.0;
                if (_Warp > 0.0001)
                    warp = (vnoise(float2(a * 2.0 + t * 0.3, b * 2.0 + 3.1)) * 2.0 - 1.0) * _Warp * 0.3;

                float x1 = a * _Freq1 - t * _Speed1 + _Phase1 + crossPh + warp;
                float x2 = a * _Freq2 - t * _Speed2 + _Phase2 + crossPh + warp;
                float x3 = a * _Freq3 - t * _Speed3 + _Phase3 + crossPh + warp;

                float perpD = _Amp1 * Shape(x1) + _Amp2 * Shape(x2) + _Amp3 * Shape(x3);
                float alongD = _AlongAmp * _Amp1 * 2.0 * Shape(x1 + 0.25);
                return float2(perpD, alongD);
            }

            float3 HueShift(float3 col, float deg)
            {
                float a = radians(deg);
                float cs = cos(a);
                float sn = sin(a);
                const float3 k = float3(0.57735, 0.57735, 0.57735);
                return col * cs + cross(k, col) * sn + k * dot(k, col) * (1.0 - cs);
            }

            // ───────────── fragment ─────────────

            fixed4 frag(v2f IN) : SV_Target
            {
                // local 0..1 coordinates over the drawn image (independent of atlas packing)
                float useLocal = step(0.5, _UseLocalUV);
                float2 local = lerp(IN.texcoord, IN.uv1.xy, useLocal);
                float aspect = max(lerp(_Aspect, IN.uv1.z, useLocal), 1e-4);
                float2 asp = float2(aspect, 1.0);
                float2 P = (local - 0.5) * asp; // aspect-corrected, height = 1

                // axis: a = 0 at head side, 1 at tail side; b = across
                float ang = radians(_Angle);
                float2 dir = float2(cos(ang), sin(ang));
                float2 perp = float2(-dir.y, dir.x);
                float halfLen = max(0.5 * (abs(dir.x) * aspect + abs(dir.y)), 1e-4);
                float a = dot(P, dir) / (2.0 * halfLen) + 0.5;
                float b = dot(P, perp);

                // tail weight: 0 = still head, 1 = full wave tail
                float w = pow(smoothstep(_TailStart, max(_TailEnd, _TailStart + 1e-4), a), _TailPower);

                float baseTime = (_UseUnscaledTime > 0.5) ? _WaveUnscaledTime : _Time.y;
                float t = (baseTime + _TimeOffset) * _SpeedMul;
                float env = w * (1.0 + _Pulse * sin(t * _PulseSpeed));

                float2 W = Waves(a, b, t);
                float2 D = (perp * W.x + dir * W.y) * env;  // displacement, aspect space
                float2 dls = -D / asp;                      // local-space sample offset
                float2 sl = local + dls;                    // displaced sample position

                // derivative mapping local -> texture uv (works for atlases and 9-slice)
                float2 ddxUV = ddx(IN.texcoord);
                float2 ddyUV = ddy(IN.texcoord);
                float2 ddxL = ddx(local);
                float2 ddyL = ddy(local);
                float2 ux = float2(1.0, 0.0);
                float2 uy = float2(0.0, 1.0);
                if (useLocal > 0.5)
                {
                    float det = ddxL.x * ddyL.y - ddxL.y * ddyL.x;
                    float id = abs(det) > 1e-14 ? 1.0 / det : 0.0;
                    ux = (ddxUV * ddyL.y - ddyUV * ddxL.y) * id;
                    uy = (-ddxUV * ddyL.x + ddyUV * ddxL.x) * id;
                }

                float2 uv = IN.texcoord + ux * dls.x + uy * dls.y;
                fixed4 tex = tex2D(_MainTex, uv);

                #ifdef _CHROMA
                float2 cd = (perp / asp) * (_Chroma * w);
                float2 cduv = ux * cd.x + uy * cd.y;
                tex.r = tex2D(_MainTex, uv + cduv).r;
                tex.b = tex2D(_MainTex, uv - cduv).b;
                #endif

                fixed4 c = (tex + _TextureSampleAdd) * IN.color;

                // slope shading (fabric / ribbon look)
                if (abs(_Shading) > 0.0001)
                {
                    float2 W2 = Waves(a + 0.01, b, t);
                    float slope = (W2.x - W.x) / 0.01;
                    c.rgb *= clamp(1.0 + slope * _Shading * env * 4.0, 0.15, 2.5);
                }

                // crest highlight
                if (_Highlight > 0.0001)
                {
                    float hn = saturate(W.x / max(_Amp1 + _Amp2 + _Amp3, 1e-4));
                    c.rgb += _HighlightColor.rgb * pow(hn, _HighlightPower) * _Highlight * env * c.a;
                }

                // hue shift (animated) along the tail
                if (abs(_HueRange) > 0.01 || abs(_HueSpeed) > 0.01)
                    c.rgb = HueShift(c.rgb, (_HueRange + baseTime * _HueSpeed) * w);

                // tail tint and alpha
                c.rgb = lerp(c.rgb, _TailColor.rgb, w * _TailColor.a);
                c.a *= lerp(1.0, _TailAlpha, w);

                // noise dissolve with glowing rim
                if (_Dissolve > 0.0001)
                {
                    float2 np = float2(a * _NoiseScale - t * _NoiseSpeed, b * _NoiseScale + 11.7);
                    float n = 0.65 * vnoise(np) + 0.35 * vnoise(np * 2.7 + 5.3);
                    n = saturate((n - 0.2) / 0.6);
                    float m = n - w * _Dissolve;
                    float keep = smoothstep(0.0, max(_DissolveSoft, 1e-4), m);
                    float ew = max(_EdgeWidth, 1e-4);
                    float rim = smoothstep(0.0, ew * 0.5, m) * (1.0 - smoothstep(ew * 0.5, ew, m));
                    c.rgb += _EdgeColor.rgb * _EdgeColor.a * rim;
                    c.a *= keep;
                }

                // silhouette: anything sampled from outside the image is transparent
                float2 edge = min(sl, 1.0 - sl);
                c.a *= smoothstep(0.0, max(_EdgeSoftness, 1e-5), min(edge.x, edge.y));

                #ifdef UNITY_UI_CLIP_RECT
                c.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(c.a - 0.001);
                #endif

                return c;
            }
            ENDCG
        }
    }
}
