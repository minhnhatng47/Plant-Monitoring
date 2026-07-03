Shader "Custom/PlantWorldYReveal"
{
    Properties
    {
        _MainTex ("Base Map", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BottomY ("Bottom Y", Float) = 0
        _CutoffY ("Cutoff Y", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        float _BottomY;
        float _CutoffY;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Ẩn phần nằm dưới mặt đất
            clip(IN.worldPos.y - _BottomY);

            // Ẩn phần cao hơn mức cây đang mọc
            clip(_CutoffY - IN.worldPos.y);

            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;

            o.Albedo = c.rgb;
            o.Metallic = 0;
            o.Smoothness = 0.25;
            o.Alpha = c.a;
        }
        ENDCG
    }

    FallBack "Diffuse"
}