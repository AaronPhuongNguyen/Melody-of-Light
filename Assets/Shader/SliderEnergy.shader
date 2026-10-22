Shader "UI/EnergyCubes"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _Segments ("Segments (Max Energy)", Float) = 3
        _Value ("Value", Float) = 3
        _Aspect ("Rect Width / Height", Float) = 3
        _AlignX ("Horizontal Align (0 left, 0.5 center, 1 right)", Range(0, 1)) = 0.5

        _Gap ("Gap (in cube sizes)", Range(0, 0.5)) = 0.12
        _Radius ("Corner Radius", Range(0, 0.5)) = 0.15

        _FullColor ("Full Color", Color) = (0.2, 0.9, 1, 1)
        _LowColor ("Low Color", Color) = (1, 0.1, 0.1, 1)
        _EmptyColor ("Empty Color", Color) = (0.1, 0.1, 0.12, 0.8)
        _RedStart ("Starts Turning Red Below (0-1)", Range(0.05, 1)) = 0.6

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True"
            "RenderType"="Transparent" "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
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
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            float4 _ClipRect;

            float _Segments, _Value, _Aspect, _AlignX, _Gap, _Radius, _RedStart;
            fixed4 _FullColor, _LowColor, _EmptyColor;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = v.texcoord;
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float seg = max(1.0, floor(_Segments + 0.5));

                // ---- fit square cubes inside the rect (never larger than width or height) ----
                float side = min(_Aspect / seg, 1.0);                         // cube size in rect-height units
                float2 barSize = float2(side * seg / _Aspect, side);          // bar size in 0..1 uv
                float2 barMin = float2((1.0 - barSize.x) * _AlignX, (1.0 - barSize.y) * 0.5);
                float2 buv = (IN.texcoord - barMin) / barSize;                // 0..1 across the bar
                float inside = step(0.0, buv.x) * step(buv.x, 1.0) * step(0.0, buv.y) * step(buv.y, 1.0);

                float x = buv.x * seg;
                float idx = clamp(floor(x), 0.0, seg - 1.0);
                float2 cell = float2(x - idx, buv.y);                         // 0..1 inside this cube

                // ---- rounded cube shape (cubes are exact squares) ----
                float2 half2 = float2(0.5, 0.5);
                float2 p = cell - 0.5;

                float r = clamp(_Radius, 0.0, 0.5 - _Gap * 0.5);
                float2 b = half2 - _Gap * 0.5 - r;
                float2 q = abs(p) - b;
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
                float shape = saturate(0.5 - d / (fwidth(d) + 1e-5)) * inside;

                // ---- fill of this cube (partial values fill the cube left to right) ----
                float fill = saturate(_Value - idx);
                float fw = fwidth(cell.x) + 1e-5;
                float fm = saturate((fill - cell.x) / fw + 0.5);
                if (fill >= 0.999) fm = 1.0;
                if (fill <= 0.001) fm = 0.0;

                // ---- color: full -> red as the energy ratio drops ----
                float ratio = saturate(_Value / seg);
                float t = smoothstep(0.0, max(_RedStart, 0.001), ratio);
                fixed4 filled = lerp(_LowColor, _FullColor, t);

                fixed4 col = lerp(_EmptyColor, filled, fm);
                col.a *= shape;
                col *= IN.color;

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(col.a - 0.001);
                #endif
                return col;
            }
            ENDCG
        }
    }
}