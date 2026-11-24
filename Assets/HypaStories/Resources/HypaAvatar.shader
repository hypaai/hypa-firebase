// The profile character's skin, eyes, hair and clothes (built-in pipeline, single-pass stereo via the surface shader).
// Wrap lighting with a warm scatter tint softens skin the way light passes through it; hair and lashes are alpha-tested cards.
// One shader for every part keeps the variant count small: _Cutoff 0 is simply opaque.
Shader "HypaStories/Avatar"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _TintAmount ("Tint amount (0 = texture colours)", Range(0, 1)) = 1
        _BumpMap ("Normal map", 2D) = "bump" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0
        _Wrap ("Light wrap", Range(0, 1)) = 0.4
        _Scatter ("Scatter colour", Color) = (0.55, 0.18, 0.1, 1)
        _Spec ("Specular", Range(0, 1)) = 0.2
        _Gloss ("Gloss", Range(0.03, 1)) = 0.35
        _Rim ("Rim light", Color) = (1, 0.72, 0.45, 0.35)
        _ClipPlane ("Clip plane (world xyz normal, w distance)", Vector) = (0, 0, 0, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull [_Cull]
        CGPROGRAM
        #pragma surface surf Avatar fullforwardshadows alphatest:_Cutoff addshadow nolightmap nodynlightmap nodirlightmap nometa
        #pragma target 3.0

        sampler2D _MainTex, _BumpMap;
        fixed4 _Color, _Scatter, _Rim;
        float4 _ClipPlane;
        half _TintAmount, _Wrap, _Spec, _Gloss;

        struct Input { float2 uv_MainTex; float3 viewDir; float3 worldPos; };

        half4 LightingAvatar(SurfaceOutput s, half3 viewDir, UnityGI gi)
        {
            half3 l = gi.light.dir;
            half ndl = dot(s.Normal, l);
            half wrap = saturate((ndl + _Wrap) / (1 + _Wrap));
            // the band just past the terminator picks up the scatter colour (reddish for skin, neutral elsewhere)
            half3 scatter = _Scatter.rgb * saturate(wrap - saturate(ndl)) * _Scatter.a;
            half3 h = normalize(l + viewDir);
            half spec = pow(saturate(dot(s.Normal, h)), _Gloss * 128) * s.Specular * saturate(ndl * 4);
            half4 c;
            c.rgb = (s.Albedo * (wrap + scatter) + spec) * gi.light.color + s.Albedo * gi.indirect.diffuse;
            c.a = s.Alpha;
            return c;
        }

        void LightingAvatar_GI(SurfaceOutput s, UnityGIInput data, inout UnityGI gi) { gi = UnityGlobalIllumination(data, 1.0, s.Normal); }

        void surf(Input IN, inout SurfaceOutput o)
        {
            clip(dot(IN.worldPos, _ClipPlane.xyz) + _ClipPlane.w);
            fixed4 t = tex2D(_MainTex, IN.uv_MainTex);
            o.Albedo = lerp(t.rgb, t.rgb * _Color.rgb, _TintAmount);
            o.Alpha = t.a * _Color.a;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            o.Specular = _Spec;
            o.Gloss = _Gloss;
            half rim = pow(1 - saturate(dot(normalize(IN.viewDir), o.Normal)), 3);
            o.Emission = _Rim.rgb * rim * _Rim.a * o.Albedo * 2;
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/Diffuse"
}
