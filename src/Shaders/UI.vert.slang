#include "common/ui.slang"

[vk_binding(0, 0)]
StructuredBuffer<UIQuad> Quads : register(t0, space0);

[vk_binding(0, 1)]
cbuffer UniformBlock : register(b0, space1)
{
    float4x4 ViewProjectionMatrix : packoffset(c0);
    // SV_InstanceID excludes firstInstance on both backends, so the draw call's
    // starting quad is passed explicitly.
    uint QuadOffset : packoffset(c4.x);
};

static const float2 corners[4] = {
    { 0, 0 },
    { 1, 0 },
    { 0, 1 },
    { 1, 1 },
};

[shader("vertex")]
UIVertex main(uint id : SV_VertexID, uint instanceID : SV_InstanceID)
{
    UIQuad q = Quads[QuadOffset + instanceID];
    float2 corner = corners[id % 4];

    float2 size = q.Rect.zw;
    float2 p = q.Rect.xy + corner * size;
    // Transform (identity for untransformed elements). Local coordinates below stay in the
    // quad's own space, so rounded-rect SDFs remain exact under rotation and scale.
    float2 pos = float2(p.x * q.Matrix.x + p.y * q.Matrix.z, p.x * q.Matrix.y + p.y * q.Matrix.w) + q.Translate;

    UIVertex o;
    o.Position = mul(ViewProjectionMatrix, float4(pos, 0.0, 1.0));
    o.TexCoord = lerp(q.UV.xy, q.UV.zw, corner);
    o.Color = q.Color;
    o.BorderColor = q.BorderColor;
    o.Radius = q.Radius;
    o.HalfSize = size * 0.5;
    o.Local = (corner - 0.5) * size; // centered local coords, y down
    o.BorderWidth = q.BorderWidth;
    o.Mode = q.Mode;
    o.Color2 = q.Color2;
    o.Color3 = q.Color3;
    o.Gradient = q.Gradient;
    return o;
}
