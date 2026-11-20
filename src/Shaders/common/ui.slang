// Shared between UI.vert and UI.frag. Must match Cosmiria.Render.UIQuad (160 bytes).
struct UIQuad
{
    float4 Rect;        // x, y, w, h (pixels)
    float4 UV;          // u0, v0, u1, v1
    float4 Color;       // straight alpha
    float4 BorderColor; // straight alpha
    float4 Radius;      // top-left, top-right, bottom-right, bottom-left
    float BorderWidth;
    uint Mode;          // 0 = rounded rect, 1 = glyph, 2 = image (icon / raster image), 3 = box shadow
    float2 Translate;   // affine transform applied to the quad's corners: p' = M * p + Translate
    float4 Matrix;      // (m11, m12, m21, m22), row-vector convention like System.Numerics.Matrix3x2
    float4 Color2;      // gradient end color (straight alpha)
    float4 Color3;      // gradient middle color ("via"), used when Gradient.w > 0.5
    float4 Gradient;    // (dir.x, dir.y, kind: 0 none / 1 linear / 2 radial, has via)
};

struct UIVertex
{
    float4 Color : COLOR0;
    float4 BorderColor : COLOR1;
    float4 Radius : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float2 Local : TEXCOORD2;
    float2 HalfSize : TEXCOORD3;
    nointerpolation float BorderWidth : TEXCOORD4;
    nointerpolation uint Mode : TEXCOORD5;
    nointerpolation float4 Color2 : TEXCOORD6;
    nointerpolation float4 Color3 : TEXCOORD7;
    nointerpolation float4 Gradient : TEXCOORD8;
    float4 Position : SV_Position;
};
