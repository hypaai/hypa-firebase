// A full-colour picture with alpha for the Stories UI (the profile's option tiles and its live portrait). UIIcon keeps only alpha.
// _Round rounds the corners (0 square, 0.5 a circle), with a soft edge.
Shader "HypaStories/Picture"
{
    Properties
    {
        _MainTex ("Picture", 2D) = "white" {}
        _Opacity ("Opacity", Float) = 1
        _Round ("Corner radius (uv)", Range(0, 0.5)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Opacity, _Round;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                float2 q = max(abs(i.uv - 0.5) - (0.5 - _Round), 0);
                float edge = _Round > 0 ? 1 - smoothstep(_Round - fwidth(i.uv.x) * 1.5, _Round, length(q)) : 1;
                return fixed4(t.rgb, t.a * _Opacity * edge);
            }
            ENDCG
        }
    }
}
