Shader "Nature/Terrain/RayTracingPreview"
{
    Properties
    {
        [HideInInspector] _Control ("Control", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0", 2D) = "white" {}
        [HideInInspector] _Splat1 ("Layer 1", 2D) = "white" {}
        [HideInInspector] _Splat2 ("Layer 2", 2D) = "white" {}
        [HideInInspector] _Splat3 ("Layer 3", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "SplatCount"="4" "Queue"="Geometry-100" "RenderType"="Opaque" "TerrainCompatible"="True" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2D(_Control);
            sampler2D _Splat0;
            sampler2D _Splat1;
            sampler2D _Splat2;
            sampler2D _Splat3;
            float4 _Control_ST;
            float4 _Splat0_ST;
            float4 _Splat1_ST;
            float4 _Splat2_ST;
            float4 _Splat3_ST;

            struct AppData
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 position : SV_POSITION;
                float2 controlUv : TEXCOORD0;
                float2 layerUv0 : TEXCOORD1;
                float2 layerUv1 : TEXCOORD2;
                float2 layerUv2 : TEXCOORD3;
                float2 layerUv3 : TEXCOORD4;
            };

            Interpolators vert(AppData input)
            {
                Interpolators output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.controlUv = TRANSFORM_TEX(input.texcoord, _Control);
                output.layerUv0 = TRANSFORM_TEX(input.texcoord, _Splat0);
                output.layerUv1 = TRANSFORM_TEX(input.texcoord, _Splat1);
                output.layerUv2 = TRANSFORM_TEX(input.texcoord, _Splat2);
                output.layerUv3 = TRANSFORM_TEX(input.texcoord, _Splat3);
                return output;
            }

            fixed4 frag(Interpolators input) : SV_Target
            {
                fixed4 weights = UNITY_SAMPLE_TEX2D(_Control, input.controlUv);
                weights /= max(0.0001, dot(weights, fixed4(1, 1, 1, 1)));
                fixed3 albedo = tex2D(_Splat0, input.layerUv0).rgb * weights.r
                    + tex2D(_Splat1, input.layerUv1).rgb * weights.g
                    + tex2D(_Splat2, input.layerUv2).rgb * weights.b
                    + tex2D(_Splat3, input.layerUv3).rgb * weights.a;
                return fixed4(albedo, 1);
            }
            ENDCG
        }
    }
}
