// "Glass" UI surface: a rounded rectangle with a translucent tinted body, a soft top sheen
// and a bright directional rim, drawn analytically from a signed distance field so corners
// stay crisp at any size.
//
// It imitates the look of frosted glass. It cannot actually blur what is behind it: on Quest
// the passthrough image is composited by the system underneath the app and is not available
// to app shaders.
//
// Per-renderer properties (MaterialPropertyBlock):
//   _Color : body tint and opacity
//   _Size  : x, y = quad size in metres; z = rim width in metres; w = corner radius in metres
Shader "WallInk/Glass"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,0.2)
        _Size ("Size xy, rim z, radius w (m)", Vector) = (0.1,0.03,0.001,0.015)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Glass"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
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
                float4 _Size;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
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
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 halfSize = _Size.xy * 0.5;
                float2 p = (input.uv - 0.5) * _Size.xy;            // metres from the centre
                float radius = min(_Size.w, min(halfSize.x, halfSize.y));

                // Signed distance to a rounded box: negative inside, zero on the edge.
                float2 q = abs(p) - halfSize + radius;
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;

                float px = max(fwidth(d), 1e-6);
                float shape = saturate(-d / px);                     // anti-aliased silhouette

                // Rim: a thin bright band just inside the edge, strongest toward the upper left
                // (where a light would catch the glass) with a fainter echo at the lower right.
                float rim = 1.0 - smoothstep(0.0, max(_Size.z, px), -d);
                float2 dir = normalize(p / max(halfSize, 1e-5) + 1e-5);
                float lit = saturate(dot(dir, normalize(float2(-0.55, 0.85))));
                float echo = saturate(dot(dir, normalize(float2(0.55, -0.85))));
                float rimLight = rim * (0.25 + 0.75 * lit + 0.30 * echo);

                // Sheen: soft brightening over the upper part of the body.
                float sheen = smoothstep(0.35, 1.0, input.uv.y) * 0.10;

                half3 rgb = _Color.rgb + sheen + rimLight * 0.9;
                half a = saturate(_Color.a + sheen * 0.6 + rimLight * 0.65) * shape;
                return half4(saturate(rgb), a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
