Shader "Hidden/RayTracing/ScenePreview"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        _PreviewAmbientColor ("Ambient Color", Color) = (0.4, 0.4, 0.4, 1)
        _PreviewKeyLightDirection ("Key Light Direction", Vector) = (0.35, 0.8, 0.45, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float4 _PreviewAmbientColor;
            float4 _PreviewKeyLightDirection;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                // Keep headroom for white ray materials so directional shading stays visible.
                float keyLight = abs(dot(normalize(input.worldNormal), normalize(_PreviewKeyLightDirection.xyz)));
                float3 lighting = 0.08 + _PreviewAmbientColor.rgb * 0.22 + keyLight * 0.55;
                return fixed4(tex2D(_MainTex, input.uv).rgb * _Color.rgb * lighting, _Color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
