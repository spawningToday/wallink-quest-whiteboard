// Translucent whiteboard backing with an optional procedural grid or ruled lines.
// The quad is a unit square scaled to the board size; _BoardSize (metres) converts UVs to
// physical units so the grid spacing is in metres regardless of board size.
Shader "WallInk/Board"
{
    Properties
    {
        _Color ("Backing Colour", Color) = (1,1,1,0.35)
        _GridColor ("Grid Colour", Color) = (0.25,0.45,0.75,0.55)
        _BoardSize ("Board Size (m)", Vector) = (1.2,0.8,0,0)
        _GridSpacing ("Grid Spacing (m)", Float) = 0.025
        _GridLineWidth ("Grid Line Width (m)", Float) = 0.0006
        _GridMode ("Grid Mode (0 off, 1 grid, 2 ruled)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Board"
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
                half4 _GridColor;
                float4 _BoardSize;
                float _GridSpacing;
                float _GridLineWidth;
                float _GridMode;
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
                float2 posM : TEXCOORD0; // position on the board in metres, origin at the centre
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
                output.posM = (input.uv - 0.5) * _BoardSize.xy;
                return output;
            }

            // Coverage of a line of the given width centred on every multiple of spacing.
            float lineCoverage(float coord, float spacing, float width)
            {
                float d = abs(frac(coord / spacing + 0.5) - 0.5) * spacing; // distance to nearest line
                float px = max(fwidth(coord), 1e-6);
                return saturate((width * 0.5 - d) / px + 0.5);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 c = _Color;
                // Branch-free: derivatives (fwidth) must not sit inside dynamic flow control.
                float gy = lineCoverage(input.posM.y, _GridSpacing, _GridLineWidth);
                float gx = lineCoverage(input.posM.x, _GridSpacing, _GridLineWidth);
                float anyGrid = step(0.5, _GridMode);          // 1 for grid or ruled
                float fullGrid = 1.0 - step(1.5, _GridMode);   // 1 for grid only
                float g = anyGrid * max(gy, gx * fullGrid);
                g *= _GridColor.a;
                half4 o;
                o.rgb = lerp(c.rgb, _GridColor.rgb, g);
                o.a = lerp(c.a, 1.0, g);
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
