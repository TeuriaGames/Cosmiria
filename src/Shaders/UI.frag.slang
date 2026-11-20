#include "common/ui.slang"

[vk_binding(0, 2)]
Sampler2D Texture : register(s0, space2); // glyph atlas (shared by every font and size)

[vk_binding(1, 2)]
Sampler2D Icons : register(s1, space2);   // icon atlas, or the raster image of the draw call

// Rounded box SDF with per-corner radius. r = (tl, tr, br, bl), p in y-down space.
float sdRoundBox(float2 p, float2 b, float4 r)
{
    float rad = p.x < 0.0 ? (p.y < 0.0 ? r.x : r.w) : (p.y < 0.0 ? r.y : r.z);
    float2 q = abs(p) - b + rad;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rad;
}

// erf, Abramowitz-Stegun 7.1.26 (max error 1.5e-7): the Gaussian's integral, for soft shadow edges.
float erfApprox(float x)
{
    float a = abs(x);
    float t = 1.0 / (1.0 + 0.3275911 * a);
    float y = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-a * a);
    return x < 0.0 ? -y : y;
}

[shader("fragment")]
float4 main(UIVertex v) : SV_Target0
{
    // Size of one screen pixel in the quad's local units: anti-aliasing stays one pixel wide
    // whatever the display scale or the element's transform.
    float px = max(0.5 * (length(ddx(v.Local)) + length(ddy(v.Local))), 1e-4);

    if (v.Mode == 2)
    {
        // Image: premultiplied texture (mipmapped for raster images) tinted by a straight-alpha color.
        float4 tint = float4(v.Color.rgb * v.Color.a, v.Color.a);
        float4 c = Icons.Sample(v.TexCoord) * tint;
        if (any(v.Radius > 0.0))
            c *= saturate(0.5 - sdRoundBox(v.Local, v.HalfSize, v.Radius) / px);
        if (c.a <= 0.0)
            discard;
        return c;
    }

    if (v.Mode == 3)
    {
        // Box shadow (CSS box-shadow, outer). The quad is the shape grown by 3 sigma per side,
        // centered on the shape; Gradient = (offset, spread), Radius = the box's own radii.
        float sigma = v.BorderWidth;
        float spread = v.Gradient.z;
        float2 shapeHalf = max(v.HalfSize - 3.0 * sigma, float2(0.0, 0.0));
        float limit = min(shapeHalf.x, shapeHalf.y);
        float4 shapeR = min(max(v.Radius + spread * step(0.001, v.Radius), float4(0.0, 0.0, 0.0, 0.0)), float4(limit, limit, limit, limit));
        float ds = sdRoundBox(v.Local, shapeHalf, shapeR);
        float a = sigma > 0.01 ? 0.5 - 0.5 * erfApprox(ds / (sigma * 1.41421356)) : saturate(0.5 - ds / px);
        // Only outside the box itself, which sits at -offset with the spread taken back off.
        float2 boxHalf = max(shapeHalf - spread, float2(0.0, 0.0));
        float boxLimit = min(boxHalf.x, boxHalf.y);
        float db = sdRoundBox(v.Local + v.Gradient.xy, boxHalf, min(v.Radius, float4(boxLimit, boxLimit, boxLimit, boxLimit)));
        a *= saturate(0.5 + db / px) * v.Color.a;
        if (a <= 0.0)
            discard;
        return float4(v.Color.rgb * a, a);
    }

    if (v.Mode == 1)
    {
        // Glyph: use atlas coverage only, output premultiplied.
        float a = Texture.Sample(v.TexCoord).a * v.Color.a;
        if (a <= 0.0)
            discard;
        return float4(v.Color.rgb * a, a);
    }

    float d = sdRoundBox(v.Local, v.HalfSize, v.Radius);
    float coverage = saturate(0.5 - d / px);

    float4 fill = float4(v.Color.rgb * v.Color.a, v.Color.a);
    if (v.Gradient.z > 0.5)
    {
        // CSS-like gradients, blended in premultiplied space (fading to transparent doesn't darken).
        float t;
        if (v.Gradient.z < 1.5)
        {
            // Linear: the gradient line spans the box along the direction (like CSS's angle sizing).
            float halfLength = abs(v.Gradient.x) * v.HalfSize.x + abs(v.Gradient.y) * v.HalfSize.y;
            t = dot(v.Local, v.Gradient.xy) / max(halfLength, 1e-4) * 0.5 + 0.5;
        }
        else
        {
            // Radial: an ellipse reaching the corners (CSS's default "farthest-corner").
            t = length(v.Local / max(v.HalfSize, float2(1e-4, 1e-4))) * 0.70710678;
        }
        t = saturate(t);
        float4 a = float4(v.Color.rgb * v.Color.a, v.Color.a);
        float4 b = float4(v.Color2.rgb * v.Color2.a, v.Color2.a);
        if (v.Gradient.w > 0.5)
        {
            float4 m = float4(v.Color3.rgb * v.Color3.a, v.Color3.a);
            fill = t < 0.5 ? lerp(a, m, t * 2.0) : lerp(m, b, t * 2.0 - 1.0);
        }
        else fill = lerp(a, b, t);
    }
    if (v.BorderWidth > 0.0)
    {
        float4 border = float4(v.BorderColor.rgb * v.BorderColor.a, v.BorderColor.a);
        float inner = saturate(0.5 - (d + v.BorderWidth) / px);
        fill = lerp(border, fill, inner);
    }

    fill *= coverage;
    if (fill.a <= 0.0)
        discard;
    return fill;
}
