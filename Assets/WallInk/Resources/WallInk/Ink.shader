// Unlit vertex-coloured ink for strokes, cursors and flat UI quads.
// UV.x is the normalised distance across the ribbon (-1..1, or 0..1 from a disc centre);
// the fragment fades the last pixel of the edge for anti-aliasing at constant physical width.
// Drawn in painter's order (no depth write) for board ink so coplanar strokes never z-fight;
// air strokes use a material with _ZWrite = 1.
Shader "WallInk/Ink"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(Off,0,On,1)] _ZWrite ("Depth Write", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Ink"
            Tags { "LightMode"="UniversalForward" }

            // Colour: standard alpha blend. Alpha: accumulate coverage so the passthrough
            // underlay is hidden exactly where ink is drawn.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color * _Color;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float d = abs(input.uv.x);
                // One-pixel-wide falloff at the ribbon edge (d = 1).
                float aa = saturate((1.0 - d) / max(fwidth(d), 1e-5));
                half4 c = input.color;
                c.a *= aa;
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
