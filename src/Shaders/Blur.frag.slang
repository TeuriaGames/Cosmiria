#include "common/blur.slang"

[vk_binding(0, 2)]
Sampler2D Source : register(s0, space2);

[vk_binding(0, 3)]
cbuffer BlurBlock : register(b0, space3)
{
    float4 SourceRect : packoffset(c0); // region of the source in uv: origin, size
    float2 Step : packoffset(c1.x);     // uv distance between taps along the blur direction
    float Sigma : packoffset(c1.z);     // standard deviation in taps
    float Taps : packoffset(c1.w);      // taps on each side of the center
};

// One direction of a separable Gaussian, on premultiplied pixels.
[shader("fragment")]
float4 main(BlurVertex v) : SV_Target0
{
    float2 uv = SourceRect.xy + v.UV * SourceRect.zw;
    float k = -0.5 / (Sigma * Sigma);
    float4 sum = Source.SampleLevel(uv, 0.0);
    float total = 1.0;
    int n = (int)Taps;
    for (int i = 1; i <= n; i++)
    {
        float w = exp(k * float(i * i));
        sum += (Source.SampleLevel(uv + Step * i, 0.0) + Source.SampleLevel(uv - Step * i, 0.0)) * w;
        total += 2.0 * w;
    }
    return sum / total;
}
