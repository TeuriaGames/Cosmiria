#include "common/blur.slang"

[shader("vertex")]
BlurVertex main(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    BlurVertex o;
    o.UV = uv;
    o.Position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return o;
}
